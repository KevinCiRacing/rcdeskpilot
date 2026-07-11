using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Bonsai.Graphics;
using Bonsai.Graphics.Rendering;
using Bonsai.Graphics.Scene;
using Bonsai.Graphics.UI;
using Bonsai.Graphics.Win32;
using Hexa.NET.ImGui;
using Vortice.Mathematics;

namespace Bonsai.Graphics.TestHost
{
    /// <summary>
    /// Issue 10 demo/selftest: game-menu flow in ImGui - main menu, aircraft
    /// picker (with icons), scenery picker, start flight (3D backdrop), back,
    /// quit. Menutest drives a REAL mouse click through the Win32 message
    /// path and verifies the menu state machine and rendered pixels.
    /// </summary>
    internal static unsafe class MenuDemo
    {
        private enum Screen { MainMenu, AircraftPicker, SceneryPicker, Flying }

        private static Vector2 flyButtonCenter;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public static int Run(string repoRoot, bool test, string outDir)
        {
            string aircraftRoot = Path.Combine(repoRoot, "RCSim", "Aircraft");
            string dataDir = Path.Combine(repoRoot, "RCSim", "data");

            using (var window = new Win32Window("R/C Desk Pilot", 1280, 720))
            using (var device = new GraphicsDevice(window.Handle, window.ClientWidth, window.ClientHeight, enableDebugLayer: true))
            using (var renderer = new SceneRenderer(device))
            using (var imgui = new ImGuiRenderer(device, window))
            {
                window.Resized += (w, h) => device.Resize(w, h);

                // 3D backdrop: sky + ground (menu) or picked scenery label world.
                var root = new SceneNode("backdrop");
                root.AddChild(new SceneNode("ground")
                {
                    Mesh = PrimitiveMeshes.BuildQuad(device, Vector3.Zero, Vector3.UnitX, -Vector3.UnitZ, 500f),
                    Material = new Material(Texture2D.Load(device, Path.Combine(dataDir, "scenery", "default", "grass1.jpg"))),
                });
                root.AddChild(new SceneNode("sky")
                {
                    Mesh = PrimitiveMeshes.BuildDome(device, 4500f, 16, 16),
                    Material = new Material(Texture2D.Load(device, Path.Combine(dataDir, "scenery", "default", "sky_sunny.jpg"))) { Kind = MaterialKind.Unlit },
                });
                var camera = new Camera
                {
                    Position = new Vector3(0, 3f, -12f),
                    Target = new Vector3(0, 2f, 0),
                    AspectRatio = (float)device.Width / device.Height,
                };
                window.Resized += (w, h) => camera.AspectRatio = (float)w / h;

                // Aircraft list with icons.
                var aircraft = new List<(string name, string parPath, ImTextureID icon)>();
                var iconTextures = new List<Texture2D>();
                foreach (string par in Directory.GetFiles(aircraftRoot, "*.par", SearchOption.AllDirectories))
                {
                    string icon = Path.Combine(Path.GetDirectoryName(par), "icon.png");
                    ImTextureID id = default;
                    if (File.Exists(icon))
                    {
                        var texture = Texture2D.Load(device, icon);
                        iconTextures.Add(texture);
                        id = imgui.RegisterTexture(texture);
                    }
                    aircraft.Add((Path.GetFileNameWithoutExtension(par), par, id));
                }
                string[] sceneries = { "default", "Modelvliegclub Hasselt" };

                Screen screen = Screen.MainMenu;
                string pickedAircraft = null, pickedScenery = "default";
                bool quit = false;
                var visited = new HashSet<Screen> { Screen.MainMenu };
                int frame = 0;
                byte[] shotMenu = null, shotPicker = null, shotFlying = null;
                bool clickWorked = false;

                while (!quit && window.PumpMessages())
                {
                    if (window.IsMinimized) { System.Threading.Thread.Sleep(50); continue; }

                    // Selftest script: a REAL click through WndProc on the Fly
                    // button (fixed window position), then programmatic flow.
                    if (test)
                    {
                        if (frame == 20)
                        {
                            Screen before = screen;
                            Click(window.Handle, (int)flyButtonCenter.X, (int)flyButtonCenter.Y);
                            // processed next frame
                        }
                        if (frame == 22) clickWorked = screen == Screen.AircraftPicker;
                        if (frame == 30) shotPicker = null; // captured below after draw
                        if (frame == 40) { pickedAircraft = aircraft[0].parPath; screen = Screen.SceneryPicker; visited.Add(screen); }
                        if (frame == 50) { screen = Screen.Flying; visited.Add(screen); }
                        if (frame == 70) { screen = Screen.MainMenu; }
                        if (frame == 80) quit = true;
                    }

                    imgui.NewFrame();
                    DrawUi(ref screen, ref quit, ref pickedAircraft, ref pickedScenery, aircraft, sceneries, visited);

                    var commandList = device.BeginFrame(new Color4(0.45f, 0.65f, 0.85f, 1f));
                    renderer.Render(commandList, camera, root);
                    imgui.Render(commandList);
                    device.EndFrame();

                    if (test && frame == 15) shotMenu = Capture(device, renderer, imgui, camera, root, ref screen, ref quit, ref pickedAircraft, ref pickedScenery, aircraft, sceneries, visited);
                    if (test && frame == 35) shotPicker = Capture(device, renderer, imgui, camera, root, ref screen, ref quit, ref pickedAircraft, ref pickedScenery, aircraft, sceneries, visited);
                    if (test && frame == 60) shotFlying = Capture(device, renderer, imgui, camera, root, ref screen, ref quit, ref pickedAircraft, ref pickedScenery, aircraft, sceneries, visited);
                    frame++;
                }

                device.WaitIdle();
                if (!test)
                    return 0;

                int debugErrors = device.ReportDebugMessages();
                Save(shotMenu, device, Path.Combine(outDir, "menu_main.png"));
                Save(shotPicker, device, Path.Combine(outDir, "menu_picker.png"));
                Save(shotFlying, device, Path.Combine(outDir, "menu_flying.png"));

                bool allScreens = visited.Count == 4;
                bool uiPixels = Diff(shotMenu, shotPicker) > 3000 && Diff(shotPicker, shotFlying) > 3000;
                Console.WriteLine("mouse click     : {0}", clickWorked ? "OK" : "FAILED");
                Console.WriteLine("flow complete   : {0} ({1}/4 screens)", allScreens ? "OK" : "FAILED", visited.Count);
                Console.WriteLine("aircraft picked : {0}", pickedAircraft != null ? Path.GetFileNameWithoutExtension(pickedAircraft) : "none");
                Console.WriteLine("ui pixels       : {0}", uiPixels ? "OK" : "STATIC");
                Console.WriteLine("debug errors    : {0}", debugErrors);
                bool pass = clickWorked && allScreens && uiPixels && pickedAircraft != null && debugErrors == 0;
                Console.WriteLine(pass ? "MENUTEST PASS" : "MENUTEST FAIL");
                return pass ? 0 : 1;
            }
        }

