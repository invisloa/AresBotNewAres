using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using DriverScanTester.Models;
using DriverScanTester.Utils;

// NOTE: This service uses memory offsets from the old bot (AresTrainerV3_oldBot).
// These offsets (0xC5A for inventory slots, 0x116 for storage, etc.) have NOT been
// verified with the current game version. If item reads return 0/garbage, the sell
// logic will simply skip those slots. Run "Test Sell" button to verify.

namespace DriverScanTester.Services
{
    /// <summary>
    /// Ported from old bot (AresTrainerV3_oldBot) ItemSeller.cs + ItemsToOperateListGenerator.cs + StorageMover.cs.
    /// Handles selling items from inventory and storage during repot.
    /// </summary>
    public class ItemSellerService
    {
        private readonly GameMemoryService _memory;
        private readonly Action<string> _log;
        private int _howManyTries;

        // ── Known city map IDs (ported from TeleportValues.cs) ──
        public const int MapHershal = 17;
        public const int MapKharon = 44;

        // ── Shop proximity boundaries (ported from old ItemSeller.IsCloseToShop) ──
        // These are raw coordinate values from the game (int-based coordinate system)
        private static readonly (int MinX, int MaxX, int MinY, int MaxY) HershalShopBounds =
            (1141175465, 1141336640, 1141133820, 1141308147);

        private static readonly (int MinX, int MaxX, int MinY, int MaxY) KharonShopBounds =
            (1125115858, 1125782038, 1125170820, 1125701048);

        // ════════════════════════════════════════════════════════════════
        //  INVENTORY MAPPING
        // ════════════════════════════════════════════════════════════════
        //
        // WAŻNE:
        // - pamięć sprzedaży używa compact sell slots 0..59,
        // - tab 1 ma pierwszy wiersz pomijany,
        // - dlatego klikamy clickIndex = slot + 6,
        // - NIE robimy żadnego Y -35 w normalnej sprzedaży.
        //
        // slot 0..29  -> tab 1, clickIndex 6..35
        // slot 30..59 -> tab 2, clickIndex 36..65
        //
        // To jest stare działające mapowanie. Poprawka "Y -35" była dobra wyłącznie
        // dla trybu testowego, który klikał visual indexy 0..71 bez compact offsetu.
        private const int InventoryColumns = 6;
        private const int VisualSlotsPerInventoryTab = 36;
        private const int FirstSellableClickIndexTab1 = 6;
        private const int SellableSlotsTab1 = 30;
        private const int TotalSellableInventorySlots = 60;
        private const int FirstTab2ClickIndex = 36;

        public ItemSellerService(GameMemoryService memory, Action<string> log)
        {
            _memory = memory;
            _log = log;
        }

