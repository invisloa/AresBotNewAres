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
        internal const byte VK_ESCAPE = BotConstants.Keyboard.VkEscape;
        internal const byte SCAN_ESCAPE = BotConstants.Keyboard.ScanEscape;
        internal const byte VK_SPACE = BotConstants.Keyboard.VkSpace;
        internal const byte SCAN_SPACE = BotConstants.Keyboard.ScanSpace;
        internal const byte VK_ENTER = BotConstants.Keyboard.VkEnter;
        internal const byte SCAN_ENTER = BotConstants.Keyboard.ScanEnter;

        /// <summary>
        /// Presses (down + up) a key with a 20 ms gap between down and up.
        /// </summary>
        internal static void PressKey(byte vk, byte scan)
        {
            keybd_event(vk, scan, 0, 0);
            Thread.Sleep(BotConstants.Keyboard.PressKeyGapMs);
            keybd_event(vk, scan, (uint)KEYEVENTF_KEYUP, 0);
        }

        /// <summary>
        /// Types a plain-text string by pressing each character's virtual-key code.
        /// Supports letters, digits, space and a few common punctuation characters;
        /// unsupported characters are skipped and logged through the provided callback.
        /// </summary>
        internal static void TypeText(string text, Action<string>? log = null)
        {
            foreach (char c in text)
            {
                byte vk = CharToVk(c);
                if (vk == 0)
                {
                    log?.Invoke($"[TypeText] Skipping unsupported character '{c}'.");
                    continue;
                }
                PressKey(vk, 0);
                Thread.Sleep(BotConstants.Keyboard.PressKeyGapMs);
            }
        }

        /// <summary>
        /// Maps a character to its virtual-key code for keybd_event. Letters are
        /// sent unshifted (uppercase VK codes produce lowercase text in most games).
        /// </summary>
        private static byte CharToVk(char c)
        {
            if (c >= 'a' && c <= 'z') return (byte)(VK_A + (c - 'a'));
            if (c >= 'A' && c <= 'Z') return (byte)c;
            if (c >= '0' && c <= '9') return (byte)c;
            switch (c)
            {
                case ' ': return VK_SPACE;
                case '.': return 0xBE; // VK_OEM_PERIOD
                case ',': return 0xBC; // VK_OEM_COMMA
                case '-': return 0xBD; // VK_OEM_MINUS
                case '_': return 0xBD; // VK_OEM_MINUS (underscore uses shift; sent unshifted as '-')
                case '/': return 0xBF; // VK_OEM_2
                case '!': return 0x31; // VK_1 (sent unshifted; actual '!' needs shift)
                case '?': return 0xBF; // VK_OEM_2 (sent unshifted; actual '?' needs shift)
                default: return 0;
            }
        }
    }
}
