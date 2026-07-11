using System;
using System.Collections.Generic;
using System.Numerics;

namespace Bonsai.Graphics.Rendering
{
    /// <summary>
    /// A CPU-simulated billboard particle system writing into a dynamic quad
    /// mesh each frame (smoke trails, thermal bubbles). Attach the Mesh to a
    /// SceneNode with a Particle-kind material; per-particle alpha travels in
    /// the vertex normal's X component.
    /// </summary>
    public sealed class ParticleSystem
    {
        private struct Particle
        {
            public Vector3 Position, Velocity;
            public float Age, Life, Size, GrowRate;
        }

        private readonly List<Particle> particles = new List<Particle>();
        private readonly VertexPositionNormalTexture[] scratch;
        private readonly Random random;
        private float emitAccumulator;

        public Mesh Mesh { get; }
        public int MaxParticles { get; }
        public int AliveCount { get { return particles.Count; } }

        public float EmitRate { get; set; } = 20f;           // particles/second
        public Vector3 EmitPosition { get; set; }
        public Vector3 EmitVelocity { get; set; } = new Vector3(0, 2f, 0);
        public float VelocityJitter { get; set; } = 0.5f;
        public float Life { get; set; } = 3f;
        public float StartSize { get; set; } = 0.5f;
        public float GrowRate { get; set; } = 0.4f;
        public bool Emitting { get; set; } = true;

        /// <summary>World-space wind added to every particle's drift (m/s).</summary>
        public Vector3 Wind { get; set; }

        public ParticleSystem(GraphicsDevice device, int maxParticles, int seed = 1234)
        {
            MaxParticles = maxParticles;
            Mesh = Mesh.CreateDynamicQuads(device, maxParticles);
            scratch = new VertexPositionNormalTexture[maxParticles * 4];
            random = new Random(seed);
        }

        public void Update(float dt, Vector3 cameraPosition)
        {
            // age + expire
            for (int i = particles.Count - 1; i >= 0; i--)
            {
                Particle p = particles[i];
                p.Age += dt;
                if (p.Age >= p.Life)
                {
                    particles.RemoveAt(i);
                    continue;
                }
                p.Position += (p.Velocity + Wind) * dt;
                p.Size += p.GrowRate * dt;
                particles[i] = p;
            }

            // emit
            if (Emitting)
            {
                emitAccumulator += EmitRate * dt;
                while (emitAccumulator >= 1f && particles.Count < MaxParticles)
                {
                    emitAccumulator -= 1f;
                    particles.Add(new Particle
                    {
                        Position = EmitPosition,
                        Velocity = EmitVelocity + Jitter(),
                        Life = Life,
                        Size = StartSize,
                        GrowRate = GrowRate,
                    });
                }
                emitAccumulator = Math.Min(emitAccumulator, 1f);
            }

            // write camera-facing quads
            for (int i = 0; i < particles.Count; i++)
            {
                Particle p = particles[i];
                float alpha = 1f - p.Age / p.Life;
                Vector3 toCamera = Vector3.Normalize(cameraPosition - p.Position);
                Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, toCamera)) * p.Size;
                Vector3 up = Vector3.Normalize(Vector3.Cross(toCamera, right)) * p.Size;
                Vector3 alphaNormal = new Vector3(alpha, 0, 0);

                int v = i * 4;
                scratch[v] = new VertexPositionNormalTexture { Position = p.Position - right + up, Normal = alphaNormal, TexCoord = new Vector2(0, 0) };
                scratch[v + 1] = new VertexPositionNormalTexture { Position = p.Position + right + up, Normal = alphaNormal, TexCoord = new Vector2(1, 0) };
                scratch[v + 2] = new VertexPositionNormalTexture { Position = p.Position + right - up, Normal = alphaNormal, TexCoord = new Vector2(1, 1) };
                scratch[v + 3] = new VertexPositionNormalTexture { Position = p.Position - right - up, Normal = alphaNormal, TexCoord = new Vector2(0, 1) };
            }
            Mesh.UpdateQuads(scratch, particles.Count);
        }

        private Vector3 Jitter()
        {
            return new Vector3(
                (float)(random.NextDouble() - 0.5) * 2 * VelocityJitter,
                (float)(random.NextDouble() - 0.5) * VelocityJitter,
                (float)(random.NextDouble() - 0.5) * 2 * VelocityJitter);
        }
    }
}
