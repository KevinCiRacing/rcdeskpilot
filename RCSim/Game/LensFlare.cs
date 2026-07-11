using System;
using System.IO;
using System.Numerics;
using Bonsai.Graphics;
using Bonsai.Graphics.Rendering;
using Bonsai.Graphics.Scene;

namespace RCSim
{
    /// <summary>
    /// Lens flare (legacy port): five textured billboards strung along the
    /// line from the sun through a point a third of the sun-distance in front
    /// of the camera, shown only while the sun is inside the view frustum.
    /// Sizes scale with distance so the apparent size stays constant
    /// (legacy PointMesh semantics).
    /// </summary>
    internal sealed class LensFlare : IDisposable
    {
        public static readonly Vector3 SunPosition = new Vector3(-1587, 3918, -1531);

        private sealed class Flare
        {
            public SceneNode Node;
            public Mesh Mesh;
            public float Position;
            public float Size;
            public VertexPositionNormalTexture[] Vertices = new VertexPositionNormalTexture[4];
        }

        private readonly Flare[] flares;
        private readonly SceneNode container;

        public LensFlare(GraphicsDevice device, SceneRenderer renderer, SceneNode world, string dataDir)
        {
            container = world.AddChild(new SceneNode("lensflare"));
            var specs = new (string texture, float position, float size)[]
            {
                ("flare1.png", 0.4f, 0.2f),
                ("flare2.png", 0.6f, 0.05f),
                ("flare3.png", 0.9f, 0.1f),
                ("flare4.png", 1.1f, 0.1f),
                ("flare1.png", 1.2f, 0.05f),
            };
            flares = new Flare[specs.Length];
            for (int i = 0; i < specs.Length; i++)
            {
                Mesh mesh = Mesh.CreateDynamicQuads(device, 1);
                var material = new Material(Texture2D.Load(device, Path.Combine(dataDir, specs[i].texture)))
                {
                    Kind = MaterialKind.Particle,
                };
                renderer.RegisterTexture(material.Texture);
                flares[i] = new Flare
                {
                    Mesh = mesh,
                    Position = specs[i].position,
                    Size = specs[i].size,
                    Node = container.AddChild(new SceneNode("flare" + i) { Mesh = mesh, Material = material }),
                };
            }
        }

        /// <summary>Whether the flare chain was visible on the last update.</summary>
        public bool FlareVisible { get { return container.Visible; } }

        public void Update(Camera camera)
        {
            Vector3 front = Vector3.Normalize(camera.Target - camera.Position);
            Vector3 toSun = SunPosition - camera.Position;
            float sunDistance = toSun.Length();

            // Sun-in-frustum test (legacy PointVisible approximation).
            float cosAngle = Vector3.Dot(front, toSun / sunDistance);
            bool visible = cosAngle > (float)Math.Cos(camera.FieldOfView);
            container.Visible = visible;
            if (!visible)
                return;

            Vector3 flareEndpoint = camera.Position + (sunDistance / 3f) * front;
            Vector3 flareDirection = flareEndpoint - SunPosition;

            foreach (Flare flare in flares)
            {
                Vector3 position = SunPosition + flareDirection * flare.Position;
                Vector3 toCamera = Vector3.Normalize(camera.Position - position);
                float worldSize = flare.Size * Vector3.Distance(camera.Position, position) * 0.1f;
                Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, toCamera)) * worldSize;
                Vector3 up = Vector3.Normalize(Vector3.Cross(toCamera, right)) * worldSize;
                Vector3 alpha = new Vector3(0.7f, 0, 0); // particle shader alpha in Normal.x

                flare.Vertices[0] = new VertexPositionNormalTexture { Position = position - right + up, Normal = alpha, TexCoord = new Vector2(0, 0) };
                flare.Vertices[1] = new VertexPositionNormalTexture { Position = position + right + up, Normal = alpha, TexCoord = new Vector2(1, 0) };
                flare.Vertices[2] = new VertexPositionNormalTexture { Position = position + right - up, Normal = alpha, TexCoord = new Vector2(1, 1) };
                flare.Vertices[3] = new VertexPositionNormalTexture { Position = position - right - up, Normal = alpha, TexCoord = new Vector2(0, 1) };
                flare.Mesh.UpdateQuads(flare.Vertices, 1);
            }
        }

        public void Dispose()
        {
            container.RemoveFromParent();
        }
    }
}
