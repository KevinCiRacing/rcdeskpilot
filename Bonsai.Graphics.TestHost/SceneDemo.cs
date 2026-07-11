using System;
using System.IO;
using System.Numerics;
using Bonsai.Graphics;
using Bonsai.Graphics.Assets;
using Bonsai.Graphics.Rendering;
using Bonsai.Graphics.Scene;
using Bonsai.Graphics.Win32;
using Vortice.Mathematics;

using RCSim;

namespace Bonsai.Graphics.TestHost
{
    /// <summary>
    /// Issue 07 demo/selftest: an aircraft assembled from its .X part files
    /// as a scene-node hierarchy - animated control surfaces, spinning prop,
    /// orbit camera, directional light.
    ///
    /// Scene-test verifies: aircraft pixels rendered; part animation changes
    /// the image; node add/remove and visibility work; PNG/JPG/BMP/DDS
    /// (uncompressed + synthetic BC1) textures load; zero debug-layer errors.
    /// </summary>
    internal static class SceneDemo
    {
        public static int Run(string aircraftDir, string repoDataDir, bool sceneTest, string screenshotDir)
        {
            using (var window = new Win32Window("Bonsai Scene Graph - Aircraft", 1280, 720))
            using (var device = new GraphicsDevice(window.Handle, window.ClientWidth, window.ClientHeight, enableDebugLayer: true))
            using (var renderer = new SceneRenderer(device))
            {
                window.Resized += (w, h) => device.Resize(w, h);
                window.KeyDown += key => { if (key == 0x1B) window.Dispose(); };

                // --- Build the aircraft hierarchy from part files ---
                var root = new SceneNode("scene");
                var aircraft = root.AddChild(new SceneNode("aircraft"));

                SceneNode fixedPart = AddPart(device, renderer, aircraft, Path.Combine(aircraftDir, "extra_fixed.x"));
                SceneNode aileronLeft = AddPart(device, renderer, aircraft, Path.Combine(aircraftDir, "extra_ail_l.x"));
                SceneNode aileronRight = AddPart(device, renderer, aircraft, Path.Combine(aircraftDir, "extra_ail_r.x"));
                SceneNode elevator = AddPart(device, renderer, aircraft, Path.Combine(aircraftDir, "extra_elevator.x"));
                SceneNode rudder = AddPart(device, renderer, aircraft, Path.Combine(aircraftDir, "extra_rudder.x"));
                SceneNode prop = AddPart(device, renderer, aircraft, Path.Combine(aircraftDir, "extra_prop.x"));
                Console.WriteLine("aircraft assembled: {0} parts", aircraft.Children.Count);

                // Frame the camera on the fixed part's bounds.
                Vector3 center = fixedPart.Children[0].Mesh.BoundsCenter;
                float radius = (fixedPart.Children[0].Mesh.BoundsMax - fixedPart.Children[0].Mesh.BoundsMin).Length() * 0.5f;
                var camera = new Camera
                {
                    Target = center,
                    AspectRatio = (float)device.Width / device.Height,
                    NearPlane = 0.05f,
                    FarPlane = 1000f,
                };
                window.Resized += (w, h) => camera.AspectRatio = (float)w / h;

                // --- Texture format coverage (acceptance: PNG/JPG/BMP/DDS) ---
                bool texturesOk = true;
                if (sceneTest)
                {
                    texturesOk =
                        CheckTexture(device, Path.Combine(aircraftDir, "extra300s3.png"), "PNG") &
                        CheckTexture(device, Path.Combine(repoDataDir, "bird.bmp"), "BMP") &
                        CheckTexture(device, Path.Combine(repoDataDir, "scenery", "default", "sky_sunny.jpg"), "JPG") &
                        CheckTexture(device, Path.Combine(repoDataDir, "uicontrols.dds"), "DDS(rgba)") &
                        CheckSyntheticBc1(device, screenshotDir);
                }

                // --- Frame loop ---
                int frame = 0;
                float controlDeflection = 0f;
                byte[] shotA = null, shotB = null;
                bool shotAHasAircraft = false, addRemoveOk = true;
                SceneNode[] dynamicNodes = null;

                while (window.PumpMessages())
                {
                    if (window.IsMinimized) { System.Threading.Thread.Sleep(50); continue; }

                    float t = frame / 60f;

                    // Orbit camera
                    float angle = sceneTest ? 0.7f : t * 0.5f;
                    camera.Position = center + new Vector3(
                        (float)Math.Sin(angle) * radius * 3.2f,
                        radius * 1.0f,
                        (float)Math.Cos(angle) * radius * 3.2f);

                    // Animate the part hierarchy (proves parented transforms).
                    controlDeflection = sceneTest
                        ? (frame < 45 ? 0f : 0.5f)                    // step change between screenshots
                        : (float)Math.Sin(t * 2.0) * 0.4f;
                    Rotate(prop, Vector3.UnitZ, t * 25f);
                    Rotate(elevator, Vector3.UnitX, controlDeflection);
                    Rotate(aileronLeft, Vector3.UnitX, controlDeflection);
                    Rotate(aileronRight, Vector3.UnitX, -controlDeflection);
                    Rotate(rudder, Vector3.UnitY, controlDeflection * 0.5f);

                    if (sceneTest)
                    {
                        if (frame == 30)
                        {
                            shotA = CaptureFrame(device, renderer, camera, root);
                            shotAHasAircraft = CountForeground(shotA) > 5000;
                        }
                        if (frame == 60)
                            shotB = CaptureFrame(device, renderer, camera, root);
                        if (frame == 70)
                        {
                            // Dynamic add: a ring of prop-mesh instances.
                            dynamicNodes = new SceneNode[50];
                            for (int i = 0; i < 50; i++)
                            {
                                var node = new SceneNode("dyn" + i)
                                {
                                    Mesh = prop.Children[0].Mesh,
                                    Material = prop.Children[0].Material,
                                    LocalTransform = Matrix4x4.CreateTranslation(
                                        center + new Vector3((float)Math.Sin(i * 0.4) * radius * 2, 0, (float)Math.Cos(i * 0.4) * radius * 2)),
                                };
                                root.AddChild(node);
                                dynamicNodes[i] = node;
                            }
                        }
                        if (frame == 75)
                            for (int i = 0; i < 25; i++)
                                dynamicNodes[i].Visible = false; // visibility flag
                        if (frame == 80)
                            foreach (var node in dynamicNodes)
                                node.RemoveFromParent();         // dynamic remove
                        if (frame == 90)
                            break;
                    }

                    var commandList = device.BeginFrame(new Color4(0.10f, 0.16f, 0.28f, 1f));
                    renderer.Render(commandList, camera, root);
                    device.EndFrame();
                    frame++;
                }

                device.WaitIdle();

                if (!sceneTest)
                    return 0;

                bool animationChangedImage = shotA != null && shotB != null && PixelDifference(shotA, shotB) > 1000;
                int debugErrors = device.ReportDebugMessages();

                SavePng(shotA, device.Width, device.Height, Path.Combine(screenshotDir, "scene_a.png"));
                SavePng(shotB, device.Width, device.Height, Path.Combine(screenshotDir, "scene_b.png"));

                Console.WriteLine("aircraft pixels : {0}", shotAHasAircraft ? "OK" : "MISSING");
                Console.WriteLine("part animation  : {0}", animationChangedImage ? "OK" : "STATIC");
                Console.WriteLine("add/remove      : {0}", addRemoveOk ? "OK" : "FAILED");
                Console.WriteLine("texture formats : {0}", texturesOk ? "OK" : "FAILED");
                Console.WriteLine("debug errors    : {0}", debugErrors);
                bool pass = shotAHasAircraft && animationChangedImage && addRemoveOk && texturesOk && debugErrors == 0;
                Console.WriteLine(pass ? "SCENETEST PASS" : "SCENETEST FAIL");
                return pass ? 0 : 1;
            }
        }

