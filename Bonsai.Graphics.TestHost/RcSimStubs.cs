using System.Numerics;

// Stand-ins for the Sim game objects the compiled-in flight-model sources
// reference (same pattern as RCSim.Characterization): deterministic
// zero-wind weather until the real Weather/Wind port (issue 15).
namespace RCSim
{
    internal class Program
    {
        public static Program Instance = new Program();
        public Weather Weather = new Weather();
    }

    internal class Weather
    {
        public Wind Wind = new Wind();
    }

    internal class Wind
    {
        public double CurrentDirection = 0.0;
        public double Direction = 0.0;
        public double ConstantWindSpeed = 0.0;
        public double WindTime { get; set; }

        public Vector3 GetWindAt(Vector3 position)
        {
            return Vector3.Zero;
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
