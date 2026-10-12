using System;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>Deterministic scheduling rules for coalesced background SOD/SOP passes.</summary>
    public sealed class LootPinkRescanTests
    {
        private static readonly DateTime Start = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(BotConstants.Loot.PinkRescanDelayMs);
        private static readonly TimeSpan FollowUp = TimeSpan.FromMilliseconds(50);
        private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(50);

        private static LootSystem.PinkRescanAction Decide(
            bool pinkLootOnly, bool pinkPassActive, DateTime dueAt, DateTime now)
            => LootSystem.DecidePinkRescan(
                pinkLootOnly, pinkPassActive, dueAt, now,
                TimeSpan.FromMilliseconds(BotConstants.Loot.PinkRescanExpireMs));

        [Fact]
        public void NothingPending_IsNone()
            => Assert.Equal(LootSystem.PinkRescanAction.None,
                Decide(true, false, DateTime.MinValue, Start));

        [Fact]
        public void BeforeDue_Waits()
            => Assert.Equal(LootSystem.PinkRescanAction.Wait,
                Decide(true, false, Start.Add(Delay), Start));

        [Fact]
        public void AtDue_Runs()
            => Assert.Equal(LootSystem.PinkRescanAction.Run,
                Decide(true, false, Start.Add(Delay), Start.Add(Delay)));

        [Fact]
        public void LatePendingWorkIsNotExpired()
            => Assert.Equal(LootSystem.PinkRescanAction.Run,
                Decide(true, false, Start, Start.AddMinutes(2)));

        [Fact]
        public void WhileImmediatePinkPassRuns_Waits()
            => Assert.Equal(LootSystem.PinkRescanAction.Wait,
                Decide(true, true, Start, Start.AddSeconds(1)));

        [Fact]
        public void OutsidePinkLootOnlyMode_IsExpiredAndCancelledByScheduler()
        {
            Assert.Equal(LootSystem.PinkRescanAction.Expire,
                Decide(false, false, Start, Start));

            var scheduler = new PinkRescanScheduler();
            scheduler.ScheduleKill(Start, Delay);
            Assert.False(scheduler.TryBegin(Start.Add(Delay), modeEnabled: false,
                immediatePinkPassActive: false, out _));
            Assert.False(scheduler.Snapshot.Pending);
        }

        [Fact]
        public void OneKill_SchedulesAtConfiguredDelay()
        {
            var scheduler = new PinkRescanScheduler();
            scheduler.ScheduleKill(Start, Delay);

            PinkRescanSnapshot snapshot = scheduler.Snapshot;
            Assert.True(snapshot.Pending);
            Assert.Equal(Start.Add(Delay), snapshot.DueAtUtc);
            Assert.Equal(1, snapshot.Generation);
        }

        [Fact]
        public void NewKillBeforeDeadline_DoesNotPostponeEarliestDueTime()
        {
            var scheduler = new PinkRescanScheduler();
            scheduler.ScheduleKill(Start, Delay);
            DateTime firstDue = scheduler.Snapshot.DueAtUtc;
            scheduler.ScheduleKill(Start.AddMilliseconds(800), Delay);

            Assert.Equal(firstDue, scheduler.Snapshot.DueAtUtc);
        }

        [Fact]
        public void RapidKillsCannotStarveTheFirstScheduledPass()
        {
            var scheduler = new PinkRescanScheduler();
            scheduler.ScheduleKill(Start, Delay);
            DateTime firstDue = scheduler.Snapshot.DueAtUtc;

            for (int i = 1; i <= 12; i++)
                scheduler.ScheduleKill(Start.AddMilliseconds(i * 500), Delay);

            Assert.Equal(firstDue, scheduler.Snapshot.DueAtUtc);
            Assert.True(scheduler.TryBegin(firstDue, modeEnabled: true,
                immediatePinkPassActive: false, out PinkRescanTicket ticket));
            Assert.Equal(13, ticket.Generation);
        }

        [Fact]
        public void KillDuringActivePass_IsQueuedAfterEmptyCapture()
        {
            var scheduler = new PinkRescanScheduler();
            scheduler.ScheduleKill(Start, Delay);
            Assert.True(scheduler.TryBegin(Start.Add(Delay), true, false, out PinkRescanTicket ticket));
            Assert.False(scheduler.TryBegin(Start.Add(Delay), true, false, out _));

            DateTime killDuringCapture = Start.Add(Delay).AddMilliseconds(100);
            scheduler.ScheduleKill(killDuringCapture, Delay);
            scheduler.Complete(ticket, killDuringCapture.AddMilliseconds(300),
                PinkRescanPassOutcome.NoItem, FollowUp, Retry);

            PinkRescanSnapshot snapshot = scheduler.Snapshot;
            Assert.True(snapshot.Pending);
            Assert.False(snapshot.Active);
            Assert.Equal(killDuringCapture.Add(Delay), snapshot.DueAtUtc);
            Assert.True(snapshot.Generation > ticket.Generation);
        }

        [Fact]
        public void DuePassRunsEvenWhileTargetRemainsSelected()
        {
            // Target selection is deliberately not a scheduler gate: the production loop
            // checks/rescans before its selected-mob pause, preserving background behavior.
            var scheduler = new PinkRescanScheduler();
            scheduler.ScheduleKill(Start, Delay);

            Assert.True(scheduler.TryBegin(Start.Add(Delay), modeEnabled: true,
                immediatePinkPassActive: false, out _));
        }

        [Fact]
        public void ImmediatePinkPassPreventsConcurrentBackgroundPass()
        {
            var scheduler = new PinkRescanScheduler();
            scheduler.ScheduleKill(Start, Delay);

            Assert.False(scheduler.TryBegin(Start.Add(Delay), modeEnabled: true,
                immediatePinkPassActive: true, out _));
            Assert.True(scheduler.Snapshot.Pending);
            Assert.True(scheduler.TryBegin(Start.Add(Delay).AddMilliseconds(10), true, false, out _));
        }

        [Fact]
        public void SuccessfulPickupSchedulesDrainFollowUp()
        {
            var scheduler = new PinkRescanScheduler();
            scheduler.ScheduleKill(Start, Delay);
            DateTime beganAt = Start.Add(Delay);
            Assert.True(scheduler.TryBegin(beganAt, true, false, out PinkRescanTicket ticket));
            DateTime completedAt = beganAt.AddMilliseconds(175);

            scheduler.Complete(ticket, completedAt, PinkRescanPassOutcome.ItemCollected, FollowUp, Retry);

            Assert.True(scheduler.Snapshot.Pending);
            Assert.Equal(completedAt.Add(FollowUp), scheduler.Snapshot.DueAtUtc);
        }

        [Fact]
        public void CaptureFailureRetriesInsteadOfCompletingAsEmpty()
        {
            var scheduler = new PinkRescanScheduler();
            scheduler.ScheduleKill(Start, Delay);
            DateTime beganAt = Start.Add(Delay);
            Assert.True(scheduler.TryBegin(beganAt, true, false, out PinkRescanTicket ticket));
            DateTime failedAt = beganAt.AddMilliseconds(200);

            scheduler.Complete(ticket, failedAt, PinkRescanPassOutcome.CaptureFailed, FollowUp, Retry);

            Assert.True(scheduler.Snapshot.Pending);
            Assert.Equal(failedAt.Add(Retry), scheduler.Snapshot.DueAtUtc);
        }

        [Fact]
        public void StopCancellationAndCityEntryClearPendingWork()
        {
            var scheduler = new PinkRescanScheduler();
            scheduler.ScheduleKill(Start, Delay);
            scheduler.Cancel();

            Assert.False(scheduler.Snapshot.Pending);
            Assert.Equal(DateTime.MinValue, scheduler.Snapshot.DueAtUtc);
        }

        [Fact]
        public void CompletionFromCancelledEpochCannotRearmWork()
        {
            var scheduler = new PinkRescanScheduler();
            scheduler.ScheduleKill(Start, Delay);
            Assert.True(scheduler.TryBegin(Start.Add(Delay), true, false, out PinkRescanTicket ticket));
            scheduler.Cancel();
            scheduler.Complete(ticket, Start.AddSeconds(3), PinkRescanPassOutcome.ItemCollected, FollowUp, Retry);

            Assert.False(scheduler.Snapshot.Pending);
            Assert.False(scheduler.Snapshot.Active);
        }
    }
}
