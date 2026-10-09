using System;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Scheduling rules for the background SOD/SOP rescan that runs about one second after
    /// a kill while the bot keeps moving and fighting (the rescan never holds movement).
    /// </summary>
    public sealed class LootPinkRescanTests
    {
        private static readonly DateTime Due = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Expire = TimeSpan.FromSeconds(5);

        private static LootSystem.PinkRescanAction Decide(
            bool pinkLootOnly, bool pinkPassActive, DateTime dueAt, DateTime now)
            => LootSystem.DecidePinkRescan(pinkLootOnly, pinkPassActive, dueAt, now, Expire);

        [Fact]
        public void NothingPending_IsNone()
        {
            Assert.Equal(LootSystem.PinkRescanAction.None,
                Decide(true, false, DateTime.MinValue, Due));
        }

        [Fact]
        public void BeforeDue_Waits()
        {
            Assert.Equal(LootSystem.PinkRescanAction.Wait,
                Decide(true, false, Due, Due.AddMilliseconds(-1)));
        }

        [Fact]
        public void AtDue_Runs()
        {
            Assert.Equal(LootSystem.PinkRescanAction.Run,
                Decide(true, false, Due, Due));
        }

        [Fact]
        public void LateButWithinExpiry_StillRuns()
        {
            // A pass that ran long (the post-kill check, a focus gap) still gets its rescan.
            Assert.Equal(LootSystem.PinkRescanAction.Run,
                Decide(true, false, Due, Due.AddSeconds(4)));
        }

        [Fact]
        public void PastExpiry_IsDropped()
        {
            Assert.Equal(LootSystem.PinkRescanAction.Expire,
                Decide(true, false, Due, Due.AddSeconds(5.5)));
        }

        [Fact]
        public void WhilePostKillPassRuns_Waits_EvenWhenDue()
        {
            // The post-kill check owns the loop; the rescan must not run alongside it.
            Assert.Equal(LootSystem.PinkRescanAction.Wait,
                Decide(true, true, Due, Due.AddSeconds(1)));
        }

        [Fact]
        public void OutsidePinkLootOnlyMode_IsDropped()
        {
            Assert.Equal(LootSystem.PinkRescanAction.Expire,
                Decide(false, false, Due, Due));
        }
    }
}
