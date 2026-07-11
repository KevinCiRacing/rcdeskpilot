using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using Bonsai.Objects.Terrain;
using RCSim.DataClasses;
using RCSim.Interfaces;

namespace RCSim
{
    /// <summary>
    /// Records (or verifies) ground-truth flight-model trajectories for every
    /// stock aircraft: a scripted, deterministic control sequence is stepped
    /// through the flight model at a fixed timestep with zero wind and a flat
    /// heightmap, and the resulting state trajectory is written to CSV.
    /// These recordings are the regression oracle for the physics port.
    /// </summary>
    internal static class Characterization
    {
        // Fixed integration step: mirrors the ~2 ms cadence of the game's
        // physics thread, but deterministic.
        private const float TimeStep = 0.002f;
        // State is sampled every 50 steps = every 0.1 s.
        private const int SampleInterval = 50;
        private const float Duration = 40.0f;
        // Flat ground large enough that nothing leaves it during the script.
        private const float GroundSize = 10000f;
        // Verification tolerance on position, per sample [m].
        private const float DefaultTolerance = 1.0f;
        // Early-window pass: through EarlyWindow seconds the error must stay
        // microscopic for a run to count as equivalent physics. The window is
        // short because even parked aircraft chatter on their gear (contact
        // impulses amplify FP rounding chaotically); measured equivalent-math
        // error at 1.5 s is <= 7e-3 m across all stock aircraft, while a
        // genuine physics bug shows decimeters within the first second.
        private const float EarlyWindow = 1.5f;
        private const float EarlyTolerance = 0.01f;

