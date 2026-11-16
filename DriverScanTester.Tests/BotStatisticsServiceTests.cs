using System;
using System.Diagnostics;
using System.Globalization;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// The Bot Control Panel statistics: session work time, completed cycles and the
    /// total number of items sold. Each test uses an isolated instance (no background
    /// sampler) so assertions never race the singleton's one-second sampling timer.
    /// </summary>
    public sealed class BotStatisticsServiceTests
    {
        [Fact]
        public void WorkTimeFormattingUsesHoursMinutesSeconds()
        {
            Assert.Equal("00:00:00", BotStatisticsService.FormatWorkTime(TimeSpan.Zero));
            Assert.Equal("01:02:03", BotStatisticsService.FormatWorkTime(new TimeSpan(1, 2, 3)));
            Assert.Equal("23:59:59", BotStatisticsService.FormatWorkTime(new TimeSpan(23, 59, 59)));
        }

        [Fact]
        public void WorkTimeFormattingAddsDaysForLongRuns()
        {
            Assert.Equal("1d 02:03:04", BotStatisticsService.FormatWorkTime(new TimeSpan(1, 2, 3, 4)));
            Assert.Equal("2d 00:00:00", BotStatisticsService.FormatWorkTime(TimeSpan.FromDays(2)));
        }

        [Fact]
        public void WorkTimeFormattingNeverShowsNegativeTime()
        {
            Assert.Equal("00:00:00", BotStatisticsService.FormatWorkTime(TimeSpan.FromMinutes(-5)));
        }

        [Fact]
        public void ItemsSoldAndCyclesAccumulateAcrossRuns()
        {
            var stats = new BotStatisticsService(samplerEnabled: false);

            Assert.Equal(0, stats.ItemsSold);
            Assert.Equal(0, stats.CompletedCycles);

            Assert.Equal(5, stats.AddItemsSold(5));
            Assert.Equal(7, stats.AddItemsSold(2));
            Assert.Equal(7, stats.AddItemsSold(0));
            Assert.Equal(7, stats.AddItemsSold(-3));
            Assert.Equal(7, stats.ItemsSold);

            Assert.Equal(1, stats.IncrementCompletedCycles());
            Assert.Equal(2, stats.IncrementCompletedCycles());
            Assert.Equal(2, stats.CompletedCycles);
        }

        [Fact]
        public void ResetClearsEverySessionStatistic()
        {
            var stats = new BotStatisticsService(samplerEnabled: false);
            stats.Configure(() => true, () => false);
            long start = Stopwatch.GetTimestamp();
            stats.SetLastSampleTimestampForTests(start);
            stats.SampleForTests(start + Stopwatch.Frequency);
            stats.AddItemsSold(4);
            stats.IncrementCompletedCycles();

            stats.Reset();

            Assert.Equal(TimeSpan.Zero, stats.WorkTime);
            Assert.Equal(0, stats.ItemsSold);
            Assert.Equal(0, stats.CompletedCycles);
        }

        [Fact]
        public void WorkTimeCountsOnlyWhileTheBotRunsAndIsNotPaused()
        {
            var stats = new BotStatisticsService(samplerEnabled: false);
            bool active = true;
            bool paused = false;
            stats.Configure(() => active, () => paused);

            long start = Stopwatch.GetTimestamp();
            stats.SetLastSampleTimestampForTests(start);

            // 2 seconds of active work.
            stats.SampleForTests(start + Stopwatch.Frequency * 2);
            Assert.Equal(2d, stats.WorkTime.TotalSeconds, 2);

            // Paused for 2 seconds: not work time.
            paused = true;
            stats.SampleForTests(start + Stopwatch.Frequency * 4);
            Assert.Equal(2d, stats.WorkTime.TotalSeconds, 2);

            // Idle for 2 seconds: not work time.
            paused = false;
            active = false;
            stats.SampleForTests(start + Stopwatch.Frequency * 6);
            Assert.Equal(2d, stats.WorkTime.TotalSeconds, 2);

            // Running again: counting resumes.
            active = true;
            stats.SampleForTests(start + Stopwatch.Frequency * 7);
            Assert.Equal(3d, stats.WorkTime.TotalSeconds, 2);
        }

        [Fact]
        public void SuspendedMachineDoesNotCountHoursOfWork()
        {
            var stats = new BotStatisticsService(samplerEnabled: false);
            stats.Configure(() => true, () => false);

            long start = Stopwatch.GetTimestamp();
            stats.SetLastSampleTimestampForTests(start);

            // A one-hour gap (sleep/hibernate or a stalled sampler) is clamped.
            stats.SampleForTests(start + Stopwatch.Frequency * 3600);

            Assert.Equal(TimeSpan.FromSeconds(10), stats.WorkTime);
        }

        [Fact]
        public void FaultyActivityPredicateIsTreatedAsIdle()
        {
            var stats = new BotStatisticsService(samplerEnabled: false);
            stats.Configure(() => throw new InvalidOperationException("broken runtime flag"), () => false);

            long start = Stopwatch.GetTimestamp();
            stats.SetLastSampleTimestampForTests(start);
            stats.SampleForTests(start + Stopwatch.Frequency * 2);

            Assert.Equal(TimeSpan.Zero, stats.WorkTime);
        }

        [Fact]
        public void MoneyChangeIsMeasuredAgainstTheProfileStartValueOnly()
        {
            var stats = new BotStatisticsService(samplerEnabled: false);

            // No profile run yet: nothing to show.
            Assert.False(stats.HasMoneyBaseline);
            Assert.Null(stats.MoneyChangeSinceProfileStart);
            Assert.Equal(BotStatisticsService.UnknownMoneyText, stats.MoneyChangeText);

            // Profile start: the current balance is the zero point, not money made.
            stats.SetMoneyBaseline(1_000_000);
            Assert.True(stats.HasMoneyBaseline);
            Assert.Equal(0, stats.MoneyChangeSinceProfileStart);

            // Refreshed only by the sell path: the difference is the profit.
            Assert.Equal(25_500, stats.UpdateMoney(1_025_500));
            Assert.Equal(25_500, stats.MoneyChangeSinceProfileStart);
            Assert.Equal(30_000, stats.UpdateMoney(1_030_000));

            // Spending (e.g. potions bought during repot) shows up as a negative change.
            Assert.Equal(-12_000, stats.UpdateMoney(988_000));
        }

        [Fact]
        public void SetMoneyBaselineRestartsTrackingFromScratch()
        {
            var stats = new BotStatisticsService(samplerEnabled: false);

            stats.SetMoneyBaseline(500);
            Assert.Equal(1_500, stats.UpdateMoney(2_000));

            // Production uses SetMoneyBaseline only for the first profile of a session;
            // this call is what a fresh (not re-anchored) baseline would do.
            stats.SetMoneyBaseline(2_000);
            Assert.Equal(0, stats.MoneyChangeSinceProfileStart);
            Assert.Equal(400, stats.UpdateMoney(2_400));
        }

        [Fact]
        public void RebaseMoneyKeepsTheTotalAcrossAProfileRestart()
        {
            var stats = new BotStatisticsService(samplerEnabled: false);

            stats.SetMoneyBaseline(1_000_000);
            Assert.Equal(3_000_000, stats.UpdateMoney(4_000_000));

            // Crash recovery: the game client restarted and the profile starts again. The
            // balance is the same character's money, so the already earned 3 000 000 must
            // survive the new zero point.
            stats.RebaseMoney(4_000_000);
            Assert.True(stats.HasMoneyBaseline);
            Assert.Equal(3_000_000, stats.MoneyChangeSinceProfileStart);

            // Only the money earned after the recovery is added on top.
            Assert.Equal(3_500_000, stats.UpdateMoney(4_500_000));

            // A second recovery keeps accumulating as well.
            stats.RebaseMoney(4_600_000);
            Assert.Equal(3_500_000, stats.MoneyChangeSinceProfileStart);
            Assert.Equal(3_700_000, stats.UpdateMoney(4_800_000));
        }

        [Fact]
        public void RebaseMoneyWithoutABaselineAdoptsTheReadValue()
        {
            var stats = new BotStatisticsService(samplerEnabled: false);

            stats.RebaseMoney(9_000);
            Assert.True(stats.HasMoneyBaseline);
            Assert.Equal(0, stats.MoneyChangeSinceProfileStart);
            Assert.Equal(100, stats.UpdateMoney(9_100));
        }

        [Fact]
        public void RebaseMoneyWithUnreadableBalanceKeepsTheCurrentZeroPoint()
        {
            var stats = new BotStatisticsService(samplerEnabled: false);

            stats.SetMoneyBaseline(100);
            Assert.Equal(50, stats.UpdateMoney(150));

            // The balance could not be read at the restart: nothing is re-anchored and the
            // total stays exactly where it was.
            stats.RebaseMoney(null);
            Assert.Equal(50, stats.MoneyChangeSinceProfileStart);
            Assert.Equal(80, stats.UpdateMoney(180));
        }

        [Fact]
        public void UnreadableBaselineIsAdoptedOnTheFirstSuccessfulRead()
        {
            var stats = new BotStatisticsService(samplerEnabled: false);

            // The balance could not be read when the profile started.
            stats.SetMoneyBaseline(null);
            Assert.False(stats.HasMoneyBaseline);
            Assert.Equal(BotStatisticsService.UnknownMoneyText, stats.MoneyChangeText);

            // The first value read after the first sell becomes the zero point, so money
            // that was already there is never reported as profit.
            Assert.Equal(0, stats.UpdateMoney(7_000));
            Assert.Equal(250, stats.UpdateMoney(7_250));
        }

        [Fact]
        public void MoneyChangeFormattingUsesAnExplicitSign()
        {
            Assert.Equal("--", BotStatisticsService.FormatMoneyChange(null));
            Assert.Equal("0", BotStatisticsService.FormatMoneyChange(0));
            Assert.Equal("+500", BotStatisticsService.FormatMoneyChange(500));
            Assert.Equal("-500", BotStatisticsService.FormatMoneyChange(-500));

            long large = 1_234_567;
            Assert.Equal("+" + large.ToString("N0", CultureInfo.CurrentCulture),
                BotStatisticsService.FormatMoneyChange(large));
            Assert.Equal((-large).ToString("N0", CultureInfo.CurrentCulture),
                BotStatisticsService.FormatMoneyChange(-large));
        }

        [Fact]
        public void ResetClearsMoneyTrackingToo()
        {
            var stats = new BotStatisticsService(samplerEnabled: false);
            stats.SetMoneyBaseline(100);
            stats.UpdateMoney(150);

            stats.Reset();

            Assert.False(stats.HasMoneyBaseline);
            Assert.Null(stats.MoneyChangeSinceProfileStart);
        }
    }
}
