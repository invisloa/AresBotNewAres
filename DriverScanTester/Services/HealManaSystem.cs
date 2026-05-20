using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DriverScanTester.Services
{
    public class HealManaSystem
    {
        private readonly GameMemoryService _memoryService;
        private readonly Action<string> _log;

        // Constants for heal/mana
        private const int VK_1 = 0x31;
        private const int VK_2 = 0x32;
        private const byte SCAN_CODE_1 = 0x02; // Scan code for '1'
        private const byte SCAN_CODE_2 = 0x03; // Scan code for '2'
        private const int KEYEVENTF_KEYUP = 0x0002;

        // Thresholds
        public static short Threshold2 = 50;        // Threshold for key '2' (MP?)
        public static short Threshold1 = 250;       // Threshold for key '1' (HP?)

        [DllImport("user32.dll")]
        static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        public HealManaSystem(GameMemoryService memoryService, Action<string> log)
        {
            _memoryService = memoryService;
            _log = log;
        }

        public async Task Update(CancellationToken token)
        {
            try
            {
                var (val1, val2) = _memoryService.GetHealManaValues();

                // --- Logic for '2' ---
                if (val2.HasValue && val2.Value < Threshold2)
                {
                    _log($"Threshold met for key 2. Value: {val2.Value}. Pressing key.");
                    await PressKey(VK_2, SCAN_CODE_2, token);
                }

                // --- Logic for '1' ---
                if (val1.HasValue && val1.Value < Threshold1)
                {
                    _log($"Threshold met for key 1. Value: {val1.Value}. Pressing key.");
                    await PressKey(VK_1, SCAN_CODE_1, token);
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                _log($"HealManaSystem error: {ex.Message}");
            }
        }

        private async Task PressKey(byte vkCode, byte scanCode, CancellationToken token)
        {
            keybd_event(vkCode, scanCode, 0, 0);
            await Task.Delay(50, token); // Small delay to simulate key press duration
            keybd_event(vkCode, scanCode, (uint)KEYEVENTF_KEYUP, 0);
        }
    }
}