        // ════════════════════════════════════════════════════════════════
        //  PUBLIC ENTRY POINT
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Main sell routine: opens shop, sells filtered items from inventory,
        /// then moves items from storage and sells those too.
        /// </summary>
        public void SellItemsByMouseMove()
        {
            AssignWeight();

            if (!IsCloseToShop())
            {
                _log("ItemSeller: Not close to shop. Skipping sell.");
                return;
            }

            OpenShopWindow();
            Thread.Sleep(700);

            if (!_memory.IsShopOpen())
            {
                _log("ItemSeller: Shop is not open after OpenShopWindow(). Skipping sell.");
                return;
            }

            int noProgressPasses = 0;

            while (_memory.IsShopOpen())
            {
                List<int> itemsToOperate = ItemsForSaleListGenerate();

                if (itemsToOperate.Count == 0)
                {
                    _log("ItemSeller: No inventory items qualified for sale.");
                    break;
                }

                // Sprzedajemy od największego compact slotu do najmniejszego.
                // To odpowiada starej logice sprzedaży od końca listy slotów.
                itemsToOperate.Sort((a, b) => b.CompareTo(a));

                int countBefore = itemsToOperate.Count;

                LogAllItemsForSale(itemsToOperate, "INVENTORY");

                int currentOpenedTab = -1;

                foreach (int item in itemsToOperate)
                {
                    if (!_memory.IsShopOpen())
                        break;

                    var target = GetInventoryClickTarget(item);

                    if (target.Tab != currentOpenedTab)
                    {
                        if (target.Tab == 1)
                            OpenInventoryTab1();
                        else
                            OpenInventoryTab2();

                        currentOpenedTab = target.Tab;
                        Thread.Sleep(180);
                    }

                    SellInventorySlotByMouse(item, target);
                    Thread.Sleep(80);
                }

                Thread.Sleep(150);

                int countAfter = ItemsForSaleListGenerate().Count;

                if (countAfter == 0)
                {
                    _log("ItemSeller: All inventory sale items sold.");
                    break;
                }

                if (countAfter < countBefore)
                {
                    _log($"ItemSeller: Sell pass made progress. Before={countBefore}, After={countAfter}. Starting next pass.");
                    noProgressPasses = 0;
                    continue;
                }

                noProgressPasses++;
                _log($"ItemSeller: No sell progress in full pass. Before={countBefore}, After={countAfter}, PassTries={noProgressPasses}/5");

                if (noProgressPasses >= 5)
                {
                    _log("ItemSeller: Too many full sell passes with no progress. Throwing.");
                    throw new InvalidOperationException("Sell routine stuck — no items sold after 5 full passes.");
                }
            }

            _howManyTries = 0;
            Thread.Sleep(50);

            if (ItemsFromStorageListGenerate().Count != 0
                && _memory.IsShopOpen()
                && _memory.GetCurrentWeight() < MaxCollectWeightNormalValue)
            {
                GameInput.PressKey(GameInput.VK_ESCAPE, GameInput.SCAN_ESCAPE);
                MoveItemsFromStorage();
                GameInput.PressKey(GameInput.VK_ESCAPE, GameInput.SCAN_ESCAPE);
                GameInput.PressKey(GameInput.VK_ESCAPE, GameInput.SCAN_ESCAPE);
                Thread.Sleep(500);
                SellItemsByMouseMove();
            }
        }

        /// <summary>
        /// Converts compact memory/sellable inventory slot to visual mouse click target.
        ///
        /// Correct mapping:
        /// - memory slots 0..29  -> tab 1, visual click indexes 6..35
        /// - memory slots 30..59 -> tab 2, visual click indexes 36..65
        ///
        /// No Y correction here.
        /// clickIndex = slotIndex + 6 is already the tab 1 row-skip.
        /// </summary>
        private (int Tab, int ClickIndex, int LocalIndex, int Row, int Column) GetInventoryClickTarget(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= TotalSellableInventorySlots)
            {
                throw new ArgumentOutOfRangeException(nameof(slotIndex), slotIndex,
                    $"Inventory slot must be in sellable compact range 0..{TotalSellableInventorySlots - 1}.");
            }

            int clickIndex = slotIndex + FirstSellableClickIndexTab1;
            int tab = slotIndex < SellableSlotsTab1 ? 1 : 2;

            int localIndex = tab == 1
                ? clickIndex
                : clickIndex - FirstTab2ClickIndex;

            int row = localIndex / InventoryColumns;
            int column = localIndex % InventoryColumns;

            return (tab, clickIndex, localIndex, row, column);
        }

        private void SellInventorySlotByMouse(int slotIndex, (int Tab, int ClickIndex, int LocalIndex, int Row, int Column) target)
        {
            if (!_memory.IsShopOpen())
                return;

            _log($"ItemSeller: Selling item compact slot {slotIndex} (tab {target.Tab}, click index {target.ClickIndex}, row {target.Row}, col {target.Column})");

            var pos = RepotMousePositions.itemSellPositions[target.ClickIndex];
            var (winX, winY) = GetWindowOrigin();

            int screenX = pos.X + winX;
            int screenY = pos.Y + winY;

            _log($"[ItemSeller] Sell slot {slotIndex} -> tab {target.Tab} -> clickIndex {target.ClickIndex} -> screen ({screenX},{screenY}) [relative ({pos.X},{pos.Y}) + window ({winX},{winY})]");

            MouseOperations.MoveAndRightClickAbsolute(screenX, screenY);
            MouseConfirmSelling();
        }

