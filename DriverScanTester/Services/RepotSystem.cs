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
        /// <summary>Target count for HP potions (added to ItemCount1 base). Default = BotConstants.Repot.HpBuyTarget.</summary>
        public int HpBuyTarget { get; set; } = BotConstants.Repot.HpBuyTarget;
        /// <summary>Target count for Mana potions (added to ItemCount1 base). Default = BotConstants.Repot.ManaBuyTarget.</summary>
        public int ManaBuyTarget { get; set; } = BotConstants.Repot.ManaBuyTarget;
        /// <summary>Target count for Red potions (added to ItemCount1 base). Default = BotConstants.Repot.RedBuyTarget.</summary>
        public int RedBuyTarget { get; set; } = BotConstants.Repot.RedBuyTarget;
        /// <summary>Target count for White potions (added to ItemCount1 base). Default = BotConstants.Repot.WhiteBuyTarget.</summary>
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

            // Calculate shop click position using actual window position (absolute screen coords)
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
            // Logic from BuyPotionsFromShopNormalEXP/BuyPotionsFromShopSell
            // Assuming we use Hershal positions as default or pass them
            var positions = RepotMousePositions.mousePositionsForHershalBuying; // Defaulting to Hershal

            for (int i = 0; i < positions.Length; i++)
            {
                Thread.Sleep(1000);

                // Mana Potions (Index 0)
                if (i == 0 && GetManaPotionCount() < BotConstants.GameMagicValues.ItemCount1 + ManaBuyTarget)
                {
                    MouseOperations.MoveAndLeftClick(positions[i].X, positions[i].Y, 150);
                    HowManyPotionsToBuy(i);
                }
                // Red Potions (Index 1)
                else if (i == 1 && GetRedPotionCount() < BotConstants.GameMagicValues.ItemCount1 + RedBuyTarget)
                {
                    MouseOperations.MoveAndLeftClick(positions[i].X, positions[i].Y, 150);
                    HowManyPotionsToBuy(i);
                }
                // White Potions (Index 2)
                else if (i == 2 && GetWhitePotionCount() < BotConstants.GameMagicValues.ItemCount1 + WhiteBuyTarget)
                {
                    MouseOperations.MoveAndLeftClick(positions[i].X, positions[i].Y, 150);
                    HowManyPotionsToBuy(i);
                }
                // HP Potions (Index 3)
                else if (i == 3 && GetHpPotionCount() < BotConstants.GameMagicValues.ItemCount1 + HpBuyTarget)
                {
                    MouseOperations.MoveAndLeftClick(positions[i].X, positions[i].Y, 150);
                    HowManyPotionsToBuy(i);
                }
            }
        }

        private void HowManyPotionsToBuy(int potionIndex)
        {
            // Logic from HowManyPotionsToBuyExp
            MouseOperations.SetCursorPosition(1295, 530);  // Position for second slot item
            Thread.Sleep(1000);
            MouseOperations.LeftClick(); // Down/Up with delay
            Thread.Sleep(500);

            if (potionIndex == 0) // Mana
            {
                PressKey(0x31); // 1
                PressKey(0x35); // 5
                PressKey(0x35); // 5
                Thread.Sleep(500);
                ClickOkWhenBuying();
            }
            else if (potionIndex == 1) // Red
            {
                PressKey(0x35); // 5
                Thread.Sleep(500);
                ClickOkWhenBuying();
            }
            else if (potionIndex == 2) // White
            {
                PressKey(0x35); // 5
                Thread.Sleep(500);
                ClickOkWhenBuying();
            }
            else if (potionIndex == 3) // HP
            {
                BuyingHpPotionsMax();
            }
        }

        private void BuyingHpPotionsMax()
        {
            MouseOperations.SetCursorPosition(1300, 550);
            MouseOperations.LeftClick();
            Thread.Sleep(500);
            MouseOperations.SetCursorPosition(560, 520);
            Thread.Sleep(500);
            MouseOperations.LeftClick();
            Thread.Sleep(500);
            ClickOkWhenBuying();
        }

        private void ClickOkWhenBuying()
        {
            Thread.Sleep(300);
            MouseOperations.SetCursorPosition(560, 570);
            MouseOperations.LeftClick();
            Thread.Sleep(500);
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

            // 1. Move to shop (Optional / Placeholder)
            // MoveToRepot(RepotMousePositions.HershalRepotMovePositions); // If needed

            // 2. Open Shop
            OpenShop();

            if (IsShopOpen())
            {
                // 3. Sell Items (using new ItemSellerService)
                SellItems();

                // 4. Buy Potions
                BuyPotions();

                // Close Shop (Escape key)
                GameInput.PressKey(GameInput.VK_ESCAPE, GameInput.SCAN_ESCAPE);
            }
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