        private static void DrawUi(ref Screen screen, ref bool quit, ref string pickedAircraft, ref string pickedScenery,
            List<(string name, string parPath, ImTextureID icon)> aircraft, string[] sceneries, HashSet<Screen> visited)
        {
            switch (screen)
            {
                case Screen.MainMenu:
                    ImGui.SetNextWindowPos(new Vector2(40, 40));
                    ImGui.SetNextWindowSize(new Vector2(280, 320));
                    ImGui.Begin("R/C Desk Pilot", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse);
                    ImGui.TextWrapped("Welcome back to the field.");
                    ImGui.Spacing();
                    if (ImGui.Button("Fly!", new Vector2(240, 48))) { screen = Screen.AircraftPicker; visited.Add(screen); }
                    flyButtonCenter = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * 0.5f;
                    if (ImGui.Button("Scenery", new Vector2(240, 36))) { screen = Screen.SceneryPicker; visited.Add(screen); }
                    if (ImGui.Button("Quit", new Vector2(240, 36))) quit = true;
                    ImGui.End();
                    break;

                case Screen.AircraftPicker:
                    ImGui.SetNextWindowPos(new Vector2(40, 40));
                    ImGui.SetNextWindowSize(new Vector2(460, 560));
                    ImGui.Begin("Pick an aircraft", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse);
                    foreach (var entry in aircraft)
                    {
                        if (!entry.icon.IsNull)
                        {
                            ImGui.Image(new ImTextureRef(default, entry.icon), new Vector2(48, 48));
                            ImGui.SameLine();
                        }
                        if (ImGui.Button(entry.name, new Vector2(340, 48)))
                        {
                            pickedAircraft = entry.parPath;
                            screen = Screen.SceneryPicker;
                            visited.Add(screen);
                        }
                    }
                    if (ImGui.Button("Back", new Vector2(120, 32))) screen = Screen.MainMenu;
                    ImGui.End();
                    break;

                case Screen.SceneryPicker:
                    ImGui.SetNextWindowPos(new Vector2(40, 40));
                    ImGui.SetNextWindowSize(new Vector2(360, 240));
                    ImGui.Begin("Pick a scenery", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse);
                    foreach (string scenery in sceneries)
                    {
                        if (ImGui.Button(scenery, new Vector2(300, 40)))
                        {
                            pickedScenery = scenery;
                            screen = Screen.Flying;
                            visited.Add(screen);
                        }
                    }
                    if (ImGui.Button("Back", new Vector2(120, 32))) screen = Screen.MainMenu;
                    ImGui.End();
                    break;

                case Screen.Flying:
                    ImGui.SetNextWindowPos(new Vector2(40, 40));
                    ImGui.SetNextWindowSize(new Vector2(380, 120));
                    ImGui.Begin("Flight", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse);
                    ImGui.Text(string.Format("Flying {0} at {1}",
                        pickedAircraft != null ? System.IO.Path.GetFileNameWithoutExtension(pickedAircraft) : "?", pickedScenery));
                    if (ImGui.Button("Back to menu", new Vector2(160, 36))) screen = Screen.MainMenu;
                    ImGui.End();
                    break;
            }
        }

