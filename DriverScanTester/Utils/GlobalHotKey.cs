using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DriverScanTester.Utils
{
    /// <summary>
    /// Registers a global hotkey (system-wide) that fires even when the window is not focused.
    /// </summary>
    public sealed class GlobalHotKey : IDisposable
    {
        private readonly int _id;
        private readonly Window _window;
        private HwndSource _hwndSource;
        private bool _disposed;

        public event Action HotKeyPressed;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        /// <summary>
        /// Registers a global hotkey for the specified window.
        /// </summary>
        /// <param name="window">The window that will own the hotkey (its handle is used for message dispatch).</param>
        /// <param name="key">The virtual-key code (e.g., 0x70 for F1).</param>
        /// <param name="modifiers">Modifier flags (MOD_ALT=0x1, MOD_CONTROL=0x2, MOD_SHIFT=0x4, MOD_WIN=0x8, or 0 for none).</param>
        /// <param name="id">Unique hotkey ID (use a unique value to avoid conflicts).</param>
        public GlobalHotKey(Window window, uint key, uint modifiers = 0, int id = 9000)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _id = id;

            // Ensure the window handle is created
            var helper = new WindowInteropHelper(window);
            var handle = helper.EnsureHandle();

            _hwndSource = HwndSource.FromHwnd(handle);
            if (_hwndSource != null)
            {
                _hwndSource.AddHook(WndProc);
            }
            else
            {
                Debug.WriteLine("HwndSource.FromHwnd returned null – WndProc hook not installed. " +
                                "Global hotkey events will NOT be processed.");
            }

            if (!RegisterHotKey(handle, _id, modifiers, key))
            {
                int error = Marshal.GetLastWin32Error();
                Debug.WriteLine($"RegisterHotKey failed (error={error}). Global hotkey will not work.");
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && (int)wParam == _id)
            {
                HotKeyPressed?.Invoke();
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_hwndSource != null)
            {
                _hwndSource.RemoveHook(WndProc);
                _hwndSource = null;
            }

            try
            {
                var handle = new WindowInteropHelper(_window).Handle;
                if (handle != IntPtr.Zero)
                    UnregisterHotKey(handle, _id);
            }
            catch { /* ignore cleanup errors */ }
        }
    }
}
