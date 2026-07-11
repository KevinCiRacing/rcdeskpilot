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
            using var session = new FlightSession(device, renderer, repoRoot, aircraftPar, "default");
            SceneNode world = session.World;
            IFlightModel model = session.Model;
            IAirplaneControl controls = session.Controls;
            AudioEngine.ListenerPosition = FlightSession.PilotPosition;

            // --- Pilot camera (legacy ObserverCamera: fixed pilot, zoom 1.5) ---
            var camera = new Camera
            {
                Position = FlightSession.PilotPosition,
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
                if (key == (int)InputKey.R && !test) { session.Reset(); }
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
                    FlightSession.KeyboardControls(input, controls, 1f / 60f, ref kbThrottle, ref kbElevator, ref kbAileron, ref kbRudder);
                }

                // --- Fixed-step physics + scene/audio updates ---
                session.Step(1f / 60f, camera.Position);

                Vector3 aircraftPosition = session.AircraftPosition;
                float altitude = session.Altitude;
                maxAltitude = Math.Max(maxAltitude, altitude);
                if (altitude > 10f) sawAirborne = true;
                if (model.Crashed && sawAirborne) sawCrash = true;

                // --- Camera: pilot looks at the aircraft ---
                camera.Target = aircraftPosition;
                float distance = Vector3.Distance(camera.Position, aircraftPosition);
                camera.FieldOfView = (float)Math.PI / 4 / Math.Max(1.5f, distance / 40f); // legacy-style zoom

                // --- HUD ---
                imgui.NewFrame();
                FlightSession.DrawHud(model, controls, altitude, model.Speed);
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
                    if (frame == 10) surfaceAtRest = session.FirstSurfaceTransform;
                    if (frame == 320 && !surfaceAtRest.Equals(session.FirstSurfaceTransform)) surfacesMove = true;
                    if (frame == 60 || frame == 420 || frame == 800)
                        shots.Add(("flight_" + frame, CaptureFrame(device, renderer, imgui, camera, world, model, controls, altitude)));
                    if (sawCrash && !sawReset && frame > 500)
                    {
                        session.Reset();
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

        /// <summary>Scripted autopilot for the selftest: take off, climb, then dive in.</summary>
        internal static void ScriptedControls(IAirplaneControl c, IFlightModel m, float t, ref bool sawCrash)
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



        private static byte[] CaptureFrame(GraphicsDevice device, SceneRenderer renderer, ImGuiRenderer imgui,
            Camera camera, SceneNode world, IFlightModel model, IAirplaneControl controls, float altitude)
        {
            imgui.NewFrame();
            FlightSession.DrawHud(model, controls, altitude, model.Speed);
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
