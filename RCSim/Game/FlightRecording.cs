using System;
using System.IO;
using System.Numerics;
using RCSim.Interfaces;

namespace RCSim
{
    /// <summary>
    /// The legacy .dat flight-recording format (FlightRecorder/RecordedFlight/
    /// AirplaneState port): header string "aircraft\...\X.par", then repeated
    /// [double time][3f position][3f yaw/pitch/roll][4x short controls*100]
    /// [byte switches: water<<3|flaps<<2|gear<<1|smoke]. Stock demo.dat and
    /// flightN.dat files play back unchanged.
    /// </summary>
    internal struct AirplaneState
    {
        public Vector3 Position;      // world space
        public Vector3 Orientation;   // yaw, pitch, roll (radians)
        public double Rudder, Throttle, Elevator, Ailerons;
        public bool Smoke, Flaps, Gear, OnWater;

        public void Write(BinaryWriter writer)
        {
            writer.Write(Position.X); writer.Write(Position.Y); writer.Write(Position.Z);
            writer.Write(Orientation.X); writer.Write(Orientation.Y); writer.Write(Orientation.Z);
            writer.Write((short)Math.Round(Rudder * 100));
            writer.Write((short)Math.Round(Throttle * 100));
            writer.Write((short)Math.Round(Elevator * 100));
            writer.Write((short)Math.Round(Ailerons * 100));
            writer.Write((byte)(((OnWater ? 1 : 0) << 3) | ((Flaps ? 1 : 0) << 2) |
                ((Gear ? 1 : 0) << 1) | (Smoke ? 1 : 0)));
        }

        public void Read(BinaryReader reader)
        {
            Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Orientation = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Rudder = reader.ReadInt16() / 100.0;
            Throttle = reader.ReadInt16() / 100.0;
            Elevator = reader.ReadInt16() / 100.0;
            Ailerons = reader.ReadInt16() / 100.0;
            byte switches = reader.ReadByte();
            Smoke = (switches & 1) > 0;
            Gear = (switches & 2) > 0;
            Flaps = (switches & 4) > 0;
            OnWater = (switches & 8) > 0;
        }
    }

    /// <summary>Records a live FlightSession to a .dat file at 20 Hz.</summary>
    internal sealed class FlightRecorder : IDisposable
    {
        private const double Interval = 0.05;
        private BinaryWriter writer;
        private double startTime = -1;
        private double lastRecord = -1;

        public bool Recording { get { return writer != null; } }

        public void Start(string filePath, string aircraftPar)
        {
            Stop();
            writer = new BinaryWriter(File.Create(filePath));
            // Legacy header: the path from "aircraft\" on, e.g. "aircraft\extra\Xtra.par".
            string full = Path.GetFullPath(aircraftPar).Replace('/', '\\');
            int index = full.ToLowerInvariant().LastIndexOf("\\aircraft\\", StringComparison.Ordinal);
            writer.Write(index >= 0 ? full.Substring(index + 1) : Path.GetFileName(full));
            startTime = -1;
        }

        public void Sample(double totalTime, FlightSession session)
        {
            if (writer == null)
                return;
            if (startTime < 0)
                startTime = totalTime;
            if (totalTime - lastRecord < Interval)
                return;
            lastRecord = totalTime;

            IFlightModel model = session.Model;
            Vector3 angles = model.Angles; // X=roll, Y=pitch, Z=yaw
            writer.Write(totalTime - startTime);
            new AirplaneState
            {
                Position = session.AircraftPosition,
                Orientation = new Vector3(angles.Z, angles.Y, angles.X),
                Rudder = session.Controls.Rudder,
                Throttle = session.Controls.Throttle,
                Elevator = session.Controls.Elevator,
                Ailerons = session.Controls.Ailerons,
                Smoke = session.SmokeEmitting,
                Flaps = session.Controls.Flaps > 0.5,
                Gear = session.Controls.Gear > 0.5,
                OnWater = false,
            }.Write(writer);
        }

