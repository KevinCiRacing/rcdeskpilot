using System;
using System.Collections.Generic;
using Bonsai.Graphics.Win32;
using Vortice.DirectInput;

namespace Bonsai.Graphics.Input
{
    /// <summary>Engine-level key identifiers (the Sim never sees VK codes or
    /// DirectInput key enums). Values are Win32 virtual-key codes.</summary>
    public enum InputKey
    {
        UpArrow = 0x26, DownArrow = 0x28, LeftArrow = 0x25, RightArrow = 0x27,
        PageUp = 0x21, PageDown = 0x22, Home = 0x24, End = 0x23,
        NumPad1 = 0x61, NumPad2 = 0x62, NumPad3 = 0x63, NumPad4 = 0x64,
        NumPad6 = 0x66, NumPad7 = 0x67, NumPad8 = 0x68, NumPad9 = 0x69,
        F = 0x46, G = 0x47, R = 0x52, Space = 0x20, Escape = 0x1B,
    }

    /// <summary>
    /// The input layer on Vortice.DirectInput (ADR 0001): enumerates HID game
    /// controllers (the Transmitter), reads axes through the legacy channel
    /// mapping (+-100 range, function names "elevator"/"aileron"/"rudder"/
    /// "throttle"/"flaps"/"gear"), and tracks keyboard state from Win32
    /// window messages. No DirectInput types escape this class.
    /// </summary>
    public sealed class InputManager : IDisposable
    {
        private IDirectInput8 directInput;
        private IDirectInputDevice8 joystick;
        private JoystickState joystickState = new JoystickState();
        private readonly bool[] keys = new bool[256];
        private readonly InputSettings settings;

        public bool JoystickAvailable { get { return joystick != null; } }
        public string JoystickName { get; private set; }
        public InputSettings Settings { get { return settings; } }

        public InputManager(IntPtr windowHandle, Win32Window window, string settingsPath)
        {
            settings = new InputSettings(settingsPath);
            settings.SetDefaultAxis("elevator", JoystickAxis.Ry, false);
            settings.SetDefaultAxis("aileron", JoystickAxis.X, false);
            settings.SetDefaultAxis("rudder", JoystickAxis.Rz, false);
            settings.SetDefaultAxis("throttle", JoystickAxis.Y, false);

            if (window != null)
            {
                window.KeyDown += code => { if (code >= 0 && code < 256) keys[code] = true; };
                window.KeyUp += code => { if (code >= 0 && code < 256) keys[code] = false; };
            }

            try
            {
                directInput = DInput.DirectInput8Create();
                foreach (DeviceInstance device in directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
                {
                    joystick = directInput.CreateDevice(device.InstanceGuid);
                    joystick.SetCooperativeLevel(windowHandle, CooperativeLevel.NonExclusive | CooperativeLevel.Background);
                    joystick.SetDataFormat<RawJoystickState>();
                    // Legacy range: every axis reports -100..+100.
                    foreach (DeviceObjectInstance objectInstance in joystick.GetObjects(DeviceObjectTypeFlags.AbsoluteAxis))
                        joystick.Properties.Range = new InputRange(-100, 100);
                    joystick.Acquire();
                    JoystickName = device.ProductName;
                    break;
                }
            }
            catch (Exception)
            {
                joystick = null;
                JoystickName = null;
            }
        }

        /// <summary>Polls the joystick; call once per frame (or physics tick).</summary>
        public void Update()
        {
            if (joystick == null)
                return;
            try
            {
                joystick.Poll();
                joystickState = joystick.GetCurrentJoystickState();
            }
            catch (Exception)
            {
                try { joystick.Acquire(); } catch (Exception) { }
            }
        }

        /// <summary>Mapped axis value in -100..+100 (legacy semantics).</summary>
        public int GetAxisValue(string function)
        {
            bool inverted;
            switch (settings.GetAxis(function, out inverted))
            {
                case JoystickAxis.X: return inverted ? -joystickState.X : joystickState.X;
                case JoystickAxis.Y: return inverted ? -joystickState.Y : joystickState.Y;
                case JoystickAxis.Z: return inverted ? -joystickState.Z : joystickState.Z;
                case JoystickAxis.Rx: return inverted ? -joystickState.RotationX : joystickState.RotationX;
                case JoystickAxis.Ry: return inverted ? -joystickState.RotationY : joystickState.RotationY;
                case JoystickAxis.Rz: return inverted ? -joystickState.RotationZ : joystickState.RotationZ;
                case JoystickAxis.Slider1: return Slider(0, inverted);
                case JoystickAxis.Slider2: return Slider(1, inverted);
                default: return 0;
            }
        }

        private int Slider(int index, bool inverted)
        {
            int[] sliders = joystickState.Sliders;
            if (sliders == null || sliders.Length <= index)
                return 0;
            return inverted ? -sliders[index] : sliders[index];
        }

        /// <summary>Raw axis snapshot for calibration UI: name -> value.</summary>
        public IReadOnlyList<KeyValuePair<string, int>> GetRawAxes()
        {
            return new[]
            {
                new KeyValuePair<string, int>("X", joystickState.X),
                new KeyValuePair<string, int>("Y", joystickState.Y),
                new KeyValuePair<string, int>("Z", joystickState.Z),
                new KeyValuePair<string, int>("Rx", joystickState.RotationX),
                new KeyValuePair<string, int>("Ry", joystickState.RotationY),
                new KeyValuePair<string, int>("Rz", joystickState.RotationZ),
            };
        }

        public bool IsKeyDown(InputKey key)
        {
            return keys[(int)key];
        }

        public void Dispose()
        {
            if (joystick != null)
            {
                try { joystick.Unacquire(); } catch (Exception) { }
                joystick.Dispose();
                joystick = null;
            }
            if (directInput != null)
            {
                directInput.Dispose();
                directInput = null;
            }
        }
    }
}
