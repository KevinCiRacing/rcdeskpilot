using System;
using Vortice.Multimedia;
using Vortice.XAudio2;

namespace Bonsai.Graphics.Audio
{
    /// <summary>A playable WAV (legacy Sound surface: Play(looping)/Stop/IsPlaying/Length).</summary>
    public class Sound : IDisposable
    {
        private protected IXAudio2SourceVoice voice;
        private protected WavFile wav;
        private protected AudioBuffer buffer;
        // Wind sweeps to 5x base frequency; leave generous headroom.
        private protected const float MaxFrequencyRatio = 8f;

        public string FileName { get; }
        public float Length { get { return wav.LengthSeconds; } }
        internal int SourceSampleRate { get { return wav.SampleRate; } }

        public bool IsPlaying
        {
            get { return voice != null && voice.State.BuffersQueued > 0; }
        }

        /// <summary>Total samples played (diagnostics/tests).</summary>
        public long SamplesPlayed
        {
            get { return voice != null ? (long)voice.State.SamplesPlayed : 0; }
        }

        public Sound(string fileName)
        {
            FileName = fileName;
            wav = WavFile.Load(fileName);
            buffer = new AudioBuffer(wav.Data);
            AudioEngine.Register(this);
        }

        private protected void EnsureVoice()
        {
            if (voice != null)
                return;
            var format = new WaveFormat(wav.SampleRate, wav.BitsPerSample, wav.Channels);
            voice = AudioEngine.Device.CreateSourceVoice(format, VoiceFlags.None, MaxFrequencyRatio);
            OnVoiceCreated();
        }

        private protected virtual void OnVoiceCreated() { }

        public virtual void Play(bool looping)
        {
            EnsureVoice();
            voice.Stop();
            voice.FlushSourceBuffers();
            buffer.LoopCount = looping ? Vortice.XAudio2.XAudio2.LoopInfinite : 0u;
            voice.SubmitSourceBuffer(buffer);
            voice.Start();
        }

        public void Stop()
        {
            if (voice != null)
            {
                voice.Stop();
                voice.FlushSourceBuffers();
            }
        }

        public virtual void Dispose()
        {
            AudioEngine.Unregister(this);
            if (voice != null)
            {
                voice.Stop();
                voice.DestroyVoice();
                voice.Dispose();
                voice = null;
            }
        }
    }

    /// <summary>
    /// Legacy SoundControllable surface: Volume 0-100, Frequency in absolute
    /// Hz (mapped to an XAudio2 frequency ratio against the source rate), Pan
    /// -100..+100.
    /// </summary>
    public class SoundControllable : Sound
    {
        private int volume = 100;
        private int pan;

        public SoundControllable(string fileName) : base(fileName) { }

        public int Volume
        {
            get { return volume; }
            set
            {
                volume = Math.Max(0, Math.Min(100, value));
                EnsureVoice();
                voice.Volume = volume / 100f;
            }
        }

        /// <summary>Playback frequency in Hz (legacy DirectSound semantics).</summary>
        public int Frequency
        {
            get { return voice != null ? (int)(voice.FrequencyRatio * wav.SampleRate) : wav.SampleRate; }
            set
            {
                EnsureVoice();
                float ratio = Math.Max(0.05f, Math.Min(MaxFrequencyRatio, value / (float)wav.SampleRate));
                voice.SetFrequencyRatio(ratio, 0);
            }
        }

        /// <summary>Stereo pan -100 (left) .. +100 (right).</summary>
        public int Pan
        {
            get { return pan; }
            set
            {
                pan = Math.Max(-100, Math.Min(100, value));
                EnsureVoice();
                float right = (pan + 100) / 200f;
                SetPanMatrix(1f - right, right);
            }
        }

        private protected void SetPanMatrix(float left, float right)
        {
            int sourceChannels = wav.Channels;
            int destinationChannels = AudioEngine.MasteringChannels;
            var matrix = new float[sourceChannels * destinationChannels];
            for (int src = 0; src < sourceChannels; src++)
            {
                matrix[src] = left;                                       // dest 0
                if (destinationChannels > 1)
                    matrix[sourceChannels + src] = right;                 // dest 1
            }
            voice.SetOutputMatrix((uint)sourceChannels, (uint)destinationChannels, matrix);
        }

        /// <summary>Last applied L/R gains (diagnostics/tests).</summary>
        public (float Left, float Right) DebugPanGains
        {
            get { float r = (pan + 100) / 200f; return (1f - r, r); }
        }
    }
}
