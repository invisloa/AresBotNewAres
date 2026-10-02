using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Reflection;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class MobGroupingTests
    {
        private static readonly MobPoint Player = new(100, 60);
        private static readonly MobPoint[] ScatteredPoints =
        {
            new(20, 60), new(180, 60), new(100, 0)
        };
        private static readonly MobPoint[] ClusteredPoints =
        {
            new(95, 60), new(100, 65), new(105, 60)
        };
        /// <summary>Four tightly grouped mobs plus two remote outliers.</summary>
        private static readonly MobPoint[] AttackReadyClusterWithOutliers =
        {
            new(95, 60), new(100, 65), new(105, 60), new(100, 55),
            new(20, 60), new(180, 60)
        };
        /// <summary>Five mobs spread far apart with no compact four-mob subset.</summary>
        private static readonly MobPoint[] SpreadWithoutCluster =
        {
            new(20, 60), new(110, 20), new(180, 60), new(60, 110), new(150, 110)
        };

        private static MobGroupAnalysis Analyze(params MobPoint[] points)
        {
            var markers = points.Select(point => new MobMarker(
                point, BotConstants.MobGrouping.ExpectedMarkerRadiusPx, 1f, MobMarkerSource.CyanCenter));
            return MobGroupAnalyzer.Analyze(markers, Player);
        }

        private static MobGroupingTickInput Input(
            DateTime now,
            MobGroupAnalysis? analysis = null,
            bool sample = false,
            bool enabled = true,
            BotMode mode = BotMode.MoveAndAttack,
            bool combat = true,
            bool paused = false,
            bool stopping = false,
            bool transition = false,
            bool recovery = false,
            bool lootHold = false,
            bool window = true,
            bool positionValid = true,
            float playerX = 0,
            float playerY = 0,
            bool gatherNav = true)
            => new(now, enabled, mode, combat, paused, stopping, transition, recovery,
                lootHold, window, sample, analysis, positionValid, playerX, playerY, gatherNav);

        private static DateTime Utc(int milliseconds = 0) =>
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(milliseconds);

        private static MobGroupingDecision Trigger(MobGroupingController controller, DateTime start)
        {
            MobGroupingDecision decision = default;
            foreach (int offset in new[] { 0, 225, 450, 675, 900, 1200 })
                decision = controller.Tick(Input(start.AddMilliseconds(offset), Analyze(ScatteredPoints), sample: true));
            return decision;
        }

        private static MobGroupingController MakeController(int gatherDurationMs)
            => new(null, new FixedDurationRandom(gatherDurationMs));

        /// <summary>Deterministic gather-duration source for the timing tests.</summary>
        private sealed class FixedDurationRandom : Random
        {
            private readonly int _durationMs;

            public FixedDurationRandom(int durationMs) => _durationMs = durationMs;

            public override int Next(int minValue, int maxValue)
                => Math.Clamp(_durationMs, minValue, Math.Max(minValue, maxValue - 1));
        }

        /// <summary>Sequence random source that also counts how many deadlines were requested.</summary>
        private sealed class SequenceRandom : Random
        {
            private readonly Queue<int> _values;

            public int CallCount { get; private set; }

            public SequenceRandom(params int[] values) => _values = new Queue<int>(values);

            public override int Next(int minValue, int maxValue)
            {
                CallCount++;
                int value = _values.Count > 0 ? _values.Dequeue() : minValue;
                return Math.Clamp(value, minValue, Math.Max(minValue, maxValue - 1));
            }
        }

        [Fact]
        public void Disabled_NeverGroups()
        {
            var controller = new MobGroupingController();
            MobGroupingDecision decision = controller.Tick(Input(
                Utc(), Analyze(ScatteredPoints), sample: true, enabled: false));

            Assert.Equal(MobGroupingDirective.None, decision.Directive);
            Assert.Equal(MobGroupingState.Idle, controller.State);
        }

        [Theory]
        [InlineData(BotMode.OnlyMove)]
        [InlineData(BotMode.MoveAndAttackAndLoot)]
        public void ForbiddenModes_NeverGroup(BotMode mode)
        {
            var controller = new MobGroupingController();
            MobGroupingDecision decision = controller.Tick(Input(
                Utc(), Analyze(ScatteredPoints), sample: true, mode: mode));

            Assert.Equal(MobGroupingDirective.None, decision.Directive);
            Assert.NotEqual(MobGroupingState.GroupingMove, controller.State);
        }

        [Fact]
        public void BelowMinimumMobCount_NeverGroups()
        {
            var controller = new MobGroupingController();
            MobPoint[] points = ScatteredPoints.Take(BotConstants.MobGrouping.MinimumMobs - 1).ToArray();
            for (int i = 0; i < 8; i++)
            {
                MobGroupingDecision decision = controller.Tick(Input(
                    Utc(i * 225), Analyze(points), sample: true));
                Assert.NotEqual(MobGroupingDirective.StartMove, decision.Directive);
            }
        }

        [Fact]
        public void TwoScatteredMobsAfterPersistenceAndCombatMinimum_TriggerGather()
        {
            // MinimumMobs is now 2: two sufficiently scattered mobs can need gathering.
            var controller = MakeController(2000);
            DateTime start = Utc();
            MobGroupingDecision decision = default;
            foreach (int offset in new[] { 0, 225, 450, 675, 900, 1200 })
                decision = controller.Tick(Input(
                    start.AddMilliseconds(offset), Analyze(ScatteredPoints.Take(2).ToArray()), sample: true));

            Assert.Equal(MobGroupingDirective.StartMove, decision.Directive);
            Assert.Equal(1, controller.GroupingsThisEncounter);
        }

        [Fact]
        public void TwoClusteredMobs_NeverGroups()
        {
            var controller = new MobGroupingController();
            for (int i = 0; i < 10; i++)
            {
                MobGroupingDecision decision = controller.Tick(Input(
                    Utc(i * 225), Analyze(new MobPoint(95, 60), new MobPoint(105, 60)), sample: true));
                Assert.NotEqual(MobGroupingDirective.StartMove, decision.Directive);
            }
        }

        [Fact]
        public void AttackReadyClusterWithOutliers_AttacksInsteadOfGathering()
        {
            // Four mobs already form a valid AOE cluster; the two outliers must not keep
            // the gatherer chasing perfect clustering forever.
            var controller = new MobGroupingController();
            for (int i = 0; i < 10; i++)
            {
                MobGroupingDecision decision = controller.Tick(Input(
                    Utc(i * 225), Analyze(AttackReadyClusterWithOutliers), sample: true));
                Assert.NotEqual(MobGroupingDirective.StartMove, decision.Directive);
            }
            Assert.Equal(0, controller.GroupingsThisEncounter);
        }

        [Fact]
        public void GatherStopsEarlyWhenAttackReadyClusterAppears()
        {
            // Deadline is 3000 ms, but a rescan already finds a compact four-mob subset
            // while outliers remain, so the walk must stop and attack the local cluster.
            var controller = MakeController(3000);
            Assert.Equal(MobGroupingDirective.StartMove, Trigger(controller, Utc()).Directive);

            DateTime earlyStop = Utc(1200 + 1000);
            MobGroupingDecision resumed = controller.Tick(Input(
                earlyStop, Analyze(AttackReadyClusterWithOutliers), sample: true, playerX: 10f));

            Assert.Equal(MobGroupingDirective.ResumeCombat, resumed.Directive);
        }

        [Fact]
        public void GatherDoesNotStartWithoutNavigationTarget()
        {
            var controller = new MobGroupingController();
            for (int i = 0; i < 8; i++)
            {
                MobGroupingDecision decision = controller.Tick(Input(
                    Utc(i * 225), Analyze(ScatteredPoints), sample: true, gatherNav: false));
                Assert.NotEqual(MobGroupingDirective.StartMove, decision.Directive);
            }
        }

        [Fact]
        public void GatherEndsCleanlyWhenNavigationTargetIsReached()
        {
            var controller = MakeController(3000);
            Assert.Equal(MobGroupingDirective.StartMove, Trigger(controller, Utc()).Directive);

            MobGroupingDecision resumed = controller.Tick(Input(
                Utc(1400), Analyze(ScatteredPoints), sample: true, playerX: 10f, gatherNav: false));

            Assert.Equal(MobGroupingDirective.ResumeCombat, resumed.Directive);
            Assert.Equal(1, resumed.MoveNumber);
            controller.CompleteResume(Utc(1400));
            Assert.Equal(MobGroupingState.Cooldown, controller.State);
        }

        [Fact]
        public void ThreeTightlyGroupedMobs_NeverGroups()
        {
            var controller = new MobGroupingController();
            for (int i = 0; i < 10; i++)
            {
                MobGroupingDecision decision = controller.Tick(Input(
                    Utc(i * 225), Analyze(ClusteredPoints), sample: true));
                Assert.NotEqual(MobGroupingDirective.StartMove, decision.Directive);
            }
        }

        [Fact]
        public void ScatteredFormationShorterThanPersistence_DoesNotTrigger()
        {
            var controller = new MobGroupingController();
            controller.Tick(Input(Utc(), Analyze(ScatteredPoints), sample: true));
            controller.Tick(Input(Utc(500), Analyze(ScatteredPoints), sample: true));

            Assert.Equal(MobGroupingState.Observing, controller.State);
            Assert.Equal(0, controller.GroupingsThisEncounter);
        }

        [Fact]
        public void OneFailedDetectionFrameGetsGraceButCombatPresenceMinimumStillApplies()
        {
            var controller = new MobGroupingController();
            controller.Tick(Input(Utc(), Analyze(ScatteredPoints), sample: true));
            controller.Tick(Input(Utc(225), Analyze(ScatteredPoints), sample: true));
            controller.Tick(Input(Utc(450), Analyze(ScatteredPoints), sample: true));
            controller.Tick(Input(Utc(675), analysis: null, sample: true));

            MobGroupingDecision beforeCombatMinimum = controller.Tick(Input(
                Utc(900), Analyze(ScatteredPoints), sample: true));
            Assert.Equal(MobGroupingDirective.None, beforeCombatMinimum.Directive);

            MobGroupingDecision afterCombatMinimum = controller.Tick(Input(
                Utc(1200), Analyze(ScatteredPoints), sample: true));
            Assert.Equal(MobGroupingDirective.StartMove, afterCombatMinimum.Directive);
        }

        [Fact]
        public void PersistedScatteredFormationAfterCombatMinimum_Triggers()
        {
            var controller = new MobGroupingController();
            MobGroupingDecision decision = Trigger(controller, Utc());

            Assert.Equal(MobGroupingDirective.StartMove, decision.Directive);
            Assert.Equal(1, decision.MoveNumber);
            Assert.Equal(1, controller.GroupingsThisEncounter);
            Assert.Equal(MobGroupingState.GroupingMove, controller.State);
        }

        [Fact]
        public void GatherStopsEarlyWhenMobsBecomeClustered()
        {
            // Deadline is 3000 ms, but a 1000 ms rescan already reports a compact
            // formation, so the walk must stop immediately and resume combat.
            var controller = MakeController(3000);
            MobGroupingDecision started = Trigger(controller, Utc());
            Assert.Equal(MobGroupingDirective.StartMove, started.Directive);

            DateTime earlyStop = Utc(1200 + 1000);
            MobGroupingDecision resumed = controller.Tick(Input(
                earlyStop, Analyze(ClusteredPoints), sample: true, playerX: 10f));

            Assert.Equal(MobGroupingDirective.ResumeCombat, resumed.Directive);
            Assert.Equal(1, resumed.MoveNumber);
            Assert.Equal(1, started.MoveNumber);

            controller.CompleteResume(earlyStop);
            Assert.Equal(MobGroupingState.Cooldown, controller.State);
            Assert.Equal(MobGroupingDirective.None, controller.Tick(Input(
                earlyStop.AddMilliseconds(BotConstants.MobGrouping.CooldownMs - 1),
                Analyze(ScatteredPoints), sample: true)).Directive);
        }

        [Fact]
        public void GatherDeadlineStopsMovementAndResumesCombat()
        {
            var controller = MakeController(2200);
            Trigger(controller, Utc());
            DateTime moveStart = Utc(1200);
            DateTime deadline = moveStart.AddMilliseconds(2200);

            // The rescans never satisfy the grouping condition; the walk still ends at
            // the selected deadline without waiting for another scan cadence.
            foreach (int offset in new[] { 500, 1000, 1500, 2000 })
            {
                MobGroupingDecision scan = controller.Tick(Input(
                    moveStart.AddMilliseconds(offset), Analyze(ScatteredPoints), sample: true,
                    playerX: offset / 100f));
                Assert.Equal(MobGroupingDirective.None, scan.Directive);
                Assert.Equal(MobGroupingState.GroupingMove, controller.State);
            }

            MobGroupingDecision resumed = controller.Tick(Input(deadline, playerX: 25f));
            Assert.Equal(MobGroupingDirective.ResumeCombat, resumed.Directive);
            Assert.Equal(1, resumed.MoveNumber);
            Assert.Equal(1, controller.MovesStarted);

            controller.CompleteResume(deadline);
            Assert.Equal(MobGroupingState.Cooldown, controller.State);
        }

        [Fact]
        public void GatherUsesOneDeadlineAndNeverStartsASecondMove()
        {
            // The sequence random would report a much shorter duration if the controller
            // re-rolled on a rescan, and CallCount proves the deadline was chosen once.
            var random = new SequenceRandom(3000, 1000, 1000, 1000);
            var controller = new MobGroupingController(null, random);
            Trigger(controller, Utc());
            DateTime moveStart = Utc(1200);

            // Several rescans pass without grouping; the deadline must stay 3000 ms and
            // must NOT be re-randomized or extended by the rescan loop.
            foreach (int offset in new[] { 500, 1000, 1500, 2000, 2500 })
            {
                controller.Tick(Input(moveStart.AddMilliseconds(offset),
                    Analyze(ScatteredPoints), sample: true, playerX: offset / 100f));
            }

            MobGroupingDecision beforeDeadline = controller.Tick(Input(
                moveStart.AddMilliseconds(2999), playerX: 30f));
            Assert.Equal(MobGroupingDirective.None, beforeDeadline.Directive);
            Assert.Equal(MobGroupingState.GroupingMove, controller.State);
            Assert.Equal(1, random.CallCount);

            MobGroupingDecision atDeadline = controller.Tick(Input(
                moveStart.AddMilliseconds(3000), playerX: 30f));
            Assert.Equal(MobGroupingDirective.ResumeCombat, atDeadline.Directive);
            Assert.Equal(1, atDeadline.MoveNumber);
            Assert.Equal(1, controller.MovesStarted);
            Assert.Equal(1, random.CallCount);
        }

        [Fact]
        public void GatherSuccessPredicateRequiresMinimumMobsAndClustering()
        {
            // Single source of truth: enough mobs AND the existing clustering condition.
            Assert.True(MobGroupingController.IsGatheringComplete(Analyze(ClusteredPoints)));
            Assert.False(MobGroupingController.IsGatheringComplete(Analyze(ScatteredPoints)));
            Assert.False(MobGroupingController.IsGatheringComplete(Analyze()));
            Assert.False(MobGroupingController.IsGatheringComplete(
                Analyze(ScatteredPoints.Take(BotConstants.MobGrouping.MinimumMobs - 1).ToArray())));
        }

        [Fact]
        public void CooldownBlocksRetrigger()
        {
            var controller = MakeController(2000);
            Trigger(controller, Utc());
            DateTime moveStart = Utc(1200);
            DateTime deadline = moveStart.AddMilliseconds(2000);

            MobGroupingDecision resumed = controller.Tick(Input(deadline, playerX: 30f));
            Assert.Equal(MobGroupingDirective.ResumeCombat, resumed.Directive);
            controller.CompleteResume(deadline);

            MobGroupingDecision decision = controller.Tick(Input(
                deadline.AddMilliseconds(BotConstants.MobGrouping.CooldownMs - 1),
                Analyze(ScatteredPoints), sample: true, playerX: 30f));

            Assert.Equal(MobGroupingDirective.None, decision.Directive);
            Assert.Equal(MobGroupingState.Cooldown, controller.State);
        }

        [Fact]
        public void EncounterLimitBlocksThirdGroupingUntilStableQuietReset()
        {
            var controller = MakeController(2000);
            DateTime time = Utc();

            for (int grouping = 0; grouping < BotConstants.MobGrouping.MaxGroupingsPerEncounter; grouping++)
            {
                MobGroupingDecision start;
                DateTime moveStart;
                if (grouping == 0)
                {
                    start = Trigger(controller, time);
                    moveStart = time.AddMilliseconds(1200);
                }
                else
                {
                    (start, moveStart) = TriggerAfterCooldown(controller, time);
                }
                Assert.Equal(MobGroupingDirective.StartMove, start.Directive);

                DateTime deadline = moveStart.AddMilliseconds(2000);
                MobGroupingDecision resumed = controller.Tick(Input(deadline, playerX: grouping + 1f));
                Assert.Equal(MobGroupingDirective.ResumeCombat, resumed.Directive);
                controller.CompleteResume(deadline);
                time = deadline.AddMilliseconds(BotConstants.MobGrouping.CooldownMs);
            }

            (MobGroupingDecision blocked, _) = TriggerAfterCooldown(controller, time);
            Assert.Equal(MobGroupingDirective.None, blocked.Directive);
            Assert.Equal(BotConstants.MobGrouping.MaxGroupingsPerEncounter, controller.GroupingsThisEncounter);
        }

        [Fact]
        public void GatherDurationStaysWithinNominalPlusMinusVariation()
        {
            var controller = new MobGroupingController(null, new Random(20260101));
            int min = BotConstants.MobGrouping.GatherMoveNominalDurationMs -
                      BotConstants.MobGrouping.GatherMoveDurationVariationMs;
            int max = BotConstants.MobGrouping.GatherMoveNominalDurationMs +
                      BotConstants.MobGrouping.GatherMoveDurationVariationMs;

            for (int i = 0; i < 500; i++)
                Assert.InRange(controller.ChooseGatherDurationMs(), min, max);
        }

        [Fact]
        public void GatherRescanIsDueEveryConfiguredIntervalAndNeverPastTheDeadline()
        {
            var controller = MakeController(3000);
            Trigger(controller, Utc());
            DateTime moveStart = Utc(1200);

            Assert.False(controller.NeedsGatherRescan(moveStart.AddMilliseconds(499)));
            Assert.True(controller.NeedsGatherRescan(moveStart.AddMilliseconds(500)));

            controller.Tick(Input(moveStart.AddMilliseconds(500), Analyze(ScatteredPoints),
                sample: true, playerX: 5f));
            Assert.False(controller.NeedsGatherRescan(moveStart.AddMilliseconds(999)));
            Assert.True(controller.NeedsGatherRescan(moveStart.AddMilliseconds(1000)));

            // The deadline always wins over the rescan cadence: no rescan is due at or
            // past it, so the deadline stop can never be delayed by a pending scan.
            Assert.False(controller.NeedsGatherRescan(moveStart.AddMilliseconds(3000)));
            Assert.False(controller.NeedsGatherRescan(moveStart.AddMilliseconds(4200)));
        }

        [Fact]
        public void NoStaleRescanOrGatherWorkAfterTheGatherCompletes()
        {
            var controller = MakeController(3000);
            Trigger(controller, Utc());
            DateTime moveStart = Utc(1200);
            DateTime earlyStop = moveStart.AddMilliseconds(1000);

            MobGroupingDecision resumed = controller.Tick(Input(
                earlyStop, Analyze(ClusteredPoints), sample: true, playerX: 10f));
            Assert.Equal(MobGroupingDirective.ResumeCombat, resumed.Directive);
            controller.CompleteResume(earlyStop);
            Assert.Equal(MobGroupingState.Cooldown, controller.State);
            Assert.False(controller.NeedsGatherRescan(earlyStop));
            Assert.False(controller.NeedsGatherRescan(earlyStop.AddMilliseconds(500)));

            MobGroupingDecision late = controller.Tick(Input(
                earlyStop.AddMilliseconds(600), Analyze(ScatteredPoints), sample: true, playerX: 10f));
            Assert.Equal(MobGroupingDirective.None, late.Directive);
            Assert.Equal(MobGroupingState.Cooldown, controller.State);
        }

        [Fact]
        public void EmptyDetectionDoesNotEndGatherEarly()
        {
            // A rescan that finds no markers (occlusion/out of view) must not be mistaken
            // for "mobs are grouped"; only the bounded deadline ends this attempt.
            var controller = MakeController(2000);
            Trigger(controller, Utc());
            DateTime moveStart = Utc(1200);

            foreach (int offset in new[] { 500, 1000, 1500 })
            {
                MobGroupingDecision scan = controller.Tick(Input(
                    moveStart.AddMilliseconds(offset), Analyze(), sample: true, playerX: offset / 100f));
                Assert.Equal(MobGroupingDirective.None, scan.Directive);
                Assert.Equal(MobGroupingState.GroupingMove, controller.State);
            }

            MobGroupingDecision resumed = controller.Tick(Input(
                moveStart.AddMilliseconds(2000), playerX: 20f));
            Assert.Equal(MobGroupingDirective.ResumeCombat, resumed.Directive);
        }

        [Fact]
        public void CancellingGatherStopsTheRescanLoopWithoutResumingThroughGather()
        {
            var controller = MakeController(3000);
            Trigger(controller, Utc());
            DateTime cancelAt = Utc(1700);

            MobGroupingDecision cancelled = controller.Cancel(cancelAt, "test cancel");

            Assert.Equal(MobGroupingDirective.Cancelled, cancelled.Directive);
            Assert.Equal(MobGroupingState.Cooldown, controller.State);
            Assert.False(controller.NeedsGatherRescan(cancelAt));
            Assert.False(controller.NeedsGatherRescan(cancelAt.AddMilliseconds(500)));
        }

        private static (MobGroupingDecision Decision, DateTime StartedAt) TriggerAfterCooldown(
            MobGroupingController controller, DateTime start)
        {
            MobGroupingDecision decision = default;
            foreach (int offset in new[] { 0, 225, 450, 675, 900, 1200 })
            {
                DateTime now = start.AddMilliseconds(offset);
                decision = controller.Tick(Input(now, Analyze(ScatteredPoints), sample: true));
                if (decision.Directive == MobGroupingDirective.StartMove)
                {
                    return (decision, now);
                }
            }
            return (decision, DateTime.MinValue);
        }

        [Fact]
        public void StableQuietPeriodResetsEncounterGroupingCount()
        {
            var controller = new MobGroupingController();
            Trigger(controller, Utc());
            controller.Cancel(Utc(1300), "test cleanup");
            Assert.Equal(1, controller.GroupingsThisEncounter);

            // The encounter reset is driven by combat-context absence, regardless of
            // whether marker detection is still running.
            for (int time = 1400; time <= 3900; time += 500)
                controller.Tick(Input(Utc(time), Analyze(), sample: true, combat: false));

            Assert.Equal(0, controller.GroupingsThisEncounter);
        }

        [Fact]
        public void ActiveGroupingCancelsImmediatelyWhenCombatContextEnds()
        {
            var controller = new MobGroupingController();
            Trigger(controller, Utc());

            MobGroupingDecision cancelled = controller.Tick(Input(
                Utc(1300), Analyze(ScatteredPoints), sample: true, combat: false));

            Assert.Equal(MobGroupingDirective.Cancelled, cancelled.Directive);
            Assert.Equal("combat context ended", cancelled.Reason);
            Assert.Equal(MobGroupingState.Cooldown, controller.State);
            // The sequence must not be active on the cancellation tick, so MovementSystem's
            // same-tick handoff runs the ordinary combat pipeline immediately.
            Assert.False(controller.IsSequenceActive);
        }

        [Fact]
        public void GroupingCancellationAllowsSameTickCombatHandoffWhenNoHigherPriorityOwner()
        {
            Assert.True(MovementSystem.CanContinueAfterGroupingCancellation(
                paused: false, cancellationRequested: false, transitioning: false,
                recoveryActive: false, lootHoldActive: false, windowValid: true));
            Assert.False(MovementSystem.CanContinueAfterGroupingCancellation(
                paused: true, cancellationRequested: false, transitioning: false,
                recoveryActive: false, lootHoldActive: false, windowValid: true));
            Assert.False(MovementSystem.CanContinueAfterGroupingCancellation(
                paused: false, cancellationRequested: true, transitioning: false,
                recoveryActive: false, lootHoldActive: false, windowValid: true));
            Assert.False(MovementSystem.CanContinueAfterGroupingCancellation(
                paused: false, cancellationRequested: false, transitioning: true,
                recoveryActive: false, lootHoldActive: false, windowValid: true));
            Assert.False(MovementSystem.CanContinueAfterGroupingCancellation(
                paused: false, cancellationRequested: false, transitioning: false,
                recoveryActive: true, lootHoldActive: false, windowValid: true));
            Assert.False(MovementSystem.CanContinueAfterGroupingCancellation(
                paused: false, cancellationRequested: false, transitioning: false,
                recoveryActive: false, lootHoldActive: true, windowValid: true));
            Assert.False(MovementSystem.CanContinueAfterGroupingCancellation(
                paused: false, cancellationRequested: false, transitioning: false,
                recoveryActive: false, lootHoldActive: false, windowValid: false));
        }

        [Fact]
        public void EncounterCountSurvivesShortCombatGapAndResetsOnlyAfterQuietDuration()
        {
            var controller = new MobGroupingController();
            SetGroupingsThisEncounter(controller, BotConstants.MobGrouping.MaxGroupingsPerEncounter);

            controller.Tick(Input(Utc(), combat: false));
            controller.Tick(Input(Utc(BotConstants.MobGrouping.EncounterQuietResetMs - 1), combat: false));
            Assert.Equal(BotConstants.MobGrouping.MaxGroupingsPerEncounter, controller.GroupingsThisEncounter);

            // A brief combat-context comeback restarts the absence window.
            controller.Tick(Input(Utc(BotConstants.MobGrouping.EncounterQuietResetMs), combat: true));
            controller.Tick(Input(Utc(BotConstants.MobGrouping.EncounterQuietResetMs + 1000), combat: false));
            controller.Tick(Input(Utc(BotConstants.MobGrouping.EncounterQuietResetMs + 3000), combat: false));
            Assert.Equal(BotConstants.MobGrouping.MaxGroupingsPerEncounter, controller.GroupingsThisEncounter);

            controller.Tick(Input(Utc(BotConstants.MobGrouping.EncounterQuietResetMs + 3600), combat: false));
            Assert.Equal(0, controller.GroupingsThisEncounter);
        }

        [Fact]
        public void EncounterCountResetsAcrossModeChangeAndWhileFeatureDisabled()
        {
            var controller = new MobGroupingController();
            SetGroupingsThisEncounter(controller, 2);

            DateTime modeStart = Utc();
            controller.Tick(Input(modeStart, mode: BotMode.OnlyMove, combat: false));
            controller.Tick(Input(modeStart.AddMilliseconds(
                BotConstants.MobGrouping.EncounterQuietResetMs), mode: BotMode.OnlyMove, combat: false));
            Assert.Equal(0, controller.GroupingsThisEncounter);

            // Combat returns and re-arms the lifecycle before the disabled scenario.
            controller.Tick(Input(modeStart.AddSeconds(5), combat: true));
            SetGroupingsThisEncounter(controller, 2);

            DateTime disabledStart = modeStart.AddSeconds(10);
            controller.Tick(Input(disabledStart, enabled: false, combat: false));
            controller.Tick(Input(disabledStart.AddMilliseconds(
                BotConstants.MobGrouping.EncounterQuietResetMs), enabled: false, combat: false));
            Assert.Equal(0, controller.GroupingsThisEncounter);
        }

        [Fact]
        public void DisablingDuringGroupingCancelsSequence()
        {
            var controller = new MobGroupingController();
            Trigger(controller, Utc());

            MobGroupingDecision cancelled = controller.Tick(Input(
                Utc(1300), Analyze(ScatteredPoints), enabled: false));

            Assert.Equal(MobGroupingDirective.Cancelled, cancelled.Directive);
            Assert.Equal(MobGroupingState.Cooldown, controller.State);
        }

        [Fact]
        public void ModeChangeDuringGroupingCancelsSequence()
        {
            var controller = new MobGroupingController();
            Trigger(controller, Utc());

            MobGroupingDecision cancelled = controller.Tick(Input(
                Utc(1300), Analyze(ScatteredPoints), mode: BotMode.OnlyMove));

            Assert.Equal(MobGroupingDirective.Cancelled, cancelled.Directive);
            Assert.Equal("mode changed to OnlyMove", cancelled.Reason);
        }

        [Theory]
        [InlineData(true, false, false, false)]
        [InlineData(false, true, false, false)]
        [InlineData(false, false, true, false)]
        [InlineData(false, false, false, true)]
        public void RecoveryPauseStopAndLootHoldsCancelGrouping(
            bool recovery, bool paused, bool stopping, bool lootHold)
        {
            var controller = new MobGroupingController();
            Trigger(controller, Utc());

            MobGroupingDecision cancelled = controller.Tick(Input(
                Utc(1300), Analyze(ScatteredPoints), recovery: recovery, paused: paused,
                stopping: stopping, lootHold: lootHold));

            Assert.Equal(MobGroupingDirective.Cancelled, cancelled.Directive);
        }

        [Fact]
        public void RepotOrUnfocusedWindowCancelsGrouping()
        {
            var controller = new MobGroupingController();
            Trigger(controller, Utc());
            Assert.Equal(MobGroupingDirective.Cancelled,
                controller.Tick(Input(Utc(1300), transition: true)).Directive);

            controller = new MobGroupingController();
            Trigger(controller, Utc());
            Assert.Equal(MobGroupingDirective.Cancelled,
                controller.Tick(Input(Utc(1300), window: false)).Directive);
        }

        [Fact]
        public void AnalyzerFindsAttackReadyClusterAmongOutliers()
        {
            MobGroupAnalysis analysis = Analyze(AttackReadyClusterWithOutliers);

            Assert.Equal(6, analysis.DetectedMobCount);
            Assert.True(analysis.IsScattered);
            Assert.False(analysis.IsSufficientlyClustered);
            Assert.True(analysis.HasAttackReadyCluster);
            Assert.Equal(BotConstants.MobGrouping.AttackReadyClusterMobCount, analysis.AttackReadyClusterMobCount);
        }

        [Fact]
        public void AnalyzerFindsClusterEvenWhenItIsNotCenteredOnTheWholeFormation()
        {
            // Two separate tight clusters with the whole centroid between them. Picking the
            // four nearest to the overall centroid would mix the clusters; the exact subset
            // search must still find a valid local cluster.
            MobPoint[] twoClusters =
            {
                new(55, 60), new(60, 55), new(65, 60), new(60, 65),
                new(235, 60), new(240, 55), new(245, 60), new(240, 65)
            };
            MobGroupAnalysis analysis = Analyze(twoClusters);

            Assert.Equal(8, analysis.DetectedMobCount);
            Assert.True(analysis.IsScattered);
            Assert.False(analysis.IsSufficientlyClustered);
            Assert.True(analysis.HasAttackReadyCluster);
        }

        [Fact]
        public void AnalyzerDoesNotReportAttackReadyClusterWithoutAnyCompactFourSubset()
        {
            MobGroupAnalysis analysis = Analyze(SpreadWithoutCluster);

            Assert.True(analysis.IsScattered);
            Assert.False(analysis.HasAttackReadyCluster);
            Assert.Equal(0, analysis.AttackReadyClusterMobCount);
        }

        [Fact]
        public void AnalyzerKeepsWholeFormationAndAttackReadyClusterConceptsSeparate()
        {
            // Exactly four clustered mobs: the whole formation is clustered, but the
            // subset concept only applies when more than four mobs are visible.
            MobGroupAnalysis analysis = Analyze(
                new MobPoint(95, 60), new MobPoint(100, 65),
                new MobPoint(105, 60), new MobPoint(100, 55));

            Assert.True(analysis.IsSufficientlyClustered);
            Assert.False(analysis.HasAttackReadyCluster);
        }

        [Fact]
        public void GatherNavigationTargetUsesLiveWaypointAndWaypointSteering()
        {
            var path = new List<Waypoint>
            {
                new(5000, 5000, MovementPrecision.Medium, BotMode.MoveAndAttack,
                    steeringMode: MovementSteeringMode.DirectCamera)
            };
            MovementSystem movement = new(CreateStubMemory(), _ => { }, 0, 0, customPath: path);

            Assert.True(movement.CanGatherTowardCurrentNavigationTarget(0, 0));
            Assert.True(movement.TryGetGatherNavigationTarget(0, 0,
                out float targetX, out float targetY, out MovementSteeringMode steering));
            Assert.Equal(5000f, targetX);
            Assert.Equal(5000f, targetY);
            Assert.Equal(MovementSteeringMode.DirectCamera, steering);
            movement.DisposeMobGroupingDetector();
        }

        [Fact]
        public void GatherNavigationTargetRejectsReachedWaypointsAndOperationSteps()
        {
            var reached = new List<Waypoint>
            {
                new(10, 10, MovementPrecision.Medium, BotMode.MoveAndAttack)
            };
            MovementSystem reachedMovement = new(CreateStubMemory(), _ => { }, 0, 0, customPath: reached);
            Assert.False(reachedMovement.CanGatherTowardCurrentNavigationTarget(10, 10));
            reachedMovement.DisposeMobGroupingDetector();

            var operationHead = new List<Waypoint>
            {
                new(0, 0, MovementPrecision.Medium, BotMode.OnlyMove, isOperationStep: true),
                new(50, 50, MovementPrecision.Medium, BotMode.MoveAndAttack)
            };
            MovementSystem operationMovement = new(CreateStubMemory(), _ => { }, 0, 0, customPath: operationHead);
            Assert.False(operationMovement.CanGatherTowardCurrentNavigationTarget(0, 0));
            operationMovement.DisposeMobGroupingDetector();
        }

        [Fact]
        public void AnalyzerCalculatesCentroidAndSpread()
        {
            MobGroupAnalysis analysis = Analyze(new MobPoint(0, 0), new MobPoint(4, 0), new MobPoint(0, 3));

            Assert.Equal(3, analysis.DetectedMobCount);
            Assert.Equal(4f / 3f, analysis.Centroid.X, 3);
            Assert.Equal(1f, analysis.Centroid.Y, 3);
            Assert.Equal((5f + (float)Math.Sqrt(73) + (float)Math.Sqrt(52)) / 9f, analysis.AverageSpread, 3);
            Assert.Equal((float)Math.Sqrt(73) / 3f, analysis.MaximumSpread, 3);
        }

        [Fact]
        public void AggregateDirectionAndEscapeUseEightWayQuantizationAcrossNorthWrap()
        {
            MobGroupAnalysis east = Analyze(
                new MobPoint(190, 55), new MobPoint(190, 60), new MobPoint(190, 65));
            Assert.Equal(MobGroupingDirection.West, east.RecommendedEscapeDirection);

            Assert.Equal(MobGroupingDirection.North,
                MobGroupingDirectionMath.QuantizeScreenVector(-0.01f, -1f));
            Assert.Equal(MobGroupingDirection.North,
                MobGroupingDirectionMath.QuantizeScreenVector(0.01f, -1f));
        }

        [Fact]
        public void CandidateDeduplicatesRingAndCyanCenterAndPrefersCyan()
        {
            float radius = BotConstants.MobGrouping.ExpectedMarkerRadiusPx;
            var candidates = new[]
            {
                new MobMarker(new MobPoint(50, 50), radius, 0.75f, MobMarkerSource.MagentaRing),
                new MobMarker(new MobPoint(52, 49), radius, 1f, MobMarkerSource.CyanCenter),
                new MobMarker(new MobPoint(100, 50), radius, 0.8f, MobMarkerSource.MagentaRing)
            };

            IReadOnlyList<MobMarker> unique = MobMarkerDetector.DeduplicateCandidates(candidates, radius);

            Assert.Equal(2, unique.Count);
            Assert.Equal(MobMarkerSource.CyanCenter, unique[0].Source);
            Assert.Equal(52, unique[0].Center.X);
        }

        [Fact]
        public void PinkLootScanExcludesSparseMarkerRingsButKeepsSolidDropSquares()
        {
            using var frame = NewFrame(120, 840);
            var ring = new LootSystem.WhiteComponent();
            var ringPixels = new HashSet<(int X, int Y)>();
            for (int angle = 0; angle < 360; angle++)
            {
                double radians = angle * Math.PI / 180.0;
                for (int thickness = -1; thickness <= 1; thickness++)
                {
                    int x = 50 + (int)Math.Round((18 + thickness) * Math.Cos(radians));
                    int y = 50 + (int)Math.Round((18 + thickness) * Math.Sin(radians));
                    ringPixels.Add((x, y));
                }
            }
            foreach (var pixel in ringPixels)
                ring.Add(pixel.X, pixel.Y);

            var solidDrop = new LootSystem.WhiteComponent();
            for (int x = 0; x < 24; x++)
            for (int y = 0; y < 24; y++)
                solidDrop.Add(x, y);

            Assert.True(LootSystem.IsPinkLootCandidate(ring));
            Assert.True(LootSystem.IsMobMarkerRingComponent(ring, frame));
            Assert.True(LootSystem.IsPinkLootCandidate(solidDrop));
            Assert.False(LootSystem.IsMobMarkerRingComponent(solidDrop, frame));
        }

        [Fact]
        public void DetectorRejectsCyanArtifactWithoutArtificialMagentaRing()
        {
            using var detector = new MobMarkerDetector();
            using var frame = NewFrame(120, 100);
            using (Graphics graphics = Graphics.FromImage(frame))
            using (var centerBrush = new SolidBrush(Color.Cyan))
                graphics.FillEllipse(centerBrush, 55, 45, 5, 5);

            Assert.Empty(detector.DetectBitmapForTesting(frame));
        }

        [Fact]
        public void DetectorAcceptsCyanCenterConfirmedByPartialMagentaRing()
        {
            using var detector = new MobMarkerDetector();
            using var frame = NewFrame(180, 100);
            float radius = BotConstants.MobGrouping.ExpectedMarkerRadiusPx;
            using (Graphics graphics = Graphics.FromImage(frame))
            {
                using var ringPen = new Pen(Color.Magenta, 2f);
                graphics.DrawArc(ringPen, 90 - radius, 50 - radius, radius * 2, radius * 2, 30, 100);
                using var centerBrush = new SolidBrush(Color.Cyan);
                graphics.FillEllipse(centerBrush, 90 - 2, 50 - 2, 5, 5);
            }

            MobMarker[] markers = detector.DetectBitmapForTesting(frame);
            Assert.Contains(markers, marker => marker.Source == MobMarkerSource.CyanCenter &&
                Math.Abs(marker.Center.X - 90) <= 2 && Math.Abs(marker.Center.Y - 50) <= 2);
        }

        [Fact]
        public void GroupingTargetBearingIsAbsoluteFromTheCameraAtMoveStart()
        {
            Assert.Equal(90f, MovementSystem.ComputeMobGroupingTargetBearingDeg(
                0f, MobGroupingDirection.East));
            Assert.Equal(10f, MovementSystem.ComputeMobGroupingTargetBearingDeg(
                10f, MobGroupingDirection.North));
            Assert.Equal(325f, MovementSystem.ComputeMobGroupingTargetBearingDeg(
                10f, MobGroupingDirection.NorthWest));
            Assert.Equal(350f, MovementSystem.ComputeMobGroupingTargetBearingDeg(
                35f, MobGroupingDirection.NorthWest));
            Assert.Equal(0f, MovementSystem.ComputeMobGroupingTargetBearingDeg(
                315f, MobGroupingDirection.NorthEast));

            // A second move started after the camera changed resolves a NEW absolute
            // bearing — the first move's target is not reused.
            Assert.NotEqual(
                MovementSystem.ComputeMobGroupingTargetBearingDeg(0f, MobGroupingDirection.East),
                MovementSystem.ComputeMobGroupingTargetBearingDeg(45f, MobGroupingDirection.East));
        }

        [Fact]
        public void GroupingKeyboardFeedbackReleasesTheTurnKeyWithinTheConfiguredTolerance()
        {
            // KeyboardTurn re-evaluates this pure decision against the live camera bearing
            // every tick; the stored grouping bearing stays fixed while the camera turns.
            const float storedBearing = 90f;
            Assert.Equal("RightD", DecideTurnKey(0f, storedBearing));
            Assert.Equal("RightD", DecideTurnKey(60f, storedBearing));
            Assert.Equal("None", DecideTurnKey(80f, storedBearing));
            Assert.Equal("None", DecideTurnKey(90f, storedBearing));
            Assert.Equal("None", DecideTurnKey(100f, storedBearing));
            Assert.Equal("LeftA", DecideTurnKey(120f, storedBearing));
        }

        [Fact]
        public void GroupingStopMovementClearsStoredBearingAndSteeringOwnership()
        {
            MovementSystem movement = CreateMovementSystem();
            FieldInfo bearingField = typeof(MovementSystem).GetField("_mobGroupingTargetBearingDeg",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo modeField = typeof(MovementSystem).GetField("_mobGroupingSteeringMode",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo ownsField = typeof(MovementSystem).GetField("_mobGroupingOwnsMovement",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo movingField = typeof(MovementSystem).GetField("_isMovingForward",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            MethodInfo apply = typeof(MovementSystem).GetMethod("ApplyMobGroupingDecision",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            bearingField.SetValue(movement, 123f);
            modeField.SetValue(movement, MovementSteeringMode.KeyboardTurn);
            ownsField.SetValue(movement, true);
            movingField.SetValue(movement, true);

            apply.Invoke(movement, new object[]
            {
                new MobGroupingDecision(MobGroupingDirective.StopMovement, MoveNumber: 1),
                Utc()
            });

            Assert.Equal(BotConstants.Movement.UnsetBearing, (float)bearingField.GetValue(movement)!);
            Assert.Null(modeField.GetValue(movement));
            Assert.False((bool)ownsField.GetValue(movement)!);
            Assert.False((bool)movingField.GetValue(movement)!);
            movement.DisposeMobGroupingDetector();
        }

        [Fact]
        public void GroupingResumeCombatReleasesMovementBeforeCombatResumes()
        {
            MovementSystem movement = CreateMovementSystem();
            FieldInfo bearingField = typeof(MovementSystem).GetField("_mobGroupingTargetBearingDeg",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo modeField = typeof(MovementSystem).GetField("_mobGroupingSteeringMode",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo ownsField = typeof(MovementSystem).GetField("_mobGroupingOwnsMovement",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo movingField = typeof(MovementSystem).GetField("_isMovingForward",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo skillField = typeof(MovementSystem).GetField("_isSkillThreeHeld",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            object combatHandler = typeof(MovementSystem)
                .GetField("_combatHandler", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(movement)!;
            MethodInfo apply = typeof(MovementSystem).GetMethod("ApplyMobGroupingDecision",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            bearingField.SetValue(movement, 90f);
            modeField.SetValue(movement, MovementSteeringMode.KeyboardTurn);
            ownsField.SetValue(movement, true);
            movingField.SetValue(movement, true);
            skillField.SetValue(movement, true);
            typeof(CombatHandler).GetMethod("SuspendForExternalMovement")!.Invoke(combatHandler, null);

            apply.Invoke(movement, new object[]
            {
                new MobGroupingDecision(MobGroupingDirective.ResumeCombat, MoveNumber: 1),
                Utc()
            });

            // Every gather-owned input must be released before attack control resumes.
            Assert.False(movement.IsAttackKeyHeld);
            Assert.False((bool)movingField.GetValue(movement)!);
            Assert.False((bool)ownsField.GetValue(movement)!);
            Assert.Equal(BotConstants.Movement.UnsetBearing, (float)bearingField.GetValue(movement)!);
            Assert.Null(modeField.GetValue(movement));
            Assert.False((bool)typeof(CombatHandler)
                .GetField("_externallySuspended", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(combatHandler)!);
            movement.DisposeMobGroupingDetector();
        }

        [Fact]
        public void GroupingSteeringTickReusesTheStoredBearingWithoutDrift()
        {
            MovementSystem movement = CreateMovementSystem();
            FieldInfo bearingField = typeof(MovementSystem).GetField("_mobGroupingTargetBearingDeg",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo modeField = typeof(MovementSystem).GetField("_mobGroupingSteeringMode",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo movingField = typeof(MovementSystem).GetField("_isMovingForward",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            MethodInfo steeringTick = typeof(MovementSystem).GetMethod("ApplyMobGroupingSteeringTick",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            // The stub camera reads 0°, so a stored bearing of 0° keeps the turn key at
            // None and no A/D key event is synthesized. W is already "held" so StartMoving
            // is a no-op too — this exercises the real per-tick steering path safely.
            bearingField.SetValue(movement, 0f);
            modeField.SetValue(movement, MovementSteeringMode.KeyboardTurn);
            movingField.SetValue(movement, true);

            for (int tick = 0; tick < 5; tick++)
                steeringTick.Invoke(movement, null);

            Assert.Equal(0f, (float)bearingField.GetValue(movement)!);
            Assert.Equal("None", typeof(MovementSystem)
                .GetField("_heldTurnKey", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(movement)!.ToString());
            movement.StopMoving();
            movement.DisposeMobGroupingDetector();
        }

        [Fact]
        public void ArtificialMagentaPredicateIsNarrowAndRejectsPinkLootShades()
        {
            Assert.True(MobMarkerDetector.IsMagentaMarkerPixel(Color.FromArgb(255, 0, 255)));
            Assert.True(MobMarkerDetector.IsMagentaMarkerPixel(Color.FromArgb(230, 20, 232)));
            Assert.False(MobMarkerDetector.IsMagentaMarkerPixel(Color.FromArgb(255, 34, 175)));
            Assert.False(MobMarkerDetector.IsMagentaMarkerPixel(Color.FromArgb(255, 20, 147)));
            Assert.True(MobMarkerDetector.IsCyanCenterPixel(Color.FromArgb(5, 250, 255)));
        }

        [Fact]
        public void DetectorFindsIsolatedAndTouchingCentersAndRingFallbacks()
        {
            using var detector = new MobMarkerDetector();
            using var isolated = NewFrame(150, 100);
            DrawMarker(isolated, 45, 48, includeCenter: true);
            MobMarker[] one = detector.DetectBitmapForTesting(isolated);
            Assert.Single(one);
            Assert.Equal(MobMarkerSource.CyanCenter, one[0].Source);
            Assert.InRange(one[0].Center.X, 44, 46);

            using var touching = NewFrame(180, 100);
            DrawMarker(touching, 55, 48, includeCenter: true);
            DrawMarker(touching, 89, 48, includeCenter: true);
            MobMarker[] two = detector.DetectBitmapForTesting(touching);
            Assert.True(two.Length == 2,
                $"Expected two touching mob centers; got {two.Length}: {string.Join(",", two.Select(m => $"{m.Center.X:F0},{m.Center.Y:F0}/{m.Source}/{m.Confidence:F2}"))}");

            using var overlapped = NewFrame(180, 100);
            DrawMarker(overlapped, 55, 48, includeCenter: false);
            DrawMarker(overlapped, 83, 48, includeCenter: false);
            MobMarker[] inferred = detector.DetectBitmapForTesting(overlapped);
            Assert.True(inferred.Count(marker => marker.Source == MobMarkerSource.MagentaRing) >= 2,
                $"Expected two overlapping ring hypotheses; got {inferred.Length}: {string.Join(",", inferred.Select(m => $"{m.Center.X:F0}/{m.Source}"))}");
        }

        [Fact]
        public void DetectorCanInferAVisiblePartialRingWithoutItsCyanCenter()
        {
            using var detector = new MobMarkerDetector();
            using var frame = NewFrame(180, 100);
            float radius = BotConstants.MobGrouping.ExpectedMarkerRadiusPx;
            float innerRadius = radius * BotConstants.MobGrouping.InnerRingRadiusRatio;
            using (Graphics graphics = Graphics.FromImage(frame))
            using (var pen = new Pen(Color.Magenta, 2f))
            {
                graphics.DrawArc(pen, 72, 32, radius * 2, radius * 2, 30, 220);
                graphics.DrawArc(pen, 90 - innerRadius, 50 - innerRadius,
                    innerRadius * 2, innerRadius * 2, 30, 220);
            }

            MobMarker[] markers = detector.DetectBitmapForTesting(frame);
            Assert.Contains(markers, marker => marker.Source == MobMarkerSource.MagentaRing &&
                Math.Abs(marker.Center.X - 90) <= 3 && Math.Abs(marker.Center.Y - 50) <= 3);
        }

        [Fact]
        public void DetectorRejectsFilledMagentaLootSquareWithoutMobRingGeometry()
        {
            using var detector = new MobMarkerDetector();
            using var frame = NewFrame(120, 100);
            using (Graphics graphics = Graphics.FromImage(frame))
            using (var brush = new SolidBrush(Color.Magenta))
                graphics.FillRectangle(brush, 35, 30, 24, 24);

            Assert.Empty(detector.DetectBitmapForTesting(frame));
        }

        [Fact]
        public void RoiAndPlayerAnchorAreClientRelativeAndScaleWithClient()
        {
            Rectangle roi = MobMarkerDetector.GetCombatRoi(1400, 900);
            MobPoint anchor = MobMarkerDetector.GetPlayerAnchor(1400, 900);

            Assert.True(roi.Left >= 0 && roi.Top >= 0);
            Assert.True(roi.Right <= 1400 && roi.Bottom <= 900);
            Assert.Equal(1400 * BotConstants.MobGrouping.PlayerAnchorRatioX, anchor.X, 3);
            Assert.Equal(900 * BotConstants.MobGrouping.PlayerAnchorRatioY, anchor.Y, 3);
            Assert.Equal(BotConstants.MobGrouping.ExpectedMarkerRadiusPx * 900 /
                BotConstants.MobGrouping.ReferenceClientHeightPx, MobMarkerDetector.GetExpectedRadius(900), 3);
        }

        [Fact]
        public void CancelMobGroupingReleasesGroupingAndCombatOwnedInputs()
        {
            GameMemoryService memory = CreateStubMemory();
            var path = new List<Waypoint>
            {
                new(5000, 5000, MovementPrecision.Medium, BotMode.MoveAndAttack)
            };
            var movement = new MovementSystem(memory, _ => { }, 5000, 5000, customPath: path);
            var controller = typeof(MovementSystem).GetField("_mobGroupingController",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(movement)!;
            typeof(MobGroupingController).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(controller, (int)MobGroupingState.GroupingMove);
            typeof(MobGroupingController).GetField("_groupingsThisEncounter", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(controller, 1);
            typeof(MovementSystem).GetField("_mobGroupingOwnsMovement", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(movement, true);
            typeof(MovementSystem).GetField("_isMovingForward", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(movement, true);
            typeof(MovementSystem).GetField("_isSkillThreeHeld", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(movement, true);

            typeof(MovementSystem).GetField("_mobGroupingTargetBearingDeg",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(movement, 120f);
            typeof(MovementSystem).GetField("_mobGroupingSteeringMode",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(movement, MovementSteeringMode.KeyboardTurn);
            object combatHandler = typeof(MovementSystem)
                .GetField("_combatHandler", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(movement)!;
            typeof(CombatHandler).GetMethod("SuspendForExternalMovement")!.Invoke(combatHandler, null);

            movement.CancelMobGrouping("pause test");

            Assert.False((bool)typeof(CombatHandler)
                .GetField("_externallySuspended", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(combatHandler)!);
            Assert.False(movement.IsAttackKeyHeld);
            Assert.False((bool)typeof(MovementSystem).GetField("_isMovingForward",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(movement)!);
            Assert.False((bool)typeof(MovementSystem).GetField("_mobGroupingOwnsMovement",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(movement)!);
            Assert.Equal(BotConstants.Movement.UnsetBearing, (float)typeof(MovementSystem)
                .GetField("_mobGroupingTargetBearingDeg", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(movement)!);
            Assert.Null(typeof(MovementSystem)
                .GetField("_mobGroupingSteeringMode", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(movement));
            movement.DisposeMobGroupingDetector();
        }

        private static void SetGroupingsThisEncounter(MobGroupingController controller, int value)
        {
            typeof(MobGroupingController)
                .GetField("_groupingsThisEncounter", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(controller, value);
        }

        private static string DecideTurnKey(float currentBearing, float storedBearing)
        {
            MethodInfo method = typeof(MovementSystem).GetMethod("GetDesiredTurnKey",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            object?[] arguments = { currentBearing, storedBearing, 0f };
            return method.Invoke(null, arguments)!.ToString()!;
        }

        private static MovementSystem CreateMovementSystem()
        {
            var path = new List<Waypoint>
            {
                new(5000, 5000, MovementPrecision.Medium, BotMode.MoveAndAttack)
            };
            return new MovementSystem(CreateStubMemory(), _ => { }, 5000, 5000, customPath: path);
        }

        private static Bitmap NewFrame(int width, int height)
            => new(width, height, PixelFormat.Format32bppArgb);

        private static void DrawMarker(Bitmap bitmap, int centerX, int centerY, bool includeCenter)
        {
            float radius = BotConstants.MobGrouping.ExpectedMarkerRadiusPx;
            using Graphics graphics = Graphics.FromImage(bitmap);
            using var ringPen = new Pen(Color.Magenta, 2f);
            using var innerPen = new Pen(Color.Magenta, 2f);
            graphics.DrawEllipse(ringPen, centerX - radius, centerY - radius, radius * 2, radius * 2);
            float innerRadius = radius * BotConstants.MobGrouping.InnerRingRadiusRatio;
            graphics.DrawEllipse(innerPen, centerX - innerRadius, centerY - innerRadius,
                innerRadius * 2, innerRadius * 2);
            if (includeCenter)
            {
                using var centerBrush = new SolidBrush(Color.Cyan);
                graphics.FillEllipse(centerBrush, centerX - 2, centerY - 2, 5, 5);
            }
        }

        private static GameMemoryService CreateStubMemory()
        {
            GameMemoryService.ReadMemoryDelegate read =
                (uint pid, ulong address, byte[] buffer, out uint bytesRead) =>
                {
                    bytesRead = 0;
                    return false;
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
}
