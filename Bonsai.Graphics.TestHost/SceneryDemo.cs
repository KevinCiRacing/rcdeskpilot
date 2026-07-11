using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Xml.Linq;
using Bonsai.Graphics;
using Bonsai.Graphics.Assets;
using Bonsai.Graphics.Rendering;
using Bonsai.Graphics.Scene;
using Bonsai.Graphics.Win32;
using RCSim;
using Bonsai.Objects.Terrain;
using Vortice.Mathematics;

namespace Bonsai.Graphics.TestHost
{
    /// <summary>
    /// Issue 08 demo/selftest: the complete default Scenery (splatted terrain,
    /// sky dome, tree billboards, placed .x objects, spinning windmills) and
    /// the Hasselt Photo Scenery (photo box with depth occlusion), built from
    /// the unmodified .par / terrain.def content.
    /// </summary>
    internal static class SceneryDemo
    {
        public static int Run(string repoRoot, bool photo, bool test, string outDir)
        {
            string sceneryDir = photo
                ? Path.Combine(repoRoot, "RCSim", "data", "scenery", "Modelvliegclub Hasselt")
                : Path.Combine(repoRoot, "RCSim", "data", "scenery", "default");
            string dataDir = Path.Combine(repoRoot, "RCSim", "data");

            using (var window = new Win32Window(photo ? "Bonsai - Photo Scenery" : "Bonsai - Default Scenery", 1280, 720))
            using (var device = new GraphicsDevice(window.Handle, window.ClientWidth, window.ClientHeight, enableDebugLayer: true))
            using (var renderer = new SceneRenderer(device))
            {
                window.Resized += (w, h) => device.Resize(w, h);
                window.KeyDown += key => { if (key == 0x1B) window.Dispose(); };

                var root = new SceneNode("world");
                var billboards = new List<SceneNode>();
                Heightmap heightmap = null;
                SceneNode windmillBlades = null;

                if (photo)
                    SceneryBuilder.BuildPhotoScenery(device, renderer, root, sceneryDir);
                else
                    heightmap = SceneryBuilder.BuildDefaultScenery(device, renderer, root, sceneryDir, dataDir, billboards, out windmillBlades);

                var camera = new Camera
                {
                    AspectRatio = (float)device.Width / device.Height,
                    NearPlane = 0.1f,
                    FarPlane = 10000f,
                };
                window.Resized += (w, h) => camera.AspectRatio = (float)w / h;

                // --- Height query acceptance check (default scenery only):
                // trees in terrain.def are planted on the ground, so the
                // heightmap must reproduce their Y coordinates.
                bool heightsOk = true;
                if (!photo && test)
                {
                    // Primary: the float-coordinate query must reproduce the
                    // rendered terrain mesh exactly at its vertices (this is
                    // what ground contact relies on).
                    heightsOk = CheckHeightsMatchMesh(heightmap);
                    // Sanity: trees in terrain.def sit near the ground (their
                    // Y values carry historical editor offsets up to ~2.5 m).
                    heightsOk &=
                        CheckHeight(heightmap, -33.4746246f, 185.730087f, 4.154431f, 3f) &
                        CheckHeight(heightmap, 352.108978f, -45.81587f, 7.158631f, 3f) &
                        CheckHeight(heightmap, -35.4022751f, 312.609955f, 15.8116484f, 3f) &
                        CheckHeight(heightmap, 0f, -15f, 0f, 1f);
                }

                int frame = 0;
                byte[] shotA = null, shotB = null;
                var stopwatch = Stopwatch.StartNew();
                double renderSeconds = 0;
                int renderedFrames = 0;

                while (window.PumpMessages())
                {
                    if (window.IsMinimized) { System.Threading.Thread.Sleep(50); continue; }

                    float t = frame / 60f;

                    // Free camera flight: a sweeping pass over the field.
                    if (photo)
                    {
                        float yaw = test ? (frame < 60 ? 0f : (float)Math.PI) : t * 0.3f;
                        camera.Position = new Vector3(0, 8, 0);
                        camera.Target = camera.Position + new Vector3((float)Math.Sin(yaw), 0.05f, (float)Math.Cos(yaw));
                    }
                    else
                    {
                        float angle = t * 0.15f + 3.6f;
                        float cameraHeight = 25f + 12f * (float)Math.Sin(t * 0.2);
                        camera.Position = new Vector3((float)Math.Sin(angle) * 160f, cameraHeight, (float)Math.Cos(angle) * 160f);
                        camera.Target = new Vector3(0, 5, 0);

                        // Y-axis billboarding for the tree quads.
                        foreach (SceneNode tree in billboards)
                        {
                            Vector3 position = tree.LocalTransform.Translation;
                            float yaw = (float)Math.Atan2(camera.Position.X - position.X, camera.Position.Z - position.Z);
                            tree.LocalTransform = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(position);
                        }
                        if (windmillBlades != null)
                        {
                            Vector3 pivot = windmillBlades.Mesh.BoundsCenter;
                            windmillBlades.LocalTransform =
                                Matrix4x4.CreateTranslation(-pivot) *
                                Matrix4x4.CreateRotationZ(t * 2f) *
                                Matrix4x4.CreateTranslation(pivot);
                        }
                    }

                    if (test)
                    {
                        if (frame == 30)
                            shotA = FrameCapture.RenderAndReadback(device, list => renderer.Render(list, camera, root), SkyClear);
                        if (frame == 90)
                        {
                            shotB = FrameCapture.RenderAndReadback(device, list => renderer.Render(list, camera, root), SkyClear);
                            break;
                        }
                    }

                    long before = stopwatch.ElapsedTicks;
                    var commandList = device.BeginFrame(SkyClear);
                    renderer.Render(commandList, camera, root);
                    device.EndFrame();
                    renderSeconds += (stopwatch.ElapsedTicks - before) / (double)Stopwatch.Frequency;
                    renderedFrames++;
                    frame++;
                }

                device.WaitIdle();
                if (!test)
                    return 0;

                int debugErrors = device.ReportDebugMessages();
                double fps = renderedFrames / Math.Max(renderSeconds, 1e-6);

                string prefix = photo ? "photo" : "scenery";
                SavePng(shotA, device.Width, device.Height, Path.Combine(outDir, prefix + "_a.png"));
                SavePng(shotB, device.Width, device.Height, Path.Combine(outDir, prefix + "_b.png"));

                bool contentA = CountNonSky(shotA) > 50000;
                bool contentB = CountNonSky(shotB) > 50000;
                bool viewsDiffer = PixelDifference(shotA, shotB) > 10000;

                Console.WriteLine("content in view : {0}", contentA && contentB ? "OK" : "MISSING");
                Console.WriteLine("views differ    : {0}", viewsDiffer ? "OK" : "STATIC");
                if (!photo)
                    Console.WriteLine("height queries  : {0}", heightsOk ? "OK" : "FAILED");
                Console.WriteLine("render fps      : {0:F0} (frame cost only, excl. vsync wait)", fps);
                Console.WriteLine("debug errors    : {0}", debugErrors);

                bool pass = contentA && contentB && viewsDiffer && heightsOk && debugErrors == 0 && fps >= 30;
                Console.WriteLine(pass ? (photo ? "PHOTOTEST PASS" : "SCENERYTEST PASS") : (photo ? "PHOTOTEST FAIL" : "SCENERYTEST FAIL"));
                return pass ? 0 : 1;
            }
        }

