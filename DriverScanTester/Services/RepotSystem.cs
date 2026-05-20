using System;
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

        public RepotSystem(GameMemoryService memoryService, Action<string> log)
        {
            _memoryService = memoryService;
            _log = log;
        }

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

        public bool IsSellWindowOpen()
        {
            return _memoryService.IsSellWindowOpen();
        }

        #endregion

        #region Shop Operations

        public void OpenShop()
        {
            _log("Opening Shop Window...");
            // Logic from MouseClickOpenShop in OLD_ExpBotClass.cs
            Thread.Sleep(1000);
            MouseOperations.MoveAndLeftClick(580, 565, 200);

            // Wait for shop to open?
            int retries = 0;
            while (!IsShopOpen() && retries < 10)
            {
                Thread.Sleep(500);
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
                if (i == 0 && GetManaPotionCount() < GameMemoryService.ItemCount1 + 99)
                {
                    MouseOperations.MoveAndLeftClick(positions[i].X, positions[i].Y, 150);
                    HowManyPotionsToBuy(i);
                }
                // Red Potions (Index 1)
                else if (i == 1 && GetRedPotionCount() < GameMemoryService.ItemCount1 + 3)
                {
                    MouseOperations.MoveAndLeftClick(positions[i].X, positions[i].Y, 150);
                    HowManyPotionsToBuy(i);
                }
                // White Potions (Index 2)
                else if (i == 2 && GetWhitePotionCount() < GameMemoryService.ItemCount1 + 3)
                {
                    MouseOperations.MoveAndLeftClick(positions[i].X, positions[i].Y, 150);
                    HowManyPotionsToBuy(i);
                }
                // HP Potions (Index 3)
                else if (i == 3 && GetHpPotionCount() < GameMemoryService.ItemCount1 + 120)
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

        public void SellItems()
        {
            _log("Selling Items...");

            var sellPositions = RepotMousePositions.itemSellPositions;

            // Loop through inventory slots
            for (int i = 0; i < sellPositions.Length; i++)
            {
                if (!IsShopOpen()) break;

                int itemType = GetInventoryItemType(i);

                // Check if item should be sold
                if (itemType == 0) continue; // Empty slot

                bool isSafe = false;
                foreach (var safeItem in GameMemoryService.ItemsNotForSaleValues)
                {
                    if (itemType == safeItem)
                    {
                        isSafe = true;
                        break;
                    }
                }

                if (isSafe) continue;

                // Sell the item
                _log($"Selling item in slot {i} (Type: {itemType})");

                // Click on item
                // Note: OLD_ExpBotClass used MoveAndLeftClick, but ItemSeller.cs used MoveAndRightClickOperation
                // I will use RightClick as per ItemSeller.cs which seems more specific
                MouseOperations.SetCursorPosition(sellPositions[i].X, sellPositions[i].Y);
                Thread.Sleep(100);
                MouseOperations.RightClick();
                Thread.Sleep(200);

                // Confirm Sell
                MouseConfirmSelling();
            }
        }

        private void MouseConfirmSelling()
        {
            // Logic from ItemSeller.MouseConfirmSelling
            // Check if sell window is open
            if (IsSellWindowOpen())
            {
                LeftClickOnConfirmSell();
            }

            // Double check (High value items prompt?)
            Thread.Sleep(100);
            if (IsSellWindowOpen())
            {
                 LeftClickOnConfirmSell();
            }
        }

        private void LeftClickOnConfirmSell()
        {
            MouseOperations.SetCursorPosition(560, 570);
            MouseOperations.LeftClick(); // Down/Up
            Thread.Sleep(50);
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
                // 3. Sell Items
                SellItems();

                // 4. Buy Potions
                BuyPotions();

                // Close Shop (Escape key)
                PressKey(0x1B); // VK_ESCAPE
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
