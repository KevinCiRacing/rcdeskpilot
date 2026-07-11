using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Bonsai.Graphics.Audio;
using Bonsai.Objects.Terrain;

// The Sim game-object graph the compiled-in flight-model sources reference.
// Weather/Wind is the real legacy algorithm ported to System.Numerics
// (issue 15); Program is still a minimal stand-in until RCSim proper is
// resurrected. All defaults are zero wind, so characterization-style
// deterministic runs are unaffected until a host dials weather in.
namespace RCSim
{
    internal class Program
    {
        public static Program Instance = new Program();
        public Weather Weather = new Weather();

        /// <summary>Terrain for wind ground influence; set by the flight host.</summary>
        public Heightmap Heightmap;

        /// <summary>Sim time in seconds; drives the thermal boost cycle.</summary>
        public double CurrentTime;
    }

    internal class Weather : IDisposable
    {
        public Wind Wind = new Wind();

        public void Update(double totalTime, float elapsedTime)
        {
            Wind.Update(totalTime, elapsedTime);
        }

        public void Dispose()
        {
            Wind.Dispose();
        }
    }

    /// <summary>
    /// The legacy RCSim.Wind model: constant wind + gust waves (four gust
    /// types), direction variance, turbulence, thermal columns with optional
    /// downdrafts, and terrain-slope ground influence. Wind speeds in m/s,
    /// directions in radians, world space.
    /// </summary>
    internal class Wind : IDisposable
    {
        public enum GustTypeEnum
        {
            Default,
            HighFrequency,
            LowFreqSharpTransitions,
            LowFreqSmoothTransitions
        }

        private sealed class ThermalSource
        {
            public Vector3 Position;
            public float OriginalStrength;
            public float OriginalSize;
            public float Strength;
            public float SizeSq;
        }

        private readonly List<ThermalSource> thermalSources = new List<ThermalSource>();
        private readonly Random random = new Random(12345);
        private double constantWindSpeed;
        private double gustSpeed;
        private double currentGustSpeed;
        private double currentSpeed;
        private double lastSoundUpdate;
        private float nextRandomTime;
        private double randomFactor;
        private float thermalStrengthFactor = 1.0f;
        private float thermalSizeFactor = 1.0f;
        private SoundControllable sound;

        public double ConstantWindSpeed
        {
            get { return constantWindSpeed; }
            set { constantWindSpeed = value; CheckSound(); }
        }

        public double GustSpeed
        {
            get { return gustSpeed; }
            set { gustSpeed = value; CheckSound(); }
        }

        public GustTypeEnum GustType { get; set; } = GustTypeEnum.LowFreqSharpTransitions;
        public double Direction { get; set; }
        public double DirectionVariance { get; set; }
        public Vector3 CurrentWind { get; set; }
        public double MaximumConstantWindSpeed { get { return 12f; } }
        public double MaximumGustSpeed { get { return 12f; } }
        public double CurrentDirection { get; set; }
        public double DownDrafts { get; set; }
        public double GustFrequency { get; set; }
        public double GustVariability { get; set; }
        public double Turbulence { get; set; }
        public float WindTime { get; set; }

        /// <summary>Path to wind.wav; enables the wind loop when set.</summary>
        public string SoundPath { get; set; }

        public float ThermalStrengthFactor
        {
            get { return thermalStrengthFactor; }
            set { thermalStrengthFactor = value; RescaleThermals(); }
        }

        public float ThermalSizeFactor
        {
            get { return thermalSizeFactor; }
            set { thermalSizeFactor = value; RescaleThermals(); }
        }

        public void AddThermalSource(Vector3 position, float strength, float size)
        {
            thermalSources.Add(new ThermalSource
            {
                Position = new Vector3(position.X, 0, position.Z),
                OriginalStrength = strength,
                OriginalSize = size,
                Strength = strength * thermalStrengthFactor,
                SizeSq = size * size * thermalSizeFactor * thermalSizeFactor,
            });
        }

        public void ClearThermalSources()
        {
            thermalSources.Clear();
        }

        private void RescaleThermals()
        {
            foreach (ThermalSource source in thermalSources)
            {
                source.Strength = source.OriginalStrength * thermalStrengthFactor;
                source.SizeSq = source.OriginalSize * source.OriginalSize * thermalSizeFactor * thermalSizeFactor;
            }
        }

