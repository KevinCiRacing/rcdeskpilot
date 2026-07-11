using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Bonsai.Graphics;
using Bonsai.Graphics.Rendering;
using Bonsai.Graphics.Scene;
using Bonsai.Objects.Terrain;

namespace Bonsai.Graphics.TestHost
{
    internal static class ActorModels
    {
        /// <summary>Imports a model as a scene node (textures registered).</summary>
        public static SceneNode Load(GraphicsDevice device, SceneRenderer renderer, string path,
            MaterialKind? kindOverride = null)
        {
            var node = new SceneNode(Path.GetFileNameWithoutExtension(path));
            var model = Bonsai.Graphics.Assets.ModelImporter.Load(device, path);
            foreach (var (mesh, material) in model.Parts)
            {
                if (kindOverride.HasValue)
                    material.Kind = kindOverride.Value;
                if (material.Texture != null)
                    renderer.RegisterTexture(material.Texture);
                node.AddChild(new SceneNode { Mesh = mesh, Material = material });
            }
            return node;
        }
    }

    /// <summary>
    /// The tractor ploughing the far field (legacy Tractor port): drives
    /// north/south lanes between (-152,310) and (-42,188) with half-circle
    /// turns, follows the terrain height and slope, wheels spinning.
    /// </summary>
    internal sealed class Tractor
    {
        private enum DriveStage { South, North, TurnToNorth, TurnToSouth }

        private const float X1 = -152f, Z1 = 310f, X2 = -42f, Z2 = 188f;
        private const float Speed = 3f, TurnRadius = 3f;

        private readonly SceneNode root;
        private readonly SceneNode frontWheels;
        private readonly SceneNode rearWheels;
        private static readonly Vector3 FrontOffset = new Vector3(0, -1.5503f, 2.3186f);
        private static readonly Vector3 RearOffset = new Vector3(0, -0.8669f, -1.7969f);

        private DriveStage stage = DriveStage.South;
        private bool toRight = true;
        private float turnAngle, rotX, rotZ;
        private float x = X1, z = Z1, yaw = (float)Math.PI;

        public Tractor(GraphicsDevice device, SceneRenderer renderer, SceneNode world, string dataDir)
        {
            root = ActorModels.Load(device, renderer, Path.Combine(dataDir, "tractor_fixed.x"));
            frontWheels = root.AddChild(ActorModels.Load(device, renderer, Path.Combine(dataDir, "tractor_frontwheels.x")));
            rearWheels = root.AddChild(ActorModels.Load(device, renderer, Path.Combine(dataDir, "tractor_rearwheels.x")));
            world.AddChild(root);
        }

        public void Update(double totalTime, float dt, Heightmap heightmap)
        {
            if (dt > 1f)
                return;
            switch (stage)
            {
                case DriveStage.South:
                    z -= Speed * dt;
                    yaw = (float)Math.PI;
                    if (z < Z2) BeginTurn(DriveStage.TurnToNorth);
                    break;
                case DriveStage.North:
                    z += Speed * dt;
                    yaw = 0f;
                    if (z > Z1) BeginTurn(DriveStage.TurnToSouth);
                    break;
                case DriveStage.TurnToNorth:
                    turnAngle += dt;
                    if (toRight)
                    {
                        x = rotX - (float)Math.Cos(turnAngle) * TurnRadius;
                        z = rotZ - (float)Math.Sin(turnAngle) * TurnRadius;
                        yaw = (float)Math.PI - turnAngle;
                    }
                    else
                    {
                        x = rotX + (float)Math.Cos(turnAngle) * TurnRadius;
                        z = rotZ - (float)Math.Sin(turnAngle) * TurnRadius;
                        yaw = (float)Math.PI + turnAngle;
                    }
                    if (turnAngle > Math.PI) stage = DriveStage.North;
                    break;
                case DriveStage.TurnToSouth:
                    turnAngle += dt;
                    if (toRight)
                    {
                        x = rotX - (float)Math.Cos(turnAngle) * TurnRadius;
                        z = rotZ + (float)Math.Sin(turnAngle) * TurnRadius;
                        yaw = turnAngle;
                    }
                    else
                    {
                        x = rotX + (float)Math.Cos(turnAngle) * TurnRadius;
                        z = rotZ + (float)Math.Sin(turnAngle) * TurnRadius;
                        yaw = -turnAngle;
                    }
                    if (turnAngle > Math.PI) stage = DriveStage.South;
                    break;
            }

            float height = heightmap != null ? heightmap.GetHeightAt(x, z) : 0f;
            Vector3 normal = heightmap != null ? heightmap.GetSmoothNormalAt(x, z) : Vector3.UnitY;
            float rollAngle = -(float)Math.Atan(normal.X);
            float pitchAngle = (float)Math.Atan(normal.Z);

            root.LocalTransform =
                Matrix4x4.CreateScale(0.7f) *
                Matrix4x4.CreateRotationY(yaw) *
                Matrix4x4.CreateRotationX(pitchAngle) *
                Matrix4x4.CreateRotationZ(rollAngle) *
                Matrix4x4.CreateTranslation(x, height + 1.5f, z);
            frontWheels.LocalTransform = Matrix4x4.CreateRotationX((float)(1.2f * Speed * totalTime)) * Matrix4x4.CreateTranslation(FrontOffset);
            rearWheels.LocalTransform = Matrix4x4.CreateRotationX((float)(Speed * totalTime / 1.6f)) * Matrix4x4.CreateTranslation(RearOffset);
        }

