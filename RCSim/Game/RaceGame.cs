using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Xml.Linq;
using Bonsai.Graphics;
using Bonsai.Graphics.Audio;
using Bonsai.Graphics.Rendering;
using Bonsai.Graphics.Scene;

namespace RCSim
{
    /// <summary>
    /// Pylon racing (legacy Race/Gate port): fly through the numbered gates in
    /// order; the first gate starts the clock, the last stops it. The crossing
    /// test is the legacy 2D segment intersection between the aircraft's frame
    /// path and the 6 m gate post line. A spinning arrow marks the next gate.
    /// </summary>
    internal sealed class RaceGame : IDisposable
    {
        private const float GateHeight = 6.0f;

        private sealed class GateInfo
        {
            public Vector3 Position;
            public int Sequence;
            public float X1, Y1, X2, Y2; // post line in the ground plane
        }

        private readonly List<GateInfo> gates = new List<GateInfo>();
        private readonly SceneNode arrow;
        private readonly Sound passSound;
        private Vector3 previousPosition;
        private bool hasPrevious;
        private int currentGate;
        private double startTime;
        private double statusUntil;

        public bool Racing { get; private set; }
        public int CurrentGate { get { return currentGate; } }
        public string StatusText { get; private set; }

        public RaceGame(GraphicsDevice device, SceneRenderer renderer, SceneNode world,
            string sceneryDir, string dataDir)
        {
            string terrainDef = Path.Combine(sceneryDir, "terrain.def");
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            foreach (XElement entry in XDocument.Load(terrainDef).Root.Elements("Gates"))
            {
                XElement p = entry.Element("Position");
                XElement o = entry.Element("Orientation");
                var gate = new GateInfo
                {
                    Position = new Vector3(
                        float.Parse(p.Element("X").Value, ic),
                        float.Parse(p.Element("Y").Value, ic),
                        float.Parse(p.Element("Z").Value, ic)),
                    Sequence = int.Parse(entry.Element("SequenceNr").Value, ic),
                };
                float yaw = float.Parse(o.Element("Y").Value, ic);
                // Legacy post positions: +-3 m along (cos yaw, -sin yaw).
                gate.X1 = gate.Position.X + 3f * (float)Math.Cos(yaw);
                gate.Y1 = gate.Position.Z - 3f * (float)Math.Sin(yaw);
                gate.X2 = gate.Position.X - 3f * (float)Math.Cos(yaw);
                gate.Y2 = gate.Position.Z + 3f * (float)Math.Sin(yaw);
                gates.Add(gate);
            }
            gates.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));

            // Next-gate marker.
            string arrowFile = Path.Combine(dataDir, "arrow.x");
            if (File.Exists(arrowFile))
            {
                var model = Bonsai.Graphics.Assets.ModelImporter.Load(device, arrowFile);
                arrow = new SceneNode("race_arrow");
                foreach (var (mesh, material) in model.Parts)
                {
                    if (material.Texture != null)
                        renderer.RegisterTexture(material.Texture);
                    arrow.AddChild(new SceneNode { Mesh = mesh, Material = material });
                }
                world.AddChild(arrow);
            }

            string wav = Path.Combine(dataDir, "gate.wav");
            if (File.Exists(wav) && AudioEngine.IsInitialized)
                passSound = new Sound(wav);

            Restart(0);
        }

        public void Restart(double time)
        {
            Racing = false;
            currentGate = 0;
            hasPrevious = false;
            ShowText("Fly through the first gate to start the clock", time, 1000);
        }

        /// <summary>Per-frame: crossing detection + arrow/status updates.</summary>
        public void Update(Vector3 aircraftPosition, double time)
        {
            if (gates.Count == 0)
                return;

            if (hasPrevious && currentGate < gates.Count)
            {
                GateInfo gate = gates[currentGate];
                if (Crosses(gate, previousPosition, aircraftPosition))
                    GatePassed(time);
            }
            previousPosition = aircraftPosition;
            hasPrevious = true;

            if (arrow != null && currentGate < gates.Count)
            {
                GateInfo next = gates[currentGate];
                arrow.LocalTransform = Matrix4x4.CreateRotationY((float)time) *
                    Matrix4x4.CreateTranslation(next.Position + new Vector3(0, GateHeight, 0));
            }

            if (Racing)
            {
                TimeSpan ts = TimeSpan.FromSeconds(time - startTime);
                StatusText = string.Format("Your time: {0}:{1}.{2}",
                    (int)Math.Floor(ts.TotalMinutes), ts.Seconds.ToString("00"), ts.Milliseconds.ToString("000"));
                statusUntil = time + 1;
            }
            else if (time > statusUntil)
            {
                StatusText = null;
            }
        }

        private void GatePassed(double time)
        {
            if (passSound != null)
                passSound.Play(false);

            if (currentGate == 0)
            {
                Racing = true;
                startTime = time;
                currentGate = 1;
            }
            else if (currentGate == gates.Count - 1)
            {
                Racing = false;
                TimeSpan ts = TimeSpan.FromSeconds(time - startTime);
                ShowText(string.Format("You finished in {0}:{1}.{2}",
                    (int)Math.Floor(ts.TotalMinutes), ts.Seconds.ToString("00"), ts.Milliseconds.ToString("000")), time, 10);
                currentGate = 0;
            }
            else
            {
                currentGate++;
            }
        }

        private void ShowText(string text, double time, double seconds)
        {
            StatusText = text;
            statusUntil = time + seconds;
        }

        /// <summary>Legacy Gate.OnFrameMove test: the path segment crosses the
        /// post line, below gate height.</summary>
        private static bool Crosses(GateInfo gate, Vector3 from, Vector3 to)
        {
            float xa = to.X, ya = to.Z, xb = from.X, yb = from.Z;
            return CCW(gate.X1, gate.Y1, xa, ya, xb, yb) != CCW(gate.X2, gate.Y2, xa, ya, xb, yb)
                && CCW(gate.X1, gate.Y1, gate.X2, gate.Y2, xa, ya) != CCW(gate.X1, gate.Y1, gate.X2, gate.Y2, xb, yb)
                && to.Y < gate.Position.Y + GateHeight;
        }

        private static bool CCW(float xa, float ya, float xb, float yb, float xc, float yc)
        {
            return (yc - ya) * (xb - xa) > (yb - ya) * (xc - xa);
        }

        public void Dispose()
        {
            if (passSound != null)
                passSound.Dispose();
            if (arrow != null && arrow.Parent != null)
                arrow.Parent.RemoveChild(arrow);
        }
    }
}
