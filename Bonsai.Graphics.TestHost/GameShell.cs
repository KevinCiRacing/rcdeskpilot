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
        private enum Screen { MainMenu, AircraftPicker, SceneryPicker, Settings, Flying }

        // Controls tab: axis-assignment listen mode.
        private static string listeningFunction;
        private static int[] listenBaseline;

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
            // --- Persistent settings (legacy frameworkconfig.xml keys) ---
            var settings = new GameSettings(Path.Combine(outDir, "frameworkconfig.xml"));
            AudioEngine.Volume = settings.GetInt("Volume", 100);

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
            RaceGame race = null;
            bool quit = false;
            bool escPressed = false;
            float kbThrottle = 0, kbElevator = 0, kbAileron = 0, kbRudder = 0;
            int frame = 0;

            // Test assertions
            bool sawFlying = false, sawAirborne = false, windReachedModel = false, backToMenu = false;
            bool sawSettings = false, sawSmoke = false, raceStarts = false;
            byte[] shotMenu = null, shotFlight = null;

            window.KeyDown += key =>
            {
                if (key == 0x1B) escPressed = true;
                if (key == (int)InputKey.R && session != null && session.Model.Crashed) session.Reset();
                if (key == (int)InputKey.S && session != null) session.SmokeEmitting = !session.SmokeEmitting;
            };

            void EndFlight()
            {
                if (race != null) { race.Dispose(); race = null; }
                if (session != null) { session.Dispose(); session = null; }
                kbThrottle = kbElevator = kbAileron = kbRudder = 0;
                screen = Screen.MainMenu;
            }

            void StartFlight()
            {
                session = new FlightSession(device, renderer, repoRoot,
                    pickedAircraft ?? Path.Combine(aircraftRoot, "extra", "Xtra.par"), pickedScenery, settings);
                ApplyWeatherSettings(settings, session.Wind);
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
                    if (frame == 6) screen = Screen.Settings;
                    if (frame == 10 && screen == Screen.Settings) { sawSettings = true; settings.SetInt("Volume", 77); }
                    if (frame == 14) screen = Screen.MainMenu;
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
                        if (frame == 200) session.SmokeEmitting = true;
                        if (frame > 260 && session.SmokeParticles > 10) sawSmoke = true;
                        if (frame == 300)
                        {
                            // Race logic: a synthetic path through gate 0 must start the clock.
                            using (var testRace = new RaceGame(device, renderer, session.World,
                                Path.Combine(repoRoot, "RCSim", "data", "scenery", "default"),
                                Path.Combine(repoRoot, "RCSim", "data")))
                            {
                                testRace.Restart(5.0);
                                testRace.Update(new Vector3(-60f, 1f, 0f), 5.0);
                                testRace.Update(new Vector3(-48f, 1f, 0f), 5.1);
                                raceStarts = testRace.Racing && testRace.CurrentGate == 1;
                            }
                        }
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
                    if (race != null)
                        race.Update(aircraftPosition, frame / 60.0);
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
                    DrawWeatherPanel(session.Wind, device, settings);
                    DrawRacePanel(ref race, session, device, renderer, repoRoot, pickedScenery, frame / 60.0);
                }
                else if (screen == Screen.Settings)
                {
                    DrawSettings(ref screen, settings, input, window, device);
                }
                else
                {
                    DrawMenu(ref screen, ref quit, ref pickedAircraft, ref pickedScenery, aircraft, sceneries, StartFlight);
                }

                var commandList = device.BeginFrame(new Color4(0.45f, 0.65f, 0.85f, 1f));
                renderer.Render(commandList, camera, world);
                imgui.Render(commandList);
                device.EndFrame();

                if (test && frame == 4)
                    shotMenu = CaptureMenu(device, renderer, imgui, menuCamera, backdrop, ref screen, ref quit, ref pickedAircraft, ref pickedScenery, aircraft, sceneries);
                if (test && frame == 400 && session != null)
                    shotFlight = CaptureFlight(device, renderer, imgui, flightCamera, session, settings);
                frame++;
            }

            if (session != null) session.Dispose();
            device.WaitIdle();
            if (!test)
                return 0;

            int debugErrors = device.ReportDebugMessages();
            SavePng(shotMenu, device, Path.Combine(outDir, "game_menu.png"));
            SavePng(shotFlight, device, Path.Combine(outDir, "game_flight.png"));

            // Settings persistence: a fresh load of the file must see the value.
            bool settingsPersisted = new GameSettings(Path.Combine(outDir, "frameworkconfig.xml")).GetInt("Volume", 0) == 77;

            Console.WriteLine("menu -> flight  : {0}", sawFlying ? "OK" : "FAILED");
            Console.WriteLine("settings screen : {0}", sawSettings ? "OK" : "FAILED");
            Console.WriteLine("settings persist: {0}", settingsPersisted ? "OK" : "FAILED");
            Console.WriteLine("airborne        : {0}", sawAirborne ? "OK" : "FAILED");
            Console.WriteLine("wind -> model   : {0}", windReachedModel ? "OK" : "FAILED");
            Console.WriteLine("smoke trail     : {0}", sawSmoke ? "OK" : "FAILED");
            Console.WriteLine("race clock      : {0}", raceStarts ? "OK" : "FAILED");
            Console.WriteLine("flight -> menu  : {0}", backToMenu ? "OK" : "FAILED");
            Console.WriteLine("debug errors    : {0}", debugErrors);
            bool pass = sawFlying && sawSettings && settingsPersisted && sawAirborne && windReachedModel && sawSmoke && raceStarts && backToMenu && debugErrors == 0;
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
                    if (ImGui.Button("Settings", new Vector2(240, 36))) screen = Screen.Settings;
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

        /// <summary>Applies the persisted weather (legacy 0-100 slider keys)
        /// to the Wind model at flight start.</summary>
        private static void ApplyWeatherSettings(GameSettings settings, RCSim.Wind wind)
        {
            wind.ConstantWindSpeed = settings.GetInt("WindSpeed", 0) * wind.MaximumConstantWindSpeed / 100.0;
            wind.Direction = (100 - settings.GetInt("WindDirection", 0)) * 2 * Math.PI / 100.0;
            wind.GustSpeed = settings.GetInt("GustStrength", 0) * wind.MaximumGustSpeed / 100.0;
            wind.GustFrequency = settings.GetInt("GustFrequency", 0) / 100.0;
            wind.GustVariability = settings.GetInt("GustVariability", 0) / 100.0;
            wind.Turbulence = settings.GetInt("Turbulence", 0) / 100.0;
            wind.DownDrafts = settings.GetInt("DownDrafts", 0) / 100.0;
            wind.ThermalStrengthFactor = settings.GetInt("ThermalStrength", 50) / 50.0f;
            wind.ThermalSizeFactor = settings.GetInt("ThermalSize", 50) / 50.0f;
        }

        private static void DrawWeatherPanel(RCSim.Wind wind, GraphicsDevice device, GameSettings settings)
        {
            ImGui.SetNextWindowPos(new Vector2(device.Width - 296, 16), ImGuiCond.Once);
            ImGui.SetNextWindowSize(new Vector2(280, 240), ImGuiCond.Once);
            ImGui.Begin("Weather");

            // Sliders apply live; the legacy 0-100 keys persist on release.
            void Persist(string key, double normalized)
            {
                if (ImGui.IsItemDeactivatedAfterEdit())
                    settings.SetInt(key, (int)Math.Round(normalized * 100));
            }

            float windSpeed = (float)wind.ConstantWindSpeed;
            if (ImGui.SliderFloat("Wind m/s", ref windSpeed, 0f, (float)wind.MaximumConstantWindSpeed))
                wind.ConstantWindSpeed = windSpeed;
            Persist("WindSpeed", wind.ConstantWindSpeed / wind.MaximumConstantWindSpeed);

            float direction = (float)(wind.Direction * 180.0 / Math.PI);
            if (ImGui.SliderFloat("Direction", ref direction, 0f, 360f, "%.0f deg"))
                wind.Direction = direction * Math.PI / 180.0;
            Persist("WindDirection", 1.0 - wind.Direction / (2 * Math.PI));

            float gusts = (float)wind.GustSpeed;
            if (ImGui.SliderFloat("Gusts m/s", ref gusts, 0f, (float)wind.MaximumGustSpeed))
                wind.GustSpeed = gusts;
            Persist("GustStrength", wind.GustSpeed / wind.MaximumGustSpeed);

            float variability = (float)wind.GustVariability;
            if (ImGui.SliderFloat("Variability", ref variability, 0f, 1f))
                wind.GustVariability = variability;
            Persist("GustVariability", wind.GustVariability);

            float turbulence = (float)wind.Turbulence;
            if (ImGui.SliderFloat("Turbulence", ref turbulence, 0f, 1f))
                wind.Turbulence = turbulence;
            Persist("Turbulence", wind.Turbulence);

            float downdrafts = (float)wind.DownDrafts;
            if (ImGui.SliderFloat("Downdrafts", ref downdrafts, 0f, 1f))
                wind.DownDrafts = downdrafts;
            Persist("DownDrafts", wind.DownDrafts);

            float thermals = wind.ThermalStrengthFactor;
            if (ImGui.SliderFloat("Thermals", ref thermals, 0f, 2f))
                wind.ThermalStrengthFactor = thermals;
            Persist("ThermalStrength", wind.ThermalStrengthFactor / 2.0); // key = factor * 50

            ImGui.Text(string.Format("current {0:F1} m/s", wind.CurrentWind.Length()));
            ImGui.End();
        }

        private static void DrawRacePanel(ref RaceGame race, FlightSession session, GraphicsDevice device,
            SceneRenderer renderer, string repoRoot, string sceneryName, double time)
        {
            ImGui.SetNextWindowPos(new Vector2(16, device.Height - 96), ImGuiCond.Once);
            ImGui.SetNextWindowSize(new Vector2(230, 80), ImGuiCond.Once);
            ImGui.Begin("Race");
            if (race == null)
            {
                if (ImGui.Button("Start race", new Vector2(160, 32)))
                {
                    string sceneryDir = Path.Combine(repoRoot, "RCSim", "data", "scenery", sceneryName);
                    if (File.Exists(Path.Combine(sceneryDir, "terrain.def")))
                    {
                        race = new RaceGame(device, renderer, session.World, sceneryDir,
                            Path.Combine(repoRoot, "RCSim", "data"));
                        race.Restart(time);
                    }
                }
            }
            else if (ImGui.Button("Stop race", new Vector2(160, 32)))
            {
                race.Dispose();
                race = null;
            }
            ImGui.End();

            // Centered game text (legacy CenterHud.ShowGameText).
            string status = race != null ? race.StatusText : null;
            if (!string.IsNullOrEmpty(status))
            {
                var io = ImGui.GetIO();
                Vector2 size = ImGui.CalcTextSize(status);
                ImGui.SetNextWindowPos(new Vector2(io.DisplaySize.X / 2 - size.X / 2 - 12, 60));
                ImGui.Begin("##gametext", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoInputs);
                ImGui.Text(status);
                ImGui.End();
            }
        }

        private static readonly string[] DetailLevels = { "Low", "Medium", "High" };
        private static readonly (string label, string function)[] ControlFunctions =
        {
            ("Throttle", "throttle"), ("Elevator", "elevator"), ("Aileron", "aileron"), ("Rudder", "rudder"),
        };

        private static void DrawSettings(ref Screen screen, GameSettings settings, InputManager input,
            Win32Window window, GraphicsDevice device)
        {
            ImGui.SetNextWindowPos(new Vector2(40, 40));
            ImGui.SetNextWindowSize(new Vector2(560, 520));
            ImGui.Begin("Settings", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse);
            if (ImGui.BeginTabBar("tabs"))
            {
                if (ImGui.BeginTabItem("Simulation"))
                {
                    bool variometer = settings.GetBool("EnableVariometer", false);
                    if (ImGui.Checkbox("Variometer sound", ref variometer)) settings.SetBool("EnableVariometer", variometer);
                    bool compass = settings.GetBool("CompassVisible", false);
                    if (ImGui.Checkbox("Show compass", ref compass)) settings.SetBool("CompassVisible", compass);
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Graphics"))
                {
                    bool fullscreen = window.IsFullscreen;
                    if (ImGui.Checkbox("Fullscreen (F11)", ref fullscreen))
                    {
                        window.SetFullscreen(fullscreen);
                        settings.SetBool("FullScreen", fullscreen);
                    }
                    DetailCombo(settings, "Scenery detail", "SceneryDetail");
                    DetailCombo(settings, "Water detail", "WaterDetail");
                    DetailCombo(settings, "Water ripples", "WaterRipplesDetail");
                    DetailCombo(settings, "Reflections", "ReflectionDetail");
                    DetailCombo(settings, "Smoke detail", "SmokeDetail");
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Sound"))
                {
                    int volume = settings.GetInt("Volume", 100);
                    if (ImGui.SliderInt("Master volume", ref volume, 0, 100))
                        AudioEngine.Volume = volume;
                    if (ImGui.IsItemDeactivatedAfterEdit())
                        settings.SetInt("Volume", volume);
                    bool windSound = settings.GetBool("EnableWindSound", true);
                    if (ImGui.Checkbox("Wind sound", ref windSound)) settings.SetBool("EnableWindSound", windSound);
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Controls"))
                {
                    DrawControlsTab(input);
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
            ImGui.Spacing();
            if (ImGui.Button("Back", new Vector2(120, 32)))
            {
                listeningFunction = null;
                listenBaseline = null;
                screen = Screen.MainMenu;
            }
            ImGui.End();
        }

        private static void DetailCombo(GameSettings settings, string label, string key)
        {
            int value = Math.Clamp(settings.GetInt(key, 2), 0, DetailLevels.Length - 1);
            if (ImGui.Combo(label, ref value, DetailLevels, DetailLevels.Length))
                settings.SetInt(key, value);
        }

        private static void DrawControlsTab(InputManager input)
        {
            if (!input.JoystickAvailable)
            {
                ImGui.TextWrapped("No transmitter/joystick detected. Keyboard flight is active " +
                    "(arrows/numpad; PageUp/PageDown throttle).");
                return;
            }
            ImGui.Text(string.Format("Device: {0}", input.JoystickName));
            ImGui.Spacing();

            input.Update();
            IReadOnlyList<KeyValuePair<string, int>> raw = input.GetRawAxes();

            foreach (var (label, function) in ControlFunctions)
            {
                bool inverted;
                JoystickAxis axis = input.Settings.GetAxis(function, out inverted);

                ImGui.PushID(function);
                int axisIndex = (int)axis;
                string[] names = Enum.GetNames(typeof(JoystickAxis));
                ImGui.SetNextItemWidth(120);
                if (ImGui.Combo(label, ref axisIndex, names, names.Length))
                    input.Settings.SetAxis(function, (JoystickAxis)axisIndex, inverted);
                ImGui.SameLine();
                if (ImGui.Checkbox("Inv", ref inverted))
                    input.Settings.SetAxis(function, (JoystickAxis)axisIndex, inverted);
                ImGui.SameLine();
                bool listening = listeningFunction == function;
                if (ImGui.Button(listening ? "Move stick..." : "Assign", new Vector2(100, 0)))
                {
                    listeningFunction = function;
                    listenBaseline = null;
                }
                ImGui.SameLine();
                float value = input.GetAxisValue(function) / 100f; // -1..1
                ImGui.ProgressBar(value * 0.5f + 0.5f, new Vector2(120, 16), string.Format("{0:F0}", value * 100));
                ImGui.PopID();
            }

            // Listen mode: assign the axis that moves the most from its baseline.
            if (listeningFunction != null)
            {
                if (listenBaseline == null)
                {
                    listenBaseline = new int[raw.Count];
                    for (int i = 0; i < raw.Count; i++) listenBaseline[i] = raw[i].Value;
                }
                else
                {
                    int bestAxis = -1, bestDelta = 0;
                    for (int i = 0; i < raw.Count && i < listenBaseline.Length; i++)
                    {
                        int delta = Math.Abs(raw[i].Value - listenBaseline[i]);
                        if (delta > bestDelta) { bestDelta = delta; bestAxis = i; }
                    }
                    if (bestAxis >= 0 && bestDelta > 50) // half deflection
                    {
                        bool wasInverted;
                        input.Settings.GetAxis(listeningFunction, out wasInverted);
                        input.Settings.SetAxis(listeningFunction, (JoystickAxis)bestAxis, wasInverted);
                        listeningFunction = null;
                        listenBaseline = null;
                    }
                }
                ImGui.TextWrapped("Move the stick you want to assign; ESC cancels.");
            }
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
            Camera camera, FlightSession session, GameSettings settings)
        {
            imgui.NewFrame();
            FlightDemo.DrawHud(session.Model, session.Controls, session.Altitude, session.Model.Speed);
            DrawWeatherPanel(session.Wind, device, settings);
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