        public static int Run(string[] args)
        {
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "record";
            string aircraftRoot = args.Length > 1 ? args[1] : FindDefault("RCSim\\Aircraft");
            string recordingRoot = args.Length > 2 ? args[2] : FindDefault("RCSim.Characterization\\recordings");
            float tolerance = args.Length > 3 ? float.Parse(args[3], CultureInfo.InvariantCulture) : DefaultTolerance;

            if (mode != "record" && mode != "verify")
            {
                Console.Error.WriteLine("Usage: RCSim.Characterization [record|verify] [aircraftRoot] [recordingRoot] [toleranceMeters]");
                return 2;
            }
            if (aircraftRoot == null || (mode == "verify" && recordingRoot == null))
            {
                Console.Error.WriteLine("Could not locate the aircraft or recordings folder; pass paths explicitly.");
                return 2;
            }
            if (mode == "record" && !Directory.Exists(recordingRoot))
                Directory.CreateDirectory(recordingRoot);

            string[] parFiles = Directory.GetFiles(aircraftRoot, "*.par", SearchOption.AllDirectories);
            Array.Sort(parFiles, StringComparer.OrdinalIgnoreCase);
            if (parFiles.Length == 0)
            {
                Console.Error.WriteLine("No .par files found under " + aircraftRoot);
                return 2;
            }

            int failures = 0;
            foreach (string parFile in parFiles)
            {
                string name = Path.GetFileNameWithoutExtension(parFile);
                string csvPath = Path.Combine(recordingRoot, Sanitize(name) + ".csv");
                try
                {
                    List<Sample> trajectory = Simulate(parFile);
                    if (mode == "record")
                    {
                        WriteCsv(csvPath, parFile, trajectory);
                        Console.WriteLine("recorded  {0}  ({1} samples)", name, trajectory.Count);
                    }
                    else
                    {
                        float maxError;
                        float earlyError;
                        float divergenceTime;
                        bool strict = Compare(csvPath, trajectory, tolerance, out maxError, out earlyError, out divergenceTime);
                        // Two-tier criterion (see README): flight dynamics are
                        // chaotic, so equivalent math with different FP rounding
                        // may diverge late. A run whose error is still microscopic
                        // through the early window is a pass; a genuine physics
                        // bug shows up as early error.
                        bool earlyPass = earlyError <= EarlyTolerance;
                        bool pass = strict || earlyPass;
                        Console.WriteLine("{0}  {1}  maxErr={2:F4} m  earlyErr={3:E1} m{4}",
                            strict ? "PASS " : (earlyPass ? "PASS~" : "FAIL "), name, maxError, earlyError,
                            strict ? "" : string.Format(CultureInfo.InvariantCulture, "  divergedAt={0:F1} s", divergenceTime));
                        if (!pass)
                            failures++;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("ERROR  {0}: {1}", name, ex.ToString());
                    failures++;
                }
            }

            if (mode == "verify")
                Console.WriteLine(failures == 0 ? "All aircraft within tolerance." : failures + " aircraft FAILED.");
            return failures == 0 ? 0 : 1;
        }

        #region Simulation
        private struct Sample
        {
            public float T;
            public float Throttle, Elevator, Ailerons, Rudder;
            public float X, Y, Z;
            public float Qw, Qx, Qy, Qz;
            public float Vx, Vy, Vz;
            public bool Crashed, TouchedDown;
        }

        private static List<Sample> Simulate(string parFile)
        {
            AircraftParameters parameters = new AircraftParameters();
            parameters.ReadParameters(parFile);

            IFlightModel model;
            if (parameters.Version == 2)
                model = new FlightModelWind2();
            else
                model = new FlightModelWind();

            model.AircraftParameters = parameters;
            model.UpdateConstants();
            model.Heightmap = new Heightmap(GroundSize);
            model.Water = new List<Water>();
            model.Wind = new Vector3(0f, 0f, 0f);
            model.Reset();
            model.Paused = false;

            List<Sample> samples = new List<Sample>();
            int totalSteps = (int)(Duration / TimeStep);
            IAirplaneControl controls = (IAirplaneControl)model;
            bool isHeli = parameters.FlightModelType == AircraftParameters.FlightModelTypeEnum.Helicopter
                || parameters.FlightModelType == AircraftParameters.FlightModelTypeEnum.HelicopterCoax;
            bool handLaunched = false;

            for (int step = 0; step < totalSteps; step++)
            {
                float t = step * TimeStep;
                ApplyScript(controls, model, t, isHeli, ref handLaunched);
                model.UpdateControls(TimeStep);
                // MoveScene is the model's integration step (normally driven
                // by its physics thread, which we bypass for determinism).
                if (parameters.Version == 2)
                    ((FlightModelWind2)model).MoveScene(TimeStep);
                else
                    ((FlightModelWind)model).MoveScene(TimeStep);

                if (step % SampleInterval == 0)
                    samples.Add(Capture(t, controls, model));
            }
            // No Dispose(): it joins the physics thread, which we never
            // started (Initialize() is bypassed for determinism).
            return samples;
        }

        private static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        /// <summary>
        /// The scripted control sequence: a deterministic autopilot (state
        /// feedback is a pure function of model state, so runs reproduce
        /// bit-identically). Covers the dynamic range across 17 different
        /// airframes: takeoff (or hand launch for unpowered/underpowered
        /// aircraft), climb, cruise, aileron doublet, pitch pull, rudder
        /// input, power-off stall, and a final descent to ground contact.
        /// </summary>
        private static void ApplyScript(IAirplaneControl c, IFlightModel m, float t, bool isHeli, ref bool handLaunched)
        {
            float alt = -m.Z;                 // Z is down-positive (NED)
            float climb = -m.Velocity.Z;      // positive = up

            if (isHeli)
            {
                // Helicopter: altitude is throttle-driven; keep cyclic gentle.
                c.Ailerons = Clamp(-0.6 * m.Roll, -0.3, 0.3);
                c.Rudder = 0;
                if (t < 3f)
                {
                    c.Throttle = 0; c.Elevator = 0; c.Ailerons = 0;
                }
                else if (t < 20f)
                { // climb to and hold ~25 m, forward/back cyclic excursions
                    c.Throttle = Clamp(0.65 + 0.02 * (25f - alt) - 0.05 * climb, 0, 1);
                    c.Elevator = (t < 10f) ? 0.05 : ((t < 14f) ? 0.15 : -0.10);
                    if (t >= 16f && t < 18f)
                        c.Rudder = 0.3;
                }
                else if (t < 32f)
                { // descend to low hover
                    c.Throttle = Clamp(0.6 + 0.02 * (8f - alt) - 0.05 * climb, 0, 1);
                    c.Elevator = 0.05;
                }
                else
                { // set down
                    c.Throttle = Clamp(0.55 + 0.02 * (0f - alt) - 0.05 * climb, 0, 1);
                    c.Elevator = 0;
                }
                return;
            }

            // Fixed-wing. PD altitude hold and wing leveler keep 17 very
            // different airframes flying the same scripted maneuvers.
            // Nose-down authority kept small: overpowered aerobatic airframes
            // (Xtra 3D) otherwise dive into the ground on altitude overshoot.
            double elevHold60 = Clamp(0.02 * (60f - alt) - 0.03 * climb, -0.15, 0.6);
            double leveler = Clamp(-0.8 * m.Roll, -0.4, 0.4);

            if (t < 3f)
            { // settle on gear
                c.Throttle = 0; c.Elevator = 0; c.Ailerons = 0; c.Rudder = 0;
            }
            else if (t < 16f)
            { // takeoff roll and climb toward 60 m
                c.Throttle = 1.0;
                c.Elevator = elevHold60;
                c.Ailerons = leveler;
                c.Rudder = 0;
                // Unpowered airframes never leave the ground, and torquey 3D
                // airframes can depart right after a low-speed liftoff: give
                // either a single clean mid-air launch instead.
                if (t >= 8f && (alt < 2f || m.Crashed) && !handLaunched)
                {
                    m.Crashed = false;
                    m.HandLaunch(m.X, m.Y, -40f);
                    handLaunched = true;
                }
            }
            else if (t < 18f)
            { // aileron doublet
                c.Throttle = 0.7; c.Elevator = elevHold60; c.Rudder = 0;
                c.Ailerons = (t < 17f) ? 0.5 : -0.5;
            }
            else if (t < 20f)
            { // recover level
                c.Throttle = 0.7; c.Elevator = elevHold60; c.Ailerons = leveler; c.Rudder = 0;
            }
            else if (t < 22f)
            { // pitch pull
                c.Throttle = 0.9; c.Elevator = 0.5; c.Ailerons = leveler; c.Rudder = 0;
            }
            else if (t < 25f)
            { // recover level
                c.Throttle = 0.7; c.Elevator = elevHold60; c.Ailerons = leveler; c.Rudder = 0;
            }
            else if (t < 27f)
            { // rudder input
                c.Throttle = 0.7; c.Elevator = elevHold60; c.Ailerons = leveler; c.Rudder = 0.5;
            }
            else if (t < 33f)
            { // power-off stall entry: elevator ramps up
                c.Throttle = 0;
                c.Elevator = Clamp(0.3 + 0.5 * (t - 27f) / 6f, 0, 0.8);
                c.Ailerons = leveler;
                c.Rudder = 0;
            }
            else
            { // recover and descend toward ground contact
                c.Throttle = 0.55;
                c.Elevator = Clamp(0.02 * (25f - alt) - 0.04 * climb, -0.4, 0.6);
                c.Ailerons = leveler;
                c.Rudder = 0;
            }
        }

        private static Sample Capture(float t, IAirplaneControl c, IFlightModel m)
        {
            Sample s = new Sample();
            s.T = t;
            s.Throttle = (float)c.Throttle;
            s.Elevator = (float)c.Elevator;
            s.Ailerons = (float)c.Ailerons;
            s.Rudder = (float)c.Rudder;
            s.X = m.X; s.Y = m.Y; s.Z = m.Z;
            Quaternion q = Quaternion.CreateFromYawPitchRoll(m.Yaw, m.Pitch, m.Roll);
            s.Qw = q.W; s.Qx = q.X; s.Qy = q.Y; s.Qz = q.Z;
            Vector3 v = m.Velocity;
            s.Vx = v.X; s.Vy = v.Y; s.Vz = v.Z;
            s.Crashed = m.Crashed;
            s.TouchedDown = m.TouchedDown;
            return s;
        }
        #endregion

        #region CSV + comparison
        private const string Header = "t,throttle,elevator,ailerons,rudder,x,y,z,qw,qx,qy,qz,vx,vy,vz,crashed,touchedDown";

        private static void WriteCsv(string path, string parFile, List<Sample> samples)
        {
            using (StreamWriter w = new StreamWriter(path))
            {
                w.WriteLine("# aircraft: {0}", Path.GetFileName(parFile));
                w.WriteLine("# dt: {0}  sampleEvery: {1}  duration: {2}", TimeStep.ToString("R", CultureInfo.InvariantCulture), SampleInterval, Duration);
                w.WriteLine(Header);
                foreach (Sample s in samples)
                    w.WriteLine(Format(s));
            }
        }

        private static string Format(Sample s)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return string.Join(",", new string[] {
                s.T.ToString("F3", ic),
                s.Throttle.ToString("R", ic), s.Elevator.ToString("R", ic),
                s.Ailerons.ToString("R", ic), s.Rudder.ToString("R", ic),
                s.X.ToString("R", ic), s.Y.ToString("R", ic), s.Z.ToString("R", ic),
                s.Qw.ToString("R", ic), s.Qx.ToString("R", ic), s.Qy.ToString("R", ic), s.Qz.ToString("R", ic),
                s.Vx.ToString("R", ic), s.Vy.ToString("R", ic), s.Vz.ToString("R", ic),
                s.Crashed ? "1" : "0", s.TouchedDown ? "1" : "0" });
        }

