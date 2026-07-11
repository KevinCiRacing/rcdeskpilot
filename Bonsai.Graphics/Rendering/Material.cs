using System.Numerics;

namespace Bonsai.Graphics.Rendering
{
    /// <summary>Which pipeline a material renders with.</summary>
    public enum MaterialKind
    {
        /// <summary>Textured, directional-lit, opaque (the default).</summary>
        StandardLit,
        /// <summary>Textured, no lighting (sky dome).</summary>
        Unlit,
        /// <summary>Lit with alpha-test discard (tree billboards, foliage).</summary>
        CutoutLit,
        /// <summary>Heightmap terrain with 4-layer texture splatting.</summary>
        TerrainSplat,
        /// <summary>Photo Scenery panel: unlit color + per-pixel depth from a depth map.</summary>
        PhotoPanel,
        /// <summary>Lit, alpha-blended, depth-read-only; drawn back-to-front after opaques.</summary>
        TransparentLit,
        /// <summary>Wind-driven cloth vertex animation (flag, windsock), lit.</summary>
        FlagCloth,
        /// <summary>Animated water: scrolling bump, planar reflection, ripples. TextureSet = [bump, reflection].</summary>
        Water,
        /// <summary>Unlit billboard particles; vertex alpha in Normal.X; blended, depth-read-only.</summary>
        Particle,
    }

    /// <summary>
    /// How a surface is shaded: shader selection plus textures and parameters.
    /// Replaces fixed-function render/texture state (see CONTEXT.md).
    /// </summary>
    public sealed class Material
    {
        public MaterialKind Kind { get; set; } = MaterialKind.StandardLit;

        /// <summary>Diffuse texture; null renders with DiffuseColor only.</summary>
        public Texture2D Texture { get; set; }

        public Vector4 DiffuseColor { get; set; } = Vector4.One;

        /// <summary>
        /// Multi-texture kinds. TerrainSplat: [splatMask, detail1..detail4,
        /// normalMap]. PhotoPanel: [color, depthMap]. Registered contiguously
        /// in the SRV heap by the renderer.
        /// </summary>
        public Texture2D[] TextureSet { get; set; }

        /// <summary>TerrainSplat parameters (legacy splat.fx defaults).</summary>
        public float NearRepeat = 64f, FarRepeat = 16f;
        public float BlendSqDistance = 50f * 50f, BlendSqWidthInv = 1f / (200f * 200f);
        public float NearFactor2 = 1f, NearFactor3 = 1f, NearFactor4 = 5f;
        public float FarFactor2 = 1f, FarFactor3 = 1f, FarFactor4 = 5f;
        public float TerrainAmbient = 0.2f, TerrainSun = 0.6f;

        /// <summary>Water parameters (legacy WaterEffects.fx spirit).</summary>
        public float WaveLength = 0.1f, WaveHeight = 0.06f, WindForce = 0.1f;

        /// <summary>Up to two active ripple disturbances: (x, z, ageSeconds, strength).</summary>
        public Vector4 Ripple0, Ripple1;

        internal int SrvBaseSlot = -1;

        internal bool IsTransparent
        {
            get { return Kind == MaterialKind.TransparentLit || Kind == MaterialKind.Particle || Kind == MaterialKind.Water; }
        }

        public Material() { }

        public Material(Texture2D texture)
        {
            Texture = texture;
        }
    }
}
