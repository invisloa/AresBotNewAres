using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Trigger rule for the automatic item MouseOver re-discovery:
    ///   - no configured value → immediately,
    ///   - NPC value auto-calibrated in this game session → on the next scan (once),
    ///   - otherwise only after N consecutive scans that found no item.
    /// </summary>
    public sealed class ItemMouseOverCalibrationTriggerTests
    {
        private const int ActiveValue = 121498128;

        [Fact]
        public void MissingConfiguredValueStartsDiscoveryImmediately()
        {
            bool start = LootSystem.TryGetItemCalibrationReason(
                activeItemValue: 0,
                consecutiveEmptyScans: 0,
                needsItemValueDiscovery: false,
                npcTriggerAttempted: false,
                out string reason,
                out bool consumeNpcTrigger);

            Assert.True(start);
            Assert.False(consumeNpcTrigger);
            Assert.Contains("no item MouseOver value", reason);
        }

        [Fact]
        public void NpcAutoCalibrationStartsDiscoveryOnTheNextScan()
        {
            bool start = LootSystem.TryGetItemCalibrationReason(
                ActiveValue,
                consecutiveEmptyScans: 0,
                needsItemValueDiscovery: true,
                npcTriggerAttempted: false,
                out string reason,
                out bool consumeNpcTrigger);

            Assert.True(start);
            Assert.True(consumeNpcTrigger);
            Assert.Contains("NPC MouseOver value was auto-calibrated", reason);
        }

        [Fact]
        public void NpcTriggerIsOnlyUsedOnce()
        {
            bool start = LootSystem.TryGetItemCalibrationReason(
                ActiveValue,
                consecutiveEmptyScans: 0,
                needsItemValueDiscovery: true,
                npcTriggerAttempted: true,
                out _,
                out bool consumeNpcTrigger);

            Assert.False(start);
            Assert.False(consumeNpcTrigger);
        }

        [Fact]
        public void EmptyScanStreakStartsDiscoveryOnlyAfterTheConfiguredNumberOfScans()
        {
            bool beforeThreshold = LootSystem.TryGetItemCalibrationReason(
                ActiveValue,
                consecutiveEmptyScans: BotConstants.Loot.ItemCalibrationAfterEmptyScans - 1,
                needsItemValueDiscovery: false,
                npcTriggerAttempted: true,
                out _,
                out _);

            bool atThreshold = LootSystem.TryGetItemCalibrationReason(
                ActiveValue,
                consecutiveEmptyScans: BotConstants.Loot.ItemCalibrationAfterEmptyScans,
                needsItemValueDiscovery: false,
                npcTriggerAttempted: true,
                out string reason,
                out bool consumeNpcTrigger);

            Assert.False(beforeThreshold);
            Assert.True(atThreshold);
            Assert.False(consumeNpcTrigger);
            Assert.Contains("consecutive loot scans found no item", reason);
        }

        [Fact]
        public void NpcTriggerTakesPrecedenceOverTheEmptyScanStreak()
        {
            bool start = LootSystem.TryGetItemCalibrationReason(
                ActiveValue,
                consecutiveEmptyScans: BotConstants.Loot.ItemCalibrationAfterEmptyScans,
                needsItemValueDiscovery: true,
                npcTriggerAttempted: false,
                out string reason,
                out bool consumeNpcTrigger);

            Assert.True(start);
            Assert.True(consumeNpcTrigger);
            Assert.Contains("NPC", reason);
        }

        [Fact]
        public void KnownItemValueAndNoEmptyScansDoNotStartDiscovery()
        {
            bool start = LootSystem.TryGetItemCalibrationReason(
                ActiveValue,
                consecutiveEmptyScans: 0,
                needsItemValueDiscovery: false,
                npcTriggerAttempted: false,
                out string reason,
                out bool consumeNpcTrigger);

            Assert.False(start);
            Assert.False(consumeNpcTrigger);
            Assert.Empty(reason);
        }
    }
}
