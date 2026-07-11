using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Bonsai.Graphics;
using Bonsai.Graphics.Rendering;
using Bonsai.Graphics.Scene;
using RCSim.DataClasses;
using RCSim.Interfaces;

namespace Bonsai.Graphics.TestHost
{
    /// <summary>Control values fed from a recording (surface animation only).</summary>
    internal sealed class RecordedControls : IAirplaneControl
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

    /// <summary>
    /// The menu-background demo flight (legacy Demo port): the default field
    /// with the recorded aircraft flying its .dat recording on a loop.
    /// No physics; the recording drives the visual directly.
    /// </summary>
    internal sealed class DemoPlayback : IDisposable
    {
        private readonly string demoPath;
        private readonly string repoRoot;
        private readonly AircraftVisual visual;
        private readonly RecordedControls controls = new RecordedControls();
        private RecordedFlight recording;
        private double lastTime;

        public SceneNode World { get; }
        public Vector3 AircraftPosition { get; private set; }

        public DemoPlayback(GraphicsDevice device, SceneRenderer renderer, string repoRoot, string demoPath)
        {
            this.repoRoot = repoRoot;
            this.demoPath = demoPath;

            string dataDir = Path.Combine(repoRoot, "RCSim", "data");
            World = new SceneNode("demo_world");
            var billboards = new List<SceneNode>();
            SceneNode windmillBlades;
            SceneryDemo.BuildDefaultScenery(device, renderer, World,
                Path.Combine(dataDir, "scenery", "default"), dataDir, billboards, out windmillBlades);

            recording = new RecordedFlight(demoPath, repoRoot);
            var parameters = new AircraftParameters();
            parameters.ReadParameters(recording.AircraftPar);
            controls.AircraftParameters = parameters;
            visual = new AircraftVisual(device, renderer, parameters, Path.GetDirectoryName(recording.AircraftPar));
            World.AddChild(visual.Root);
        }

        public void Update(double time)
        {
            if (!recording.Update(time))
            {
                // Loop the demo.
                recording.Dispose();
                recording = new RecordedFlight(demoPath, repoRoot);
                recording.Update(time);
            }

            AircraftPosition = recording.Position;
            Vector3 ypr = recording.YawPitchRoll;
            visual.Root.LocalTransform =
                Matrix4x4.CreateFromYawPitchRoll(ypr.X, ypr.Y, ypr.Z) *
                Matrix4x4.CreateTranslation(recording.Position);

            controls.Throttle = recording.Throttle;
            controls.Rudder = recording.Rudder;
            controls.Elevator = recording.Elevator;
            controls.Ailerons = recording.Ailerons;
            float dt = lastTime > 0 ? (float)(time - lastTime) : 1f / 60f;
            lastTime = time;
            visual.UpdateSurfaces(controls, dt);
        }

        public void Dispose()
        {
            recording.Dispose();
        }
    }
}
