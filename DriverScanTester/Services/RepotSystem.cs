using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;
using DriverScanTester.Utils;

namespace DriverScanTester.Services
{
    public class RepotSystem
    {
        private readonly GameMemoryService _memoryService;
        private readonly Action<string> _log;
        private readonly ItemSellerService _itemSeller;

        public RepotSystem(GameMemoryService memoryService, Action<string> log)
        {
            _memoryService = memoryService;
            _log = log;
            _itemSeller = new ItemSellerService(memoryService, log);
        }

        // --- Potion buy targets (can be overridden per-profile) ---
        /// <summary>Target count for HP potions (buy up to this many). Default = BotConstants.Repot.HpBuyTarget.</summary>
        public int HpBuyTarget { get; set; } = BotConstants.Repot.HpBuyTarget;
        /// <summary>Target count for Mana potions (buy up to this many). Default = BotConstants.Repot.ManaBuyTarget.</summary>
        public int ManaBuyTarget { get; set; } = BotConstants.Repot.ManaBuyTarget;
        /// <summary>Target count for Red potions (buy up to this many). Default = BotConstants.Repot.RedBuyTarget.</summary>
        public int RedBuyTarget { get; set; } = BotConstants.Repot.RedBuyTarget;
        /// <summary>Target count for White potions (buy up to this many). Default = BotConstants.Repot.WhiteBuyTarget.</summary>
        public int WhiteBuyTarget { get; set; } = BotConstants.Repot.WhiteBuyTarget;

        #region State Checks

        public bool IsShopOpen()
        {
            return _memoryService.IsShopOpen();
        }

        public int GetManaPotionCount()
        {
            return _memoryService.GetManaPotionCount();
        }

        public int GetRedPotionCount()
        {
            return _memoryService.GetRedPotionCount();
        }

        public int GetWhitePotionCount()
        {
            return _memoryService.GetWhitePotionCount();
        }

        public int GetHpPotionCount()
        {
            return _memoryService.GetHpPotionCount();
        }

        public int GetSorPotionCount()
        {
            return _memoryService.GetSorPotionCount();
        }

        public int GetInventoryItemType(int slotIndex)
        {
            return _memoryService.GetInventoryItemType(slotIndex);
        }

        public bool IsSellConfirmWindowOpen()
        {
            return _memoryService.IsSellConfirmWindowOpen();
        }

        #endregion

        #region Shop Operations

        // Window-relative position for clicking the shop NPC.
        // With window at (541,91), shop click should be at (685,550).
        // Relative to window: (685-541, 550-91) = (144, 459).
        // Calibrated to give screen (590,565) at window (445,105).
        // NOTE: kept as a fallback only — the primary open path is the
        // seller mouseover scan (see OpenShop), which locates the NPC by
        // sweeping the view until the mouse points at it.
        private const int ShopRelX = 145;
        private const int ShopRelY = 460;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        public void OpenShop()
        {
            _log("Opening Shop Window...");
            Thread.Sleep(BotConstants.Delays.OpenShopInitialMs);

            // Primary path: zoom the camera to the known sell view, then scan the
            // game window from the center outward until the mouse hovers the seller
            // NPC (S_IsSellerPointed), right-click it, click the Shop option and
            // verify the shop window actually opened.
            if (_itemSeller.OpenSellerDialog())
            {
                _log("Shop Window Opened.");
                return;
            }

            // Fallback: single fixed-position click (window-relative calibration).
            (int clickX, int clickY) = GetShopClickPosition();
            _log($"Shop click absolute: ({clickX}, {clickY})");
            MouseOperations.MoveAndLeftClickAbsolute(clickX, clickY, 200);

            // Wait for shop to open?
            int retries = 0;
            while (!IsShopOpen() && retries < BotConstants.Repot.OpenShopRetries)
            {
                Thread.Sleep(BotConstants.Delays.OpenShopRetryMs);
                retries++;
            }
            if (IsShopOpen()) _log("Shop Window Opened.");
            else _log("Failed to open Shop Window.");
        }

