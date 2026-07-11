using System.Numerics;

namespace Bonsai.Graphics.Rendering
{
    /// <summary>
    /// How a surface is shaded: shader selection (implicitly the standard lit
    /// PSO for now) plus its texture and parameters. Replaces fixed-function
    /// render/texture state (see CONTEXT.md).
    /// </summary>
    public sealed class Material
    {
        /// <summary>Diffuse texture; null renders with DiffuseColor only.</summary>
        public Texture2D Texture { get; set; }

        public Vector4 DiffuseColor { get; set; } = Vector4.One;

        public Material() { }

        public Material(Texture2D texture)
        {
            Texture = texture;
        }
    }
}
