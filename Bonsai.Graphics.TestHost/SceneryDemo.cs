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
                    BuildPhotoScenery(device, renderer, root, sceneryDir);
                else
                    heightmap = BuildDefaultScenery(device, renderer, root, sceneryDir, dataDir, billboards, out windmillBlades);

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

        #region Default scenery construction
        internal static Heightmap BuildDefaultScenery(GraphicsDevice device, SceneRenderer renderer,
            SceneNode root, string sceneryDir, string dataDir, List<SceneNode> billboards, out SceneNode windmillBlades)
        {
            XElement definition = XDocument.Load(Path.Combine(sceneryDir, "default.par")).Root.Element("definition");
            string Value(string name) { var e = definition.Element(name); return e != null ? e.Value : null; }

            // Terrain: heightmap + splat material (legacy sizes: 1000m, 100x100 grid).
            var heightmap = new Heightmap(Path.Combine(sceneryDir, Value("heightmap")), 1000f, 100, 100)
            {
                MinHeight = float.Parse(Value("minimumheight"), System.Globalization.CultureInfo.InvariantCulture),
                MaxHeight = float.Parse(Value("maximumheight"), System.Globalization.CultureInfo.InvariantCulture),
            };
            var terrainMaterial = new Material
            {
                Kind = MaterialKind.TerrainSplat,
                TextureSet = new[]
                {
                    Texture2D.Load(device, RepoPath(sceneryDir, Value("splatlow"))),
                    Texture2D.Load(device, RepoPath(sceneryDir, Value("texture1"))),
                    Texture2D.Load(device, RepoPath(sceneryDir, Value("texture2"))),
                    Texture2D.Load(device, RepoPath(sceneryDir, Value("texture3"))),
                    Texture2D.Load(device, RepoPath(sceneryDir, Value("texture4"))),
                    Texture2D.Load(device, RepoPath(sceneryDir, Value("normalmap"))),
                },
            };
            renderer.RegisterMaterial(terrainMaterial);
            root.AddChild(new SceneNode("terrain")
            {
                Mesh = PrimitiveMeshes.BuildTerrain(device, heightmap, 1f),
                Material = terrainMaterial,
            });

            // Sky dome (legacy: radius 4500, 16x16, sunny afternoon).
            root.AddChild(new SceneNode("sky")
            {
                Mesh = PrimitiveMeshes.BuildDome(device, 4500f, 16, 16),
                Material = new Material(Texture2D.Load(device, Path.Combine(sceneryDir, "sky_sunny.jpg"))) { Kind = MaterialKind.Unlit },
            });

            // terrain.def content
            XElement terrainDef = XDocument.Load(Path.Combine(sceneryDir, Value("definition"))).Root;

            var treeKinds = new (string element, string texture, float halfSize)[]
            {
                ("Trees", "tall_tree1_256.png", 5f),
                ("SimpleTrees", "tree1_256.png", 2.5f),
                ("SimpleTallTrees", "tall_tree1_256.png", 4f),
                ("SimpleSmallTrees", "small_tree1_256.png", 1.5f),
            };
            foreach (var (element, texture, halfSize) in treeKinds)
            {
                var material = new Material(Texture2D.Load(device, Path.Combine(dataDir, texture))) { Kind = MaterialKind.CutoutLit };
                Mesh quad = PrimitiveMeshes.BuildQuad(device, new Vector3(0, halfSize, 0), Vector3.UnitX, Vector3.UnitY, halfSize);
                foreach (XElement entry in terrainDef.Elements(element))
                {
                    Vector3 position = ReadVector(entry.Element("Position"));
                    var node = new SceneNode(element) { Mesh = quad, Material = material, LocalTransform = Matrix4x4.CreateTranslation(position) };
                    root.AddChild(node);
                    billboards.Add(node);
                }
            }

            // Placed .x objects (skip files removed from the content, e.g. the old ad billboard).
            foreach (XElement entry in terrainDef.Elements("Objects"))
            {
                string file = Path.Combine(dataDir, entry.Element("FileName").Value);
                if (!File.Exists(file))
                {
                    Console.WriteLine("skipping missing object {0}", Path.GetFileName(file));
                    continue;
                }
                Vector3 position = ReadVector(entry.Element("Position"));
                Vector3 orientation = ReadVector(entry.Element("Orientation"));
                SceneNode node = LoadModelNode(device, renderer, file);
                node.LocalTransform = Matrix4x4.CreateFromYawPitchRoll(orientation.Y, orientation.X, orientation.Z)
                    * Matrix4x4.CreateTranslation(position);
                root.AddChild(node);
            }

            // Gates
            var gateFile = Path.Combine(dataDir, "gate1.x");
            foreach (XElement entry in terrainDef.Elements("Gates"))
            {
                Vector3 position = ReadVector(entry.Element("Position"));
                Vector3 orientation = ReadVector(entry.Element("Orientation"));
                SceneNode node = LoadModelNode(device, renderer, gateFile);
                node.LocalTransform = Matrix4x4.CreateFromYawPitchRoll(orientation.Y, orientation.X, orientation.Z)
                    * Matrix4x4.CreateTranslation(position);
                root.AddChild(node);
            }

            // Windmills: fixed tower + spinning blades child.
            windmillBlades = null;
            foreach (XElement entry in terrainDef.Elements("Windmills"))
            {
                Vector3 position = ReadVector(entry.Element("Position"));
                SceneNode tower = LoadModelNode(device, renderer, Path.Combine(dataDir, "windmill_fixed.x"));
                SceneNode blades = LoadModelNode(device, renderer, Path.Combine(dataDir, "windmill_blades.x"));
                tower.AddChild(blades);
                tower.LocalTransform = Matrix4x4.CreateTranslation(position);
                root.AddChild(tower);
                if (windmillBlades == null)
                    windmillBlades = blades.Children.Count > 0 ? blades.Children[0] : null;
            }
            // The blades node we animate is the mesh-bearing child.
            return heightmap;
        }
        #endregion

        #region Photo scenery construction
        private static void BuildPhotoScenery(GraphicsDevice device, SceneRenderer renderer, SceneNode root, string sceneryDir)
        {
            XElement definition = XDocument.Load(Directory.GetFiles(sceneryDir, "*.par")[0]).Root.Element("definition");
            string Value(string name) { var e = definition.Element(name); return e != null ? e.Value : null; }

            const float s = 1000f;
            // face: color, depth (may be null), quad center, axisU (right, +U), axisV (up, +V)
            var faces = new (string color, string depth, Vector3 center, Vector3 u, Vector3 v)[]
            {
                (Value("front"), Value("frontdepthmap"), new Vector3(0, 0, s), -Vector3.UnitX, Vector3.UnitY),
                (Value("back"), Value("backdepthmap"), new Vector3(0, 0, -s), Vector3.UnitX, Vector3.UnitY),
                (Value("right"), Value("rightdepthmap"), new Vector3(s, 0, 0), Vector3.UnitZ, Vector3.UnitY),
                (Value("left"), Value("leftdepthmap"), new Vector3(-s, 0, 0), -Vector3.UnitZ, Vector3.UnitY),
                (Value("top"), Value("topdepthmap"), new Vector3(0, s, 0), -Vector3.UnitX, Vector3.UnitZ),
                (Value("bottom"), Value("bottomdepthmap"), new Vector3(0, -s, 0), -Vector3.UnitX, -Vector3.UnitZ),
            };

            foreach (var face in faces)
            {
                if (face.color == null)
                    continue;
                Texture2D color = Texture2D.Load(device, Path.Combine(sceneryDir, face.color));
                Material material;
                if (face.depth != null)
                {
                    material = new Material
                    {
                        Kind = MaterialKind.PhotoPanel,
                        TextureSet = new[] { color, Texture2D.Load(device, Path.Combine(sceneryDir, face.depth)) },
                    };
                    renderer.RegisterMaterial(material);
                }
                else
                {
                    material = new Material(color) { Kind = MaterialKind.Unlit };
                    renderer.RegisterTexture(color);
                }
                root.AddChild(new SceneNode("photo_" + face.color)
                {
                    Mesh = PrimitiveMeshes.BuildQuad(device, face.center, face.u, face.v, s),
                    Material = material,
                });
            }
        }
        #endregion

        #region Helpers
        private static SceneNode LoadModelNode(GraphicsDevice device, SceneRenderer renderer, string file)
        {
            var node = new SceneNode(Path.GetFileNameWithoutExtension(file));
            ModelImporter.ImportedModel model = ModelImporter.Load(device, file);
            foreach (var (mesh, material) in model.Parts)
            {
                if (material.Texture != null)
                    renderer.RegisterTexture(material.Texture);
                node.AddChild(new SceneNode { Mesh = mesh, Material = material });
            }
            return node;
        }

        private static string RepoPath(string sceneryDir, string reference)
        {
            // .par values are either scenery-relative filenames or repo-data paths like "data/scenery/default/x.jpg".
            string local = Path.Combine(sceneryDir, Path.GetFileName(reference.Replace('/', '\\')));
            return File.Exists(local) ? local : reference;
        }

        private static Vector3 ReadVector(XElement element)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            return new Vector3(
                float.Parse(element.Element("X").Value, ic),
                float.Parse(element.Element("Y").Value, ic),
                float.Parse(element.Element("Z").Value, ic));
        }

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