        private void BeginTurn(DriveStage next)
        {
            if (toRight && x > X2) toRight = false;
            else if (x < X1) toRight = true;
            rotX = toRight ? x + TurnRadius : x - TurnRadius;
            rotZ = z;
            turnAngle = 0f;
            stage = next;
        }
    }

    /// <summary>
    /// The bird flock (legacy Birds port): one lead bird flies to the target,
    /// the rest follow it with separation; the player's aircraft scares them
    /// (within 10 m) into fleeing at double speed. Steering decisions at the
    /// legacy 10 Hz, integration every frame.
    /// </summary>
    internal sealed class BirdsFlock : IDisposable
    {
        private sealed class Bird
        {
            public SceneNode Node;
            public Vector3 Position, Velocity, Acceleration, Target;
            public float Roll, Speed = 3f, Acc = 3f, UpdateElapsed;
            public bool DoUpdate = true, Scared;
        }

        private readonly List<Bird> birds = new List<Bird>();
        private readonly SceneNode container;
        private readonly Random rnd = new Random(4321);
        private double lastUpdate = -10.0, lastMoveUpdate = -10.0;
        private int targetBird;

        /// <summary>The scare source (the player's aircraft), world space.</summary>
        public Vector3 ScarePosition { get; set; } = new Vector3(0, 10000, 0);

        public bool Scared { get; private set; }

        public bool TargetReached
        {
            get { return (birds[targetBird].Position - birds[targetBird].Target).LengthSquared() < 2.0f; }
        }

        public BirdsFlock(GraphicsDevice device, SceneRenderer renderer, SceneNode world, string dataDir, int count)
        {
            container = world.AddChild(new SceneNode("birds"));
            SceneNode prototype = ActorModels.Load(device, renderer, Path.Combine(dataDir, "bird.x"));
            for (int i = 0; i < count; i++)
            {
                var node = container.AddChild(new SceneNode("bird" + i));
                foreach (SceneNode part in prototype.Children)
                    node.AddChild(new SceneNode { Mesh = part.Mesh, Material = part.Material });
                birds.Add(new Bird
                {
                    Node = node,
                    Position = new Vector3(i % 10, 5f + 0.5f * i % 6, ((i + 5) % 10) * i / 20f + 50f),
                    Velocity = new Vector3(1, 0, 0),
                });
            }
            birds[0].Target = new Vector3(0f, 10f, 20f);
        }

        public bool Random { get; set; }

        public void SetRandomTarget()
        {
            targetBird = rnd.Next(birds.Count);
            birds[targetBird].Target = new Vector3(rnd.Next(200) - 100f, rnd.Next(5, 10), rnd.Next(200) - 100f);
        }

        public void SetTarget(Vector3 target)
        {
            targetBird = rnd.Next(birds.Count);
            birds[targetBird].Target = target;
        }

        public void Update(double totalTime, float dt)
        {
            if (Random && totalTime > lastUpdate + 10.0)
            {
                SetRandomTarget();
                lastUpdate = totalTime;
            }
            if (totalTime > lastMoveUpdate + 0.1f)
            {
                foreach (Bird bird in birds)
                {
                    bird.DoUpdate = true;
                    bird.UpdateElapsed = (float)(totalTime - lastMoveUpdate);
                }
                lastMoveUpdate = totalTime;
            }

            int scared = 0;
            for (int i = 0; i < birds.Count; i++)
            {
                Bird bird = birds[i];
                if (i != targetBird)
                    bird.Target = birds[targetBird].Position;
                StepBird(bird, i, dt);
                if (bird.Scared)
                    scared++;
            }
            Scared = scared > birds.Count / 2;
        }