        public void BuyPotions()
        {
            _log("Buying Potions...");
            // City-specific buy positions: Kharon, Etana and Hershal each have their
            // own shop layout; everything else falls back to the Hershal positions.
            var positions = _memoryService.GetCurrentMap() switch
            {
                ItemSellerService.MapKharon => RepotMousePositions.mousePositionsForKharonBuying,
                ItemSellerService.MapEtana => RepotMousePositions.mousePositionsForEtanBuying,
                _ => RepotMousePositions.mousePositionsForHershalBuying
            };

            // Buy order is ALWAYS: HP → Mana → Red → White → SOR.
            // Slot index per potion: 0=mana, 1=red, 2=white, 3=hp, 4=SOR.
            int[] buyOrder = { 3, 0, 1, 2, 4 };

            foreach (int i in buyOrder)
            {
                // Skip slots the city's layout does not have (e.g. no SOR in Kharon/Hershal yet).
                if (i >= positions.Length)
                    continue;

                Thread.Sleep(1000);

                // HP Potions (Index 3)
                if (i == 3 && GetHpPotionCount() < HpBuyTarget)
                {
                    ClickCalibrated(positions[i].X, positions[i].Y, 150);
                    HowManyPotionsToBuy(HpBuyTarget - GetHpPotionCount());
                }
                // Mana Potions (Index 0)
                else if (i == 0 && GetManaPotionCount() < ManaBuyTarget)
                {
                    ClickCalibrated(positions[i].X, positions[i].Y, 150);
                    HowManyPotionsToBuy(ManaBuyTarget - GetManaPotionCount());
                }
                // Red Potions (Index 1)
                else if (i == 1 && GetRedPotionCount() < RedBuyTarget)
                {
                    ClickCalibrated(positions[i].X, positions[i].Y, 150);
                    HowManyPotionsToBuy(RedBuyTarget - GetRedPotionCount());
                }
                // White Potions (Index 2)
                else if (i == 2 && GetWhitePotionCount() < WhiteBuyTarget)
                {
                    ClickCalibrated(positions[i].X, positions[i].Y, 150);
                    HowManyPotionsToBuy(WhiteBuyTarget - GetWhitePotionCount());
                }
                // SOR — Scroll of Return (Index 4, the 5th shop slot right after HP)
                // The SOR limit is ALWAYS hardcoded (BotConstants.Repot.SorBuyTarget =
                // 10) — no profile override. The current SOR count is read from the 5th
                // inventory potion slot (playerBase + 0xFA0), so only the difference is
                // bought. Only fires when the city's position array actually has a 5th
                // entry (Etana has it; Kharon/Hershal do not yet).
                else if (i == 4 && GetSorPotionCount() < BotConstants.Repot.SorBuyTarget)
                {
                    ClickCalibrated(positions[i].X, positions[i].Y, 150);
                    HowManyPotionsToBuy(BotConstants.Repot.SorBuyTarget - GetSorPotionCount());
                }
            }
        }

        /// <summary>
        /// Types the given amount of potions into the shop quantity field and confirms.
        /// The amount is the difference between the profile target and the current
        /// potion count, so the profile values are honored exactly (no more hardcoded
        /// quantities or MAX buys).
        /// </summary>
        private void HowManyPotionsToBuy(int amountToBuy)
        {
            if (amountToBuy <= 0) return;

            _log($"Buying {amountToBuy} potions (profile target minus current count).");

            // Move to the quantity input field (calibrated absolute 1295,530 at
            // reference window 445,105), wait for the dialog, then click once.
            MoveCalibrated(1295, 530);
            Thread.Sleep(1000);
            MouseOperations.LeftClick(); // Down/Up with delay
            Thread.Sleep(500);

            // Type the amount as digits (max 3 digits, same as the old bot).
            int digits = Math.Min(amountToBuy, 999);
            string amountStr = digits.ToString();
            foreach (char c in amountStr)
            {
                PressKey((byte)(0x30 + (c - '0'))); // VK_0..VK_9
            }
            Thread.Sleep(500);
            ClickOkWhenBuying();
        }

        private void ClickOkWhenBuying()
        {
            Thread.Sleep(300);
            MoveCalibrated(560, 570);
            MouseOperations.LeftClick();
            Thread.Sleep(500);
        }

        /// <summary>
        /// Reference window origin at which the shop coordinates were calibrated.
        /// All calibrated shop positions are ABSOLUTE screen coords measured with the
        /// game window at (445,105) — same convention as Test Sell All.
        /// </summary>
        private const int RefWindowX = 445;
        private const int RefWindowY = 105;

        /// <summary>
        /// Converts a calibrated absolute screen position (measured at reference window
        /// 445,105) to the current game-window position and moves the cursor there
        /// WITHOUT clicking (no window offset applied — same as the working Test Sell All flow).
        /// </summary>
        private void MoveCalibrated(int calibratedX, int calibratedY)
        {
            var (screenX, screenY) = CalibratedToScreen(calibratedX, calibratedY);
            MouseOperations.SetCursorPositionAbsolute(screenX, screenY);
        }

