using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Bonsai.Graphics.Input;
using Bonsai.Graphics.Win32;

namespace Bonsai.Graphics.TestHost
{
    /// <summary>
    /// Issue 11 selftest: joystick/Transmitter enumeration + polling rate,
    /// keyboard state via real WM_KEYDOWN/KEYUP messages, and legacy-format
    /// channel-mapping settings roundtrip.
    /// </summary>
    internal static class InputDemo
    {
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public static int Run(string outDir)
        {
            string settingsPath = Path.Combine(outDir, "frameworkconfig.xml");
            if (File.Exists(settingsPath))
                File.Delete(settingsPath);

            using (var window = new Win32Window("Input Test", 320, 200))
            using (var input = new InputManager(window.Handle, window, settingsPath))
            {
                // --- Joystick/Transmitter ---
                Console.WriteLine("joystick        : {0}", input.JoystickAvailable ? input.JoystickName : "none attached (pass-through)");
                bool pollOk = true;
                if (input.JoystickAvailable)
                {
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < 500; i++)
                        input.Update();
                    sw.Stop();
                    double hz = 500.0 / Math.Max(sw.Elapsed.TotalSeconds, 1e-9);
                    Console.WriteLine("poll rate       : {0:F0} polls/s", hz);
                    foreach (var axis in input.GetRawAxes())
                        Console.WriteLine("  axis {0,-3} = {1}", axis.Key, axis.Value);
                    pollOk = hz > 500; // flight-loop rates
                }

                // --- Keyboard through real window messages ---
                window.PumpMessages();
                SendMessageW(window.Handle, 0x0100, (IntPtr)(int)InputKey.UpArrow, IntPtr.Zero);
                window.PumpMessages();
                bool downSeen = input.IsKeyDown(InputKey.UpArrow);
                SendMessageW(window.Handle, 0x0101, (IntPtr)(int)InputKey.UpArrow, IntPtr.Zero);
                window.PumpMessages();
                bool upSeen = !input.IsKeyDown(InputKey.UpArrow);
                SendMessageW(window.Handle, 0x0100, (IntPtr)(int)InputKey.F, IntPtr.Zero);
                window.PumpMessages();
                bool flapsKey = input.IsKeyDown(InputKey.F);
                Console.WriteLine("keyboard        : {0}", downSeen && upSeen && flapsKey ? "OK" : "FAILED");

                // --- Settings roundtrip (legacy Input.Joystick table format) ---
                input.Settings.SetAxis("elevator", JoystickAxis.Ry, true);
                input.Settings.SetAxis("throttle", JoystickAxis.Slider1, false);
                var reloaded = new InputSettings(settingsPath);
                bool inverted;
                bool settingsOk =
                    reloaded.GetAxis("elevator", out inverted) == JoystickAxis.Ry && inverted &&
                    reloaded.GetAxis("throttle", out inverted) == JoystickAxis.Slider1 && !inverted &&
                    reloaded.GetAxis("aileron", out inverted) == JoystickAxis.X;
                string xml = File.ReadAllText(settingsPath);
                bool legacyFormat = xml.Contains("Input.Joystick") && xml.Contains("Function") && xml.Contains("Inverted");
                Console.WriteLine("settings        : {0} (legacy format {1})", settingsOk ? "OK" : "FAILED", legacyFormat ? "OK" : "MISSING");

                bool pass = pollOk && downSeen && upSeen && flapsKey && settingsOk && legacyFormat;
                Console.WriteLine(pass ? "INPUTTEST PASS" : "INPUTTEST FAIL");
                return pass ? 0 : 1;
            }
        }
    }
}
