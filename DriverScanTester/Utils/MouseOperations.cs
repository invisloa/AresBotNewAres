using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace DriverScanTester.Utils
{
    public static class MouseOperations
    {
        [Flags]
        public enum MouseEventFlags
        {
            LeftDown = 0x00000002,
            LeftUp = 0x00000004,
            MiddleDown = 0x00000020,
            MiddleUp = 0x00000040,
            Move = 0x00000001,
            Absolute = 0x00008000,
            RightDown = 0x00000008,
            RightUp = 0x00000010
        }

        [DllImport("user32.dll", EntryPoint = "SetCursorPos")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(int dwFlags, int dx, int dy, int dwData, int dwExtraInfo);

        // ── Debug logging ──
        /// <summary>
        /// Optional callback for logging every mouse action. Set before bot operations.
        /// </summary>
        public static Action<string>? Log { get; set; }

        // ── Window-offset support ──
        private static int _windowOffsetX;
        private static int _windowOffsetY;

        public static void SetWindowOffset(int offsetX, int offsetY)
        {
            _windowOffsetX = offsetX;
            _windowOffsetY = offsetY;
        }

        public static void ResetWindowOffset()
        {
            _windowOffsetX = 0;
            _windowOffsetY = 0;
        }

        public static void SetCursorPosition(int x, int y)
        {
            int screenX = x + _windowOffsetX;
            int screenY = y + _windowOffsetY;
            Log?.Invoke($"[Mouse] Move -> ({screenX}, {screenY})  (hardcoded ({x},{y}) + offset ({_windowOffsetX},{_windowOffsetY}))");
            SetCursorPos(screenX, screenY);
        }

        public static void MouseEvent(MouseEventFlags value)
        {
            mouse_event((int)value, 0, 0, 0, 0);
        }

        public static void LeftClick()
        {
            Log?.Invoke($"[Mouse] LeftClick");
            MouseEvent(MouseEventFlags.LeftDown);
            Thread.Sleep(50);
            MouseEvent(MouseEventFlags.LeftUp);
        }

        public static void RightClick()
        {
            Log?.Invoke($"[Mouse] RightClick");
            MouseEvent(MouseEventFlags.RightDown);
            Thread.Sleep(50);
            MouseEvent(MouseEventFlags.RightUp);
        }

        public static void MoveAndLeftClick(int x, int y, int delay = 100)
        {
            Log?.Invoke($"[Mouse] MoveAndLeftClick -> hardcoded ({x},{y}) + offset ({_windowOffsetX},{_windowOffsetY}) = screen ({x+_windowOffsetX},{y+_windowOffsetY})");
            SetCursorPosition(x, y);
            Thread.Sleep(delay);
            LeftClick();
            Thread.Sleep(delay);
        }

        /// <summary>
        /// Moves the cursor to an absolute screen position WITHOUT adding window offset.
        /// Use this for coordinates that already account for window position.
        /// </summary>
        public static void MoveAndLeftClickAbsolute(int screenX, int screenY, int delay = 100)
        {
            Log?.Invoke($"[Mouse] MoveAndLeftClickAbsolute -> screen ({screenX},{screenY}) [no offset applied]");
            SetCursorPos(screenX, screenY);
            Thread.Sleep(delay);
            LeftClick();
            Thread.Sleep(delay);
        }

        public static void MoveAndRightClick(int x, int y, int delay = 100)
        {
            Log?.Invoke($"[Mouse] MoveAndRightClick -> hardcoded ({x},{y}) + offset ({_windowOffsetX},{_windowOffsetY}) = screen ({x+_windowOffsetX},{y+_windowOffsetY})");
            SetCursorPosition(x, y);
            Thread.Sleep(delay);
            RightClick();
            Thread.Sleep(delay);
        }

        public static void OpenInventoryTab1()
        {
            Log?.Invoke($"[Mouse] OpenInventoryTab1 -> hardcoded (1165,515) + offset ({_windowOffsetX},{_windowOffsetY}) = screen ({1165+_windowOffsetX},{515+_windowOffsetY})");
            SetCursorPosition(1165, 515);
            Thread.Sleep(50);
            LeftClick();
            Thread.Sleep(50);
        }

        public static void OpenInventoryTab2()
        {
            Log?.Invoke($"[Mouse] OpenInventoryTab2 -> hardcoded (1235,670) + offset ({_windowOffsetX},{_windowOffsetY}) = screen ({1235+_windowOffsetX},{670+_windowOffsetY})");
            SetCursorPosition(1235, 670);
            Thread.Sleep(50);
            LeftClick();
            Thread.Sleep(50);
        }

        /// <summary>
        /// Opens inventory tab 1 at an absolute screen position (no offset applied).
        /// </summary>
        public static void OpenInventoryTab1Absolute(int screenX, int screenY)
        {
            Log?.Invoke($"[Mouse] OpenInventoryTab1Absolute -> screen ({screenX},{screenY}) [no offset]");
            SetCursorPos(screenX, screenY);
            Thread.Sleep(50);
            LeftClick();
            Thread.Sleep(50);
        }

        /// <summary>
        /// Opens inventory tab 2 at an absolute screen position (no offset applied).
        /// </summary>
        public static void OpenInventoryTab2Absolute(int screenX, int screenY)
        {
            Log?.Invoke($"[Mouse] OpenInventoryTab2Absolute -> screen ({screenX},{screenY}) [no offset]");
            SetCursorPos(screenX, screenY);
            Thread.Sleep(50);
            LeftClick();
            Thread.Sleep(50);
        }

        /// <summary>
        /// Left click at an absolute screen position (no offset applied).
        /// </summary>
        public static void LeftClickAbsolute(int screenX, int screenY, int delay = 100)
        {
            Log?.Invoke($"[Mouse] LeftClickAbsolute -> screen ({screenX},{screenY}) [no offset]");
            SetCursorPos(screenX, screenY);
            Thread.Sleep(delay);
            LeftClick();
            Thread.Sleep(delay);
        }
    }
}