        public void Stop()
        {
            if (writer != null)
            {
                writer.Flush();
                writer.Dispose();
                writer = null;
            }
        }

        public void Dispose() { Stop(); }
    }

    /// <summary>Plays a .dat recording back: interpolated position/attitude and
    /// control values (for surface animation), with the legacy angle-wrap
    /// handling. No physics runs during playback.</summary>
    internal sealed class RecordedFlight : IDisposable
    {
        private BinaryReader reader;
        private AirplaneState previousState, nextState;
        private double previousTime, nextTime;
        private double startTime = -1;

        public string AircraftPar { get; }
        public bool Playing { get; private set; }
        public Vector3 Position { get; private set; }
        /// <summary>Sample-derived world velocity (legacy Towing feed).</summary>
        public Vector3 Velocity { get; private set; }
        /// <summary>Seconds into the recording.</summary>
        public double Time { get; private set; }
        public Vector3 YawPitchRoll { get; private set; }
        public double Throttle { get; private set; }
        public double Rudder { get; private set; }
        public double Elevator { get; private set; }
        public double Ailerons { get; private set; }
        public bool Smoke { get; private set; }

        public RecordedFlight(string filePath, string repoRoot)
        {
            reader = new BinaryReader(File.OpenRead(filePath));
            string header = reader.ReadString(); // "aircraft\extra\Xtra.par"
            AircraftPar = Path.Combine(repoRoot, "RCSim",
                header.Replace('\\', Path.DirectorySeparatorChar));
            nextTime = reader.ReadDouble();
            nextState.Read(reader);
            previousTime = nextTime;
            previousState = nextState;
            Playing = true;
        }

        /// <summary>Advances playback; false when the recording has ended.</summary>
        public bool Update(double totalTime)
        {
            if (!Playing)
                return false;
            if (startTime < 0)
                startTime = totalTime;
            double relativeTime = totalTime - startTime;

            while (relativeTime >= nextTime)
            {
                previousTime = nextTime;
                previousState = nextState;
                try
                {
                    nextTime = reader.ReadDouble();
                    nextState.Read(reader);
                }
                catch (EndOfStreamException)
                {
                    Playing = false;
                    return false;
                }
            }

            Time = relativeTime;
            float factor = nextTime > previousTime
                ? (float)((relativeTime - previousTime) / (nextTime - previousTime)) : 0f;
            Position = Vector3.Lerp(previousState.Position, nextState.Position, factor);
            Velocity = nextTime > previousTime
                ? (nextState.Position - previousState.Position) / (float)(nextTime - previousTime)
                : Vector3.Zero;
            YawPitchRoll = new Vector3(
                LerpAngle(previousState.Orientation.X, nextState.Orientation.X, factor),
                LerpAngle(previousState.Orientation.Y, nextState.Orientation.Y, factor),
                LerpAngle(previousState.Orientation.Z, nextState.Orientation.Z, factor));
            Rudder = previousState.Rudder + factor * (nextState.Rudder - previousState.Rudder);
            Throttle = previousState.Throttle + factor * (nextState.Throttle - previousState.Throttle);
            Elevator = previousState.Elevator + factor * (nextState.Elevator - previousState.Elevator);
            Ailerons = previousState.Ailerons + factor * (nextState.Ailerons - previousState.Ailerons);
            Smoke = previousState.Smoke;
            return true;
        }

        /// <summary>Legacy wrap-aware interpolation across the +-pi seam.</summary>
        private static float LerpAngle(float a, float b, float factor)
        {
            const float TwoPi = 2f * (float)Math.PI;
            if (Math.Abs(a - b) > Math.PI)
                return (1 - factor) * ((a + TwoPi) % TwoPi) + factor * ((b + TwoPi) % TwoPi);
            return (1 - factor) * a + factor * b;
        }

        public void Dispose()
        {
            if (reader != null)
            {
                reader.Dispose();
                reader = null;
            }
            Playing = false;
        }
    }
}
