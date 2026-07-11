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
using Bonsai.Objects.Terrain;
using Hexa.NET.ImGui;
using RCSim;
using RCSim.DataClasses;
using RCSim.Interfaces;
using Vortice.Mathematics;

namespace Bonsai.Graphics.TestHost
{
    /// <summary>
    /// Issue 13 - FIRST FLIGHT: the integrating tracer bullet. Menu (aircraft
    /// picker) -> default scenery world -> flight model driving the aircraft
    /// scene nodes (deflecting surfaces, spinning prop) -> transmitter/
    /// keyboard control -> pilot camera -> ImGui HUD -> engine audio ->
    /// ground contact, crash and reset.
    ///
    /// Flytest: a scripted autopilot takes off, climbs, is verified airborne,
    /// then dives in; crash + reset are asserted, screenshots captured.
    /// </summary>
    internal static class FlightDemo
    {
        private const float PhysicsStep = 0.002f;
        private static readonly Vector3 PilotPosition = new Vector3(0.1f, 1.7f, -15.0f);

        public static int Run(string repoRoot, bool test, string outDir, string aircraftPar)
        {
            string dataDir = Path.Combine(repoRoot, "RCSim", "data");
            string sceneryDir = Path.Combine(dataDir, "scenery", "default");
            aircraftPar = aircraftPar ?? Path.Combine(repoRoot, "RCSim", "Aircraft", "extra", "Xtra.par");

            using (var window = new Win32Window("R/C Desk Pilot - First Flight", 1280, 720))
            using (var device = new GraphicsDevice(window.Handle, window.ClientWidth, window.ClientHeight, enableDebugLayer: true))
            using (var renderer = new SceneRenderer(device))
            using (var imgui = new ImGuiRenderer(device, window))
            using (var input = new InputManager(window.Handle, window,
                Path.Combine(outDir, "frameworkconfig.xml")))
            {
                window.Resized += (w, h) => device.Resize(w, h);
                AudioEngine.Initialize();
                try
                {
                    return RunFlight(repoRoot, test, outDir, aircraftPar, window, device, renderer, imgui, input, sceneryDir, dataDir);
                }
                finally
                {
                    AudioEngine.Shutdown();
                }
            }
        }