        /// <summary>Per-frame update (legacy OnFrameMove): gust wave, direction
        /// drift, the CurrentWind vector, and the wind sound loop.</summary>
        public void Update(double totalTime, float elapsedTime)
        {
            UpdateGustSpeed(WindTime);
            CurrentDirection = Direction + DirectionVariance * Math.Sin(WindTime / 130.0);
            currentSpeed = ConstantWindSpeed + currentGustSpeed;
            lock (this)
            {
                CurrentWind = new Vector3(
                    (float)(currentSpeed * Math.Cos(CurrentDirection)), 0,
                    (float)(currentSpeed * Math.Sin(CurrentDirection)));
            }
            if (totalTime - lastSoundUpdate > 0.1)
            {
                UpdateSound();
                lastSoundUpdate = totalTime;
            }
        }

        public Vector3 GetWindAt(Vector3 position)
        {
            return GetWindAt(position, includeTurbulence: true);
        }

        public Vector3 GetWindAt(Vector3 position, bool includeTurbulence)
        {
            Vector3 windVector = new Vector3(
                (float)(ConstantWindSpeed * Math.Cos(CurrentDirection)), 0,
                (float)(ConstantWindSpeed * Math.Sin(CurrentDirection))) + GetGustAt(position, WindTime);
            if (includeTurbulence)
                windVector += GetTurbulence(position, WindTime);
            return windVector + GetThermalInfluence(position) + (float)currentSpeed * GetGroundInfluence(position);
        }

        private void UpdateGustSpeed(double totalTime)
        {
            double signedSpeed;
            switch (GustType)
            {
                case GustTypeEnum.Default:
                    currentGustSpeed = Math.Sin(totalTime / 3.3) > 0
                        ? Math.Abs(GustSpeed * Math.Sin(totalTime / 10) * Math.Sin(totalTime / 13.5) * Math.Sin(totalTime / 3.3))
                        : 0;
                    break;
                case GustTypeEnum.HighFrequency:
                    currentGustSpeed = Math.Sin(totalTime / 1.3) > 0
                        ? Math.Abs(GustSpeed * Math.Sin(totalTime / 20) * Math.Sin(totalTime / 13.5) * Math.Sin(totalTime / 1.3))
                        : 0;
                    break;
                case GustTypeEnum.LowFreqSharpTransitions:
                    signedSpeed = GustSpeed * Math.Sin(totalTime / 10.0) * Math.Sin(totalTime / 13.5) * Math.Sin(totalTime / 15.3);
                    currentGustSpeed = signedSpeed > 0 ? Math.Sqrt(signedSpeed) : 0;
                    break;
                case GustTypeEnum.LowFreqSmoothTransitions:
                    signedSpeed = GustSpeed * Math.Sin(totalTime / 10.0) * Math.Sin(totalTime / 13.5) * Math.Sin(totalTime / 15.3);
                    currentGustSpeed = signedSpeed > 0 ? signedSpeed : 0;
                    break;
            }
        }

        private Vector3 GetGustAt(Vector3 position, float totalTime)
        {
            // The gust wave travels across the field along the wind direction.
            totalTime -= (float)((2.0 / (MaximumConstantWindSpeed + 1)) * (Math.Cos(Direction) * position.X + Math.Sin(Direction) * position.Z));
            double gust = 0;
            double frequency = 0.5 + 2 * GustFrequency;
            double signedSpeed = GustSpeed * 0.66 * (0.5 +
                Math.Sin(totalTime * frequency / 5.0) *
                 (1.0 - GustVariability + GustVariability *
                        Math.Sin(totalTime * frequency / 2.3) *
                        Math.Sin(totalTime * frequency / 13.5)));
            if (signedSpeed > 0)
                gust = signedSpeed;
            double direction = CurrentDirection + GustVariability * Math.Sin(totalTime / 20f);
            return new Vector3(
                (float)(gust * Math.Cos(direction)),
                (float)(0.3f * gust * GustVariability * Math.Cos(totalTime)),
                (float)(gust * Math.Sin(direction)));
        }

