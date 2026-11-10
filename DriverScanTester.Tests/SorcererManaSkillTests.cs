using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Sorcerer/Mage automatic mana skill (key 6) for the EXP hunting loop.
    ///
    /// The feature is enabled only when ALL of the following hold:
    ///   • the run is the workflow ExpLoop step (<see cref="MovementSystem.ExpHuntLoopMode"/>,
    ///     never inferred from <c>LoopPath</c>),
    ///   • the effective waypoint mode is MoveAndAttack / MoveAndAttackAndLoot,
    ///   • the class is <see cref="PlayerClass.Mage"/>,
    ///   • current mana is below 50% of maximum mana,
    ///   • an existing enemy check finished WITHOUT acquiring a monster (one-shot opportunity
    ///     evaluated on the following tick), no retarget scan is pending, no loot/grouping/
    ///     recovery owns the input, and the 5-second cooldown elapsed.
    /// </summary>
    public sealed class SorcererManaSkillTests
    {
        private const ulong ModuleBase = 0;
        private const ulong PlayerBase = 0x100000;
        private const int MonsterTargetId = 700;

        // ── 1. the happy path ─────────────────────────────────────────────────

        [Fact]
        public async Task Sorcerer_LowMana_NoEnemyAfterCompletedTargetCheck_PressesKey6Once()
        {
            var memory = new Memory { Mana = 40, MaxMana = 100 };
            var rig = new Rig(memory.Create());

            rig.ArmSorcererManaCastOpportunity("TAB target cycle");
            bool consumed = await rig.TryCastSorcererManaSkillAsync(CancellationToken.None);

            Assert.True(consumed);
            Assert.Equal(1, rig.KeyPressCount);
        }

        [Fact]
        public async Task Sorcerer_FinalWaypointStandby_LowMana_PressesKey6Once()
        {
            var memory = new Memory { Mana = 40, MaxMana = 100 };
            var rig = new Rig(memory.Create(), BotMode.MoveAndAttackAndLoot);
            SetPrivateField(rig, "_finalStandbyActive", true);
            SetPrivateField(rig, "_finalStandbyMode", BotMode.MoveAndAttackAndLoot);
            SetPrivateField(rig, "_finalStandbyX", 5000f);
            SetPrivateField(rig, "_finalStandbyY", 5000f);

            rig.ArmSorcererManaCastOpportunity("TAB target cycle in final standby");
            bool consumed = await rig.TryCastSorcererManaSkillAsync(CancellationToken.None);

            Assert.True(consumed);
            Assert.Equal(1, rig.KeyPressCount);
        }

        // ── 2. mana threshold ─────────────────────────────────────────────────

        [Fact]
        public async Task Sorcerer_SufficientMana_DoesNotCast()
        {
            var memory = new Memory { Mana = 60, MaxMana = 100 };
            var rig = new Rig(memory.Create());

            rig.ArmSorcererManaCastOpportunity("TAB target cycle");
            bool consumed = await rig.TryCastSorcererManaSkillAsync(CancellationToken.None);

            Assert.False(consumed);
            Assert.Equal(0, rig.KeyPressCount);
            Assert.False(rig.TryEvaluateSorcererManaCastEligibility(out _));
        }

        [Theory]
        [InlineData(49, 100, true)]   // 49% → cast
        [InlineData(50, 100, false)]  // exactly 50% → no cast
        [InlineData(51, 100, false)]  // 51% → no cast
        [InlineData(99, 200, true)]   // 49.5% → cast
        [InlineData(100, 200, false)] // exactly 50% → no cast
        public void ManaThreshold_IsHalfOfMaxMana(int currentMana, int maxMana, bool expected)
        {
            Assert.Equal(expected, MovementSystem.IsSorcererManaCastEligible(
                expHuntLoopMode: true,
                effectiveMode: BotMode.MoveAndAttack,
                playerClass: PlayerClass.Mage,
                isAlive: true,
                manaReadSucceeded: true,
                currentMana: (short)currentMana,
                maxManaReadSucceeded: true,
                maxMana: maxMana));
        }

        [Fact]
        public void SorcererManaSkillConstants_AreConfiguredAsSpecified()
        {
            Assert.Equal(0.5, BotConstants.SorcererManaSkill.ManaThresholdFraction);
            Assert.Equal(5_000, BotConstants.SorcererManaSkill.CooldownMs);
            Assert.Equal(BotConstants.Rebuff.StopMovementSettleMs, BotConstants.SorcererManaSkill.StopMovementSettleMs);
            Assert.Equal(BotConstants.Rebuff.PostCastSettleMs, BotConstants.SorcererManaSkill.PostCastSettleMs);
            Assert.True(BotConstants.SorcererManaSkill.OpportunityMaxAgeMs > 0);
        }

        // ── 3. enemy detection has priority ───────────────────────────────────

        [Fact]
        public async Task Sorcerer_LowMana_EnemyAcquired_AttacksInsteadOfCasting()
        {
            var memory = new Memory { Mana = 40, MaxMana = 100, TargetId = MonsterTargetId };
            var rig = new Rig(memory.Create());

            rig.ArmSorcererManaCastOpportunity("TAB target cycle");
            bool consumed = await rig.TryCastSorcererManaSkillAsync(CancellationToken.None);

            Assert.False(consumed);
            Assert.Equal(0, rig.KeyPressCount);
        }

        [Fact]
        public void EnemyPresentGate_BlocksTheCast()
        {
            Assert.Equal(
                MovementSystem.SorcererManaCastGate.EnemyPresent,
                MovementSystem.EvaluateSorcererManaCastGate(
                    opportunityArmed: true,
                    opportunityAge: TimeSpan.FromMilliseconds(100),
                    maxOpportunityAgeMs: BotConstants.SorcererManaSkill.OpportunityMaxAgeMs,
                    contextChanged: false,
                    enemyPresent: true,
                    searchPending: false,
                    lootOrGroupingOwnsInput: false,
                    recoveryOwnsInput: false,
                    cooldownElapsed: true));
        }

        [Fact]
        public async Task EnemyAppearsDuringTheSettleWindow_AbortsTheCast()
        {
            // First target read (the gate) reports "no target"; every later read (the recheck
            // after the settle delay) reports an acquired monster.
            var memory = new Memory
            {
                Mana = 40,
                MaxMana = 100,
                TargetId = 0,
                TargetIdAfterReads = 1,
                TargetIdAfterReadsValue = MonsterTargetId
            };
            var rig = new Rig(memory.Create());

            rig.ArmSorcererManaCastOpportunity("TAB target cycle");
            bool consumed = await rig.TryCastSorcererManaSkillAsync(CancellationToken.None);

            Assert.False(consumed);
            Assert.Equal(0, rig.KeyPressCount);
        }

        // ── 4. retarget scans are never interrupted ───────────────────────────

        [Fact]
        public void PendingRetargetSearch_BlocksTheCast()
        {
            Assert.Equal(
                MovementSystem.SorcererManaCastGate.SearchPending,
                MovementSystem.EvaluateSorcererManaCastGate(
                    opportunityArmed: true,
                    opportunityAge: TimeSpan.FromMilliseconds(100),
                    maxOpportunityAgeMs: BotConstants.SorcererManaSkill.OpportunityMaxAgeMs,
                    contextChanged: false,
                    enemyPresent: false,
                    searchPending: true,
                    lootOrGroupingOwnsInput: false,
                    recoveryOwnsInput: false,
                    cooldownElapsed: true));
        }

        [Fact]
        public async Task StartingANewRetargetScan_InvalidatesTheArmedOpportunity()
        {
            var memory = new Memory { Mana = 40, MaxMana = 100 };
            var rig = new Rig(memory.Create());

            rig.ArmSorcererManaCastOpportunity("TAB target cycle");
            rig.StartCombatRetargetSearch();

            bool consumed = await rig.TryCastSorcererManaSkillAsync(CancellationToken.None);

            Assert.False(consumed);
            Assert.Equal(0, rig.KeyPressCount);
        }

        // ── 5. other owners block the cast ────────────────────────────────────

        [Fact]
        public void LootOrMobGroupingOwnership_BlocksTheCast()
        {
            Assert.Equal(
                MovementSystem.SorcererManaCastGate.LootOrGroupingOwnsInput,
                MovementSystem.EvaluateSorcererManaCastGate(
                    opportunityArmed: true,
                    opportunityAge: TimeSpan.FromMilliseconds(100),
                    maxOpportunityAgeMs: BotConstants.SorcererManaSkill.OpportunityMaxAgeMs,
                    contextChanged: false,
                    enemyPresent: false,
                    searchPending: false,
                    lootOrGroupingOwnsInput: true,
                    recoveryOwnsInput: false,
                    cooldownElapsed: true));
        }

        [Fact]
        public void RecoveryOwnership_BlocksTheCast()
        {
            Assert.Equal(
                MovementSystem.SorcererManaCastGate.RecoveryOwnsInput,
                MovementSystem.EvaluateSorcererManaCastGate(
                    opportunityArmed: true,
                    opportunityAge: TimeSpan.FromMilliseconds(100),
                    maxOpportunityAgeMs: BotConstants.SorcererManaSkill.OpportunityMaxAgeMs,
                    contextChanged: false,
                    enemyPresent: false,
                    searchPending: false,
                    lootOrGroupingOwnsInput: false,
                    recoveryOwnsInput: true,
                    cooldownElapsed: true));
        }

        // ── 6. class / mode / workflow gates ───────────────────────────────────

        [Theory]
        [InlineData(PlayerClass.Knight)]
        [InlineData(PlayerClass.Archer)]
        [InlineData(PlayerClass.Spear)]
        [InlineData(PlayerClass.Unknown)]
        public void OtherClasses_NeverCast(PlayerClass playerClass)
        {
            Assert.False(MovementSystem.IsSorcererManaCastEligible(
                expHuntLoopMode: true,
                effectiveMode: BotMode.MoveAndAttack,
                playerClass: playerClass,
                isAlive: true,
                manaReadSucceeded: true,
                currentMana: 1,
                maxManaReadSucceeded: true,
                maxMana: 100));
        }

        [Fact]
        public async Task OnlyMove_NeverCasts()
        {
            var memory = new Memory { Mana = 10, MaxMana = 100 };
            var rig = new Rig(memory.Create(), BotMode.OnlyMove);

            rig.ArmSorcererManaCastOpportunity("TAB target cycle");
            bool consumed = await rig.TryCastSorcererManaSkillAsync(CancellationToken.None);

            Assert.False(consumed);
            Assert.Equal(0, rig.KeyPressCount);
            Assert.False(rig.TryEvaluateSorcererManaCastEligibility(out _));
        }

        [Fact]
        public async Task TravelOrRepotRun_LoopPathAloneNeverEnablesTheSkill()
        {
            // loopPath is true (a looped route) but the run is NOT the ExpLoop step.
            var memory = new Memory { Mana = 10, MaxMana = 100 };
            var rig = new Rig(memory.Create(), BotMode.MoveAndAttack, expHuntLoop: false, loopPath: true);

            rig.ArmSorcererManaCastOpportunity("TAB target cycle");
            bool consumed = await rig.TryCastSorcererManaSkillAsync(CancellationToken.None);

            Assert.False(consumed);
            Assert.Equal(0, rig.KeyPressCount);
            Assert.False(MovementSystem.IsSorcererManaCastEligible(
                expHuntLoopMode: false,
                effectiveMode: BotMode.MoveAndAttack,
                playerClass: PlayerClass.Mage,
                isAlive: true,
                manaReadSucceeded: true,
                currentMana: 1,
                maxManaReadSucceeded: true,
                maxMana: 100));
        }

        // ── 7. unreadable / invalid inputs never cast ─────────────────────────

        [Fact]
        public void UnreadableClass_NeverCasts()
        {
            var memory = new Memory { ClassValue = null, Mana = 10, MaxMana = 100 };
            var rig = new Rig(memory.Create());

            Assert.False(rig.TryEvaluateSorcererManaCastEligibility(out _));
        }

        [Fact]
        public void UnreadableMana_NeverCasts()
        {
            var memory = new Memory { Mana = null, MaxMana = 100 };
            var rig = new Rig(memory.Create());

            Assert.False(rig.TryEvaluateSorcererManaCastEligibility(out _));
        }

        [Fact]
        public void UnreadableMaxMana_NeverCasts()
        {
            var memory = new Memory { Mana = 10, MaxMana = null };
            var rig = new Rig(memory.Create());

            Assert.False(rig.TryEvaluateSorcererManaCastEligibility(out _));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void InvalidMaxMana_NeverCasts(int maxMana)
        {
            Assert.False(MovementSystem.IsSorcererManaCastEligible(
                expHuntLoopMode: true,
                effectiveMode: BotMode.MoveAndAttack,
                playerClass: PlayerClass.Mage,
                isAlive: true,
                manaReadSucceeded: true,
                currentMana: 0,
                maxManaReadSucceeded: true,
                maxMana: maxMana));
        }

        [Fact]
        public async Task DeadPlayer_NeverCasts()
        {
            var memory = new Memory { Hp = 0, Mana = 40, MaxMana = 100 };
            var rig = new Rig(memory.Create());

            Assert.False(rig.TryEvaluateSorcererManaCastEligibility(out _));

            rig.ArmSorcererManaCastOpportunity("TAB target cycle");
            bool consumed = await rig.TryCastSorcererManaSkillAsync(CancellationToken.None);

            Assert.False(consumed);
            Assert.Equal(0, rig.KeyPressCount);
        }

        // ── 8. cooldown and repeated low-mana ticks ───────────────────────────

        [Fact]
        public async Task RepeatedLowManaOpportunities_HonourTheCooldown()
        {
            var memory = new Memory { Mana = 40, MaxMana = 100 };
            var rig = new Rig(memory.Create());

            rig.ArmSorcererManaCastOpportunity("first TAB target cycle");
            Assert.True(await rig.TryCastSorcererManaSkillAsync(CancellationToken.None));
            Assert.Equal(1, rig.KeyPressCount);

            // The very next eligible no-enemy opportunity is inside the 5 s cooldown.
            rig.ArmSorcererManaCastOpportunity("second TAB target cycle");
            Assert.False(await rig.TryCastSorcererManaSkillAsync(CancellationToken.None));
            Assert.Equal(1, rig.KeyPressCount);
        }

        [Fact]
        public void CooldownGate_AllowsTheNextAttemptOnceElapsed()
        {
            Assert.Equal(
                MovementSystem.SorcererManaCastGate.CooldownActive,
                MovementSystem.EvaluateSorcererManaCastGate(
                    opportunityArmed: true,
                    opportunityAge: TimeSpan.FromMilliseconds(100),
                    maxOpportunityAgeMs: BotConstants.SorcererManaSkill.OpportunityMaxAgeMs,
                    contextChanged: false,
                    enemyPresent: false,
                    searchPending: false,
                    lootOrGroupingOwnsInput: false,
                    recoveryOwnsInput: false,
                    cooldownElapsed: false));

            Assert.Equal(
                MovementSystem.SorcererManaCastGate.Ready,
                MovementSystem.EvaluateSorcererManaCastGate(
                    opportunityArmed: true,
                    opportunityAge: TimeSpan.FromMilliseconds(100),
                    maxOpportunityAgeMs: BotConstants.SorcererManaSkill.OpportunityMaxAgeMs,
                    contextChanged: false,
                    enemyPresent: false,
                    searchPending: false,
                    lootOrGroupingOwnsInput: false,
                    recoveryOwnsInput: false,
                    cooldownElapsed: true));
        }

        // ── 9. one-shot opportunity lifecycle ─────────────────────────────────

        [Fact]
        public void ExpiredOrSupersededOpportunity_NeverCasts()
        {
            Assert.Equal(
                MovementSystem.SorcererManaCastGate.Expired,
                MovementSystem.EvaluateSorcererManaCastGate(
                    opportunityArmed: true,
                    opportunityAge: TimeSpan.FromMilliseconds(BotConstants.SorcererManaSkill.OpportunityMaxAgeMs + 1),
                    maxOpportunityAgeMs: BotConstants.SorcererManaSkill.OpportunityMaxAgeMs,
                    contextChanged: false,
                    enemyPresent: false,
                    searchPending: false,
                    lootOrGroupingOwnsInput: false,
                    recoveryOwnsInput: false,
                    cooldownElapsed: true));

            Assert.Equal(
                MovementSystem.SorcererManaCastGate.ContextChanged,
                MovementSystem.EvaluateSorcererManaCastGate(
                    opportunityArmed: true,
                    opportunityAge: TimeSpan.FromMilliseconds(100),
                    maxOpportunityAgeMs: BotConstants.SorcererManaSkill.OpportunityMaxAgeMs,
                    contextChanged: true,
                    enemyPresent: false,
                    searchPending: false,
                    lootOrGroupingOwnsInput: false,
                    recoveryOwnsInput: false,
                    cooldownElapsed: true));

            Assert.Equal(
                MovementSystem.SorcererManaCastGate.NotArmed,
                MovementSystem.EvaluateSorcererManaCastGate(
                    opportunityArmed: false,
                    opportunityAge: TimeSpan.Zero,
                    maxOpportunityAgeMs: BotConstants.SorcererManaSkill.OpportunityMaxAgeMs,
                    contextChanged: false,
                    enemyPresent: false,
                    searchPending: false,
                    lootOrGroupingOwnsInput: false,
                    recoveryOwnsInput: false,
                    cooldownElapsed: true));
        }

        [Fact]
        public async Task ModeChangeBetweenArmingAndEvaluation_InvalidatesTheOpportunity()
        {
            var memory = new Memory { Mana = 40, MaxMana = 100 };
            var rig = new Rig(memory.Create(), BotMode.MoveAndAttackAndLoot);

            rig.ArmSorcererManaCastOpportunity("TAB target cycle");
            // The route moved on to an OnlyMove waypoint before the opportunity was evaluated.
            rig.ReplaceWaypointForTest(new Waypoint(6000, 6000, MovementPrecision.Medium, BotMode.OnlyMove));

            bool consumed = await rig.TryCastSorcererManaSkillAsync(CancellationToken.None);

            Assert.False(consumed);
            Assert.Equal(0, rig.KeyPressCount);
        }

        // ── 10. normal EXP processing resumes after the cast ──────────────────

        [Fact]
        public async Task AfterTheCast_NoKeysStayHeld_AndNormalProcessingResumes()
        {
            var memory = new Memory { Mana = 40, MaxMana = 100 };
            var rig = new Rig(memory.Create());

            rig.ArmSorcererManaCastOpportunity("TAB target cycle");
            Assert.True(await rig.TryCastSorcererManaSkillAsync(CancellationToken.None));

            // The attack key is not held and the movement input was released.
            Assert.False(rig.IsAttackKeyHeld);
            Assert.False(rig.IsRecoveryInputOwnershipActive);

            // The opportunity is consumed: the next tick without a new enemy check does not
            // press 6 again (and no cooldown-less repeat can happen).
            Assert.False(await rig.TryCastSorcererManaSkillAsync(CancellationToken.None));
            Assert.Equal(1, rig.KeyPressCount);

            // Mana recovered above the threshold → the next opportunity is a no-op.
            memory.Mana = 90;
            rig.ArmSorcererManaCastOpportunity("TAB target cycle after mana recovery");
            Assert.False(await rig.TryCastSorcererManaSkillAsync(CancellationToken.None));
            Assert.Equal(1, rig.KeyPressCount);
        }

        // ── test rig ─────────────────────────────────────────────────────────

        private sealed class Rig : MovementSystem
        {
            public Rig(
                GameMemoryService memory,
                BotMode mode = BotMode.MoveAndAttack,
                bool expHuntLoop = true,
                bool loopPath = true)
                : base(memory, _ => { }, 5000, 5000,
                    MovementPrecision.Medium,
                    customPath: new List<Waypoint>
                    {
                        new Waypoint(5000, 5000, MovementPrecision.Medium, mode)
                    },
                    initialMode: mode,
                    loopPath: loopPath,
                    targetSafety: CombatSafetyFixtures.WithPresence(
                        () => 0, new StubCombatPlayerPresenceScanner()))
            {
                ExpHuntLoopMode = expHuntLoop;
            }

            public int KeyPressCount { get; private set; }

            /// <summary>Swaps the queued waypoint so the route context changes.</summary>
            public void ReplaceWaypointForTest(Waypoint waypoint)
            {
                var queue = (Queue<Waypoint>)typeof(MovementSystem)
                    .GetField("_waypoints", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(this)!;
                queue.Clear();
                queue.Enqueue(waypoint);
            }

            protected override void SendSorcererManaSkillKey() => KeyPressCount++;

            protected override void SendSkillThreeKeyEvent(bool keyUp) { }
            protected override void SendSyntheticForwardKeyEvent(bool keyUp) { }
            protected override void SendSyntheticBackwardKeyEvent(bool keyUp) { }
        }

        /// <summary>
        /// Memory stub for the Sorcerer mana skill: player pointer, class, HP/max HP,
        /// mana/max mana and the selected target id, each with an explicit "unreadable"
        /// switch (null) so a failed read can never look like a valid zero.
        /// </summary>
        private sealed class Memory
        {
            public int? ClassValue = BotConstants.PlayerClassIds.Mage;
            public short? Hp = 500;
            public short? Mana = 40;
            public short? MaxHp = 1000;
            public short? MaxMana = 100;
            public int? TargetId = 0;

            /// <summary>Number of target-id reads after which <see cref="TargetIdAfterReadsValue"/> is returned.</summary>
            public int? TargetIdAfterReads;

            public int? TargetIdAfterReadsValue;

            private int _targetReads;

            public GameMemoryService Create()
            {
                GameMemoryService.ReadMemoryDelegate read =
                    (uint pid, ulong address, byte[] buffer, out uint count) =>
                    {
                        byte[]? value = address switch
                        {
                            ModuleBase + BotConstants.MemoryOffsets.PlayerPtr =>
                                BitConverter.GetBytes(PlayerBase),
                            PlayerBase + BotConstants.MemoryOffsets.Hp =>
                                Hp.HasValue ? BitConverter.GetBytes(Hp.Value) : null,
                            PlayerBase + BotConstants.MemoryOffsets.MaxHp =>
                                MaxHp.HasValue ? BitConverter.GetBytes(MaxHp.Value) : null,
                            PlayerBase + BotConstants.MemoryOffsets.Mana =>
                                Mana.HasValue ? BitConverter.GetBytes(Mana.Value) : null,
                            PlayerBase + BotConstants.MemoryOffsets.MaxMp =>
                                MaxMana.HasValue ? BitConverter.GetBytes(MaxMana.Value) : null,
                            PlayerBase + BotConstants.MemoryOffsets.CurrentClassPlayer =>
                                ClassValue.HasValue ? BitConverter.GetBytes(ClassValue.Value) : null,
                            PlayerBase + BotConstants.MemoryOffsets.TargetSelected =>
                                NextTargetValue(),
                            _ => null
                        };

                        count = 0;
                        if (value == null || value.Length < buffer.Length) return false;

                        Buffer.BlockCopy(value, 0, buffer, 0, buffer.Length);
                        count = (uint)buffer.Length;
                        return true;
                    };

                GameMemoryService.WriteMemoryDelegate write =
                    (uint pid, ulong address, byte[] buffer, out uint count) => { count = 0; return false; };

                return new GameMemoryService(0, read, write, 0, 8, _ => { });
            }

            private byte[]? NextTargetValue()
            {
                int reads = ++_targetReads;
                int? value = TargetIdAfterReads.HasValue && reads > TargetIdAfterReads.Value
                    ? TargetIdAfterReadsValue
                    : TargetId;
                return value.HasValue ? BitConverter.GetBytes(value.Value) : null;
            }
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            for (Type? current = target.GetType(); current != null; current = current.BaseType)
            {
                FieldInfo? field = current.GetField(fieldName,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field == null)
                    continue;
                field.SetValue(target, value);
                return;
            }
            Assert.Fail($"Private field '{fieldName}' was not found.");
        }
    }
}
