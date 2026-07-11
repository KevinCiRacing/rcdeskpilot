using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Bonsai.Graphics;
using Bonsai.Graphics.Rendering;
using Bonsai.Graphics.Scene;
using Bonsai.Graphics.Win32;
using Vortice.Mathematics;

using RCSim;

namespace Bonsai.Graphics.TestHost
{
    /// <summary>
    /// Issue 09 demo/selftest: animated water with planar reflection and
    /// ripples (lake from terrain.def at (90,0,180) size 100), wind-driven
    /// flag cloth, sorted transparency, and smoke/thermal particle systems.
    /// </summary>
    internal static class EffectsDemo
    {
        public static int Run(string repoRoot, bool test, string outDir)
        {
            string dataDir = Path.Combine(repoRoot, "RCSim", "data");

            using (var window = new Win32Window("Bonsai - Effect Materials", 1280, 720))
            using (var device = new GraphicsDevice(window.Handle, window.ClientWidth, window.ClientHeight, enableDebugLayer: true))
            using (var renderer = new SceneRenderer(device))
            using (var reflection = new RenderTexture(device, 512, 512))
            {
                window.Resized += (w, h) => device.Resize(w, h);
                window.KeyDown += key => { if (key == 0x1B) window.Dispose(); };

                var root = new SceneNode("world");

                // Ground plane (simple green, keeps focus on effects).
                root.AddChild(new SceneNode("ground")
                {
                    Mesh = PrimitiveMeshes.BuildQuad(device, Vector3.Zero, Vector3.UnitX, -Vector3.UnitZ, 500f),
                    Material = new Material(Texture2D.Load(device, Path.Combine(dataDir, "scenery", "default", "grass1.jpg"))),
                });
                // Sky dome for the reflection to pick up.
                root.AddChild(new SceneNode("sky")
                {
                    Mesh = PrimitiveMeshes.BuildDome(device, 4500f, 16, 16),
                    Material = new Material(Texture2D.Load(device, Path.Combine(dataDir, "scenery", "default", "sky_sunny.jpg"))) { Kind = MaterialKind.Unlit },
                });

                // Water: terrain.def lake, world-baked quad at (90, 0.05, 180), size 100.
                var waterCenter = new Vector3(90f, 0.05f, 180f);
                var waterMaterial = new Material
                {
                    Kind = MaterialKind.Water,
                    TextureSet = new[]
                    {
                        Texture2D.Load(device, Path.Combine(dataDir, "waterbump.dds")),
                        reflection.AsTexture,
                    },
                };
                renderer.RegisterMaterial(waterMaterial);
                var waterNode = root.AddChild(new SceneNode("water")
                {
                    Mesh = PrimitiveMeshes.BuildQuad(device, waterCenter, Vector3.UnitX, -Vector3.UnitZ, 50f),
                    Material = waterMaterial,
                });

                // Flag: pole + cloth grid hinged at the pole (flag.fx animation).
                var flagPosition = new Vector3(60f, 0f, 160f);
                SceneNode pole = LoadModel(device, renderer, root, Path.Combine(dataDir, "flagpole.x"));
                pole.LocalTransform = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(flagPosition);
                var flagNode = root.AddChild(new SceneNode("flag")
                {
                    Mesh = PrimitiveMeshes.BuildGrid(device, 1.5f, 1.0f, 24, 12),
                    Material = new Material(Texture2D.Load(device, Path.Combine(repoRoot, "RCSim", "Aircraft", "extra", "icon.png"))) { Kind = MaterialKind.FlagCloth },
                    LocalTransform = Matrix4x4.CreateTranslation(flagPosition + new Vector3(0, 4.2f, 0)),
                });

                // A transparent-lit panel to prove sorted blending.
                root.AddChild(new SceneNode("glass")
                {
                    Mesh = PrimitiveMeshes.BuildQuad(device, new Vector3(75f, 3f, 170f), Vector3.UnitX, Vector3.UnitY, 3f),
                    Material = new Material { Kind = MaterialKind.TransparentLit, DiffuseColor = new Vector4(0.3f, 0.5f, 1f, 0.45f) },
                });

                // Particles: smoke trail + thermal bubbles.
                var smoke = new ParticleSystem(device, 512) { EmitPosition = new Vector3(70f, 1.5f, 150f), EmitRate = 60f, Life = 4f, StartSize = 0.4f, GrowRate = 0.8f, EmitVelocity = new Vector3(1.5f, 2.5f, 0) };
                root.AddChild(new SceneNode("smoke")
                {
                    Mesh = smoke.Mesh,
                    Material = new Material(Texture2D.Load(device, Path.Combine(dataDir, "smokepuff.png"))) { Kind = MaterialKind.Particle },
                });
                var thermal = new ParticleSystem(device, 256, seed: 77) { EmitPosition = new Vector3(95f, 0.5f, 175f), EmitRate = 25f, Life = 5f, StartSize = 0.6f, GrowRate = 0.3f, EmitVelocity = new Vector3(0, 3f, 0), VelocityJitter = 1f };
                root.AddChild(new SceneNode("thermal")
                {
                    Mesh = thermal.Mesh,
                    Material = new Material(Texture2D.Load(device, Path.Combine(dataDir, "bubble.png"))) { Kind = MaterialKind.Particle },
                });

                var camera = new Camera
                {
                    Position = new Vector3(55f, 6f, 130f),
                    Target = new Vector3(90f, 1f, 180f),
                    AspectRatio = (float)device.Width / device.Height,
                    NearPlane = 0.1f,
                    FarPlane = 10000f,
                };
                window.Resized += (w, h) => camera.AspectRatio = (float)w / h;

                int frame = 0;
                byte[] shotA = null, shotB = null;
                int particlesAtA = 0, particlesAtB = 0;

                while (window.PumpMessages())
                {
                    if (window.IsMinimized) { System.Threading.Thread.Sleep(50); continue; }

                    float t = frame / 60f;
                    renderer.Time = t;
                    renderer.WindSpeed = 4f + 2f * (float)Math.Sin(t * 0.5f);

                    // Ripple triggers (as if something touched the water).
                    waterMaterial.Ripple0 = new Vector4(85f, 175f, t % 2f, 1f);
                    waterMaterial.Ripple1 = new Vector4(100f, 190f, (t + 1f) % 2f, 0.8f);

                    smoke.Update(1f / 60f, camera.Position);
                    thermal.Update(1f / 60f, camera.Position);

                    if (test && frame == 40) { shotA = Capture(device, renderer, camera, root, reflection); particlesAtA = smoke.AliveCount + thermal.AliveCount; }
                    if (test && frame == 120) { shotB = Capture(device, renderer, camera, root, reflection); particlesAtB = smoke.AliveCount + thermal.AliveCount; break; }

                    var commandList = device.BeginFrame(new Color4(0.45f, 0.65f, 0.85f, 1f));
                    renderer.RenderReflection(commandList, camera, root, 0.05f, reflection, new Color4(0.45f, 0.65f, 0.85f, 1f));
                    RebindBackbuffer(device, commandList);
                    renderer.Render(commandList, camera, root);
                    device.EndFrame();
                    frame++;
                }

                device.WaitIdle();
                if (!test)
                    return 0;

                int debugErrors = device.ReportDebugMessages();
                SavePng(shotA, device.Width, device.Height, Path.Combine(outDir, "effects_a.png"));
                SavePng(shotB, device.Width, device.Height, Path.Combine(outDir, "effects_b.png"));

                bool animated = PixelDifference(shotA, shotB) > 5000;
                bool particlesAlive = particlesAtA > 50 && particlesAtB > particlesAtA;

                Console.WriteLine("animation      : {0}", animated ? "OK" : "STATIC");
                Console.WriteLine("particles      : {0} -> {1}  {2}", particlesAtA, particlesAtB, particlesAlive ? "OK" : "FAILED");
                Console.WriteLine("debug errors   : {0}", debugErrors);
                bool pass = animated && particlesAlive && debugErrors == 0;
                Console.WriteLine(pass ? "EFFECTSTEST PASS" : "EFFECTSTEST FAIL");
                return pass ? 0 : 1;
            }
        }

