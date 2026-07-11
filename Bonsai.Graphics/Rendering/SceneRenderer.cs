using System;
using System.Collections.Generic;
using System.Numerics;
using Bonsai.Graphics.Scene;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace Bonsai.Graphics.Rendering
{
    /// <summary>
    /// Traverses the scene graph and records draw commands: the only code in
    /// the system that touches the command list for scene content (ADR 0002).
    /// One root signature; a PSO per MaterialKind; a shader-visible SRV heap.
    /// </summary>
    public sealed class SceneRenderer : IDisposable
    {
        private const int MaxTextures = 512;
        private const int ConstantCount = 60; // 15 float4 rows

        #region HLSL
        private const string ShaderSource = @"
struct SceneConstants
{
    row_major float4x4 WorldViewProj;   // rows 0-3
    row_major float4x4 World;           // rows 4-7
    float4 LightDirection;              // row 8: xyz = direction light travels
    float4 LightColor;                  // row 9: rgb, w = ambient intensity
    float4 DiffuseColor;                // row 10: rgb tint, w = textured flag
    float4 CameraPosition;              // row 11: xyz
    float4 TerrainParams0;              // row 12: nearRepeat, farRepeat, blendSqDist, blendSqWidthInv
    float4 TerrainParams1;              // row 13: f2near, f3near, f4near, terrainAmbient
    float4 TerrainParams2;              // row 14: f2far, f3far, f4far, terrainSun
};
ConstantBuffer<SceneConstants> C : register(b0);

Texture2D Tex0 : register(t0);
Texture2D Tex1 : register(t1);
Texture2D Tex2 : register(t2);
Texture2D Tex3 : register(t3);
Texture2D Tex4 : register(t4);
Texture2D Tex5 : register(t5);
SamplerState LinearSampler : register(s0);

struct VSInput  { float3 position : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD; };
struct PSInput
{
    float4 position : SV_POSITION;
    float3 normalW : NORMAL;
    float2 uv : TEXCOORD0;
    float3 worldPos : TEXCOORD1;
};

PSInput VSMain(VSInput input)
{
    PSInput result;
    result.position = mul(float4(input.position, 1.0), C.WorldViewProj);
    result.normalW = normalize(mul(float4(input.normal, 0.0), C.World).xyz);
    result.uv = input.uv;
    result.worldPos = mul(float4(input.position, 1.0), C.World).xyz;
    return result;
}

// Reflection-pass clip plane: CameraPosition.w = enable, LightDirection.w = plane Y.
// Geometry below the water plane must not appear in the mirrored render.
void ClipBelowPlane(float3 worldPos)
{
    if (C.CameraPosition.w > 0.5)
        clip(worldPos.y - C.LightDirection.w);
}

float3 Lighting(float3 normalW)
{
    float ndotl = saturate(dot(normalize(normalW), -normalize(C.LightDirection.xyz)));
    return C.LightColor.rgb * ndotl + C.LightColor.www;
}

float4 SampleAlbedo(float2 uv)
{
    float4 albedo = C.DiffuseColor;
    if (C.DiffuseColor.w > 0.5)
        albedo = float4(albedo.rgb, 1.0) * Tex0.Sample(LinearSampler, uv);
    return albedo;
}

float4 PSStandard(PSInput input) : SV_TARGET
{
    ClipBelowPlane(input.worldPos);
    float4 albedo = SampleAlbedo(input.uv);
    return float4(albedo.rgb * Lighting(input.normalW), 1.0);
}

float4 PSUnlit(PSInput input) : SV_TARGET
{
    ClipBelowPlane(input.worldPos);
    return float4(SampleAlbedo(input.uv).rgb, 1.0);
}

float4 PSCutout(PSInput input) : SV_TARGET
{
    ClipBelowPlane(input.worldPos);
    float4 albedo = SampleAlbedo(input.uv);
    clip(albedo.a - 0.5);
    return float4(albedo.rgb * Lighting(input.normalW), 1.0);
}

// Texture splatting, ported from splat.fx:
// t0 = splat mask, t1..t4 = detail layers, t5 = terrain normal map.
float4 PSTerrain(PSInput input) : SV_TARGET
{
    ClipBelowPlane(input.worldPos);
    float3 d = input.worldPos - C.CameraPosition.xyz;
    float sqDistance = dot(d, d);
    float blendFactor = clamp((sqDistance - C.TerrainParams0.z) * C.TerrainParams0.w, 0.0, 1.0);

    float4 splat = Tex0.Sample(LinearSampler, input.uv);

    float2 nearUv = input.uv * C.TerrainParams0.x;
    float2 farUv = input.uv * C.TerrainParams0.y;

    float4 farColor =
        Tex1.Sample(LinearSampler, farUv) * splat[0] +
        Tex2.Sample(LinearSampler, farUv * C.TerrainParams2.x) * splat[1] +
        Tex3.Sample(LinearSampler, farUv * C.TerrainParams2.y) * splat[2] +
        Tex4.Sample(LinearSampler, farUv * C.TerrainParams2.z) * splat[3];

    float4 nearColor =
        Tex1.Sample(LinearSampler, nearUv) * splat[0] +
        Tex2.Sample(LinearSampler, nearUv * C.TerrainParams1.x) * splat[1] +
        Tex3.Sample(LinearSampler, nearUv * C.TerrainParams1.y) * splat[2] +
        Tex4.Sample(LinearSampler, nearUv * C.TerrainParams1.z) * splat[3];

    float4 terrainColor = farColor * blendFactor + nearColor * (1.0 - blendFactor);

    float3 normal = Tex5.Sample(LinearSampler, input.uv).xyz * 2.0 - 1.0;
    float diffuse = saturate(dot(normal, -normalize(C.LightDirection.xyz)));

    float ambientFactor = C.TerrainParams1.w;
    float sunFactor = C.TerrainParams2.w;
    float4 final = ambientFactor * terrainColor + sunFactor * terrainColor * diffuse;
    return float4(final.rgb, 1.0);
}

// Photo Scenery panel: color from t0, per-pixel scene depth reconstructed
// from the depth map (t1). The legacy dual-pass background/foreground trick
// becomes a single pass: each photo pixel writes the depth of the object it
// contains, and the regular z-buffer handles occlusion of/by 3D objects.
// Legacy encoding: red = closeness c; player-distance comparison used
//   c = 1 - d/255        (d <= 128)
//   c = 0.5 - (d-128)/1024   (d > 128)
// inverted here to distance:
//   d = (1-c)*255        (c >= 0.5)
//   d = 128 + (0.5-c)*1024   (c < 0.5)
float4 PSTransparent(PSInput input) : SV_TARGET
{
    float4 albedo = SampleAlbedo(input.uv);
    return float4(albedo.rgb * Lighting(input.normalW), albedo.a);
}

// Flag cloth, ported from flag.fx: TerrainParams0.x = time, .y = windSpeed.
PSInput VSFlag(VSInput input)
{
    float time = C.TerrainParams0.x;
    float windSpeed = C.TerrainParams0.y;
    float3 pos = input.position;
    float timeangle = (time % 360.0) * 3.0;
    float angleinv = windSpeed + 2.0 - pos.y;
    float angle = 1.57 / angleinv;
    float displacement = sin(pos.x * (10.0 + windSpeed) + timeangle + 3.0 * pos.y);
    displacement += sin(pos.y / 0.2 + timeangle);
    pos.z = displacement * pos.x * 0.09;
    pos.y -= pos.x * sin(angle);
    pos.x -= pos.x * sin(angle);

    PSInput result;
    result.position = mul(float4(pos, 1.0), C.WorldViewProj);
    float3 n = input.normal;
    n.x -= 0.5 * displacement;
    result.normalW = normalize(mul(float4(normalize(n), 0.0), C.World).xyz);
    result.uv = input.uv;
    result.worldPos = mul(float4(pos, 1.0), C.World).xyz;
    return result;
}

// Unlit billboard particles: per-vertex alpha arrives in Normal.x.
PSInput VSParticle(VSInput input)
{
    PSInput result;
    result.position = mul(float4(input.position, 1.0), C.WorldViewProj);
    result.normalW = input.normal;
    result.uv = input.uv;
    result.worldPos = input.position;
    return result;
}

float4 PSParticle(PSInput input) : SV_TARGET
{
    float4 color = Tex0.Sample(LinearSampler, input.uv) * float4(C.DiffuseColor.rgb, 1.0);
    color.a *= saturate(input.normalW.x);
    return color;
}

// Water (WaterEffects.fx spirit): vertices are world-baked (World slot
// carries the reflection view-projection instead). TerrainParams0 =
// time, windForce, waveLength, waveHeight; TerrainParams1/2 = ripples
// (x, z, age, strength). t0 = bump map, t1 = planar reflection.
PSInput VSWater(VSInput input)
{
    PSInput result;
    result.position = mul(float4(input.position, 1.0), C.WorldViewProj);
    result.normalW = input.normal;
    result.uv = input.uv;
    result.worldPos = input.position;
    return result;
}

float RippleBump(float4 ripple, float3 worldPos)
{
    if (ripple.w <= 0.0)
        return 0.0;
    float dist = length(worldPos.xz - ripple.xy);
    float radius = ripple.z * 6.0;                 // expands 6 m/s
    float fade = saturate(1.0 - ripple.z * 0.5);   // dies over ~2 s
    return sin(saturate(1.0 - abs(dist - radius) * 0.7) * 6.283) * fade * ripple.w;
}

float4 PSWater(PSInput input) : SV_TARGET
{
    float time = C.TerrainParams0.x;
    float windForce = C.TerrainParams0.y;
    float waveLength = C.TerrainParams0.z;
    float waveHeight = C.TerrainParams0.w;

    float2 bumpUv = input.uv / waveLength + time * windForce * float2(0.0, 1.0);
    float4 bumpColor = Tex0.Sample(LinearSampler, bumpUv);
    float2 perturbation = waveHeight * (bumpColor.rg - 0.5);
    perturbation.x += 0.02 * (RippleBump(C.TerrainParams1, input.worldPos) + RippleBump(C.TerrainParams2, input.worldPos));

    float4 reflectionPos = mul(float4(input.worldPos, 1.0), C.World); // reflection view-proj
    float2 projectedUv;
    projectedUv.x = reflectionPos.x / reflectionPos.w / 2.0 + 0.5;
    projectedUv.y = -reflectionPos.y / reflectionPos.w / 2.0 + 0.5;
    float4 reflectiveColor = Tex1.Sample(LinearSampler, projectedUv + perturbation);

    float3 eye = normalize(C.CameraPosition.xyz - input.worldPos);
    float fresnel = saturate(dot(eye, float3(0, 1, 0)));
    float4 dullColor = float4(0.1, 0.15, 0.25, 1.0);
    float4 combined = reflectiveColor * (1.0 - fresnel) + dullColor * fresnel;
    return float4(combined.rgb, 0.92);
}

struct PSDepthOutput { float4 color : SV_TARGET; float depth : SV_Depth; };

PSDepthOutput PSPhoto(PSInput input)
{
    PSDepthOutput result;
    result.color = float4(Tex0.Sample(LinearSampler, input.uv).rgb, 1.0);

    float c = Tex1.Sample(LinearSampler, input.uv).r;
    float dist = (c >= 0.5) ? (1.0 - c) * 255.0 : 128.0 + (0.5 - c) * 1024.0;

    float3 ray = normalize(input.worldPos - C.CameraPosition.xyz);
    float3 point3d = C.CameraPosition.xyz + ray * dist;
    // Panels are built in world space (World = identity), so WorldViewProj
    // is the view-projection.
    float4 clip = mul(float4(point3d, 1.0), C.WorldViewProj);
    result.depth = saturate(clip.z / clip.w);
    return result;
}";
        #endregion

        private readonly GraphicsDevice device;
        private ID3D12RootSignature rootSignature;
        private readonly Dictionary<MaterialKind, ID3D12PipelineState> pipelines = new Dictionary<MaterialKind, ID3D12PipelineState>();
        private ID3D12DescriptorHeap srvHeap;
        private int srvDescriptorSize;
        private int nextSrvIndex;
        private Texture2D whiteTexture;

        public Vector3 LightDirection { get; set; } = Vector3.Normalize(new Vector3(0.5f, -0.707f, 0.5f));
        public Vector3 LightColor { get; set; } = new Vector3(0.9f, 0.9f, 0.9f);
        public float AmbientIntensity { get; set; } = 0.35f;

        /// <summary>Scene time in seconds (drives cloth and water animation).</summary>
        public float Time { get; set; }
        /// <summary>Wind speed [m/s] for cloth animation.</summary>
        public float WindSpeed { get; set; } = 4f;
        /// <summary>View-projection of the planar reflection camera (water).</summary>
        public Matrix4x4 ReflectionViewProjection { get; set; } = Matrix4x4.Identity;

        private readonly List<(SceneNode node, Matrix4x4 world, float depth)> transparentQueue =
            new List<(SceneNode, Matrix4x4, float)>();

        private bool reflectionClipEnabled;
        private float reflectionClipHeight;

        public SceneRenderer(GraphicsDevice device)
        {
            this.device = device;

            var rootParameters = new[]
            {
                new RootParameter1(new RootConstants(0, 0, ConstantCount), ShaderVisibility.All),
                new RootParameter1(new RootDescriptorTable1(
                    // DescriptorsVolatile: textures are registered lazily while
                    // a command list referencing the heap may be in flight.
                    new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 6, 0, 0, 0,
                        DescriptorRangeFlags.DescriptorsVolatile)),
                    ShaderVisibility.Pixel),
            };
            var samplers = new[]
            {
                new StaticSamplerDescription(SamplerDescription.LinearWrap, ShaderVisibility.Pixel, 0, 0),
            };
            rootSignature = device.NativeDevice.CreateRootSignature(
                new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout, rootParameters, samplers));

            byte[] vs = ShaderCompiler.Compile(ShaderSource, "VSMain", DxcShaderStage.Vertex, "scene.hlsl");
            byte[] vsFlag = ShaderCompiler.Compile(ShaderSource, "VSFlag", DxcShaderStage.Vertex, "scene.hlsl");
            byte[] vsParticle = ShaderCompiler.Compile(ShaderSource, "VSParticle", DxcShaderStage.Vertex, "scene.hlsl");
            byte[] vsWater = ShaderCompiler.Compile(ShaderSource, "VSWater", DxcShaderStage.Vertex, "scene.hlsl");
            pipelines[MaterialKind.StandardLit] = CreatePipeline(vs, "PSStandard", false, true);
            pipelines[MaterialKind.Unlit] = CreatePipeline(vs, "PSUnlit", false, true);
            pipelines[MaterialKind.CutoutLit] = CreatePipeline(vs, "PSCutout", false, true);
            pipelines[MaterialKind.TerrainSplat] = CreatePipeline(vs, "PSTerrain", false, true);
            pipelines[MaterialKind.PhotoPanel] = CreatePipeline(vs, "PSPhoto", false, true);
            pipelines[MaterialKind.TransparentLit] = CreatePipeline(vs, "PSTransparent", true, false);
            pipelines[MaterialKind.FlagCloth] = CreatePipeline(vsFlag, "PSStandard", false, true);
            pipelines[MaterialKind.Water] = CreatePipeline(vsWater, "PSWater", true, false);
            pipelines[MaterialKind.Particle] = CreatePipeline(vsParticle, "PSParticle", true, false);

            srvHeap = device.NativeDevice.CreateDescriptorHeap(new DescriptorHeapDescription(
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                MaxTextures, DescriptorHeapFlags.ShaderVisible));
            srvDescriptorSize = (int)device.NativeDevice.GetDescriptorHandleIncrementSize(
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

            // Prefill every slot with a null SRV so partially-used descriptor
            // tables never point at uninitialized descriptors.
            var nullDesc = new ShaderResourceViewDescription
            {
                Format = Format.R8G8B8A8_UNorm,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
            };
            for (int i = 0; i < MaxTextures; i++)
                device.NativeDevice.CreateShaderResourceView(null, nullDesc,
                    srvHeap.GetCPUDescriptorHandleForHeapStart() + i * srvDescriptorSize);

            whiteTexture = Texture2D.FromPixels(device, 1, 1, new byte[] { 255, 255, 255, 255 });
            RegisterTexture(whiteTexture);
        }

        private ID3D12PipelineState CreatePipeline(byte[] vs, string pixelEntryPoint, bool alphaBlend, bool depthWrite)
        {
            byte[] ps = ShaderCompiler.Compile(ShaderSource, pixelEntryPoint, DxcShaderStage.Pixel, "scene.hlsl");
            var depthStencil = DepthStencilDescription.Default;
            depthStencil.DepthWriteMask = depthWrite ? DepthWriteMask.All : DepthWriteMask.Zero;
            var desc = new GraphicsPipelineStateDescription
            {
                RootSignature = rootSignature,
                VertexShader = vs,
                PixelShader = ps,
                InputLayout = new InputLayoutDescription(
                    new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                    new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                    new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 24, 0)),
                RasterizerState = RasterizerDescription.CullNone,
                BlendState = alphaBlend ? BlendDescription.NonPremultiplied : BlendDescription.Opaque,
                DepthStencilState = depthStencil,
                DepthStencilFormat = GraphicsDevice.DepthFormat,
                RenderTargetFormats = new[] { GraphicsDevice.BackBufferFormat },
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                SampleDescription = SampleDescription.Default,
            };
            return device.NativeDevice.CreateGraphicsPipelineState(desc);
        }

        /// <summary>Assigns the texture a slot in the shader-visible SRV heap.</summary>
        public void RegisterTexture(Texture2D texture)
        {
            if (texture.SrvIndex >= 0)
                return;
            if (nextSrvIndex >= MaxTextures)
                throw new InvalidOperationException("SRV heap full.");
            texture.SrvIndex = nextSrvIndex++;
            device.NativeDevice.CreateShaderResourceView(texture.Resource, null,
                srvHeap.GetCPUDescriptorHandleForHeapStart() + texture.SrvIndex * srvDescriptorSize);
        }

        /// <summary>Registers a material's TextureSet in contiguous SRV slots.</summary>
        public void RegisterMaterial(Material material)
        {
            if (material.SrvBaseSlot >= 0 || material.TextureSet == null)
                return;
            if (nextSrvIndex + material.TextureSet.Length > MaxTextures)
                throw new InvalidOperationException("SRV heap full.");
            material.SrvBaseSlot = nextSrvIndex;
            foreach (Texture2D texture in material.TextureSet)
            {
                device.NativeDevice.CreateShaderResourceView(texture.Resource, null,
                    srvHeap.GetCPUDescriptorHandleForHeapStart() + nextSrvIndex * srvDescriptorSize);
                nextSrvIndex++;
            }
        }

        /// <summary>Records draw commands for every visible mesh node under root.</summary>
        public void Render(ID3D12GraphicsCommandList4 commandList, Camera camera, SceneNode root)
        {
            commandList.SetGraphicsRootSignature(rootSignature);
            commandList.SetDescriptorHeaps(srvHeap);
            commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

            Matrix4x4 viewProj = camera.GetViewProjection();
            transparentQueue.Clear();
            RenderNode(commandList, root, Matrix4x4.Identity, viewProj, camera.Position, collectTransparents: true);

            // Transparents: back-to-front by view depth (correct blending).
            transparentQueue.Sort((a, b) => b.depth.CompareTo(a.depth));
            foreach (var entry in transparentQueue)
                DrawMesh(commandList, entry.node, entry.world, viewProj, camera.Position);
            transparentQueue.Clear();
        }

        /// <summary>Renders the scene into an offscreen target with a mirrored camera
        /// (planar reflection across the y = waterHeight plane). Water nodes are
        /// skipped; transparents render unsorted into the reflection for cheapness.</summary>
        public void RenderReflection(ID3D12GraphicsCommandList4 commandList, Camera camera,
            SceneNode root, float waterHeight, RenderTexture target, Vortice.Mathematics.Color4 clear)
        {
            var mirrored = new Camera
            {
                Position = Reflect(camera.Position, waterHeight),
                Target = Reflect(camera.Target, waterHeight),
                Up = -Vector3.UnitY,
                FieldOfView = camera.FieldOfView,
                AspectRatio = camera.AspectRatio,
                NearPlane = camera.NearPlane,
                FarPlane = camera.FarPlane,
            };
            ReflectionViewProjection = mirrored.GetViewProjection();
            reflectionClipEnabled = true;
            reflectionClipHeight = waterHeight;

            target.Begin(commandList, clear);
            commandList.SetGraphicsRootSignature(rootSignature);
            commandList.SetDescriptorHeaps(srvHeap);
            commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            RenderNode(commandList, root, Matrix4x4.Identity, ReflectionViewProjection, mirrored.Position, collectTransparents: false);
            target.End(commandList);
            reflectionClipEnabled = false;
        }

        private static Vector3 Reflect(Vector3 v, float planeY)
        {
            return new Vector3(v.X, 2 * planeY - v.Y, v.Z);
        }

        private void RenderNode(ID3D12GraphicsCommandList4 commandList, SceneNode node,
            Matrix4x4 parentWorld, Matrix4x4 viewProj, Vector3 cameraPosition, bool collectTransparents)
        {
            if (!node.Visible)
                return;

            Matrix4x4 world = node.LocalTransform * parentWorld;

            if (node.Mesh != null)
            {
                Material material = node.Material ?? new Material();
                if (material.IsTransparent)
                {
                    if (collectTransparents)
                    {
                        Vector3 position = Vector3.Transform(node.Mesh.BoundsCenter, world);
                        transparentQueue.Add((node, world, Vector3.DistanceSquared(position, cameraPosition)));
                    }
                    // In the reflection pass transparents are skipped (water
                    // must not reflect itself; particles are negligible there).
                }
                else
                {
                    DrawMesh(commandList, node, world, viewProj, cameraPosition);
                }
            }

            foreach (SceneNode child in node.Children)
                RenderNode(commandList, child, world, viewProj, cameraPosition, collectTransparents);
        }

        private void DrawMesh(ID3D12GraphicsCommandList4 commandList, SceneNode node,
            Matrix4x4 world, Matrix4x4 viewProj, Vector3 cameraPosition)
        {
            {
                Material material = node.Material ?? new Material();
                commandList.SetPipelineState(pipelines[material.Kind]);

                var constants = new float[ConstantCount];
                WriteMatrix(constants, 0, world * viewProj);
                // Water reuses the World slot for the reflection view-projection
                // (its vertices are world-baked).
                WriteMatrix(constants, 16, material.Kind == MaterialKind.Water ? ReflectionViewProjection : world);
                constants[32] = LightDirection.X; constants[33] = LightDirection.Y; constants[34] = LightDirection.Z;
                constants[36] = LightColor.X; constants[37] = LightColor.Y; constants[38] = LightColor.Z; constants[39] = AmbientIntensity;
                constants[40] = material.DiffuseColor.X; constants[41] = material.DiffuseColor.Y; constants[42] = material.DiffuseColor.Z;
                constants[44] = cameraPosition.X; constants[45] = cameraPosition.Y; constants[46] = cameraPosition.Z;
                constants[47] = reflectionClipEnabled ? 1f : 0f;
                constants[35] = reflectionClipHeight;
                if (material.Kind == MaterialKind.FlagCloth)
                {
                    constants[48] = Time; constants[49] = WindSpeed;
                }
                else if (material.Kind == MaterialKind.Water)
                {
                    constants[48] = Time; constants[49] = material.WindForce;
                    constants[50] = material.WaveLength; constants[51] = material.WaveHeight;
                    constants[52] = material.Ripple0.X; constants[53] = material.Ripple0.Y;
                    constants[54] = material.Ripple0.Z; constants[55] = material.Ripple0.W;
                    constants[56] = material.Ripple1.X; constants[57] = material.Ripple1.Y;
                    constants[58] = material.Ripple1.Z; constants[59] = material.Ripple1.W;
                }
                else
                {
                    constants[48] = material.NearRepeat; constants[49] = material.FarRepeat;
                    constants[50] = material.BlendSqDistance; constants[51] = material.BlendSqWidthInv;
                    constants[52] = material.NearFactor2; constants[53] = material.NearFactor3; constants[54] = material.NearFactor4;
                    constants[55] = material.TerrainAmbient;
                    constants[56] = material.FarFactor2; constants[57] = material.FarFactor3; constants[58] = material.FarFactor4;
                    constants[59] = material.TerrainSun;
                }

                int srvSlot;
                if (material.TextureSet != null)
                {
                    if (material.SrvBaseSlot < 0)
                        RegisterMaterial(material);
                    srvSlot = material.SrvBaseSlot;
                    constants[43] = 1f;
                }
                else
                {
                    Texture2D texture = material.Texture ?? whiteTexture;
                    if (texture.SrvIndex < 0)
                        RegisterTexture(texture);
                    srvSlot = texture.SrvIndex;
                    constants[43] = material.Texture != null ? 1f : 0f;
                }

                commandList.SetGraphicsRoot32BitConstants(0, constants, 0);
                commandList.SetGraphicsRootDescriptorTable(1,
                    srvHeap.GetGPUDescriptorHandleForHeapStart() + srvSlot * srvDescriptorSize);

                commandList.IASetVertexBuffers(0, node.Mesh.VertexBufferView);
                commandList.IASetIndexBuffer(node.Mesh.IndexBufferView);
                commandList.DrawIndexedInstanced((uint)node.Mesh.IndexCount, 1, 0, 0, 0);
            }
        }

        private static void WriteMatrix(float[] target, int offset, Matrix4x4 m)
        {
            target[offset + 0] = m.M11; target[offset + 1] = m.M12; target[offset + 2] = m.M13; target[offset + 3] = m.M14;
            target[offset + 4] = m.M21; target[offset + 5] = m.M22; target[offset + 6] = m.M23; target[offset + 7] = m.M24;
            target[offset + 8] = m.M31; target[offset + 9] = m.M32; target[offset + 10] = m.M33; target[offset + 11] = m.M34;
            target[offset + 12] = m.M41; target[offset + 13] = m.M42; target[offset + 14] = m.M43; target[offset + 15] = m.M44;
        }

        public void Dispose()
        {
            if (whiteTexture != null) whiteTexture.Dispose();
            if (srvHeap != null) srvHeap.Dispose();
            foreach (var pipeline in pipelines.Values)
                pipeline.Dispose();
            pipelines.Clear();
            if (rootSignature != null) rootSignature.Dispose();
        }
    }
}
