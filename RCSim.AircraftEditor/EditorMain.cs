using System;
using System.IO;
using System.Numerics;
using System.Windows.Forms;
using Bonsai.Graphics;
using Bonsai.Graphics.Rendering;
using Bonsai.Graphics.Scene;
using Bonsai.Graphics.TestHost;
using RCSim.DataClasses;
using RCSim.Interfaces;
using Vortice.Mathematics;

namespace RCSim.AircraftEditor
{
    /// <summary>
    /// Aircraft Editor on the new stack (issue 16): a WinForms window with the
    /// DX12 renderer embedded via a panel HWND. Open a .par, orbit the model,
    /// animate its control surfaces, edit every Aircraft Parameter in the
    /// grid, and save a .par the Sim loads.
    ///
    /// --selftest: loads the stock Xtra, renders 60 frames into the panel,
    /// round-trips a save, and exits 0 on success.
    /// </summary>
    internal sealed class EditorMain : Form
    {
        /// <summary>Editable control values for the surface-animation preview.</summary>
        private sealed class PreviewControls : IAirplaneControl
        {
            public double Throttle { get; set; }
            public double Ailerons { get; set; }
            public double Elevator { get; set; }
            public double Rudder { get; set; }
            public double Flaps { get; set; }
            public double Gear { get; set; }
            public AircraftParameters AircraftParameters { get; set; }
            public float RotorRPM { get; set; }
            public float RelativeRotorForce { get; set; }
        }

        private readonly string repoRoot;
        private readonly bool selfTest;
        private readonly string outDir;

        private Panel viewport;
        private PropertyGrid propertyGrid;
        private CheckBox animateBox;
        private Timer renderTimer;

        private GraphicsDevice device;
        private SceneRenderer renderer;
        private SceneNode world;
        private SceneNode aircraftNode;
        private Camera camera;
        private AircraftVisual visual;
        private AircraftParameters parameters;
        private readonly PreviewControls controls = new PreviewControls();
        private string openFile;

        // Orbit camera state
        private float orbitYaw = 0.6f, orbitPitch = 0.25f, orbitDistance = 6f;
        private System.Drawing.Point lastMouse;
        private int renderedFrames;

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool selfTest = Array.IndexOf(args, "--selftest") >= 0;
            string outDir = args.Length > 1 && !args[args.Length - 1].StartsWith("--")
                ? args[args.Length - 1] : Environment.CurrentDirectory;
            using (var form = new EditorMain(selfTest, outDir))
            {
                Application.Run(form);
                return form.SelfTestResult;
            }
        }

        public int SelfTestResult { get; private set; }

        public EditorMain(bool selfTest, string outDir)
        {
            this.selfTest = selfTest;
            this.outDir = outDir;
            repoRoot = FindRepoRoot();

            Text = "R/C Desk Pilot - Aircraft Editor";
            ClientSize = new System.Drawing.Size(1280, 720);
            BuildLayout();

            Load += (s, e) => InitializeGraphics();
            FormClosed += (s, e) => TearDownGraphics();
        }

        private void BuildLayout()
        {
            var menu = new MenuStrip();
            var fileMenu = new ToolStripMenuItem("&File");
            fileMenu.DropDownItems.Add("&Open...", null, (s, e) => OpenAircraft());
            fileMenu.DropDownItems.Add("&Save", null, (s, e) => SaveAircraft(openFile));
            fileMenu.DropDownItems.Add("Save &As...", null, (s, e) => SaveAircraftAs());
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add("E&xit", null, (s, e) => Close());
            menu.Items.Add(fileMenu);
            MainMenuStrip = menu;

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                FixedPanel = FixedPanel.Panel2,
                SplitterDistance = 900,
            };