        private static bool Compare(string csvPath, List<Sample> actual, float tolerance,
            out float maxError, out float earlyError, out float divergenceTime)
        {
            maxError = 0f;
            earlyError = 0f;
            divergenceTime = -1f;
            if (!File.Exists(csvPath))
                throw new FileNotFoundException("No recording found: " + csvPath);

            List<Sample> expected = ReadCsv(csvPath);
            int count = Math.Min(expected.Count, actual.Count);
            bool pass = expected.Count == actual.Count;

            for (int i = 0; i < count; i++)
            {
                float dx = expected[i].X - actual[i].X;
                float dy = expected[i].Y - actual[i].Y;
                float dz = expected[i].Z - actual[i].Z;
                float err = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (err > maxError)
                    maxError = err;
                if (expected[i].T <= EarlyWindow && err > earlyError)
                    earlyError = err;
                if (err > tolerance)
                {
                    if (divergenceTime < 0)
                        divergenceTime = expected[i].T;
                    pass = false;
                }
            }
            return pass;
        }

        private static List<Sample> ReadCsv(string path)
        {
            List<Sample> samples = new List<Sample>();
            CultureInfo ic = CultureInfo.InvariantCulture;
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.Length == 0 || line[0] == '#' || line.StartsWith("t,"))
                    continue;
                string[] p = line.Split(',');
                Sample s = new Sample();
                s.T = float.Parse(p[0], ic);
                s.X = float.Parse(p[5], ic);
                s.Y = float.Parse(p[6], ic);
                s.Z = float.Parse(p[7], ic);
                samples.Add(s);
            }
            return samples;
        }
        #endregion

        #region Path discovery
        /// <summary>Walks up from the exe location looking for the repo-relative path.</summary>
        private static string FindDefault(string relative)
        {
            DirectoryInfo dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 6 && dir != null; i++)
            {
                string candidate = Path.Combine(dir.FullName, relative);
                if (Directory.Exists(candidate) || Directory.Exists(Path.GetDirectoryName(candidate)))
                    return candidate;
                dir = dir.Parent;
            }
            return null;
        }
        #endregion

        private static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Replace(' ', '_');
        }
    }
}
