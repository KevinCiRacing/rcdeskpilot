using System;
using System.Numerics;
using Vortice.XAudio2;

namespace Bonsai.Graphics.Audio
{
    /// <summary>
    /// A 3D-positioned sound (legacy Sound3D surface): set Location as the
    /// emitter moves; panning, attenuation, and doppler follow the
    /// AudioEngine listener via X3DAudio.
    /// </summary>
    public sealed class Sound3D : Sound
    {
        private Vector3 location;
        private Vector3 velocity;
        private DspSettings dsp;
        private float baseFrequencyRatio = 1f;

        /// <summary>World scale for attenuation (legacy DistanceFactor spirit).</summary>
        public float CurveDistanceScaler { get; set; } = 14f;

        /// <summary>Diagnostics/tests: output matrix from the last Apply3D.</summary>
        public float[] DebugMatrix { get { return dsp != null ? dsp.MatrixCoefficients : null; } }

        public Sound3D(string fileName) : base(fileName) { }

        public Vector3 Location
        {
            get { return location; }
            set { location = value; Apply3D(); }
        }

        public Vector3 Velocity
        {
            get { return velocity; }
            set { velocity = value; }
        }

        /// <summary>Base pitch multiplier applied on top of doppler (engine RPM).</summary>
        public float FrequencyRatio
        {
            get { return baseFrequencyRatio; }
            set { baseFrequencyRatio = value; Apply3D(); }
        }

        public override void Play(bool looping)
        {
            base.Play(looping);
            Apply3D();
        }

        private protected override void OnVoiceCreated()
        {
            dsp = new DspSettings((uint)wav.Channels, (uint)AudioEngine.MasteringChannels);
        }

        private void Apply3D()
        {
            if (voice == null || AudioEngine.X3D == null)
                return;

            var listener = new Listener
            {
                Position = AudioEngine.ListenerPosition,
                Velocity = AudioEngine.ListenerVelocity,
                OrientFront = AudioEngine.ListenerFront,
                OrientTop = AudioEngine.ListenerTop,
            };
            var emitter = new Emitter
            {
                Position = location,
                Velocity = velocity,
                OrientFront = new Vector3(0, 0, 1),
                OrientTop = new Vector3(0, 1, 0),
                ChannelCount = (uint)wav.Channels,
                CurveDistanceScaler = CurveDistanceScaler,
                DopplerScaler = 1f,
            };

            AudioEngine.X3D.Calculate(listener, emitter, CalculateFlags.Matrix | CalculateFlags.Doppler, dsp);

            voice.SetOutputMatrix((uint)wav.Channels, (uint)AudioEngine.MasteringChannels, dsp.MatrixCoefficients);
            float ratio = Math.Max(0.05f, Math.Min(MaxFrequencyRatio, baseFrequencyRatio * dsp.DopplerFactor));
            voice.SetFrequencyRatio(ratio, 0);
        }
    }
}
