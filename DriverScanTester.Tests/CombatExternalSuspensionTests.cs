using System;
using System.Reflection;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Guards the combat-timer suspension contract used while MobGrouping temporarily owns
    /// movement: combat state is preserved and elapsed suspension time is removed from the
    /// timers, so the deliberate no-attack grouping walk can never fabricate a
    /// mana-stuck/position-stuck/idle/target-cycle/speed-potion expiry.
    /// </summary>
    public sealed class CombatExternalSuspensionTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly string[] ShiftedTimestamps =
        {
            "_lastNonIdleActionTime",
            "_lastMoveModeTabTime",
            "_lastAttackSpeedCheck",
            "_combatIdleStartTime",
            "_lastManaChangeAt",
            "_lastCombatPosChangeAt"
        };

        private static FieldInfo Field(string name) =>
            typeof(CombatHandler).GetField(name, PrivateInstance)
            ?? throw new InvalidOperationException($"CombatHandler.{name} was not found.");

        [Fact]
        public void SuspendForExternalMovementPreservesStateAndResumeShiftsTimers()
        {
            var handler = new CombatHandler(_ => { });
            DateTime baseline = DateTime.Now.AddSeconds(-30);
            foreach (string name in ShiftedTimestamps)
                Field(name).SetValue(handler, baseline);
            Field("_wasAttacking").SetValue(handler, true);

            handler.SuspendForExternalMovement();
            Assert.True((bool)Field("_externallySuspended").GetValue(handler)!);

            // Simulate a 1500 ms grouping walk deterministically instead of sleeping.
            Field("_externalSuspensionStartedAt").SetValue(handler, DateTime.Now.AddMilliseconds(-1500));

            handler.ResumeAfterExternalMovement();

            Assert.False((bool)Field("_externallySuspended").GetValue(handler)!);
            // Combat state is preserved (not reset): the in-progress attack survives the pause.
            Assert.True((bool)Field("_wasAttacking").GetValue(handler)!);
            foreach (string name in ShiftedTimestamps)
            {
                DateTime value = (DateTime)Field(name).GetValue(handler)!;
                double shiftedBy = (value - baseline).TotalMilliseconds;
                Assert.InRange(shiftedBy, 1200, 1800);
            }
        }

        [Fact]
        public void RepeatedSuspendAndResumeAreIdempotent()
        {
            var handler = new CombatHandler(_ => { });
            DateTime baseline = DateTime.Now;
            Field("_lastNonIdleActionTime").SetValue(handler, baseline);

            handler.SuspendForExternalMovement();
            handler.SuspendForExternalMovement();
            handler.ResumeAfterExternalMovement();
            handler.ResumeAfterExternalMovement();

            Assert.False((bool)Field("_externallySuspended").GetValue(handler)!);
            DateTime shifted = (DateTime)Field("_lastNonIdleActionTime").GetValue(handler)!;
            Assert.InRange((shifted - baseline).TotalMilliseconds, 0, 500);
        }
    }
}