            viewport = new Panel { Dock = DockStyle.Fill, BackColor = System.Drawing.Color.Black };
            viewport.MouseDown += (s, e) => lastMouse = e.Location;
            viewport.MouseMove += Viewport_MouseMove;
            viewport.MouseWheel += (s, e) => orbitDistance = Math.Clamp(orbitDistance * (e.Delta > 0 ? 0.9f : 1.1f), 1f, 60f);
            viewport.Resize += (s, e) =>
            {
                if (device != null && viewport.ClientSize.Width > 0 && viewport.ClientSize.Height > 0)
                {
                    device.Resize(viewport.ClientSize.Width, viewport.ClientSize.Height);
                    camera.AspectRatio = (float)viewport.ClientSize.Width / viewport.ClientSize.Height;
                }
            };
            split.Panel1.Controls.Add(viewport);

            propertyGrid = new PropertyGrid { Dock = DockStyle.Fill, HelpVisible = true };
            propertyGrid.PropertyValueChanged += (s, e) => ReloadModel();
            animateBox = new CheckBox { Text = "Animate control surfaces", Dock = DockStyle.Bottom, Checked = true, Padding = new Padding(6) };
            split.Panel2.Controls.Add(propertyGrid);
            split.Panel2.Controls.Add(animateBox);

