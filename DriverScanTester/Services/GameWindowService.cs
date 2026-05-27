using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Detects the game window's current screen position and provides coordinate translation
    /// so that hardcoded mouse positions (designed for a fixed window location) are adjusted
    /// to work with the actual window position.
    ///
    /// The hardcoded coordinates in RepotMousePositions, BotConstants, etc. assume the game
    /// window's client area top-left corner is at screen coordinate (0, 0). If the window is
    /// positioned elsewhere (e.g., centered, multi-monitor, etc.), this service calculates the
    /// needed offset.
    ///
    /// You can provide manual override offsets (e.g. from profile) — if non-zero, they are used
    /// directly instead of auto-detection.
    /// </summary>
    public class GameWindowService
    {
        private readonly uint _pid;
        private int _originX;
        private int _originY;
        private bool _initialized;
        private int _manualOffsetX;
        private int _manualOffsetY;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(nint hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint FindWindow(string lpClassName, string lpWindowName);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        public GameWindowService(uint pid)
        {
            _pid = pid;
        }

        /// <summary>
        /// Whether the service has been initialized with the window position.
        /// </summary>
        public bool IsInitialized => _initialized;

        /// <summary>
        /// Gets the window's top-left corner in screen coordinates (from GetWindowRect).
        /// </summary>
        public (int X, int Y) WindowOrigin => _initialized ? (_originX, _originY) : (0, 0);

        /// <summary>
        /// Sets manual override offsets. If non-zero, these will be used instead of auto-detection.
        /// Call before <see cref="Initialize"/>.
        /// </summary>
        public void SetManualOffset(int offsetX, int offsetY)
        {
            _manualOffsetX = offsetX;
            _manualOffsetY = offsetY;
        }

        /// <summary>
        /// Initializes the service. If manual offsets were provided (non-zero), they are used directly.
        /// Otherwise auto-detects the window position via GetWindowRect.
        /// </summary>
        /// <param name="fallbackWindowTitle">Optional window title to search by if process handle fails.</param>
        public void Initialize(string fallbackWindowTitle = "Legend of Ares")
        {
            // Manual override takes priority
            if (_manualOffsetX != 0 || _manualOffsetY != 0)
            {
                _originX = _manualOffsetX;
                _originY = _manualOffsetY;
                _initialized = true;
                Debug.WriteLine($"[GameWindowService] Using manual offset: ({_originX}, {_originY})");
                return;
            }

            nint hwnd = FindWindowByProcess();
            if (hwnd == nint.Zero)
            {
                hwnd = FindWindow(null, fallbackWindowTitle);
            }
            if (hwnd == nint.Zero)
            {
                hwnd = FindWindow(null, "Nostalgia");
            }
            if (hwnd == nint.Zero)
            {
                hwnd = FindWindow(null, "Epic Of Ares Client");
            }

            if (hwnd == nint.Zero)
            {
                Debug.WriteLine("[GameWindowService] Could not find game window. Using (0,0) offset.");
                _originX = 0;
                _originY = 0;
                _initialized = true;
                return;
            }

            // Get window position on screen (includes title bar and borders)
            if (!GetWindowRect(hwnd, out RECT windowRect))
            {
                Debug.WriteLine("[GameWindowService] GetWindowRect failed. Using (0,0).");
                _originX = 0;
                _originY = 0;
                _initialized = true;
                return;
            }

            _originX = windowRect.Left;
            _originY = windowRect.Top;

            Debug.WriteLine($"[GameWindowService] Game window top-left at screen: ({_originX}, {_originY}), " +
                          $"size: {windowRect.Right - windowRect.Left}x{windowRect.Bottom - windowRect.Top}");

            _initialized = true;
        }

        /// <summary>
        /// Translates hardcoded coordinates (designed for origin 0,0) to actual screen coordinates
        /// by adding the stored window offset.
        /// </summary>
        public (int X, int Y) Translate(int hardcodedX, int hardcodedY)
        {
            if (!_initialized)
            {
                Initialize();
            }
            return (hardcodedX + _originX, hardcodedY + _originY);
        }

        /// <summary>
        /// Translates a single X coordinate.
        /// </summary>
        public int TranslateX(int hardcodedX) => hardcodedX + (_initialized ? _originX : 0);

        /// <summary>
        /// Translates a single Y coordinate.
        /// </summary>
        public int TranslateY(int hardcodedY) => hardcodedY + (_initialized ? _originY : 0);

        private nint FindWindowByProcess()
        {
            try
            {
                var proc = Process.GetProcessById((int)_pid);
                if (proc != null && !proc.HasExited)
                {
                    nint hwnd = proc.MainWindowHandle;
                    if (hwnd != nint.Zero)
                    {
                        return hwnd;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GameWindowService] FindWindowByProcess error: {ex.Message}");
            }
            return nint.Zero;
        }
    }
}
