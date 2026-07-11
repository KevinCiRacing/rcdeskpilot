using System;
using System.Globalization;
using System.Threading;
using Microsoft.DirectX;

namespace RCSim
{
    /// <summary>
    /// Headless stand-in for the game's Program singleton. The flight models
    /// (compiled in from ..\RCSim) reach wind data through
    /// Program.Instance.Weather.Wind; this stub supplies deterministic,
    /// zero-wind weather so recordings are reproducible.
    /// Mirrors the pattern RCSim.AircraftEditor uses to host the same sources.
    /// </summary>
    internal class Program
    {
        public static Program Instance = null;

        public Weather Weather;

        [STAThread]
        static int Main(string[] args)
        {
            // The game sets en-US before parsing .par files (double parsing).
            Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");

            Instance = new Program();
            Instance.Weather = new Weather();

            return Characterization.Run(args);
        }
    }

    /// <summary>Deterministic weather stub: no wind, no gusts, no thermals.</summary>
    internal class Weather
    {
        public Wind Wind = new Wind();
    }

    /// <summary>The members the compiled-in RCSim sources consume from the real Wind.</summary>
    internal class Wind
    {
        public double CurrentDirection = 0.0;

        public double Direction = 0.0;

        public double ConstantWindSpeed = 0.0;

        public double WindTime { get; set; }

        public Vector3 GetWindAt(Vector3 position)
        {
            return new Vector3(0f, 0f, 0f);
        }
    }
}
