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

        internal const int KEYEVENTF_KEYUP = BotConstants.Keyboard.KeyEventKeyUp;

        // Virtual-key and scan codes
        internal const byte VK_W = BotConstants.Keyboard.VkW;
        internal const byte SCAN_W = BotConstants.Keyboard.ScanW;
        internal const byte VK_TAB = BotConstants.Keyboard.VkTab;
        internal const byte SCAN_TAB = BotConstants.Keyboard.ScanTab;
        internal const byte VK_3 = BotConstants.Keyboard.Vk3;
        internal const byte SCAN_3 = BotConstants.Keyboard.Scan3;
        internal const byte VK_7 = BotConstants.Keyboard.Vk7;
        internal const byte SCAN_7 = BotConstants.Keyboard.Scan7;
        internal const byte VK_8 = BotConstants.Keyboard.Vk8;
        internal const byte SCAN_8 = BotConstants.Keyboard.Scan8;
        internal const byte VK_6 = BotConstants.Keyboard.Vk6;
        internal const byte SCAN_6 = BotConstants.Keyboard.Scan6;
        internal const byte VK_A = BotConstants.Keyboard.VkA;
        internal const byte SCAN_A = BotConstants.Keyboard.ScanA;
        internal const byte VK_D = BotConstants.Keyboard.VkD;
        internal const byte SCAN_D = BotConstants.Keyboard.ScanD;

        /// <summary>
        /// Presses (down + up) a key with a 20 ms gap between down and up.
        /// </summary>
        internal static void PressKey(byte vk, byte scan)
        {
            keybd_event(vk, scan, 0, 0);
            Thread.Sleep(BotConstants.Keyboard.PressKeyGapMs);
            keybd_event(vk, scan, (uint)KEYEVENTF_KEYUP, 0);
        }
    }
}
