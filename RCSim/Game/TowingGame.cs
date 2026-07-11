using System;
using System.IO;
using System.Numerics;
using Bonsai.Graphics;
using Bonsai.Graphics.Rendering;
using Bonsai.Graphics.Scene;
using RCSim;
using RCSim.DataClasses;

namespace RCSim
{
    /// <summary>
    /// Aerotow (legacy Towing port): the SF260 towplane flies the stock
    /// towing.dat recording; the cable physics already lives in the flight
    /// models (UpdateCable) and is fed CableOrigin/CableVelocity in model
    /// (NED) space each frame. A thin camera-facing quad renders the rope.
    /// The tow auto-releases after the legacy 70 s.
    /// </summary>
    internal sealed class TowingGame : IDisposable
    {
        private readonly RecordedFlight recording;
        private readonly AircraftVisual towVisual;
        private readonly RecordedControls controls = new RecordedControls();
        private readonly SceneNode container;
        private readonly Mesh ropeMesh;
        private readonly VertexPositionNormalTexture[] ropeVertices = new VertexPositionNormalTexture[4];
        private double lastTime;

        public bool Active { get { return recording.Playing && recording.Time <= 70.0; } }
        public Vector3 TowplanePosition { get { return recording.Position; } }

        public TowingGame(GraphicsDevice device, SceneRenderer renderer, SceneNode world, string repoRoot)
        {
            recording = new RecordedFlight(Path.Combine(repoRoot, "RCSim", "towing.dat"), repoRoot);
            var parameters = new AircraftParameters();
            parameters.ReadParameters(recording.AircraftPar);
            controls.AircraftParameters = parameters;

            container = world.AddChild(new SceneNode("towing"));
            towVisual = new AircraftVisual(device, renderer, parameters, Path.GetDirectoryName(recording.AircraftPar));
            container.AddChild(towVisual.Root);

            ropeMesh = Mesh.CreateDynamicQuads(device, 1);
            container.AddChild(new SceneNode("rope")
            {
                Mesh = ropeMesh,
                Material = new Material { DiffuseColor = new Vector4(0.25f, 0.22f, 0.2f, 1f) },
            });
        }

        /// <summary>Advances the towplane and feeds the cable into the glider's
        /// flight model. Returns false when the recording ends.</summary>
        public bool Update(FlightSession session, Vector3 cameraPosition, double time)
        {
            if (!recording.Update(time) || recording.Time > 70.0)
            {
                session.Model.CableEnabled = false;
                return false;
            }

            // Towplane visual.
            Vector3 ypr = recording.YawPitchRoll;
            towVisual.Root.LocalTransform =
                Matrix4x4.CreateFromYawPitchRoll(ypr.X, ypr.Y, ypr.Z) *
                Matrix4x4.CreateTranslation(recording.Position);
            controls.Throttle = recording.Throttle;
            controls.Rudder = recording.Rudder;
            controls.Elevator = recording.Elevator;
            controls.Ailerons = recording.Ailerons;
            float dt = lastTime > 0 ? (float)(time - lastTime) : 1f / 60f;
            lastTime = time;
            towVisual.UpdateSurfaces(controls, dt);

            // Cable feed (legacy Player: world -> model/NED space).
            session.Model.CableOrigin = FlightModelWind.ToModel(recording.Position);
            session.Model.CableVelocity = FlightModelWind.ToModel(recording.Velocity);

            // Rope: a thin camera-facing quad from glider nose to towplane tail.
            if (session.Model.CableEnabled)
            {
                Vector3 a = session.AircraftPosition;
                Vector3 b = recording.Position;
                Vector3 mid = (a + b) * 0.5f;
                Vector3 direction = b - a;
                Vector3 toCamera = cameraPosition - mid;
                Vector3 side = Vector3.Cross(direction, toCamera);
                side = side.LengthSquared() > 1e-10f ? Vector3.Normalize(side) * 0.012f : Vector3.UnitY * 0.012f;
                ropeVertices[0] = new VertexPositionNormalTexture { Position = a - side, Normal = Vector3.UnitY };
                ropeVertices[1] = new VertexPositionNormalTexture { Position = a + side, Normal = Vector3.UnitY };
                ropeVertices[2] = new VertexPositionNormalTexture { Position = b + side, Normal = Vector3.UnitY };
                ropeVertices[3] = new VertexPositionNormalTexture { Position = b - side, Normal = Vector3.UnitY };
                ropeMesh.UpdateQuads(ropeVertices, 1);
            }
            else
            {
                ropeMesh.UpdateQuads(ropeVertices, 0);
            }
            return true;
        }

        public void Dispose()
        {
            recording.Dispose();
            container.RemoveFromParent();
        }
    }
}
