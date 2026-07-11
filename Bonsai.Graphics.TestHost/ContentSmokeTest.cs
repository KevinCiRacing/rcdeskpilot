using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using Bonsai.Graphics;
using Bonsai.Graphics.Assets;
using Bonsai.Graphics.Audio;
using Bonsai.Graphics.Rendering;
using Bonsai.Objects.Terrain;
using RCSim.DataClasses;

namespace Bonsai.Graphics.TestHost
{
    /// <summary>
    /// Issue 14: content smoke tests. Loads every stock aircraft and scenery
    /// .par through the real pipeline — parameter parse, Assimp mesh import,
    /// texture upload (headless DX12 device), wav decode — and reports every
    /// breakage with the asset, the referencing .par, and the reason.
    ///
    /// References the legacy engine tolerated when the file is absent
    /// (control surfaces, icons, sounds, placed scenery objects) are reported
    /// as skips, not failures; a file that exists but fails to load always
    /// fails the suite.
    /// </summary>
    internal static class ContentSmokeTest
    {
        private sealed class Report
        {
            public readonly List<string> Failures = new List<string>();
            public readonly List<string> Skips = new List<string>();
            public int Meshes, Textures, Sounds;

            public void Fail(string referencedBy, string asset, string reason)
            {
                Failures.Add(string.Format("{0}\n    referenced by {1}\n    {2}", asset, referencedBy, reason));
            }

            public void Skip(string referencedBy, string asset)
            {
                Skips.Add(string.Format("{0} (referenced by {1})", asset, referencedBy));
            }
        }

        public static int Run(string repoRoot)
        {
            string aircraftRoot = Path.Combine(repoRoot, "RCSim", "Aircraft");
            string sceneryRoot = Path.Combine(repoRoot, "RCSim", "data", "scenery");
            string dataDir = Path.Combine(repoRoot, "RCSim", "data");

            var report = new Report();
            int aircraftCount = 0, sceneryCount = 0;

            using (var device = new GraphicsDevice(enableDebugLayer: true))
            {
                AudioEngine.Initialize();
                try
                {
                    foreach (string par in Directory.GetFiles(aircraftRoot, "*.par", SearchOption.AllDirectories))
                    {
                        aircraftCount++;
                        Console.WriteLine("aircraft: {0}", RelativeName(repoRoot, par));
                        CheckAircraft(device, par, report);
                    }

                    foreach (string sceneryDir in Directory.GetDirectories(sceneryRoot))
                    {
                        string[] pars = Directory.GetFiles(sceneryDir, "*.par");
                        if (pars.Length == 0)
                            continue;
                        sceneryCount++;
                        Console.WriteLine("scenery : {0}", RelativeName(repoRoot, pars[0]));
                        CheckScenery(device, pars[0], sceneryDir, dataDir, report);
                    }
                }
                finally
                {
                    AudioEngine.Shutdown();
                }

                device.WaitIdle();
                int debugErrors = device.ReportDebugMessages();

                Console.WriteLine();
                Console.WriteLine("aircraft .par   : {0}", aircraftCount);
                Console.WriteLine("scenery .par    : {0}", sceneryCount);
                Console.WriteLine("meshes loaded   : {0}", report.Meshes);
                Console.WriteLine("textures loaded : {0}", report.Textures);
                Console.WriteLine("sounds loaded   : {0}", report.Sounds);
                Console.WriteLine("debug errors    : {0}", debugErrors);

                if (report.Skips.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("skipped missing references ({0}):", report.Skips.Count);
                    foreach (string skip in report.Skips)
                        Console.WriteLine("  - {0}", skip);
                }

                if (report.Failures.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("FAILURES ({0}):", report.Failures.Count);
                    foreach (string failure in report.Failures)
                        Console.WriteLine("  - {0}", failure);
                }

                bool pass = report.Failures.Count == 0 && debugErrors == 0 &&
                            aircraftCount > 0 && sceneryCount > 0;
                Console.WriteLine();
                Console.WriteLine(pass ? "CONTENTTEST PASS" : "CONTENTTEST FAIL");
                return pass ? 0 : 1;
            }
        }

