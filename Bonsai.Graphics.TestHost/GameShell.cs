using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Bonsai.Graphics;
using Bonsai.Graphics.Audio;
using Bonsai.Graphics.Input;
using Bonsai.Graphics.Rendering;
using Bonsai.Graphics.Scene;
using Bonsai.Graphics.UI;
using Bonsai.Graphics.Win32;
using Hexa.NET.ImGui;
using RCSim.Interfaces;
using Vortice.Mathematics;

namespace Bonsai.Graphics.TestHost
{
    /// <summary>
    /// Issue 15: the chained game flow. One window hosts the whole session:
    /// main menu -> aircraft picker -> scenery picker -> flight (FlightSession)
    /// -> ESC back to the menu -> fly again or quit. In flight, a Weather
    /// panel drives the ported Wind model live.
    ///
    /// Gametest: scripted menu navigation starts a flight, the autopilot
    /// climbs out, wind is dialed in and verified to reach the flight model,
    /// then the flow returns to the menu and quits.
    /// </summary>
    internal static unsafe class GameShell
    {
        private enum Screen { MainMenu, AircraftPicker, SceneryPicker, Flying }

        public static int Run(string repoRoot, bool test, string outDir)
        {
            string aircraftRoot = Path.Combine(repoRoot, "RCSim", "Aircraft");
            string dataDir = Path.Combine(repoRoot, "RCSim", "data");

            using (var window = new Win32Window("R/C Desk Pilot", 1280, 720))
            using (var device = new GraphicsDevice(window.Handle, window.ClientWidth, window.ClientHeight, enableDebugLayer: true))
            using (var renderer = new SceneRenderer(device))
            using (var imgui = new ImGuiRenderer(device, window))
            using (var input = new InputManager(window.Handle, window, Path.Combine(outDir, "frameworkconfig.xml")))
            {
                window.Resized += (w, h) => device.Resize(w, h);
                AudioEngine.Initialize();
                try
                {
                    return RunShell(repoRoot, test, outDir, aircraftRoot, dataDir, window, device, renderer, imgui, input);
                }
                finally
                {
                    AudioEngine.Shutdown();
                }
            }
        }

