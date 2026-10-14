using System;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class GameConnectionWatchdogTests
    {
        [Fact]
        public void ReportsOnlyAfterMovementStaysStillForConfiguredTimeout()
        {
            var watchdog = new GameConnectionWatchdog(timeoutMs: 5_000, minimumProgress: 1f);
            DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            Assert.False(watchdog.Observe(true, 10, 20, start));
            Assert.False(watchdog.Observe(true, 10, 20, start.AddMilliseconds(4_999)));
            Assert.True(watchdog.Observe(true, 10, 20, start.AddMilliseconds(5_000)));
            Assert.False(watchdog.Observe(true, 10, 20, start.AddSeconds(10)));
        }

        [Fact]
        public void RealMovementRestartsTheTimeout()
        {
            var watchdog = new GameConnectionWatchdog(timeoutMs: 5_000, minimumProgress: 1f);
            DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            Assert.False(watchdog.Observe(true, 10, 20, start));
            Assert.False(watchdog.Observe(true, 11, 20, start.AddSeconds(4)));
            Assert.False(watchdog.Observe(true, 11, 20, start.AddSeconds(8)));
            Assert.True(watchdog.Observe(true, 11, 20, start.AddSeconds(9)));
        }

        [Fact]
        public void IntentionalIdleAndInvalidPositionsDoNotAccumulateStallTime()
        {
            var watchdog = new GameConnectionWatchdog(timeoutMs: 5_000, minimumProgress: 1f);
            DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            Assert.False(watchdog.Observe(true, 10, 20, start));
            Assert.False(watchdog.Observe(false, 10, 20, start.AddSeconds(20)));
            Assert.False(watchdog.Observe(true, 10, 20, start.AddSeconds(20)));
            Assert.False(watchdog.Observe(true, float.NaN, 20, start.AddSeconds(21)));
            Assert.False(watchdog.Observe(true, 10, 20, start.AddSeconds(21)));
            Assert.False(watchdog.Observe(true, 10, 20, start.AddSeconds(25)));
            Assert.True(watchdog.Observe(true, 10, 20, start.AddSeconds(26)));
        }
    }
}
