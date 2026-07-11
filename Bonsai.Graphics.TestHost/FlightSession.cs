using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Xml.Linq;
using Bonsai.Graphics;
using Bonsai.Graphics.Audio;
using Bonsai.Graphics.Rendering;
using Bonsai.Graphics.Scene;
using Bonsai.Objects.Terrain;
using RCSim;
using RCSim.DataClasses;
using RCSim.Interfaces;

namespace Bonsai.Graphics.TestHost
{
    /// <summary>
    /// One flyable session (issue 15): scenery world + aircraft physics/visual
    /// + weather + engine audio, stepped at the fixed 2 ms physics rate.
    /// Owned by a host (GameShell, FlightDemo) that supplies the window,
    /// device, renderer, controls, camera, and HUD.
    /// </summary>
    internal sealed class FlightSession : IDisposable
    {
        public const float PhysicsStep = 0.002f;

        public SceneNode World { get; }
        public IFlightModel Model { get; }
        public IAirplaneControl Controls { get { return (IAirplaneControl)Model; } }
        public AircraftParameters Parameters { get; }
        public Heightmap Heightmap { get; }
        public Wind Wind { get { return RCSim.Program.Instance.Weather.Wind; } }

        private readonly AircraftVisual visual;
        private readonly List<SceneNode> billboards;
        private readonly SceneNode windmillBlades;
        private readonly Sound3D engineSound;
        private readonly SoundControllable variometer;
        private readonly ParticleSystem smoke;
        private float physicsAccumulator;
        private double physicsTime;
        private double frameTime;
        private double lastVariometerUpdate;

        /// <summary>Smoke trail toggle (legacy Player.ToggleSmoke).</summary>
        public bool SmokeEmitting
        {
            get { return smoke != null && smoke.Emitting; }
            set { if (smoke != null) smoke.Emitting = value; }
        }

        public Vector3 AircraftPosition
        {
            get { return new Vector3(-Model.Y, -Model.Z, -Model.X); }
        }

        public float Altitude { get { return -Model.Z; } }

