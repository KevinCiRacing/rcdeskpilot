using System;
using System.Runtime.InteropServices;

namespace Bonsai.Graphics.Win32
{
    /// <summary>
    /// A raw Win32 top-level window with a polling message pump: the Sim's
    /// window host. Supports windowed and borderless-fullscreen. The renderer
    /// itself never sees this type - it only receives the HWND.
    /// </summary>
    public sealed class Win32Window : IDisposable
    {
        private const string ClassName = "BonsaiWindowClass";

        private static Win32Native.WndProc wndProcKeepAlive;
        private static Win32Window instance;

        private Win32Native.RECT windowedRect;
        private bool isFullscreen;
        private bool isMinimized;

        public IntPtr Handle { get; private set; }
        public int ClientWidth { get; private set; }
        public int ClientHeight { get; private set; }
        public bool IsClosed { get; private set; }
        public bool IsMinimized { get { return isMinimized; } }
        public bool IsFullscreen { get { return isFullscreen; } }

        /// <summary>Optional first-chance message hook (e.g. ImGui). A nonzero
        /// return means the message was handled.</summary>
        public Func<IntPtr, uint, IntPtr, IntPtr, IntPtr> MessageHook;

        /// <summary>Raised when the client area size changes (not while minimized).</summary>
        public event Action<int, int> Resized;
        /// <summary>Raised on WM_KEYDOWN/WM_SYSKEYDOWN with the virtual-key code.</summary>
        public event Action<int> KeyDown;
        /// <summary>Raised on WM_KEYUP/WM_SYSKEYUP with the virtual-key code.</summary>
        public event Action<int> KeyUp;
        public event Action Closed;

