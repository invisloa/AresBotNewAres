using System.Collections.Generic;
using System.IO;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Round-trip tests for the calibration file: confirmed values must survive an app
    /// restart, and pending cross-scan observations must only be reused for the same game
    /// session and the same active item value.
    /// </summary>
    public sealed class MouseCalibrationStoreTests
    {
        [Fact]
        public void ConfirmedValuesAndItemProgressRoundTrip()
        {
            try
            {
                Assert.True(MouseCalibrationStore.Save(111, 222));
                Assert.True(MouseCalibrationStore.SaveItemScanProgress(
                    processId: 7,
                    moduleBase: 0x400000,
                    activeLootMouseOverValue: 222,
                    scanCounts: new Dictionary<int, int> { [333] = 2 }));

                Assert.True(MouseCalibrationStore.Load(out int seller, out int loot));
                Assert.Equal(111, seller);
                Assert.Equal(222, loot);

                Assert.True(MouseCalibrationStore.LoadItemScanProgress(7, 0x400000, 222, out var counts));
                Assert.Equal(2, counts[333]);

                // A different game session or a re-calibrated active value must not inherit it.
                Assert.False(MouseCalibrationStore.LoadItemScanProgress(8, 0x400000, 222, out _));
                Assert.False(MouseCalibrationStore.LoadItemScanProgress(7, 0x500000, 222, out _));
                Assert.False(MouseCalibrationStore.LoadItemScanProgress(7, 0x400000, 999, out _));

                // Saving a confirmed calibration invalidates pending observations.
                Assert.True(MouseCalibrationStore.Save(444, 555));
                Assert.False(MouseCalibrationStore.LoadItemScanProgress(7, 0x400000, 222, out _));
            }
            finally
            {
                try
                {
                    if (File.Exists(MouseCalibrationStore.FilePath))
                        File.Delete(MouseCalibrationStore.FilePath);
                }
                catch (IOException)
                {
                    // Cleanup is best-effort; a scanner briefly holding the file must not
                    // turn the persistence assertion into a flaky failure.
                }
            }
        }
    }
}