        public FlightSession(GraphicsDevice device, SceneRenderer renderer,
            string repoRoot, string aircraftPar, string sceneryName, GameSettings settings = null)
        {
            string dataDir = Path.Combine(repoRoot, "RCSim", "data");
            string sceneryDir = Path.Combine(dataDir, "scenery", sceneryName);
            bool photo = IsPhotoScenery(sceneryDir);

            // --- World ---
            World = new SceneNode("world");
            billboards = new List<SceneNode>();
            if (photo)
            {
                SceneryDemo.BuildPhotoScenery(device, renderer, World, sceneryDir);
                Heightmap = new Heightmap(1000f); // photo fields are flat
                windmillBlades = null;
            }
            else
            {
                Heightmap = SceneryDemo.BuildDefaultScenery(device, renderer, World, sceneryDir, dataDir,
                    billboards, out windmillBlades);
            }

            // --- Weather: wire the terrain + thermals into the shared Wind ---
            RCSim.Program.Instance.Heightmap = Heightmap;
            Wind wind = Wind;
            wind.SoundPath = settings == null || settings.GetBool("EnableWindSound", true)
                ? Path.Combine(dataDir, "wind.wav") : null;
            wind.ClearThermalSources();
            if (!photo)
                AddThermalsFromTerrainDef(sceneryDir, wind);

            // --- Windsock: flag.x cloth near the pilot (default field only) ---
            if (!photo)
            {
                string flagFile = Path.Combine(dataDir, "flag.x");
                if (File.Exists(flagFile))
                {
                    var model = Bonsai.Graphics.Assets.ModelImporter.Load(device, flagFile);
                    var sockNode = new SceneNode("windsock") { LocalTransform = Matrix4x4.CreateTranslation(1f, 1f, 0f) };
                    foreach (var (mesh, material) in model.Parts)
                    {
                        material.Kind = MaterialKind.FlagCloth;
                        if (material.Texture != null)
                            renderer.RegisterTexture(material.Texture);
                        sockNode.AddChild(new SceneNode { Mesh = mesh, Material = material });
                    }
                    World.AddChild(sockNode);
                }
            }

            // --- Aircraft ---
            Parameters = new AircraftParameters();
            Parameters.ReadParameters(aircraftPar);
            Model = Parameters.Version == 2 ? new FlightModelWind2() : (IFlightModel)new FlightModelWind();
            Model.AircraftParameters = Parameters;
            Model.UpdateConstants();
            Model.Heightmap = Heightmap;
            Model.Water = new List<Water>();
            Model.Wind = Vector3.Zero;
            Reset();

            visual = new AircraftVisual(device, renderer, Parameters, Path.GetDirectoryName(aircraftPar));
            World.AddChild(visual.Root);

            // --- Engine audio: 3D emitter at the aircraft ---
            string engineWav = Parameters.EngineSound != null
                ? Path.Combine(Path.GetDirectoryName(aircraftPar), Path.GetFileName(Parameters.EngineSound)) : null;
            if (engineWav != null && File.Exists(engineWav) && AudioEngine.IsInitialized)
            {
                engineSound = new Sound3D(engineWav);
                engineSound.Play(true);
            }

            // --- Variometer (legacy Player.UpdateVariometer) ---
            string varioWav = Path.Combine(dataDir, "variometer.wav");
            if (settings != null && settings.GetBool("EnableVariometer", false) &&
                Parameters.HasVariometer && File.Exists(varioWav) && AudioEngine.IsInitialized)
            {
                variometer = new SoundControllable(varioWav);
                variometer.Volume = 10;
                variometer.Play(true);
            }

            // --- Smoke trail (legacy Smoke: SmokeDetail-scaled puff system) ---
            string smokeTexture = Path.Combine(dataDir, "smokepuff.png");
            if (File.Exists(smokeTexture))
            {
                int detail = settings != null ? settings.GetInt("SmokeDetail", 2) : 2;
                smoke = new ParticleSystem(device, detail >= 3 ? 450 : detail == 2 ? 300 : 150)
                {
                    Life = detail >= 3 ? 4.5f : detail == 2 ? 3f : 1.5f,
                    StartSize = 0.15f,
                    GrowRate = 0.25f,
                    VelocityJitter = 0.05f,
                    EmitVelocity = Vector3.Zero,
                    Emitting = false,
                };
                World.AddChild(new SceneNode("smoke")
                {
                    Mesh = smoke.Mesh,
                    Material = new Material(Texture2D.Load(device, smokeTexture)) { Kind = MaterialKind.Particle },
                });
            }
        }

        public void Reset()
        {
            Model.Reset();
            // Default start position (0, 0.05, 0) world -> NED (0, 0, -0.05).
            Model.X = 0; Model.Y = 0; Model.Z = -0.05f;
            Model.Crashed = false;
        }

