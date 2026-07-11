using System;
using System.Numerics;

namespace Bonsai.Graphics.Scene
{
    /// <summary>A perspective camera producing view/projection matrices.</summary>
    public sealed class Camera
    {
        public Vector3 Position { get; set; } = new Vector3(0, 2, -10);
        public Vector3 Target { get; set; } = Vector3.Zero;
        public Vector3 Up { get; set; } = Vector3.UnitY;
        public float FieldOfView { get; set; } = (float)Math.PI / 4;
        public float AspectRatio { get; set; } = 16f / 9f;
        public float NearPlane { get; set; } = 0.1f;
        public float FarPlane { get; set; } = 5000f;

        public Matrix4x4 GetView()
        {
            return Matrix4x4.CreateLookAt(Position, Target, Up);
        }

        public Matrix4x4 GetProjection()
        {
            return Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, AspectRatio, NearPlane, FarPlane);
        }

        public Matrix4x4 GetViewProjection()
        {
            return GetView() * GetProjection();
        }
    }
}
