using System;
using System.Numerics;
using Bonsai.Objects.Terrain;

namespace Bonsai.Graphics.Rendering
{
    /// <summary>Procedural meshes ported from the legacy engine (TerrainMesh, DomeMesh, SquareMesh).</summary>
    public static class PrimitiveMeshes
    {
        /// <summary>
        /// Heightmap terrain, identical vertex math to the legacy TerrainMesh:
        /// spans +-(heightmap.Size/2), row 0 at +Z, UV = textureScale across.
        /// </summary>
        public static Mesh BuildTerrain(GraphicsDevice device, Heightmap heightmap, float textureScale)
        {
            int xSub = heightmap.XSubdivisions;
            int ySub = heightmap.YSubdivisions;
            float size = heightmap.Size / 2;

            var vertices = new VertexPositionNormalTexture[(xSub + 1) * (ySub + 1)];
            int pos = 0;
            for (int row = 0; row <= ySub; row++)
            {
                for (int col = 0; col <= xSub; col++)
                {
                    float x = -size + (size * 2 / xSub) * col;
                    float z = size - (size * 2 / ySub) * row;
                    vertices[pos].Position = new Vector3(x, heightmap.GetHeightAt(row, col), z);
                    vertices[pos].Normal = heightmap.GetNormalAt(row, col);
                    vertices[pos].TexCoord = new Vector2((textureScale / xSub) * col, (textureScale / ySub) * row);
                    pos++;
                }
            }

            var indices = new uint[xSub * ySub * 6];
            int index = 0;
            for (int row = 0; row < ySub; row++)
            {
                for (int col = 0; col < xSub; col++)
                {
                    uint topLeft = (uint)(row * (xSub + 1) + col);
                    uint topRight = topLeft + 1;
                    uint bottomLeft = topLeft + (uint)(xSub + 1);
                    uint bottomRight = bottomLeft + 1;
                    indices[index++] = topLeft; indices[index++] = topRight; indices[index++] = bottomLeft;
                    indices[index++] = topRight; indices[index++] = bottomRight; indices[index++] = bottomLeft;
                }
            }
            return new Mesh(device, vertices, indices);
        }

        /// <summary>Upper-hemisphere sky dome, legacy DomeMesh UV mapping.</summary>
        public static Mesh BuildDome(GraphicsDevice device, float radius, int rings, int segments)
        {
            var vertices = new VertexPositionNormalTexture[(rings + 1) * (segments + 1)];
            float deltaRing = (float)Math.PI / 2 / rings;
            float deltaSeg = 2f * (float)Math.PI / segments;

            int pos = 0;
            for (int ring = 0; ring <= rings; ring++)
            {
                float r0 = (float)Math.Sin(ring * deltaRing);
                float y0 = (float)Math.Cos(ring * deltaRing);
                for (int seg = 0; seg <= segments; seg++)
                {
                    float x0 = r0 * (float)Math.Sin(seg * deltaSeg);
                    float z0 = r0 * (float)Math.Cos(seg * deltaSeg);
                    vertices[pos].Position = new Vector3(x0 * radius, y0 * radius, z0 * radius);
                    vertices[pos].Normal = Vector3.Normalize(-vertices[pos].Position);
                    vertices[pos].TexCoord = new Vector2((float)seg / segments, (float)ring / rings);
                    pos++;
                }
            }

            var indices = new uint[rings * segments * 6];
            int index = 0;
            for (int ring = 0; ring < rings; ring++)
            {
                for (int seg = 0; seg < segments; seg++)
                {
                    uint current = (uint)(ring * (segments + 1) + seg);
                    uint below = current + (uint)(segments + 1);
                    indices[index++] = current; indices[index++] = below; indices[index++] = current + 1;
                    indices[index++] = current + 1; indices[index++] = below; indices[index++] = below + 1;
                }
            }
            return new Mesh(device, vertices, indices);
        }

        /// <summary>
        /// A quad of half-extent size. axisU/axisV span the surface; vertices are
        /// center + (u * axisU + v * axisV) * size for u,v in [-1,1]; the normal
        /// is axisU x axisV. UV (0,0) maps to the (-u,+v) corner.
        /// </summary>
        public static Mesh BuildQuad(GraphicsDevice device, Vector3 center, Vector3 axisU, Vector3 axisV, float size)
        {
            Vector3 normal = Vector3.Normalize(Vector3.Cross(axisU, axisV));
            var vertices = new VertexPositionNormalTexture[4];
            vertices[0] = new VertexPositionNormalTexture { Position = center + (-axisU + axisV) * size, Normal = normal, TexCoord = new Vector2(0, 0) };
            vertices[1] = new VertexPositionNormalTexture { Position = center + (axisU + axisV) * size, Normal = normal, TexCoord = new Vector2(1, 0) };
            vertices[2] = new VertexPositionNormalTexture { Position = center + (axisU - axisV) * size, Normal = normal, TexCoord = new Vector2(1, 1) };
            vertices[3] = new VertexPositionNormalTexture { Position = center + (-axisU - axisV) * size, Normal = normal, TexCoord = new Vector2(0, 1) };
            var indices = new uint[] { 0, 1, 2, 0, 2, 3 };
            return new Mesh(device, vertices, indices);
        }
    }
}