        // ════════════════════════════════════════════════════════════════
        //  SHOP PROXIMITY
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Checks if the player is within the shop zone for the current city.
        /// Uses the working GetPlayerPosition() with offsets 0x144/0xEE8.
        /// NOTE: Shop bounds need calibration for these short coordinates.
        /// Currently logs position for easy calibration and returns true
        /// to allow sell to proceed.
        /// </summary>
        public bool IsCloseToShop()
        {
            var (x, y, success) = _memory.GetPlayerPosition();
            if (!success)
            {
                _log("IsCloseToShop: Cannot read player position, assuming at shop.");
                return true;
            }

            int currentMap = _memory.GetCurrentMap();
            _log($"IsCloseToShop: Map={currentMap}, Pos=({x:F1}, {y:F1})");

            if (currentMap == MapHershal || currentMap == MapKharon)
            {
                return true;
            }

            return true;
        }

        // ════════════════════════════════════════════════════════════════
        //  ITEM EVALUATION
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Generates a list of inventory compact slot indexes that should be sold.
        /// </summary>
        public List<int> ItemsForSaleListGenerate()
        {
            return ItemsOperationsListGenerate(slotIndex => IsItemForSaleCheck(slotIndex, InventoryType.Inventory), InventoryType.Inventory);
        }

        /// <summary>
        /// Generates a list of storage slot indexes that should be sold.
        /// </summary>
        public List<int> ItemsFromStorageListGenerate()
        {
            return ItemsOperationsListGenerate(slotIndex => IsItemForSaleCheck(slotIndex, InventoryType.Storage), InventoryType.Storage);
        }

        /// <summary>
        /// Generates a list of inventory slot indexes that should be moved to storage.
        /// </summary>
        public List<int> ItemsToStorageMoveListGenerate()
        {
            return ItemsOperationsListGenerate(IsItemForToStorageMoveCheck, InventoryType.Inventory);
        }

        private List<int> ItemsOperationsListGenerate(Func<int, bool> delegateIsItemFitToAdd, InventoryType invType)
        {
            List<int> itemsToOperate = new List<int>();

            int inventoryCount = (invType == InventoryType.Inventory)
                ? TotalSellableInventorySlots
                : BotConstants.GameMagicValues.TotalStorageSlots;

            for (int i = 0; i < inventoryCount; i++)
            {
                if (delegateIsItemFitToAdd(i))
                {
                    itemsToOperate.Add(i);
                }
            }

            return itemsToOperate;
        }

        private bool IsItemForSaleCheck(int slotIndex, InventoryType invType)
        {
            int count = invType == InventoryType.Inventory
                ? _memory.TryGetSellSlotItemCount(slotIndex)
                : _memory.TryGetStorageItemCount(slotIndex);

            if (count <= 0)
                return false;

            return !IsItemHighValue(slotIndex, invType)
                && IsItemSaleType(slotIndex, invType);
        }

        private bool IsItemSaleType(int slotIndex, InventoryType invType)
        {
            int itemTypeValue;

            if (invType == InventoryType.Inventory)
            {
                itemTypeValue = _memory.TryGetSellSlotItemType(slotIndex);
            }
            else
            {
                itemTypeValue = _memory.TryGetStorageItemType(slotIndex);
            }

            if (IsItemNotForSale(itemTypeValue) || IsEventSnowmanItem(itemTypeValue))
            {
                return false;
            }

            return true;
        }

        private bool IsItemNotForSale(int itemType)
        {
            foreach (var safeItem in BotConstants.GameMagicValues.ItemsNotForSale)
            {
                if (itemType == safeItem)
                    return true;
            }

            return false;
        }