        private static int RunFlight(string repoRoot, bool test, string outDir, string aircraftPar,
            Win32Window window, GraphicsDevice device, SceneRenderer renderer, ImGuiRenderer imgui,
            InputManager input, string sceneryDir, string dataDir)
        {
            // --- World: the default scenery (terrain, sky, trees, objects) ---
            var world = new SceneNode("world");
            var billboards = new List<SceneNode>();
            SceneNode windmillBlades;
            Heightmap heightmap = SceneryDemo.BuildDefaultScenery(device, renderer, world, sceneryDir, dataDir, billboards, out windmillBlades);

            // --- Aircraft: parameters -> physics + visual ---
            var parameters = new AircraftParameters();
            parameters.ReadParameters(aircraftPar);
            IFlightModel model = parameters.Version == 2 ? new FlightModelWind2() : (IFlightModel)new FlightModelWind();
            model.AircraftParameters = parameters;
            model.UpdateConstants();
            model.Heightmap = heightmap;
            model.Water = new List<Water>();
            model.Wind = Vector3.Zero;
            ResetFlight(model);

            var visual = new AircraftVisual(device, renderer, parameters, Path.GetDirectoryName(aircraftPar));
            world.AddChild(visual.Root);
            var controls = (IAirplaneControl)model;

            // --- Engine audio: 3D emitter at the aircraft, pitch by throttle ---
            Sound3D engineSound = null;
            string engineWav = parameters.EngineSound != null
                ? Path.Combine(Path.GetDirectoryName(aircraftPar), Path.GetFileName(parameters.EngineSound)) : null;
            if (engineWav != null && File.Exists(engineWav))
            {
                engineSound = new Sound3D(engineWav);
                engineSound.Play(true);
            }
            AudioEngine.ListenerPosition = PilotPosition;

            // --- Pilot camera (legacy ObserverCamera: fixed pilot, zoom 1.5) ---
            var camera = new Camera
            {
                Position = PilotPosition,
                FieldOfView = (float)Math.PI / 4 / 1.5f,
                AspectRatio = (float)device.Width / device.Height,
                NearPlane = 0.1f,
                FarPlane = 10000f,
            };
            window.Resized += (w, h) => camera.AspectRatio = (float)w / h;

            // --- Keyboard flight state (legacy Player accumulate/decay port) ---
            float kbThrottle = 0, kbElevator = 0, kbAileron = 0, kbRudder = 0;

            // --- Loop state ---
            int frame = 0;
            float physicsAccumulator = 0;
            bool crashedShown = false;
            var shots = new List<(string name, byte[] pixels)>();
            float maxAltitude = 0;
            bool sawAirborne = false, sawCrash = false, sawReset = false;
            bool surfacesMove = false;
            Matrix4x4 surfaceAtRest = default;
            var frameTimer = System.Diagnostics.Stopwatch.StartNew();
            double renderSeconds = 0;
            int renderedFrames = 0;

            window.KeyDown += key =>
            {
                if (key == 0x1B) window.Dispose();
                if (key == (int)InputKey.R && !test) { ResetFlight(model); }
            };

            while (window.PumpMessages())
            {
                if (window.IsMinimized) { System.Threading.Thread.Sleep(50); continue; }
                float t = frame / 60f;

                // --- Controls: scripted (test) > transmitter > keyboard ---
                if (test)
                {
                    ScriptedControls(controls, model, t, ref sawCrash);
                }
                else if (input.JoystickAvailable)
                {
                    input.Update();
                    controls.Throttle = input.GetAxisValue("throttle") / 100.0;
                    controls.Elevator = input.GetAxisValue("elevator") / 100.0;
                    controls.Ailerons = input.GetAxisValue("aileron") / 100.0;
                    controls.Rudder = input.GetAxisValue("rudder") / 100.0;
                }
                else
                {
                    KeyboardControls(input, controls, 1f / 60f, ref kbThrottle, ref kbElevator, ref kbAileron, ref kbRudder);
                }

                // --- Fixed-step physics (characterization-style stepping) ---
                physicsAccumulator += 1f / 60f;
                while (physicsAccumulator >= PhysicsStep)
                {
                    model.UpdateControls(PhysicsStep);
                    if (parameters.Version == 2)
                        ((FlightModelWind2)model).MoveScene(PhysicsStep);
                    else
                        ((FlightModelWind)model).MoveScene(PhysicsStep);
                    physicsAccumulator -= PhysicsStep;
                }

                // --- Flight model -> scene nodes ---
                visual.UpdateTransform(model);
                visual.UpdateSurfaces(controls, 1f / 60f);
                if (windmillBlades != null)
                {
                    Vector3 pivot = windmillBlades.Mesh.BoundsCenter;
                    windmillBlades.LocalTransform = Matrix4x4.CreateTranslation(-pivot)
                        * Matrix4x4.CreateRotationZ(t * 2f) * Matrix4x4.CreateTranslation(pivot);
                }

                Vector3 aircraftPosition = new Vector3(-model.Y, -model.Z, -model.X);
                float altitude = -model.Z;
                maxAltitude = Math.Max(maxAltitude, altitude);
                if (altitude > 10f) sawAirborne = true;
                if (model.Crashed && sawAirborne) sawCrash = true;

                // --- Camera: pilot looks at the aircraft ---
                camera.Target = aircraftPosition;
                float distance = Vector3.Distance(camera.Position, aircraftPosition);
                camera.FieldOfView = (float)Math.PI / 4 / Math.Max(1.5f, distance / 40f); // legacy-style zoom

                // --- Billboards face the pilot ---
                foreach (SceneNode tree in billboards)
                {
                    Vector3 position = tree.LocalTransform.Translation;
                    float yaw = (float)Math.Atan2(camera.Position.X - position.X, camera.Position.Z - position.Z);
                    tree.LocalTransform = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(position);
                }

                // --- Audio follows the aircraft ---
                if (engineSound != null)
                {
                    engineSound.Location = aircraftPosition;
                    float hz = parameters.EngineMinFrequency
                        + (float)controls.Throttle * (parameters.EngineMaxFrequency - parameters.EngineMinFrequency);
                    engineSound.FrequencyRatio = Math.Max(0.1f, hz / 22050f);
                }

                // --- HUD ---
                imgui.NewFrame();
                DrawHud(model, controls, altitude, model.Speed);
                if (model.Crashed && !crashedShown && !test)
                    crashedShown = true;

                // --- Test script hooks ---
                long before = frameTimer.ElapsedTicks;
                var commandList = device.BeginFrame(new Color4(0.45f, 0.65f, 0.85f, 1f));
                renderer.Render(commandList, camera, world);
                imgui.Render(commandList);
                device.EndFrame();
                renderSeconds += (frameTimer.ElapsedTicks - before) / (double)System.Diagnostics.Stopwatch.Frequency;
                renderedFrames++;

                if (test)
                {
                    if (frame == 10) surfaceAtRest = visual.FirstSurfaceTransform;
                    if (frame == 320 && !surfaceAtRest.Equals(visual.FirstSurfaceTransform)) surfacesMove = true;
                    if (frame == 60 || frame == 420 || frame == 800)
                        shots.Add(("flight_" + frame, CaptureFrame(device, renderer, imgui, camera, world, model, controls, altitude)));
                    if (sawCrash && !sawReset && frame > 500)
                    {
                        ResetFlight(model);
                        if (!model.Crashed && -model.Z < 1f)
                            sawReset = true;
                    }
                    if (frame == 900)
                        break;
                }
                frame++;
            }

            device.WaitIdle();
            if (!test)
                return 0;

            int debugErrors = device.ReportDebugMessages();
            double fps = renderedFrames / Math.Max(renderSeconds, 1e-6);
            foreach (var shot in shots)
                SavePng(shot.pixels, device, Path.Combine(outDir, shot.name + ".png"));

            Console.WriteLine("airborne        : {0} (max alt {1:F1} m)", sawAirborne ? "OK" : "FAILED", maxAltitude);
            Console.WriteLine("surfaces move   : {0}", surfacesMove ? "OK" : "STATIC");
            Console.WriteLine("crash detected  : {0}", sawCrash ? "OK" : "MISSING");
            Console.WriteLine("reset works     : {0}", sawReset ? "OK" : "FAILED");
            Console.WriteLine("render fps      : {0:F0}", fps);
            Console.WriteLine("debug errors    : {0}", debugErrors);
            bool pass = sawAirborne && surfacesMove && sawCrash && sawReset && fps >= 30 && debugErrors == 0;
            Console.WriteLine(pass ? "FLIGHTTEST PASS" : "FLIGHTTEST FAIL");
            return pass ? 0 : 1;
        }

