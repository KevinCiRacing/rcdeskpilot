using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Xml.Linq;
using Bonsai.Graphics;
using Bonsai.Graphics.Audio;
using Bonsai.Graphics.Input;
using Bonsai.Graphics.Rendering;
using Bonsai.Graphics.Scene;
using Bonsai.Objects.Terrain;
using Hexa.NET.ImGui;
using RCSim;
using RCSim.DataClasses;
using RCSim.Interfaces;

namespace RCSim
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
        private readonly Tractor tractor;
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
                SceneryBuilder.BuildPhotoScenery(device, renderer, World, sceneryDir);
                Heightmap = new Heightmap(1000f); // photo fields are flat
                windmillBlades = null;
            }
            else
            {
                Heightmap = SceneryBuilder.BuildDefaultScenery(device, renderer, World, sceneryDir, dataDir,
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

            // --- Field actors (default field only) ---
            if (!photo)
            {
                tractor = new Tractor(device, renderer, World, dataDir);
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

            if (tractor != null)
                tractor.Update(frameTime, dt, Heightmap);

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

        /// <summary>Legacy fixed pilot location (ObserverCamera).</summary>
        internal static readonly Vector3 PilotPosition = new Vector3(0.1f, 1.7f, -15.0f);

        /// <summary>Legacy Player keyboard flight (accumulate/decay), via InputKey.</summary>
        internal static void KeyboardControls(InputManager input, IAirplaneControl c, float dt,
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

        internal static void DrawHud(IFlightModel model, IAirplaneControl controls, float altitude, double speed)
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
