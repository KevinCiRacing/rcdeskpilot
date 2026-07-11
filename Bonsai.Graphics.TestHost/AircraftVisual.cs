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
    /// <summary>
    /// The aircraft's visual scene-node hierarchy, built from
    /// AircraftParameters: the fixed mesh plus a node per control surface,
    /// deflected/spun each frame from the flight model (legacy
    /// ControlSurface.OnFrameMove recipe). Part meshes are modeled in
    /// aircraft space; hinge rotation is applied about the surface's
    /// Position along its RotationAxis.
    /// </summary>
    internal sealed class AircraftVisual
    {
        private sealed class SurfacePart
        {
            public SceneNode Node;
            public AircraftParameters.ControlSurface Definition;
            public float AccumulatedAngle; // for prop/rotor types
        }

        private readonly List<SurfacePart> surfaces = new List<SurfacePart>();

        public SceneNode Root { get; }

        public AircraftVisual(GraphicsDevice device, SceneRenderer renderer, AircraftParameters parameters, string aircraftDir)
        {
            Root = new SceneNode("aircraft");
            if (!string.IsNullOrEmpty(parameters.FixedMesh))
                Root.AddChild(LoadPart(device, renderer, Path.Combine(aircraftDir, parameters.FixedMesh)));
            if (parameters.ControlSurfaces != null)
                foreach (AircraftParameters.ControlSurface surface in parameters.ControlSurfaces)
                    AddSurface(device, renderer, Root, surface, aircraftDir);
        }

        private void AddSurface(GraphicsDevice device, SceneRenderer renderer, SceneNode parent,
            AircraftParameters.ControlSurface definition, string aircraftDir)
        {
            if (string.IsNullOrEmpty(definition.Filename))
                return;
            string path = Path.Combine(aircraftDir, definition.Filename);
            if (!File.Exists(path))
                return;
            SceneNode node = parent.AddChild(LoadPart(device, renderer, path));
            node.Name = definition.Filename;
            surfaces.Add(new SurfacePart { Node = node, Definition = definition });
            if (definition.ChildControlSurfaces != null)
                foreach (AircraftParameters.ControlSurface child in definition.ChildControlSurfaces)
                    AddSurface(device, renderer, node, child, aircraftDir);
        }

        private static SceneNode LoadPart(GraphicsDevice device, SceneRenderer renderer, string path)
        {
            var node = new SceneNode(Path.GetFileNameWithoutExtension(path));
            var model = Bonsai.Graphics.Assets.ModelImporter.Load(device, path);
            foreach (var (mesh, material) in model.Parts)
            {
                if (material.Texture != null)
                    renderer.RegisterTexture(material.Texture);
                node.AddChild(new SceneNode { Mesh = mesh, Material = material });
            }
            return node;
        }

        /// <summary>Places the aircraft from flight-model state (legacy Player mapping:
        /// world = (-Y, -Z, -X) NED, rotation = YawPitchRoll(anglesZ, anglesY, anglesX)).</summary>
        public void UpdateTransform(IFlightModel model)
        {
            Vector3 angles = model.Angles; // X=roll, Y=pitch, Z=yaw
            Root.LocalTransform =
                Matrix4x4.CreateFromYawPitchRoll(angles.Z, angles.Y, angles.X) *
                Matrix4x4.CreateTranslation(-model.Y, -model.Z, -model.X);
        }

        /// <summary>Deflects control surfaces / spins props from the control state.</summary>
        public void UpdateSurfaces(IAirplaneControl control, float elapsedTime)
        {
            foreach (SurfacePart part in surfaces)
            {
                AircraftParameters.ControlSurface definition = part.Definition;
                double input = 0;
                switch (definition.Channel)
                {
                    case AircraftParameters.ChannelEnum.Elevator: input = control.Elevator; break;
                    case AircraftParameters.ChannelEnum.Rudder: input = control.Rudder; break;
                    case AircraftParameters.ChannelEnum.Aileron: input = control.Ailerons; break;
                    case AircraftParameters.ChannelEnum.Throttle: input = control.Throttle; break;
                    case AircraftParameters.ChannelEnum.Flaps: input = control.Flaps; break;
                    case AircraftParameters.ChannelEnum.Gear: input = control.Gear; break;
                }
                if (definition.Reversed)
                    input = -input;

                float angle;
                switch (definition.Type)
                {
                    case AircraftParameters.ControlSurfaceTypeEnum.Normal:
                    case AircraftParameters.ControlSurfaceTypeEnum.Reflective:
                        angle = input < 0
                            ? (float)(definition.ZeroAngle - input * (definition.MinimumAngle - definition.ZeroAngle))
                            : (float)(definition.ZeroAngle + input * (definition.MaximumAngle - definition.ZeroAngle));
                        break;
                    case AircraftParameters.ControlSurfaceTypeEnum.PropHighRPM:
                        part.AccumulatedAngle += 8f * elapsedTime * (float)(input - 0.5);
                        angle = part.AccumulatedAngle;
                        break;
                    case AircraftParameters.ControlSurfaceTypeEnum.PropLowRPM:
                    case AircraftParameters.ControlSurfaceTypeEnum.PropFoldingLowRPM:
                        part.AccumulatedAngle += 1000f * elapsedTime * (float)(input + 0.13);
                        angle = part.AccumulatedAngle;
                        break;
                    case AircraftParameters.ControlSurfaceTypeEnum.RotorHighRPM:
                    case AircraftParameters.ControlSurfaceTypeEnum.RotorLowRPM:
                        part.AccumulatedAngle += elapsedTime * control.RotorRPM;
                        angle = part.AccumulatedAngle;
                        break;
                    default:
                        angle = 0f;
                        break;
                }

                Vector3 axis = definition.RotationAxis;
                if (axis.LengthSquared() < 1e-6f)
                    axis = Vector3.UnitX;
                part.Node.LocalTransform =
                    Matrix4x4.CreateTranslation(-definition.Position) *
                    Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(axis), angle) *
                    Matrix4x4.CreateTranslation(definition.Position);
            }
        }

        /// <summary>Diagnostics: a surface's current local transform (flytest).</summary>
        public Matrix4x4 FirstSurfaceTransform
        {
            get { return surfaces.Count > 0 ? surfaces[0].Node.LocalTransform : Matrix4x4.Identity; }
        }
    }
}
