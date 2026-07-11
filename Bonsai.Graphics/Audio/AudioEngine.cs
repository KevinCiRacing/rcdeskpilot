using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice.Multimedia;
using Vortice.XAudio2;

namespace Bonsai.Graphics.Audio
{
    /// <summary>
    /// The sound layer on XAudio2 + X3DAudio (ADR 0001), mirroring the legacy
    /// static SoundManager surface: master volume 0-100 and a listener that
    /// 3D sounds attenuate/pan against.
    /// </summary>
    public static class AudioEngine
    {
        private static IXAudio2 xaudio;
        private static IXAudio2MasteringVoice mastering;
        private static X3DAudio x3d;
        private static readonly List<Sound> sounds = new List<Sound>();
        private static int volume = 100;

        internal static IXAudio2 Device { get { return xaudio; } }
        internal static X3DAudio X3D { get { return x3d; } }
        internal static int MasteringChannels { get; private set; }

        public static bool IsInitialized { get { return xaudio != null; } }

        /// <summary>Listener pose for 3D sounds (the pilot camera).</summary>
        public static Vector3 ListenerPosition { get; set; }
        public static Vector3 ListenerVelocity { get; set; }
        public static Vector3 ListenerFront { get; set; } = new Vector3(0, 0, 1);
        public static Vector3 ListenerTop { get; set; } = new Vector3(0, 1, 0);

        /// <summary>Master volume 0-100 (legacy semantics).</summary>
        public static int Volume
        {
            get { return volume; }
            set
            {
                volume = Math.Max(0, Math.Min(100, value));
                if (mastering != null)
                    mastering.Volume = volume / 100f;
            }
        }

        public static void Initialize()
        {
            if (xaudio != null)
                return;
            xaudio = XAudio2.XAudio2Create();
            mastering = xaudio.CreateMasteringVoice();
            MasteringChannels = (int)mastering.VoiceDetails.InputChannels;
            x3d = new X3DAudio(Speakers.FrontLeft | Speakers.FrontRight);
            mastering.Volume = volume / 100f;
        }

        internal static void Register(Sound sound) { sounds.Add(sound); }
        internal static void Unregister(Sound sound) { sounds.Remove(sound); }

        public static void Shutdown()
        {
            foreach (Sound sound in sounds.ToArray())
                sound.Dispose();
            sounds.Clear();
            if (mastering != null) { mastering.Dispose(); mastering = null; }
            if (xaudio != null) { xaudio.Dispose(); xaudio = null; }
            x3d = null;
        }
    }
}