            Controls.Add(split);
            Controls.Add(menu);
        }

        private void InitializeGraphics()
        {
            device = new GraphicsDevice(viewport.Handle, viewport.ClientSize.Width, viewport.ClientSize.Height,
                enableDebugLayer: selfTest);
            renderer = new SceneRenderer(device);

            string sceneryDir = Path.Combine(repoRoot, "RCSim", "data", "scenery", "default");
            world = new SceneNode("editor_world");
            world.AddChild(new SceneNode("ground")
            {
                Mesh = PrimitiveMeshes.BuildQuad(device, Vector3.Zero, Vector3.UnitX, -Vector3.UnitZ, 500f),
                Material = new Material(Texture2D.Load(device, Path.Combine(sceneryDir, "grass1.jpg"))),
            });
            world.AddChild(new SceneNode("sky")
            {
                Mesh = PrimitiveMeshes.BuildDome(device, 4500f, 16, 16),
                Material = new Material(Texture2D.Load(device, Path.Combine(sceneryDir, "sky_sunny.jpg"))) { Kind = MaterialKind.Unlit },
            });

            camera = new Camera
            {
                AspectRatio = (float)viewport.ClientSize.Width / viewport.ClientSize.Height,
                NearPlane = 0.05f,
                FarPlane = 10000f,
            };

            // Default aircraft: the stock Xtra.
            LoadAircraft(Path.Combine(repoRoot, "RCSim", "Aircraft", "extra", "Xtra.par"));

            renderTimer = new Timer { Interval = 15 };
            renderTimer.Tick += (s, e) => RenderFrame();
            renderTimer.Start();
        }

        private void TearDownGraphics()
        {
            if (renderTimer != null) renderTimer.Stop();
            if (device != null) device.WaitIdle();
            if (renderer != null) renderer.Dispose();
            if (device != null) device.Dispose();
            device = null;
        }

        private void Viewport_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                orbitYaw += (e.X - lastMouse.X) * 0.01f;
                orbitPitch = Math.Clamp(orbitPitch + (e.Y - lastMouse.Y) * 0.01f, -1.4f, 1.4f);
                lastMouse = e.Location;
            }
        }

        private void LoadAircraft(string parPath)
        {
            if (!File.Exists(parPath))
                return;
            parameters = new AircraftParameters();
            parameters.ReadParameters(parPath);
            openFile = parPath;
            controls.AircraftParameters = parameters;
            propertyGrid.SelectedObject = parameters;
            Text = "R/C Desk Pilot - Aircraft Editor - " + Path.GetFileName(parPath);
            RebuildVisual();
        }

        private void RebuildVisual()
        {
            if (aircraftNode != null)
                aircraftNode.RemoveFromParent();
            visual = new AircraftVisual(device, renderer, parameters, Path.GetDirectoryName(openFile));
            aircraftNode = visual.Root;
            // Hover at eye height so the editor orbits around the model.
            aircraftNode.LocalTransform = Matrix4x4.CreateTranslation(0f, 1.5f, 0f);
            world.AddChild(aircraftNode);
        }

        /// <summary>Rebuilds meshes after grid edits (mesh/surface changes).</summary>
        private void ReloadModel()
        {
            try { RebuildVisual(); }
            catch (Exception e) { MessageBox.Show(this, e.Message, "Model reload failed"); }
        }

        private void OpenAircraft()
        {
            using (var dialog = new OpenFileDialog
            {
                Filter = "Aircraft parameters (*.par)|*.par",
                InitialDirectory = Path.Combine(repoRoot, "RCSim", "Aircraft"),
            })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    try { LoadAircraft(dialog.FileName); }
                    catch (Exception e) { MessageBox.Show(this, e.Message, "Open failed"); }
                }
            }
        }

        private void SaveAircraft(string path)
        {
            if (parameters == null || path == null)
                return;
            try
            {
                parameters.Save(path);
                openFile = path;
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, "Save failed"); }
        }

        private void SaveAircraftAs()
        {
            using (var dialog = new SaveFileDialog
            {
                Filter = "Aircraft parameters (*.par)|*.par",
                InitialDirectory = openFile != null ? Path.GetDirectoryName(openFile) : null,
            })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    SaveAircraft(dialog.FileName);
            }
        }

        private void RenderFrame()
        {
            if (device == null || viewport.ClientSize.Width == 0)
                return;

            // Orbit camera around the model.
            Vector3 target = new Vector3(0f, 1.5f, 0f);
            camera.Target = target;
            camera.Position = target + new Vector3(
                orbitDistance * (float)(Math.Cos(orbitPitch) * Math.Sin(orbitYaw)),
                orbitDistance * (float)Math.Sin(orbitPitch),
                orbitDistance * (float)(Math.Cos(orbitPitch) * Math.Cos(orbitYaw)));

            // Surface animation preview.
            if (visual != null && animateBox.Checked)
            {
                double t = renderedFrames / 60.0;
                controls.Elevator = Math.Sin(t * 2.1);
                controls.Ailerons = Math.Sin(t * 1.7);
                controls.Rudder = Math.Sin(t * 1.3);
                controls.Throttle = 0.5 + 0.5 * Math.Sin(t * 0.7);
                visual.UpdateSurfaces(controls, 1f / 60f);
            }

            var commandList = device.BeginFrame(new Color4(0.45f, 0.65f, 0.85f, 1f));
            renderer.Render(commandList, camera, world);
            device.EndFrame();
            renderedFrames++;

            if (selfTest && renderedFrames == 60)
                RunSelfTest();
        }

        private void RunSelfTest()
        {
            // Save round-trip: what the editor writes, the Sim's reader loads back.
            string savedPar = Path.Combine(outDir, "editortest_aircraft.par");
            parameters.Mass = 12.345;
            parameters.Save(savedPar);
            var reloaded = new AircraftParameters();
            reloaded.ReadParameters(savedPar);

            bool saveRoundTrips = Math.Abs(reloaded.Mass - 12.345) < 1e-6
                && reloaded.FixedMesh == parameters.FixedMesh
                && (reloaded.ControlSurfaces?.Count ?? 0) == (parameters.ControlSurfaces?.Count ?? 0);

            // Screenshot of the viewport for visual verification.
            byte[] pixels = FrameCapture.RenderAndReadback(device,
                list => renderer.Render(list, camera, world), new Color4(0.45f, 0.65f, 0.85f, 1f));
            SavePng(pixels, Path.Combine(outDir, "editor_aircraft.png"));

            int debugErrors = device.ReportDebugMessages();

            Console.WriteLine("frames rendered : {0}", renderedFrames);
            Console.WriteLine("save round-trip : {0}", saveRoundTrips ? "OK" : "FAILED");
            Console.WriteLine("debug errors    : {0}", debugErrors);
            bool pass = saveRoundTrips && debugErrors == 0;
            Console.WriteLine(pass ? "EDITORTEST PASS" : "EDITORTEST FAIL");
            SelfTestResult = pass ? 0 : 1;
            Close();
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
