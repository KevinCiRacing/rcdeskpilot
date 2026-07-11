using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Xml.Linq;
using Bonsai.Graphics;
using Bonsai.Graphics.Assets;
using Bonsai.Graphics.Rendering;
using Bonsai.Graphics.Scene;
using Bonsai.Objects.Terrain;

namespace RCSim
{
    /// <summary>
    /// Builds the stock scenery worlds from unmodified .par / terrain.def
    /// content: the default field (splatted terrain, sky dome, tree
    /// billboards, placed .x objects, windmills) and photo sceneries
    /// (photo box with depth occlusion).
    /// </summary>
    internal static class SceneryBuilder
    {
        internal static Heightmap BuildDefaultScenery(GraphicsDevice device, SceneRenderer renderer,
            SceneNode root, string sceneryDir, string dataDir, List<SceneNode> billboards, out SceneNode windmillBlades)
        {
            XElement definition = XDocument.Load(Path.Combine(sceneryDir, "default.par")).Root.Element("definition");
            string Value(string name) { var e = definition.Element(name); return e != null ? e.Value : null; }

            // Terrain: heightmap + splat material (legacy sizes: 1000m, 100x100 grid).
            var heightmap = new Heightmap(Path.Combine(sceneryDir, Value("heightmap")), 1000f, 100, 100)
            {
                MinHeight = float.Parse(Value("minimumheight"), System.Globalization.CultureInfo.InvariantCulture),
                MaxHeight = float.Parse(Value("maximumheight"), System.Globalization.CultureInfo.InvariantCulture),
            };
            var terrainMaterial = new Material
            {
                Kind = MaterialKind.TerrainSplat,
                TextureSet = new[]
                {
                    Texture2D.Load(device, RepoPath(sceneryDir, Value("splatlow"))),
                    Texture2D.Load(device, RepoPath(sceneryDir, Value("texture1"))),
                    Texture2D.Load(device, RepoPath(sceneryDir, Value("texture2"))),
                    Texture2D.Load(device, RepoPath(sceneryDir, Value("texture3"))),
                    Texture2D.Load(device, RepoPath(sceneryDir, Value("texture4"))),
                    Texture2D.Load(device, RepoPath(sceneryDir, Value("normalmap"))),
                },
            };
            renderer.RegisterMaterial(terrainMaterial);
            root.AddChild(new SceneNode("terrain")
            {
                Mesh = PrimitiveMeshes.BuildTerrain(device, heightmap, 1f),
                Material = terrainMaterial,
            });

            // Sky dome (legacy: radius 4500, 16x16, sunny afternoon).
            root.AddChild(new SceneNode("sky")
            {
                Mesh = PrimitiveMeshes.BuildDome(device, 4500f, 16, 16),
                Material = new Material(Texture2D.Load(device, Path.Combine(sceneryDir, "sky_sunny.jpg"))) { Kind = MaterialKind.Unlit },
            });

            // terrain.def content
            XElement terrainDef = XDocument.Load(Path.Combine(sceneryDir, Value("definition"))).Root;

            var treeKinds = new (string element, string texture, float halfSize)[]
            {
                ("Trees", "tall_tree1_256.png", 5f),
                ("SimpleTrees", "tree1_256.png", 2.5f),
                ("SimpleTallTrees", "tall_tree1_256.png", 4f),
                ("SimpleSmallTrees", "small_tree1_256.png", 1.5f),
            };
            foreach (var (element, texture, halfSize) in treeKinds)
            {
                var material = new Material(Texture2D.Load(device, Path.Combine(dataDir, texture))) { Kind = MaterialKind.CutoutLit };
                Mesh quad = PrimitiveMeshes.BuildQuad(device, new Vector3(0, halfSize, 0), Vector3.UnitX, Vector3.UnitY, halfSize);
                foreach (XElement entry in terrainDef.Elements(element))
                {
                    Vector3 position = ReadVector(entry.Element("Position"));
                    var node = new SceneNode(element) { Mesh = quad, Material = material, LocalTransform = Matrix4x4.CreateTranslation(position) };
                    root.AddChild(node);
                    billboards.Add(node);
                }
            }

            // Placed .x objects (skip files removed from the content, e.g. the old ad billboard).
            foreach (XElement entry in terrainDef.Elements("Objects"))
            {
                string file = Path.Combine(dataDir, entry.Element("FileName").Value);
                if (!File.Exists(file))
                {
                    Console.WriteLine("skipping missing object {0}", Path.GetFileName(file));
                    continue;
                }
                Vector3 position = ReadVector(entry.Element("Position"));
                Vector3 orientation = ReadVector(entry.Element("Orientation"));
                SceneNode node = LoadModelNode(device, renderer, file);
                node.LocalTransform = Matrix4x4.CreateFromYawPitchRoll(orientation.Y, orientation.X, orientation.Z)
                    * Matrix4x4.CreateTranslation(position);
                root.AddChild(node);
            }

            // Gates
            var gateFile = Path.Combine(dataDir, "gate1.x");
            foreach (XElement entry in terrainDef.Elements("Gates"))
            {
                Vector3 position = ReadVector(entry.Element("Position"));
                Vector3 orientation = ReadVector(entry.Element("Orientation"));
                SceneNode node = LoadModelNode(device, renderer, gateFile);
                node.LocalTransform = Matrix4x4.CreateFromYawPitchRoll(orientation.Y, orientation.X, orientation.Z)
                    * Matrix4x4.CreateTranslation(position);
                root.AddChild(node);
            }

            // Windmills: fixed tower + spinning blades child.
            windmillBlades = null;
            foreach (XElement entry in terrainDef.Elements("Windmills"))
            {
                Vector3 position = ReadVector(entry.Element("Position"));
                SceneNode tower = LoadModelNode(device, renderer, Path.Combine(dataDir, "windmill_fixed.x"));
                SceneNode blades = LoadModelNode(device, renderer, Path.Combine(dataDir, "windmill_blades.x"));
                tower.AddChild(blades);
                tower.LocalTransform = Matrix4x4.CreateTranslation(position);
                root.AddChild(tower);
                if (windmillBlades == null)
                    windmillBlades = blades.Children.Count > 0 ? blades.Children[0] : null;
            }
            // The blades node we animate is the mesh-bearing child.
            return heightmap;
        }