        private static void Click(IntPtr hwnd, int x, int y)
        {
            IntPtr pos = (IntPtr)((y << 16) | (x & 0xFFFF));
            SendMessageW(hwnd, 0x0200, IntPtr.Zero, pos);      // WM_MOUSEMOVE
            SendMessageW(hwnd, 0x0201, (IntPtr)1, pos);        // WM_LBUTTONDOWN
            SendMessageW(hwnd, 0x0202, IntPtr.Zero, pos);      // WM_LBUTTONUP
        }

        private static byte[] Capture(GraphicsDevice device, SceneRenderer renderer, ImGuiRenderer imgui, Camera camera, SceneNode root,
            ref Screen screen, ref bool quit, ref string pickedAircraft, ref string pickedScenery,
            List<(string name, string parPath, ImTextureID icon)> aircraft, string[] sceneries, HashSet<Screen> visited)
        {
            // Render one extra captured frame with the same UI state.
            Screen s = screen; bool q = quit; string pa = pickedAircraft, ps = pickedScenery;
            imgui.NewFrame();
            DrawUi(ref s, ref q, ref pa, ref ps, aircraft, sceneries, visited);
            byte[] pixels = FrameCapture.RenderAndReadback(device, list =>
            {
                renderer.Render(list, camera, root);
                imgui.Render(list);
            }, new Color4(0.45f, 0.65f, 0.85f, 1f));
            return pixels;
        }

        private static int Diff(byte[] a, byte[] b)
        {
            if (a == null || b == null) return 0;
            int diff = 0;
            for (int i = 0; i < Math.Min(a.Length, b.Length); i += 4)
                if (Math.Abs(a[i] - b[i]) > 16 || Math.Abs(a[i + 1] - b[i + 1]) > 16 || Math.Abs(a[i + 2] - b[i + 2]) > 16)
                    diff++;
            return diff;
        }

        private static void Save(byte[] rgba, GraphicsDevice device, string path)
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
    }
}