        private bool IsEventSnowmanItem(int itemType)
        {
            foreach (var evtItem in BotConstants.GameMagicValues.ItemValuesEventSnowman)
            {
                if (itemType == evtItem)
                    return true;
            }

            return false;
        }

        private bool IsItemForToStorageMoveCheck(int slotIndex)
        {
            return _memory.TryGetSellSlotItemCount(slotIndex) != 0;
        }

        /// <summary>
        /// Determines if an item has "high value" and should be kept (not sold).
        /// Ported from old ItemsToOperateListGenerator.isItemHighValue().
        /// </summary>
        public bool IsItemHighValue(int slotIndex, InventoryType invType)
        {
            int stat1, stat2;

            if (invType == InventoryType.Inventory)
            {
                stat1 = _memory.TryGetSellSlotItemStat1(slotIndex);
                stat2 = _memory.TryGetSellSlotItemStat2(slotIndex);
            }
            else
            {
                stat1 = _memory.TryGetStorageItemStat1(slotIndex);
                stat2 = _memory.TryGetStorageItemStat2(slotIndex);
            }

            int Mp = 0, Agi = 0, Con = 0, Td = 0;
            int Fire = 0, Earth = 0, Air = 0, Water = 0;
            int Sihon = 0, Luck = 0;
            int StrikingPower = 0, Justus = 0;
            int MageEmpPower = 0, MageAlliPower = 0;
            int Gedel = 0, Mizaph = 0;

            const int hightValueMainStats = 14;
            const int SihonValue = 40;
            const int SihonWithStats = 35;
            const int magicAttackLimit = 85;
            const int magicJustusLimit = 40;
            const int magicJustus25Limit = 30;

            #region Mizaph
            if (stat1 == 120) Mizaph += 1;
            else if (stat1 == 121) Mizaph += 2;
            #endregion

            #region Agility (Bilhan + Idbash)
            if (stat1 == 17) Agi += 1;
            else if (stat1 == 18) Agi += 3;
            else if (stat1 == 19) Agi += 5;
            else if (stat1 == 20) Agi += 7;
            else if (stat1 == 21) Agi += 9;

            if (stat2 == 15) Agi += 2;
            else if (stat2 == 16) Agi += 4;
            else if (stat2 == 17) Agi += 6;
            else if (stat2 == 18) Agi += 8;
            else if (stat2 == 19) Agi += 10;
            #endregion

            #region Muscle Power (Baruch + Keluchi)
            if (stat1 == 1) Mp += 1;
            else if (stat1 == 2) Mp += 3;
            else if (stat1 == 3) Mp += 5;
            else if (stat1 == 4) Mp += 7;
            else if (stat1 == 5) Mp += 9;

            if (stat2 == 1) Mp += 2;
            else if (stat2 == 2) Mp += 4;
            else if (stat2 == 3) Mp += 6;
            else if (stat2 == 4) Mp += 8;
            else if (stat2 == 5) Mp += 10;
            #endregion

            #region Concentration (Lhasha + Dalphon)
            if (stat1 == 12) Con += 1;
            else if (stat1 == 13) Con += 3;
            else if (stat1 == 14) Con += 5;
            else if (stat1 == 15) Con += 7;
            else if (stat1 == 16) Con += 9;

            if (stat2 == 10) Con += 2;
            else if (stat2 == 11) Con += 4;
            else if (stat2 == 12) Con += 6;
            else if (stat2 == 13) Con += 8;
            else if (stat2 == 14) Con += 10;
            #endregion

            #region Total Damage (Amos + Magbisch)
            if (stat1 == 75) Td += 5;
            else if (stat1 == 76) Td += 10;
            else if (stat1 == 77) Td += 15;
            else if (stat1 == 78) Td += 20;

            if (stat2 == 64) Td += 5;
            else if (stat2 == 65) Td += 10;
            else if (stat2 == 66) Td += 15;
            else if (stat2 == 67) Td += 20;
            #endregion

            #region Sihon
            if (stat1 == 186) Sihon += 40;
            else if (stat1 == 187) Sihon += 45;
            else if (stat1 == 188) Sihon += 50;
            #endregion

            #region Striking Power (Kazen / Doriat)
            if (stat2 == 123 || stat2 == 118) StrikingPower += 60;
            else if (stat2 == 124 || stat2 == 119) StrikingPower += 80;
            else if (stat2 == 125 || stat2 == 120) StrikingPower += 100;
            else if (stat2 == 126 || stat2 == 121) StrikingPower += 120;
            #endregion

            #region Mage
            if (stat2 == 50) MageEmpPower += 5;
            else if (stat2 == 51) MageEmpPower += 15;
            else if (stat2 == 52) MageEmpPower += 25;
            else if (stat2 == 53) MageEmpPower += 35;
            else if (stat2 == 54) MageEmpPower += 45;
            else if (stat2 == 55) MageEmpPower += 55;

            if (stat2 == 44) MageAlliPower += 5;
            else if (stat2 == 45) MageAlliPower += 15;
            else if (stat2 == 46) MageAlliPower += 25;
            else if (stat2 == 47) MageAlliPower += 35;
            else if (stat2 == 48) MageAlliPower += 45;
            else if (stat2 == 49) MageAlliPower += 55;

            if (stat1 == 41 || stat1 == 35) MageEmpPower += 10;
            else if (stat1 == 41 || stat1 == 36) MageEmpPower += 20;
            else if (stat1 == 42 || stat1 == 37) MageEmpPower += 30;
            else if (stat1 == 43 || stat1 == 38) MageEmpPower += 40;
            else if (stat1 == 44 || stat1 == 39) MageEmpPower += 50;
            else if (stat1 == 45 || stat1 == 40) MageEmpPower += 60;

            if (stat1 == 35) MageAlliPower += 10;
            else if (stat1 == 36) MageAlliPower += 20;
            else if (stat1 == 37) MageAlliPower += 30;
            else if (stat1 == 38) MageAlliPower += 40;
            else if (stat1 == 39) MageAlliPower += 50;
            else if (stat1 == 40) MageAlliPower += 60;
            #endregion

            #region Justus
            if (stat1 == 189) Justus += 10;
            else if (stat1 == 190) Justus += 15;
            else if (stat1 == 191) Justus += 20;
            else if (stat1 == 192) Justus += 25;
            else if (stat1 == 193) Justus += 30;
            #endregion

            #region Gedel
            if (stat1 == 81) Gedel += 8;
            else if (stat1 == 82) Gedel += 10;
            else if (stat1 == 83) Gedel += 15;
            else if (stat1 == 84) Gedel += 20;
            #endregion

            #region Luck
            if (stat2 == 97) Luck += 10;
            else if (stat2 == 98) Luck += 20;
            else if (stat2 == 99) Luck += 30;

            if (stat1 == 126) Luck += 5;
            else if (stat1 == 127) Luck += 15;
            else if (stat1 == 128) Luck += 25;
            #endregion

            #region Fire (stat2)
            if (stat2 == 32) Water += 5;
            else if (stat2 == 33) Fire += 15;
            else if (stat2 == 34) Fire += 25;
            else if (stat2 == 35) Fire += 35;
            else if (stat2 == 36) Fire += 45;
            else if (stat2 == 37) Fire += 55;
            #endregion

            #region Water
            if (stat1 == 41) Water += 5;
            else if (stat1 == 42) Water += 10;
            else if (stat1 == 43) Water += 20;
            else if (stat1 == 44) Water += 30;
            else if (stat1 == 45) Water += 40;
            else if (stat1 == 46) Water += 50;
            else if (stat1 == 47) Water += 60;

            if (stat2 == 38) Water += 5;
            else if (stat2 == 39) Water += 15;
            else if (stat2 == 40) Water += 25;
            else if (stat2 == 41) Water += 35;
            else if (stat2 == 42) Water += 45;
            else if (stat2 == 43) Water += 55;
            #endregion

            #region Earth
            if (stat1 == 48) Earth += 5;
            else if (stat1 == 49) Earth += 10;
            else if (stat1 == 50) Earth += 20;
            else if (stat1 == 51) Earth += 30;
            else if (stat1 == 52) Earth += 40;
            else if (stat1 == 53) Earth += 50;
            else if (stat1 == 54) Earth += 60;

            if (stat2 == 44) Earth += 5;
            else if (stat2 == 45) Earth += 15;
            else if (stat2 == 46) Earth += 25;
            else if (stat2 == 47) Earth += 35;
            else if (stat2 == 48) Earth += 45;
            else if (stat2 == 49) Earth += 55;
            #endregion

            #region Air
            if (stat1 == 55) Air += 5;
            else if (stat1 == 56) Air += 10;
            else if (stat1 == 57) Air += 20;
            else if (stat1 == 58) Air += 30;
            else if (stat1 == 59) Air += 40;
            else if (stat1 == 60) Air += 50;
            else if (stat1 == 61) Air += 60;

            if (stat2 == 50) Air += 5;
            else if (stat2 == 51) Air += 15;
            else if (stat2 == 52) Air += 25;
            else if (stat2 == 53) Air += 35;
            else if (stat2 == 54) Air += 45;
            else if (stat2 == 55) Air += 55;
            #endregion

            if (Agi > hightValueMainStats) return true;
            if (Mp > hightValueMainStats) return true;
            if (Con > hightValueMainStats) return true;
            if (Gedel > 15 && (Agi > 7 || Mp > 7 || Con > 7)) return true;
            if (Td > 30) return true;
            if (Fire > magicAttackLimit || Earth > magicAttackLimit || Water > magicAttackLimit || Air > magicAttackLimit) return true;
            if ((Fire > magicJustusLimit || Water > magicJustusLimit || Air > magicJustusLimit) && Justus > 15) return true;
            if ((Fire > magicJustus25Limit || Earth > magicJustus25Limit || Water > magicJustus25Limit || Air > magicJustus25Limit) && Justus > 20) return true;
            if (Td > 15 && StrikingPower > 80) return true;
            if (Justus > 20 && StrikingPower > 100) return true;
            if (Justus > 20 && Td > 10) return true;
            if (Sihon > SihonValue) return true;
            if (Sihon > SihonWithStats && (Mp > 4 || Agi > 4 || Con > 4)) return true;
            if (Luck > 25) return true;
            if (Mizaph > 1 && (Mp > 6 || Con > 6 || Agi > 6)) return true;

            return false;
        }