        private static byte[] Capture(GraphicsDevice device, SceneRenderer renderer, Camera camera, SceneNode root, RenderTexture reflection)
        {
            return FrameCapture.RenderAndReadback(device, list =>
            {
                renderer.RenderReflection(list, camera, root, 0.05f, reflection, new Color4(0.45f, 0.65f, 0.85f, 1f));
                RebindBackbuffer(device, list);
                renderer.Render(list, camera, root);
            }, new Color4(0.45f, 0.65f, 0.85f, 1f));
        }

        private static void RebindBackbuffer(GraphicsDevice device, Vortice.Direct3D12.ID3D12GraphicsCommandList4 commandList)
        {
            device.BindBackbuffer(commandList);
        }

        private static SceneNode LoadModel(GraphicsDevice device, SceneRenderer renderer, SceneNode root, string file)
        {
            var node = new SceneNode(Path.GetFileNameWithoutExtension(file));
            var model = Bonsai.Graphics.Assets.ModelImporter.Load(device, file);
            foreach (var (mesh, material) in model.Parts)
            {
                if (material.Texture != null)
                    renderer.RegisterTexture(material.Texture);
                node.AddChild(new SceneNode { Mesh = mesh, Material = material });
            }
            root.AddChild(node);
            return node;
        }

        private static int PixelDifference(byte[] a, byte[] b)
        {
            if (a == null || b == null) return 0;
            int diff = 0;
            for (int i = 0; i < Math.Min(a.Length, b.Length); i += 4)
                if (Math.Abs(a[i] - b[i]) > 16 || Math.Abs(a[i + 1] - b[i + 1]) > 16 || Math.Abs(a[i + 2] - b[i + 2]) > 16)
                    diff++;
            return diff;
        }

        private static void SavePng(byte[] rgba, int width, int height, string path)
        {
            if (rgba == null) return;
            using (var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int i = (y * width + x) * 4;
                        bitmap.SetPixel(x, y, System.Drawing.Color.FromArgb(255, rgba[i], rgba[i + 1], rgba[i + 2]));
                    }
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
    }
}