        private static void ResetFlight(IFlightModel model)
        {
            model.Reset();
            // Default start position (0, 0.05, 0) world -> NED (0, 0, -0.05).
            model.X = 0; model.Y = 0; model.Z = -0.05f;
            model.Crashed = false;
        }

        /// <summary>Scripted autopilot for the selftest: take off, climb, then dive in.</summary>
        private static void ScriptedControls(IAirplaneControl c, IFlightModel m, float t, ref bool sawCrash)
        {
            float altitude = -m.Z;
            float climb = -m.Velocity.Z;
            double leveler = Clamp(-0.8 * m.Roll, -0.4, 0.4);
            if (t < 5f)
            { // takeoff roll + climb out
                c.Throttle = 1.0;
                c.Elevator = Clamp(0.02 * (40f - altitude) - 0.03 * climb, -0.15, 0.6);
                c.Ailerons = leveler; c.Rudder = 0;
            }
            else if (!sawCrash && altitude > 2f)
            { // dive it in
                c.Throttle = 0.4; c.Elevator = -0.6; c.Ailerons = leveler; c.Rudder = 0;
            }
            else
            { // after crash: idle
                c.Throttle = 0; c.Elevator = 0; c.Ailerons = 0; c.Rudder = 0;
            }
        }

        /// <summary>Legacy Player keyboard flight (accumulate/decay), via InputKey.</summary>
        private static void KeyboardControls(InputManager input, IAirplaneControl c, float dt,
            ref float kbThrottle, ref float kbElevator, ref float kbAileron, ref float kbRudder)
        {
            if (input.IsKeyDown(InputKey.NumPad9) || input.IsKeyDown(InputKey.PageUp))
                kbThrottle = Math.Min(100, kbThrottle + 75 * dt);
            else if (input.IsKeyDown(InputKey.NumPad7) || input.IsKeyDown(InputKey.PageDown))
                kbThrottle = Math.Max(-100, kbThrottle - 75 * dt);

            if (input.IsKeyDown(InputKey.NumPad3) || input.IsKeyDown(InputKey.End))
                kbRudder = Math.Min(100, kbRudder + 200 * dt);
            else if (input.IsKeyDown(InputKey.NumPad1) || input.IsKeyDown(InputKey.Home))
                kbRudder = Math.Max(-100, kbRudder - 200 * dt);
            else if (Math.Abs(kbRudder) < 5) kbRudder = 0;
            else kbRudder += kbRudder > 0 ? -350 * dt : 350 * dt;

            if (input.IsKeyDown(InputKey.NumPad2) || input.IsKeyDown(InputKey.DownArrow))
                kbElevator = Math.Min(100, kbElevator + 300 * dt);
            else if (input.IsKeyDown(InputKey.NumPad8) || input.IsKeyDown(InputKey.UpArrow))
                kbElevator = Math.Max(-100, kbElevator - 300 * dt);
            else if (Math.Abs(kbElevator) < 5) kbElevator = 0;
            else kbElevator += kbElevator > 0 ? -350 * dt : 350 * dt;

            if (input.IsKeyDown(InputKey.NumPad4) || input.IsKeyDown(InputKey.LeftArrow))
                kbAileron = Math.Max(-100, kbAileron - 75 * dt);
            else if (input.IsKeyDown(InputKey.NumPad6) || input.IsKeyDown(InputKey.RightArrow))
                kbAileron = Math.Min(100, kbAileron + 75 * dt);
            else if (Math.Abs(kbAileron) < 5) kbAileron = 0;
            else kbAileron += kbAileron > 0 ? -450 * dt : 450 * dt;

            c.Throttle = kbThrottle / 100.0;
            c.Rudder = kbRudder / 100.0;
            c.Elevator = kbElevator / 100.0;
            c.Ailerons = kbAileron / 100.0;
        }