        /// <summary>
        /// Advances one render frame: weather, fixed-step physics, scene-node
        /// transforms, billboard facing, and engine audio. Controls must
        /// already be applied to <see cref="Controls"/>.
        /// </summary>
        public void Step(float dt, Vector3 cameraPosition)
        {
            frameTime += dt;
            RCSim.Program.Instance.CurrentTime = frameTime;
            RCSim.Program.Instance.Weather.Update(frameTime, dt);

            // Wind at the aircraft (legacy Player feed; v2 also samples per-wing).
            Model.Wind = Wind.GetWindAt(AircraftPosition);

            physicsAccumulator += dt;
            while (physicsAccumulator >= PhysicsStep)
            {
                physicsTime += PhysicsStep;
                Model.UpdateControls(PhysicsStep);
                if (Parameters.Version == 2)
                    ((FlightModelWind2)Model).MoveScene(PhysicsStep);
                else
                    ((FlightModelWind)Model).MoveScene(PhysicsStep);
                physicsAccumulator -= PhysicsStep;
            }
            Wind.WindTime = (float)physicsTime;

            visual.UpdateTransform(Model);
            visual.UpdateSurfaces(Controls, dt);

            if (windmillBlades != null)
            {
                Vector3 pivot = windmillBlades.Mesh.BoundsCenter;
                windmillBlades.LocalTransform = Matrix4x4.CreateTranslation(-pivot)
                    * Matrix4x4.CreateRotationZ((float)frameTime * 2f) * Matrix4x4.CreateTranslation(pivot);
            }

            foreach (SceneNode tree in billboards)
            {
                Vector3 position = tree.LocalTransform.Translation;
                float yaw = (float)Math.Atan2(cameraPosition.X - position.X, cameraPosition.Z - position.Z);
                tree.LocalTransform = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(position);
            }

            if (engineSound != null)
            {
                engineSound.Location = AircraftPosition;
                float hz = Parameters.EngineMinFrequency
                    + (float)Controls.Throttle * (Parameters.EngineMaxFrequency - Parameters.EngineMinFrequency);
                engineSound.FrequencyRatio = Math.Max(0.1f, hz / 22050f);
            }

            // Variometer pitch/volume by climb rate, at the legacy 10 Hz cadence.
            if (variometer != null && frameTime - lastVariometerUpdate > 0.1)
            {
                lastVariometerUpdate = frameTime;
                float climbRate = Model.Velocity.Z; // NED: negative = climbing
                variometer.Frequency = (int)(22100 - Math.Sign(climbRate) * Math.Sqrt(Math.Abs(climbRate)) * 1000);
                variometer.Volume = Math.Min(100, (int)(Math.Abs(climbRate - 0.3f) * 100));
            }

            // Smoke trail: emit just behind the aircraft, drifting with the wind.
            if (smoke != null)
            {
                Vector3 velocity = new Vector3(-Model.Velocity.Y, -Model.Velocity.Z, -Model.Velocity.X);
                Vector3 back = velocity.LengthSquared() > 0.01f ? -Vector3.Normalize(velocity) : Vector3.Zero;
                smoke.EmitPosition = AircraftPosition + back * (float)Controls.Throttle;
                smoke.EmitRate = Math.Max(5f, (float)Controls.Throttle * 100f);
                smoke.Wind = Wind.CurrentWind;
                smoke.Update(dt, cameraPosition);
            }
        }

        /// <summary>Live smoke particle count (diagnostics).</summary>
        public int SmokeParticles { get { return smoke != null ? smoke.AliveCount : 0; } }

        /// <summary>Diagnostics passthrough for the flytest.</summary>
        public Matrix4x4 FirstSurfaceTransform { get { return visual.FirstSurfaceTransform; } }

        private static bool IsPhotoScenery(string sceneryDir)
        {
            string[] pars = Directory.GetFiles(sceneryDir, "*.par");
            if (pars.Length == 0)
                return false;
            XElement definition = XDocument.Load(pars[0]).Root.Element("definition");
            XElement type = definition != null ? definition.Element("type") : null;
            return type != null && type.Value == "photo";
        }

        private static void AddThermalsFromTerrainDef(string sceneryDir, Wind wind)
        {
            XElement definition = XDocument.Load(Directory.GetFiles(sceneryDir, "*.par")[0]).Root.Element("definition");
            XElement defName = definition != null ? definition.Element("definition") : null;
            if (defName == null)
                return;
            string terrainDef = Path.Combine(sceneryDir, defName.Value);
            if (!File.Exists(terrainDef))
                return;
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            foreach (XElement thermal in XDocument.Load(terrainDef).Root.Elements("Thermals"))
            {
                XElement position = thermal.Element("Position");
                wind.AddThermalSource(
                    new Vector3(
                        float.Parse(position.Element("X").Value, ic),
                        float.Parse(position.Element("Y").Value, ic),
                        float.Parse(position.Element("Z").Value, ic)),
                    float.Parse(thermal.Element("Strength").Value, ic),
                    float.Parse(thermal.Element("Size").Value, ic));
            }
        }

        public void Dispose()
        {
            // GPU resources (meshes/textures built for this world) are released
            // with the device; sessions are user-driven and few per run.
            if (engineSound != null)
            {
                engineSound.Stop();
                engineSound.Dispose();
            }
            if (variometer != null)
            {
                variometer.Stop();
                variometer.Dispose();
            }
            Wind.ClearThermalSources();
            RCSim.Program.Instance.Heightmap = null;
        }
    }
}
