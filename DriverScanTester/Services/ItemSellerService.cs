using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
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
    /// Handles selling items from inventory and storage during repot.
    ///
    /// Main rule in this version:
    /// - every click is based on REAL visual inventory slot index,
    /// - realSlot is exactly the index in RepotMousePositions.itemSellPositions[],
    /// - memorySlot is derived only for memory reads: memorySlot = realSlot - 6.
    /// </summary>
    public class ItemSellerService
    {
        private readonly GameMemoryService _memory;
        private readonly Action<string> _log;
        private readonly WKeyCameraController? _wKeyCameraController;
        private int _howManyTries;
        private bool _sellerScanYawRestoreWarningLogged;

        /// <summary>
        /// Screen coordinates of the most recent NPC right-click performed by
        /// <see cref="ScanAndRightClickNpc"/>, or null when no NPC was clicked in the
        /// current scan. Used only for operation failure diagnostics.
        /// </summary>
        public (int X, int Y)? LastNpcRightClickScreenPosition { get; internal set; }

        public const int MapHershal = 17;
        public const int MapKharon = 44;
        public const int MapEtana = 35;

        private static readonly (int MinX, int MaxX, int MinY, int MaxY) HershalShopBounds =
            (1141175465, 1141336640, 1141133820, 1141308147);

        private static readonly (int MinX, int MaxX, int MinY, int MaxY) KharonShopBounds =
            (1125115858, 1125782038, 1125170820, 1125701048);

        // ════════════════════════════════════════════════════════════════
        //  REAL INVENTORY SLOT MAPPING
        // ════════════════════════════════════════════════════════════════
        //
        // realSlot = prawdziwy slot inventory / indeks itemSellPositions[]
        // memorySlot = indeks używany tylko do _memory.TryGetSellSlotItem*()
        //
        // Realne sloty inventory:
        //   tab 1: realSlot 0..35
        //   tab 2: realSlot 36..71
        //
        // Sprzedaż:
        //   tab 1: sprzedajemy realSlot 6..35, wiersz 0 (sloty 0..5) pomijamy
        //   tab 2: sprzedajemy realSlot 36..71
        //
        // Mapowanie:
        //   memorySlot = realSlot (bez offsetu, pointer wskazuje prosto na slot 0)
        //
        // Przykłady:
        //   realSlot 0  -> tab 1, mousePosition itemSellPositions[0],  memorySlot 0
        //   realSlot 10 -> tab 1, mousePosition itemSellPositions[10], memorySlot 10
        //   realSlot 35 -> tab 1, mousePosition itemSellPositions[35], memorySlot 35
        //   realSlot 40 -> tab 2, mousePosition itemSellPositions[40], memorySlot 40
        //
        private const int InventoryColumns = 6;
        private const int VisualSlotsPerInventoryTab = 36;
        private const int TotalVisualInventorySlots = VisualSlotsPerInventoryTab * 2; // 72

        private const int FirstSellableRealSlotTab1 = 6;
        private const int LastSellableRealSlotTab1 = 35;
        private const int FirstRealSlotTab2 = 36;
        private const int LastRealSlotTab2 = 71;

        private const int MemorySlotOffsetFromRealSlot = 0;
        private const int TotalSellableInventoryMemorySlots = 72; // real 0..71 -> memory 0..71

        public ItemSellerService(
            GameMemoryService memory,
            Action<string> log,
            WKeyCameraController? wKeyCameraController = null)
        {
            _memory = memory;
            _log = log;
            _wKeyCameraController = wKeyCameraController;
        }

        // ════════════════════════════════════════════════════════════════
        //  PUBLIC ENTRY POINT
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Opens the seller/shop dialog the same way the old bot did:
        /// 1. Zoom the camera to the known NPC scan view (distance 16720, top-down
        ///    vertical lock 16330) so the shop NPC is in its expected on-screen position.
        /// 2. Scan the game window from the center outward, moving the mouse until
        ///    S_IsSellerPointed equals the active NPC value (the mouse is over the NPC),
        ///    then right-click the NPC, click the "Shop" option and verify the shop
        ///    window actually opened.
        /// 3. When that fast scan fails, the slow discovery stage tries to find and
        ///    validate a NEW MouseOver NPC value automatically (right-click + dialogue).
        /// Returns true when the shop window is open.
        /// </summary>
        public bool OpenSellerDialog()
        {
            if (!TryAcquireSellerCamera(out object? cameraOwner))
                return false;

            try
            {
                if (!SetSellCameraView())
                    return false;

                if (_memory.IsShopOpen())
                {
                    _log("ItemSeller: Seller dialog already open.");
                    return true;
                }

                if (TryOpenSellerDialogByScanning())
                {
                    _log("ItemSeller: Seller dialog opened via mouseover scan.");
                    return true;
                }

                CaptureSellFailureScreenshot();
                _log($"ItemSeller: Seller NPC not found with the active mouseover value {MouseOverValueDiscovery.Describe(SellerPointedValue)} and automatic calibration did not confirm a new value. Seller dialog NOT opened.");
                return false;
            }
            finally
            {
                ReleaseSellerCamera(cameraOwner);
            }
        }

        /// <summary>
        /// Scans for the seller NPC (same spiral scan as the repot seller flow) and
        /// right-clicks it when found. Does NOT click any dialog option — the caller
        /// inspects the resulting NPC dialog itself. Returns true when the NPC was
        /// found and right-clicked (or the dialog is already open).
        /// </summary>
        /// <param name="maxFullScans">
        /// How many sweeps of the camera-safe client interior to attempt before giving up. Defaults to
        /// <see cref="SellerMaxFullScans"/>; pass 1 to make exactly one full scan
        /// (used by the COT entry operations — no NPC found after one sweep = fail).
        /// </param>
        /// <param name="allowCalibration">
        /// When true (default) a failed fast scan starts the slow discovery stage that can
        /// re-calibrate the MouseOver NPC value automatically (see
        /// <see cref="TryDiscoverNpcMouseOverValue"/>). Pass false for callers that must
        /// never change the active value.
        /// </param>
        public bool ScanAndRightClickNpc(
            int maxFullScans = SellerMaxFullScans,
            bool allowCalibration = true)
        {
            LastNpcRightClickScreenPosition = null;

            if (!TryAcquireSellerCamera(out object? cameraOwner))
                return false;

            try
            {
                if (!SetSellCameraView())
                    return false;

                if (_memory.IsShopOpen())
                {
                    _log("ItemSeller: NPC dialog already open — skipping scan.");
                    return true;
                }

                return TryOpenSellerDialogByScanning(
                    clickShopOption: false,
                    maxFullScans: maxFullScans,
                    allowCalibration: allowCalibration);
            }
            finally
            {
                ReleaseSellerCamera(cameraOwner);
            }
        }

        public void SellItemsByMouseMove()
        {
            if (!TryAcquireSellerCamera(out object? cameraOwner))
                return;

            try
            {
                SellItemsByMouseMoveCore();
            }
            finally
            {
                ReleaseSellerCamera(cameraOwner);
            }
        }

        private void SellItemsByMouseMoveCore()
        {
            AssignWeight();

            if (!IsCloseToShop())
            {
                _log("ItemSeller: Not close to shop. Skipping sell.");
                return;
            }

            // ── NEW PRE-STEP: ensure the seller dialog is open ──
            // 1. Force the known NPC scan camera (distance 16720, top-down vertical
            //    lock 16330) so the shop NPC is in its expected on-screen position.
            // 2. If the dialog is already open, skip scanning and continue.
            // 3. Otherwise, scan the game window from center outward looking for the
            //    active S_IsSellerPointed value and right-click when found.
            // 4. If the fast scan finds nothing, the slow discovery stage tries to
            //    re-calibrate the MouseOver NPC value (validated by a real dialogue).
            // 5. If that also fails, take a screenshot and stop gracefully — do not run
            //    the old sell logic.
            if (!SetSellCameraView())
            {
                _log("ItemSeller: Verified seller camera view could not be established. Skipping sell.");
                return;
            }

            if (!_memory.IsShopOpen())
            {
                if (!TryOpenSellerDialogByScanning())
                {
                    CaptureSellFailureScreenshot();
                    _log($"ItemSeller: Seller NPC not found with the active mouseover value {MouseOverValueDiscovery.Describe(SellerPointedValue)} after scanning + automatic calibration. Skipping sell.");
                    return;
                }
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

                // Sprzedajemy od największego REALNEGO slotu inventory do najmniejszego.
                itemsToOperate.Sort((a, b) => b.CompareTo(a));
                int countBefore = itemsToOperate.Count;

                LogAllItemsForSale(itemsToOperate, "INVENTORY");

                int currentOpenedTab = -1;

                foreach (int item in itemsToOperate)
                {
                    if (!_memory.IsShopOpen())
                        break;

                    var target = MapRealInventorySlotToTarget(item);

                    if (target.Tab != currentOpenedTab)
                    {
                        // GATE: the tab click is an absolute click too. With the shop
                        // window gone it would land in the game world (= walk), so verify
                        // the window is still there right before clicking.
                        EnsureShopOpenForClick($"inventory tab {target.Tab} click");

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

                Thread.Sleep(200);

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

            // ── POST-SELL: high-value items left in inventory ──
            // If more than 3 inventory slots still hold high-value items after
            // the sell pass, the bot should make a trip to storage to bank them
            // so they don't sit in the inventory at risk. The trip itself is
            // not implemented yet — GoToStorage() is a stub.
            int highValueSlotCount = CountHighValueItemsInInventory();
            if (highValueSlotCount > 3)
            {
                _log($"ItemSeller: {highValueSlotCount} high-value item slots remain in inventory (> 3). Calling GoToStorage(city={_memory.GetCurrentMap()}).");
                GoToStorage(_memory.GetCurrentMap());
            }

            // Note: this method intentionally does NOT touch storage. The bot is
            // configured to sell only inventory items; storage-to-inventory
            // transfer is handled elsewhere (or not at all in this version).
        }

        public void SellSpecificSlot(int realSlot)
        {
            if (!_memory.IsShopOpen())
            {
                _log("ItemSeller: Shop is not open. Skipping specific slot sell.");
                return;
            }

            try
            {
                var target = MapRealInventorySlotToTarget(realSlot);

                // Ensure correct tab is open
                if (target.Tab == 1)
                    OpenInventoryTab1();
                else
                    OpenInventoryTab2();

                Thread.Sleep(180);

                SellInventorySlotByMouse(realSlot, target);
                _log($"ItemSeller: Specific slot {realSlot} sell attempt finished.");
            }
            catch (Exception ex)
            {
                _log($"ItemSeller: Error selling specific slot {realSlot}: {ex.Message}");
            }
        }

        private struct InventorySlotTarget
        {
            public int RealSlot;
            public int MemorySlot;
            public int Tab;
            public int MousePositionIndex;
            public int LocalIndex;
            public int Row;
            public int Column;
        }

        /// <summary>
        /// Helper: rzutuje REALNY wizualny slot inventory na tab oraz mouse position.
        /// To jest jedyne miejsce w klasie, które decyduje gdzie kliknąć inventory.
        /// </summary>
        private InventorySlotTarget MapRealInventorySlotToTarget(int realSlot)
        {
            if (realSlot < 0 || realSlot >= TotalVisualInventorySlots)
            {
                throw new ArgumentOutOfRangeException(nameof(realSlot), realSlot,
                    $"Real inventory slot must be in range 0..{TotalVisualInventorySlots - 1}.");
            }

            int tab = realSlot < VisualSlotsPerInventoryTab ? 1 : 2;
            int localIndex = tab == 1 ? realSlot : realSlot - VisualSlotsPerInventoryTab;
            int memorySlot = realSlot - MemorySlotOffsetFromRealSlot;

            if (memorySlot < 0 || memorySlot >= TotalSellableInventoryMemorySlots)
            {
                throw new ArgumentOutOfRangeException(nameof(realSlot), realSlot,
                    $"Mapped memory slot must be in range 0..{TotalSellableInventoryMemorySlots - 1}.");
            }

            return new InventorySlotTarget
            {
                RealSlot = realSlot,
                MemorySlot = memorySlot,
                Tab = tab,
                MousePositionIndex = realSlot,
                LocalIndex = localIndex,
                Row = localIndex / InventoryColumns,
                Column = localIndex % InventoryColumns
            };
        }

        private int RealInventorySlotToMemorySlot(int realSlot)
        {
            return MapRealInventorySlotToTarget(realSlot).MemorySlot;
        }

        private void SellInventorySlotByMouse(int realSlot, InventorySlotTarget target)
        {
            // GATE: never click a predefined shop position on a closed shop — the click
            // would land in the game world and move the character instead of selling.
            EnsureShopOpenForClick($"sell of inventory slot {realSlot}");

            if (target.MousePositionIndex < 0 || target.MousePositionIndex >= RepotMousePositions.itemSellPositions.Length)
            {
                _log($"ItemSeller: Invalid mousePositionIndex {target.MousePositionIndex} for realSlot {realSlot}. Skipping.");
                return;
            }

            var pos = RepotMousePositions.itemSellPositions[target.MousePositionIndex];
            var (winX, winY) = GetWindowOrigin();

            int screenX = pos.X + winX;
            int screenY = pos.Y + winY;

            _log($"ItemSeller: Selling REAL slot {realSlot} -> memorySlot {target.MemorySlot}, Tab={target.Tab}, MouseIdx={target.MousePositionIndex}, Row={target.Row}, Col={target.Column}");
            _log($"[ItemSeller] Sell realSlot {realSlot} -> screen ({screenX},{screenY}) [relative ({pos.X},{pos.Y}) + window ({winX},{winY})]");

            // Re-check immediately before the click: the gate above already logged and
            // validated, this closes the remaining race window.
            EnsureShopOpenForClick($"sell click for inventory slot {realSlot}");

            MouseOperations.MoveAndRightClickAbsolute(screenX, screenY);
            MouseConfirmSelling();
        }

        /// <summary>
        /// GATE for every repot click: the shop window must still be open at the moment
        /// the click is sent. A missed click (e.g. on the inventory tab) closes the shop
        /// window; without this gate the following absolute clicks land in the game
        /// world, where a left-click means "walk there" — that is how the bot used to
        /// end up in strange, extreme places instead of buying/selling.
        ///
        /// Throws <see cref="ShopWindowClosedException"/> so the repot aborts BEFORE any
        /// further click is sent; the workflow then waits about a minute and restarts the
        /// whole repot from the top.
        /// </summary>
        private void EnsureShopOpenForClick(string operation)
        {
            if (_memory.IsShopOpen())
                return;

            _log($"ItemSeller: Shop window is CLOSED before '{operation}' — aborting the repot sequence without sending the click.");
            throw new ShopWindowClosedException(
                $"Shop window closed before '{operation}' (a missed click closed it).");
        }

        // ════════════════════════════════════════════════════════════════
        //  SHOP PROXIMITY
        // ════════════════════════════════════════════════════════════════

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
                return true;

            return true;
        }

        // ════════════════════════════════════════════════════════════════
        //  ITEM EVALUATION
        // ════════════════════════════════════════════════════════════════

        public List<int> ItemsForSaleListGenerate()
        {
            List<int> itemsToOperate = new List<int>();

            for (int realSlot = FirstSellableRealSlotTab1; realSlot <= LastSellableRealSlotTab1; realSlot++)
            {
                if (IsItemForSaleCheck(realSlot, InventoryType.Inventory))
                    itemsToOperate.Add(realSlot);
            }

            for (int realSlot = FirstRealSlotTab2; realSlot <= LastRealSlotTab2; realSlot++)
            {
                if (IsItemForSaleCheck(realSlot, InventoryType.Inventory))
                    itemsToOperate.Add(realSlot);
            }

            return itemsToOperate;
        }

        public List<int> ItemsFromStorageListGenerate()
        {
            List<int> itemsToOperate = new List<int>();

            for (int i = 0; i < BotConstants.GameMagicValues.TotalStorageSlots; i++)
            {
                if (IsItemForSaleCheck(i, InventoryType.Storage))
                    itemsToOperate.Add(i);
            }

            return itemsToOperate;
        }

        public List<int> ItemsToStorageMoveListGenerate()
        {
            List<int> itemsToOperate = new List<int>();

            for (int realSlot = FirstSellableRealSlotTab1; realSlot <= LastSellableRealSlotTab1; realSlot++)
            {
                if (IsItemForToStorageMoveCheck(realSlot))
                    itemsToOperate.Add(realSlot);
            }

            for (int realSlot = FirstRealSlotTab2; realSlot <= LastRealSlotTab2; realSlot++)
            {
                if (IsItemForToStorageMoveCheck(realSlot))
                    itemsToOperate.Add(realSlot);
            }

            return itemsToOperate;
        }

        private bool IsItemForSaleCheck(int slotIndex, InventoryType invType)
        {
            int count;

            if (invType == InventoryType.Inventory)
            {
                int memorySlot = RealInventorySlotToMemorySlot(slotIndex);
                count = _memory.TryGetSellSlotItemCount(memorySlot);
            }
            else
            {
                count = _memory.TryGetStorageItemCount(slotIndex);
            }

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
                int memorySlot = RealInventorySlotToMemorySlot(slotIndex);
                itemTypeValue = _memory.TryGetSellSlotItemType(memorySlot);
            }
            else
            {
                itemTypeValue = _memory.TryGetStorageItemType(slotIndex);
            }

            if (IsItemNotForSale(itemTypeValue) || IsEventSnowmanItem(itemTypeValue))
                return false;

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

        private bool IsItemForToStorageMoveCheck(int realSlot)
        {
            int memorySlot = RealInventorySlotToMemorySlot(realSlot);
            return _memory.TryGetSellSlotItemCount(memorySlot) != 0;
        }

        public bool IsItemHighValue(int slotIndex, InventoryType invType)
        {
            int stat1, stat2;

            if (invType == InventoryType.Inventory)
            {
                int memorySlot = RealInventorySlotToMemorySlot(slotIndex);
                stat1 = _memory.TryGetSellSlotItemStat1(memorySlot);
                stat2 = _memory.TryGetSellSlotItemStat2(memorySlot);
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
        //  DETAILED LOGGING
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
                var target = MapRealInventorySlotToTarget(item);

                var pos = RepotMousePositions.itemSellPositions[target.MousePositionIndex];
                int screenX = pos.X + winX;
                int screenY = pos.Y + winY;

                int itemType = _memory.TryGetSellSlotItemType(target.MemorySlot);
                int stat1 = _memory.TryGetSellSlotItemStat1(target.MemorySlot);
                int stat2 = _memory.TryGetSellSlotItemStat2(target.MemorySlot);
                int count = _memory.TryGetSellSlotItemCount(target.MemorySlot);

                _log($"  ▶ RealSlot={item,2} | MemorySlot={target.MemorySlot,2} | MouseIdx={target.MousePositionIndex,2} | Tab={target.Tab} | Wiersz={target.Row} | Kolumna={target.Column} | " +
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
        //  POST-SELL HIGH-VALUE CHECK + STORAGE TRIP
        //  CountHighValueItemsInInventory() — iterates both inventory tabs
        //  and counts slots that contain at least one item flagged as
        //  high-value by IsItemHighValue().
        //
        //  GoToStorage(int currentCityIntValue) — STUB. Called when more
        //  than 3 high-value slots remain in inventory after the sell pass.
        //  Intended to navigate the character to the city storage NPC and
        //  bank the items. NOT IMPLEMENTED YET.
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Threshold (exclusive) above which the post-sell logic decides to
        /// call <see cref="GoToStorage"/>: if the count is strictly greater
        /// than this, the storage trip is triggered.
        /// </summary>
        private const int HighValueSlotGoToStorageThreshold = 3;

        /// <summary>
        /// Counts how many inventory slots currently hold at least one item
        /// that <see cref="IsItemHighValue"/> flags as high-value. Walks both
        /// inventory tabs (real slots 6..35 and 36..71).
        /// </summary>
        private int CountHighValueItemsInInventory()
        {
            int highValueCount = 0;

            for (int realSlot = FirstSellableRealSlotTab1; realSlot <= LastSellableRealSlotTab1; realSlot++)
            {
                int memorySlot = RealInventorySlotToMemorySlot(realSlot);
                int count = _memory.TryGetSellSlotItemCount(memorySlot);
                if (count > 0 && IsItemHighValue(realSlot, InventoryType.Inventory))
                {
                    highValueCount++;
                }
            }

            for (int realSlot = FirstRealSlotTab2; realSlot <= LastRealSlotTab2; realSlot++)
            {
                int memorySlot = RealInventorySlotToMemorySlot(realSlot);
                int count = _memory.TryGetSellSlotItemCount(memorySlot);
                if (count > 0 && IsItemHighValue(realSlot, InventoryType.Inventory))
                {
                    highValueCount++;
                }
            }

            return highValueCount;
        }

        /// <summary>
        /// STUB. Intended to navigate the character to the city storage NPC
        /// and bank the high-value items left in the inventory. Called from
        /// <see cref="SellItemsByMouseMove"/> when more than
        /// <see cref="HighValueSlotGoToStorageThreshold"/> high-value slots
        /// remain after the sell pass. NOT IMPLEMENTED YET.
        /// </summary>
        /// <param name="currentCityIntValue">The current city / map id
        /// (<c>_memory.GetCurrentMap()</c>) — the storage trip is expected
        /// to use this to pick the right destination.</param>
        private void GoToStorage(int currentCityIntValue)
        {
            _log($"ItemSeller: GoToStorage(currentCityIntValue={currentCityIntValue}) — STUB, not implemented yet.");
            // TODO: implement the actual trip: path to storage NPC, open storage,
            // move the high-value inventory slots into storage, return to the
            // current city.
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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(nint hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint GetForegroundWindow();

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private (int X, int Y) GetWindowOrigin()
        {
            nint hwnd = FindWindow(null, "Legend of Ares");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Ares");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Nostalgia");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Epic Of Ares Client");

            if (hwnd != nint.Zero && GetWindowRect(hwnd, out RECT rect))
                return (rect.Left, rect.Top);

            return (0, 0);
        }

        /// <summary>
        /// Resolves the game window handle using the known window titles.
        /// Returns <see cref="nint.Zero"/> when the game window cannot be found.
        /// </summary>
        private nint FindGameWindowHandle()
        {
            nint hwnd = FindWindow(null, "Legend of Ares");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Ares");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Nostalgia");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Epic Of Ares Client");
            return hwnd;
        }

        /// <summary>
        /// True when the game window is currently the foreground (selected) window.
        /// False when the user switched to another window (e.g. the bot log) or the
        /// game window cannot be found.
        /// </summary>
        private bool IsGameWindowForeground()
        {
            nint hwnd = FindGameWindowHandle();
            return hwnd != nint.Zero && GetForegroundWindow() == hwnd;
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
            // GATE: the sell-confirm click is an absolute click as well. The caller only
            // reaches it while the confirm window is open, so that window (part of the
            // shop UI) is the authority; if neither it nor the shop is open the click
            // would hit the game world and move the character — abort instead.
            if (!_memory.IsSellConfirmWindowOpen() && !_memory.IsShopOpen())
            {
                _log($"ItemSeller: Sell-confirm window and shop window are both CLOSED before '{debugMessage}' — aborting without sending the click.");
                throw new ShopWindowClosedException(
                    $"Shop window closed before the sell-confirm click ('{debugMessage}').");
            }

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
        //  SELLER DIALOG OPEN (NEW PRE-STEP)
        //  - SetSellCameraView()
        //  - ReadIsSellerPointed()
        //  - TryOpenSellerDialogByScanning()
        //  - CaptureSellFailureScreenshot()
        // These helpers gate SellItemsByMouseMove on the seller dialog
        // actually being open before the existing sell logic runs.
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// ACTIVE MouseOver NPC value compared against S_IsSellerPointed
        /// (<c>[Ares.exe + 0x4883E8] + 0xC</c>) while the cursor sweeps the game window.
        ///
        /// The ADDRESS CHAIN is stable, but the VALUE stored at that address is the hovered
        /// object's heap address/ID (see MouseOverValues_Log.txt), so it changes between game
        /// launches. That is why a previously configured value can stop matching. It can be
        /// set in two ways:
        ///   1. manually — hover the NPC and press 'Mouseover NPC' in the Bot window
        ///      (MainViewModel.CaptureNpcMouseOver; persisted to MouseCalibration.json), or
        ///   2. automatically — the slow discovery stage validates a candidate with a real
        ///      right-click + NPC-dialogue check and writes the confirmed value here
        ///      (see <see cref="TryDiscoverNpcMouseOverValue"/>).
        /// Both paths persist the confirmed value to MouseCalibration.json, so the next bot
        /// run / app start reuses it. A value loaded from an older game launch is simply
        /// replaced by the automatic calibration when it no longer matches.
        /// </summary>
        public static int SellerPointedValue = 138599240;

        /// <summary>
        /// Step in pixels between scan points when sweeping the game window for the seller NPC.
        ///
        /// 54 px = the former 17 px grid × √10. The number of scan points grows with
        /// 1 / step², so this grid visits about 1/10 of the former points per sweep
        /// (~2451 → ~247 on a 1024x768 client) and both scan stages finish roughly 10x faster.
        ///
        /// TRADE-OFF (requested): a 54 px grid can step OVER a small NPC hover zone. The
        /// previous 17 px grid was chosen so the hover zone could not slip between points;
        /// if the seller/NPC stops being detected, restore a smaller step here (17 px was
        /// the dense value). This single constant drives the fast scan AND the slow
        /// discovery scan, so both stages always use the same grid.
        /// </summary>
        internal const int SellerScanStepPx = 54;

        /// <summary>Number of full game-window scans attempted before giving up.</summary>
        private const int SellerMaxFullScans = 5;

        /// <summary>Per-point wait after moving the mouse (ms). 4 ms per point — a
        /// slightly longer tick for the game to update the mouseover read while the
        /// scan stays fast. If the NPC is ever missed, raise this value.</summary>
        private const int SellerScanPointDelayMs = 4;

        /// <summary>
        /// Keep scan points away from the game-client boundary: Ares can rotate the
        /// camera when the cursor is parked against an edge. This small exclusion band
        /// prevents the scan itself from changing the view while still covering nearly
        /// the entire client.
        /// </summary>
        private const int SellerScanCameraEdgeGuardPx = 24;

        private const int SellerCameraSetupAttempts = 3;
        private const int SellerCameraSetupRetryDelayMs = 50;

        /// <summary>
        /// Render settle time (ms) after the top-down scan camera values were written and
        /// verified. The lock readback is immediate, but the game animates the visible
        /// pitch — the sweep must not start before the view has actually reached top-down,
        /// otherwise the first part of the NPC scan would still run in the tilted
        /// movement view.
        /// </summary>
        private const int SellerCameraSettleDelayMs = 500;
        private const int SellerScanYawCheckIntervalPoints = 32;

        // ── STAGE 2: SLOW NPC DISCOVERY (automatic MouseOver NPC calibration) ──
        // Runs only after the fast scan above failed to match the active value. It sweeps
        // the SAME center-outward points (EnumerateSellerScanPoints) but samples slowly
        // enough for the game to publish the hovered object's value, collects candidates and
        // validates each one with a real right-click + NPC-dialogue check.

        /// <summary>Settle time after the cursor arrives at a slow-scan point (ms). At least one game frame.</summary>
        private const int NpcDiscoveryPointSettleMs = 16;

        /// <summary>Gap between the two reads that must agree before a sample counts (ms).</summary>
        private const int NpcDiscoveryConfirmSampleMs = 8;

        /// <summary>
        /// Hard upper bound for one slow discovery run (ms). The center-outward spiral reaches
        /// the NPC's expected on-screen position (around the player, i.e. the client center)
        /// long before this bound; the budget only limits how far into the outer corners the
        /// slow scan may go when nothing can be confirmed.
        /// </summary>
        private const int NpcDiscoveryTimeoutMs = 90_000;

        /// <summary>Maximum number of candidates validated with a right-click per run.</summary>
        private const int NpcDiscoveryMaxCandidateTests = MouseOverValueDiscovery.DefaultMaxCandidateTests;

        /// <summary>How many rejected values the run remembers.</summary>
        private const int NpcDiscoveryMaxRejectedValues = MouseOverValueDiscovery.DefaultMaxRejectedValues;

        /// <summary>How long to wait for the NPC dialogue to appear after the right-click (ms).</summary>
        private const int NpcDiscoveryDialogOpenTimeoutMs = 700;

        /// <summary>Polling interval while waiting for a dialogue/window state change (ms).</summary>
        private const int NpcDiscoveryStatePollMs = 25;

        /// <summary>
        /// How long to wait for the "Shop" option to open the shop window after a candidate
        /// dialogue opened. Uses the existing repot shop-open budget so the seller workflow
        /// behaves exactly like the fast path.
        /// </summary>
        private const int NpcDiscoveryShopOpenTimeoutMs =
            BotConstants.Repot.OpenShopRetries * BotConstants.Delays.OpenShopRetryMs;

        /// <summary>Populate delay after a shop opened through a calibrated value (same as the fast path).</summary>
        private const int NpcDiscoveryShopPopulateDelayMs = 500;

        /// <summary>How often the foreground/cancel condition is re-checked during slow discovery (points).</summary>
        private const int NpcDiscoveryForegroundCheckIntervalPoints = 32;

        /// <summary>Progress line interval during slow discovery (points).</summary>
        private const int NpcDiscoveryProgressLogIntervalPoints = 500;

        private bool TryAcquireSellerCamera(out object? owner)
        {
            owner = null;
            if (_wKeyCameraController == null)
                return true;

            object candidate = new();
            if (!_wKeyCameraController.TryAcquireExternalCameraOwner(candidate))
            {
                _log("ItemSeller: Camera scan blocked while another bot input owns the camera.");
                return false;
            }

            owner = candidate;
            return true;
        }

        private void ReleaseSellerCamera(object? owner)
        {
            if (owner != null)
                _wKeyCameraController?.ReleaseExternalCameraOwner(owner, restoreStationary: true);
        }

        /// <summary>
        /// Applies the NPC scan view (distance 16720, top-down vertical lock 16330),
        /// verifies both values after the game has had time to settle, and fails closed
        /// instead of starting a scan with an unknown camera. The vertical lock is the
        /// same top-down baseline the rest of the bot uses, so the view does not change
        /// when the NPC is clicked and the camera lease is released. The caller holds the
        /// shared external camera lease so bot movement cannot overwrite this view.
        /// </summary>
        private bool SetSellCameraView()
        {
            short sellCameraDistance = BotConstants.Camera.SellerDistanceLock;
            short sellVerticalLock = BotConstants.Camera.NpcScanVerticalLock;
            _log($"ItemSeller: Setting NPC scan top-down view (distance={sellCameraDistance}, vertical lock={sellVerticalLock}).");

            for (int attempt = 1; attempt <= SellerCameraSetupAttempts; attempt++)
            {
                if (!_memory.TryGetCameraDistance(out short currentDistance) || currentDistance != sellCameraDistance)
                    _memory.TrySetCameraDistance(sellCameraDistance);

                if (!_memory.TryGetCameraVerticalLock(out short currentVerticalLock) || currentVerticalLock != sellVerticalLock)
                    _memory.TrySetCameraVerticalLock(sellVerticalLock);

                if (IsSellCameraViewVerified(sellCameraDistance, sellVerticalLock))
                {
                    Thread.Sleep(SellerCameraSettleDelayMs);
                    if (IsSellCameraViewVerified(sellCameraDistance, sellVerticalLock))
                    {
                        _log($"ItemSeller: Seller camera verified and settled (attempt {attempt}/{SellerCameraSetupAttempts}).");
                        return true;
                    }
                }

                _log($"ItemSeller: Seller camera verification failed (attempt {attempt}/{SellerCameraSetupAttempts}); retrying.");
                if (attempt < SellerCameraSetupAttempts)
                    Thread.Sleep(SellerCameraSetupRetryDelayMs);
            }

            _log($"ItemSeller: ERROR — seller camera could not be verified at distance={sellCameraDistance}, vertical lock={sellVerticalLock}; scan aborted.");
            return false;
        }

        private bool IsSellCameraViewVerified(short expectedDistance, short expectedVerticalLock)
            => _memory.TryGetCameraDistance(out short actualDistance) && actualDistance == expectedDistance &&
               _memory.TryGetCameraVerticalLock(out short actualVerticalLock) && actualVerticalLock == expectedVerticalLock;

        /// <summary>
        /// Reads the seller-mouseover int at <c>[Ares.exe + 0x4883E8] + 0xC</c>.
        /// 0 is treated as "not pointed" (also returned if the pointer chain fails).
        /// </summary>
        private int ReadIsSellerPointed()
        {
            return _memory.ReadIsSellerPointed();
        }

        /// <summary>
        /// Takes one STABLE mouseover sample: two reads of S_IsSellerPointed separated by
        /// <see cref="NpcDiscoveryConfirmSampleMs"/> must both succeed and agree. A failed read
        /// is never treated as "nothing under the cursor" (0), and a value that is still
        /// settling after the cursor move is never treated as a candidate.
        /// The NPC-highlight flag is captured for diagnostics only.
        /// </summary>
        private MouseOverValueSample ReadStableMouseOverSample()
        {
            bool firstReadSucceeded = _memory.TryReadIsSellerPointed(out int firstValue);
            Thread.Sleep(NpcDiscoveryConfirmSampleMs);
            bool confirmReadSucceeded = _memory.TryReadIsSellerPointed(out int confirmValue);
            bool npcHighlighted = _memory.TryReadIsNpcMousePointed(out bool highlighted) && highlighted;

            return MouseOverValueSample.FromReads(
                firstReadSucceeded, firstValue,
                confirmReadSucceeded, confirmValue,
                npcHighlighted);
        }

        /// <summary>
        /// STAGE 1 (fast) + optional STAGE 2 (slow discovery / automatic calibration).
        ///
        /// Stage 1 sweeps the mouse from the center of the game window outward in a square
        /// spiral looking for exactly <see cref="SellerPointedValue"/>, right-clicks the match
        /// and verifies the shop window. When it fails and calibration is allowed,
        /// <see cref="TryDiscoverNpcMouseOverValue"/> sweeps the SAME points slowly, looking
        /// for a stable NEW value that follows the cursor, and validates it with a real
        /// right-click + NPC-dialogue check before saving it.
        ///
        /// Before each full fast scan starts, the foreground window is checked: when the game
        /// window is no longer the selected window (the user switched away, e.g. to read the
        /// log), a <see cref="BotStopRequestedException"/> is thrown so the workflow stops the
        /// bot instead of continuing the sweep.
        ///
        /// Scan points use the actual client rectangle and stay outside the edge band that can
        /// rotate the camera, regardless of window position or resolution.
        /// </summary>
        private bool TryOpenSellerDialogByScanning(
            bool clickShopOption = true,
            int maxFullScans = SellerMaxFullScans,
            bool allowCalibration = true)
        {
            int? stableCameraAngleBits = null;
            if (_memory.TryGetCameraAngleBits(out int cameraAngleBits) &&
                float.IsFinite(BitConverter.Int32BitsToSingle(cameraAngleBits)))
            {
                stableCameraAngleBits = cameraAngleBits;
                _log($"ItemSeller: Preserving scan camera yaw (raw=0x{cameraAngleBits:X8}).");
            }
            else
            {
                _log("ItemSeller: WARNING — camera yaw could not be read; scan will continue without yaw-lock verification.");
            }

            _sellerScanYawRestoreWarningLogged = false;
            try
            {
                if (TryOpenSellerDialogByScanningCore(clickShopOption, maxFullScans, stableCameraAngleBits))
                    return true;

                if (!allowCalibration)
                    return false;

                return TryRunNpcCalibration(clickShopOption, stableCameraAngleBits);
            }
            finally
            {
                if (stableCameraAngleBits.HasValue)
                    RestoreSellerScanYaw(stableCameraAngleBits.Value);
            }
        }

        /// <summary>
        /// Stage 2 entry point: logs why the slow scan starts, runs it and reports the result.
        /// A confirmed calibration returns true so the caller continues the original NPC
        /// workflow — the validating right-click already opened the dialog/shop it needs, so
        /// the NPC is never clicked a second time.
        /// </summary>
        private bool TryRunNpcCalibration(bool clickShopOption, int? stableCameraAngleBits)
        {
            _log($"[NPC Scan] Normal scan finished without a match — the active MouseOver NPC value {MouseOverValueDiscovery.Describe(SellerPointedValue)} may be stale for this game session.");
            _log("[NPC Calibration] Starting slow discovery.");

            if (TryDiscoverNpcMouseOverValue(clickShopOption, stableCameraAngleBits, out string failureReason))
            {
                _log("[NPC Scan] Continuing original NPC workflow.");
                return true;
            }

            // Same final gate the fast scan uses: if the shop window did open (e.g. late),
            // the workflow is satisfied even though discovery itself did not confirm a value.
            if (clickShopOption && _memory.IsShopOpen())
            {
                _log("[NPC Calibration] Shop window is open — continuing the original NPC workflow without changing the active value.");
                return true;
            }

            _log($"[NPC Calibration] Slow discovery did not confirm a new value: {failureReason}");
            return false;
        }

        private bool TryOpenSellerDialogByScanningCore(
            bool clickShopOption,
            int maxFullScans,
            int? stableCameraAngleBits)
        {
            if (_memory.IsShopOpen())
            {
                _log("ItemSeller: Seller dialog already open before scan — skipping scan.");
                return true;
            }

            if (!TryGetGameClientArea(out int clientOriginX, out int clientOriginY, out int clientWidth, out int clientHeight))
            {
                _log("ItemSeller: Could not resolve game client area for seller scan. Aborting scan.");
                return false;
            }

            // 0 means "not calibrated". Comparing against it would match the first hovered
            // object and right-click it, so the fast scan is skipped and stage 2 discovers
            // (and validates) the value instead.
            if (SellerPointedValue == 0)
            {
                _log("ItemSeller: No MouseOver NPC value configured (0) — skipping the fast scan.");
                return false;
            }

            _log($"ItemSeller: Starting seller mouseover scan. client=({clientOriginX},{clientOriginY}) size=({clientWidth}x{clientHeight}), step={SellerScanStepPx}px, edgeGuard={SellerScanCameraEdgeGuardPx}px, maxFullScans={maxFullScans}, activeValue={MouseOverValueDiscovery.Describe(SellerPointedValue)}.");

            NpcScanSweepState state = SweepNpcScanPoints(
                clientOriginX, clientOriginY, clientWidth, clientHeight,
                maxFullScans,
                "Seller",
                (clientX, clientY, scanIndex, pointIndex) =>
                    ScanSellerAtClientPoint(clientX, clientY,
                                            clientOriginX, clientOriginY,
                                            clientWidth, clientHeight,
                                            scanIndex, pointIndex,
                                            stableCameraAngleBits, clickShopOption)
                        ? NpcScanSweepState.Found
                        : NpcScanSweepState.Continue);

            if (state == NpcScanSweepState.Found)
                return true;

            return clickShopOption && _memory.IsShopOpen();
        }

        /// <summary>State a single scanned point reports back to the sweep loop.</summary>
        private enum NpcScanSweepState
        {
            /// <summary>Nothing found at this point; keep scanning.</summary>
            Continue,

            /// <summary>The requested NPC/dialog/shop is open — stop the sweep successfully.</summary>
            Found,

            /// <summary>The sweep must stop without success (timeout, exhausted budget, unusable UI).</summary>
            Aborted
        }

        /// <summary>Handles one scan point. Returns how the sweep has to continue.</summary>
        private delegate NpcScanSweepState NpcScanPointHandler(
            int clientX, int clientY, int scanIndex, int pointIndex);

        /// <summary>
        /// Runs the shared center-outward NPC scan pattern and delegates every point to
        /// <paramref name="handlePoint"/>. The foreground check and the "dialog already open"
        /// short-circuit are applied before every full sweep, exactly as the fast scan always
        /// did, so both stages share one scanning architecture.
        /// </summary>
        private NpcScanSweepState SweepNpcScanPoints(
            int clientOriginX, int clientOriginY, int clientWidth, int clientHeight,
            int maxFullScans, string scanLabel, NpcScanPointHandler handlePoint)
        {
            for (int scanIndex = 0; scanIndex < maxFullScans; scanIndex++)
            {
                // Before each full scan, verify the game window is still the selected
                // (foreground) window. If the user switched away — e.g. to read the
                // bot log — stop the bot immediately instead of sweeping the window
                // for many more seconds/minutes.
                if (!IsGameWindowForeground())
                {
                    throw new BotStopRequestedException(
                        "Game window is not the selected window — stopping the bot (NPC scan aborted).");
                }

                if (_memory.IsShopOpen())
                {
                    _log($"ItemSeller: Seller dialog opened before scan #{scanIndex + 1} started.");
                    return NpcScanSweepState.Found;
                }

                _log($"ItemSeller: {scanLabel} scan #{scanIndex + 1}/{maxFullScans} — center-outward spiral.");

                int pointsThisScan = 0;
                foreach ((int clientX, int clientY) in EnumerateSellerScanPoints(clientWidth, clientHeight))
                {
                    NpcScanSweepState pointState = handlePoint(clientX, clientY, scanIndex, pointsThisScan);
                    pointsThisScan++;

                    if (pointState != NpcScanSweepState.Continue)
                        return pointState;
                }

                _log($"ItemSeller: {scanLabel} scan #{scanIndex + 1}/{maxFullScans} finished — no match ({pointsThisScan} points checked).");
            }

            return NpcScanSweepState.Continue;
        }

        /// <summary>
        /// Enumerates the NPC scan points in the exact center-outward square-spiral order used
        /// by BOTH the fast scan and the slow discovery scan: center first, then concentric
        /// squares of radius step, step*2, ... clamped to the camera-safe client interior.
        /// </summary>
        /// <param name="stepPx">
        /// Grid spacing. Defaults to the step both production stages use
        /// (<see cref="SellerScanStepPx"/>); an explicit value is only useful for callers
        /// that need to compare/inspect another grid (e.g. tests).
        /// </param>
        internal static IEnumerable<(int X, int Y)> EnumerateSellerScanPoints(
            int clientWidth, int clientHeight, int stepPx = SellerScanStepPx)
        {
            if (clientWidth <= 0 || clientHeight <= 0)
                yield break;

            int centerClientX = clientWidth / 2;
            int centerClientY = clientHeight / 2;
            int step = Math.Max(1, stepPx);
            int maxRadius = Math.Max(clientWidth, clientHeight); // spiral upper bound

            // Center first.
            if (IsSellerScanPointWithinSafeArea(centerClientX, centerClientY, clientWidth, clientHeight))
                yield return (centerClientX, centerClientY);

            for (int radius = step; radius <= maxRadius; radius += step)
            {
                // Top edge: from -radius to +radius
                for (int dx = -radius; dx <= radius; dx += step)
                {
                    int x = centerClientX + dx;
                    int y = centerClientY - radius;
                    if (IsSellerScanPointWithinSafeArea(x, y, clientWidth, clientHeight))
                        yield return (x, y);
                }
                // Right edge: from -radius+step to +radius-step
                for (int dy = -radius + step; dy <= radius; dy += step)
                {
                    int x = centerClientX + radius;
                    int y = centerClientY + dy;
                    if (IsSellerScanPointWithinSafeArea(x, y, clientWidth, clientHeight))
                        yield return (x, y);
                }
                // Bottom edge: from +radius-step down to -radius
                for (int dx = radius - step; dx >= -radius; dx -= step)
                {
                    int x = centerClientX + dx;
                    int y = centerClientY + radius;
                    if (IsSellerScanPointWithinSafeArea(x, y, clientWidth, clientHeight))
                        yield return (x, y);
                }
                // Left edge: from +radius-step up to -radius+step
                for (int dy = radius - step; dy >= -radius + step; dy -= step)
                {
                    int x = centerClientX - radius;
                    int y = centerClientY + dy;
                    if (IsSellerScanPointWithinSafeArea(x, y, clientWidth, clientHeight))
                        yield return (x, y);
                }
            }
        }

        /// <summary>
        /// Returns whether a client point stays inside the scan area, away from the edges
        /// that can rotate the game camera.
        /// </summary>
        internal static bool IsSellerScanPointWithinSafeArea(
            int clientX, int clientY, int clientWidth, int clientHeight)
        {
            if (clientWidth <= 0 || clientHeight <= 0)
                return false;

            int edgeGuard = Math.Min(
                SellerScanCameraEdgeGuardPx,
                Math.Max(0, Math.Min(clientWidth, clientHeight) / 2 - 1));
            return clientX >= edgeGuard && clientY >= edgeGuard &&
                   clientX < clientWidth - edgeGuard &&
                   clientY < clientHeight - edgeGuard;
        }

        /// <summary>
        /// Moves the mouse to one safe client-coordinate point, waits for the mouseover
        /// value, then right-clicks a detected NPC and optionally opens its Shop option.
        /// Returns true when the requested dialog/shop has opened and the scan should stop.
        /// </summary>
        private bool ScanSellerAtClientPoint(int clientX, int clientY,
                                             int clientOriginX, int clientOriginY,
                                             int clientWidth, int clientHeight,
                                             int scanIndex, int pointIndex,
                                             int? stableCameraAngleBits,
                                             bool clickShopOption)
        {
            // Skip points outside the client and the edge band where Ares can rotate
            // the view in response to the cursor. This avoids camera movement caused by
            // the scan itself while still sampling almost the whole game area.
            if (!IsSellerScanPointWithinSafeArea(clientX, clientY, clientWidth, clientHeight))
                return false;

            // Movement steering is also gated by the shared camera lease. This periodic
            // readback catches any camera yaw changed by the game itself while the scan
            // moves the cursor; restore the exact pre-scan angle before testing hover.
            RestoreScanYawIfDrifted(pointIndex, stableCameraAngleBits);

            int screenX = clientOriginX + clientX;
            int screenY = clientOriginY + clientY;

            // Move and wait for the mouseover to update.
            MouseOperations.SetCursorPositionAbsolute(screenX, screenY);
            Thread.Sleep(SellerScanPointDelayMs);

            int pointed = ReadIsSellerPointed();
            if (pointed != SellerPointedValue)
                return false;

            _log($"ItemSeller: S_IsSellerPointed=={SellerPointedValue} (0x{(uint)SellerPointedValue:X8}) detected at client ({clientX},{clientY}) screen ({screenX},{screenY}) [scan {scanIndex + 1}, point {pointIndex}]. Right-clicking to open the NPC dialog.");
            LastNpcRightClickScreenPosition = (screenX, screenY);

            // Right-click the seller NPC to open its context menu (Shop / Storage / etc.).
            MouseOperations.MouseEvent(MouseOperations.MouseEventFlags.RightDown);
            Thread.Sleep(50);
            MouseOperations.MouseEvent(MouseOperations.MouseEventFlags.RightUp);
            // Wait for the NPC dialog (S_IsDialogOpen1 == 1) to actually render
            // before clicking anything — clicking while dialog == 0 hits empty
            // ground (left-click = walk) and sends the player off in a random
            // direction. Poll instead of a fixed sleep so slow renders are handled.
            const int dialogTimeoutMs = 200;
            const int dialogPollMs = 20;
            int dialogWaitedMs = 0;
            while (!_memory.IsDialogOpen() && dialogWaitedMs < dialogTimeoutMs)
            {
                Thread.Sleep(dialogPollMs);
                dialogWaitedMs += dialogPollMs;
            }

            if (!clickShopOption)
            {
                // Caller only wanted the NPC found and right-clicked (e.g. a custom
                // operation that will handle the dialog itself).
                _log($"ItemSeller: NPC found and right-clicked (no dialog option clicked). IsDialogOpen={_memory.IsDialogOpen()} after {dialogWaitedMs}ms.");
                return true;
            }

            // GATE: only click the "Shop" option when the dialog is confirmed open.
            // Without this, a missed/slow dialog means ClickShopOptionInDialog()
            // clicks air at (145,460) and the player walks away.
            if (!_memory.IsDialogOpen())
            {
                _log($"ItemSeller: NPC dialog did NOT open (S_IsDialogOpen1 != 1 after {dialogTimeoutMs}ms) — skipping Shop-option click, continuing scan.");
                return false;
            }

            // The right-click only opens the context menu — we still need to click the
            // "Shop" option in that menu to actually open the shop window. This is the
            // same click the pre-refactor OpenShopWindow() performed.
            ClickShopOptionInDialog();

            // Wait for the shop window to actually open.
            int retry = 0;
            while (!_memory.IsShopOpen() && retry < BotConstants.Repot.OpenShopRetries)
            {
                Thread.Sleep(BotConstants.Delays.OpenShopRetryMs);
                retry++;
            }

            if (_memory.IsShopOpen())
            {
                // The shop window reports open before its item list has finished
                // loading. Give the inventory an extra 500ms to populate so the
                // existing sell logic doesn't click into an empty/half-loaded grid.
                Thread.Sleep(500);
                _log($"ItemSeller: Shop window opened via mouseover scan + dialog click (scan {scanIndex + 1}, point {pointIndex}).");
                return true;
            }

            _log("ItemSeller: Right-click + Shop-option click sent but shop window did not open — continuing scan.");
            return false;
        }

        // ════════════════════════════════════════════════════════════════
        //  STAGE 2 — SLOW NPC DISCOVERY + AUTOMATIC CALIBRATION
        //
        //  The fast scan above can only match the configured value. After a game launch
        //  the hovered NPC's value (the object address/ID stored at a stable address) may
        //  differ, so the fast scan finds nothing even though the NPC is on screen.
        //
        //  This stage sweeps the SAME points slowly, reads the mouseover value at every
        //  point, keeps stable NON-ZERO values that follow the cursor as CANDIDATES, and
        //  accepts one only after a real right-click made an actual NPC dialogue open
        //  (plus, for the seller workflow, the Shop option actually opened the shop).
        //  Memory evidence alone never counts as calibration.
        // ════════════════════════════════════════════════════════════════

        /// <summary>Mutable state of one slow discovery run.</summary>
        private sealed class NpcDiscoveryContext
        {
            public NpcDiscoveryContext(
                MouseOverValueDiscovery discovery,
                Stopwatch stopwatch,
                bool clickShopOption,
                int? stableCameraAngleBits)
            {
                Discovery = discovery;
                Stopwatch = stopwatch;
                ClickShopOption = clickShopOption;
                StableCameraAngleBits = stableCameraAngleBits;
            }

            public MouseOverValueDiscovery Discovery { get; }
            public Stopwatch Stopwatch { get; }
            public bool ClickShopOption { get; }
            public int? StableCameraAngleBits { get; }

            /// <summary>Stable sample read at the previous scan point (hover-transition baseline).</summary>
            public MouseOverValueSample? PreviousSample { get; set; }

            /// <summary>Most recent scan point that published value 0 (cursor is over nothing).</summary>
            public (int ScreenX, int ScreenY)? LastEmptyScreenPoint { get; set; }

            public int PointsScanned { get; set; }
            public int StableObjectSamples { get; set; }
            public string AbortReason { get; set; } = string.Empty;
        }

        /// <summary>Result of validating one candidate with a right-click.</summary>
        private enum NpcCandidateTestOutcome
        {
            /// <summary>An NPC dialogue (and, when required, the shop) opened — value saved.</summary>
            Confirmed,

            /// <summary>The candidate was rejected (or became unreadable) — keep scanning.</summary>
            NotConfirmed,

            /// <summary>Discovery cannot continue safely (the game UI is in an unusable state).</summary>
            AbortDiscovery
        }

        /// <summary>
        /// Runs the slow discovery sweep and, on success, updates
        /// <see cref="SellerPointedValue"/> — the single active NPC detection value.
        /// Returns false with a human-readable <paramref name="failureReason"/> when no
        /// candidate could be confirmed; the active value is never overwritten then.
        /// </summary>
        private bool TryDiscoverNpcMouseOverValue(
            bool clickShopOption,
            int? stableCameraAngleBits,
            out string failureReason)
        {
            failureReason = string.Empty;

            if (_memory.IsShopOpen())
            {
                _log("[NPC Calibration] Shop window is already open — no calibration needed.");
                return true;
            }

            // If the mouseover buffer itself cannot be read, no scan can discover anything:
            // fail fast instead of sweeping the whole time budget with an unusable chain.
            if (!_memory.TryReadIsSellerPointed(out _))
            {
                failureReason = "the MouseOver buffer ([Ares.exe + 0x4883E8] + 0xC) could not be read";
                _log($"[NPC Calibration] {failureReason} — slow discovery aborted.");
                return false;
            }

            if (!TryGetGameClientArea(out int clientOriginX, out int clientOriginY, out int clientWidth, out int clientHeight))
            {
                failureReason = "game client area could not be resolved";
                return false;
            }

            // A dialogue that was already open would make "did MY click open it?"
            // undecidable, and it blocks world hover reads. Close it first.
            if (!TryCloseOpenNpcDialog("before slow NPC discovery", out string closeFailure))
            {
                failureReason = closeFailure;
                return false;
            }

            var discovery = new MouseOverValueDiscovery(
                () => SellerPointedValue,
                value => SellerPointedValue = value,
                _log,
                NpcDiscoveryMaxCandidateTests,
                NpcDiscoveryMaxRejectedValues);

            var context = new NpcDiscoveryContext(
                discovery,
                Stopwatch.StartNew(),
                clickShopOption,
                stableCameraAngleBits);

            _log($"[NPC Calibration] Slow scan: client=({clientOriginX},{clientOriginY}) size=({clientWidth}x{clientHeight}), step={SellerScanStepPx}px, settle={NpcDiscoveryPointSettleMs}ms, confirmSample={NpcDiscoveryConfirmSampleMs}ms, timeout={NpcDiscoveryTimeoutMs}ms, maxCandidateTests={NpcDiscoveryMaxCandidateTests}.");

            NpcScanSweepState state;
            try
            {
                state = SweepNpcScanPoints(
                    clientOriginX, clientOriginY, clientWidth, clientHeight,
                    1,
                    "Slow NPC discovery",
                    (clientX, clientY, scanIndex, pointIndex) => HandleNpcDiscoveryPoint(
                        clientX, clientY, pointIndex,
                        clientOriginX, clientOriginY, clientWidth, clientHeight,
                        context));
            }
            catch (BotStopRequestedException)
            {
                _log($"[NPC Calibration] Cancelled after {context.Stopwatch.ElapsedMilliseconds}ms ({context.PointsScanned} points scanned, {discovery.CandidateTestCount} candidate(s) tested).");
                throw;
            }

            long elapsedMs = context.Stopwatch.ElapsedMilliseconds;

            if (state == NpcScanSweepState.Found)
            {
                _log($"[NPC Calibration] Discovery succeeded in {elapsedMs}ms ({context.PointsScanned} points scanned, {discovery.CandidateTestCount} candidate(s) tested).");
                return true;
            }

            if (state == NpcScanSweepState.Aborted && !string.IsNullOrEmpty(context.AbortReason))
                failureReason = context.AbortReason;

            string detail =
                $"{context.PointsScanned} points scanned, {context.StableObjectSamples} stable object samples, " +
                $"{discovery.CandidateTestCount} candidate(s) tested, {discovery.RejectedValueCount} rejected";

            failureReason = string.IsNullOrEmpty(failureReason)
                ? $"no NPC value could be validated ({detail})"
                : $"{failureReason} ({detail})";

            _log($"[NPC Calibration] Discovery ended after {elapsedMs}ms without a confirmed value.");
            return false;
        }

        /// <summary>
        /// One slow-scan point: move, settle, take a stable sample and, when the value looks
        /// like a new hovered object, validate it with a right-click.
        /// </summary>
        private NpcScanSweepState HandleNpcDiscoveryPoint(
            int clientX, int clientY, int pointIndex,
            int clientOriginX, int clientOriginY, int clientWidth, int clientHeight,
            NpcDiscoveryContext context)
        {
            if (!IsSellerScanPointWithinSafeArea(clientX, clientY, clientWidth, clientHeight))
                return NpcScanSweepState.Continue;

            // Stop/cancel and the hard time bound are checked periodically so the bot reacts
            // promptly instead of after the whole sweep.
            if (pointIndex % NpcDiscoveryForegroundCheckIntervalPoints == 0 && !IsGameWindowForeground())
            {
                throw new BotStopRequestedException(
                    "Game window is not the selected window — stopping the bot (slow NPC discovery aborted).");
            }

            if (context.Stopwatch.ElapsedMilliseconds >= NpcDiscoveryTimeoutMs)
            {
                context.AbortReason = $"slow scan timed out after {NpcDiscoveryTimeoutMs}ms ({context.PointsScanned} points searched from the screen center outwards)";
                return NpcScanSweepState.Aborted;
            }

            RestoreScanYawIfDrifted(pointIndex, context.StableCameraAngleBits);

            if (pointIndex > 0 && pointIndex % NpcDiscoveryProgressLogIntervalPoints == 0)
            {
                _log($"[NPC Calibration] Slow scan progress: {pointIndex} points, {context.StableObjectSamples} stable object samples, {context.Discovery.CandidateTestCount} candidate(s) tested, {context.Stopwatch.ElapsedMilliseconds}ms.");
            }

            int screenX = clientOriginX + clientX;
            int screenY = clientOriginY + clientY;

            MouseOperations.SetCursorPositionAbsolute(screenX, screenY);
            Thread.Sleep(NpcDiscoveryPointSettleMs);

            MouseOverValueSample sample = ReadStableMouseOverSample();
            context.PointsScanned++;

            if (!sample.ReadSucceeded)
            {
                // A failed read is "unknown", never "nothing hovered". Drop the hover
                // baseline so the next point is not compared against a stale value.
                context.PreviousSample = null;
                return NpcScanSweepState.Continue;
            }

            MouseOverValueSample? previousSample = context.PreviousSample;
            context.PreviousSample = sample;

            if (sample.Value == 0)
            {
                context.LastEmptyScreenPoint = (screenX, screenY);
                return NpcScanSweepState.Continue;
            }

            if (sample.Stable)
                context.StableObjectSamples++;

            // Transition evidence: the previous point published a different (stable) value, so
            // the cursor ARRIVING here changed the value. When it did not (e.g. the value lags
            // by one point or a large sprite spans several points), the probe below moves the
            // cursor to a known empty point and back. The probe is invoked lazily by
            // MouseOverValueDiscovery, i.e. only for values that are otherwise testable.
            bool naturalHoverChange = previousSample is { ReadSucceeded: true, Stable: true } previous
                                      && previous.Value != sample.Value;

            bool probeUsed = false;
            Func<bool> hoverChangeProbe = () =>
            {
                if (naturalHoverChange)
                    return true;

                if (context.LastEmptyScreenPoint is not { } emptyPoint)
                    return true; // no reference point yet — cannot contradict the sample

                probeUsed = true;
                return ProbeHoverChange(sample.Value, screenX, screenY, emptyPoint);
            };

            MouseOverCandidateDecision decision = context.Discovery.Evaluate(sample, hoverChangeProbe);
            if (decision != MouseOverCandidateDecision.Candidate)
                return NpcScanSweepState.Continue;

            // Never start a candidate validation that could outlive the run's time budget.
            if (context.Stopwatch.ElapsedMilliseconds >= NpcDiscoveryTimeoutMs)
            {
                context.AbortReason = $"slow scan timed out after {NpcDiscoveryTimeoutMs}ms ({context.PointsScanned} points searched from the screen center outwards)";
                return NpcScanSweepState.Aborted;
            }

            int attempt = context.Discovery.StartCandidateTest(sample.Value);
            string hoverEvidence = naturalHoverChange
                ? "point transition"
                : probeUsed ? "leave/return probe" : "unverified";
            _log($"[NPC Calibration] Candidate value detected: {MouseOverValueDiscovery.Describe(sample.Value)} at client ({clientX},{clientY}) screen ({screenX},{screenY}) [attempt {attempt}/{NpcDiscoveryMaxCandidateTests}, stableObservations={context.Discovery.ObservationCount(sample.Value)}, hoverChange={hoverEvidence}, npcHighlight={sample.NpcHighlighted}].");

            switch (TestNpcCandidate(sample.Value, screenX, screenY, context))
            {
                case NpcCandidateTestOutcome.Confirmed:
                    return NpcScanSweepState.Found;

                case NpcCandidateTestOutcome.AbortDiscovery:
                    context.AbortReason = "candidate validation left the game UI in an unusable state";
                    return NpcScanSweepState.Aborted;

                default:
                    return NpcScanSweepState.Continue;
            }
        }

        /// <summary>
        /// Confirms that <paramref name="candidateValue"/> is tied to the cursor position:
        /// the cursor moves to a known empty point (the value must stop matching) and then
        /// returns to the candidate (the value must be published again). The cursor always
        /// ends on the candidate point, so a confirmed candidate can be right-clicked at once.
        ///
        /// An inconclusive probe (a failed/unstable read at either location) returns true: it
        /// must never block a candidate that the right-click + dialogue check can still verify.
        /// </summary>
        private bool ProbeHoverChange(int candidateValue, int screenX, int screenY, (int ScreenX, int ScreenY) emptyPoint)
        {
            MouseOperations.SetCursorPositionAbsolute(emptyPoint.ScreenX, emptyPoint.ScreenY);
            Thread.Sleep(NpcDiscoveryPointSettleMs);
            MouseOverValueSample away = ReadStableMouseOverSample();

            MouseOperations.SetCursorPositionAbsolute(screenX, screenY);
            Thread.Sleep(NpcDiscoveryPointSettleMs);
            MouseOverValueSample back = ReadStableMouseOverSample();

            if (!away.ReadSucceeded || !away.Stable || !back.ReadSucceeded || !back.Stable)
                return true;

            return away.Value != candidateValue && back.Value == candidateValue;
        }

        /// <summary>
        /// Validates one candidate exactly the way the bot would use it: keep the cursor on the
        /// object, right-click it and require an ACTUAL NPC dialogue to open. A memory change
        /// or a sent click is never treated as success.
        ///
        /// On success the confirmed value becomes the active NPC value. When the caller needs
        /// the shop (repot/seller flow) the Shop option must also open the shop window; a
        /// dialogue that opens but is not usable for the workflow is closed again and the
        /// candidate is rejected without touching the active value.
        /// </summary>
        private NpcCandidateTestOutcome TestNpcCandidate(
            int candidateValue, int screenX, int screenY, NpcDiscoveryContext context)
        {
            MouseOverValueDiscovery discovery = context.Discovery;

            if (_memory.IsShopOpen())
            {
                _log("[NPC Calibration] Shop window is already open — keeping the current NPC value and continuing the workflow.");
                return NpcCandidateTestOutcome.Confirmed;
            }

            // A dialogue left open before the click would make the transition ambiguous.
            if (!TryCloseOpenNpcDialog("before testing a candidate", out string closeFailure))
            {
                _log($"[NPC Calibration] Cannot test candidate {MouseOverValueDiscovery.Describe(candidateValue)}: {closeFailure}.");
                return NpcCandidateTestOutcome.AbortDiscovery;
            }

            // The candidate must still be under the cursor right before the click.
            Thread.Sleep(NpcDiscoveryPointSettleMs);
            MouseOverValueSample current = ReadStableMouseOverSample();
            if (!current.ReadSucceeded || !current.Stable || current.Value != candidateValue)
            {
                _log($"[NPC Calibration] Candidate {MouseOverValueDiscovery.Describe(candidateValue)} is no longer under the cursor — skipping the click.");
                return NpcCandidateTestOutcome.NotConfirmed;
            }

            _log($"[NPC Calibration] Testing candidate {MouseOverValueDiscovery.Describe(candidateValue)} with a right-click.");
            LastNpcRightClickScreenPosition = (screenX, screenY);

            MouseOperations.MouseEvent(MouseOperations.MouseEventFlags.RightDown);
            Thread.Sleep(50);
            MouseOperations.MouseEvent(MouseOperations.MouseEventFlags.RightUp);

            // Bounded wait for the dialogue state transition CLOSED -> OPEN. A dialogue that
            // was already open was closed above, so only this click can have opened it.
            var dialogWait = Stopwatch.StartNew();
            bool dialogOpened = false;
            while (true)
            {
                if (_memory.IsDialogOpen())
                {
                    dialogOpened = true;
                    break;
                }

                if (dialogWait.ElapsedMilliseconds >= NpcDiscoveryDialogOpenTimeoutMs)
                    break;

                Thread.Sleep(NpcDiscoveryStatePollMs);
            }

            if (!dialogOpened)
            {
                discovery.Reject(candidateValue,
                    $"NPC dialogue did not open within {NpcDiscoveryDialogOpenTimeoutMs}ms after the right-click");
                return NpcCandidateTestOutcome.NotConfirmed;
            }

            _log($"[NPC Calibration] NPC dialogue confirmed for candidate {MouseOverValueDiscovery.Describe(candidateValue)} after {dialogWait.ElapsedMilliseconds}ms (S_IsDialogOpen1=1).");

            if (!context.ClickShopOption)
            {
                int previousValue = discovery.Confirm(candidateValue);
                NoteNpcMouseOverValueAutoCalibrated();
                _log($"[NPC Calibration] Calibration complete: {MouseOverValueDiscovery.Describe(previousValue)} -> {MouseOverValueDiscovery.Describe(candidateValue)}. The dialogue stays open for the caller.");
                return NpcCandidateTestOutcome.Confirmed;
            }

            // Seller workflow: the dialogue must also expose the Shop option, otherwise it
            // belongs to some other NPC and cannot satisfy the current repot action.
            ClickShopOptionInDialog();

            var shopWait = Stopwatch.StartNew();
            while (true)
            {
                if (_memory.IsShopOpen())
                    break;

                if (shopWait.ElapsedMilliseconds >= NpcDiscoveryShopOpenTimeoutMs)
                    break;

                Thread.Sleep(NpcDiscoveryStatePollMs);
            }

            if (_memory.IsShopOpen())
            {
                // Same populate delay the fast path uses, then apply the value in memory.
                Thread.Sleep(NpcDiscoveryShopPopulateDelayMs);
                int previousValue = discovery.Confirm(candidateValue);
                NoteNpcMouseOverValueAutoCalibrated();
                _log($"[NPC Calibration] Calibration complete: shop window opened through the candidate's dialogue, {MouseOverValueDiscovery.Describe(previousValue)} -> {MouseOverValueDiscovery.Describe(candidateValue)}.");
                return NpcCandidateTestOutcome.Confirmed;
            }

            _log($"[NPC Calibration] Candidate {MouseOverValueDiscovery.Describe(candidateValue)} opened a dialogue, but the Shop option did not open the shop window within {NpcDiscoveryShopOpenTimeoutMs}ms — not usable for the seller workflow. Closing the dialogue and continuing discovery.");
            discovery.Reject(candidateValue,
                "dialogue opened but the Shop option did not open the shop window");

            if (!TryCloseOpenNpcDialog("after an unusable NPC dialogue", out string unusableCloseFailure))
            {
                _log($"[NPC Calibration] {unusableCloseFailure}.");
                return NpcCandidateTestOutcome.AbortDiscovery;
            }

            return NpcCandidateTestOutcome.NotConfirmed;
        }

        /// <summary>
        /// Marks this game session as "the NPC MouseOver value was discovered automatically"
        /// and persists the new value to MouseCalibration.json, so the next bot run / app
        /// start reuses it instead of falling back to the previous in-memory value.
        /// Both the NPC and the item MouseOver values change together with the game launch, so
        /// the loot system uses the session marker to re-discover the item value on its next
        /// scan (see <see cref="MouseOverCalibrationSession.NeedsItemValueDiscovery"/>).
        /// </summary>
        private void NoteNpcMouseOverValueAutoCalibrated()
        {
            MouseOverCalibrationSession.Current.MarkNpcValueDiscovered(_memory.GameSessionIdentity);

            bool saved = MouseCalibrationStore.Save(
                SellerPointedValue,
                BotConstants.GameMagicValues.LootMouseOverValue);

            if (saved)
            {
                _log($"[NPC Calibration] Calibration saved to {MouseCalibrationStore.FilePath} — " +
                     "the next bot run/app start reuses the new value.");
            }
            else
            {
                _log($"[NPC Calibration] WARNING: could not save the confirmed NPC value to {MouseCalibrationStore.FilePath} — " +
                     "the running bot uses it, but a restart would fall back to the previous calibration.");
            }

            _log("[Item Calibration] NPC value was auto-calibrated in this game session — the item MouseOver value is now queued for automatic discovery on the next loot scan.");
        }

        /// <summary>
        /// Closes any open NPC dialogue / shop window with Escape (the existing UI mechanism —
        /// never a click, which could walk the character) and waits, bounded, for the state to
        /// clear. Returns false when the UI stayed open: candidate validation is impossible then.
        /// </summary>
        private bool TryCloseOpenNpcDialog(string logContext, out string failureReason)
        {
            failureReason = string.Empty;

            // Never close the shop window: it is the state the repot/seller workflow needs.
            // (Stage 2 starts only when no shop is open; this guards the rare late-open case.)
            if (_memory.IsShopOpen())
            {
                failureReason = $"the shop window is open {logContext} — not closing it";
                return false;
            }

            if (!_memory.IsDialogOpen())
                return true;

            _log($"[NPC Calibration] Closing the open NPC dialogue with Escape {logContext}.");
            GameInput.PressKey(GameInput.VK_ESCAPE, GameInput.SCAN_ESCAPE);

            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < NpcDiscoveryDialogOpenTimeoutMs)
            {
                if (!_memory.IsDialogOpen() && !_memory.IsShopOpen())
                    return true;

                Thread.Sleep(NpcDiscoveryStatePollMs);
            }

            failureReason = $"the open NPC dialogue did not close within {NpcDiscoveryDialogOpenTimeoutMs}ms {logContext}";
            return false;
        }

        /// <summary>
        /// Periodic yaw-drift guard shared by the fast scan and the slow discovery scan: if
        /// the cursor sweep itself changed the camera yaw, restore the exact pre-scan angle so
        /// the on-screen NPC positions stay valid. Warns once per scan when the restore fails.
        /// </summary>
        private void RestoreScanYawIfDrifted(int pointIndex, int? stableCameraAngleBits)
        {
            if (!stableCameraAngleBits.HasValue || pointIndex % SellerScanYawCheckIntervalPoints != 0)
                return;

            if (_memory.TryGetCameraAngleBits(out int currentAngleBits) &&
                currentAngleBits != stableCameraAngleBits.Value &&
                !_memory.TrySetCameraAngleBits(stableCameraAngleBits.Value) &&
                !_sellerScanYawRestoreWarningLogged)
            {
                _sellerScanYawRestoreWarningLogged = true;
                _log("ItemSeller: WARNING — camera yaw drifted during scan and could not be restored.");
            }
        }

        private void RestoreSellerScanYaw(int expectedAngleBits)
        {
            if (!_memory.TryGetCameraAngleBits(out int currentAngleBits))
            {
                _log("ItemSeller: WARNING — camera yaw could not be read back after scan; it may have changed.");
                return;
            }

            if (currentAngleBits == expectedAngleBits)
                return;

            if (_memory.TrySetCameraAngleBits(expectedAngleBits))
                _log($"ItemSeller: Restored camera yaw after scan (raw=0x{expectedAngleBits:X8}).");
            else
                _log("ItemSeller: WARNING — failed to restore the pre-scan camera yaw.");
        }

        /// <summary>
        /// Clicks the "Shop" option in the seller context-menu dialog at the same
        /// window-relative position (145, 460) used by the pre-refactor
        /// <c>OpenShopWindow()</c>. This is the click that actually opens the shop
        /// window after the right-click on the seller NPC.
        /// </summary>
        private void ClickShopOptionInDialog()
        {
            const int shopRelX = 145;
            const int shopRelY = 460;

            var (winX, winY) = GetWindowOrigin();
            int screenX = shopRelX + winX;
            int screenY = shopRelY + winY;

            _log($"[ItemSeller] ClickShopOptionInDialog -> screen ({screenX},{screenY}) [relative ({shopRelX},{shopRelY}) + window ({winX},{winY})]");
            MouseOperations.MoveAndLeftClickAbsolute(screenX, screenY, 200);
        }

        /// <summary>
        /// Resolves the game-window client area in screen coordinates. Returns false
        /// when the game window cannot be located. The returned origin is the
        /// top-left of the client area in absolute screen coordinates; width/height
        /// are the client dimensions.
        /// </summary>
        private bool TryGetGameClientArea(out int originX, out int originY, out int width, out int height)
        {
            originX = 0; originY = 0; width = 0; height = 0;

            nint hwnd = FindGameWindowHandle();
            if (hwnd == nint.Zero) return false;

            if (!GetClientRect(hwnd, out RECT clientRect))
                return false;

            POINT topLeft = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref topLeft))
                return false;

            originX = topLeft.X;
            originY = topLeft.Y;
            width = clientRect.Right - clientRect.Left;
            height = clientRect.Bottom - clientRect.Top;
            return width > 0 && height > 0;
        }

        /// <summary>
        /// Captures a screenshot of the game client area into
        /// <c>Screenshots/SellDialogScanFailed/&lt;timestamp&gt;.png</c> for debugging
        /// when the seller dialog could not be opened. Errors are logged but never thrown.
        /// </summary>
        private void CaptureSellFailureScreenshot()
        {
            try
            {
                int captureX, captureY, captureW, captureH;
                if (TryGetGameClientArea(out captureX, out captureY, out captureW, out captureH))
                {
                    // All good.
                }
                else
                {
                    captureX = 0;
                    captureY = 0;
                    captureW = BotConstants.Loot.BitmapWidth;
                    captureH = BotConstants.Loot.BitmapHeight;
                }

                using (Bitmap bitmap = new Bitmap(captureW, captureH))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(captureX, captureY, 0, 0, bitmap.Size);

                    string screenshotsDir = Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Screenshots", "SellDialogScanFailed");
                    Directory.CreateDirectory(screenshotsDir);

                    string fileName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";
                    string filePath = Path.Combine(screenshotsDir, fileName);

                    bitmap.Save(filePath, ImageFormat.Png);
                    _log($"[ItemSeller] Seller-scan-failure screenshot saved: {filePath}");
                }
            }
            catch (Exception ex)
            {
                _log($"[ItemSeller] Failed to capture seller-scan-failure screenshot: {ex.Message}");
            }
        }

        public enum InventoryType
        {
            Inventory,
            Storage
        }
    }
}