        private static int RunShell(string repoRoot, bool test, string outDir, string aircraftRoot, string dataDir,
            Win32Window window, GraphicsDevice device, SceneRenderer renderer, ImGuiRenderer imgui, InputManager input)
        {
            // --- Menu backdrop: grass + sky ---
            var backdrop = new SceneNode("backdrop");
            backdrop.AddChild(new SceneNode("ground")
            {
                Mesh = PrimitiveMeshes.BuildQuad(device, Vector3.Zero, Vector3.UnitX, -Vector3.UnitZ, 500f),
                Material = new Material(Texture2D.Load(device, Path.Combine(dataDir, "scenery", "default", "grass1.jpg"))),
            });
            backdrop.AddChild(new SceneNode("sky")
            {
                Mesh = PrimitiveMeshes.BuildDome(device, 4500f, 16, 16),
                Material = new Material(Texture2D.Load(device, Path.Combine(dataDir, "scenery", "default", "sky_sunny.jpg"))) { Kind = MaterialKind.Unlit },
            });

            // --- Aircraft list with menu icons ---
            var aircraft = new List<(string name, string parPath, ImTextureID icon)>();
            foreach (string par in Directory.GetFiles(aircraftRoot, "*.par", SearchOption.AllDirectories))
            {
                string icon = Path.Combine(Path.GetDirectoryName(par), "icon.png");
                ImTextureID id = default;
                if (File.Exists(icon))
                    id = imgui.RegisterTexture(Texture2D.Load(device, icon));
                aircraft.Add((Path.GetFileNameWithoutExtension(par), par, id));
            }
            string[] sceneries = { "default", "Modelvliegclub Hasselt" };

            // --- Cameras ---
            var menuCamera = new Camera
            {
                Position = new Vector3(0, 3f, -12f),
                Target = new Vector3(0, 2f, 0),
                AspectRatio = (float)device.Width / device.Height,
            };
            var flightCamera = new Camera
            {
                Position = FlightDemo.PilotPosition,
                FieldOfView = (float)Math.PI / 4 / 1.5f,
                AspectRatio = (float)device.Width / device.Height,
                NearPlane = 0.1f,
                FarPlane = 10000f,
            };
            window.Resized += (w, h) => { menuCamera.AspectRatio = flightCamera.AspectRatio = (float)w / h; };
            AudioEngine.ListenerPosition = FlightDemo.PilotPosition;

            // --- Shell state ---
            Screen screen = Screen.MainMenu;
            string pickedAircraft = null;
            string pickedScenery = "default";
            FlightSession session = null;
            bool quit = false;
            bool escPressed = false;
            float kbThrottle = 0, kbElevator = 0, kbAileron = 0, kbRudder = 0;
            int frame = 0;

            // Test assertions
            bool sawFlying = false, sawAirborne = false, windReachedModel = false, backToMenu = false;
            byte[] shotMenu = null, shotFlight = null;

            window.KeyDown += key =>
            {
                if (key == 0x1B) escPressed = true;
                if (key == (int)InputKey.R && session != null && session.Model.Crashed) session.Reset();
            };

            void EndFlight()
            {
                if (session != null) { session.Dispose(); session = null; }
                kbThrottle = kbElevator = kbAileron = kbRudder = 0;
                screen = Screen.MainMenu;
            }

            void StartFlight()
            {
                session = new FlightSession(device, renderer, repoRoot,
                    pickedAircraft ?? Path.Combine(aircraftRoot, "extra", "Xtra.par"), pickedScenery);
                screen = Screen.Flying;
            }

            while (!quit && window.PumpMessages())
            {
                if (window.IsMinimized) { System.Threading.Thread.Sleep(50); continue; }

                // ESC: in flight -> menu; in a picker -> main menu; in menu -> quit.
                if (escPressed)
                {
                    escPressed = false;
                    if (screen == Screen.Flying) EndFlight();
                    else if (screen != Screen.MainMenu) screen = Screen.MainMenu;
                    else quit = true;
                }

                // --- Gametest script ---
                if (test)
                {
                    if (frame == 20) { pickedAircraft = Path.Combine(aircraftRoot, "extra", "Xtra.par"); pickedScenery = "default"; StartFlight(); }
                    if (screen == Screen.Flying && session != null)
                    {
                        sawFlying = true;
                        float t = (frame - 20) / 60f;
                        // Takeoff/climb only (no dive): full throttle, altitude-hold elevator.
                        var c = session.Controls;
                        c.Throttle = 1.0;
                        c.Elevator = Math.Clamp(0.02 * (40f - session.Altitude) + 0.03 * session.Model.Velocity.Z, -0.15, 0.6);
                        c.Ailerons = Math.Clamp(-0.8 * session.Model.Roll, -0.4, 0.4);
                        c.Rudder = 0;
                        if (frame == 120)
                        {
                            session.Wind.ConstantWindSpeed = 5.0;
                            session.Wind.Direction = 0.0;
                            session.Wind.GustSpeed = 2.0;
                        }
                        if (frame > 130 && session.Model.Wind.Length() > 2.5f)
                            windReachedModel = true;
                        if (session.Altitude > 10f) sawAirborne = true;
                    }
                    if (frame == 440) { EndFlight(); }
                    if (frame == 445 && screen == Screen.MainMenu && session == null) backToMenu = true;
                    if (frame == 470) quit = true;
                }

                // --- Controls (real flight) ---
                if (!test && screen == Screen.Flying && session != null)
                {
                    if (input.JoystickAvailable)
                    {
                        input.Update();
                        var c = session.Controls;
                        c.Throttle = input.GetAxisValue("throttle") / 100.0;
                        c.Elevator = input.GetAxisValue("elevator") / 100.0;
                        c.Ailerons = input.GetAxisValue("aileron") / 100.0;
                        c.Rudder = input.GetAxisValue("rudder") / 100.0;
                    }
                    else
                    {
                        FlightDemo.KeyboardControls(input, session.Controls, 1f / 60f,
                            ref kbThrottle, ref kbElevator, ref kbAileron, ref kbRudder);
                    }
                }

                // --- Step the world ---
                Camera camera = menuCamera;
                SceneNode world = backdrop;
                if (screen == Screen.Flying && session != null)
                {
                    session.Step(1f / 60f, flightCamera.Position);
                    Vector3 aircraftPosition = session.AircraftPosition;
                    flightCamera.Target = aircraftPosition;
                    float distance = Vector3.Distance(flightCamera.Position, aircraftPosition);
                    flightCamera.FieldOfView = (float)Math.PI / 4 / Math.Max(1.5f, distance / 40f);
                    camera = flightCamera;
                    world = session.World;
                }

                // --- UI ---
                imgui.NewFrame();
                if (screen == Screen.Flying && session != null)
                {
                    FlightDemo.DrawHud(session.Model, session.Controls, session.Altitude, session.Model.Speed);
                    DrawWeatherPanel(session.Wind, device);
                }
                else
                {
                    DrawMenu(ref screen, ref quit, ref pickedAircraft, ref pickedScenery, aircraft, sceneries, StartFlight);
                }

                var commandList = device.BeginFrame(new Color4(0.45f, 0.65f, 0.85f, 1f));
                renderer.Render(commandList, camera, world);
                imgui.Render(commandList);
                device.EndFrame();

                if (test && frame == 10)
                    shotMenu = CaptureMenu(device, renderer, imgui, menuCamera, backdrop, ref screen, ref quit, ref pickedAircraft, ref pickedScenery, aircraft, sceneries);
                if (test && frame == 400 && session != null)
                    shotFlight = CaptureFlight(device, renderer, imgui, flightCamera, session);
                frame++;
            }

            if (session != null) session.Dispose();
            device.WaitIdle();
            if (!test)
                return 0;

            int debugErrors = device.ReportDebugMessages();
            SavePng(shotMenu, device, Path.Combine(outDir, "game_menu.png"));
            SavePng(shotFlight, device, Path.Combine(outDir, "game_flight.png"));

            Console.WriteLine("menu -> flight  : {0}", sawFlying ? "OK" : "FAILED");
            Console.WriteLine("airborne        : {0}", sawAirborne ? "OK" : "FAILED");
            Console.WriteLine("wind -> model   : {0}", windReachedModel ? "OK" : "FAILED");
            Console.WriteLine("flight -> menu  : {0}", backToMenu ? "OK" : "FAILED");
            Console.WriteLine("debug errors    : {0}", debugErrors);
            bool pass = sawFlying && sawAirborne && windReachedModel && backToMenu && debugErrors == 0;
            Console.WriteLine(pass ? "GAMETEST PASS" : "GAMETEST FAIL");
            return pass ? 0 : 1;
        }