        internal static void BuildPhotoScenery(GraphicsDevice device, SceneRenderer renderer, SceneNode root, string sceneryDir)
        {
            XElement definition = XDocument.Load(Directory.GetFiles(sceneryDir, "*.par")[0]).Root.Element("definition");
            string Value(string name) { var e = definition.Element(name); return e != null ? e.Value : null; }

            const float s = 1000f;
            // face: color, depth (may be null), quad center, axisU (right, +U), axisV (up, +V)
            var faces = new (string color, string depth, Vector3 center, Vector3 u, Vector3 v)[]
            {
                (Value("front"), Value("frontdepthmap"), new Vector3(0, 0, s), -Vector3.UnitX, Vector3.UnitY),
                (Value("back"), Value("backdepthmap"), new Vector3(0, 0, -s), Vector3.UnitX, Vector3.UnitY),
                (Value("right"), Value("rightdepthmap"), new Vector3(s, 0, 0), Vector3.UnitZ, Vector3.UnitY),
                (Value("left"), Value("leftdepthmap"), new Vector3(-s, 0, 0), -Vector3.UnitZ, Vector3.UnitY),
                (Value("top"), Value("topdepthmap"), new Vector3(0, s, 0), -Vector3.UnitX, Vector3.UnitZ),
                (Value("bottom"), Value("bottomdepthmap"), new Vector3(0, -s, 0), -Vector3.UnitX, -Vector3.UnitZ),
            };

            foreach (var face in faces)
            {
                if (face.color == null)
                    continue;
                Texture2D color = Texture2D.Load(device, Path.Combine(sceneryDir, face.color));
                Material material;
                if (face.depth != null)
                {
                    material = new Material
                    {
                        Kind = MaterialKind.PhotoPanel,
                        TextureSet = new[] { color, Texture2D.Load(device, Path.Combine(sceneryDir, face.depth)) },
                    };
                    renderer.RegisterMaterial(material);
                }
                else
                {
                    material = new Material(color) { Kind = MaterialKind.Unlit };
                    renderer.RegisterTexture(color);
                }
                root.AddChild(new SceneNode("photo_" + face.color)
                {
                    Mesh = PrimitiveMeshes.BuildQuad(device, face.center, face.u, face.v, s),
                    Material = material,
                });
            }
        }

        internal static SceneNode LoadModelNode(GraphicsDevice device, SceneRenderer renderer, string file)
        {
            var node = new SceneNode(Path.GetFileNameWithoutExtension(file));
            ModelImporter.ImportedModel model = ModelImporter.Load(device, file);
            foreach (var (mesh, material) in model.Parts)
            {
                if (material.Texture != null)
                    renderer.RegisterTexture(material.Texture);
                node.AddChild(new SceneNode { Mesh = mesh, Material = material });
            }
            return node;
        }

        internal static string RepoPath(string sceneryDir, string reference)
        {
            // .par values are either scenery-relative filenames or repo-data paths like "data/scenery/default/x.jpg".
            string local = Path.Combine(sceneryDir, Path.GetFileName(reference.Replace('/', '\\')));
            return File.Exists(local) ? local : reference;
        }

        internal static Vector3 ReadVector(XElement element)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            return new Vector3(
                float.Parse(element.Element("X").Value, ic),
                float.Parse(element.Element("Y").Value, ic),
                float.Parse(element.Element("Z").Value, ic));
        }
    }
}
