using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Threading;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// End-to-end attack-input safety regression tests (2026-10-29 incident): every path that
    /// can press or resume attack skill 3 must go through the central target validation, and a
    /// player that appears as the current target must release the key and never be attacked
    /// again by any retry/recovery path.
    ///
    /// Input is recorded in overrides so no real key is injected into user32 (the established
    /// convention of this project's tests). The other-player evidence is a deterministic fixture
    /// (<see cref="StubCombatPlayerPresenceScanner"/>), never a hardcoded id classification.
    /// </summary>
    public sealed class CombatTargetAttackSafetyTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private sealed class Memory
        {
            public int Target = 5000;
            public short Mana = 1400;
            public byte Action = 39;
            public GameMemoryService Create()
            {
                GameMemoryService.ReadMemoryDelegate read = (uint pid, ulong address, byte[] buffer, out uint count) =>
                {
                    byte[]? value = address switch
                    {
                        0x486BC8 => BitConverter.GetBytes(0x100000UL),
                        0x100060 => BitConverter.GetBytes(Target),
                        0x100C58 => BitConverter.GetBytes(Mana),
                        0x1003B0 => new[] { Action },
                        0x1003BA => BitConverter.GetBytes(0),
                        _ => null
                    };
                    count = 0;
                    if (value == null) return false;
                    Buffer.BlockCopy(value, 0, buffer, 0, value.Length);
                    count = (uint)value.Length;
                    return true;
                };
                GameMemoryService.WriteMemoryDelegate write =
                    (uint pid, ulong address, byte[] buffer, out uint count) => { count = 0; return false; };
                return new GameMemoryService(0, read, write, 0, 8, _ => { });
            }
        }

        private sealed class Rig : MovementSystem
        {
            public readonly List<string> Events = new();

            public Rig(GameMemoryService memory, CombatTargetSafety safety, Action<string> log)
                : base(memory, log, 5000, 5000,
                    MovementPrecision.Medium,
                    customPath: new List<Waypoint>
                    {
                        new Waypoint(5000, 5000, MovementPrecision.Medium, BotMode.MoveAndAttack)
                    },
                    initialMode: BotMode.MoveAndAttack,
                    loopPath: true,
                    targetSafety: safety)
            {
            }

            protected override void SendSkillThreeKeyEvent(bool keyUp)
                => Events.Add(keyUp ? "3-up" : "3-down");

            protected override void SendTargetCycleKey()
                => Events.Add("TAB");

            protected override void SendSyntheticForwardKeyEvent(bool keyUp) { }
            protected override void SendSyntheticBackwardKeyEvent(bool keyUp) { }
        }

        private static Rig CreateRig(
            out Memory memory,
            out StubCombatPlayerPresenceScanner scanner,
            out List<string> log,
            Action<string>? logSink = null)
        {
            memory = new Memory();
            scanner = new StubCombatPlayerPresenceScanner();
            Memory captured = memory;
            StubCombatPlayerPresenceScanner capturedScanner = scanner;
            var sink = new List<string>();
            log = sink;
            var safety = new CombatTargetSafety(
                () => captured.Target,
                capturedScanner,
                () => false,
                line => { lock (sink) sink.Add(line); logSink?.Invoke(line); });

            return new Rig(memory.Create(), safety, line => { lock (sink) sink.Add(line); });
        }

        private static object? Invoke(MovementSystem rig, string name, params object?[] args)
            => typeof(MovementSystem).GetMethod(name, PrivateInstance)!.Invoke(rig, args);

        private static void AgeRetry(MovementSystem rig)
            => typeof(MovementSystem).GetField("_attackRetryStartedAt", PrivateInstance)!
                .SetValue(rig, DateTime.UtcNow.AddMilliseconds(-1100));

        private static bool IsHeld(MovementSystem rig) => rig.IsAttackKeyHeld;

        private static void SetField(MovementSystem rig, string name, object value)
            => typeof(MovementSystem).GetField(name, PrivateInstance)!.SetValue(rig, value);

        private static void MarkPlayerTarget(
            Memory memory, StubCombatPlayerPresenceScanner scanner, int targetId = 718)
        {
            memory.Target = targetId;
            scanner.Status = CombatPlayerPresenceStatus.OtherPlayer;
            scanner.Candidate = StubCombatPlayerPresenceScanner.PlayerMarkerAt(new PointF(449f, 321f));
            scanner.Detail = "faction marker Alliance/green distanceFromCharacter=89px";
        }

        // ───────────────────────── normal monster hunting ─────────────────────────

        [Fact]
        public void ConfirmedMonster_PressesAndHoldsAttackSkill()
        {
            var rig = CreateRig(out _, out _, out _);

            Invoke(rig, "HoldSkillThree");

            Assert.Equal(new[] { "3-down" }, rig.Events);
            Assert.True(IsHeld(rig));
            rig.ReleaseCombatKeys();
        }

        [Fact]
        public void PlayerTarget718_DoesNotPressAttackSkill()
        {
            var rig = CreateRig(out Memory memory, out StubCombatPlayerPresenceScanner scanner, out List<string> log);
            MarkPlayerTarget(memory, scanner);

            Invoke(rig, "HoldSkillThree");

            Assert.Empty(rig.Events);
            Assert.False(IsHeld(rig));
            Assert.Contains(log, line => line.Contains("[CombatSafety] AttackBlocked TargetId=718"));
        }

        [Fact]
        public void UnknownTargetEvidence_DoesNotPressAttackSkill()
        {
            var rig = CreateRig(out _, out StubCombatPlayerPresenceScanner scanner, out List<string> log);
            scanner.Status = CombatPlayerPresenceStatus.Unavailable;
            scanner.Detail = "game client frame could not be captured";

            Invoke(rig, "HoldSkillThree");

            Assert.Empty(rig.Events);
            Assert.False(IsHeld(rig));
            Assert.Contains(log, line => line.Contains("Type=Unknown"));
        }

        // ───────────────────── attack retry must never restart ─────────────────────

        [Fact]
        public void OneSecondRetry_CannotRestartAttackWhenTheTargetBecameAPlayer()
        {
            var rig = CreateRig(out Memory memory, out StubCombatPlayerPresenceScanner scanner, out _);

            Invoke(rig, "HoldSkillThree");
            Assert.Equal(new[] { "3-down" }, rig.Events);

            MarkPlayerTarget(memory, scanner);
            AgeRetry(rig);
            Invoke(rig, "CheckAttackKeyRetry", CancellationToken.None);

            Assert.Equal(new[] { "3-down", "3-up" }, rig.Events);
            Assert.False(IsHeld(rig));
        }

        [Fact]
        public void PlayerDetectedDuringAttack_ReleasesHeldKeyAndLogsInterrupted()
        {
            var rig = CreateRig(out Memory memory, out StubCombatPlayerPresenceScanner scanner, out List<string> log);

            Invoke(rig, "HoldSkillThree");
            Assert.True(IsHeld(rig));

            MarkPlayerTarget(memory, scanner);
            CombatTargetValidation validation = rig.TargetSafety.Validate();
            Invoke(rig, "HandleRejectedCombatTarget", validation);

            Assert.Contains("3-up", rig.Events);
            Assert.False(IsHeld(rig));
            Assert.Contains(log, line => line.Contains("[CombatSafety] AttackInterrupted TargetId=718 Reason=OtherPlayer"));
        }

        // ──────────────────── MobGrouping resume / reposition retry ────────────────────

        [Fact]
        public void MobGroupingCombatResume_CannotPressAttackForAPlayerTarget()
        {
            var rig = CreateRig(out Memory memory, out StubCombatPlayerPresenceScanner scanner, out _);
            MarkPlayerTarget(memory, scanner);

            // The exact state grouping sets before its CombatWait branch reacquires the hold.
            SetField(rig, "_resumeCombatSkillAfterGrouping", true);
            Invoke(rig, "HoldSkillThree");

            Assert.Empty(rig.Events);
            Assert.False(IsHeld(rig));
        }

        [Fact]
        public void RepositionAndRetry_DoesNotTabIntoARejectedTarget()
        {
            var rig = CreateRig(out Memory memory, out StubCombatPlayerPresenceScanner scanner, out List<string> log);
            MarkPlayerTarget(memory, scanner);

            bool attackable = (bool)Invoke(rig, "IsRetryTargetStillAttackable")!;

            Assert.False(attackable);
            Assert.DoesNotContain("TAB", rig.Events);
            Assert.Contains(log, line => line.Contains("RepositionAndRetry cancelled — TargetId=718"));
        }

        [Fact]
        public void RepositionAndRetry_StillAllowsAConfirmedMonster()
        {
            var rig = CreateRig(out _, out _, out _);

            bool attackable = (bool)Invoke(rig, "IsRetryTargetStillAttackable")!;

            Assert.True(attackable);
        }

        // ──────────────────────────── rejection loop safety ────────────────────────────

        [Fact]
        public void RejectedTargetTargetCycleIsBounded()
        {
            var rig = CreateRig(out Memory memory, out StubCombatPlayerPresenceScanner scanner, out _);
            MarkPlayerTarget(memory, scanner);

            var results = new List<bool>();
            for (int i = 0; i < 4; i++)
                results.Add((bool)Invoke(rig, "ShouldCycleTargetAwayFromRejectedTarget", 718)!);

            Assert.Equal(
                new[] { true, true, false, false },
                results);
        }

        [Fact]
        public void NormalHuntingContinuesAfterARejectedTarget()
        {
            var rig = CreateRig(out Memory memory, out StubCombatPlayerPresenceScanner scanner, out _);
            MarkPlayerTarget(memory, scanner);
            Invoke(rig, "HoldSkillThree");
            Assert.False(IsHeld(rig));

            memory.Target = 20005;
            scanner.Status = CombatPlayerPresenceStatus.NoOtherPlayer;
            scanner.Candidate = null;
            Invoke(rig, "HoldSkillThree");

            Assert.Equal(new[] { "3-down" }, rig.Events);
            Assert.True(IsHeld(rig));
            rig.ReleaseCombatKeys();
        }

        // ───────────────────────────── combat evaluation ─────────────────────────────

        [Fact]
        public void CombatHandlerReturnsTargetRejectedForAPlayer()
        {
            var memory = new Memory { Target = 718 };
            var scanner = new StubCombatPlayerPresenceScanner
            {
                Status = CombatPlayerPresenceStatus.OtherPlayer,
                Candidate = StubCombatPlayerPresenceScanner.PlayerMarkerAt(new PointF(449f, 321f))
            };
            var safety = CombatSafetyFixtures.WithPresence(() => memory.Target, scanner);
            var handler = new CombatHandler(_ => { });

            CombatAction action = handler.EvaluateCombatAction(
                memory.Create(), BotMode.MoveAndAttackAndLoot, isUnstuckActive: false,
                currX: 10f, currY: 10f, targetSafety: safety);

            Assert.Equal(CombatAction.TargetRejected, action);
        }

        [Fact]
        public void CombatHandlerAttacksAConfirmedMonster()
        {
            var memory = new Memory { Target = 20005 };
            var safety = CombatSafetyFixtures.WithPresence(
                () => memory.Target, new StubCombatPlayerPresenceScanner());
            var handler = new CombatHandler(_ => { });

            CombatAction action = handler.EvaluateCombatAction(
                memory.Create(), BotMode.MoveAndAttackAndLoot, isUnstuckActive: false,
                currX: 10f, currY: 10f, targetSafety: safety);

            Assert.Equal(CombatAction.Attack, action);
        }
    }
}
