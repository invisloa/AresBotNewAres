using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;
using DriverScanTester.Services;
using DriverScanTester.ViewModels;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// DoT opening attack (EXP loop, Sorcerer/Archer): skill 4 is activated a configured
    /// number of times per NEWLY acquired monster, then the existing skill-3 attack takes
    /// over. These tests use deterministic clocks and virtual input seams: no real keyboard
    /// input, camera access or screen capture is performed.
    /// </summary>
    public sealed class DotOpeningTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const int MonsterA = 700;
        private const int MonsterB = 701;
        private const int PlayerTargetId = 9_000_000;

        // ─────────────────────────── profile model / persistence ───────────────────────────

        [Fact]
        public void LegacyProfile_HasNoDotSettings_AndReadsDisabledWithoutCreatingEntries()
        {
            var profile = JsonSerializer.Deserialize<BotProfile>("""{"Name":"Legacy"}""")!;

            Assert.Empty(profile.DotOpeningByClass);

            foreach (PlayerClass playerClass in Enum.GetValues<PlayerClass>())
            {
                DotOpeningSettings settings = profile.GetDotOpeningSettings(playerClass);
                Assert.False(settings.Enabled);
                Assert.Equal(DotOpeningSettings.DefaultSkill4AttackCount, settings.Skill4AttackCount);
                Assert.Equal(DotOpeningSettings.DefaultAttackIntervalMs, settings.AttackIntervalMs);
            }

            // Reading must not create or mutate entries.
            Assert.Empty(profile.DotOpeningByClass);
        }

        [Fact]
        public void MageAndArcherSettings_AreIndependent_AndSurviveJsonRoundTrip()
        {
            var profile = new BotProfile();
            DotOpeningSettings mage = profile.GetOrCreateDotOpeningSettings(PlayerClass.Mage);
            mage.Enabled = true;
            mage.Skill4AttackCount = 2;
            mage.AttackIntervalMs = 650;

            DotOpeningSettings archer = profile.GetOrCreateDotOpeningSettings(PlayerClass.Archer);
            archer.Enabled = true;
            archer.Skill4AttackCount = 3;
            archer.AttackIntervalMs = 1450;

            profile.DotOpeningByClass[PlayerClass.Knight] = new DotOpeningSettings { Enabled = true };

            var reloaded = JsonSerializer.Deserialize<BotProfile>(JsonSerializer.Serialize(profile))!;

            DotOpeningSettings reloadedMage = reloaded.GetDotOpeningSettings(PlayerClass.Mage);
            Assert.True(reloadedMage.Enabled);
            Assert.Equal(2, reloadedMage.Skill4AttackCount);
            Assert.Equal(650, reloadedMage.AttackIntervalMs);

            DotOpeningSettings reloadedArcher = reloaded.GetDotOpeningSettings(PlayerClass.Archer);
            Assert.True(reloadedArcher.Enabled);
            Assert.Equal(3, reloadedArcher.Skill4AttackCount);
            Assert.Equal(1450, reloadedArcher.AttackIntervalMs);

            // Unsupported classes never resolve a stored entry, even if one exists.
            Assert.False(reloaded.GetDotOpeningSettings(PlayerClass.Knight).Enabled);
            Assert.False(reloaded.GetDotOpeningSettings(PlayerClass.Spear).Enabled);
            Assert.False(reloaded.GetDotOpeningSettings(PlayerClass.Unknown).Enabled);
        }

        [Fact]
        public void Validation_RejectsInvalidCountsIntervalsAndUnsupportedClasses()
        {
            var profile = new BotProfile();
            DotOpeningSettings mage = profile.GetOrCreateDotOpeningSettings(PlayerClass.Mage);
            mage.Enabled = true;
            mage.Skill4AttackCount = 4;
            mage.AttackIntervalMs = 499;

            DotOpeningSettings archer = profile.GetOrCreateDotOpeningSettings(PlayerClass.Archer);
            archer.Skill4AttackCount = 1;
            archer.AttackIntervalMs = 3001;

            profile.DotOpeningByClass[PlayerClass.Knight] = new DotOpeningSettings();
            profile.DotOpeningByClass[PlayerClass.Unknown] = new DotOpeningSettings();
            profile.DotOpeningByClass[PlayerClass.Spear] = null!;

            var loader = new BotProfileLoader(_ => { });
            var errors = loader.ValidateProfile(profile);

            Assert.Contains(errors, e => e.Contains("Mage DoT opening settings: opening attacks"));
            Assert.Contains(errors, e => e.Contains("Mage DoT opening settings: attack interval"));
            Assert.Contains(errors, e => e.Contains("Archer DoT opening settings: opening attacks"));
            Assert.Contains(errors, e => e.Contains("Archer DoT opening settings: attack interval"));
            Assert.Contains(errors, e => e.Contains("DoT opening: unsupported class Knight"));
            Assert.Contains(errors, e => e.Contains("DoT opening: unsupported class Unknown"));
            Assert.Contains(errors, e => e.Contains("DoT opening: unsupported class Spear"));
            Assert.Contains(errors, e => e.Contains("Spear DoT opening settings are missing"));
        }

        [Fact]
        public void Validation_AcceptsValidSettings()
        {
            var profile = new BotProfile();
            DotOpeningSettings mage = profile.GetOrCreateDotOpeningSettings(PlayerClass.Mage);
            mage.Enabled = true;
            mage.Skill4AttackCount = 2;
            mage.AttackIntervalMs = 500;

            DotOpeningSettings archer = profile.GetOrCreateDotOpeningSettings(PlayerClass.Archer);
            archer.Enabled = false;
            archer.Skill4AttackCount = 3;
            archer.AttackIntervalMs = 3000;

            var loader = new BotProfileLoader(_ => { });
            Assert.DoesNotContain(loader.ValidateProfile(profile), e => e.Contains("DoT opening"));
        }

        [Fact]
        public void Editor_ClassSelectionKeepsIndependentValues_AndRefreshesOnProfileChange()
        {
            var profile = new BotProfile();
            var editor = new ProfileEditorViewModel { CurrentProfile = profile };

            Assert.Equal(PlayerClass.Mage, editor.SelectedDotOpeningClass);
            Assert.False(editor.DotOpeningEnabled);
            Assert.False(editor.DotOpeningInputsEnabled);

            // Editing creates an independent configuration only for the selected class.
            editor.DotOpeningEnabled = true;
            editor.DotOpeningAttackCount = 2;
            editor.DotOpeningAttackIntervalMs = 700;
            Assert.Single(profile.DotOpeningByClass);
            Assert.True(profile.GetDotOpeningSettings(PlayerClass.Mage).Enabled);
            Assert.False(profile.GetDotOpeningSettings(PlayerClass.Archer).Enabled);

            // The repot class selection and settings must stay untouched.
            Assert.Empty(profile.RepotSettingsByClass);
            Assert.Equal(PlayerClass.Unknown, editor.SelectedRepotClass);

            editor.SelectedDotOpeningClass = PlayerClass.Archer;
            Assert.False(editor.DotOpeningEnabled);
            Assert.Equal(DotOpeningSettings.DefaultSkill4AttackCount, editor.DotOpeningAttackCount);

            editor.DotOpeningEnabled = true;
            editor.DotOpeningAttackCount = 3;
            editor.DotOpeningAttackIntervalMs = 1500;

            editor.SelectedDotOpeningClass = PlayerClass.Mage;
            Assert.True(editor.DotOpeningEnabled);
            Assert.Equal(2, editor.DotOpeningAttackCount);
            Assert.Equal(700, editor.DotOpeningAttackIntervalMs);

            // Disabling only turns the inputs off visually; saved values are preserved.
            editor.DotOpeningEnabled = false;
            Assert.False(editor.DotOpeningInputsEnabled);
            Assert.Equal(2, editor.DotOpeningAttackCount);
            Assert.Equal(700, editor.DotOpeningAttackIntervalMs);
            Assert.Equal(2, profile.GetDotOpeningSettings(PlayerClass.Mage).Skill4AttackCount);
            editor.DotOpeningEnabled = true;

            // Loading another profile refreshes the whole section.
            var replacement = new BotProfile();
            DotOpeningSettings replacementArcher = replacement.GetOrCreateDotOpeningSettings(PlayerClass.Archer);
            replacementArcher.Enabled = false;
            replacementArcher.Skill4AttackCount = 2;
            replacementArcher.AttackIntervalMs = 900;

            editor.CurrentProfile = replacement;
            editor.SelectedDotOpeningClass = PlayerClass.Archer;
            Assert.False(editor.DotOpeningEnabled);
            Assert.Equal(2, editor.DotOpeningAttackCount);
            Assert.Equal(900, editor.DotOpeningAttackIntervalMs);

            editor.SelectedDotOpeningClass = PlayerClass.Mage;
            Assert.False(editor.DotOpeningEnabled);
            Assert.Equal(DotOpeningSettings.DefaultSkill4AttackCount, editor.DotOpeningAttackCount);
            Assert.Equal(DotOpeningSettings.DefaultAttackIntervalMs, editor.DotOpeningAttackIntervalMs);
        }

        // ─────────────────────────── controller state machine ───────────────────────────

        [Fact]
        public void Controller_CountsExactlyTheInitiatedActivations_AndNeverRestartsSameTarget()
        {
            var log = new LogSink();
            var controller = new DotOpeningController(log.Add);
            DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            controller.BeginEncounter(MonsterA, PlayerClass.Mage, 3, 1000, now);
            Assert.True(controller.CanActivate(now));
            controller.RegisterActivation(now);
            Assert.Equal(1, controller.CompletedOpeningActivations);

            // A normal combat tick before the interval elapsed is not an activation.
            Assert.False(controller.CanActivate(now.AddMilliseconds(999)));
            Assert.Equal(1, controller.CompletedOpeningActivations);

            now = now.AddMilliseconds(1000);
            Assert.True(controller.CanActivate(now));
            controller.RegisterActivation(now);
            now = now.AddMilliseconds(1000);
            controller.RegisterActivation(now);
            Assert.True(controller.IsOpeningCompleteAwaitingSwitch);
            Assert.False(controller.CanActivate(now));

            // Final interval, then switch.
            now = now.AddMilliseconds(1000);
            Assert.True(controller.CanSwitchToMainAttack(now));
            controller.CompleteOpening(now);
            Assert.True(controller.IsMainAttackActive);

            Assert.Contains(log.Snapshot(), l => l.Contains("[DoT] New target=700, starting opening sequence"));
            Assert.Contains(log.Snapshot(), l => l.Contains("skill 4 activation 3/3"));
            Assert.Contains(log.Snapshot(), l => l.Contains("opening complete; switching to skill 3"));
        }

        [Fact]
        public void Controller_DirectTargetChange_DiscardsOldProgress()
        {
            var log = new LogSink();
            var controller = new DotOpeningController(log.Add);
            DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            controller.BeginEncounter(MonsterA, PlayerClass.Mage, 3, 1000, now);
            controller.RegisterActivation(now);
            controller.RegisterActivation(now.AddMilliseconds(1000));
            Assert.Equal(2, controller.CompletedOpeningActivations);

            controller.BeginEncounter(MonsterB, PlayerClass.Archer, 3, 900, now.AddMilliseconds(1500));
            Assert.Equal(MonsterB, controller.ActiveTargetId);
            Assert.Equal(0, controller.CompletedOpeningActivations);
            Assert.Equal(PlayerClass.Archer, controller.ActiveClass);
            Assert.Contains(log.Snapshot(), l => l.Contains("Target changed 700 -> 701, new opening"));
        }

        // ─────────────────────────── integration: attack sequence ───────────────────────────

        [Fact]
        public async Task MageEnabledWithTwoAttacks_SendsExactlyTwoSkill4ThenSkill3()
        {
            var clock = new TestClock();
            var log = new LogSink();
            var memory = new FakeMemory { ClassId = BotConstants.PlayerClassIds.Mage, Target = MonsterA };
            var rig = CreateRig(memory, log);
            rig.DotOpeningNowProvider = () => clock.Now;
            rig.DotOpeningByClass = MageSettings(enabled: true, count: 2, intervalMs: 500);

            Assert.True(await rig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(1, rig.SkillFourPresses);
            Assert.Equal(1, rig.SkillFourReleases);
            Assert.Equal(0, rig.SkillThreePresses);

            clock.Advance(499);
            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(1, rig.SkillFourPresses);

            clock.Advance(1);
            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(2, rig.SkillFourPresses);
            Assert.Equal(2, rig.SkillFourReleases);
            Assert.Equal(0, rig.SkillThreePresses);
            Assert.True(rig.DotOpeningController.IsOpeningCompleteAwaitingSwitch);

            clock.Advance(500);
            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(2, rig.SkillFourPresses);
            Assert.Equal(1, rig.SkillThreePresses);
            Assert.True(rig.DotOpeningController.IsMainAttackActive);

            // Repeated combat ticks keep ordinary skill-3 combat and never restart skill 4.
            clock.Advance(5000);
            memory.Mana--; // prove attack progress so the existing one-second retry stays idle
            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(2, rig.SkillFourPresses);
            Assert.Equal(1, rig.SkillThreePresses);

            Assert.Contains(log.Snapshot(), l => l.Contains("[DoT] Enabled for Mage, count=2, interval=500ms"));
            Assert.Contains(log.Snapshot(), l => l.Contains("[DoT] Target=700, skill 4 activation 1/2"));
            Assert.Contains(log.Snapshot(), l => l.Contains("[DoT] Target=700, skill 4 activation 2/2"));
            Assert.Contains(log.Snapshot(), l => l.Contains("[DoT] Target=700, opening complete; switching to skill 3"));
        }

        [Fact]
        public async Task MageEnabledWithThreeAttacks_SendsExactlyThreeSkill4ThenSkill3()
        {
            var clock = new TestClock();
            var memory = new FakeMemory { ClassId = BotConstants.PlayerClassIds.Mage, Target = MonsterA };
            var rig = CreateRig(memory, new LogSink());
            rig.DotOpeningNowProvider = () => clock.Now;
            rig.DotOpeningByClass = MageSettings(enabled: true, count: 3, intervalMs: 700);

            Assert.True(await rig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.MoveAndAttack, CancellationToken.None));
            for (int i = 0; i < 2; i++)
            {
                clock.Advance(700);
                Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            }

            Assert.Equal(3, rig.SkillFourPresses);
            Assert.Equal(0, rig.SkillThreePresses);

            clock.Advance(700);
            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(3, rig.SkillFourPresses);
            Assert.Equal(1, rig.SkillThreePresses);
            Assert.True(rig.DotOpeningController.IsMainAttackActive);
        }

        [Fact]
        public async Task ArcherUsesItsOwnIndependentSettings()
        {
            var clock = new TestClock();
            var memory = new FakeMemory { ClassId = BotConstants.PlayerClassIds.Archer, Target = MonsterA };
            var rig = CreateRig(memory, new LogSink());
            rig.DotOpeningNowProvider = () => clock.Now;
            rig.DotOpeningByClass = new Dictionary<PlayerClass, DotOpeningSettings>
            {
                [PlayerClass.Mage] = new() { Enabled = true, Skill4AttackCount = 2, AttackIntervalMs = 500 },
                [PlayerClass.Archer] = new() { Enabled = true, Skill4AttackCount = 3, AttackIntervalMs = 900 }
            };

            Assert.True(await rig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(1, rig.SkillFourPresses);

            clock.Advance(499);
            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(1, rig.SkillFourPresses);

            clock.Advance(401); // 900 ms total
            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(2, rig.SkillFourPresses);

            clock.Advance(900);
            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(3, rig.SkillFourPresses);

            clock.Advance(900);
            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(3, rig.SkillFourPresses);
            Assert.Equal(1, rig.SkillThreePresses);
            Assert.Equal(900, rig.DotOpeningController.AttackIntervalMs);
        }

        [Fact]
        public async Task DisabledOrLegacyConfiguration_UsesOriginalSkill3Only()
        {
            var memory = new FakeMemory { ClassId = BotConstants.PlayerClassIds.Mage, Target = MonsterA };
            var rig = CreateRig(memory, new LogSink());
            rig.DotOpeningByClass = MageSettings(enabled: false, count: 3, intervalMs: 1000);

            Assert.False(await rig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(0, rig.SkillFourPresses);
            Assert.False(rig.DotOpeningController.OwnsAttackSequence);

            // The original Attack branch then holds skill 3.
            Invoke(rig, "HoldSkillThree");
            Assert.Equal(1, rig.SkillThreePresses);
        }

        [Theory]
        [InlineData(BotConstants.PlayerClassIds.Knight)]
        [InlineData(BotConstants.PlayerClassIds.Spear)]
        public async Task KnightAndSpear_NeverPressSkill4(int classId)
        {
            var memory = new FakeMemory { ClassId = classId, Target = MonsterA };
            var rig = CreateRig(memory, new LogSink());
            rig.DotOpeningByClass = MageSettings(enabled: true, count: 3, intervalMs: 1000);

            Assert.False(await rig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(0, rig.SkillFourPresses);
            Assert.False(rig.DotOpeningController.OwnsAttackSequence);
        }

        [Fact]
        public async Task UnknownOrUnreadableClass_NeverPressSkill4()
        {
            var memory = new FakeMemory { ClassId = null, Target = MonsterA };
            var rig = CreateRig(memory, new LogSink());
            rig.DotOpeningByClass = MageSettings(enabled: true, count: 2, intervalMs: 500);

            Assert.False(await rig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(0, rig.SkillFourPresses);
            Assert.False(rig.DotOpeningController.OwnsAttackSequence);
        }

        [Fact]
        public async Task DirectTargetReplacement_StartsFreshOpeningForNewMonster()
        {
            var clock = new TestClock();
            var log = new LogSink();
            var memory = new FakeMemory { ClassId = BotConstants.PlayerClassIds.Mage, Target = MonsterA };
            var rig = CreateRig(memory, log);
            rig.DotOpeningNowProvider = () => clock.Now;
            rig.DotOpeningByClass = MageSettings(enabled: true, count: 3, intervalMs: 600);

            Assert.True(await rig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.MoveAndAttack, CancellationToken.None));
            clock.Advance(600);
            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(2, rig.SkillFourPresses);
            Assert.Equal(2, rig.DotOpeningController.CompletedOpeningActivations);

            // Direct A → B replacement (no empty-target interval).
            memory.Target = MonsterB;
            clock.Advance(600);
            Assert.True(await rig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.MoveAndAttack, CancellationToken.None));

            Assert.Equal(MonsterB, rig.DotOpeningController.ActiveTargetId);
            Assert.Equal(1, rig.DotOpeningController.CompletedOpeningActivations);
            Assert.Equal(3, rig.SkillFourPresses);
            Assert.Contains(log.Snapshot(), l => l.Contains("Target changed 700 -> 701, new opening"));
        }

        [Fact]
        public async Task SameMonsterAcrossRepeatedTicks_NeverRestartsTheOpening()
        {
            var clock = new TestClock();
            var memory = new FakeMemory { ClassId = BotConstants.PlayerClassIds.Mage, Target = MonsterA };
            var rig = CreateRig(memory, new LogSink());
            rig.DotOpeningNowProvider = () => clock.Now;
            rig.DotOpeningByClass = MageSettings(enabled: true, count: 3, intervalMs: 500);

            Assert.True(await rig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(1, rig.SkillFourPresses);

            // Repeated Attack/CombatWait evaluations for the same monster do not restart.
            Assert.True(await rig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(1, rig.SkillFourPresses);
            Assert.Equal(1, rig.DotOpeningController.CompletedOpeningActivations);

            clock.Advance(500);
            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(2, rig.SkillFourPresses);
            Assert.Equal(2, rig.DotOpeningController.CompletedOpeningActivations);
        }

        [Fact]
        public async Task NewTargetAfterKill_StartsNewOpening()
        {
            var clock = new TestClock();
            var log = new LogSink();
            var memory = new FakeMemory { ClassId = BotConstants.PlayerClassIds.Mage, Target = MonsterA };
            var rig = CreateRig(memory, log);
            rig.DotOpeningNowProvider = () => clock.Now;
            rig.DotOpeningByClass = MageSettings(enabled: true, count: 3, intervalMs: 500);

            Assert.True(await rig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(1, rig.SkillFourPresses);

            // The monster dies/loses selection while its opening is incomplete.
            memory.Target = 0;
            clock.Advance(300);
            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.False(rig.DotOpeningController.OwnsAttackSequence);
            Assert.Equal(1, rig.SkillFourPresses);
            Assert.Equal(0, rig.SkillThreePresses);
            Assert.Contains(log.Snapshot(), l => l.Contains("[DoT] Target=700, opening cancelled: target lost"));

            // The next acquired monster gets a completely fresh opening.
            memory.Target = MonsterB;
            clock.Advance(1000);
            Assert.True(await rig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(MonsterB, rig.DotOpeningController.ActiveTargetId);
            Assert.Equal(1, rig.DotOpeningController.CompletedOpeningActivations);
            Assert.Equal(2, rig.SkillFourPresses);
        }

        [Fact]
        public async Task MonsterDiesDuringOpening_CancelsRemainingActivationsAndNeverPressesSkill3()
        {
            var clock = new TestClock();
            var memory = new FakeMemory { ClassId = BotConstants.PlayerClassIds.Mage, Target = MonsterA };
            var rig = CreateRig(memory, new LogSink());
            rig.DotOpeningNowProvider = () => clock.Now;
            rig.DotOpeningByClass = MageSettings(enabled: true, count: 3, intervalMs: 500);

            Assert.True(await rig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(1, rig.SkillFourPresses);

            memory.Target = 0;
            clock.Advance(5000);
            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));

            Assert.Equal(1, rig.SkillFourPresses);
            Assert.Equal(0, rig.SkillThreePresses);
            Assert.False(rig.DotOpeningController.OwnsAttackSequence);
        }

        [Fact]
        public async Task TargetBecomingPlayer_NeverReceivesFurtherAttackInput()
        {
            var clock = new TestClock();
            var memory = new FakeMemory { ClassId = BotConstants.PlayerClassIds.Mage, Target = MonsterA };
            var rig = CreateRig(memory, new LogSink());
            rig.DotOpeningNowProvider = () => clock.Now;
            rig.DotOpeningByClass = MageSettings(enabled: true, count: 3, intervalMs: 500);

            Assert.True(await rig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(1, rig.SkillFourPresses);

            memory.Target = PlayerTargetId;
            clock.Advance(500);
            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));

            Assert.Equal(1, rig.SkillFourPresses);
            Assert.Equal(0, rig.SkillThreePresses);
            Assert.False(rig.DotOpeningController.OwnsAttackSequence);
        }

        [Fact]
        public async Task GroupingInterruptsIncompleteOpening_ResumesWithoutPrematureSkill3()
        {
            var clock = new TestClock();
            var memory = new FakeMemory { ClassId = BotConstants.PlayerClassIds.Mage, Target = MonsterA };
            var rig = CreateRig(memory, new LogSink());
            rig.DotOpeningNowProvider = () => clock.Now;
            rig.DotOpeningByClass = MageSettings(enabled: true, count: 3, intervalMs: 500);

            Assert.True(await rig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(1, rig.SkillFourPresses);

            // Simulate the grouping controller owning movement plus MovementSystem's resume flag.
            SetMobGroupingState(rig, MobGroupingState.GroupingMove);
            Invoke(rig, "StartMobGroupingMove", new MobGroupingDecision(MobGroupingDirective.StartMove));
            clock.Advance(2000);

            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(1, rig.SkillFourPresses);
            Assert.Equal(0, rig.SkillThreePresses);
            Assert.Equal(1, rig.DotOpeningController.CompletedOpeningActivations);

            // Grouping finishes: resume the SAME opening (2/3), never restart it and never
            // let _resumeCombatSkillAfterGrouping press skill 3 prematurely.
            SetMobGroupingState(rig, MobGroupingState.Idle);
            Invoke(rig, "FinishMobGroupingOwnership", true, clock.Now);

            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(2, rig.SkillFourPresses);
            Assert.Equal(0, rig.SkillThreePresses);
            Assert.Equal(2, rig.DotOpeningController.CompletedOpeningActivations);

            clock.Advance(500);
            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(3, rig.SkillFourPresses);
            Assert.Equal(0, rig.SkillThreePresses);

            clock.Advance(500);
            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(3, rig.SkillFourPresses);
            Assert.Equal(1, rig.SkillThreePresses);
        }

        [Fact]
        public async Task GroupingInterceptsInitialAttack_OpenerStillInitializesThroughCombatWait()
        {
            var clock = new TestClock();
            var memory = new FakeMemory { ClassId = BotConstants.PlayerClassIds.Mage, Target = MonsterA };
            var rig = CreateRig(memory, new LogSink());
            rig.DotOpeningNowProvider = () => clock.Now;
            rig.DotOpeningByClass = MageSettings(enabled: true, count: 3, intervalMs: 500);

            // Grouping starts BEFORE the first Attack switch executes: the controller has not
            // been initialized yet, but the grouping resume flag is already armed.
            SetMobGroupingState(rig, MobGroupingState.GroupingMove);
            Invoke(rig, "StartMobGroupingMove", new MobGroupingDecision(MobGroupingDirective.StartMove));

            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(0, rig.SkillFourPresses);
            Assert.Equal(0, rig.SkillThreePresses);
            Assert.False(rig.DotOpeningController.OwnsAttackSequence);

            // Combat resumes through CombatWait: start the opener instead of holding skill 3.
            SetMobGroupingState(rig, MobGroupingState.Idle);
            Invoke(rig, "FinishMobGroupingOwnership", true, clock.Now);

            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(1, rig.SkillFourPresses);
            Assert.Equal(0, rig.SkillThreePresses);
            Assert.Equal(MonsterA, rig.DotOpeningController.ActiveTargetId);
        }

        [Fact]
        public async Task BotStopDuringOpening_StopsAllFurtherSkill4Input()
        {
            var clock = new TestClock();
            var memory = new FakeMemory { ClassId = BotConstants.PlayerClassIds.Mage, Target = MonsterA };
            var rig = CreateRig(memory, new LogSink());
            rig.DotOpeningNowProvider = () => clock.Now;
            rig.DotOpeningByClass = MageSettings(enabled: true, count: 3, intervalMs: 500);

            Assert.True(await rig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(1, rig.SkillFourPresses);

            rig.Stop();
            clock.Advance(2000);

            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(1, rig.SkillFourPresses);
            Assert.Equal(0, rig.SkillThreePresses);
            Assert.False(rig.DotOpeningController.OwnsAttackSequence);
        }

        [Fact]
        public async Task SwitchingToOnlyMoveDuringOpening_StopsSkill4()
        {
            var memory = new FakeMemory { ClassId = BotConstants.PlayerClassIds.Mage, Target = MonsterA };
            var rig = CreateRig(memory, new LogSink());
            rig.DotOpeningByClass = MageSettings(enabled: true, count: 3, intervalMs: 500);

            Assert.True(await rig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(1, rig.SkillFourPresses);

            Assert.False(await rig.TryTickDotOpeningAsync(BotMode.OnlyMove, CancellationToken.None));
            Assert.Equal(1, rig.SkillFourPresses);
            Assert.Equal(0, rig.SkillThreePresses);
            Assert.False(rig.DotOpeningController.OwnsAttackSequence);

            // Returning to the attack mode must not restart the interrupted opening for the
            // SAME monster; ordinary skill 3 continues instead.
            Assert.True(await rig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(1, rig.SkillFourPresses);
            Assert.Equal(1, rig.SkillThreePresses);
        }

        [Fact]
        public async Task UnreadableLiveClassDuringOpening_FallsBackToSkill3_AndNeverRestartsOpener()
        {
            var clock = new TestClock();
            var memory = new FakeMemory { ClassId = BotConstants.PlayerClassIds.Mage, Target = MonsterA };
            var rig = CreateRig(memory, new LogSink());
            rig.DotOpeningNowProvider = () => clock.Now;
            rig.DotOpeningByClass = MageSettings(enabled: true, count: 3, intervalMs: 500);

            Assert.True(await rig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(1, rig.SkillFourPresses);

            // A transient unreadable class BETWEEN activations does not abort the healthy
            // opening, because no skill 4 is authorized on that tick.
            memory.ClassId = null;
            clock.Advance(499);
            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(1, rig.SkillFourPresses);
            Assert.True(rig.DotOpeningController.IsOpeningActive);

            memory.ClassId = BotConstants.PlayerClassIds.Mage;
            clock.Advance(1);
            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(2, rig.SkillFourPresses);

            // At the NEXT activation the live class cannot be established: no skill 4 may be
            // authorized and the rest of this encounter falls back to ordinary skill 3.
            memory.ClassId = null;
            clock.Advance(500);
            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(2, rig.SkillFourPresses);
            Assert.Equal(1, rig.SkillThreePresses);
            Assert.False(rig.DotOpeningController.OwnsAttackSequence);

            // When the class becomes readable again, the SAME monster is not given a second
            // opening - it continues with the existing skill-3 attack.
            memory.ClassId = BotConstants.PlayerClassIds.Mage;
            clock.Advance(1000);
            Assert.True(await rig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(2, rig.SkillFourPresses);
            Assert.Equal(1, rig.SkillThreePresses);
            Assert.False(rig.DotOpeningController.OwnsAttackSequence);
        }

        [Fact]
        public async Task RecoveryInterruption_BlocksRepeatedOpenersForTheSameMonster()
        {
            var clock = new TestClock();
            var log = new LogSink();
            var memory = new FakeMemory { ClassId = BotConstants.PlayerClassIds.Mage, Target = MonsterA };
            var rig = CreateRig(memory, log);
            rig.DotOpeningNowProvider = () => clock.Now;
            rig.DotOpeningByClass = MageSettings(enabled: true, count: 3, intervalMs: 500);

            Assert.True(await rig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(1, rig.SkillFourPresses);

            rig.BlockDotOpeningForRecovery("combat unstuck recovery");
            clock.Advance(5000);

            // The same stuck monster is reselected after recovery: ordinary skill 3, no loop
            // of fresh skill-4 openings.
            Assert.True(await rig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(1, rig.SkillFourPresses);
            Assert.Equal(1, rig.SkillThreePresses);
            Assert.False(rig.DotOpeningController.OwnsAttackSequence);
            Assert.Contains(log.Snapshot(), l => l.Contains("Falling back to skill 3 for Target=700"));

            // A genuinely different monster still receives its own opening.
            memory.Target = MonsterB;
            clock.Advance(1000);
            Assert.True(await rig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(2, rig.SkillFourPresses);
            Assert.Equal(MonsterB, rig.DotOpeningController.ActiveTargetId);
        }

        [Fact]
        public async Task Skill3Retry_IsSuppressedDuringOpening_AndWorksAfterOpening()
        {
            var clock = new TestClock();
            var memory = new FakeMemory { ClassId = BotConstants.PlayerClassIds.Mage, Target = MonsterA, Mana = 1400 };
            var rig = CreateRig(memory, new LogSink());
            rig.DotOpeningNowProvider = () => clock.Now;
            rig.DotOpeningByClass = MageSettings(enabled: true, count: 2, intervalMs: 500);

            Assert.True(await rig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(1, rig.SkillFourPresses);

            Invoke(rig, "CheckAttackKeyRetry", CancellationToken.None);
            Assert.Equal(0, rig.SkillThreePresses);
            Assert.Equal(1, rig.SkillFourPresses);

            clock.Advance(500);
            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            clock.Advance(500);
            Assert.True(await rig.TryTickDotOpeningAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(1, rig.SkillThreePresses);

            // Existing one-second skill-3 retry still applies after the opening.
            AgeAttackRetry(rig);
            Invoke(rig, "CheckAttackKeyRetry", CancellationToken.None);
            Assert.Equal(2, rig.SkillThreePresses);
            Assert.Equal(2, rig.SkillFourPresses);
        }

        [Fact]
        public async Task NonExpRouteAndNonCombatMode_NeverPressSkill4()
        {
            var memory = new FakeMemory { ClassId = BotConstants.PlayerClassIds.Mage, Target = MonsterA };
            var nonExpRig = CreateRig(memory, new LogSink(), expHuntLoop: false);
            nonExpRig.DotOpeningByClass = MageSettings(enabled: true, count: 3, intervalMs: 500);

            Assert.False(await nonExpRig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.MoveAndAttack, CancellationToken.None));
            Assert.Equal(0, nonExpRig.SkillFourPresses);

            var expRig = CreateRig(memory, new LogSink());
            expRig.DotOpeningByClass = MageSettings(enabled: true, count: 3, intervalMs: 500);
            Assert.False(await expRig.TryBeginOrResumeDotOpeningOnAttackAsync(BotMode.OnlyMove, CancellationToken.None));
            Assert.Equal(0, expRig.SkillFourPresses);
        }

        // ─────────────────────────── combat handler target identity ───────────────────────────

        [Fact]
        public void CombatHandler_SameMonster_DoesNotReturnAttackTwice()
        {
            var stub = new CombatMemory { Target = MonsterA };
            var safety = CombatSafetyFixtures.WithPresence(
                () => stub.Target, new StubCombatPlayerPresenceScanner());
            var handler = new CombatHandler(_ => { });
            GameMemoryService memory = stub.Create();

            Assert.Equal(CombatAction.Attack, Evaluate(handler, memory, safety));
            Assert.Equal(CombatAction.CombatWait, Evaluate(handler, memory, safety));
        }

        [Fact]
        public void CombatHandler_DirectMonsterReplacement_ReturnsAttackWhenDotIsCapable()
        {
            var stub = new CombatMemory { Target = MonsterA };
            var safety = CombatSafetyFixtures.WithPresence(
                () => stub.Target, new StubCombatPlayerPresenceScanner());
            var handler = new CombatHandler(_ => { });
            GameMemoryService memory = stub.Create();

            Assert.Equal(CombatAction.Attack, Evaluate(handler, memory, safety, dotOpeningCapable: true));
            Assert.Equal(CombatAction.CombatWait, Evaluate(handler, memory, safety, dotOpeningCapable: true));

            stub.Target = MonsterB;
            Assert.Equal(CombatAction.Attack, Evaluate(handler, memory, safety, dotOpeningCapable: true));
        }

        [Fact]
        public void CombatHandler_DirectMonsterReplacement_KeepsOriginalBehaviorWhenDotIsDisabled()
        {
            var stub = new CombatMemory { Target = MonsterA };
            var safety = CombatSafetyFixtures.WithPresence(
                () => stub.Target, new StubCombatPlayerPresenceScanner());
            var handler = new CombatHandler(_ => { });
            GameMemoryService memory = stub.Create();

            Assert.Equal(CombatAction.Attack, Evaluate(handler, memory, safety, dotOpeningCapable: false));
            stub.Target = MonsterB;
            Assert.Equal(CombatAction.CombatWait, Evaluate(handler, memory, safety, dotOpeningCapable: false));
        }

        [Fact]
        public void CombatHandler_DoTOpeningInterval_DoesNotTriggerFalseDeath()
        {
            var stub = new CombatMemory { Target = MonsterA, Action = 0 };
            var safety = CombatSafetyFixtures.WithPresence(
                () => stub.Target, new StubCombatPlayerPresenceScanner());
            var handler = new CombatHandler(_ => { });
            GameMemoryService memory = stub.Create();

            Assert.Equal(CombatAction.Attack, Evaluate(handler, memory, safety, dotOpeningCapable: true, dotOpeningIntervalMs: 3000));

            // 1.5 s idle would declare a dead monster with the ordinary 1 s timeout, but the
            // configured 3 s opening interval legitimately explains the pause.
            Backdate(handler, "_combatIdleStartTime", TimeSpan.FromMilliseconds(1500));
            Assert.Equal(CombatAction.CombatWait, Evaluate(handler, memory, safety, dotOpeningCapable: true, dotOpeningIntervalMs: 3000));

            // Beyond interval + 250 ms, the target really is treated as dead/stuck.
            Backdate(handler, "_combatIdleStartTime", TimeSpan.FromMilliseconds(2500));
            Assert.Equal(CombatAction.TabAfterKill, Evaluate(handler, memory, safety, dotOpeningCapable: true, dotOpeningIntervalMs: 3000));
        }

        // ─────────────────────────── test rig ───────────────────────────

        private sealed class TestClock
        {
            public DateTime Now { get; private set; } =
                new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            public void Advance(int milliseconds) => Now = Now.AddMilliseconds(milliseconds);
        }

        private sealed class LogSink
        {
            private readonly object _gate = new();
            private readonly List<string> _lines = new();

            public void Add(string message)
            {
                lock (_gate) _lines.Add(message);
            }

            public IReadOnlyList<string> Snapshot()
            {
                lock (_gate) return _lines.ToList();
            }
        }

        private static Dictionary<PlayerClass, DotOpeningSettings> MageSettings(
            bool enabled, int count, int intervalMs) =>
            new()
            {
                [PlayerClass.Mage] = new DotOpeningSettings
                {
                    Enabled = enabled,
                    Skill4AttackCount = count,
                    AttackIntervalMs = intervalMs
                }
            };

        private static DotOpeningRig CreateRig(
            FakeMemory memory,
            LogSink log,
            bool expHuntLoop = true,
            BotMode mode = BotMode.MoveAndAttack)
        {
            var rig = new DotOpeningRig(memory, log.Add, mode, expHuntLoop);
            return rig;
        }

        private sealed class DotOpeningRig : MovementSystem
        {
            private readonly List<string> _events = new();

            public DotOpeningRig(
                FakeMemory memory,
                Action<string> log,
                BotMode mode,
                bool expHuntLoop)
                : base(
                    memory.Create(),
                    log,
                    5000,
                    5000,
                    MovementPrecision.Medium,
                    customPath: new List<Waypoint>
                    {
                        new Waypoint(5000, 5000, MovementPrecision.Medium, mode)
                    },
                    initialMode: mode,
                    loopPath: true,
                    targetSafety: CombatSafetyFixtures.WithPresence(
                        () => memory.Target,
                        new StubCombatPlayerPresenceScanner()))
            {
                ExpHuntLoopMode = expHuntLoop;
            }

            public int SkillFourPresses => _events.Count(e => e == "4-down");
            public int SkillFourReleases => _events.Count(e => e == "4-up");
            public int SkillThreePresses => _events.Count(e => e == "3-down");

            protected override void SendSkillFourKeyEvent(bool keyUp) =>
                _events.Add(keyUp ? "4-up" : "4-down");

            protected override void SendSkillThreeKeyEvent(bool keyUp) =>
                _events.Add(keyUp ? "3-up" : "3-down");

            protected override void SendSyntheticForwardKeyEvent(bool keyUp) { }
            protected override void SendSyntheticBackwardKeyEvent(bool keyUp) { }

            // Tests never touch the desktop: the production implementation still runs the
            // real attack-start marker check before every first skill-4 press.
            protected override Task RunAttackStartPlayerMarkerCheck(CancellationToken token) =>
                Task.CompletedTask;
        }

        /// <summary>
        /// Game memory stub with a live class and selected-target id. An unreadable class
        /// (null) reproduces the "live class cannot be established" fallback.
        /// </summary>
        private sealed class FakeMemory
        {
            private const ulong ModuleBase = 0;
            private const ulong PlayerBase = 0x100000;

            public int? ClassId = BotConstants.PlayerClassIds.Mage;
            public int Target;
            public short Mana = 1400;

            public GameMemoryService Create()
            {
                GameMemoryService.ReadMemoryDelegate read =
                    (uint pid, ulong address, byte[] buffer, out uint bytesRead) =>
                    {
                        bytesRead = 0;

                        if (address == ModuleBase + BotConstants.MemoryOffsets.PlayerPtr)
                            return Fill(buffer, BitConverter.GetBytes(PlayerBase), out bytesRead);

                        byte[]? value = address switch
                        {
                            PlayerBase + BotConstants.MemoryOffsets.TargetSelected =>
                                BitConverter.GetBytes(Target),
                            PlayerBase + BotConstants.MemoryOffsets.CurrentClassPlayer =>
                                ClassId.HasValue ? BitConverter.GetBytes(ClassId.Value) : null,
                            PlayerBase + BotConstants.MemoryOffsets.Mana =>
                                BitConverter.GetBytes(Mana),
                            _ => null
                        };

                        if (value == null)
                            return false;

                        return Fill(buffer, value, out bytesRead);
                    };

                GameMemoryService.WriteMemoryDelegate write =
                    (uint pid, ulong address, byte[] buffer, out uint bytesWritten) =>
                    {
                        bytesWritten = 0;
                        return false;
                    };

                return new GameMemoryService(0, read, write, 0, 8, _ => { });
            }

            private static bool Fill(byte[] buffer, byte[] value, out uint bytesRead)
            {
                if (value.Length > buffer.Length)
                {
                    bytesRead = 0;
                    return false;
                }

                Buffer.BlockCopy(value, 0, buffer, 0, value.Length);
                bytesRead = (uint)value.Length;
                return true;
            }
        }

        /// <summary>Memory stub for the isolated <see cref="CombatHandler"/> tests.</summary>
        private sealed class CombatMemory
        {
            private const ulong ModuleBase = 0;
            private const ulong PlayerBase = 0x100000;

            public int Target;
            public byte Action = 39;

            public GameMemoryService Create()
            {
                GameMemoryService.ReadMemoryDelegate read =
                    (uint pid, ulong address, byte[] buffer, out uint bytesRead) =>
                    {
                        bytesRead = 0;

                        if (address == ModuleBase + BotConstants.MemoryOffsets.PlayerPtr)
                        {
                            byte[] player = BitConverter.GetBytes(PlayerBase);
                            Buffer.BlockCopy(player, 0, buffer, 0, player.Length);
                            bytesRead = (uint)player.Length;
                            return true;
                        }

                        byte[]? value = address switch
                        {
                            PlayerBase + BotConstants.MemoryOffsets.TargetSelected =>
                                BitConverter.GetBytes(Target),
                            PlayerBase + BotConstants.MemoryOffsets.Animation1 =>
                                BitConverter.GetBytes(0),
                            PlayerBase + BotConstants.MemoryOffsets.CurrentAction =>
                                new[] { Action },
                            PlayerBase + BotConstants.MemoryOffsets.Mana =>
                                BitConverter.GetBytes((short)1400),
                            _ => null
                        };

                        if (value == null)
                            return false;

                        Buffer.BlockCopy(value, 0, buffer, 0, value.Length);
                        bytesRead = (uint)value.Length;
                        return true;
                    };

                GameMemoryService.WriteMemoryDelegate write =
                    (uint pid, ulong address, byte[] buffer, out uint bytesWritten) =>
                    {
                        bytesWritten = 0;
                        return false;
                    };

                return new GameMemoryService(0, read, write, 0, 8, _ => { });
            }
        }

        private static CombatAction Evaluate(
            CombatHandler handler,
            GameMemoryService memory,
            CombatTargetSafety safety,
            bool dotOpeningCapable = false,
            int dotOpeningIntervalMs = 0)
            => handler.EvaluateCombatAction(
                memory,
                BotMode.MoveAndAttack,
                isUnstuckActive: false,
                currX: 10f,
                currY: 10f,
                targetSafety: safety,
                dotOpeningCapable: dotOpeningCapable,
                dotOpeningIntervalMs: dotOpeningIntervalMs);

        private static void Backdate(CombatHandler handler, string fieldName, TimeSpan amount)
        {
            FieldInfo field = typeof(CombatHandler).GetField(fieldName, PrivateInstance)
                ?? throw new InvalidOperationException($"Field {fieldName} not found.");
            DateTime current = (DateTime)field.GetValue(handler)!;
            if (current == DateTime.MinValue)
                current = DateTime.Now;
            field.SetValue(handler, current - amount);
        }

        private static void AgeAttackRetry(MovementSystem rig)
        {
            FieldInfo field = typeof(MovementSystem).GetField("_attackRetryStartedAt", PrivateInstance)
                ?? throw new InvalidOperationException("_attackRetryStartedAt not found.");
            field.SetValue(rig, DateTime.UtcNow.AddMilliseconds(-1100));
        }

        private static void SetMobGroupingState(MovementSystem rig, MobGroupingState state)
        {
            FieldInfo controllerField = typeof(MovementSystem)
                .GetField("_mobGroupingController", PrivateInstance)
                ?? throw new InvalidOperationException("_mobGroupingController not found.");
            object controller = controllerField.GetValue(rig)!;

            FieldInfo stateField = controller.GetType().GetField("_state", PrivateInstance)
                ?? throw new InvalidOperationException("MobGroupingController._state not found.");
            stateField.SetValue(controller, (int)state);
        }

        private static object? Invoke(object target, string methodName, params object?[] args)
        {
            for (Type? type = target.GetType(); type != null; type = type.BaseType)
            {
                MethodInfo? method = type.GetMethod(
                    methodName,
                    PrivateInstance | BindingFlags.DeclaredOnly);
                if (method != null)
                    return method.Invoke(target, args);
            }

            throw new InvalidOperationException($"Method {methodName} not found.");
        }
    }
}