        /// <summary>
        /// Converts a calibrated absolute screen position (measured at reference window
        /// 445,105) to the current game-window position and left-clicks it absolutely
        /// (no window offset applied — same as the working Test Sell All flow).
        /// </summary>
        private void ClickCalibrated(int calibratedX, int calibratedY, int delay = 200)
        {
            var (screenX, screenY) = CalibratedToScreen(calibratedX, calibratedY);
            MouseOperations.MoveAndLeftClickAbsolute(screenX, screenY, delay);
        }

        /// <summary>
        /// Converts a calibrated absolute screen position (measured at reference window
        /// 445,105) into current screen coordinates using the live game-window rect.
        /// Falls back to the calibrated position when the window cannot be found.
        /// </summary>
        private (int X, int Y) CalibratedToScreen(int calibratedX, int calibratedY)
        {
            nint hwnd = FindWindowByProcess();
            if (hwnd != nint.Zero && GetWindowRect(hwnd, out RECT rect))
            {
                int screenX = rect.Left + (calibratedX - RefWindowX);
                int screenY = rect.Top + (calibratedY - RefWindowY);
                _log($"[ShopPos] Calibrated ({calibratedX},{calibratedY}) → screen ({screenX},{screenY}) [window ({rect.Left},{rect.Top})]");
                return (screenX, screenY);
            }

            _log("[ShopPos] Could not get window rect — using calibrated position as-is.");
            return (calibratedX, calibratedY);
        }

        /// <summary>
        /// New SellItems implementation that delegates to the ported ItemSellerService.
        /// This uses the full logic from the old bot: high-value detection, tab switching,
        /// storage management, and anti-bug handling.
        /// </summary>
        public void SellItems()
        {
            _log("Selling Items using ItemSellerService (ported from old bot)...");

            if (!_itemSeller.IsCloseToShop())
            {
                _log("Not close to shop. Sell skipped.");
                return;
            }

            // Open shop first if not already open
            if (!IsShopOpen())
            {
                OpenShop();
            }

            if (IsShopOpen())
            {
                _itemSeller.SellItemsByMouseMove();
            }
            else
            {
                _log("Cannot sell — shop window is not open.");
            }
        }

        /// <summary>
        /// Calculates the shop NPC click position relative to the actual game window.
        /// Uses window-relative offsets (ShopRelX, ShopRelY) added to the window origin.
        /// </summary>
        private (int X, int Y) GetShopClickPosition()
        {
            nint hwnd = FindWindowByProcess();
            if (hwnd != nint.Zero && GetWindowRect(hwnd, out RECT rect))
            {
                int clickX = ShopRelX + rect.Left;
                int clickY = ShopRelY + rect.Top;
                _log($"[ShopPos] Window=({rect.Left},{rect.Top}), click=({clickX},{clickY}) [relative ({ShopRelX},{ShopRelY})]");
                return (clickX, clickY);
            }

            _log("[ShopPos] Could not get window rect, using relative position as fallback");
            return (ShopRelX, ShopRelY);
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern nint FindWindow(string lpClassName, string lpWindowName);

        private static nint FindWindowByProcess()
        {
            nint hwnd = FindWindow(null, "Legend of Ares");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Nostalgia");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Epic Of Ares Client");
            return hwnd;
        }

        public void Repot()
        {
            _log("Starting Repot Sequence...");

            // Give the character a moment to settle at the repot waypoint before
            // any mouse input (2s per user requirement).
            Thread.Sleep(BotConstants.Delays.RepotArrivalWaitMs);

            // 1. Open Shop: zoom the camera to the known sell view, then scan the
            //    view until the mouse points at the seller NPC (S_IsSellerPointed),
            //    right-click it and click the Shop option.
            OpenShop();

            if (!IsShopOpen())
            {
                // Fail loudly instead of silently continuing to the next path —
                // a failed repot must not look like a successful one.
                throw new InvalidOperationException(
                    "Shop window did not open at the repot point (seller NPC not found or click missed).");
            }

            // 2. Sell Items (using new ItemSellerService)
            SellItems();

            // 3. Buy Potions
            BuyPotions();

            // Close Shop (Escape key)
            GameInput.PressKey(GameInput.VK_ESCAPE, GameInput.SCAN_ESCAPE);
        }

        #endregion

        #region Keyboard Helpers

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        private const int KEYEVENTF_KEYUP = 0x0002;

        private void PressKey(byte vk)
        {
            keybd_event(vk, 0, 0, 0);
            Thread.Sleep(50);
            keybd_event(vk, 0, (uint)KEYEVENTF_KEYUP, 0);
            Thread.Sleep(200);
        }

        #endregion
    }
}