        #region Aircraft
        private static void CheckAircraft(GraphicsDevice device, string par, Report report)
        {
            var parameters = new AircraftParameters();
            try
            {
                parameters.ReadParameters(par);
            }
            catch (Exception e)
            {
                report.Fail(par, par, "parameter parse failed: " + e.Message);
                return;
            }

            string dir = Path.GetDirectoryName(par);

            // Meshes: the fixed part (required) plus every control surface (optional files).
            if (string.IsNullOrEmpty(parameters.FixedMesh))
                report.Fail(par, "<fixedmesh>", "no fixed mesh declared");
            else
                CheckMesh(device, par, Path.Combine(dir, parameters.FixedMesh), required: true, report);
            if (parameters.ControlSurfaces != null)
                foreach (AircraftParameters.ControlSurface surface in parameters.ControlSurfaces)
                    CheckSurfaceMeshes(device, par, dir, surface, report);

            // Sounds (legacy resolution: filename portion, aircraft folder).
            CheckSound(par, dir, parameters.EngineSound, report);
            CheckSound(par, dir, parameters.RotorSound, report);

            // Menu icon.
            if (!string.IsNullOrEmpty(parameters.IconFile))
            {
                string icon = Path.Combine(dir, Path.GetFileName(parameters.IconFile));
                if (!File.Exists(icon))
                    report.Skip(par, icon);
                else
                    CheckTexture(device, par, icon, report);
            }
        }

        private static void CheckSurfaceMeshes(GraphicsDevice device, string par, string dir,
            AircraftParameters.ControlSurface surface, Report report)
        {
            if (!string.IsNullOrEmpty(surface.Filename))
            {
                string path = Path.Combine(dir, surface.Filename);
                if (!File.Exists(path))
                    report.Skip(par, path); // legacy AddSurface skips absent surface files
                else
                    CheckMesh(device, par, path, required: true, report);
            }
            if (surface.ChildControlSurfaces != null)
                foreach (AircraftParameters.ControlSurface child in surface.ChildControlSurfaces)
                    CheckSurfaceMeshes(device, par, dir, child, report);
        }

        private static void CheckSound(string par, string dir, string reference, Report report)
        {
            if (string.IsNullOrEmpty(reference))
                return;
            string path = Path.Combine(dir, Path.GetFileName(reference));
            if (!File.Exists(path))
            {
                report.Skip(par, path);
                return;
            }
            try
            {
                using (var sound = new Sound(path)) { }
                report.Sounds++;
            }
            catch (Exception e)
            {
                report.Fail(par, path, "sound load failed: " + e.Message);
            }
        }
        #endregion