        // ════════════════════════════════════════════════════════════════
        //  DETAILED SELL LIST LOGGING
        // ════════════════════════════════════════════════════════════════

        private void LogAllItemsForSale(List<int> itemsToSell, string source)
        {
            _log("");
            _log("╔══════════════════════════════════════════════════════════════════════════╗");
            _log($"║  SELL LIST [{source}] - {itemsToSell.Count} items qualified for sale");
            _log("╚══════════════════════════════════════════════════════════════════════════╝");

            if (itemsToSell.Count == 0)
            {
                _log("  (no items to sell)");
                _log("");
                return;
            }

            var (winX, winY) = GetWindowOrigin();

            foreach (var item in itemsToSell)
            {
                var target = GetInventoryClickTarget(item);

                var pos = RepotMousePositions.itemSellPositions[target.ClickIndex];
                int screenX = pos.X + winX;
                int screenY = pos.Y + winY;

                int itemType = _memory.TryGetSellSlotItemType(item);
                int stat1 = _memory.TryGetSellSlotItemStat1(item);
                int stat2 = _memory.TryGetSellSlotItemStat2(item);
                int count = _memory.TryGetSellSlotItemCount(item);

                _log($"  ▶ Slot={item,2} | ClickIdx={target.ClickIndex,2} | Tab={target.Tab} | Wiersz={target.Row} | Kolumna={target.Column} | " +
                     $"PozycjaMyszki=({pos.X,4},{pos.Y,4}) | Screen=({screenX,4},{screenY,4}) | " +
                     $"ItemType={itemType} | Stat1={stat1} | Stat2={stat2} | Count={count}");
            }

            _log("");
        }