        private static void DrawMenu(ref Screen screen, ref bool quit, ref string pickedAircraft, ref string pickedScenery,
            List<(string name, string parPath, ImTextureID icon)> aircraft, string[] sceneries, Action startFlight)
        {
            switch (screen)
            {
                case Screen.MainMenu:
                    ImGui.SetNextWindowPos(new Vector2(40, 40));
                    ImGui.SetNextWindowSize(new Vector2(280, 240));
                    ImGui.Begin("R/C Desk Pilot", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse);
                    ImGui.TextWrapped("Welcome back to the field.");
                    ImGui.Spacing();
                    if (ImGui.Button("Fly!", new Vector2(240, 48))) screen = Screen.AircraftPicker;
                    if (ImGui.Button("Quit", new Vector2(240, 36))) quit = true;
                    ImGui.End();
                    break;

                case Screen.AircraftPicker:
                    ImGui.SetNextWindowPos(new Vector2(40, 40));
                    ImGui.SetNextWindowSize(new Vector2(460, 600));
                    ImGui.Begin("Pick an aircraft", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse);
                    ImGui.BeginChild("list", new Vector2(0, -44));
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
                        }
                    }
                    ImGui.EndChild();
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
                            startFlight();
                        }
                    }
                    if (ImGui.Button("Back", new Vector2(120, 32))) screen = Screen.MainMenu;
                    ImGui.End();
                    break;
            }
        }

        private static void DrawWeatherPanel(RCSim.Wind wind, GraphicsDevice device)
        {
            ImGui.SetNextWindowPos(new Vector2(device.Width - 296, 16), ImGuiCond.Once);
            ImGui.SetNextWindowSize(new Vector2(280, 220), ImGuiCond.Once);
            ImGui.Begin("Weather");
            float windSpeed = (float)wind.ConstantWindSpeed;
            if (ImGui.SliderFloat("Wind m/s", ref windSpeed, 0f, (float)wind.MaximumConstantWindSpeed))
                wind.ConstantWindSpeed = windSpeed;
            float direction = (float)(wind.Direction * 180.0 / Math.PI);
            if (ImGui.SliderFloat("Direction", ref direction, 0f, 360f, "%.0f deg"))
                wind.Direction = direction * Math.PI / 180.0;
            float gusts = (float)wind.GustSpeed;
            if (ImGui.SliderFloat("Gusts m/s", ref gusts, 0f, (float)wind.MaximumGustSpeed))
                wind.GustSpeed = gusts;
            float variability = (float)wind.GustVariability;
            if (ImGui.SliderFloat("Variability", ref variability, 0f, 1f))
                wind.GustVariability = variability;
            float turbulence = (float)wind.Turbulence;
            if (ImGui.SliderFloat("Turbulence", ref turbulence, 0f, 1f))
                wind.Turbulence = turbulence;
            float thermals = wind.ThermalStrengthFactor;
            if (ImGui.SliderFloat("Thermals", ref thermals, 0f, 2f))
                wind.ThermalStrengthFactor = thermals;
            ImGui.Text(string.Format("current {0:F1} m/s", wind.CurrentWind.Length()));
            ImGui.End();
        }

        private static byte[] CaptureMenu(GraphicsDevice device, SceneRenderer renderer, ImGuiRenderer imgui,
            Camera camera, SceneNode backdrop, ref Screen screen, ref bool quit, ref string pickedAircraft, ref string pickedScenery,
            List<(string name, string parPath, ImTextureID icon)> aircraft, string[] sceneries)
        {
            Screen s = screen; bool q = quit; string pa = pickedAircraft, ps = pickedScenery;
            imgui.NewFrame();
            DrawMenu(ref s, ref q, ref pa, ref ps, aircraft, sceneries, () => { });
            return FrameCapture.RenderAndReadback(device, list =>
            {
                renderer.Render(list, camera, backdrop);
                imgui.Render(list);
            }, new Color4(0.45f, 0.65f, 0.85f, 1f));
        }

        private static byte[] CaptureFlight(GraphicsDevice device, SceneRenderer renderer, ImGuiRenderer imgui,
            Camera camera, FlightSession session)
        {
            imgui.NewFrame();
            FlightDemo.DrawHud(session.Model, session.Controls, session.Altitude, session.Model.Speed);
            DrawWeatherPanel(session.Wind, device);
            return FrameCapture.RenderAndReadback(device, list =>
            {
                renderer.Render(list, camera, session.World);
                imgui.Render(list);
            }, new Color4(0.45f, 0.65f, 0.85f, 1f));
        }

        private static void SavePng(byte[] rgba, GraphicsDevice device, string path)
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
