using System;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class GameMemoryReadWatchdogTests
    {
        [Fact]
        public void TheDefaultTimeoutMatchesTheFifteenSecondCrashRule()
        {
            var watchdog = new GameMemoryReadWatchdog();
            DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            Assert.False(watchdog.Observe(readSucceeded: false, start));
            Assert.False(watchdog.Observe(readSucceeded: false, start.AddMilliseconds(14_999)));
            Assert.True(watchdog.Observe(readSucceeded: false, start.AddMilliseconds(15_000)));
        }

        [Fact]
        public void ReportsOnlyAfterReadFailuresStayContinuousForTheTimeout()
        {
            var watchdog = new GameMemoryReadWatchdog(timeoutMs: 15_000);
            DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            Assert.False(watchdog.Observe(readSucceeded: false, start));
            Assert.False(watchdog.Observe(readSucceeded: false, start.AddMilliseconds(14_999)));
            Assert.True(watchdog.Observe(readSucceeded: false, start.AddMilliseconds(15_000)));
        }

        [Fact]
        public void ASuccessfulReadRestartsTheFailureInterval()
        {
            var watchdog = new GameMemoryReadWatchdog(timeoutMs: 15_000);
            DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            Assert.False(watchdog.Observe(readSucceeded: false, start));
            Assert.False(watchdog.Observe(readSucceeded: true, start.AddSeconds(10)));
            Assert.False(watchdog.Observe(readSucceeded: false, start.AddSeconds(11)));
            Assert.False(watchdog.Observe(readSucceeded: false, start.AddSeconds(25)));
            Assert.True(watchdog.Observe(readSucceeded: false, start.AddSeconds(26)));
        }

        [Fact]
        public void TheReportIsLatchedUntilReset()
        {
            var watchdog = new GameMemoryReadWatchdog(timeoutMs: 1_000);
            DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            Assert.False(watchdog.Observe(readSucceeded: false, start));
            Assert.True(watchdog.Observe(readSucceeded: false, start.AddSeconds(1)));
            Assert.False(watchdog.Observe(readSucceeded: false, start.AddSeconds(30)));

            watchdog.Reset();

            Assert.False(watchdog.Observe(readSucceeded: false, start.AddSeconds(31)));
            Assert.True(watchdog.Observe(readSucceeded: false, start.AddSeconds(32)));
        }
    }
}