        private static void DrawHud(IFlightModel model, IAirplaneControl controls, float altitude, double speed)
        {
            ImGui.SetNextWindowPos(new Vector2(16, 16));
            ImGui.SetNextWindowSize(new Vector2(230, 130));
            ImGui.Begin("Flight", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse);
            ImGui.Text(string.Format("Throttle {0,4:F0} %", controls.Throttle * 100));
            ImGui.Text(string.Format("Speed    {0,4:F1} m/s", speed));
            ImGui.Text(string.Format("Altitude {0,4:F1} m", altitude));
            ImGui.Text(model.TouchedDown ? "on ground" : "airborne");
            ImGui.End();

            if (model.Crashed)
            {
                var io = ImGui.GetIO();
                ImGui.SetNextWindowPos(new Vector2(io.DisplaySize.X / 2 - 130, io.DisplaySize.Y / 2 - 40));
                ImGui.SetNextWindowSize(new Vector2(260, 80));
                ImGui.Begin("Crashed!", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse);
                ImGui.Text("You crashed!");
                ImGui.Text("Press R to reset.");
                ImGui.End();
            }
        }

        private static byte[] CaptureFrame(GraphicsDevice device, SceneRenderer renderer, ImGuiRenderer imgui,
            Camera camera, SceneNode world, IFlightModel model, IAirplaneControl controls, float altitude)
        {
            imgui.NewFrame();
            DrawHud(model, controls, altitude, model.Speed);
            return FrameCapture.RenderAndReadback(device, list =>
            {
                renderer.Render(list, camera, world);
                imgui.Render(list);
            }, new Color4(0.45f, 0.65f, 0.85f, 1f));
        }

        private static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }

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
