using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DriverScanTester.Services
{
    public class HealManaSystem
    {
        private readonly GameMemoryService _memoryService;
        private readonly Action<string> _log;
        private bool _wasDead;
        /// <summary>
        /// One-shot flags for the slot-reserve log: while HP/mana potions sit at the
        /// reserve (1), the drink key is skipped and this suppresses the repeat log
        /// until drinking resumes or the need goes away.
        /// </summary>
        private bool _hpReserveLogged;
        private bool _manaReserveLogged;

        // Constants for heal/mana
        private const int VK_1 = BotConstants.HealMana.Vk1;
        private const int VK_2 = BotConstants.HealMana.Vk2;
        private const byte SCAN_CODE_1 = BotConstants.HealMana.ScanCode1; // Scan code for '1'
        private const byte SCAN_CODE_2 = BotConstants.HealMana.ScanCode2; // Scan code for '2'
        private const int KEYEVENTF_KEYUP = BotConstants.Keyboard.KeyEventKeyUp;

        // Shared fallback/manual thresholds. The Bot Window initializes its manual
        // values from player max HP/mana when it opens; workflow instances set profile values.
        public static short Threshold2 = BotConstants.HealMana.MpThreshold;        // Threshold for key '2' (MP?)
        public static short Threshold1 = BotConstants.HealMana.HpThreshold;       // Threshold for key '1' (HP?)

        /// <summary>True when this instance should derive thresholds from player max
        /// HP/mana. Manual Bot Window and workflow instances disable this behavior.</summary>
        private readonly bool _usePlayerBasedThresholds;

        /// <summary>Set once player max HP/mana have been read successfully for an
        /// instance with player-based threshold initialization enabled.</summary>
        private bool _playerThresholdsLoaded;

        [DllImport("user32.dll")]
        static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(nint hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        public HealManaSystem(GameMemoryService memoryService, Action<string> log, bool usePlayerBasedThresholds = true)
        {
            _memoryService = memoryService;
            _log = log;
            _usePlayerBasedThresholds = usePlayerBasedThresholds;

            // Player-based initialization remains available to callers that opt in.
            // The manual Bot Window initializes its thresholds when opened and creates
            // this system with player-based initialization disabled.
            if (_usePlayerBasedThresholds && !TryLoadPlayerThresholds())
            {
                _log("[HealMana] Player max HP/mana not readable yet — will retry while the bot runs.");
            }
        }

        /// <summary>
        /// Reads the player's max HP/mana once and derives the drink thresholds:
        /// 60% of max HP for key '1' and 20% of max mana for key '2'.
        /// Returns false while the player object or max HP is not readable (e.g. not in game).
        /// </summary>
        private bool TryLoadPlayerThresholds()
        {
            var (maxHp, maxMana, success) = _memoryService.GetMaxHpMana();
            if (!success || maxHp <= 0)
                return false;

            Threshold1 = (short)Math.Clamp((int)Math.Round(maxHp * BotConstants.HealMana.HpThresholdFraction), 0, short.MaxValue);
            Threshold2 = (short)Math.Clamp((int)Math.Round(maxMana * BotConstants.HealMana.MpThresholdFraction), 0, short.MaxValue);
            _playerThresholdsLoaded = true;

            int hpPercent = (int)(BotConstants.HealMana.HpThresholdFraction * 100);
            int mpPercent = (int)(BotConstants.HealMana.MpThresholdFraction * 100);
            _log($"[HealMana] Player max HP {maxHp} / max Mana {maxMana} loaded once — thresholds: HP < {Threshold1} ({hpPercent}% max HP), Mana < {Threshold2} ({mpPercent}% max mana).");
            return true;
        }

        public async Task Update(CancellationToken token)
        {
            try
            {
                // For callers that opted into player-based initialization, retry the
                // one-time max HP/mana load until it succeeds.
                if (_usePlayerBasedThresholds && !_playerThresholdsLoaded)
                {
                    TryLoadPlayerThresholds();
                }

                var (val1, val2) = _memoryService.GetHealManaValues();

                // --- Death detection: HP == 0 ---
                if (val1.HasValue && val1.Value <= 0)
                {
                    if (!_wasDead)
                    {
                        _wasDead = true;
                        _log($"[Death] Player HP is 0 — player died. Capturing screenshot.");
                        CaptureDeathScreenshot();
                    }
                }
                else
                {
                    // Reset death flag when HP is restored (player respawned/healed)
                    _wasDead = false;
                }

                // --- Logic for '2' ---
                // Slot reserve: the last mana potion is never drunk — one must stay
                // so the inventory slot keeps its place.
                if (val2.HasValue && val2.Value < Threshold2)
                {
                    if (_memoryService.GetManaPotionCount() <= BotConstants.Repot.PotionSlotReserve)
                    {
                        if (!_manaReserveLogged)
                        {
                            _log($"[HealMana] Mana low ({val2.Value}) but only the slot-reserve potion is left — not drinking.");
                            _manaReserveLogged = true;
                        }
                    }
                    else
                    {
                        _manaReserveLogged = false;
                        _log($"Threshold met for key 2. Value: {val2.Value}. Pressing key.");
                        await PressKey(VK_2, SCAN_CODE_2, token);
                    }
                }
                else
                {
                    _manaReserveLogged = false;
                }

                // --- Logic for '1' ---
                // Slot reserve: the last HP potion is never drunk — one must stay
                // so the inventory slot keeps its place.
                if (val1.HasValue && val1.Value < Threshold1)
                {
                    if (_memoryService.GetHpPotionCount() <= BotConstants.Repot.PotionSlotReserve)
                    {
                        if (!_hpReserveLogged)
                        {
                            _log($"[HealMana] HP low ({val1.Value}) but only the slot-reserve potion is left — not drinking.");
                            _hpReserveLogged = true;
                        }
                    }
                    else
                    {
                        _hpReserveLogged = false;
                        _log($"Threshold met for key 1. Value: {val1.Value}. Pressing key.");
                        await PressKey(VK_1, SCAN_CODE_1, token);
                    }
                }
                else
                {
                    _hpReserveLogged = false;
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                _log($"HealManaSystem error: {ex.Message}");
            }
        }

        /// <summary>
        /// Captures a screenshot of the game window and saves it to Screenshots/Dead/
        /// when the player dies (HP reaches 0).
        /// </summary>
        private void CaptureDeathScreenshot()
        {
            try
            {
                nint hwnd = FindWindow(null, "Legend of Ares");
                if (hwnd == nint.Zero) hwnd = FindWindow(null, "Ares");
                if (hwnd == nint.Zero) hwnd = FindWindow(null, "Nostalgia");
                if (hwnd == nint.Zero) hwnd = FindWindow(null, "Epic Of Ares Client");

                int captureX = 0, captureY = 0, captureW = BotConstants.Loot.BitmapWidth, captureH = BotConstants.Loot.BitmapHeight;

                if (hwnd != nint.Zero)
                {
                    if (GetClientRect(hwnd, out RECT clientRect))
                    {
                        POINT topLeft = new POINT { X = 0, Y = 0 };
                        if (ClientToScreen(hwnd, ref topLeft))
                        {
                            captureX = topLeft.X;
                            captureY = topLeft.Y;
                            captureW = clientRect.Right - clientRect.Left;
                            captureH = clientRect.Bottom - clientRect.Top;
                        }
                    }
                }

                using (Bitmap bitmap = new Bitmap(captureW, captureH))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(captureX, captureY, 0, 0, bitmap.Size);

                    string screenshotsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Screenshots", "Dead");
                    Directory.CreateDirectory(screenshotsDir);

                    string fileName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";
                    string filePath = Path.Combine(screenshotsDir, fileName);

                    bitmap.Save(filePath, ImageFormat.Png);
                    _log($"[Death] Screenshot saved: {filePath}");
                }
            }
            catch (Exception ex)
            {
                _log($"[Death] Failed to capture death screenshot: {ex.Message}");
            }
        }

        private async Task PressKey(byte vkCode, byte scanCode, CancellationToken token)
        {
            keybd_event(vkCode, scanCode, 0, 0);
            await Task.Delay(BotConstants.Delays.HealManaKeyPressMs, token); // Small delay to simulate key press duration
            keybd_event(vkCode, scanCode, (uint)KEYEVENTF_KEYUP, 0);
        }
    }
}
