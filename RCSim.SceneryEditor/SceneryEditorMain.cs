using System;
using System.Data;
using System.IO;
using System.Numerics;
using System.Windows.Forms;
using System.Xml.Linq;
using Bonsai.Graphics;
using Bonsai.Graphics.Rendering;
using Bonsai.Graphics.Scene;
using Bonsai.Graphics.TestHost;
using Bonsai.Objects.Terrain;
using RCSim;
using Vortice.Mathematics;

namespace RCSim.SceneryEditor
{
    /// <summary>
    /// Scenery Editor on the new stack (issue 16): a WinForms window with the
    /// DX12 renderer embedded via a panel HWND. The default field renders
    /// live from the Sim's TerrainDefinition DataSet; right-click places the
    /// selected object type on the terrain, delete removes the nearest, and
    /// File Save writes a terrain.def the Sim loads.
    ///
    /// --selftest: loads the stock terrain.def, places a tree, round-trips a
    /// save, renders 60 frames, and exits 0 on success.
    /// </summary>
    internal sealed class SceneryEditorMain : Form
    {
        private readonly string repoRoot;
        private readonly bool selfTest;
        private readonly string outDir;
        private string sceneryDir;
        private string dataDir;
        private string openFile;

        private Panel viewport;
        private ComboBox palette;
        private Label status;
        private Timer renderTimer;

        private GraphicsDevice device;
        private SceneRenderer renderer;
        private SceneNode world;
        private SceneNode editableRoot;
        private readonly System.Collections.Generic.List<SceneNode> billboards = new System.Collections.Generic.List<SceneNode>();
        private Camera camera;
        private Heightmap heightmap;
        private TerrainDefinition definition;
        private Vector3 cursorPosition;
        private SceneNode cursorNode;

        private float orbitYaw = 3.6f, orbitPitch = 0.5f, orbitDistance = 150f;
        private Vector3 focus = new Vector3(0, 0, 0);
        private System.Drawing.Point lastMouse;
        private int renderedFrames;

        private static readonly string[] PaletteItems =
        {
            "Tree", "Simple tree", "Tall tree", "Small tree", "Windmill", "Gate", "Thermal",
        };

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool selfTest = Array.IndexOf(args, "--selftest") >= 0;
            string outDir = args.Length > 1 && !args[args.Length - 1].StartsWith("--")
                ? args[args.Length - 1] : Environment.CurrentDirectory;
            using (var form = new SceneryEditorMain(selfTest, outDir))
            {
                Application.Run(form);
                return form.SelfTestResult;
            }
        }

        public int SelfTestResult { get; private set; }

        public SceneryEditorMain(bool selfTest, string outDir)
        {
            this.selfTest = selfTest;
            this.outDir = outDir;
            repoRoot = FindRepoRoot();
            dataDir = Path.Combine(repoRoot, "RCSim", "data");
            sceneryDir = Path.Combine(dataDir, "scenery", "default");

            Text = "R/C Desk Pilot - Scenery Editor";
            ClientSize = new System.Drawing.Size(1280, 720);
            BuildLayout();

            Load += (s, e) => InitializeGraphics();
            FormClosed += (s, e) => TearDownGraphics();
        }