        private void LogAllStorageItemsForSale(List<int> itemsToMove, string source)
        {
            _log("");
            _log("╔══════════════════════════════════════════════════════════════════════════╗");
            _log($"║  STORAGE MOVE LIST [{source}] - {itemsToMove.Count} items to move from storage");
            _log("╚══════════════════════════════════════════════════════════════════════════╝");

            if (itemsToMove.Count == 0)
            {
                _log("  (no storage items to move)");
                _log("");
                return;
            }

            var (winX, winY) = GetWindowOrigin();
            const int colsPerTab = 6;

            foreach (var item in itemsToMove)
            {
                int row = item / colsPerTab;
                int col = item % colsPerTab;

                var pos = RepotMousePositions.itemMoveFromStoragePositions[item];
                int screenX = pos.X + winX;
                int screenY = pos.Y + winY;

                int itemType = _memory.TryGetStorageItemType(item);
                int stat1 = _memory.TryGetStorageItemStat1(item);
                int stat2 = _memory.TryGetStorageItemStat2(item);
                int count = _memory.TryGetStorageItemCount(item);

                _log($"  ▶ StorageSlot={item,2} | Wiersz={row} | Kolumna={col} | " +
                     $"PozycjaMyszki=({pos.X,4},{pos.Y,4}) | Screen=({screenX,4},{screenY,4}) | " +
                     $"ItemType={itemType} | Stat1={stat1} | Stat2={stat2} | Count={count}");
            }

            _log("");
        }

