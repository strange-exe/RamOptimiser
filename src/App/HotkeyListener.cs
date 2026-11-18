// RegisterHotKey for the global Clean memory shortcut (R7), on a message-only window of the UI thread.
// Docs: RegisterHotKey, https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey
// (it fails for a window of another thread; the same hWnd and id registered again keeps both, so it is always
// unregistered first); WM_HOTKEY (0x0312), https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-hotkey
// (wParam is the id). The decisions (which keys, when to register, the 60 s floor) live in Logic/Hotkey.cs.
using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace RamOptimiser.App
{
    /// <summary>
    /// Owns one hotkey id on a message-only window (HWND_MESSAGE: it gets no broadcasts and never shows), created on
    /// the UI thread, so it works under --tray, where the main window has no handle yet. Only created when the app
    /// runs elevated. Pressed is raised on the UI thread.
    /// </summary>
    internal sealed class HotkeyListener : IHotkeyRegistrar, IDisposable
    {
        const int HotkeyId = 1;
        const int WmHotkey = 0x0312;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        readonly HwndSource source;
        readonly HwndSourceHook hook;
        bool registered, disposed;

        public event EventHandler Pressed;

        public HotkeyListener()
        {
            var p = new HwndSourceParameters("RamOptimiser hotkey");
            p.ParentWindow = new IntPtr(-3); // HWND_MESSAGE
            p.WindowStyle = 0;
            source = new HwndSource(p);
            hook = WndProc;
            source.AddHook(hook);
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmHotkey && wParam.ToInt64() == HotkeyId)
            {
                handled = true;
                EventHandler h = Pressed;
                if (h != null && !disposed && registered) h(this, EventArgs.Empty);
            }
            return IntPtr.Zero;
        }

        public bool TryRegister(HotkeyGesture g, out int error)
        {
            error = 0;
            if (disposed || g == null) { error = 6; return false; } // ERROR_INVALID_HANDLE
            Unregister();
            if (RegisterHotKey(source.Handle, HotkeyId, g.RegisterFlags, g.Key))
            {
                registered = true;
                return true;
            }
            error = Marshal.GetLastWin32Error();
            return false;
        }

        public void Unregister()
        {
            if (!registered || disposed) return;
            UnregisterHotKey(source.Handle, HotkeyId);
            registered = false;
        }

        public void Dispose()
        {
            if (disposed) return;
            try { Unregister(); }
            finally
            {
                disposed = true;
                source.RemoveHook(hook);
                source.Dispose();
            }
        }
    }
}
