using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Assimp;
using Bonsai.Graphics.Rendering;
using Mesh = Bonsai.Graphics.Rendering.Mesh;
using Material = Bonsai.Graphics.Rendering.Material;

namespace Bonsai.Graphics.Assets
{
    /// <summary>
    /// Runtime mesh import through Assimp (ADR 0004): parses the existing .X
    /// content (and glTF/OBJ for free) and builds GPU meshes + materials,
    /// resolving referenced textures relative to the model file.
    /// </summary>
    public static class ModelImporter
    {
        private static readonly Dictionary<string, Texture2D> textureCache =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        public sealed class ImportedModel : IDisposable
        {
            public readonly List<(Mesh Mesh, Material Material)> Parts = new List<(Mesh, Material)>();

            public void Dispose()
            {
                foreach (var part in Parts)
                    part.Mesh.Dispose();
                Parts.Clear();
            }
        }

        public static ImportedModel Load(GraphicsDevice device, string path)
        {
            using (var context = new AssimpContext())
            {
                // MakeLeftHanded restores the authored D3D coordinate system:
                // Assimp converts .X (left-handed) to its right-handed
                // convention on import, which mirrors every model nose-to-tail
                // (and mirror-images decals). FlipWindingOrder keeps triangle
                // orientation consistent with the mirror.
                Assimp.Scene scene = context.ImportFile(path,
                    PostProcessSteps.Triangulate |
                    PostProcessSteps.GenerateSmoothNormals |
                    PostProcessSteps.PreTransformVertices |
                    PostProcessSteps.JoinIdenticalVertices |
                    PostProcessSteps.MakeLeftHanded |
                    PostProcessSteps.FlipWindingOrder);
                if (scene == null || scene.MeshCount == 0)
                    throw new InvalidDataException("No meshes imported from " + path);

                string modelDir = Path.GetDirectoryName(Path.GetFullPath(path));
                var model = new ImportedModel();

                foreach (Assimp.Mesh assimpMesh in scene.Meshes)
                {
                    var vertices = new VertexPositionNormalTexture[assimpMesh.VertexCount];
                    bool hasUv = assimpMesh.HasTextureCoords(0);
                    for (int i = 0; i < assimpMesh.VertexCount; i++)
                    {
                        Vector3D p = assimpMesh.Vertices[i];
                        Vector3D n = assimpMesh.HasNormals ? assimpMesh.Normals[i] : new Vector3D(0, 1, 0);
                        vertices[i].Position = new Vector3(p.X, p.Y, p.Z);
                        vertices[i].Normal = new Vector3(n.X, n.Y, n.Z);
                        if (hasUv)
                        {
                            Vector3D uv = assimpMesh.TextureCoordinateChannels[0][i];
                            vertices[i].TexCoord = new Vector2(uv.X, uv.Y);
                        }
                    }

                    uint[] indices = new uint[assimpMesh.FaceCount * 3];
                    int index = 0;
                    foreach (Face face in assimpMesh.Faces)
                    {
                        if (face.IndexCount != 3)
                            continue;
                        indices[index++] = (uint)face.Indices[0];
                        indices[index++] = (uint)face.Indices[1];
                        indices[index++] = (uint)face.Indices[2];
                    }
                    if (index != indices.Length)
                        Array.Resize(ref indices, index);

                    var material = new Material();
                    if (assimpMesh.MaterialIndex >= 0 && assimpMesh.MaterialIndex < scene.MaterialCount)
                    {
                        Assimp.Material assimpMaterial = scene.Materials[assimpMesh.MaterialIndex];
                        Color4D diffuse = assimpMaterial.ColorDiffuse;
                        material.DiffuseColor = new Vector4(diffuse.R, diffuse.G, diffuse.B, diffuse.A);
                        if (assimpMaterial.HasTextureDiffuse)
                        {
                            string texturePath = ResolveTexturePath(modelDir, assimpMaterial.TextureDiffuse.FilePath);
                            if (texturePath != null)
                                material.Texture = LoadCachedTexture(device, texturePath);
                        }
                    }

                    model.Parts.Add((new Mesh(device, vertices, indices), material));
                }
                return model;
            }
        }

        private static string ResolveTexturePath(string modelDir, string reference)
        {
            if (string.IsNullOrEmpty(reference))
                return null;
            string candidate = Path.Combine(modelDir, Path.GetFileName(reference));
            if (File.Exists(candidate))
                return candidate;
            candidate = Path.Combine(modelDir, reference);
            return File.Exists(candidate) ? candidate : null;
        }

        private static Texture2D LoadCachedTexture(GraphicsDevice device, string path)
        {
            Texture2D texture;
            if (!textureCache.TryGetValue(path, out texture))
            {
                texture = Texture2D.Load(device, path);
                textureCache.Add(path, texture);
            }
            return texture;
        }
    }
}