        private void StepBird(Bird bird, int number, float dt)
        {
            bird.Scared = false;
            if (bird.DoUpdate)
            {
                bird.Speed = 3f;
                bird.Acc = 3f;
                bird.Acceleration = bird.Target - bird.Position;
                if (number == targetBird)
                {
                    if (bird.Target.Y == 0f && bird.Acceleration.LengthSquared() > 100)
                        bird.Acceleration = new Vector3(bird.Target.X, 10f, bird.Target.Z) - bird.Position;
                }
                else
                {
                    foreach (Bird other in birds)
                    {
                        if (other != bird && (other.Position - bird.Position).LengthSquared() < 0.5f)
                            bird.Acceleration += bird.Position - other.Position;
                    }
                }

                if ((ScarePosition - bird.Position).LengthSquared() < 100f)
                {
                    bird.Acceleration = bird.Position - ScarePosition;
                    if (bird.Position.Y < 1.0f)
                        bird.Acceleration.Y = 1.0f;
                    bird.Speed = 6f;
                    bird.Acc = 6f;
                    bird.Scared = true;
                }
                if (bird.Acceleration.LengthSquared() > 1e-8f)
                    bird.Acceleration = Vector3.Normalize(bird.Acceleration);
            }

            bird.Velocity += bird.Acc * bird.Acceleration * dt;
            if (bird.Velocity.LengthSquared() > 1e-8f)
                bird.Velocity = Vector3.Normalize(bird.Velocity);
            bird.Position += dt * bird.Speed * bird.Velocity;
            if (bird.Position.Y < 0)
                bird.Position = new Vector3(bird.Position.X, 0, bird.Position.Z);

            if (bird.DoUpdate)
            {
                Vector3 left = Vector3.Cross(bird.Velocity, Vector3.UnitY);
                bird.Roll -= (Vector3.Dot(left, bird.Acceleration) + bird.Roll) * bird.UpdateElapsed;
                bird.Roll = Math.Clamp(bird.Roll, -1f, 1f);
                float bankYaw = (float)Math.Atan2(bird.Velocity.Z, -bird.Velocity.X) + (float)Math.PI / 2;
                bird.Node.LocalTransform =
                    Matrix4x4.CreateScale(0.0005f) *
                    Matrix4x4.CreateFromYawPitchRoll(bankYaw, bird.Velocity.Y / bird.Speed, bird.Roll) *
                    Matrix4x4.CreateTranslation(bird.Position);
                bird.DoUpdate = false;
            }
            else
            {
                // Keep the last rotation, move the translation.
                Matrix4x4 m = bird.Node.LocalTransform;
                m.Translation = bird.Position;
                bird.Node.LocalTransform = m;
            }
        }

        public void Dispose()
        {
            container.RemoveFromParent();
        }
    }

    /// <summary>
    /// The scarecrow game (legacy ScareCrow port): cornfields of corn.x
    /// crosses, a 100-bird flock raiding a random field; buzz them with the
    /// aircraft to scare them off. Crops drain 2%/s while birds feed.
    /// </summary>
    internal sealed class ScareCrowGame : IDisposable
    {
        private readonly List<(SceneNode node, Vector3 position, bool perpendicular)> corns =
            new List<(SceneNode, Vector3, bool)>();
        private static readonly Vector3[] FieldPositions =
        {
            new Vector3(30, 0, 30), new Vector3(-30, 0, 30), new Vector3(-30, 0, -30),
        };

        private readonly BirdsFlock birds;
        private readonly SceneNode arrow;
        private readonly SceneNode container;
        private readonly Random rnd = new Random(8765);
        private double lastUpdate = -10.0;
        private int currentTargetField = -1;
        private double cropsLeft = 100.0;
        private bool justArrived = true;
        private double startTime = -1;
        private int minutes, seconds;

        public string StatusText { get; private set; }
        public bool GameOver { get { return cropsLeft < 0; } }
        public double CropsLeft { get { return cropsLeft; } }