        // ════════════════════════════════════════════════════════════════
        //  STORAGE OPERATIONS
        // ════════════════════════════════════════════════════════════════

        public void MoveItemsFromStorage()
        {
            GameInput.PressKey(GameInput.VK_ESCAPE, GameInput.SCAN_ESCAPE);
            Thread.Sleep(200);

            OpenStorageWindow();
            Thread.Sleep(700);

            OpenInventoryTab1();
            List<int> itemsToMove = ItemsFromStorageListGenerate();

            LogAllStorageItemsForSale(itemsToMove, "STORAGE");

            foreach (int item in itemsToMove)
            {
                if (_memory.IsInventoryOpen() && _memory.GetCurrentWeight() < MaxCollectWeightNormalValue)
                {
                    _log($"ItemSeller: Moving item from storage slot {item}");
                    Thread.Sleep(1);
                    var pos = RepotMousePositions.itemMoveFromStoragePositions[item];
                    MouseOperations.MoveAndRightClick(pos.X, pos.Y);
                    Thread.Sleep(1);
                }
            }
        }

        public bool IsStorageFull()
        {
            int invSlotValue = 0;
            for (int i = 95; i < 98; i++)
            {
                invSlotValue += _memory.TryGetStorageItemCount(i);
            }
            return invSlotValue > 2;
        }

        // ════════════════════════════════════════════════════════════════
        //  WEIGHT MANAGEMENT
        // ════════════════════════════════════════════════════════════════

        private int _maxCollectWeightNormalValue;

        public int MaxCollectWeightNormalValue => _maxCollectWeightNormalValue;

        private void AssignWeight()
        {
            int maxWeight = _memory.GetMaxWeight();
            _maxCollectWeightNormalValue = maxWeight - 150;
        }

        // ════════════════════════════════════════════════════════════════
        //  WINDOW POSITION
        // ════════════════════════════════════════════════════════════════

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern nint FindWindow(string lpClassName, string lpWindowName);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private (int X, int Y) GetWindowOrigin()
        {
            nint hwnd = FindWindow(null, "Legend of Ares");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Nostalgia");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Epic Of Ares Client");