        private static readonly Color4 SkyClear = new Color4(0.45f, 0.65f, 0.85f, 1f);


        #region Helpers
        private static bool CheckHeight(Heightmap heightmap, float x, float z, float expected, float tolerance)
        {
            float actual = heightmap.GetHeightAt(x, z);
            bool ok = Math.Abs(actual - expected) < tolerance;
            if (!ok)
                Console.Error.WriteLine("height query at ({0},{1}): expected {2:F2}, got {3:F2}", x, z, expected, actual);
            return ok;
        }

        /// <summary>Float-coordinate queries must reproduce mesh vertex heights.</summary>
        private static bool CheckHeightsMatchMesh(Heightmap heightmap)
        {
            float size = heightmap.Size / 2;
            int xSub = heightmap.XSubdivisions, ySub = heightmap.YSubdivisions;
            var random = new Random(12345);
            for (int i = 0; i < 200; i++)
            {
                int row = random.Next(1, ySub);
                int col = random.Next(1, xSub);
                float x = -size + (size * 2 / xSub) * col;
                float z = size - (size * 2 / ySub) * row;
                float meshY = heightmap.GetHeightAt(row, col);
                float queryY = heightmap.GetHeightAt(x, z);
                if (Math.Abs(meshY - queryY) > 0.05f)
                {
                    Console.Error.WriteLine("mesh/query mismatch at row={0} col={1}: mesh {2:F3}, query {3:F3}", row, col, meshY, queryY);
                    return false;
                }
            }
            return true;
        }

        private static int CountNonSky(byte[] rgba)
        {
            if (rgba == null) return 0;
            int count = 0;
            for (int i = 0; i < rgba.Length; i += 4)
            {
                // sky-blue-ish pixels excluded; terrain/trees/photo content counted
                bool skyish = rgba[i + 2] > rgba[i] && rgba[i + 2] > 120;
                if (!skyish)
                    count++;
            }
            return count;
        }

        private static int PixelDifference(byte[] a, byte[] b)
        {
            if (a == null || b == null) return 0;
            int diff = 0;
            int length = Math.Min(a.Length, b.Length);
            for (int i = 0; i < length; i += 4)
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
        #endregion
    }
}