        public Win32Window(string title, int clientWidth, int clientHeight)
        {
            if (instance != null)
                throw new InvalidOperationException("Only one Win32Window is supported.");
            instance = this;

            IntPtr hInstance = Win32Native.GetModuleHandleW(null);
            wndProcKeepAlive = StaticWndProc;

            var wc = new Win32Native.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<Win32Native.WNDCLASSEX>(),
                style = 0x0003, // CS_HREDRAW | CS_VREDRAW
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProcKeepAlive),
                hInstance = hInstance,
                hCursor = Win32Native.LoadCursorW(IntPtr.Zero, Win32Native.IDC_ARROW),
                lpszClassName = ClassName,
            };
            if (Win32Native.RegisterClassExW(ref wc) == 0)
                throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastWin32Error());

            var rect = new Win32Native.RECT { Left = 0, Top = 0, Right = clientWidth, Bottom = clientHeight };
            Win32Native.AdjustWindowRect(ref rect, Win32Native.WS_OVERLAPPEDWINDOW, false);

            Handle = Win32Native.CreateWindowExW(0, ClassName, title,
                Win32Native.WS_OVERLAPPEDWINDOW | Win32Native.WS_VISIBLE,
                100, 100, rect.Right - rect.Left, rect.Bottom - rect.Top,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (Handle == IntPtr.Zero)
                throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());

            Win32Native.RECT client;
            Win32Native.GetClientRect(Handle, out client);
            ClientWidth = client.Right - client.Left;
            ClientHeight = client.Bottom - client.Top;
        }

        /// <summary>Pumps all pending messages. Returns false once the window has closed.</summary>
        public bool PumpMessages()
        {
            Win32Native.MSG msg;
            while (Win32Native.PeekMessageW(out msg, IntPtr.Zero, 0, 0, Win32Native.PM_REMOVE))
            {
                if (msg.message == 0x0012) // WM_QUIT
                {
                    IsClosed = true;
                    break;
                }
                Win32Native.TranslateMessage(ref msg);
                Win32Native.DispatchMessageW(ref msg);
            }
            return !IsClosed;
        }

        /// <summary>Toggles borderless fullscreen on the window's current monitor.</summary>
        public void SetFullscreen(bool fullscreen)
        {
            if (fullscreen == isFullscreen)
                return;
            isFullscreen = fullscreen;

            if (fullscreen)
            {
                Win32Native.GetWindowRect(Handle, out windowedRect);
                IntPtr monitor = Win32Native.MonitorFromWindow(Handle, Win32Native.MONITOR_DEFAULTTONEAREST);
                var info = new Win32Native.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Win32Native.MONITORINFO>() };
                Win32Native.GetMonitorInfoW(monitor, ref info);

                Win32Native.SetWindowLongPtr(Handle, Win32Native.GWL_STYLE, new IntPtr(Win32Native.WS_POPUP | Win32Native.WS_VISIBLE));
                Win32Native.SetWindowPos(Handle, IntPtr.Zero,
                    info.rcMonitor.Left, info.rcMonitor.Top,
                    info.rcMonitor.Right - info.rcMonitor.Left,
                    info.rcMonitor.Bottom - info.rcMonitor.Top,
                    Win32Native.SWP_FRAMECHANGED | Win32Native.SWP_NOOWNERZORDER);
            }
            else
            {
                Win32Native.SetWindowLongPtr(Handle, Win32Native.GWL_STYLE, new IntPtr(Win32Native.WS_OVERLAPPEDWINDOW | Win32Native.WS_VISIBLE));
                Win32Native.SetWindowPos(Handle, IntPtr.Zero,
                    windowedRect.Left, windowedRect.Top,
                    windowedRect.Right - windowedRect.Left,
                    windowedRect.Bottom - windowedRect.Top,
                    Win32Native.SWP_FRAMECHANGED | Win32Native.SWP_NOOWNERZORDER);
                Win32Native.ShowWindow(Handle, Win32Native.SW_RESTORE);
            }
        }

        /// <summary>Resizes the client area (windowed mode only; used by tests).</summary>
        public void SetClientSize(int width, int height)
        {
            var rect = new Win32Native.RECT { Left = 0, Top = 0, Right = width, Bottom = height };
            Win32Native.AdjustWindowRect(ref rect, Win32Native.WS_OVERLAPPEDWINDOW, false);
            Win32Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0,
                rect.Right - rect.Left, rect.Bottom - rect.Top,
                0x0002 /*SWP_NOMOVE*/ | Win32Native.SWP_NOOWNERZORDER);
        }

        private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            Win32Window window = instance;
            if (window != null && hWnd == window.Handle)
            {
                var hook = window.MessageHook;
                if (hook != null)
                {
                    IntPtr handled = hook(hWnd, msg, wParam, lParam);
                    if (handled != IntPtr.Zero)
                        return handled;
                }
                switch (msg)
                {
                    case Win32Native.WM_SIZE:
                    {
                        window.isMinimized = (int)wParam == Win32Native.SIZE_MINIMIZED;
                        int w = (int)((long)lParam & 0xFFFF);
                        int h = (int)(((long)lParam >> 16) & 0xFFFF);
                        if (!window.isMinimized && w > 0 && h > 0 &&
                            (w != window.ClientWidth || h != window.ClientHeight))
                        {
                            window.ClientWidth = w;
                            window.ClientHeight = h;
                            var resized = window.Resized;
                            if (resized != null)
                                resized(w, h);
                        }
                        return IntPtr.Zero;
                    }
                    case Win32Native.WM_KEYDOWN:
                    case Win32Native.WM_SYSKEYDOWN:
                    {
                        var keyDown = window.KeyDown;
                        if (keyDown != null)
                            keyDown((int)wParam);
                        break;
                    }
                    case 0x0101: // WM_KEYUP
                    case 0x0105: // WM_SYSKEYUP
                    {
                        var keyUp = window.KeyUp;
                        if (keyUp != null)
                            keyUp((int)wParam);
                        break;
                    }
                    case Win32Native.WM_CLOSE:
                        Win32Native.DestroyWindow(hWnd);
                        return IntPtr.Zero;
                    case Win32Native.WM_DESTROY:
                    {
                        window.IsClosed = true;
                        var closed = window.Closed;
                        if (closed != null)
                            closed();
                        Win32Native.PostQuitMessage(0);
                        return IntPtr.Zero;
                    }
                }
            }
            return Win32Native.DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            if (!IsClosed && Handle != IntPtr.Zero)
                Win32Native.DestroyWindow(Handle);
            instance = null;
        }
    }
}
