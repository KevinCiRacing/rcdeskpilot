using System;
using System.IO;
using System.Numerics;
using System.Threading;
using Bonsai.Graphics.Audio;

namespace Bonsai.Graphics.TestHost
{
    /// <summary>
    /// Issue 12 selftest (audible + programmatically verified): engine loop
    /// with a throttle-style pitch sweep, overlapping one-shots, 3D panning/
    /// attenuation via X3DAudio, and a load/play pass over every WAV in the
    /// repo content.
    /// </summary>
    internal static class AudioDemo
    {
        public static int Run(string repoRoot)
        {
            string dataDir = Path.Combine(repoRoot, "RCSim", "data");
            AudioEngine.Initialize();
            try
            {
                // --- Engine loop with pitch sweep (throttle 0 -> 1 -> 0) ---
                var engine = new SoundControllable(Path.Combine(repoRoot, "RCSim", "Aircraft", "extra", "engine.wav"));
                engine.Volume = 60;
                engine.Play(true);
                long samplesStart = engine.SamplesPlayed;
                for (int i = 0; i <= 60; i++)
                {
                    float throttle = 1f - Math.Abs(i - 30) / 30f;
                    engine.Frequency = (int)(engine.Length > 0 ? (22050 + throttle * 22050) : 22050);
                    Thread.Sleep(25);
                }
                bool loopAlive = engine.IsPlaying;                       // still queued after 1.5 s (loops)
                bool samplesAdvanced = engine.SamplesPlayed > samplesStart;
                engine.Stop();
                Console.WriteLine("engine loop     : {0} (samples {1})", loopAlive && samplesAdvanced ? "OK" : "FAILED", engine.SamplesPlayed);

                // --- Overlapping one-shots ---
                var crash1 = new Sound(Path.Combine(dataDir, "crash.wav"));
                var crash2 = new Sound(Path.Combine(dataDir, "crash.wav"));
                var gate = new Sound(Path.Combine(dataDir, "gate.wav"));
                crash1.Play(false); crash2.Play(false); gate.Play(false);
                Thread.Sleep(120);
                bool overlap = crash1.IsPlaying && crash2.IsPlaying;
                Console.WriteLine("overlap oneshot : {0}", overlap ? "OK" : "FAILED");
                Thread.Sleep(600);

                // --- 3D panning/attenuation ---
                AudioEngine.ListenerPosition = Vector3.Zero;
                AudioEngine.ListenerFront = new Vector3(0, 0, 1);
                var engine3d = new Sound3D(Path.Combine(repoRoot, "RCSim", "Aircraft", "SF260", "engine.wav"));
                engine3d.Play(true);
                engine3d.Location = new Vector3(-30, 0, 0);              // hard left
                float[] leftMatrix = (float[])engine3d.DebugMatrix.Clone();
                Thread.Sleep(400);
                engine3d.Location = new Vector3(30, 0, 0);               // hard right
                float[] rightMatrix = (float[])engine3d.DebugMatrix.Clone();
                Thread.Sleep(400);
                engine3d.Location = new Vector3(0, 0, 300);              // far ahead
                float[] farMatrix = (float[])engine3d.DebugMatrix.Clone();
                engine3d.Stop();
                // mono source, stereo out: [0]=L, [1]=R
                bool pans = leftMatrix[0] > leftMatrix[1] && rightMatrix[1] > rightMatrix[0];
                bool attenuates = (farMatrix[0] + farMatrix[1]) < (leftMatrix[0] + leftMatrix[1]);
                Console.WriteLine("3d pan          : {0} (L {1:F2}/{2:F2}  R {3:F2}/{4:F2})",
                    pans ? "OK" : "FAILED", leftMatrix[0], leftMatrix[1], rightMatrix[0], rightMatrix[1]);
                Console.WriteLine("3d attenuation  : {0} (far {1:F3})", attenuates ? "OK" : "FAILED", farMatrix[0] + farMatrix[1]);

                // --- Every WAV in the repo loads and plays ---
                int okCount = 0, failCount = 0;
                foreach (string wavPath in Directory.GetFiles(Path.Combine(repoRoot, "RCSim"), "*.wav", SearchOption.AllDirectories))
                {
                    try
                    {
                        using (var sound = new Sound(wavPath))
                        {
                            sound.Play(false);
                            Thread.Sleep(30);
                            okCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        failCount++;
                        Console.Error.WriteLine("WAV FAILED {0}: {1}", Path.GetFileName(wavPath), ex.Message);
                    }
                }
                Console.WriteLine("wav content     : {0}/{1} OK", okCount, okCount + failCount);

                bool pass = loopAlive && samplesAdvanced && overlap && pans && attenuates && failCount == 0;
                Console.WriteLine(pass ? "AUDIOTEST PASS" : "AUDIOTEST FAIL");
                return pass ? 0 : 1;
            }
            finally
            {
                AudioEngine.Shutdown();
            }
        }
    }
}
