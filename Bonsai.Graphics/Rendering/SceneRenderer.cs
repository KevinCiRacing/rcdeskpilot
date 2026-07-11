using System;
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
    /// One standard lit-textured PSO; a shader-visible SRV heap for textures.
    /// </summary>
    public sealed class SceneRenderer : IDisposable
    {
        private const int MaxTextures = 256;

        private const string ShaderSource = @"
struct SceneConstants
{
    row_major float4x4 WorldViewProj;
    row_major float4x4 World;
    float4 LightDirection;   // xyz = direction the light travels
    float4 LightColor;       // rgb * intensity, w = ambient
    float4 DiffuseColor;     // material tint; w = 1 when textured
};
ConstantBuffer<SceneConstants> Constants : register(b0);

Texture2D DiffuseTexture : register(t0);
SamplerState LinearSampler : register(s0);

struct VSInput  { float3 position : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD; };
struct PSInput  { float4 position : SV_POSITION; float3 normalW : NORMAL; float2 uv : TEXCOORD; };

PSInput VSMain(VSInput input)
{
    PSInput result;
    result.position = mul(float4(input.position, 1.0), Constants.WorldViewProj);
    result.normalW = normalize(mul(float4(input.normal, 0.0), Constants.World).xyz);
    result.uv = input.uv;
    return result;
}

float4 PSMain(PSInput input) : SV_TARGET
{
    float3 n = normalize(input.normalW);
    float ndotl = saturate(dot(n, -normalize(Constants.LightDirection.xyz)));
    float ambient = Constants.LightColor.w;
    float3 lighting = Constants.LightColor.rgb * ndotl + ambient.xxx;

    float4 albedo = Constants.DiffuseColor;
    if (Constants.DiffuseColor.w > 0.5)
        albedo *= DiffuseTexture.Sample(LinearSampler, input.uv);
    return float4(albedo.rgb * lighting, 1.0);
}";

        private readonly GraphicsDevice device;
        private ID3D12RootSignature rootSignature;
        private ID3D12PipelineState pipeline;
        private ID3D12DescriptorHeap srvHeap;
        private int srvDescriptorSize;
        private int nextSrvIndex;
        private Texture2D whiteTexture;

        public Vector3 LightDirection { get; set; } = Vector3.Normalize(new Vector3(0.5f, -0.707f, 0.5f));
        public Vector3 LightColor { get; set; } = new Vector3(0.9f, 0.9f, 0.9f);
        public float AmbientIntensity { get; set; } = 0.35f;

        public SceneRenderer(GraphicsDevice device)
        {
            this.device = device;

            var rootParameters = new[]
            {
                new RootParameter1(new RootConstants(0, 0, 44), ShaderVisibility.All),
                new RootParameter1(new RootDescriptorTable1(
                    new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 0, 0, 0)),
                    ShaderVisibility.Pixel),
            };
            var samplers = new[]
            {
                new StaticSamplerDescription(SamplerDescription.LinearWrap, ShaderVisibility.Pixel, 0, 0),
            };
            rootSignature = device.NativeDevice.CreateRootSignature(
                new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout, rootParameters, samplers));

            byte[] vs = ShaderCompiler.Compile(ShaderSource, "VSMain", DxcShaderStage.Vertex, "scene.hlsl");
            byte[] ps = ShaderCompiler.Compile(ShaderSource, "PSMain", DxcShaderStage.Pixel, "scene.hlsl");

            var psoDesc = new GraphicsPipelineStateDescription
            {
                RootSignature = rootSignature,
                VertexShader = vs,
                PixelShader = ps,
                InputLayout = new InputLayoutDescription(
                    new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                    new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                    new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 24, 0)),
                RasterizerState = RasterizerDescription.CullNone,
                BlendState = BlendDescription.Opaque,
                DepthStencilState = DepthStencilDescription.Default,
                DepthStencilFormat = GraphicsDevice.DepthFormat,
                RenderTargetFormats = new[] { GraphicsDevice.BackBufferFormat },
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                SampleDescription = SampleDescription.Default,
            };
            pipeline = device.NativeDevice.CreateGraphicsPipelineState(psoDesc);

            srvHeap = device.NativeDevice.CreateDescriptorHeap(new DescriptorHeapDescription(
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                MaxTextures, DescriptorHeapFlags.ShaderVisible));
            srvDescriptorSize = (int)device.NativeDevice.GetDescriptorHandleIncrementSize(
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

            whiteTexture = Texture2D.FromPixels(device, 1, 1, new byte[] { 255, 255, 255, 255 });
            RegisterTexture(whiteTexture);
        }

        /// <summary>Assigns the texture a slot in the shader-visible SRV heap.</summary>
        public void RegisterTexture(Texture2D texture)
        {
            if (texture.SrvIndex >= 0)
                return;
            if (nextSrvIndex >= MaxTextures)
                throw new InvalidOperationException("SRV heap full.");
            texture.SrvIndex = nextSrvIndex++;
            CpuDescriptorHandle handle = srvHeap.GetCPUDescriptorHandleForHeapStart() + texture.SrvIndex * srvDescriptorSize;
            device.NativeDevice.CreateShaderResourceView(texture.Resource, null, handle);
        }

        /// <summary>Records draw commands for every visible mesh node under root.</summary>
        public void Render(ID3D12GraphicsCommandList4 commandList, Camera camera, SceneNode root)
        {
            commandList.SetPipelineState(pipeline);
            commandList.SetGraphicsRootSignature(rootSignature);
            commandList.SetDescriptorHeaps(srvHeap);
            commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

            Matrix4x4 viewProj = camera.GetViewProjection();
            RenderNode(commandList, root, Matrix4x4.Identity, viewProj);
        }

        private void RenderNode(ID3D12GraphicsCommandList4 commandList, SceneNode node, Matrix4x4 parentWorld, Matrix4x4 viewProj)
        {
            if (!node.Visible)
                return;

            Matrix4x4 world = node.LocalTransform * parentWorld;

            if (node.Mesh != null)
            {
                Material material = node.Material ?? new Material();
                Texture2D texture = material.Texture ?? whiteTexture;
                if (texture.SrvIndex < 0)
                    RegisterTexture(texture);

                var constants = new float[44];
                WriteMatrix(constants, 0, world * viewProj);
                WriteMatrix(constants, 16, world);
                constants[32] = LightDirection.X; constants[33] = LightDirection.Y; constants[34] = LightDirection.Z; constants[35] = 0;
                constants[36] = LightColor.X; constants[37] = LightColor.Y; constants[38] = LightColor.Z; constants[39] = AmbientIntensity;
                constants[40] = material.DiffuseColor.X; constants[41] = material.DiffuseColor.Y; constants[42] = material.DiffuseColor.Z;
                constants[43] = material.Texture != null ? 1f : 0f;
                commandList.SetGraphicsRoot32BitConstants(0, constants, 0);

                GpuDescriptorHandle srv = srvHeap.GetGPUDescriptorHandleForHeapStart() + texture.SrvIndex * srvDescriptorSize;
                commandList.SetGraphicsRootDescriptorTable(1, srv);

                commandList.IASetVertexBuffers(0, node.Mesh.VertexBufferView);
                commandList.IASetIndexBuffer(node.Mesh.IndexBufferView);
                commandList.DrawIndexedInstanced((uint)node.Mesh.IndexCount, 1, 0, 0, 0);
            }

            foreach (SceneNode child in node.Children)
                RenderNode(commandList, child, world, viewProj);
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
            if (pipeline != null) pipeline.Dispose();
            if (rootSignature != null) rootSignature.Dispose();
        }
    }
}
