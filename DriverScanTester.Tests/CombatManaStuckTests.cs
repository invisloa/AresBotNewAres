using System;
using System.Reflection;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Regression tests for the combat "attack not connecting" detector.
    ///
    /// Bug this guards (1): the detector used to treat ANY change of the mana value as
    /// attack progress (mana != previous). Natural mana regeneration (a slow +1 MP
    /// every few seconds) therefore reset the 5 s stall window over and over, so a
    /// phantom attack against an unreachable mob never triggered
    /// <see cref="CombatAction.RepositionAndRetry"/> nor the standard unstuck — the
    /// bot stood in combat forever with a mob selected and no mana consumed.
    ///
    /// Correct semantics: only a mana DECREASE proves the skill actually consumed
    /// mana (the attack connected). Mana increases (regen / potions) must NOT reset
    /// the stall timer.
    ///
    /// Bug this guards (2): a valid target that still has to be reached consumes no mana
    /// while the character runs toward it. The mana-only window therefore fired a false
    /// <see cref="CombatAction.RepositionAndRetry"/> after five seconds of legitimate
    /// pursuit. The stall now requires BOTH no mana consumption AND no meaningful position
    /// progress (Euclidean displacement of at least
    /// <see cref="BotConstants.Combat.CombatManaStuckMoveProgressUnits"/> units from the last
    /// progress anchor). Sub-threshold steps and oscillation never count as progress.
    /// </summary>
    public sealed class CombatManaStuckTests
    {
        private const ulong PlayerPtrOffset = BotConstants.MemoryOffsets.PlayerPtr;
        private const ulong PlayerBase = 0x100000;
        private const ulong TargetSelectedOffset = 0x60;
        private const ulong CurrentActionOffset = 0x3B0;
        private const ulong Animation1Offset = 0x3BA;
        private const ulong ManaOffset = 0xC58;

        private sealed class MemoryStub
        {
            public short Mana = 1400;
            public int TargetId = 5000; // valid mob (< MaxMobTargetId)
            public byte Action = 39;    // 39 = attacking (non-idle)
            public bool ManaReadFails;

            public GameMemoryService Create(Action<string> log)
            {
                GameMemoryService.ReadMemoryDelegate read =
                    (uint pid, ulong address, byte[] buffer, out uint bytesRead) =>
                    {
                        bytesRead = 0;

                        if (address == PlayerPtrOffset)
                            return Fill(buffer, BitConverter.GetBytes(PlayerBase), out bytesRead);

                        if (address == PlayerBase + TargetSelectedOffset)
                            return Fill(buffer, BitConverter.GetBytes(TargetId), out bytesRead);

                        if (address == PlayerBase + Animation1Offset)
                            return Fill(buffer, BitConverter.GetBytes(0), out bytesRead);

                        if (address == PlayerBase + CurrentActionOffset)
                        {
                            buffer[0] = Action;
                            bytesRead = 1;
                            return true;
                        }

                        if (address == PlayerBase + ManaOffset)
                        {
                            if (ManaReadFails) return false;
                            return Fill(buffer, BitConverter.GetBytes(Mana), out bytesRead);
                        }

                        return false;
                    };

                GameMemoryService.WriteMemoryDelegate write =
                    (uint pid, ulong address, byte[] buffer, out uint bytesWritten) =>
                    {
                        bytesWritten = 0;
                        return false;
                    };

                return new GameMemoryService(0, read, write, 0, 8, log);
            }

            private static bool Fill(byte[] buffer, byte[] data, out uint bytesRead)
            {
                Buffer.BlockCopy(data, 0, buffer, 0, data.Length);
                bytesRead = (uint)data.Length;
                return true;
            }
        }

        private static readonly Action<string> NoLog = _ => { };

        private static FieldInfo Field(string fieldName)
            => typeof(CombatHandler).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Field {fieldName} not found.");

        private static void Backdate(CombatHandler handler, string fieldName, double seconds)
            => Field(fieldName).SetValue(handler, DateTime.Now.AddSeconds(-seconds));

        /// <summary>
        /// Ages BOTH progress clocks of the stall window. A stationary "five seconds passed"
        /// simulation must age the mana clock and the independent movement-progress clock —
        /// otherwise the fresh movement baseline hides the stall.
        /// </summary>
        private static void BackdateProgressClocks(CombatHandler handler, double seconds)
        {
            Backdate(handler, "_lastManaChangeAt", seconds);
            Backdate(handler, "_lastMovementProgressAt", seconds);
        }

        /// <summary>Ages an existing timestamp by the given number of seconds (no-op at MinValue).</summary>
        private static void Age(CombatHandler handler, string fieldName, double seconds)
        {
            FieldInfo field = Field(fieldName);
            if ((DateTime)field.GetValue(handler)! == DateTime.MinValue)
                return;
            field.SetValue(handler, ((DateTime)field.GetValue(handler)!).AddSeconds(-seconds));
        }

        private static (float X, float Y)? MovementAnchor(CombatHandler handler)
            => ((float X, float Y)?)Field("_movementProgressAnchor").GetValue(handler);

        private static CombatAction Evaluate(CombatHandler handler, GameMemoryService memory, float x = 10f, float y = 10f)
            => handler.EvaluateCombatAction(memory, BotMode.MoveAndAttackAndLoot, isUnstuckActive: false, currX: x, currY: y);

        [Fact]
        public void ManaIncrease_DoesNotResetStallTimer_TriggersReposition()
        {
            var stub = new MemoryStub { Mana = 1400 };
            var handler = new CombatHandler(NoLog);
            GameMemoryService memory = stub.Create(NoLog);

            // First tick with a selected mob / attack status baselines the attack state.
            Assert.Equal(CombatAction.Attack, Evaluate(handler, memory));

            // Simulate 6 s of phantom attacking: mana only went UP (regen/potion) and the
            // character did not move at all.
            BackdateProgressClocks(handler, 6.0);
            stub.Mana = 1402;

            CombatAction action = Evaluate(handler, memory);

            Assert.Equal(CombatAction.RepositionAndRetry, action);
        }

        /// <summary>
        /// Scenario 1: a target that takes longer than the stall timeout to reach. The
        /// character keeps making real position progress, mana stays unchanged — no recovery.
        /// </summary>
        [Fact]
        public void PursuitWithRealPositionProgress_NeverTriggersReposition()
        {
            var stub = new MemoryStub { Mana = 1400 };
            var handler = new CombatHandler(NoLog);
            GameMemoryService memory = stub.Create(NoLog);

            Assert.Equal(CombatAction.Attack, Evaluate(handler, memory, 100f, 100f));

            // 00:05 — (104,103): 5 units from the anchor, mana unchanged for 5 s+.
            Backdate(handler, "_lastManaChangeAt", 5.5);
            Assert.Equal(CombatAction.CombatWait, Evaluate(handler, memory, 104f, 103f));

            // 00:09 — (110,107): another 7.2 units, mana still unchanged.
            Backdate(handler, "_lastManaChangeAt", 5.5);
            Assert.Equal(CombatAction.CombatWait, Evaluate(handler, memory, 110f, 107f));

            // 00:12 — (115,111): still pursuing and still no mana spent.
            Backdate(handler, "_lastManaChangeAt", 5.5);
            Assert.Equal(CombatAction.CombatWait, Evaluate(handler, memory, 115f, 111f));
        }

        /// <summary>
        /// Scenario 3: reaching the mob region refreshes the progress window; the recovery may
        /// only fire after a further five seconds without movement or mana consumption.
        /// </summary>
        [Fact]
        public void MovementRefreshesTheWindow_ThenStationaryTimeoutTriggersRecovery()
        {
            var stub = new MemoryStub { Mana = 1400 };
            var handler = new CombatHandler(NoLog);
            GameMemoryService memory = stub.Create(NoLog);

            Assert.Equal(CombatAction.Attack, Evaluate(handler, memory, 100f, 100f));

            // 00:04 — 8 units of real progress resets the movement-progress clock.
            Backdate(handler, "_lastManaChangeAt", 4.0);
            Assert.Equal(CombatAction.CombatWait, Evaluate(handler, memory, 108f, 100f));
            Assert.Equal((108f, 100f), MovementAnchor(handler)!.Value);

            // 00:09 — nothing moved and no mana spent for another 5 s: genuine stall.
            BackdateProgressClocks(handler, 5.2);
            Assert.Equal(CombatAction.RepositionAndRetry, Evaluate(handler, memory, 108f, 100f));
        }

        /// <summary>
        /// Scenario 4: movement smaller than the progress threshold never proves pursuit.
        /// </summary>
        [Fact]
        public void SubThresholdMovement_DoesNotSuppressTheRecovery()
        {
            var stub = new MemoryStub { Mana = 1400 };
            var handler = new CombatHandler(NoLog);
            GameMemoryService memory = stub.Create(NoLog);

            Assert.Equal(CombatAction.Attack, Evaluate(handler, memory, 100f, 100f));

            BackdateProgressClocks(handler, 5.2);
            Assert.Equal(CombatAction.RepositionAndRetry, Evaluate(handler, memory, 101f, 100f));

            // The anchor is untouched by sub-threshold movement.
            Assert.Equal((100f, 100f), MovementAnchor(handler)!.Value);
        }

        /// <summary>
        /// Oscillating around the anchor (never reaching 2 units of displacement) must not keep
        /// postponing the recovery forever.
        /// </summary>
        [Fact]
        public void RepeatedSmallOscillation_NeverCountsAsMovementProgress()
        {
            var stub = new MemoryStub { Mana = 1400 };
            var handler = new CombatHandler(NoLog);
            GameMemoryService memory = stub.Create(NoLog);

            Assert.Equal(CombatAction.Attack, Evaluate(handler, memory, 100f, 100f));

            // 1.6 s per tick, oscillating by 1.5 units around the anchor.
            Age(handler, "_lastManaChangeAt", 1.6);
            Age(handler, "_lastMovementProgressAt", 1.6);
            Assert.Equal(CombatAction.CombatWait, Evaluate(handler, memory, 101.5f, 100f));
            Assert.Equal((100f, 100f), MovementAnchor(handler)!.Value);

            Age(handler, "_lastManaChangeAt", 1.6);
            Age(handler, "_lastMovementProgressAt", 1.6);
            Assert.Equal(CombatAction.CombatWait, Evaluate(handler, memory, 100f, 100f));
            Assert.Equal((100f, 100f), MovementAnchor(handler)!.Value);

            Age(handler, "_lastManaChangeAt", 1.6);
            Age(handler, "_lastMovementProgressAt", 1.6);
            Assert.Equal(CombatAction.CombatWait, Evaluate(handler, memory, 101.5f, 100f));
            Assert.Equal((100f, 100f), MovementAnchor(handler)!.Value);

            // 6.4 s after the last real progress: oscillation did not refresh the window.
            Age(handler, "_lastManaChangeAt", 1.6);
            Age(handler, "_lastMovementProgressAt", 1.6);
            Assert.Equal(CombatAction.RepositionAndRetry, Evaluate(handler, memory, 100f, 100f));
            Assert.Equal((100f, 100f), MovementAnchor(handler)!.Value);
        }

        /// <summary>
        /// A real movement-progress event refreshes the stall window without touching the
        /// mana-consumption clock (<c>_lastManaChangeAt</c> keeps meaning "mana consumed").
        /// </summary>
        [Fact]
        public void MovementProgress_DoesNotOverwriteTheManaConsumptionClock()
        {
            var stub = new MemoryStub { Mana = 1400 };
            var handler = new CombatHandler(NoLog);
            GameMemoryService memory = stub.Create(NoLog);

            Assert.Equal(CombatAction.Attack, Evaluate(handler, memory, 100f, 100f));
            Backdate(handler, "_lastManaChangeAt", 4.0);
            DateTime manaClockBefore = (DateTime)Field("_lastManaChangeAt").GetValue(handler)!;

            Assert.Equal(CombatAction.CombatWait, Evaluate(handler, memory, 110f, 100f));

            Assert.Equal(manaClockBefore, (DateTime)Field("_lastManaChangeAt").GetValue(handler)!);
            Assert.True((DateTime)Field("_lastMovementProgressAt").GetValue(handler)! > manaClockBefore);
        }

        /// <summary>
        /// A completed combat episode drops the movement baseline; the next episode starts a
        /// fresh anchor at the position the new attack begins from.
        /// </summary>
        [Fact]
        public void MovementProgressBaseline_ResetsBetweenCombatEpisodes()
        {
            var stub = new MemoryStub { Mana = 1400 };
            var handler = new CombatHandler(NoLog);
            GameMemoryService memory = stub.Create(NoLog);

            Assert.Equal(CombatAction.Attack, Evaluate(handler, memory, 100f, 100f));
            Assert.Equal(CombatAction.CombatWait, Evaluate(handler, memory, 110f, 100f));
            Assert.Equal((110f, 100f), MovementAnchor(handler)!.Value);

            // Target lost — the episode ends and every combat baseline is dropped.
            stub.TargetId = 0;
            Assert.Equal(CombatAction.TabAfterKill, Evaluate(handler, memory, 110f, 100f));
            Assert.Null(MovementAnchor(handler));
            Assert.Equal(DateTime.MinValue, (DateTime)Field("_lastMovementProgressAt").GetValue(handler)!);

            // A new episode resumes combat from a completely different position.
            stub.TargetId = 5000;
            Assert.Equal(CombatAction.Attack, Evaluate(handler, memory, 300f, 300f));
            Assert.Equal((300f, 300f), MovementAnchor(handler)!.Value);

            // The stale episode's progress cannot hide a stall of the new one.
            BackdateProgressClocks(handler, 5.2);
            Assert.Equal(CombatAction.RepositionAndRetry, Evaluate(handler, memory, 300f, 300f));
        }

        /// <summary>
        /// A temporary external movement suspension (MobGrouping walk) removes the paused time
        /// from the movement-progress clock too, so the deliberate walk cannot fabricate a stall.
        /// </summary>
        [Fact]
        public void ExternalMovementSuspension_ShiftsTheMovementProgressClock()
        {
            var stub = new MemoryStub { Mana = 1400 };
            var handler = new CombatHandler(NoLog);
            GameMemoryService memory = stub.Create(NoLog);

            Assert.Equal(CombatAction.Attack, Evaluate(handler, memory, 100f, 100f));
            Assert.Equal(CombatAction.CombatWait, Evaluate(handler, memory, 110f, 100f));
            DateTime progressBefore = (DateTime)Field("_lastMovementProgressAt").GetValue(handler)!;

            handler.SuspendForExternalMovement();
            Field("_externalSuspensionStartedAt").SetValue(handler, DateTime.Now.AddSeconds(-8.0));
            handler.ResumeAfterExternalMovement();

            DateTime progressAfter = (DateTime)Field("_lastMovementProgressAt").GetValue(handler)!;
            Assert.InRange((progressAfter - progressBefore).TotalMilliseconds, 7500, 8500);
            Assert.Equal((110f, 100f), MovementAnchor(handler)!.Value);

            // The shifted clock is not stale yet, so the same position stays in combat.
            Backdate(handler, "_lastManaChangeAt", 5.2);
            Assert.Equal(CombatAction.CombatWait, Evaluate(handler, memory, 110f, 100f));

            // Once both clocks really expire without progress, the recovery fires.
            BackdateProgressClocks(handler, 5.2);
            Assert.Equal(CombatAction.RepositionAndRetry, Evaluate(handler, memory, 110f, 100f));
        }

        [Fact]
        public void ManaDecrease_CountsAsAttackProgress_NoReposition()
        {
            var stub = new MemoryStub { Mana = 1400 };
            var handler = new CombatHandler(NoLog);
            GameMemoryService memory = stub.Create(NoLog);

            Assert.Equal(CombatAction.Attack, Evaluate(handler, memory));
            Assert.Equal(CombatAction.CombatWait, Evaluate(handler, memory));

            // 6 s later the skill finally consumes mana — attack IS connecting.
            BackdateProgressClocks(handler, 6.0);
            stub.Mana = 1380;

            Assert.Equal(CombatAction.CombatWait, Evaluate(handler, memory));

            // The mana consumption also re-arms the stall window for the next five seconds.
            BackdateProgressClocks(handler, 2.0);
            Assert.Equal(CombatAction.CombatWait, Evaluate(handler, memory));
        }

        [Fact]
        public void ManaStable_TriggersReposition()
        {
            var stub = new MemoryStub { Mana = 1400 };
            var handler = new CombatHandler(NoLog);
            GameMemoryService memory = stub.Create(NoLog);

            Assert.Equal(CombatAction.Attack, Evaluate(handler, memory));
            Assert.Equal(CombatAction.CombatWait, Evaluate(handler, memory));

            // No mana consumed and no meaningful movement for 5 s+.
            BackdateProgressClocks(handler, 6.0);

            Assert.Equal(CombatAction.RepositionAndRetry, Evaluate(handler, memory));
        }

        [Fact]
        public void UnreadableMana_WithStalePosition_FallsBackToStandardUnstuck()
        {
            var stub = new MemoryStub { Mana = 1400, ManaReadFails = true };
            var handler = new CombatHandler(NoLog);
            GameMemoryService memory = stub.Create(NoLog);

            Assert.Equal(CombatAction.Attack, Evaluate(handler, memory));

            // 11 s of no mana readings and no position change (same currX/currY).
            Backdate(handler, "_lastManaChangeAt", 11.0);
            Backdate(handler, "_lastCombatPosChangeAt", 11.0);

            Assert.Equal(CombatAction.Unstuck, Evaluate(handler, memory));
        }

        /// <summary>
        /// A mob that stays selected but goes action-idle past the idle timeout is dead:
        /// the TAB that follows must be reported as <see cref="CombatAction.TabAfterKill"/>
        /// so MoveAndAttack SOD/SOP mode runs its post-kill pink scan.
        /// </summary>
        [Fact]
        public void IdleTimeout_WhileMobSelected_ReturnsTabAfterKill()
        {
            var stub = new MemoryStub { Mana = 1400, Action = 39 };
            var handler = new CombatHandler(NoLog);
            GameMemoryService memory = stub.Create(NoLog);

            Assert.Equal(CombatAction.Attack, Evaluate(handler, memory));

            // Mob still selected, but the action is idle for longer than the timeout.
            stub.Action = 0;
            Assert.Equal(CombatAction.CombatWait, Evaluate(handler, memory));
            Backdate(handler, "_combatIdleStartTime", 2.0);

            Assert.Equal(CombatAction.TabAfterKill, Evaluate(handler, memory));
        }

        /// <summary>A kill signal is immediate even when the ordinary TAB cooldown has not elapsed.</summary>
        [Fact]
        public void TargetVanishedDuringTabCooldown_StillReportsKillImmediately()
        {
            var stub = new MemoryStub { Mana = 1400, Action = 39 };
            var handler = new CombatHandler(NoLog);
            GameMemoryService memory = stub.Create(NoLog);

            Assert.Equal(CombatAction.Attack, Evaluate(handler, memory));
            typeof(CombatHandler).GetField("_lastMoveModeTabTime", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(handler, DateTime.Now);

            stub.TargetId = 0;

            Assert.Equal(CombatAction.TabAfterKill, Evaluate(handler, memory));
        }

        /// <summary>
        /// When the target vanishes right after being attacked (mob died, target cleared)
        /// the first TAB cycle must carry the kill signal; later cycles are plain TABs.
        /// </summary>
        [Fact]
        public void TargetVanishedAfterAttack_FirstTabIsTabAfterKill_ThenPlainTab()
        {
            var stub = new MemoryStub { Mana = 1400, Action = 39 };
            var handler = new CombatHandler(NoLog);
            GameMemoryService memory = stub.Create(NoLog);

            Assert.Equal(CombatAction.Attack, Evaluate(handler, memory));

            // Target vanished (mob died) — the first TAB reports the kill.
            stub.TargetId = 0;
            Assert.Equal(CombatAction.TabAfterKill, Evaluate(handler, memory));

            // The next TAB cycle is a plain target cycle — no new kill to report.
            Backdate(handler, "_lastMoveModeTabTime", 5.0);
            Assert.Equal(CombatAction.TabTarget, Evaluate(handler, memory));
        }
    }
}
