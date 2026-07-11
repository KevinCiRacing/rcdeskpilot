using Bonsai.Core.EventArgs;

namespace Bonsai.Core
{
    /// <summary>Stores timer callback information (used by the DXUT-derived
    /// Framework's timer list; kept separate so FrameworkTimer itself stays
    /// renderer-independent).</summary>
    public struct TimerData
    {
        public TimerCallback callback;
        public float TimeoutInSecs;
        public float Countdown;
        public bool IsEnabled;
    }
}
