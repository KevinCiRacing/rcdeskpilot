using System;
using System.IO;

namespace Bonsai.Graphics.Audio
{
    /// <summary>Minimal RIFF/WAVE PCM reader for the sim's legacy content.</summary>
    internal sealed class WavFile
    {
        public int Channels { get; private set; }
        public int SampleRate { get; private set; }
        public int BitsPerSample { get; private set; }
        public byte[] Data { get; private set; }

        public float LengthSeconds
        {
            get { return Data.Length / (float)(SampleRate * Channels * (BitsPerSample / 8)); }
        }

        public static WavFile Load(string path)
        {
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                if (reader.ReadUInt32() != 0x46464952) // "RIFF"
                    throw new InvalidDataException("Not a RIFF file: " + path);
                reader.ReadUInt32(); // riff size
                if (reader.ReadUInt32() != 0x45564157) // "WAVE"
                    throw new InvalidDataException("Not a WAVE file: " + path);

                var wav = new WavFile();
                while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
                {
                    uint chunkId = reader.ReadUInt32();
                    int chunkSize = reader.ReadInt32();
                    long next = reader.BaseStream.Position + chunkSize + (chunkSize & 1);
                    if (chunkId == 0x20746D66) // "fmt "
                    {
                        int format = reader.ReadUInt16();
                        wav.Channels = reader.ReadUInt16();
                        wav.SampleRate = reader.ReadInt32();
                        reader.ReadInt32();  // byte rate
                        reader.ReadUInt16(); // block align
                        wav.BitsPerSample = reader.ReadUInt16();
                        if (format != 1)
                            throw new NotSupportedException(string.Format("WAV format {0} (non-PCM) in {1}", format, path));
                    }
                    else if (chunkId == 0x61746164) // "data"
                    {
                        wav.Data = reader.ReadBytes(chunkSize);
                    }
                    reader.BaseStream.Position = next;
                }
                if (wav.Data == null || wav.SampleRate == 0)
                    throw new InvalidDataException("Missing fmt/data chunk: " + path);
                return wav;
            }
        }
    }
}
