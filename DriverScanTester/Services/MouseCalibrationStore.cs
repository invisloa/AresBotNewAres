using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Persists the mouseover calibration values captured via the
    /// 'Mouseover NPC' / 'Mouseover Item' buttons in the Bot window
    /// (<see cref="ViewModels.MainViewModel.CaptureNpcMouseOver"/> /
    /// <see cref="ViewModels.MainViewModel.CaptureItemMouseOver"/>), the values found by the
    /// automatic NPC/item calibration, and the item-value discovery progress.
    ///
    /// The data is written to <c>MouseCalibration.json</c> next to the exe, and
    /// <see cref="Load"/> restores the calibrated values on the next app start, so the bot
    /// keeps working without re-calibrating after every launch.
    ///
    /// The pending item discovery progress is stored together with the game session identity
    /// (process id + module base) and the active item value it belongs to. A different game
    /// launch (or a re-calibrated item value) therefore never inherits stale observations.
    /// </summary>
    public static class MouseCalibrationStore
    {
        /// <summary>Full path of the calibration file (next to the exe).</summary>
        public static readonly string FilePath =
            Path.Combine(AppContext.BaseDirectory, "MouseCalibration.json");

        private static readonly object FileGate = new();
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        /// <summary>
        /// Saves the current mouseover calibration values to disk.
        /// A confirmed calibration invalidates any pending item discovery progress, so the
        /// progress section is cleared here.
        /// Returns true when the file was written successfully.
        /// </summary>
        public static bool Save(int sellerPointedValue, int lootMouseOverValue)
            => Update(data =>
            {
                data.SellerPointedValue = sellerPointedValue;
                data.LootMouseOverValue = lootMouseOverValue;
                data.SavedAt = DateTime.Now;
                data.ItemScanProgress = null;
            });

        /// <summary>
        /// Saves the cross-scan item MouseOver discovery progress (how many separate loot
        /// scans published each candidate value) without touching the calibrated values.
        /// The progress is only valid for the given game session and active item value.
        /// </summary>
        public static bool SaveItemScanProgress(
            uint processId,
            ulong moduleBase,
            int activeLootMouseOverValue,
            IReadOnlyDictionary<int, int> scanCounts)
            => Update(data =>
            {
                data.ItemScanProgress = new ItemScanProgressData
                {
                    GameProcessId = processId,
                    GameModuleBase = moduleBase,
                    ActiveLootMouseOverValue = activeLootMouseOverValue,
                    ScanCounts = new Dictionary<int, int>(scanCounts),
                    UpdatedAt = DateTime.Now
                };
            });

        /// <summary>
        /// Loads the saved item discovery progress when it belongs to the CURRENT game session
        /// (same process id + module base) and the CURRENT active item value.
        /// Returns false when there is no usable progress (missing file, different session or
        /// the active value was re-calibrated in the meantime).
        /// </summary>
        public static bool LoadItemScanProgress(
            uint processId,
            ulong moduleBase,
            int activeLootMouseOverValue,
            out Dictionary<int, int> scanCounts)
        {
            scanCounts = new Dictionary<int, int>();

            CalibrationData? data = Read();
            ItemScanProgressData? progress = data?.ItemScanProgress;
            if (progress == null)
                return false;

            if (progress.GameProcessId != processId ||
                progress.GameModuleBase != moduleBase ||
                progress.ActiveLootMouseOverValue != activeLootMouseOverValue)
            {
                return false;
            }

            if (progress.ScanCounts != null)
            {
                foreach (KeyValuePair<int, int> pair in progress.ScanCounts)
                {
                    if (pair.Key != 0 && pair.Value > 0)
                        scanCounts[pair.Key] = pair.Value;
                }
            }

            return scanCounts.Count > 0;
        }

        /// <summary>
        /// Removes the pending item discovery progress (used once a value is confirmed).
        /// Returns true when the file was updated successfully.
        /// </summary>
        public static bool ClearItemScanProgress()
            => Update(data => data.ItemScanProgress = null);

        /// <summary>
        /// Loads the last saved calibration from disk.
        /// Returns true and fills <paramref name="sellerPointedValue"/> /
        /// <paramref name="lootMouseOverValue"/> when a valid file exists;
        /// returns false (values left at 0) when the file is missing or corrupt.
        /// </summary>
        public static bool Load(out int sellerPointedValue, out int lootMouseOverValue)
        {
            sellerPointedValue = 0;
            lootMouseOverValue = 0;

            CalibrationData? data = Read();
            if (data == null)
                return false;

            sellerPointedValue = data.SellerPointedValue;
            lootMouseOverValue = data.LootMouseOverValue;
            return true;
        }

        /// <summary>
        /// Applies a change to the calibration file while preserving the sections the caller
        /// does not touch (e.g. saving progress never overwrites the calibrated values).
        /// </summary>
        private static bool Update(Action<CalibrationData> mutate)
        {
            try
            {
                lock (FileGate)
                {
                    CalibrationData data = ReadLocked() ?? new CalibrationData();
                    mutate(data);
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(data, JsonOptions));
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static CalibrationData? Read()
        {
            lock (FileGate)
                return ReadLocked();
        }

        private static CalibrationData? ReadLocked()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return null;

                string json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<CalibrationData>(json);
            }
            catch
            {
                return null;
            }
        }

        private sealed class CalibrationData
        {
            public int SellerPointedValue { get; set; }
            public int LootMouseOverValue { get; set; }
            public DateTime? SavedAt { get; set; }

            /// <summary>Pending automatic item discovery progress (never a confirmed value).</summary>
            public ItemScanProgressData? ItemScanProgress { get; set; }
        }

        private sealed class ItemScanProgressData
        {
            public uint GameProcessId { get; set; }
            public ulong GameModuleBase { get; set; }

            /// <summary>The active LootMouseOverValue the observations belong to.</summary>
            public int ActiveLootMouseOverValue { get; set; }

            /// <summary>How many separate loot scans published each candidate value.</summary>
            public Dictionary<int, int> ScanCounts { get; set; } = new();

            public DateTime? UpdatedAt { get; set; }
        }
    }
}