        #region Scenery
        private static void CheckScenery(GraphicsDevice device, string par, string sceneryDir, string dataDir, Report report)
        {
            XElement definition;
            try
            {
                definition = XDocument.Load(par).Root.Element("definition");
                if (definition == null)
                    throw new InvalidDataException("no <definition> element");
            }
            catch (Exception e)
            {
                report.Fail(par, par, "scenery parse failed: " + e.Message);
                return;
            }
            string Value(string name) { var e = definition.Element(name); return e != null ? e.Value : null; }
            string Resolve(string reference) { return ResolveSceneryPath(sceneryDir, dataDir, reference); }

            if (Value("type") == "photo")
            {
                foreach (string face in new[] { "front", "back", "left", "right", "top", "bottom" })
                {
                    CheckSceneryTexture(device, par, Resolve(Value(face)), report);
                    CheckSceneryTexture(device, par, Resolve(Value(face + "depthmap")), report);
                }
                return;
            }

            // Default-style scenery: heightmap + splat set + sky.
            string heightmap = Resolve(Value("heightmap"));
            if (heightmap == null)
                report.Fail(par, Value("heightmap") ?? "<heightmap>", "heightmap missing");
            else
            {
                try
                {
                    var map = new Heightmap(heightmap, 1000f, 100, 100);
                    map.GetHeightAt(0f, 0f);
                }
                catch (Exception e)
                {
                    report.Fail(par, heightmap, "heightmap load failed: " + e.Message);
                }
            }

            foreach (string name in new[] { "splatlow", "splathigh", "normalmap", "texture1", "texture2", "texture3", "texture4" })
                CheckSceneryTexture(device, par, Resolve(Value(name)), report);
            CheckSceneryTexture(device, par, Path.Combine(sceneryDir, "sky_sunny.jpg"), report);

            // terrain.def: tree billboards (shared textures in data/) and placed meshes.
            string terrainDef = Resolve(Value("definition"));
            if (terrainDef == null)
                return;
            XElement def;
            try
            {
                def = XDocument.Load(terrainDef).Root;
            }
            catch (Exception e)
            {
                report.Fail(par, terrainDef, "terrain.def parse failed: " + e.Message);
                return;
            }

            foreach (string texture in new[] { "tall_tree1_256.png", "tree1_256.png", "small_tree1_256.png" })
                CheckSceneryTexture(device, par, Path.Combine(dataDir, texture), report);

            var meshFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement entry in def.Elements("Objects"))
                meshFiles.Add(Path.Combine(dataDir, entry.Element("FileName").Value));
            if (def.Element("Gates") != null)
                meshFiles.Add(Path.Combine(dataDir, "gate1.x"));
            if (def.Element("Windmills") != null)
            {
                meshFiles.Add(Path.Combine(dataDir, "windmill_fixed.x"));
                meshFiles.Add(Path.Combine(dataDir, "windmill_blades.x"));
            }
            foreach (string mesh in meshFiles)
            {
                if (!File.Exists(mesh))
                    report.Skip(par, mesh); // SceneryDemo skips removed content (e.g. the old ad billboard)
                else
                    CheckMesh(device, par, mesh, required: true, report);
            }
        }

        private static string ResolveSceneryPath(string sceneryDir, string dataDir, string reference)
        {
            if (string.IsNullOrEmpty(reference))
                return null;
            // Values are either scenery-relative filenames or repo-data paths like "data/scenery/default/x.jpg".
            string local = Path.Combine(sceneryDir, Path.GetFileName(reference.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(local))
                return local;
            string fromData = Path.Combine(Path.GetDirectoryName(dataDir), reference.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(fromData) ? fromData : null;
        }

        private static void CheckSceneryTexture(GraphicsDevice device, string par, string path, Report report)
        {
            if (path == null)
                return; // absent optional element
            if (!File.Exists(path))
            {
                report.Skip(par, path);
                return;
            }
            CheckTexture(device, par, path, report);
        }
        #endregion

        #region Shared
        private static void CheckMesh(GraphicsDevice device, string par, string path, bool required, Report report)
        {
            if (!File.Exists(path))
            {
                if (required)
                    report.Fail(par, path, "mesh file missing");
                else
                    report.Skip(par, path);
                return;
            }
            try
            {
                using (ModelImporter.ImportedModel model = ModelImporter.Load(device, path))
                {
                    report.Meshes++;
                    foreach (var part in model.Parts)
                        if (part.Material.Texture != null)
                            report.Textures++;
                }
            }
            catch (Exception e)
            {
                report.Fail(par, path, "mesh import failed: " + e.Message);
            }
        }

        private static void CheckTexture(GraphicsDevice device, string par, string path, Report report)
        {
            try
            {
                Texture2D.Load(device, path).Dispose();
                report.Textures++;
            }
            catch (Exception e)
            {
                report.Fail(par, path, "texture load failed: " + e.Message);
            }
        }

        private static string RelativeName(string repoRoot, string path)
        {
            return path.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(repoRoot.Length + 1) : path;
        }
        #endregion
    }
}