        private void BuildLayout()
        {
            var menu = new MenuStrip();
            var fileMenu = new ToolStripMenuItem("&File");
            fileMenu.DropDownItems.Add("&Save terrain.def", null, (s, e) => SaveDefinition(openFile));
            fileMenu.DropDownItems.Add("Save &As...", null, (s, e) => SaveDefinitionAs());
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add("E&xit", null, (s, e) => Close());
            menu.Items.Add(fileMenu);
            MainMenuStrip = menu;

            var side = new Panel { Dock = DockStyle.Right, Width = 240, Padding = new Padding(8) };
            palette = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
            palette.Items.AddRange(PaletteItems);
            palette.SelectedIndex = 0;
            var hint = new Label
            {
                Dock = DockStyle.Top,
                Height = 96,
                Text = "Right-click: place selected object\nDel button: remove nearest to cursor\nLeft-drag: orbit  Wheel: zoom\nArrows: pan",
            };
            var deleteButton = new Button { Dock = DockStyle.Top, Text = "Delete nearest object", Height = 32 };
            deleteButton.Click += (s, e) => DeleteNearest();
            status = new Label { Dock = DockStyle.Bottom, Height = 48, Text = "" };
            side.Controls.Add(hint);
            side.Controls.Add(deleteButton);
            side.Controls.Add(palette);
            side.Controls.Add(status);

            viewport = new Panel { Dock = DockStyle.Fill, BackColor = System.Drawing.Color.Black };
            viewport.MouseDown += (s, e) =>
            {
                lastMouse = e.Location;
                if (e.Button == MouseButtons.Right)
                    PlaceObject(e.Location);
            };
            viewport.MouseMove += Viewport_MouseMove;
            viewport.MouseWheel += (s, e) => orbitDistance = Math.Clamp(orbitDistance * (e.Delta > 0 ? 0.9f : 1.1f), 5f, 800f);
            viewport.Resize += (s, e) =>
            {
                if (device != null && viewport.ClientSize.Width > 0 && viewport.ClientSize.Height > 0)
                {
                    device.Resize(viewport.ClientSize.Width, viewport.ClientSize.Height);
                    camera.AspectRatio = (float)viewport.ClientSize.Width / viewport.ClientSize.Height;
                }
            };

            Controls.Add(viewport);
            Controls.Add(side);
            Controls.Add(menu);
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                float step = orbitDistance * 0.05f;
                if (e.KeyCode == Keys.Left) focus.X -= step;
                if (e.KeyCode == Keys.Right) focus.X += step;
                if (e.KeyCode == Keys.Up) focus.Z += step;
                if (e.KeyCode == Keys.Down) focus.Z -= step;
            };
        }

        private void InitializeGraphics()
        {
            device = new GraphicsDevice(viewport.Handle, viewport.ClientSize.Width, viewport.ClientSize.Height,
                enableDebugLayer: selfTest);
            renderer = new SceneRenderer(device);

            // Static base: terrain + sky from the scenery .par (same recipe as the Sim).
            XElement parDefinition = XDocument.Load(Path.Combine(sceneryDir, "default.par")).Root.Element("definition");
            string Value(string name) { var e = parDefinition.Element(name); return e != null ? e.Value : null; }
            var ic = System.Globalization.CultureInfo.InvariantCulture;

            heightmap = new Heightmap(Path.Combine(sceneryDir, Value("heightmap")), 1000f, 100, 100)
            {
                MinHeight = float.Parse(Value("minimumheight"), ic),
                MaxHeight = float.Parse(Value("maximumheight"), ic),
            };
            world = new SceneNode("scenery_editor");
            var terrainMaterial = new Material
            {
                Kind = MaterialKind.TerrainSplat,
                TextureSet = new[]
                {
                    Texture2D.Load(device, ResolvePath(Value("splatlow"))),
                    Texture2D.Load(device, ResolvePath(Value("texture1"))),
                    Texture2D.Load(device, ResolvePath(Value("texture2"))),
                    Texture2D.Load(device, ResolvePath(Value("texture3"))),
                    Texture2D.Load(device, ResolvePath(Value("texture4"))),
                    Texture2D.Load(device, ResolvePath(Value("normalmap"))),
                },
            };
            renderer.RegisterMaterial(terrainMaterial);
            world.AddChild(new SceneNode("terrain")
            {
                Mesh = PrimitiveMeshes.BuildTerrain(device, heightmap, 1f),
                Material = terrainMaterial,
            });
            world.AddChild(new SceneNode("sky")
            {
                Mesh = PrimitiveMeshes.BuildDome(device, 4500f, 16, 16),
                Material = new Material(Texture2D.Load(device, Path.Combine(sceneryDir, "sky_sunny.jpg"))) { Kind = MaterialKind.Unlit },
            });

            // 3D cursor marker.
            cursorNode = world.AddChild(new SceneNode("cursor")
            {
                Mesh = PrimitiveMeshes.BuildQuad(device, new Vector3(0, 1.5f, 0), Vector3.UnitX, Vector3.UnitY, 1.5f),
                Material = new Material(Texture2D.Load(device, Path.Combine(dataDir, "bubble.png"))) { Kind = MaterialKind.CutoutLit },
            });

            // Editable content: the Sim's TerrainDefinition DataSet.
            openFile = Path.Combine(sceneryDir, "terrain.def");
            definition = new TerrainDefinition();
            definition.Load(openFile);
            RebuildEditableWorld();

            camera = new Camera
            {
                AspectRatio = (float)viewport.ClientSize.Width / viewport.ClientSize.Height,
                NearPlane = 0.1f,
                FarPlane = 10000f,
            };

            renderTimer = new Timer { Interval = 15 };
            renderTimer.Tick += (s, e) => RenderFrame();
            renderTimer.Start();
        }

        private string ResolvePath(string reference)
        {
            string local = Path.Combine(sceneryDir, Path.GetFileName(reference.Replace('/', Path.DirectorySeparatorChar)));
            return File.Exists(local) ? local : Path.Combine(repoRoot, "RCSim", reference.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>Rebuilds every editable object node from the DataSet.</summary>
        private void RebuildEditableWorld()
        {
            if (editableRoot != null)
                editableRoot.RemoveFromParent();
            billboards.Clear();
            editableRoot = world.AddChild(new SceneNode("editable"));

            var treeKinds = new (DataTable table, string texture, float halfSize)[]
            {
                (definition.TreeTable, "tall_tree1_256.png", 5f),
                (definition.SimpleTreeTable, "tree1_256.png", 2.5f),
                (definition.SimpleTallTreeTable, "tall_tree1_256.png", 4f),
                (definition.SimpleSmallTreeTable, "small_tree1_256.png", 1.5f),
            };
            foreach (var (table, texture, halfSize) in treeKinds)
            {
                var material = new Material(Texture2D.Load(device, Path.Combine(dataDir, texture))) { Kind = MaterialKind.CutoutLit };
                Mesh quad = PrimitiveMeshes.BuildQuad(device, new Vector3(0, halfSize, 0), Vector3.UnitX, Vector3.UnitY, halfSize);
                foreach (DataRow row in table.Rows)
                {
                    var node = new SceneNode("tree")
                    {
                        Mesh = quad,
                        Material = material,
                        LocalTransform = Matrix4x4.CreateTranslation((Vector3)row["Position"]),
                    };
                    editableRoot.AddChild(node);
                    billboards.Add(node);
                }
            }

            foreach (DataRow row in definition.ObjectTable.Rows)
            {
                string file = Path.Combine(dataDir, (string)row["FileName"]);
                if (!File.Exists(file))
                    continue;
                Vector3 orientation = (Vector3)row["Orientation"];
                SceneNode node = LoadModelNode(file);
                node.LocalTransform = Matrix4x4.CreateFromYawPitchRoll(orientation.Y, orientation.X, orientation.Z)
                    * Matrix4x4.CreateTranslation((Vector3)row["Position"]);
                editableRoot.AddChild(node);
            }

            foreach (DataRow row in definition.GateTable.Rows)
            {
                Vector3 orientation = (Vector3)row["Orientation"];
                SceneNode node = LoadModelNode(Path.Combine(dataDir, "gate1.x"));
                node.LocalTransform = Matrix4x4.CreateFromYawPitchRoll(orientation.Y, orientation.X, orientation.Z)
                    * Matrix4x4.CreateTranslation((Vector3)row["Position"]);
                editableRoot.AddChild(node);
            }

            foreach (DataRow row in definition.WindmillTable.Rows)
            {
                SceneNode tower = LoadModelNode(Path.Combine(dataDir, "windmill_fixed.x"));
                SceneNode blades = LoadModelNode(Path.Combine(dataDir, "windmill_blades.x"));
                tower.AddChild(blades);
                tower.LocalTransform = Matrix4x4.CreateTranslation((Vector3)row["Position"]);
                editableRoot.AddChild(tower);
            }

            // Thermals: translucent bubble markers.
            var thermalMaterial = new Material(Texture2D.Load(device, Path.Combine(dataDir, "bubble.png"))) { Kind = MaterialKind.CutoutLit };
            foreach (DataRow row in definition.ThermalTable.Rows)
            {
                float size = Convert.ToSingle(row["Size"]);
                Vector3 position = (Vector3)row["Position"];
                var node = new SceneNode("thermal")
                {
                    Mesh = PrimitiveMeshes.BuildQuad(device, new Vector3(0, size / 2f, 0), Vector3.UnitX, Vector3.UnitY, size / 2f),
                    Material = thermalMaterial,
                    LocalTransform = Matrix4x4.CreateTranslation(position),
                };
                editableRoot.AddChild(node);
                billboards.Add(node);
            }

            UpdateStatus();
        }

        private SceneNode LoadModelNode(string file)
        {
            var node = new SceneNode(Path.GetFileNameWithoutExtension(file));
            var model = Bonsai.Graphics.Assets.ModelImporter.Load(device, file);
            foreach (var (mesh, material) in model.Parts)
            {
                if (material.Texture != null)
                    renderer.RegisterTexture(material.Texture);
                node.AddChild(new SceneNode { Mesh = mesh, Material = material });
            }
            return node;
        }

        private void UpdateStatus()
        {
            status.Text = string.Format("{0} trees, {1} objects,\n{2} gates, {3} windmills, {4} thermals",
                definition.TreeTable.Rows.Count + definition.SimpleTreeTable.Rows.Count +
                definition.SimpleTallTreeTable.Rows.Count + definition.SimpleSmallTreeTable.Rows.Count,
                definition.ObjectTable.Rows.Count, definition.GateTable.Rows.Count,
                definition.WindmillTable.Rows.Count, definition.ThermalTable.Rows.Count);
        }

        private void Viewport_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                orbitYaw += (e.X - lastMouse.X) * 0.008f;
                orbitPitch = Math.Clamp(orbitPitch + (e.Y - lastMouse.Y) * 0.008f, 0.05f, 1.5f);
                lastMouse = e.Location;
            }
        }

        /// <summary>Ray-marches the picking ray to the terrain surface.</summary>
        private bool PickGround(System.Drawing.Point mouse, out Vector3 hit)
        {
            hit = default;
            float ndcX = 2f * mouse.X / viewport.ClientSize.Width - 1f;
            float ndcY = 1f - 2f * mouse.Y / viewport.ClientSize.Height;

            Matrix4x4 viewProj = camera.GetViewProjection();
            if (!Matrix4x4.Invert(viewProj, out Matrix4x4 inverse))
                return false;
            Vector4 near = Vector4.Transform(new Vector4(ndcX, ndcY, 0f, 1f), inverse);
            Vector4 far = Vector4.Transform(new Vector4(ndcX, ndcY, 1f, 1f), inverse);
            Vector3 origin = new Vector3(near.X, near.Y, near.Z) / near.W;
            Vector3 direction = Vector3.Normalize(new Vector3(far.X, far.Y, far.Z) / far.W - origin);

            for (float t = 0; t < 3000f; t += 0.5f)
            {
                Vector3 point = origin + direction * t;
                if (Math.Abs(point.X) < 500f && Math.Abs(point.Z) < 500f &&
                    point.Y <= heightmap.GetHeightAt(point.X, point.Z))
                {
                    hit = new Vector3(point.X, heightmap.GetHeightAt(point.X, point.Z), point.Z);
                    return true;
                }
            }
            return false;
        }

        private void PlaceObject(System.Drawing.Point mouse)
        {
            if (!PickGround(mouse, out Vector3 hit))
                return;
            cursorPosition = hit;
            switch (palette.SelectedIndex)
            {
                case 0: definition.AddTree(hit); break;
                case 1: definition.AddSimpleTree(hit); break;
                case 2: definition.AddSimpleTallTree(hit); break;
                case 3: definition.AddSimpleSmallTree(hit); break;
                case 4: definition.AddWindmill(hit); break;
                case 5: definition.AddGate(hit, Vector3.Zero, definition.GateTable.Rows.Count, 1); break;
                case 6: definition.AddThermal(hit, 1.5f, 45f); break;
            }
            RebuildEditableWorld();
        }

        private void DeleteNearest()
        {
            DataRow row = definition.GetNearestObject(cursorPosition, out _);
            if (row != null)
            {
                row.Table.Rows.Remove(row);
                RebuildEditableWorld();
            }
        }

        private void SaveDefinition(string path)
        {
            if (path == null)
                return;
            definition.Save(path);
            openFile = path;
        }

        private void SaveDefinitionAs()
        {
            using (var dialog = new SaveFileDialog
            {
                Filter = "Terrain definition (*.def)|*.def",
                InitialDirectory = Path.GetDirectoryName(openFile),
            })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    SaveDefinition(dialog.FileName);
            }
        }

        private void RenderFrame()
        {
            if (device == null || viewport.ClientSize.Width == 0)
                return;

            focus.Y = heightmap.GetHeightAt(focus.X, focus.Z);
            camera.Target = focus;
            camera.Position = focus + new Vector3(
                orbitDistance * (float)(Math.Cos(orbitPitch) * Math.Sin(orbitYaw)),
                orbitDistance * (float)Math.Sin(orbitPitch),
                orbitDistance * (float)(Math.Cos(orbitPitch) * Math.Cos(orbitYaw)));

            foreach (SceneNode node in billboards)
            {
                Vector3 position = node.LocalTransform.Translation;
                float yaw = (float)Math.Atan2(camera.Position.X - position.X, camera.Position.Z - position.Z);
                node.LocalTransform = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(position);
            }
            cursorNode.LocalTransform = Matrix4x4.CreateTranslation(cursorPosition);

            var commandList = device.BeginFrame(new Color4(0.45f, 0.65f, 0.85f, 1f));
            renderer.Render(commandList, camera, world);
            device.EndFrame();
            renderedFrames++;

            if (selfTest && renderedFrames == 60)
                RunSelfTest();
        }

        private void RunSelfTest()
        {
            int gates = definition.GateTable.Rows.Count;
            int thermals = definition.ThermalTable.Rows.Count;
            int treesBefore = definition.TreeTable.Rows.Count;

            // Edit + save round-trip through the Sim's own DataSet format.
            definition.AddTree(new Vector3(12f, heightmap.GetHeightAt(12f, 34f), 34f));
            string savedDef = Path.Combine(outDir, "editortest_terrain.def");
            definition.Save(savedDef);
            var reloaded = new TerrainDefinition();
            reloaded.Load(savedDef);

            bool loadOk = gates == 7 && thermals == 4 && treesBefore > 0;
            bool roundTrip = reloaded.TreeTable.Rows.Count == treesBefore + 1
                && reloaded.GateTable.Rows.Count == gates
                && Math.Abs(((Vector3)reloaded.TreeTable.Rows[treesBefore]["Position"]).X - 12f) < 1e-3f;

            byte[] pixels = FrameCapture.RenderAndReadback(device,
                list => renderer.Render(list, camera, world), new Color4(0.45f, 0.65f, 0.85f, 1f));
            SavePng(pixels, Path.Combine(outDir, "editor_scenery.png"));

            int debugErrors = device.ReportDebugMessages();
            Console.WriteLine("stock def loads : {0} ({1} gates, {2} thermals)", loadOk ? "OK" : "FAILED", gates, thermals);
            Console.WriteLine("edit round-trip : {0}", roundTrip ? "OK" : "FAILED");
            Console.WriteLine("frames rendered : {0}", renderedFrames);
            Console.WriteLine("debug errors    : {0}", debugErrors);
            bool pass = loadOk && roundTrip && debugErrors == 0;
            Console.WriteLine(pass ? "SCENERYEDITORTEST PASS" : "SCENERYEDITORTEST FAIL");
            SelfTestResult = pass ? 0 : 1;
            Close();
        }

        private void TearDownGraphics()
        {
            if (renderTimer != null) renderTimer.Stop();
            if (device != null) device.WaitIdle();
            if (renderer != null) renderer.Dispose();
            if (device != null) device.Dispose();
            device = null;
        }

        private void SavePng(byte[] rgba, string path)
        {
            if (rgba == null) return;
            int width = device.Width, height = device.Height;
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

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "RCDeskPilot.sln")))
                    return dir.FullName;
            throw new InvalidOperationException("Repo root not found.");
        }
    }
}
