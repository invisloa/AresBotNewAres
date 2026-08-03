using System;
using System.IO;
using System.Text.Json;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Persists the mouseover calibration values captured via the
    /// 'Mouseover NPC' / 'Mouseover Item' buttons in the Bot window
    /// (<see cref="ViewModels.MainViewModel.CaptureNpcMouseOver"/> /
    /// <see cref="ViewModels.MainViewModel.CaptureItemMouseOver"/>).
    ///
    /// The captured values are written to <c>MouseCalibration.json</c> next to the
    /// exe, and <see cref="Load"/> restores them on the next app start, so the bot
    /// keeps working without re-calibrating after every launch.
    /// </summary>
    public static class MouseCalibrationStore
    {
        /// <summary>Full path of the calibration file (next to the exe).</summary>
        public static readonly string FilePath =
            Path.Combine(AppContext.BaseDirectory, "MouseCalibration.json");

        /// <summary>
        /// Saves the current mouseover calibration values to disk.
        /// Returns true when the file was written successfully.
        /// </summary>
        public static bool Save(int sellerPointedValue, int lootMouseOverValue)
        {
            try
            {
                var data = new CalibrationData
                {
                    SellerPointedValue = sellerPointedValue,
                    LootMouseOverValue = lootMouseOverValue,
                    SavedAt = DateTime.Now
                };
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(FilePath, JsonSerializer.Serialize(data, options));
                return true;
            }
            catch
            {
                return false;
            }
        }

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

            try
            {
                if (!File.Exists(FilePath))
                    return false;

                string json = File.ReadAllText(FilePath);
                var data = JsonSerializer.Deserialize<CalibrationData>(json);
                if (data == null)
                    return false;

                sellerPointedValue = data.SellerPointedValue;
                lootMouseOverValue = data.LootMouseOverValue;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private sealed class CalibrationData
        {
            public int SellerPointedValue { get; set; }
            public int LootMouseOverValue { get; set; }
            public DateTime? SavedAt { get; set; }
        }
    }
}