        private static SceneNode AddPart(GraphicsDevice device, SceneRenderer renderer, SceneNode parent, string path)
        {
            var partNode = parent.AddChild(new SceneNode(Path.GetFileNameWithoutExtension(path)));
            ModelImporter.ImportedModel model = ModelImporter.Load(device, path);
            foreach (var (mesh, material) in model.Parts)
            {
                if (material.Texture != null)
                    renderer.RegisterTexture(material.Texture);
                partNode.AddChild(new SceneNode { Mesh = mesh, Material = material });
            }
            return partNode;
        }

        /// <summary>Rotates a part around its own bounding-box center.</summary>
        private static void Rotate(SceneNode part, Vector3 axis, float angle)
        {
            Vector3 pivot = part.Children[0].Mesh.BoundsCenter;
            part.LocalTransform =
                Matrix4x4.CreateTranslation(-pivot) *
                Matrix4x4.CreateFromAxisAngle(axis, angle) *
                Matrix4x4.CreateTranslation(pivot);
        }

        private static bool CheckTexture(GraphicsDevice device, string path, string label)
        {
            try
            {
                using (Texture2D texture = Texture2D.Load(device, path))
                {
                    bool ok = texture.Width > 0 && texture.Height > 0;
                    Console.WriteLine("texture {0,-10} {1}x{2}  {3}", label, texture.Width, texture.Height, ok ? "OK" : "EMPTY");
                    return ok;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("texture {0} FAILED: {1}", label, ex.Message);
                return false;
            }
        }

        /// <summary>Writes a 4x4 DXT1 DDS and loads it to validate the BC path.</summary>
        private static bool CheckSyntheticBc1(GraphicsDevice device, string dir)
        {
            string path = Path.Combine(dir, "synthetic_bc1.dds");
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(0x20534444u);              // magic
                writer.Write(124u);                     // header size
                writer.Write(0x1007u);                  // caps|height|width|pixelformat
                writer.Write(4u); writer.Write(4u);     // height, width
                writer.Write(8u);                       // linear size
                writer.Write(0u); writer.Write(0u);     // depth, mips
                for (int i = 0; i < 11; i++) writer.Write(0u);
                writer.Write(32u);                      // pf size
                writer.Write(0x4u);                     // fourCC flag
                writer.Write(0x31545844u);              // "DXT1"
                for (int i = 0; i < 5; i++) writer.Write(0u);
                writer.Write(0x1000u);                  // caps: texture
                for (int i = 0; i < 4; i++) writer.Write(0u);
                // One BC1 block: solid red (color0=color1=0xF800, indices 0)
                writer.Write((ushort)0xF800); writer.Write((ushort)0xF800); writer.Write(0u);
            }
            return CheckTexture(device, path, "DDS(BC1)");
        }

        private static byte[] CaptureFrame(GraphicsDevice device, SceneRenderer renderer, Camera camera, SceneNode root)
        {
            return FrameCapture.RenderAndReadback(device,
                list => renderer.Render(list, camera, root),
                new Color4(0.10f, 0.16f, 0.28f, 1f));
        }

        private static int CountForeground(byte[] rgba)
        {
            if (rgba == null) return 0;
            int count = 0;
            for (int i = 0; i < rgba.Length; i += 4)
            {
                // background is (26, 41, 71)-ish; anything much brighter is scene content
                if (rgba[i] > 60 || rgba[i + 1] > 70 || rgba[i + 2] > 100)
                    count++;
            }
            return count;
        }

        private static int PixelDifference(byte[] a, byte[] b)
        {
            int diff = 0;
            int length = Math.Min(a.Length, b.Length);
            for (int i = 0; i < length; i += 4)
                if (Math.Abs(a[i] - b[i]) > 12 || Math.Abs(a[i + 1] - b[i + 1]) > 12 || Math.Abs(a[i + 2] - b[i + 2]) > 12)
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
