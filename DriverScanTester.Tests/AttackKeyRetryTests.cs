using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class AttackKeyRetryTests
    {
        private sealed class Rig : MovementSystem
        {
            public readonly List<bool> Events = new();
            public Rig(Memory memoryStub) : base(memoryStub.Create(), _ => { }, 5000, 5000,
                MovementPrecision.Medium, customPath: new List<Waypoint> {
                    new Waypoint(5000, 5000, MovementPrecision.Medium, BotMode.MoveAndAttack) },
                initialMode: BotMode.MoveAndAttack, loopPath: true,
                targetSafety: CombatSafetyFixtures.WithPresence(
                    () => memoryStub.Target, new StubCombatPlayerPresenceScanner())) { }
            protected override void SendSkillThreeKeyEvent(bool keyUp) => Events.Add(keyUp);
            protected override void SendSyntheticForwardKeyEvent(bool keyUp) { }
            protected override void SendSyntheticBackwardKeyEvent(bool keyUp) { }
        }

        private sealed class Memory
        {
            public short Mana = 1400;
            public int Target = 5000;
            public bool ManaUnavailable;
            public GameMemoryService Create()
            {
                GameMemoryService.ReadMemoryDelegate read = (uint pid, ulong address, byte[] buffer, out uint count) =>
                {
                    byte[]? value = address switch {
                        0x489BC8 => BitConverter.GetBytes(0x100000UL),
                        0x100060 => BitConverter.GetBytes(Target),
                        0x100C58 => ManaUnavailable ? null : BitConverter.GetBytes(Mana),
                        _ => null
                    };
                    count = 0;
                    if (value == null) return false;
                    Buffer.BlockCopy(value, 0, buffer, 0, value.Length);
                    count = (uint)value.Length;
                    return true;
                };
                GameMemoryService.WriteMemoryDelegate write = (uint pid, ulong address, byte[] buffer, out uint count) => { count = 0; return false; };
                return new GameMemoryService(0, read, write, 0, 8, _ => { });
            }
        }

        private static void Invoke(Rig rig, string name, params object[] args) =>
            typeof(MovementSystem).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(rig, args);
        private static void Age(Rig rig) => typeof(MovementSystem).GetField("_attackRetryStartedAt", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(rig, DateTime.UtcNow.AddMilliseconds(-1100));
        private static void Check(Rig rig) => Invoke(rig, "CheckAttackKeyRetry", CancellationToken.None);

        [Fact]
        public void NoConsumption_RetriesOnceWithRealReleaseAndPress()
        {
            var memory = new Memory(); var rig = new Rig(memory);
            Invoke(rig, "HoldSkillThree"); Age(rig); Check(rig); Check(rig);
            Assert.Equal(new[] { false, true, false }, rig.Events);
            Assert.True(rig.IsAttackKeyHeld);
        }

        [Fact]
        public void UnchangedManaBeforeDeadline_DoesNotRetry()
        {
            var memory = new Memory(); var rig = new Rig(memory);
            Invoke(rig, "HoldSkillThree"); Check(rig);
            Assert.Equal(new[] { false }, rig.Events);
        }

        [Fact]
        public void ConsumptionBeforeDeadline_PreventsRetry()
        {
            var memory = new Memory(); var rig = new Rig(memory);
            Invoke(rig, "HoldSkillThree"); memory.Mana--; Age(rig); Check(rig); Check(rig);
            Assert.Equal(new[] { false }, rig.Events);
        }

        [Fact]
        public void ReleaseAndNewAttack_StartsFreshWindow()
        {
            var memory = new Memory(); var rig = new Rig(memory);
            Invoke(rig, "HoldSkillThree"); rig.ReleaseCombatKeys(); Check(rig);
            Invoke(rig, "HoldSkillThree"); Check(rig); Age(rig); Check(rig);
            Assert.Equal(new[] { false, true, false, true, false }, rig.Events);
        }

        [Fact]
        public void LostOrChangedTarget_CancelsRetry()
        {
            var memory = new Memory(); var rig = new Rig(memory);
            Invoke(rig, "HoldSkillThree"); Age(rig); memory.Target = 0; Check(rig);
            memory.Target = 5000; Check(rig);
            Assert.Equal(new[] { false }, rig.Events);
        }

        [Fact]
        public void UnreadableInitialMana_DoesNotCauseAnUnfoundedRetry()
        {
            var memory = new Memory { ManaUnavailable = true }; var rig = new Rig(memory);
            Invoke(rig, "HoldSkillThree"); Age(rig); Check(rig);
            memory.ManaUnavailable = false; Check(rig);
            Assert.Equal(new[] { false }, rig.Events);
            Age(rig); Check(rig);
            Assert.Equal(new[] { false, true, false }, rig.Events);
        }

        [Fact]
        public void ChangedMob_CancelsTheOldAttemptsRetry()
        {
            var memory = new Memory(); var rig = new Rig(memory);
            Invoke(rig, "HoldSkillThree"); Age(rig); memory.Target = 5001; Check(rig);
            Assert.Equal(new[] { false }, rig.Events);
            rig.ReleaseCombatKeys(); Invoke(rig, "HoldSkillThree"); Age(rig); Check(rig);
            Assert.Equal(new[] { false, true, false, true, false }, rig.Events);
        }

        [Fact]
        public void Cancellation_CancelsRetry()
        {
            var memory = new Memory(); var rig = new Rig(memory);
            Invoke(rig, "HoldSkillThree"); Age(rig);
            Invoke(rig, "CheckAttackKeyRetry", new CancellationToken(true)); Check(rig);
            Assert.Equal(new[] { false }, rig.Events);
            rig.ReleaseCombatKeys(); Assert.Equal(new[] { false, true }, rig.Events);
        }
    }
}
