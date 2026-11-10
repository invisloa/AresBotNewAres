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
                itemValueDiscoveryPending: false,
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
                itemValueDiscoveryPending: false,
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
                itemValueDiscoveryPending: false,
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
                itemValueDiscoveryPending: false,
                out _,
                out _);

            bool atThreshold = LootSystem.TryGetItemCalibrationReason(
                ActiveValue,
                consecutiveEmptyScans: BotConstants.Loot.ItemCalibrationAfterEmptyScans,
                needsItemValueDiscovery: false,
                npcTriggerAttempted: true,
                itemValueDiscoveryPending: false,
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
                itemValueDiscoveryPending: false,
                out string reason,
                out bool consumeNpcTrigger);

            Assert.True(start);
            Assert.True(consumeNpcTrigger);
            Assert.Contains("NPC", reason);
        }

        [Fact]
        public void PendingDiscoveryRetriesOnTheNextScanWithoutAnyEmptyScans()
        {
            bool start = LootSystem.TryGetItemCalibrationReason(
                ActiveValue,
                consecutiveEmptyScans: 0,
                needsItemValueDiscovery: false,
                npcTriggerAttempted: true,
                itemValueDiscoveryPending: true,
                out string reason,
                out bool consumeNpcTrigger);

            Assert.True(start);
            Assert.False(consumeNpcTrigger);
            Assert.Contains("did not confirm a value", reason);
        }

        [Fact]
        public void PendingDiscoveryDoesNotConsumeTheOneShotNpcTrigger()
        {
            bool start = LootSystem.TryGetItemCalibrationReason(
                ActiveValue,
                consecutiveEmptyScans: 0,
                needsItemValueDiscovery: true,
                npcTriggerAttempted: false,
                itemValueDiscoveryPending: true,
                out _,
                out bool consumeNpcTrigger);

            Assert.True(start);
            Assert.False(consumeNpcTrigger);
        }

        [Fact]
        public void EveryMandatoryLootRequestContributesAtMostOneItemCalibrationVote()
        {
            // First attempt of a kill may be Incomplete and still counts as that kill's scan.
            Assert.True(LootSystem.ShouldCountItemCalibrationVote(
                validComplete: false, mandatoryRequestId: 2, lastVotedRequestId: 1));

            // A retry of the SAME request must never count a second time, not even when it
            // eventually completes.
            Assert.False(LootSystem.ShouldCountItemCalibrationVote(
                validComplete: false, mandatoryRequestId: 2, lastVotedRequestId: 2));
            Assert.False(LootSystem.ShouldCountItemCalibrationVote(
                validComplete: true, mandatoryRequestId: 2, lastVotedRequestId: 2));

            // The next kill is a new scan and counts again.
            Assert.True(LootSystem.ShouldCountItemCalibrationVote(
                validComplete: false, mandatoryRequestId: 3, lastVotedRequestId: 2));
        }

        [Fact]
        public void OutsideAMandatoryRequestOnlyACompletedScanVotes()
        {
            Assert.True(LootSystem.ShouldCountItemCalibrationVote(
                validComplete: true, mandatoryRequestId: 0, lastVotedRequestId: 0));
            Assert.False(LootSystem.ShouldCountItemCalibrationVote(
                validComplete: false, mandatoryRequestId: 0, lastVotedRequestId: 0));
        }

        [Fact]
        public void KnownItemValueAndNoEmptyScansDoNotStartDiscovery()
        {
            bool start = LootSystem.TryGetItemCalibrationReason(
                ActiveValue,
                consecutiveEmptyScans: 0,
                needsItemValueDiscovery: false,
                npcTriggerAttempted: false,
                itemValueDiscoveryPending: false,
                out string reason,
                out bool consumeNpcTrigger);

            Assert.False(start);
            Assert.False(consumeNpcTrigger);
            Assert.Empty(reason);
        }
    }
}
