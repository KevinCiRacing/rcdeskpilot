using System.Numerics;

namespace RCSim
{
    /// <summary>
    /// Headless stand-in for the Sim's Water scenery object (a render-coupled
    /// GameObject). The flight models only ask whether a position is over
    /// water; the characterization scenarios have none.
    /// </summary>
    internal class Water
    {
        public bool OverWater(Vector3 position)
        {
            return false;
        }
    }
}