        public ScareCrowGame(GraphicsDevice device, SceneRenderer renderer, SceneNode world, string dataDir)
        {
            container = world.AddChild(new SceneNode("scarecrow"));

            SceneNode cornPrototype = ActorModels.Load(device, renderer, Path.Combine(dataDir, "corn.x"), MaterialKind.CutoutLit);
            foreach (Vector3 field in FieldPositions)
            {
                for (int i = 0; i < 3; i++)
                    AddCorn(cornPrototype, new Vector3(field.X, 0, field.Z + 6.6f * i - 6.6f), false);
                for (int i = 0; i < 2; i++)
                    AddCorn(cornPrototype, new Vector3(field.X + 13f * i - 6.5f, 0, field.Z), true);
            }

            birds = new BirdsFlock(device, renderer, container, dataDir, 100);
            birds.Random = false;
            birds.SetRandomTarget();

            arrow = container.AddChild(ActorModels.Load(device, renderer, Path.Combine(dataDir, "arrow.x")));
            arrow.Visible = false;
        }

        private void AddCorn(SceneNode prototype, Vector3 position, bool perpendicular)
        {
            SceneNode corn = container.AddChild(new SceneNode("corn"));
            foreach (SceneNode part in prototype.Children)
                corn.AddChild(new SceneNode { Mesh = part.Mesh, Material = part.Material });
            corns.Add((corn, position, perpendicular));
        }

        public void Update(Vector3 aircraftPosition, Vector3 cameraPosition, double time, float dt)
        {
            if (startTime < 0)
                startTime = time;
            else if (cropsLeft > 0)
            {
                minutes = (int)Math.Floor((time - startTime) / 60);
                seconds = (int)Math.Floor(time - startTime - minutes * 60);
            }
            StatusText = cropsLeft < 0
                ? string.Format("Game over!\nYou defended the crops for {0} minutes and {1} seconds", minutes, seconds)
                : string.Format("Your time : {0}:{1}\nCrops remaining : {2}%", minutes, seconds.ToString("00"), (int)Math.Floor(cropsLeft));

            if (time > lastUpdate + 5)
            {
                if (birds.TargetReached || currentTargetField == -1)
                {
                    if (birds.TargetReached && currentTargetField != -1)
                    {
                        if (!justArrived) justArrived = true;
                        else Retarget();
                    }
                    else
                        Retarget();
                }
                lastUpdate = time;
            }

            arrow.Visible = birds.TargetReached && currentTargetField != -1 && cropsLeft >= 0;
            if (arrow.Visible)
            {
                cropsLeft -= 2 * dt;
                Vector3 field = FieldPositions[currentTargetField];
                arrow.LocalTransform = Matrix4x4.CreateRotationY((float)time) *
                    Matrix4x4.CreateTranslation(field.X, 3f, field.Z);
            }

            // Corn crosses flip to face the camera (legacy one-sided quads).
            foreach (var (node, position, perpendicular) in corns)
            {
                float angle = perpendicular
                    ? (cameraPosition.X > position.X ? -(float)Math.PI / 2 : (float)Math.PI / 2)
                    : (cameraPosition.Z > position.Z ? (float)Math.PI : 0f);
                node.LocalTransform = Matrix4x4.CreateScale(1.6f) *
                    Matrix4x4.CreateRotationY(angle) * Matrix4x4.CreateTranslation(position);
            }

            birds.ScarePosition = aircraftPosition;
            birds.Update(time, dt);
            if (birds.Scared)
            {
                currentTargetField = -1;
                birds.SetRandomTarget();
            }
        }

        private void Retarget()
        {
            int next;
            do { next = rnd.Next(FieldPositions.Length); } while (next == currentTargetField);
            currentTargetField = next;
            birds.SetTarget(FieldPositions[next]);
            justArrived = false;
        }

        public void Dispose()
        {
            birds.Dispose();
            container.RemoveFromParent();
        }
    }

    /// <summary>The bombing/spot-landing target on the runway (legacy Bombing).</summary>
    internal sealed class BombingTarget : IDisposable
    {
        private readonly SceneNode node;

        public BombingTarget(GraphicsDevice device, SceneRenderer renderer, SceneNode world, string dataDir)
        {
            var material = new Material(Texture2D.Load(device, Path.Combine(dataDir, "target1.png")))
            {
                Kind = MaterialKind.CutoutLit,
            };
            renderer.RegisterTexture(material.Texture);
            node = world.AddChild(new SceneNode("bombing_target")
            {
                Mesh = PrimitiveMeshes.BuildQuad(device, new Vector3(0, 0.02f, 0), Vector3.UnitX, -Vector3.UnitZ, 2.5f),
                Material = material,
            });
        }

        public void Dispose()
        {
            node.RemoveFromParent();
        }
    }
}
