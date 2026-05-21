using System.Runtime.InteropServices;
using System.Threading;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Static helper for low-level keyboard input via user32.dll.
    /// Contains key constants and the PressKey helper used across the bot.
    /// </summary>
    internal static class GameInput
    {
        [DllImport("user32.dll")]
        internal static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        internal const int KEYEVENTF_KEYUP = 0x0002;

        // Virtual-key and scan codes
        internal const byte VK_W = 0x57;
        internal const byte SCAN_W = 0x11;
        internal const byte VK_TAB = 0x09;
        internal const byte SCAN_TAB = 0x0F;
        internal const byte VK_3 = 0x33;
        internal const byte SCAN_3 = 0x04;
        internal const byte VK_7 = 0x37;
        internal const byte SCAN_7 = 0x08;
        internal const byte VK_8 = 0x38;
        internal const byte SCAN_8 = 0x09;
        internal const byte VK_6 = 0x36;
        internal const byte SCAN_6 = 0x07;
        internal const byte VK_A = 0x41;
        internal const byte SCAN_A = 0x1E;
        internal const byte VK_D = 0x44;
        internal const byte SCAN_D = 0x20;

        /// <summary>
        /// Presses (down + up) a key with a 20 ms gap between down and up.
        /// </summary>
        internal static void PressKey(byte vk, byte scan)
        {
            keybd_event(vk, scan, 0, 0);
            Thread.Sleep(20);
            keybd_event(vk, scan, (uint)KEYEVENTF_KEYUP, 0);
        }
    }
}
