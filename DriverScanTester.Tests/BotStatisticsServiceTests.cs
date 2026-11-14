using System;
using System.Diagnostics;
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
    }
}