            if (hwnd != nint.Zero && GetWindowRect(hwnd, out RECT rect))
                return (rect.Left, rect.Top);

            return (0, 0);
        }

        // ════════════════════════════════════════════════════════════════
        //  MOUSE OPERATIONS
        // ════════════════════════════════════════════════════════════════

        public void MouseConfirmSelling()
        {
            _log("ItemSeller: Checking if sell window is open.");
            Thread.Sleep(50);

            if (_memory.IsSellConfirmWindowOpen())
            {
                LeftClickOnConfirmSell("normal item sell");
            }

            _log("ItemSeller: Checking if high value confirmation.");
            Thread.Sleep(100);

            if (_memory.IsSellConfirmWindowOpen())
            {
                Thread.Sleep(10);
                LeftClickOnConfirmSell("high value item click once more");
                Thread.Sleep(50);
            }
        }

        private void LeftClickOnConfirmSell(string debugMessage)
        {
            var (winX, winY) = GetWindowOrigin();

            int screenX = 104 + winX;
            int screenY = 464 + winY;
            int sleepTime = 35;

            _log($"ItemSeller: {debugMessage} -> screen ({screenX},{screenY}) [relative (104,464) + window ({winX},{winY})]");
            MouseOperations.LeftClickAbsolute(screenX, screenY, sleepTime);
        }

        // ════════════════════════════════════════════════════════════════
        //  ANTI-BUG
        // ════════════════════════════════════════════════════════════════

        private void ShopTooFarAntiBug()
        {
            if (IsCloseToShop())
            {
                GameInput.PressKey(GameInput.VK_ESCAPE, GameInput.SCAN_ESCAPE);
                _log("ItemSeller: ShopTooFarAntiBug triggered.");
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  WINDOW-RELATIVE INVENTORY TAB CLICKS
        // ════════════════════════════════════════════════════════════════

        private void OpenInventoryTab1()
        {
            var (winX, winY) = GetWindowOrigin();

            int screenX = 789 + winX;
            int screenY = 459 + winY;

            _log($"[ItemSeller] OpenInventoryTab1 -> screen ({screenX},{screenY}) [relative (789,459) + window ({winX},{winY})]");
            MouseOperations.OpenInventoryTab1Absolute(screenX, screenY);
        }

        private void OpenInventoryTab2()
        {
            var (winX, winY) = GetWindowOrigin();

            int screenX = 795 + winX;
            int screenY = 560 + winY;

            _log($"[ItemSeller] OpenInventoryTab2 -> screen ({screenX},{screenY}) [relative (795,560) + window ({winX},{winY})]");
            MouseOperations.OpenInventoryTab2Absolute(screenX, screenY);
        }

        // ════════════════════════════════════════════════════════════════
        //  WINDOW OPERATIONS
        // ════════════════════════════════════════════════════════════════

        private void OpenShopWindow()
        {
            _log("ItemSeller: Opening shop window.");
            Thread.Sleep(1000);

            var (winX, winY) = GetWindowOrigin();

            int screenX = 145 + winX;
            int screenY = 460 + winY;

            _log($"[ItemSeller] OpenShopWindow -> screen ({screenX},{screenY}) [relative (145,460) + window ({winX},{winY})]");
            MouseOperations.MoveAndLeftClickAbsolute(screenX, screenY, 200);
        }

        private void OpenStorageWindow()
        {
            _log("ItemSeller: Opening storage window.");
            Thread.Sleep(200);

            var (winX, winY) = GetWindowOrigin();

            int screenX = 145 + winX;
            int screenY = 460 + winY;

            _log($"[ItemSeller] OpenStorageWindow -> screen ({screenX},{screenY}) [relative (145,460) + window ({winX},{winY})]");
            MouseOperations.MoveAndLeftClickAbsolute(screenX, screenY, 200);
        }

        // ════════════════════════════════════════════════════════════════
        //  ENUMS
        // ════════════════════════════════════════════════════════════════

        public enum InventoryType
        {
            Inventory,
            Storage
        }
    }
}