        private Vector3 GetGroundInfluence(Vector3 position)
        {
            Heightmap heightmap = Program.Instance.Heightmap;
            if (heightmap != null && CurrentWind.LengthSquared() > 0.01f)
            {
                const float maxAlt = 30f;
                float groundLevel = heightmap.GetHeightAt(position.X, position.Z);
                float altitude = position.Y - groundLevel;
                if (altitude < groundLevel + maxAlt)
                {
                    Vector3 windPosition = position - (2 * altitude / maxAlt) * CurrentWind;
                    Vector3 groundNormal = heightmap.GetSmoothNormalAt(windPosition.X, windPosition.Z);
                    Vector3 normalizedWind = Vector3.Normalize(CurrentWind);
                    return (-(maxAlt - altitude) / maxAlt) * Vector3.Dot(normalizedWind, groundNormal) * groundNormal;
                }
            }
            return Vector3.Zero;
        }

        private Vector3 GetThermalInfluence(Vector3 position)
        {
            Vector3 result = Vector3.Zero;
            float minDistanceSq = 100000f;
            ThermalSource nearestSource = null;
            foreach (ThermalSource thermalSource in thermalSources)
            {
                float distanceSq = (new Vector3(position.X, 0, position.Z) - thermalSource.Position).LengthSquared();
                if (distanceSq < minDistanceSq)
                {
                    minDistanceSq = distanceSq;
                    nearestSource = thermalSource;
                    if (distanceSq < thermalSource.SizeSq)
                    {
                        result = new Vector3(0, Math.Min(position.Y,
                                Math.Min(thermalSource.Strength, thermalSource.Strength * (float)Math.Pow(2 * (thermalSource.SizeSq - distanceSq) / thermalSource.SizeSq, 0.25))),
                                0);
                        // Periodic high-altitude boost (legacy 240 s cycle).
                        if ((position.Y > 100) && (Program.Instance.CurrentTime % 240 > 200))
                            result *= 2f;
                    }
                }
            }
            if (nearestSource != null && DownDrafts > 0 && result == Vector3.Zero)
            {
                if (minDistanceSq < 2 * nearestSource.SizeSq)
                {
                    if (minDistanceSq < 3 * nearestSource.SizeSq / 2)
                        result = new Vector3(0,
                            (float)(DownDrafts * nearestSource.Strength * (nearestSource.SizeSq - minDistanceSq) / nearestSource.SizeSq), 0);
                    else
                        result = new Vector3(0,
                            (float)(DownDrafts * nearestSource.Strength * (minDistanceSq - 2 * nearestSource.SizeSq) / nearestSource.SizeSq), 0);
                }
            }
            return result;
        }

        private Vector3 GetTurbulence(Vector3 position, float totalTime)
        {
            if (totalTime > nextRandomTime)
            {
                randomFactor = random.NextDouble();
                nextRandomTime = totalTime + (randomFactor > 0.4 ? random.Next(2) : random.Next(5));
            }
            totalTime -= (float)((2.0 / (MaximumConstantWindSpeed + 1)) * (Math.Cos(Direction) * position.X + Math.Sin(Direction) * position.Z));
            if (Turbulence > 0)
                return (float)Math.Min(0.5, Math.Max(0, 0.2 + 0.02 * (40 - position.Y))) * (float)(randomFactor * Turbulence) * new Vector3(
                    (float)(Math.Sin(5 * totalTime) * Math.Sin(9.7 * totalTime)),
                    (float)(Math.Sin(5 * (totalTime + 10)) * Math.Sin(9.9 * (totalTime + 10))),
                    (float)(Math.Sin(5 * (totalTime + 20)) * Math.Sin(9.5 * (totalTime + 20))));
            return Vector3.Zero;
        }

        private void CheckSound()
        {
            if (SoundPath != null && File.Exists(SoundPath) && AudioEngine.IsInitialized &&
                (ConstantWindSpeed > 0 || GustSpeed > 0))
            {
                if (sound == null)
                {
                    sound = new SoundControllable(SoundPath);
                    UpdateSound();
                    sound.Play(true);
                }
                else
                    UpdateSound();
            }
            else if (sound != null)
            {
                sound.Stop();
                sound.Dispose();
                sound = null;
            }
        }

        private void UpdateSound()
        {
            if (sound != null)
            {
                sound.Frequency = (int)(22050 + 88200 * CurrentWind.Length() / (MaximumConstantWindSpeed + MaximumGustSpeed));
                sound.Volume = (int)(90 + 10 * CurrentWind.Length() / (MaximumConstantWindSpeed + MaximumGustSpeed));
            }
        }

        public void Dispose()
        {
            if (sound != null)
            {
                sound.Stop();
                sound.Dispose();
                sound = null;
            }
        }
    }

    internal class Water
    {
        public bool OverWater(Vector3 position)
        {
            return false;
        }
    }
}
