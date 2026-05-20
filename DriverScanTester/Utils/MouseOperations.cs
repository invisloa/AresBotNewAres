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

        public static void SetCursorPosition(int x, int y)
        {
            SetCursorPos(x, y);
        }

        public static void MouseEvent(MouseEventFlags value)
        {
            // mouse_event uses current position if dx, dy are 0 and Absolute flag is not set
            // The old code did GetCursorPos then mouse_event with position.
            // But usually 0,0 is fine if Absolute is not set.
            // Let's verify old code.

            // Old code:
            // MousePoint position = GetCursorPosition();
			// mouse_event((int)value, position.X, position.Y, 0, 0);

            // It seems redundant to pass X,Y if not using Absolute, but maybe safer.
            // I'll stick to 0,0 which means "current position" for relative moves or clicks.
            mouse_event((int)value, 0, 0, 0, 0);
        }

        public static void LeftClick()
        {
            MouseEvent(MouseEventFlags.LeftDown);
            Thread.Sleep(50); // Small delay
            MouseEvent(MouseEventFlags.LeftUp);
        }

        public static void RightClick()
        {
            MouseEvent(MouseEventFlags.RightDown);
            Thread.Sleep(50);
            MouseEvent(MouseEventFlags.RightUp);
        }

        public static void MoveAndLeftClick(int x, int y, int delay = 100)
        {
            SetCursorPosition(x, y);
            Thread.Sleep(delay);
            LeftClick();
            Thread.Sleep(delay);
        }
    }
}
