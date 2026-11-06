using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Reflection;
using DriverScanTester.Services;
using Xunit;
using static DriverScanTester.Services.LootSystem;

namespace DriverScanTester.Tests
{
    public sealed class MobGroupingTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly MobPoint Player = new(100, 60);

        /// <summary>Three wide, non-converged markers: no compact pair anywhere.</summary>
        private static readonly MobPoint[] ScatteredPoints =
        {
            new(20, 60), new(180, 60), new(100, 0)
        };
        /// <summary>Three tightly grouped markers (a compact local group already exists).</summary>
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
        /// <summary>Three tightly grouped markers plus one distant (ranged-like) outlier.</summary>
        private static readonly MobPoint[] ThreeCompactPlusOutlier =
        {
            new(95, 60), new(100, 65), new(105, 60), new(0, 60)
        };
        /// <summary>One compact pair plus two distant markers that will never converge.</summary>
        private static readonly MobPoint[] TwoCompactPlusOutliers =
        {
            new(95, 60), new(105, 60), new(0, 60), new(190, 60)
        };
        /// <summary>Very wide scattered markers: they stay non-compact for many observations.</summary>
        private static readonly MobPoint[] WideScattered =
        {
            new(0, 60), new(280, 60), new(140, 0)
        };
        /// <summary>Two distant (ranged-like) markers that keep their separation.</summary>
        private static readonly MobPoint[] TwoDistant =
        {
            new(20, 60), new(180, 60)
        };

        private static MobGroupAnalysis Analyze(params MobPoint[] points)
            => AnalyzeWithRadius(BotConstants.MobGrouping.ExpectedMarkerRadiusPx, points);

        private static MobGroupAnalysis AnalyzeWithRadius(float radius, params MobPoint[] points)
        {
            var markers = points.Select(point => new MobMarker(
                point, radius, 1f, MobMarkerSource.CyanCenter));
            return MobGroupAnalyzer.Analyze(markers, Player);
        }

        private static MobGroupAnalysis AnalyzeMarkers(params MobMarker[] markers)
            => MobGroupAnalyzer.Analyze(markers, Player);

        private static DateTime Utc(int milliseconds = 0) =>
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(milliseconds);

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
            bool gatherNav = true,
            float gatherTargetX = 5000,
            float gatherTargetY = 5000,
            MovementSteeringMode gatherSteering = MovementSteeringMode.KeyboardTurn,
            bool lootBarrier = false,
            float cameraYawDegrees = 90f,
            bool hasCameraYaw = true,
            int clientWidth = 1400,
            int clientHeight = 900)
            => new(now, enabled, mode, combat, paused, stopping, transition, recovery,
                lootHold, window, sample, analysis, positionValid, playerX, playerY, gatherNav,
                gatherTargetX, gatherTargetY, gatherSteering, lootBarrier,
                cameraYawDegrees * (MathF.PI / 180f), hasCameraYaw, clientWidth, clientHeight);

        /// <summary>
        /// Deterministic synthetic encounter driver. It contains no game memory, no screenshot
        /// and no client: the player walks along the direction to the live waypoint, the camera
        /// and the client size stay stable, and the detected formation converges by
        /// <see cref="Convergence"/> per observation (1.0 = static, &lt;1 = converging,
        /// &gt;1 = diverging).
        /// </summary>
        private sealed class Encounter
        {
            public Encounter(
                MobGroupingController? controller = null,
                MobPoint[]? markers = null,
                Action<string>? log = null)
            {
                Controller = controller ?? new MobGroupingController(log);
                Markers = (MobPoint[])(markers ?? ScatteredPoints).Clone();
            }

            public MobGroupingController Controller { get; }
            public DateTime Now { get; private set; } = Utc();
            /// <summary>Current marker centers in client coordinates (before the next step).</summary>
            public MobPoint[] Markers { get; set; }
            /// <summary>Per-observation scale toward the formation centroid.</summary>
            public float Convergence { get; set; } = 0.95f;
            /// <summary>World displacement per observation (eligibility needs real progress).</summary>
            public float StepX { get; set; } = 0.3f;
            public float StepY { get; set; } = 0.3f;
            public float PlayerX { get; private set; }
            public float PlayerY { get; private set; }
            public float WaypointX { get; set; } = 5000f;
            public float WaypointY { get; set; } = 5000f;
            public int StepMs { get; set; } = 225;
            public float CameraYawDegrees { get; set; } = 90f;
            public bool HasCameraYaw { get; set; } = true;
            public int ClientWidth { get; set; } = 1400;
            public int ClientHeight { get; set; } = 900;
            /// <summary>True when a detection sample is taken on this tick.</summary>
            public bool CaptureSample { get; set; } = true;
            /// <summary>True when the detector produced a result (false = failed capture).</summary>
            public bool Detect { get; set; } = true;
            public bool Combat { get; set; } = true;
            public bool Enabled { get; set; } = true;
            public bool Paused { get; set; }
            public bool Stopping { get; set; }
            public bool Transition { get; set; }
            public bool Recovery { get; set; }
            public bool LootHold { get; set; }
            public bool LootBarrier { get; set; }
            public bool Window { get; set; } = true;
            public bool PlayerPositionValid { get; set; } = true;
            public bool GatherNav { get; set; } = true;
            public BotMode Mode { get; set; } = BotMode.MoveAndAttack;
            /// <summary>True when the host can steer a gather toward the detected mob cluster.</summary>
            public bool CanApproachMobs { get; set; }
            public float Radius { get; set; } = BotConstants.MobGrouping.ExpectedMarkerRadiusPx;
            public float Confidence { get; set; } = 1f;
            public MobGroupingDecision LastDecision { get; private set; }
            public List<MobGroupingDecision> Decisions { get; } = new();

            public MobGroupAnalysis CurrentAnalysis()
            {
                MobMarker[] markers = Markers
                    .Select(point => new MobMarker(point, Radius, Confidence, MobMarkerSource.CyanCenter))
                    .ToArray();
                return MobGroupAnalyzer.Analyze(markers, Player);
            }

            private MobGroupingTickInput BuildInput()
            {
                MobGroupAnalysis? analysis = Detect ? CurrentAnalysis() : null;
                return new MobGroupingTickInput(
                    Now, Enabled, Mode, Combat, Paused, Stopping, Transition, Recovery,
                    LootHold, Window, CaptureSample, analysis, PlayerPositionValid,
                    PlayerX, PlayerY, GatherNav, WaypointX, WaypointY,
                    MovementSteeringMode.KeyboardTurn, LootBarrier,
                    CameraYawDegrees * (MathF.PI / 180f), HasCameraYaw,
                    ClientWidth, ClientHeight, CanApproachMobs);
            }

            public MobGroupingDecision Tick()
            {
                Now = Now.AddMilliseconds(StepMs);
                PlayerX += StepX;
                PlayerY += StepY;
                if (Math.Abs(Convergence - 1f) > 0.0001f)
                    Markers = Scale(Markers, Convergence);

                LastDecision = Controller.Tick(BuildInput());
                Decisions.Add(LastDecision);
                return LastDecision;
            }

            public void Tick(int count)
            {
                for (int i = 0; i < count; i++)
                    Tick();
            }

            /// <summary>Runs until the first StartMove (returns it) or the tick budget expires.</summary>
            public MobGroupingDecision RunUntilStartMove(int maxTicks = 20)
            {
                for (int i = 0; i < maxTicks; i++)
                {
                    MobGroupingDecision decision = Tick();
                    if (decision.Directive == MobGroupingDirective.StartMove)
                        return decision;
                }
                return LastDecision;
            }
        }

        private static MobPoint[] Scale(MobPoint[] points, float factor)
        {
            if (points.Length == 0)
                return points;

            float centreX = points.Average(point => point.X);
            float centreY = points.Average(point => point.Y);
            return points
                .Select(point => new MobPoint(
                    centreX + (point.X - centreX) * factor,
                    centreY + (point.Y - centreY) * factor))
                .ToArray();
        }

        /// <summary>Starts a gather through the production decision path and returns the driver.</summary>
        private static Encounter StartGather(
            MobGroupingController controller, List<string>? log = null, BotMode mode = BotMode.MoveAndAttack)
        {
            var encounter = new Encounter(controller, ScatteredPoints);
            encounter.Mode = mode;
            MobGroupingDecision decision = encounter.RunUntilStartMove();
            Assert.Equal(MobGroupingDirective.StartMove, decision.Directive);
            Assert.Equal(MobGroupingState.GroupingMove, controller.State);
            return encounter;
        }

        private static void AssertNeverStartsGathering(Encounter encounter, int ticks)
        {
            for (int i = 0; i < ticks; i++)
                Assert.NotEqual(MobGroupingDirective.StartMove, encounter.Tick().Directive);

            Assert.Equal(0, encounter.Controller.GroupingsThisEncounter);
            Assert.Equal(0, encounter.Controller.MovesStarted);
        }

        // ── Two-mob behaviour ──────────────────────────────────────────────────

        [Fact]
        public void ExactlyTwoDistantMarkersWithStaticFormationDoNotClaimConvergence()
        {
            // Scenario A: two ranged enemies that keep their attack distance and show no
            // convergence trend. A pair is now eligible for a bounded gather, but without evidence
            // of improvement the walk may never be reported as a confirmed convergence.
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            var encounter = new Encounter(controller, TwoDistant) { Convergence = 1f };

            for (int i = 0; i < 30; i++)
            {
                MobGroupingDecision decision = encounter.Tick();
                if (decision.Directive == MobGroupingDirective.StartMove)
                    Assert.NotEqual(MobGroupingReason.ConvergenceConfirmed, decision.Code);
            }

            Assert.DoesNotContain(lines, line =>
                line.Contains(MobGroupingReason.TwoMobNoGather, StringComparison.Ordinal));
            Assert.Contains(lines, line =>
                line.Contains(MobGroupingReason.NoConvergenceEvidence, StringComparison.Ordinal));
            Assert.True(controller.GroupingsThisEncounter <= 1);
        }

        [Fact]
        public void StationaryScatteredMobsUseAMobApproachGather()
        {
            // Real fights: the bot stands while it attacks. With a live mob direction the gather
            // walks toward the cluster instead of being refused for "no route progress".
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            var encounter = new Encounter(controller, ScatteredPoints)
            {
                StepX = 0f,
                StepY = 0f,
                CanApproachMobs = true
            };

            MobGroupingDecision decision = encounter.RunUntilStartMove();

            Assert.Equal(MobGroupingDirective.StartMove, decision.Directive);
            Assert.Equal(MobGroupingGatherTarget.ApproachMobs, decision.GatherTarget);
            Assert.Equal(MobGroupingReason.StationaryApproach, decision.Code);
            Assert.Equal(MobGroupingState.GroupingMove, controller.State);
        }

        [Fact]
        public void ModeratelySpreadThreeMobsAreEligibleForGathering()
        {
            // ~60 px triangle. The old 40/68 px trigger never called this "scattered" and it was
            // far too wide to be "compact", so grouping silently did nothing at all.
            MobPoint[] markers = { new(70, 60), new(130, 60), new(100, 12) };
            MobGroupAnalysis analysis = Analyze(markers);
            Assert.True(analysis.IsScattered);
            Assert.False(analysis.HasCompactLocalGroup);

            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            var encounter = new Encounter(controller, markers) { Convergence = 0.94f };

            MobGroupingDecision decision = encounter.RunUntilStartMove();

            Assert.Equal(MobGroupingDirective.StartMove, decision.Directive);
            Assert.Contains(lines, line =>
                line.Contains("scattered-formation candidate started", StringComparison.Ordinal));
        }

        [Fact]
        public void ExactlyTwoScatteredConvergingMarkersCanStartGathering()
        {
            // MinimumGatherMarkers == 2: a scattered pair that converges route-compatibly is
            // allowed to borrow one short gather walk instead of attacking them one by one.
            var controller = new MobGroupingController();
            var encounter = new Encounter(controller, TwoDistant);

            MobGroupingDecision decision = encounter.RunUntilStartMove();

            Assert.Equal(MobGroupingDirective.StartMove, decision.Directive);
            Assert.Equal(MobGroupingState.GroupingMove, controller.State);
            Assert.Equal(1, controller.GroupingsThisEncounter);
        }

        [Fact]
        public void ExactlyTwoCompactMarkersNeverStartGathering()
        {
            // Scenario B: one melee and one ranged marker at different apparent distances.
            // An already compact pair is a usable local group, so no movement is justified.
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            var encounter = new Encounter(controller, new[] { new MobPoint(95, 60), new MobPoint(105, 60) })
            {
                Convergence = 1f
            };

            AssertNeverStartsGathering(encounter, 20);
            Assert.Contains(lines, line =>
                line.Contains(MobGroupingReason.CompactLocalGroup, StringComparison.Ordinal));
        }

        [Fact]
        public void ThreeCompactMarkersPlusOutlierNeverStartGathering()
        {
            // Scenario D: the three nearby enemies are already a useful local group. Chasing
            // the distant outlier would give up a good attack for a speculative one.
            MobGroupAnalysis analysis = Analyze(ThreeCompactPlusOutlier);

            Assert.Equal(4, analysis.DetectedMobCount);
            Assert.True(analysis.IsScattered);
            Assert.True(analysis.HasCompactLocalGroup);
            Assert.Equal(3, analysis.BestLocalClusterSize);
            Assert.True(analysis.IsMovementSampleReliable);

            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            var encounter = new Encounter(controller, ThreeCompactPlusOutlier) { Convergence = 1f };

            AssertNeverStartsGathering(encounter, 20);
            Assert.Contains(lines, line =>
                line.Contains(MobGroupingReason.CompactLocalGroup, StringComparison.Ordinal));
        }

        [Fact]
        public void TwoCompactMarkersPlusDistantOutliersNeverStartGathering()
        {
            // Scenario C: a useful local pair exists; the outliers are never chased.
            MobGroupAnalysis analysis = Analyze(TwoCompactPlusOutliers);

            Assert.Equal(4, analysis.DetectedMobCount);
            Assert.True(analysis.HasCompactLocalGroup);
            Assert.Equal(2, analysis.BestLocalClusterSize);

            var controller = new MobGroupingController();
            var encounter = new Encounter(controller, TwoCompactPlusOutliers) { Convergence = 1f };
            AssertNeverStartsGathering(encounter, 20);
        }

        // ── No speculative movement ────────────────────────────────────────────

        [Fact]
        public void ThreeScatteredStationaryMarkersDoNotStartSpeculativeMovement()
        {
            // Scenario F: no convergence and no movement progress. The bot keeps attacking.
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            var encounter = new Encounter(controller, ScatteredPoints)
            {
                Convergence = 1f,
                StepX = 0f,
                StepY = 0f
            };

            AssertNeverStartsGathering(encounter, 20);
            Assert.Contains(lines, line =>
                line.Contains(MobGroupingReason.NoConvergenceEvidence, StringComparison.Ordinal) ||
                line.Contains(MobGroupingReason.NoMovementProgress, StringComparison.Ordinal));
        }

        [Fact]
        public void ThreeScatteredMarkersWithUnreliableDetectionDoNotStartMovement()
        {
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            var encounter = new Encounter(controller, ScatteredPoints)
            {
                Confidence = BotConstants.MobGrouping.MinimumDetectionConfidence * 0.5f
            };

            AssertNeverStartsGathering(encounter, 20);
            Assert.False(encounter.CurrentAnalysis().IsMovementSampleReliable);
            Assert.Contains(lines, line =>
                line.Contains(MobGroupingReason.DetectionUnreliable, StringComparison.Ordinal));
        }

        [Fact]
        public void TooManyMarkersForAReliableMovementDecisionSuppressGathering()
        {
            // Clutter must never be reduced to a favourable subset: the sample is simply
            // declared unreliable and normal combat continues.
            var many = new List<MobPoint>();
            int markerCount = BotConstants.MobGrouping.MaximumMarkersForEvaluation + 1;
            for (int i = 0; i < markerCount; i++)
            {
                double angle = 2 * Math.PI * i / markerCount;
                many.Add(new MobPoint(
                    100f + 200f * (float)Math.Cos(angle),
                    60f + 200f * (float)Math.Sin(angle)));
            }

            MobGroupAnalysis analysis = Analyze(many.ToArray());
            Assert.False(analysis.IsMovementSampleReliable);

            var controller = new MobGroupingController();
            AssertNeverStartsGathering(new Encounter(controller, many.ToArray()), 15);
        }

        // ── Positive convergence ───────────────────────────────────────────────

        [Fact]
        public void StableRouteCompatibleConvergenceQualifiesOneShortGather()
        {
            // Scenario E: three scattered enemies converge while the bot already walks toward
            // its waypoint. Exactly one short, bounded gather is allowed.
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            var encounter = new Encounter(controller, ScatteredPoints);

            MobGroupingDecision started = encounter.RunUntilStartMove();

            Assert.Equal(MobGroupingDirective.StartMove, started.Directive);
            Assert.Equal(1, controller.GroupingsThisEncounter);
            Assert.Equal(1, controller.MovesStarted);
            Assert.Equal(MobGroupingState.GroupingMove, controller.State);
            Assert.True(controller.LastTrend.IsImproving);
            Assert.Contains(lines, line => line.Contains("gather started", StringComparison.Ordinal) &&
                line.Contains("localCluster=", StringComparison.Ordinal) &&
                line.Contains("quality=", StringComparison.Ordinal) &&
                line.Contains("waypoint=", StringComparison.Ordinal) &&
                line.Contains($"budget={BotConstants.MobGrouping.MaximumGatherDurationMs}ms",
                    StringComparison.Ordinal));
        }

        [Fact]
        public void SlowConvergenceNeverClaimsConfirmedConvergence()
        {
            // Slow convergence: the accumulated change stays inside the noise tolerance, so the
            // trend is not treated as positive evidence. The bounded approach may still run, but
            // it must never be reported as a confirmed convergence.
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            var encounter = new Encounter(controller, ScatteredPoints) { Convergence = 0.999f };

            for (int i = 0; i < 20; i++)
            {
                MobGroupingDecision decision = encounter.Tick();
                Assert.NotEqual(MobFormationTrend.Improving, controller.LastTrend.Trend);
                if (decision.Directive == MobGroupingDirective.StartMove)
                    Assert.NotEqual(MobGroupingReason.ConvergenceConfirmed, decision.Code);
            }

            Assert.Contains(lines, line =>
                line.Contains($"[{MobGroupingReason.NoConvergenceEvidence}]", StringComparison.Ordinal));
        }

        [Fact]
        public void CameraAngleDiscontinuityInvalidatesTemporalConvergence()
        {
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            var encounter = new Encounter(controller, ScatteredPoints);

            // Three improving observations, then a 25 degree camera turn: screen-space geometry
            // is no longer comparable, so the earlier evidence is invalidated. Only two
            // post-jump observations exist, which is not enough for a positive trend.
            encounter.Tick(2);
            MobGroupingController controllerUnderTest = encounter.Controller;
            encounter.CameraYawDegrees += 25f;
            encounter.Tick(2);

            Assert.NotEqual(MobFormationTrend.Improving, controllerUnderTest.LastTrend.Trend);
            Assert.DoesNotContain(lines, line =>
                line.Contains($"gather started [{MobGroupingReason.ConvergenceConfirmed}]",
                    StringComparison.Ordinal));
        }

        [Fact]
        public void SuddenMarkerCountDecreaseDoesNotCountAsFormationImprovement()
        {
            // Losing a marker from the ROI shrinks the visible count AND can make the mean
            // nearest-neighbour distance drop sharply (here the measured quality would jump by
            // far more than the noise tolerance). That is not evidence that the local group
            // improved, so it must never qualify a gather walk.
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            var encounter = new Encounter(controller, new[]
            {
                new MobPoint(10, 60), new MobPoint(190, 60),
                new MobPoint(100, 10), new MobPoint(100, 110)
            })
            {
                Convergence = 1f
            };

            float qualityBefore = encounter.CurrentAnalysis().FormationQuality;
            encounter.Tick(6);
            Assert.Equal(4, encounter.CurrentAnalysis().DetectedMobCount);

            // Four markers become three; the remaining formation looks much more compact.
            encounter.Markers = new[] { new MobPoint(55, 60), new MobPoint(145, 60), new MobPoint(100, 25) };
            float qualityAfter = encounter.CurrentAnalysis().FormationQuality;
            Assert.True(qualityAfter > qualityBefore * 1.4f);

            for (int i = 0; i < 12; i++)
            {
                MobGroupingDecision decision = encounter.Tick();
                Assert.NotEqual(MobFormationTrend.Improving, controller.LastTrend.Trend);
                if (decision.Directive == MobGroupingDirective.StartMove)
                    Assert.NotEqual(MobGroupingReason.ConvergenceConfirmed, decision.Code);
            }

            Assert.DoesNotContain(lines, line =>
                line.Contains($"gather started [{MobGroupingReason.ConvergenceConfirmed}]",
                    StringComparison.Ordinal));
        }

        [Fact]
        public void WaypointIncompatibleMovementDirectionPreventsGathering()
        {
            // The player is already walking, but away from the live waypoint: gathering must not
            // invent a new travel direction.
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            var encounter = new Encounter(controller, ScatteredPoints)
            {
                StepX = -0.3f,
                StepY = -0.3f
            };

            AssertNeverStartsGathering(encounter, 20);
            Assert.Contains(lines, line =>
                line.Contains(MobGroupingReason.MovementNotAligned, StringComparison.Ordinal));
        }

        [Fact]
        public void StationaryPlayerNeverStartsAGatherRun()
        {
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            var encounter = new Encounter(controller, ScatteredPoints) { StepX = 0f, StepY = 0f };

            AssertNeverStartsGathering(encounter, 20);
            Assert.Contains(lines, line =>
                line.Contains(MobGroupingReason.NoMovementProgress, StringComparison.Ordinal));
        }

        // ── Active gather stop conditions ──────────────────────────────────────

        [Fact]
        public void ActiveGatherStopsWhenFormationQualityDeteriorates()
        {
            // Scenario G: the enemies separate further while the player moves.
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            Encounter encounter = StartGather(controller, lines);

            encounter.Convergence = 1.15f;
            encounter.Tick(5);

            Assert.Equal(MobGroupingState.ResumeCombat, controller.State);
            Assert.Contains(lines, line =>
                line.Contains(MobGroupingReason.FormationDeteriorated, StringComparison.Ordinal));
            Assert.Contains(encounter.Decisions, decision =>
                decision.Directive == MobGroupingDirective.ResumeCombat);
        }

        [Fact]
        public void ActiveGatherStopsWhenImprovementStagnates()
        {
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            Encounter encounter = StartGather(controller, lines);

            encounter.Convergence = 1f;
            encounter.Tick(5);

            Assert.Equal(MobGroupingState.ResumeCombat, controller.State);
            Assert.Contains(lines, line =>
                line.Contains(MobGroupingReason.NoFormationImprovement, StringComparison.Ordinal));
        }

        [Fact]
        public void ActiveGatherStopsWhenDurationBudgetExpires()
        {
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            Encounter encounter = StartGather(controller, lines);

            // Keep converging (so no stagnation stop) while walking slowly enough to stay
            // inside the displacement budget.
            encounter.StepX = 0.15f;
            encounter.StepY = 0.15f;

            DateTime startedAt = encounter.Now;
            encounter.Tick(8);

            Assert.Equal(MobGroupingState.ResumeCombat, controller.State);
            Assert.True((encounter.Now - startedAt).TotalMilliseconds >
                        BotConstants.MobGrouping.MaximumGatherDurationMs);
            Assert.Contains(lines, line =>
                line.Contains(MobGroupingReason.TimeBudgetReached, StringComparison.Ordinal));
        }

        [Fact]
        public void ActiveGatherStopsWhenDisplacementBudgetExpires()
        {
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            Encounter encounter = StartGather(controller, lines);

            encounter.StepX = 0.6f;
            encounter.StepY = 0.6f;
            encounter.Tick(3);

            Assert.Equal(MobGroupingState.ResumeCombat, controller.State);
            Assert.Contains(lines, line =>
                line.Contains(MobGroupingReason.DistanceBudgetReached, StringComparison.Ordinal));
        }

        [Fact]
        public void ActiveGatherStopsWhenACompactLocalGroupForms()
        {
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            Encounter encounter = StartGather(controller, lines);

            encounter.Convergence = 1f;
            encounter.Markers = ClusteredPoints;
            encounter.Tick();

            Assert.Equal(MobGroupingState.ResumeCombat, controller.State);
            Assert.Contains(lines, line =>
                line.Contains(MobGroupingReason.CompactLocalGroup, StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("improved=", StringComparison.Ordinal));
        }

        [Fact]
        public void ActiveGatherStopsWhenTheWaypointIsReached()
        {
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            Encounter encounter = StartGather(controller, lines);

            encounter.GatherNav = false;
            encounter.Tick();

            Assert.Equal(MobGroupingState.ResumeCombat, controller.State);
            Assert.Contains(lines, line =>
                line.Contains(MobGroupingReason.WaypointReached, StringComparison.Ordinal));
        }

        [Fact]
        public void ActiveGatherStopsWhenPlayerPositionBecomesUnavailable()
        {
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            Encounter encounter = StartGather(controller, lines);

            encounter.PlayerPositionValid = false;
            encounter.Tick();

            Assert.Equal(MobGroupingState.ResumeCombat, controller.State);
            Assert.Contains(lines, line =>
                line.Contains(MobGroupingReason.DetectionUnreliable, StringComparison.Ordinal));
        }

        [Fact]
        public void ActiveGatherStopsWhenThePlayerStopsMakingProgress()
        {
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            Encounter encounter = StartGather(controller, lines);

            encounter.StepX = 0f;
            encounter.StepY = 0f;
            encounter.Tick(4);

            Assert.Equal(MobGroupingState.ResumeCombat, controller.State);
            Assert.Contains(lines, line =>
                line.Contains(MobGroupingReason.NoMovementProgress, StringComparison.Ordinal));
        }

        [Fact]
        public void ActiveGatherStopsAfterTwoConsecutiveUnreliableSamples()
        {
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            Encounter encounter = StartGather(controller, lines);

            encounter.CaptureSample = true;
            encounter.Detect = false;
            encounter.Tick();
            Assert.Equal(MobGroupingState.GroupingMove, controller.State);

            encounter.Tick();
            Assert.Equal(MobGroupingState.ResumeCombat, controller.State);
            Assert.Contains(lines, line =>
                line.Contains(MobGroupingReason.DetectionUnreliable, StringComparison.Ordinal));
        }

        [Fact]
        public void OneLostSampleDoesNotEndTheGatherButInvalidatedEvidenceDoes()
        {
            // A single failed capture is tolerated (occlusion is normal). The next reliable
            // sample, however, can no longer confirm the earlier convergence trend, and a walk
            // that cannot prove it is improving must stop instead of continuing speculatively.
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            Encounter encounter = StartGather(controller, lines);

            encounter.Detect = false;
            encounter.Tick();
            Assert.Equal(MobGroupingState.GroupingMove, controller.State);

            encounter.Detect = true;
            encounter.Tick();

            Assert.Equal(MobGroupingState.ResumeCombat, controller.State);
            Assert.Contains(lines, line =>
                line.Contains(MobGroupingReason.NoConvergenceEvidence, StringComparison.Ordinal));
        }

        [Fact]
        public void GatherNeverExtendsItsDeadlineAndNeverReplans()
        {
            var controller = new MobGroupingController();
            Encounter encounter = StartGather(controller);
            DateTime startedAt = encounter.Now;

            // Deadline is fixed at start + MaximumGatherDurationMs and no rescan can move it.
            for (int i = 0; i < 6; i++)
            {
                encounter.Tick();
                if (controller.State != MobGroupingState.GroupingMove)
                    break;
                Assert.True((encounter.Now - startedAt).TotalMilliseconds <=
                            BotConstants.MobGrouping.MaximumGatherDurationMs);
            }

            Assert.NotEqual(MobGroupingState.GroupingMove, controller.State);
            Assert.Equal(1, controller.MovesStarted);
            Assert.False(controller.NeedsGatherRescan(encounter.Now));
        }

        [Fact]
        public void GatherRescansUseTheBoundedIntervalAndNeverRunPastTheDeadline()
        {
            var controller = new MobGroupingController();
            Encounter encounter = StartGather(controller);
            DateTime startedAt = encounter.Now;

            DateTime deadline = startedAt.AddMilliseconds(
                BotConstants.MobGrouping.MaximumGatherDurationMs);
            Assert.False(controller.NeedsGatherRescan(
                startedAt.AddMilliseconds(BotConstants.MobGrouping.GatherRescanIntervalMs - 1)));
            Assert.True(controller.NeedsGatherRescan(
                startedAt.AddMilliseconds(BotConstants.MobGrouping.GatherRescanIntervalMs)));
            Assert.False(controller.NeedsGatherRescan(deadline));
            Assert.False(controller.NeedsGatherRescan(deadline.AddMilliseconds(500)));
        }

        // ── Encounter lifecycle ────────────────────────────────────────────────

        [Fact]
        public void OnlyOneGatherAttemptIsAllowedPerEncounter()
        {
            var controller = new MobGroupingController();
            Encounter encounter = StartGather(controller);
            DateTime deadline = encounter.Now.AddMilliseconds(5000);

            encounter.Convergence = 1f;
            encounter.Tick(5);
            Assert.Equal(MobGroupingState.ResumeCombat, controller.State);
            controller.CompleteResume(encounter.Now);

            // Back to a perfectly eligible, converging, route-compatible situation.
            encounter.Convergence = 0.95f;
            encounter.Markers = (MobPoint[])ScatteredPoints.Clone();
            int beforeRetry = encounter.Decisions.Count;
            encounter.Tick(BotConstants.MobGrouping.CooldownMs / encounter.StepMs + 8);

            Assert.Equal(1, controller.GroupingsThisEncounter);
            Assert.DoesNotContain(encounter.Decisions.Skip(beforeRetry), decision =>
                decision.Directive == MobGroupingDirective.StartMove);
            Assert.True(encounter.Now > deadline);
        }

        [Fact]
        public void FailedGatherNeverRestartsWithinTheSameEncounter()
        {
            // Requirement: no start/cancel/restart cycling on a scattered formation.
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            Encounter encounter = StartGather(controller, lines);

            encounter.Convergence = 1f;
            encounter.Tick(3);
            controller.CompleteResume(encounter.Now);

            int starts = encounter.Decisions.Count(decision =>
                decision.Directive == MobGroupingDirective.StartMove);
            encounter.Tick(60);

            Assert.Equal(1, starts);
            Assert.Equal(1, controller.GroupingsThisEncounter);
            Assert.Equal(1, encounter.Decisions.Count(decision =>
                decision.Directive == MobGroupingDirective.StartMove));
        }

        [Fact]
        public void EncounterBudgetResetsAfterTheContinuousQuietInterval()
        {
            var controller = new MobGroupingController();
            Encounter encounter = StartGather(controller);
            encounter.Convergence = 1f;
            encounter.Tick(3);
            controller.CompleteResume(encounter.Now);
            Assert.Equal(1, controller.GroupingsThisEncounter);

            // No combat context for the whole quiet interval resets the encounter budget.
            encounter.Combat = false;
            encounter.CaptureSample = false;
            encounter.Tick(BotConstants.MobGrouping.EncounterQuietResetMs / encounter.StepMs + 3);
            Assert.Equal(0, controller.GroupingsThisEncounter);

            encounter.Combat = true;
            encounter.CaptureSample = true;
            encounter.Convergence = 0.95f;
            encounter.Markers = (MobPoint[])WideScattered.Clone();
            encounter.StepX = 0.3f;
            encounter.StepY = 0.3f;
            // The cooldown must expire first; combat-minimum/persistence restart from scratch.
            MobGroupingDecision decision = encounter.RunUntilStartMove(30);
            Assert.Equal(MobGroupingDirective.StartMove, decision.Directive);
            Assert.Equal(1, controller.GroupingsThisEncounter);
        }

        [Fact]
        public void StableQuietPeriodResetsEncounterGroupingCount()
        {
            var controller = new MobGroupingController();
            StartGather(controller);
            controller.Cancel(Utc(1300), "test cleanup");
            Assert.Equal(1, controller.GroupingsThisEncounter);

            // The encounter reset is driven by combat-context absence, regardless of
            // whether marker detection is still running.
            for (int time = 1400; time <= 3900; time += 500)
                controller.Tick(Input(Utc(time), Analyze(), sample: true, combat: false));

            Assert.Equal(0, controller.GroupingsThisEncounter);
        }

        [Fact]
        public void NormalCombatResumesAfterAnUnsuccessfulGather()
        {
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            Encounter encounter = StartGather(controller, lines);

            encounter.Convergence = 1f;
            MobGroupingDecision resumed = default;
            for (int i = 0; i < 5; i++)
            {
                MobGroupingDecision decision = encounter.Tick();
                if (decision.Directive == MobGroupingDirective.ResumeCombat)
                {
                    resumed = decision;
                    break;
                }
            }

            Assert.Equal(MobGroupingDirective.ResumeCombat, resumed.Directive);
            // MovementSystem completes the resume on the directive tick; only then does the
            // sequence stop owning the update.
            controller.CompleteResume(encounter.Now);
            Assert.False(controller.IsSequenceActive);
            Assert.Equal(MobGroupingState.Cooldown, controller.State);
            Assert.NotEqual(MobGroupingDirective.StartMove,
                controller.Tick(Input(encounter.Now.AddMilliseconds(10),
                    Analyze(ScatteredPoints), sample: true)).Directive);
        }

        [Fact]
        public void ResumeCombatDecisionRestoresSkillThreeThroughTheOwnershipPath()
        {
            // The controller hands back control with the production ResumeCombat directive and
            // MovementSystem restores the attack hold on the following combat tick.
            var path = new List<Waypoint>
            {
                new(5000, 5000, MovementPrecision.Medium, BotMode.MoveAndAttack)
            };
            var movement = new RecordingMovementSystem(CreateStubMemory(), _ => { }, path);
            try
            {
                typeof(MovementSystem).GetField("_isSkillThreeHeld", PrivateInstance)!
                    .SetValue(movement, true);
                typeof(MovementSystem).GetField("_isMovingForward", PrivateInstance)!
                    .SetValue(movement, true);
                SetGroupingState(movement, MobGroupingState.GroupingMove);

                InvokePrivate(movement, "ApplyMobGroupingDecision",
                    new MobGroupingDecision(MobGroupingDirective.StartMove, MobGroupingDirection.North, 1),
                    Utc());
                Assert.False(movement.IsAttackKeyHeld);
                Assert.Equal(1, movement.SkillThreeUps);
                Assert.False(IsMovingForward(movement));

                // A failed gather finishes with ResumeCombat (never with StopMovement).
                SetGroupingState(movement, MobGroupingState.ResumeCombat);
                InvokePrivate(movement, "ApplyMobGroupingDecision",
                    new MobGroupingDecision(MobGroupingDirective.ResumeCombat, MoveNumber: 1,
                        Reason: MobGroupingReason.Describe(
                            MobGroupingReason.NoFormationImprovement, "no improvement"),
                        Code: MobGroupingReason.NoFormationImprovement),
                    Utc(1200));

                Assert.False(CombatSuspended(movement));
                Assert.False(movement.IsAttackKeyHeld);
                InvokePrivate(movement, "HoldSkillThree");
                Assert.True(movement.IsAttackKeyHeld);
                Assert.Equal(1, movement.SkillThreeDowns);
            }
            finally
            {
                movement.DisposeMobGroupingDetector();
            }
        }

        // ── Cancellation / ownership ───────────────────────────────────────────

        [Fact]
        public void LootOwnershipCancelsGroupingImmediately()
        {
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            Encounter encounter = StartGather(controller, lines);

            encounter.LootBarrier = true;
            MobGroupingDecision cancelled = encounter.Tick();

            Assert.Equal(MobGroupingDirective.Cancelled, cancelled.Directive);
            Assert.Equal(MobGroupingReason.LootOwnership, cancelled.Code);
            Assert.Equal(MobGroupingController.LootBarrierBlockReason, cancelled.Reason);
            Assert.False(controller.IsSequenceActive);
            Assert.Equal(MobGroupingState.Cooldown, controller.State);
            Assert.Contains(lines, line => line.Contains(
                "grouping cancelled", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(BotMode.MoveAndAttack)]
        [InlineData(BotMode.MoveAndAttackAndLoot)]
        public void RepotRecoveryPauseStopAndDeathCancellationRemainCorrect(BotMode mode)
        {
            var scenarios = new (string Name, Action<Encounter> Apply, string Code)[]
            {
                ("pause", e => e.Paused = true, MobGroupingReason.PauseOwnership),
                ("stop", e => e.Stopping = true, MobGroupingReason.PauseOwnership),
                ("repot", e => e.Transition = true, MobGroupingReason.TransitionOwnership),
                ("recovery", e => e.Recovery = true, MobGroupingReason.RecoveryOwnership),
                ("loot", e => e.LootHold = true, MobGroupingReason.LootOwnership),
                ("death", e => e.Combat = false, MobGroupingReason.CombatContextLost)
            };

            foreach (var scenario in scenarios)
            {
                var controller = new MobGroupingController();
                Encounter encounter = StartGather(controller, null, mode);
                scenario.Apply(encounter);

                MobGroupingDecision cancelled = encounter.Tick();
                Assert.True(cancelled.Directive == MobGroupingDirective.Cancelled,
                    $"{scenario.Name} must cancel the gather");
                Assert.Equal(scenario.Code, cancelled.Code);
                Assert.False(controller.IsSequenceActive);
            }
        }

        [Fact]
        public void UnfocusedGameWindowBothBlocksAndCancelsGrouping()
        {
            var controller = new MobGroupingController();
            var encounter = new Encounter(controller, ScatteredPoints) { Window = false };
            AssertNeverStartsGathering(encounter, 15);

            Encounter active = StartGather(new MobGroupingController());
            active.Window = false;
            Assert.Equal(MobGroupingDirective.Cancelled, active.Tick().Directive);
        }

        [Fact]
        public void DisabledGroupingTogglePreventsAllGroupingMovement()
        {
            var controller = new MobGroupingController();
            var encounter = new Encounter(controller, ScatteredPoints) { Enabled = false };
            AssertNeverStartsGathering(encounter, 20);
            Assert.Equal(MobGroupingState.Idle, controller.State);

            // Disabling during an active gather cancels it.
            Encounter active = StartGather(new MobGroupingController());
            active.Enabled = false;
            Assert.Equal(MobGroupingDirective.Cancelled, active.Tick().Directive);
        }

        [Fact]
        public void OnlyMoveNeverInvokesGrouping()
        {
            var controller = new MobGroupingController();
            var encounter = new Encounter(controller, ScatteredPoints) { Mode = BotMode.OnlyMove };
            AssertNeverStartsGathering(encounter, 20);
            Assert.NotEqual(MobGroupingState.GroupingMove, controller.State);

            Encounter active = StartGather(new MobGroupingController());
            active.Mode = BotMode.OnlyMove;
            MobGroupingDecision cancelled = active.Tick();
            Assert.Equal(MobGroupingDirective.Cancelled, cancelled.Directive);
            Assert.Equal(MobGroupingReason.ModeUnsupported, cancelled.Code);
        }

        [Theory]
        [InlineData(BotMode.MoveAndAttack)]
        [InlineData(BotMode.MoveAndAttackAndLoot)]
        public void BothSupportedCombatModesBehaveConsistently(BotMode mode)
        {
            var controller = new MobGroupingController();
            var encounter = new Encounter(controller, ScatteredPoints) { Mode = mode };

            MobGroupingDecision started = encounter.RunUntilStartMove();
            Assert.Equal(MobGroupingDirective.StartMove, started.Directive);
            Assert.Equal(1, controller.GroupingsThisEncounter);

            encounter.Convergence = 1f;
            encounter.Tick(3);
            Assert.Equal(MobGroupingState.ResumeCombat, controller.State);
        }

        [Fact]
        public void MovingMarkersWithNoConvergenceNeverClaimConfirmedConvergence()
        {
            foreach (BotMode mode in new[] { BotMode.MoveAndAttack, BotMode.MoveAndAttackAndLoot })
            {
                var controller = new MobGroupingController();
                var encounter = new Encounter(controller, ScatteredPoints)
                {
                    Mode = mode,
                    Convergence = 1f
                };

                for (int i = 0; i < 20; i++)
                {
                    MobGroupingDecision decision = encounter.Tick();
                    if (decision.Directive == MobGroupingDirective.StartMove)
                        Assert.NotEqual(MobGroupingReason.ConvergenceConfirmed, decision.Code);
                }

                // Still exactly one bounded attempt per encounter, never a start/cancel cycle.
                Assert.True(controller.GroupingsThisEncounter <= 1);
            }
        }

        [Fact]
        public void LootHoldAndBarrierBlockNewGathersWithoutConsumingAttempts()
        {
            var lootHold = new Encounter(new MobGroupingController(), ScatteredPoints) { LootHold = true };
            AssertNeverStartsGathering(lootHold, 20);

            var barrier = new Encounter(new MobGroupingController(), ScatteredPoints)
            {
                Mode = BotMode.MoveAndAttackAndLoot,
                LootBarrier = true
            };
            AssertNeverStartsGathering(barrier, 20);

            Assert.Equal(0, lootHold.Controller.GroupingsThisEncounter);
            Assert.Equal(0, barrier.Controller.GroupingsThisEncounter);
            Assert.Equal(0, lootHold.Controller.MovesStarted);
            Assert.Equal(0, barrier.Controller.MovesStarted);
        }

        // ── Determinism / bounded work ─────────────────────────────────────────

        [Fact]
        public void AlgorithmRemainsDeterministicWithSyntheticObservationSequences()
        {
            List<string> first = RunScenario();
            List<string> second = RunScenario();

            Assert.Equal(first, second);
        }

        private static List<string> RunScenario()
        {
            var results = new List<string>();
            var controller = new MobGroupingController();
            var encounter = new Encounter(controller, ScatteredPoints);

            for (int i = 0; i < 20; i++)
            {
                MobGroupingDecision decision = encounter.Tick();
                MobGroupAnalysis analysis = encounter.CurrentAnalysis();
                results.Add($"{decision.Directive}|{decision.Code}|" +
                            $"{analysis.BestLocalClusterSize}|" +
                            $"{analysis.BestPairDistanceNormalized:F4}|" +
                            $"{analysis.FormationQuality:F4}|" +
                            $"{controller.LastTrend.Trend}|{controller.State}");
            }
            return results;
        }

        [Fact]
        public void ScatteredFormationCannotCauseEndlessStartCancelRestartCycling()
        {
            var controller = new MobGroupingController();
            var encounter = new Encounter(controller, ScatteredPoints);

            for (int i = 0; i < 200; i++)
            {
                encounter.Tick();
                if (controller.State == MobGroupingState.ResumeCombat)
                    controller.CompleteResume(encounter.Now);
            }

            Assert.True(encounter.Decisions.Count(decision =>
                decision.Directive == MobGroupingDirective.StartMove) <=
                BotConstants.MobGrouping.MaxGroupingsPerEncounter);
            Assert.True(controller.GroupingsThisEncounter <=
                        BotConstants.MobGrouping.MaxGroupingsPerEncounter);
        }

        // ── Analyzer: local cluster assessment ─────────────────────────────────

        [Fact]
        public void AnalyzerFindsTheMostCompactLocalPairAndItsNeighborhood()
        {
            MobGroupAnalysis analysis = Analyze(ScatteredPoints);
            // The compact neighborhood is deliberately tight (2r), so a pair 100 px apart has
            // no compact cluster around its midpoint - the formation is genuinely scattered.
            Assert.Equal(0, analysis.BestLocalClusterSize);
            Assert.Equal(100f, analysis.BestPairDistancePx, 1);
            Assert.Equal(100f / analysis.EffectiveMarkerRadius,
                analysis.BestPairDistanceNormalized, 3);
            Assert.False(analysis.HasCompactLocalGroup);

            MobGroupAnalysis compact = Analyze(ThreeCompactPlusOutlier);
            Assert.True(compact.HasCompactLocalGroup);
            Assert.Equal(3, compact.BestLocalClusterSize);
        }

        [Fact]
        public void AnalyzerNormalizesCompactPairDistanceByMarkerRadius()
        {
            // 32 px separation: a stacked pair at a 36 px marker radius (0.89r), clearly NOT
            // stacked at 18 px (1.78r) once the compact threshold is 1.25r.
            MobPoint[] markers = { new(84, 60), new(116, 60), new(100, 20) };

            Assert.True(AnalyzeWithRadius(36f, markers).HasCompactLocalGroup);
            Assert.False(AnalyzeWithRadius(18f, markers).HasCompactLocalGroup);
        }

        [Fact]
        public void AnalyzerIgnoresOutliersWhenJudgingTheLocalGroup()
        {
            // Adding remote outliers must not remove an existing compact local group.
            MobGroupAnalysis withoutOutlier = Analyze(new MobPoint(95, 60), new MobPoint(105, 60));
            MobGroupAnalysis withOutliers = Analyze(TwoCompactPlusOutliers);

            Assert.True(withoutOutlier.HasCompactLocalGroup);
            Assert.True(withOutliers.HasCompactLocalGroup);
            Assert.Equal(2, withOutliers.BestLocalClusterSize);
        }

        [Fact]
        public void AnalyzerRejectsNonFiniteMarkersBeforeClusterAnalysis()
        {
            var markers = new List<MobMarker>
            {
                new(new MobPoint(float.NaN, 60), 18f, 1f, MobMarkerSource.CyanCenter),
                new(new MobPoint(95, 60), 18f, 1f, MobMarkerSource.CyanCenter),
                new(new MobPoint(105, 60), 18f, 1f, MobMarkerSource.CyanCenter),
                new(new MobPoint(100, 60), 18f, 0f, MobMarkerSource.CyanCenter)
            };

            MobGroupAnalysis analysis = AnalyzeMarkers(markers.ToArray());
            Assert.Equal(2, analysis.DetectedMobCount);
            Assert.True(analysis.HasCompactLocalGroup);
            // Both remaining markers are reliable, and a pair is enough for a movement decision
            // now that MinimumGatherMarkers == 2.
            Assert.True(analysis.IsMovementSampleReliable);
        }

        [Fact]
        public void CompactLocalGroupRequiresVisiblyStackedMobsNotJustNearbyOnes()
        {
            // 45 px apart: exactly what the snapshots showed for two mobs in melee. That is NOT
            // "already grouped" (the old 2.75r threshold said it was and blocked every gather).
            Assert.False(Analyze(new MobPoint(78, 60), new MobPoint(123, 60)).HasCompactLocalGroup);
            // 1.0r apart: visibly stacked, nothing left to gather.
            Assert.True(Analyze(new MobPoint(91, 60), new MobPoint(109, 60)).HasCompactLocalGroup);
        }

        [Fact]
        public void GatherSuccessRequiresACompactLocalGroupAndIsNotAMarkerCount()
        {
            Assert.True(MobGroupingController.IsGatheringComplete(Analyze(ClusteredPoints)));
            Assert.True(MobGroupingController.IsGatheringComplete(Analyze(AttackReadyClusterWithOutliers)));
            Assert.False(MobGroupingController.IsGatheringComplete(Analyze(ScatteredPoints)));
            Assert.False(MobGroupingController.IsGatheringComplete(Analyze()));
            Assert.False(MobGroupingController.IsGatheringComplete(
                Analyze(ScatteredPoints.Take(1).ToArray())));
        }

        [Fact]
        public void GatherCandidateRequiresScatterAndNoCompactLocalGroup()
        {
            Assert.True(MobGroupingController.IsGatherCandidate(Analyze(ScatteredPoints)));
            Assert.False(MobGroupingController.IsGatherCandidate(Analyze(ClusteredPoints)));
            Assert.False(MobGroupingController.IsGatherCandidate(Analyze(ThreeCompactPlusOutlier)));
            // Two scattered markers are a valid candidate; only two COMPACT ones are not.
            Assert.True(MobGroupingController.IsGatherCandidate(Analyze(TwoDistant)));
            Assert.False(MobGroupingController.IsGatherCandidate(
                Analyze(new MobPoint(95, 60), new MobPoint(105, 60))));
        }

        // ── Temporal trend (pure, bounded) ─────────────────────────────────────

        [Fact]
        public void FormationHistoryRequiresTwoConsecutiveImprovementSteps()
        {
            var history = new MobFormationHistory();
            AddObservation(history, 0, 0.10f);
            AddObservation(history, 225, 0.13f);

            Assert.Equal(MobFormationTrend.Unknown, history.Evaluate(Utc(225)).Trend);

            AddObservation(history, 450, 0.16f);
            MobFormationTrendResult trend = history.Evaluate(Utc(450));
            Assert.Equal(MobFormationTrend.Improving, trend.Trend);
            Assert.Equal(3, trend.ComparableObservations);
            Assert.True(trend.RelativeChange > 0.10f);
        }

        [Fact]
        public void FormationHistoryReportsStagnationAndDeterioration()
        {
            var stagnant = new MobFormationHistory();
            AddObservation(stagnant, 0, 0.20f);
            AddObservation(stagnant, 225, 0.20f);
            AddObservation(stagnant, 450, 0.20f);
            Assert.Equal(MobFormationTrend.Stagnant, stagnant.Evaluate(Utc(450)).Trend);

            var worsening = new MobFormationHistory();
            AddObservation(worsening, 0, 0.20f);
            AddObservation(worsening, 225, 0.18f);
            AddObservation(worsening, 450, 0.15f);
            Assert.Equal(MobFormationTrend.Deteriorating, worsening.Evaluate(Utc(450)).Trend);
        }

        [Fact]
        public void FormationHistoryInvalidatesAcrossCameraChanges()
        {
            var history = new MobFormationHistory();
            AddObservation(history, 0, 0.10f);
            AddObservation(history, 225, 0.13f);
            AddObservation(history, 450, 0.16f, cameraYawDegrees: 130f);

            MobFormationTrendResult trend = history.Evaluate(Utc(450));
            Assert.Equal(MobFormationTrend.Unknown, trend.Trend);
        }

        [Fact]
        public void FormationHistoryInvalidatesAcrossClientSizeChanges()
        {
            var history = new MobFormationHistory();
            AddObservation(history, 0, 0.10f);
            AddObservation(history, 225, 0.13f);
            AddObservation(history, 450, 0.16f, clientHeight: 1080);

            Assert.Equal(MobFormationTrend.Unknown, history.Evaluate(Utc(450)).Trend);
        }

        [Fact]
        public void FormationHistoryNeverTreatsAMarkerCountDecreaseAsImprovement()
        {
            var history = new MobFormationHistory();
            AddObservation(history, 0, 0.10f, markerCount: 4);
            AddObservation(history, 225, 0.30f, markerCount: 3);
            AddObservation(history, 450, 0.45f, markerCount: 2);

            Assert.NotEqual(MobFormationTrend.Improving, history.Evaluate(Utc(450)).Trend);
        }

        [Fact]
        public void FormationHistoryIsBounded()
        {
            var history = new MobFormationHistory();
            for (int i = 0; i < 100; i++)
                AddObservation(history, i * 100, 0.1f + i * 0.001f);

            Assert.True(history.Count <= BotConstants.MobGrouping.FormationHistoryMaxObservations);
        }

        [Fact]
        public void FormationHistoryIgnoresUnreliableSamples()
        {
            var history = new MobFormationHistory();
            history.Add(new MobFormationObservation(Utc(0), true, 0.10f, 3, 1f, true, 0f, 0f,
                true, 90f, 1400, 900));
            history.Add(new MobFormationObservation(Utc(225), true, 0.14f, 3, 1f, true, 0f, 0f,
                true, 90f, 1400, 900));
            history.Add(new MobFormationObservation(Utc(450), false, 0f, 0, 0f, true, 0f, 0f,
                true, 90f, 1400, 900));

            Assert.Equal(MobFormationTrend.Unknown, history.Evaluate(Utc(450)).Trend);
        }

        private static void AddObservation(
            MobFormationHistory history,
            int milliseconds,
            float quality,
            int markerCount = 3,
            float cameraYawDegrees = 90f,
            int clientHeight = 900)
        {
            history.Add(new MobFormationObservation(
                Utc(milliseconds), true, quality, markerCount, 1f, true, 0f, 0f,
                true, cameraYawDegrees, 1400, clientHeight));
        }

        // ── Input / state transition plumbing ─────────────────────────────────

        [Fact]
        public void Disabled_NeverGroups()
        {
            var controller = new MobGroupingController();
            MobGroupingDecision decision = controller.Tick(Input(
                Utc(), Analyze(ScatteredPoints), sample: true, enabled: false));

            Assert.Equal(MobGroupingDirective.None, decision.Directive);
            Assert.Equal(MobGroupingState.Idle, controller.State);
        }

        [Fact]
        public void BelowMinimumMobCount_NeverGroups()
        {
            var controller = new MobGroupingController();
            for (int i = 0; i < 8; i++)
            {
                MobGroupingDecision decision = controller.Tick(Input(
                    Utc(i * 225), Analyze(new MobPoint(100, 60)), sample: true));
                Assert.NotEqual(MobGroupingDirective.StartMove, decision.Directive);
            }
        }

        [Fact]
        public void ThreeTightlyGroupedMobs_NeverGroups()
        {
            var controller = new MobGroupingController();
            var encounter = new Encounter(controller, ClusteredPoints) { Convergence = 1f };
            AssertNeverStartsGathering(encounter, 15);
        }

        [Fact]
        public void ScatteredFormationShorterThanPersistence_DoesNotTrigger()
        {
            var controller = new MobGroupingController();
            var encounter = new Encounter(controller, ScatteredPoints);
            encounter.Tick(2);

            Assert.Equal(MobGroupingState.Observing, controller.State);
            Assert.Equal(0, controller.GroupingsThisEncounter);
        }

        [Fact]
        public void GatherDoesNotStartWithoutNavigationTarget()
        {
            var controller = new MobGroupingController();
            var encounter = new Encounter(controller, ScatteredPoints) { GatherNav = false };
            AssertNeverStartsGathering(encounter, 20);
        }

        [Fact]
        public void CooldownBlocksRetrigger()
        {
            var controller = new MobGroupingController();
            Encounter encounter = StartGather(controller);
            encounter.Convergence = 1f;
            encounter.Tick(3);
            controller.CompleteResume(encounter.Now);

            MobGroupingDecision decision = controller.Tick(Input(
                encounter.Now.AddMilliseconds(BotConstants.MobGrouping.CooldownMs - 1),
                Analyze(ScatteredPoints), sample: true, playerX: 30f, playerY: 30f));

            Assert.Equal(MobGroupingDirective.None, decision.Directive);
            Assert.Equal(MobGroupingState.Cooldown, controller.State);
        }

        [Fact]
        public void NoStaleRescanOrGatherWorkAfterTheGatherCompletes()
        {
            var controller = new MobGroupingController();
            Encounter encounter = StartGather(controller);
            encounter.Convergence = 1f;
            encounter.Tick(3);
            Assert.Equal(MobGroupingState.ResumeCombat, controller.State);

            controller.CompleteResume(encounter.Now);
            Assert.Equal(MobGroupingState.Cooldown, controller.State);
            Assert.False(controller.NeedsGatherRescan(encounter.Now));
            Assert.False(controller.NeedsGatherRescan(encounter.Now.AddMilliseconds(
                BotConstants.MobGrouping.GatherRescanIntervalMs)));

            MobGroupingDecision late = controller.Tick(Input(
                encounter.Now.AddMilliseconds(600), Analyze(ScatteredPoints), sample: true));
            Assert.Equal(MobGroupingDirective.None, late.Directive);
            Assert.Equal(MobGroupingState.Cooldown, controller.State);
        }

        [Fact]
        public void CancellingGatherStopsTheRescanLoopWithoutResumingThroughGather()
        {
            var controller = new MobGroupingController();
            Encounter encounter = StartGather(controller);
            DateTime cancelAt = encounter.Now.AddMilliseconds(100);

            MobGroupingDecision cancelled = controller.Cancel(cancelAt, "test cancel");

            Assert.Equal(MobGroupingDirective.Cancelled, cancelled.Directive);
            Assert.Equal(MobGroupingState.Cooldown, controller.State);
            Assert.False(controller.NeedsGatherRescan(cancelAt));
            Assert.False(controller.NeedsGatherRescan(cancelAt.AddMilliseconds(500)));
        }

        [Fact]
        public void ActiveGroupingCancelsImmediatelyWhenCombatContextEnds()
        {
            var controller = new MobGroupingController();
            Encounter encounter = StartGather(controller);

            encounter.Combat = false;
            MobGroupingDecision cancelled = encounter.Tick();

            Assert.Equal(MobGroupingDirective.Cancelled, cancelled.Directive);
            Assert.Equal("combat context ended", cancelled.Reason);
            Assert.Equal(MobGroupingReason.CombatContextLost, cancelled.Code);
            Assert.Equal(MobGroupingState.Cooldown, controller.State);
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
            SetGroupingsThisEncounter(controller, 1);

            DateTime modeStart = Utc();
            controller.Tick(Input(modeStart, mode: BotMode.OnlyMove, combat: false));
            controller.Tick(Input(modeStart.AddMilliseconds(
                BotConstants.MobGrouping.EncounterQuietResetMs), mode: BotMode.OnlyMove, combat: false));
            Assert.Equal(0, controller.GroupingsThisEncounter);

            // Combat returns and re-arms the lifecycle before the disabled scenario.
            controller.Tick(Input(modeStart.AddSeconds(5), combat: true));
            SetGroupingsThisEncounter(controller, 1);

            DateTime disabledStart = modeStart.AddSeconds(10);
            controller.Tick(Input(disabledStart, enabled: false, combat: false));
            controller.Tick(Input(disabledStart.AddMilliseconds(
                BotConstants.MobGrouping.EncounterQuietResetMs), enabled: false, combat: false));
            Assert.Equal(0, controller.GroupingsThisEncounter);
        }

        [Fact]
        public void SuppressionLoggingIsEventDrivenNotPerObservation()
        {
            // A stationary player with no mob approach direction has no target at all, so the
            // refusal is logged exactly once (event-driven), never once per observation.
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            var encounter = new Encounter(controller, TwoDistant)
            {
                Convergence = 1f,
                StepX = 0f,
                StepY = 0f
            };

            encounter.Tick(30);

            Assert.Single(lines, line =>
                line.Contains("gather not started", StringComparison.Ordinal));
        }

        [Fact]
        public void GatherStartLogRecordsEveryDiagnosticField()
        {
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            var encounter = new Encounter(controller, ScatteredPoints);
            encounter.RunUntilStartMove();

            string started = Assert.Single(lines, line => line.Contains("gather started"));
            Assert.Contains("localCluster=", started);
            Assert.Contains("bestPair=", started);
            Assert.Contains("quality=", started);
            Assert.Contains("player=(", started);
            Assert.Contains("waypoint=(", started);
            Assert.Contains($"budget={BotConstants.MobGrouping.MaximumGatherDurationMs}ms", started);
            Assert.Contains($"rescan={BotConstants.MobGrouping.GatherRescanIntervalMs}ms", started);
            Assert.Contains("steering=KeyboardTurn", started);
            Assert.Contains("trend=", started);
        }

        [Fact]
        public void GatherFinishLogRecordsDurationDisplacementAndImprovement()
        {
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            Encounter encounter = StartGather(controller, lines);

            encounter.Convergence = 1f;
            encounter.Tick(3);

            string finished = Assert.Single(lines, line => line.Contains("gather finished"));
            Assert.Contains("duration=", finished);
            Assert.Contains("displacement=", finished);
            Assert.Contains("quality=", finished);
            Assert.Contains("improved=", finished);
            Assert.Contains("localCluster=", finished);
        }

        [Fact]
        public void GatherRescanLogIncludesLocalClusterAndTrend()
        {
            var lines = new List<string>();
            var controller = new MobGroupingController(lines.Add);
            Encounter encounter = StartGather(controller, lines);

            encounter.Tick();

            string rescan = Assert.Single(lines, line => line.Contains("gather rescan:"));
            Assert.Contains("mobs=3", rescan);
            Assert.Contains("localCluster=", rescan);
            Assert.Contains("bestPair=", rescan);
            Assert.Contains("quality=", rescan);
            Assert.Contains("trend=", rescan);
            Assert.Contains("displacement=", rescan);
        }

        [Fact]
        public void GatherNeverDequeuesOrAdvancesTheWaypoint()
        {
            var path = new List<Waypoint>
            {
                new(5000, 5000, MovementPrecision.Medium, BotMode.MoveAndAttack,
                    steeringMode: MovementSteeringMode.DirectCamera),
                new(6000, 6000, MovementPrecision.Medium, BotMode.MoveAndAttack,
                    steeringMode: MovementSteeringMode.DirectCamera)
            };
            MovementSystem movement = new(CreateStubMemory(), _ => { }, 0, 0, customPath: path);
            try
            {
                SetGroupingState(movement, MobGroupingState.GroupingMove);
                var queue = (Queue<Waypoint>)typeof(MovementSystem)
                    .GetField("_waypoints", PrivateInstance)!.GetValue(movement)!;
                Waypoint headBefore = queue.Peek();
                float headX = headBefore.X;
                float headY = headBefore.Y;
                int countBefore = queue.Count;

                // Keep W logically held so the gather step never synthesizes a keydown.
                typeof(MovementSystem).GetField("_isMovingForward", PrivateInstance)!
                    .SetValue(movement, true);
                InvokePrivate(movement, "ApplyGatherMovement", 0f, 0f);
                InvokePrivate(movement, "ApplyGatherMovement", 0.5f, 0.5f);

                Assert.Equal(headX, queue.Peek().X);
                Assert.Equal(headY, queue.Peek().Y);
                Assert.Equal(countBefore, queue.Count);
                Assert.False(movement.IsGoalReached);
            }
            finally
            {
                movement.DisposeMobGroupingDetector();
            }
        }

        [Fact]
        public void OnlyMoveNeverInvokesGroupingThroughTheMovementSystemGate()
        {
            Assert.False(MobGroupingController.IsSupportedCombatMode(BotMode.OnlyMove));
            Assert.True(MobGroupingController.IsSupportedCombatMode(BotMode.MoveAndAttack));
            Assert.True(MobGroupingController.IsSupportedCombatMode(BotMode.MoveAndAttackAndLoot));
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
        public void ExactlyFourMobsWithOneOutlierDoNotFormAnAttackReadyClusterAndNeverGather()
        {
            // Regression for the old behaviour: three clustered mobs plus one distant marker
            // used to trigger a gather walk because no four-mob subset existed. The local
            // cluster assessment now suppresses it: the compact trio is already the useful
            // local group and the outlier is never chased.
            MobPoint[] threeGroupedAndOutlier =
            {
                new(95, 60), new(100, 65), new(105, 60), new(0, 60)
            };
            MobGroupAnalysis analysis = Analyze(threeGroupedAndOutlier);

            Assert.Equal(4, analysis.DetectedMobCount);
            Assert.True(analysis.IsScattered);
            Assert.False(analysis.IsSufficientlyClustered);
            Assert.False(analysis.HasAttackReadyCluster);
            Assert.Equal(0, analysis.AttackReadyClusterMobCount);
            Assert.True(analysis.HasCompactLocalGroup);
            Assert.Equal(3, analysis.BestLocalClusterSize);

            var controller = new MobGroupingController();
            for (int i = 0; i < 12; i++)
            {
                MobGroupingDecision decision = controller.Tick(Input(
                    Utc(i * 225), analysis, sample: true, playerX: i * 0.3f, playerY: i * 0.3f));
                Assert.NotEqual(MobGroupingDirective.StartMove, decision.Directive);
            }
            Assert.Equal(0, controller.GroupingsThisEncounter);
            Assert.Equal(0, controller.MovesStarted);
        }

        [Fact]
        public void FiveMobsWithFourClusterAndOutlierAttackTheClusterInsteadOfGathering()
        {
            // The minimum >4 case: four tightly clustered mobs plus one distant outlier.
            // The exact subset search must be the only reason the scatter is tolerated.
            MobPoint[] fourClusterAndOutlier =
            {
                new(95, 60), new(100, 65), new(105, 60), new(100, 55), new(0, 60)
            };
            MobGroupAnalysis analysis = Analyze(fourClusterAndOutlier);

            Assert.Equal(5, analysis.DetectedMobCount);
            Assert.True(analysis.IsScattered);
            Assert.False(analysis.IsSufficientlyClustered);
            Assert.True(analysis.HasAttackReadyCluster);
            Assert.Equal(BotConstants.MobGrouping.AttackReadyClusterMobCount,
                analysis.AttackReadyClusterMobCount);

            var controller = new MobGroupingController();
            for (int i = 0; i < 10; i++)
            {
                MobGroupingDecision decision = controller.Tick(Input(
                    Utc(i * 225), analysis, sample: true));
                Assert.NotEqual(MobGroupingDirective.StartMove, decision.Directive);
            }
            Assert.Equal(0, controller.GroupingsThisEncounter);
        }

        [Fact]
        public void AttackReadyClusterAverageSpreadEqualityAtThresholdPasses()
        {
            // Four-subset spread is exactly the 18 px average success threshold
            // (and 18 px maximum, inside the 30 px maximum threshold).
            MobPoint[] formation =
            {
                new(82, 60), new(118, 60), new(100, 42), new(100, 78), new(0, 60)
            };
            MobGroupAnalysis analysis = Analyze(formation);

            Assert.True(analysis.HasAttackReadyCluster);
        }

        [Fact]
        public void AttackReadyClusterAverageSpreadJustBeyondThresholdFails()
        {
            // Same geometry with 18.5 px spread: the average threshold rejects the subset
            // while the maximum threshold would still accept it.
            MobPoint[] formation =
            {
                new(81.5f, 60), new(118.5f, 60), new(100, 41.5f), new(100, 78.5f), new(0, 60)
            };
            MobGroupAnalysis analysis = Analyze(formation);

            Assert.False(analysis.HasAttackReadyCluster);
            Assert.Equal(0, analysis.AttackReadyClusterMobCount);
        }

        [Fact]
        public void AttackReadyClusterMaximumSpreadEqualityAtThresholdPasses()
        {
            // Two mobs sit exactly 30 px from the subset centroid (the maximum success
            // threshold) while the other two sit 1 px away, so the average stays inside
            // its 18 px limit and only the maximum boundary is exercised.
            MobPoint[] formation =
            {
                new(130, 60), new(70, 60), new(99, 60), new(101, 60), new(0, 60)
            };
            MobGroupAnalysis analysis = Analyze(formation);

            Assert.True(analysis.HasAttackReadyCluster);
            Assert.Equal(BotConstants.MobGrouping.AttackReadyClusterMobCount,
                analysis.AttackReadyClusterMobCount);
        }

        [Fact]
        public void AttackReadyClusterMaximumSpreadJustBeyondThresholdFails()
        {
            // Same geometry with 30.5 px maximum spread: just beyond the maximum
            // threshold while the average is still inside its own limit.
            MobPoint[] formation =
            {
                new(130.5f, 60), new(69.5f, 60), new(99, 60), new(101, 60), new(0, 60)
            };
            MobGroupAnalysis analysis = Analyze(formation);

            Assert.False(analysis.HasAttackReadyCluster);
        }

        [Fact]
        public void AttackReadyClusterScalesSuccessThresholdsBySubsetAverageMarkerRadius()
        {
            // Four mobs sit 36 px from their local centroid: beyond the unscaled 18 px
            // average-success threshold, but inside the thresholds scaled by a 36 px
            // marker radius (36 / 18 = 2 -> 36 px average, 60 px maximum).
            MobPoint[] geometry =
            {
                new(64, 60), new(136, 60), new(100, 24), new(100, 96), new(0, 60)
            };

            Assert.True(AnalyzeWithRadius(36f, geometry).HasAttackReadyCluster);
            Assert.False(AnalyzeWithRadius(18f, geometry).HasAttackReadyCluster);

            // The scale uses the SUBSET AVERAGE radius, not the minimum: three 36 px
            // markers and one 18 px marker average to 31.5 px (scale 1.75 -> 31.5 px
            // average threshold), which still accepts a 25 px spread even though the
            // minimum-radius scale (1.0 -> 18 px) would not.
            var mixed = new[]
            {
                new MobMarker(new MobPoint(75, 60), 36f, 1f, MobMarkerSource.CyanCenter),
                new MobMarker(new MobPoint(125, 60), 36f, 1f, MobMarkerSource.CyanCenter),
                new MobMarker(new MobPoint(100, 35), 36f, 1f, MobMarkerSource.CyanCenter),
                new MobMarker(new MobPoint(100, 85), 18f, 1f, MobMarkerSource.CyanCenter),
                new MobMarker(new MobPoint(0, 60), 18f, 1f, MobMarkerSource.CyanCenter)
            };
            Assert.True(AnalyzeMarkers(mixed).HasAttackReadyCluster);
        }

        [Fact]
        public void GatherNavigationTargetUsesLiveWaypointAndWaypointDirectCameraSteering()
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
        public void GatherNavigationTargetUsesKeyboardTurnWaypointOverride()
        {
            var path = new List<Waypoint>
            {
                new(5000, 5000, MovementPrecision.Medium, BotMode.MoveAndAttack,
                    steeringMode: MovementSteeringMode.KeyboardTurn)
            };
            MovementSystem movement = new(CreateStubMemory(), _ => { }, 0, 0,
                customPath: path, steeringMode: MovementSteeringMode.DirectCamera);

            Assert.True(movement.TryGetGatherNavigationTarget(0, 0,
                out float targetX, out float targetY, out MovementSteeringMode steering));
            Assert.Equal(5000f, targetX);
            Assert.Equal(5000f, targetY);
            Assert.Equal(MovementSteeringMode.KeyboardTurn, steering);
            movement.DisposeMobGroupingDetector();
        }

        [Theory]
        [InlineData(null, MovementSteeringMode.DirectCamera, MovementSteeringMode.DirectCamera)]
        [InlineData(null, MovementSteeringMode.KeyboardTurn, MovementSteeringMode.KeyboardTurn)]
        public void GatherNavigationTargetFallsBackToMovementSystemDefaultSteering(
            MovementSteeringMode? waypointMode,
            MovementSteeringMode runnerDefault,
            MovementSteeringMode expected)
        {
            var path = new List<Waypoint>
            {
                new(5000, 5000, MovementPrecision.Medium, BotMode.MoveAndAttack,
                    steeringMode: waypointMode)
            };
            MovementSystem movement = new(CreateStubMemory(), _ => { }, 0, 0,
                customPath: path, steeringMode: runnerDefault);

            Assert.True(movement.TryGetGatherNavigationTarget(0, 0,
                out _, out _, out MovementSteeringMode steering));
            Assert.Equal(expected, steering);
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
        public void GatherNavigationTargetRejectsEmptyWaypointQueueWithoutFinalStandby()
        {
            // No queued waypoint and no final-waypoint standby: there is no positional
            // navigation target, so gather must return false instead of inventing one.
            MovementSystem movement = new(CreateStubMemory(), _ => { }, 0, 0,
                customPath: new List<Waypoint>());

            Assert.False(movement.IsFinalStandbyActive);
            Assert.False(movement.CanGatherTowardCurrentNavigationTarget(0, 0));
            Assert.False(movement.TryGetGatherNavigationTarget(0, 0,
                out _, out _, out _));
            movement.DisposeMobGroupingDetector();
        }

        [Fact]
        public void GatherWaypointBearingIsRecomputedFromLivePlayerPosition()
        {
            var path = new List<Waypoint>
            {
                new(100, 100, MovementPrecision.Medium, BotMode.MoveAndAttack,
                    steeringMode: MovementSteeringMode.DirectCamera)
            };
            MovementSystem movement = new(CreateStubMemory(), _ => { }, 0, 0, customPath: path);
            try
            {
                // The same waypoint resolves from both live player positions.
                Assert.True(movement.TryGetGatherNavigationTarget(0, 0,
                    out float firstTargetX, out float firstTargetY, out _));
                Assert.True(movement.TryGetGatherNavigationTarget(60, 10,
                    out float laterTargetX, out float laterTargetY, out _));
                Assert.Equal(firstTargetX, laterTargetX);
                Assert.Equal(firstTargetY, laterTargetY);
                Assert.Equal(100f, firstTargetX);
                Assert.Equal(100f, firstTargetY);

                // The pure production bearing used by MoveTowards follows the player.
                Assert.Equal(45f, MovementSystem.ComputeMoveTowardsBearingDeg(
                    0, 0, firstTargetX, firstTargetY), 3);
                float expectedLater = GeometryUtils.GetBearingToTargetDeg(60, 10, 100, 100);
                Assert.Equal(expectedLater, MovementSystem.ComputeMoveTowardsBearingDeg(
                    60, 10, laterTargetX, laterTargetY), 3);
                Assert.NotEqual(
                    MovementSystem.ComputeMoveTowardsBearingDeg(0, 0, firstTargetX, firstTargetY),
                    MovementSystem.ComputeMoveTowardsBearingDeg(60, 10, laterTargetX, laterTargetY));

                // The real gather step reuses that calculation: the applied bearing must
                // change with the player's position for the SAME waypoint.
                typeof(MovementSystem).GetField("_isMovingForward",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(movement, true);
                MethodInfo applyGather = typeof(MovementSystem).GetMethod("ApplyGatherMovement",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                FieldInfo appliedBearing = typeof(MovementSystem).GetField("_lastSetBearingDeg",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;

                Assert.True((bool)applyGather.Invoke(movement, new object[] { 0f, 0f })!);
                Assert.Equal(45f, (float)appliedBearing.GetValue(movement)!, 3);

                typeof(MovementSystem).GetField("_isMovingForward",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(movement, true);
                Assert.True((bool)applyGather.Invoke(movement, new object[] { 60f, 10f })!);
                Assert.Equal(expectedLater, (float)appliedBearing.GetValue(movement)!, 3);
            }
            finally
            {
                movement.DisposeMobGroupingDetector();
            }
        }

        [Fact]
        public void RecommendedEscapeDirectionCannotControlGatherMovement()
        {
            // Same geometry, player position, waypoint and steering; only the analysis
            // direction differs. Gather target and applied waypoint bearing must be
            // identical or the direction has been reconnected to movement.
            (float TargetX, float TargetY, MovementSteeringMode Steering, float AppliedBearing) north =
                CaptureGatherMovement(MobGroupingDirection.North);
            (float TargetX, float TargetY, MovementSteeringMode Steering, float AppliedBearing) southWest =
                CaptureGatherMovement(MobGroupingDirection.SouthWest);

            Assert.Equal(north.TargetX, southWest.TargetX);
            Assert.Equal(north.TargetY, southWest.TargetY);
            Assert.Equal(north.Steering, southWest.Steering);
            Assert.Equal(north.AppliedBearing, southWest.AppliedBearing);
            Assert.Equal(45f, north.AppliedBearing, 3);
        }

        private static (float TargetX, float TargetY, MovementSteeringMode Steering, float AppliedBearing)
            CaptureGatherMovement(MobGroupingDirection direction)
        {
            // The gather is started through the production decision path (converging
            // formation + route-compatible movement), then only the diagnostic direction
            // carried by the decision is varied.
            var encounter = new Encounter(new MobGroupingController(), ScatteredPoints);
            Assert.Equal(MobGroupingDirective.StartMove,
                encounter.RunUntilStartMove().Directive);
            var decision = new MobGroupingDecision(
                MobGroupingDirective.StartMove, direction, 1);

            var path = new List<Waypoint>
            {
                new(5000, 5000, MovementPrecision.Medium, BotMode.MoveAndAttack,
                    steeringMode: MovementSteeringMode.DirectCamera)
            };
            MovementSystem movement = new(CreateStubMemory(), _ => { }, 0, 0, customPath: path);
            try
            {
                Assert.True(movement.TryGetGatherNavigationTarget(10, 10,
                    out float targetX, out float targetY, out MovementSteeringMode steering));

                MethodInfo applyDecision = typeof(MovementSystem).GetMethod("ApplyMobGroupingDecision",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                applyDecision.Invoke(movement, new object[] { decision, Utc() });

                // Keep W logically held so the measurement does not synthesize a keydown.
                typeof(MovementSystem).GetField("_isMovingForward",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(movement, true);
                MethodInfo applyGather = typeof(MovementSystem).GetMethod("ApplyGatherMovement",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                Assert.True((bool)applyGather.Invoke(movement, new object[] { 10f, 10f })!);
                float appliedBearing = (float)typeof(MovementSystem)
                    .GetField("_lastSetBearingDeg", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(movement)!;

                return (targetX, targetY, steering, appliedBearing);
            }
            finally
            {
                movement.DisposeMobGroupingDetector();
            }
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
        public void CandidateDedupeMergesTheSameMarkerDetectedTwiceButKeepsRealMobs()
        {
            // Regression for the analysed snapshots: one marker produced a cyan-center and a
            // Hough-ring candidate 6-15 px apart, which the analyzer then reported as a
            // "compact pair" and every gather was refused with CompactLocalGroup.
            float radius = BotConstants.MobGrouping.ExpectedMarkerRadiusPx;
            var candidates = new[]
            {
                new MobMarker(new MobPoint(50, 50), radius, 1f, MobMarkerSource.CyanCenter),
                new MobMarker(new MobPoint(56, 53), radius, 0.80f, MobMarkerSource.MagentaRing),
                // A genuinely different mob: 37 px away (observed melee spacing in a live fight).
                new MobMarker(new MobPoint(87, 50), radius, 0.80f, MobMarkerSource.MagentaRing)
            };

            IReadOnlyList<MobMarker> unique = MobMarkerDetector.DeduplicateCandidates(candidates, radius);

            Assert.Equal(2, unique.Count);
            Assert.Equal(MobMarkerSource.CyanCenter, unique[0].Source);
            Assert.Equal(87, unique[1].Center.X);
        }

        [Fact]
        public void DetectorRejectsSparseMagicArcWithoutCyanCenter()
        {
            // 170 degrees of ring (≈47% coverage) used to pass the ring-only path. Spell bursts
            // and decorative glyphs look exactly like that; a real mob ring is a full circle.
            using var detector = new MobMarkerDetector();
            using var frame = NewFrame(180, 100);
            float radius = BotConstants.MobGrouping.ExpectedMarkerRadiusPx;
            using (Graphics graphics = Graphics.FromImage(frame))
            using (var pen = new Pen(Color.Magenta, 2f))
                graphics.DrawArc(pen, 90 - radius, 50 - radius, radius * 2, radius * 2, 0, 170);

            MobMarker[] markers = detector.DetectBitmapForTesting(frame);

            Assert.DoesNotContain(markers, marker =>
                marker.Source == MobMarkerSource.MagentaRing &&
                Math.Abs(marker.Center.X - 90) <= 3 && Math.Abs(marker.Center.Y - 50) <= 3);
        }

        [Fact]
        public void MobApproachTargetMapsTheScreenDirectionThroughCameraYaw()
        {
            // The standstill gather walks toward the detected mob cluster: the analysis gives a
            // screen-space direction, the camera yaw turns it into an absolute world bearing.
            MovementSystem movement = CreateMovementSystem();
            try
            {
                FieldInfo analysisField = typeof(MovementSystem).GetField("_lastMobGroupingAnalysis",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                FieldInfo hasYawField = typeof(MovementSystem).GetField("_lastMobGroupingHasCameraYaw",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                FieldInfo yawField = typeof(MovementSystem).GetField("_lastMobGroupingCameraYawRadians",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                MethodInfo resolve = typeof(MovementSystem).GetMethod("TryResolveMobApproachTarget",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;

                // Markers straight ABOVE the player anchor on screen = camera forward.
                var markers = new[]
                {
                    new MobMarker(new MobPoint(690, 200), 18f, 1f, MobMarkerSource.CyanCenter),
                    new MobMarker(new MobPoint(710, 210), 18f, 1f, MobMarkerSource.CyanCenter)
                };
                MobGroupAnalysis analysis = MobGroupAnalyzer.Analyze(markers, new MobPoint(700, 400));
                Assert.True(analysis.HasDominantMobDirection);

                analysisField.SetValue(movement, analysis);
                hasYawField.SetValue(movement, true);

                // Camera facing North: the approach target lies north (+Y) of the player.
                yawField.SetValue(movement, GeometryUtils.ConvertBearingToRadians(0f));
                object?[] north = { 100f, 100f, 0f, 0f };
                Assert.True((bool)resolve.Invoke(movement, north)!);
                Assert.InRange((float)north[2]!, 98.5f, 101.5f);
                Assert.InRange((float)north[3]!, 110.5f, 113.5f);

                // Camera facing East: screen-up is East, so the target lies east (+X).
                yawField.SetValue(movement, GeometryUtils.ConvertBearingToRadians(90f));
                object?[] east = { 100f, 100f, 0f, 0f };
                Assert.True((bool)resolve.Invoke(movement, east)!);
                Assert.InRange((float)east[2]!, 110.5f, 113.5f);
                Assert.InRange((float)east[3]!, 98.5f, 101.5f);

                // Screen-right with a North-facing camera = East.
                var rightMarkers = new[]
                {
                    new MobMarker(new MobPoint(900, 400), 18f, 1f, MobMarkerSource.CyanCenter),
                    new MobMarker(new MobPoint(900, 416), 18f, 1f, MobMarkerSource.CyanCenter)
                };
                analysisField.SetValue(movement,
                    MobGroupAnalyzer.Analyze(rightMarkers, new MobPoint(700, 400)));
                yawField.SetValue(movement, GeometryUtils.ConvertBearingToRadians(0f));
                object?[] screenRight = { 100f, 100f, 0f, 0f };
                Assert.True((bool)resolve.Invoke(movement, screenRight)!);
                Assert.InRange((float)screenRight[2]!, 110.5f, 113.5f);
                Assert.InRange((float)screenRight[3]!, 98.5f, 101.5f);

                // No camera yaw = no approach target (never invent a direction).
                hasYawField.SetValue(movement, false);
                object?[] noYaw = { 100f, 100f, 0f, 0f };
                Assert.False((bool)resolve.Invoke(movement, noYaw)!);
            }
            finally
            {
                movement.DisposeMobGroupingDetector();
            }
        }

        [Fact]
        public void PinkLootScanExcludesSparseMarkerRingsButKeepsSolidDropSquares()
        {
            using var frame = NewFrame(120, 840);
            var ring = new LootSystem.LootComponent();
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

            var solidDrop = new LootSystem.LootComponent();
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
        public void DetectorAcceptsCyanCenterConfirmedByPerspectiveProjectedEllipse()
        {
            using var detector = new MobMarkerDetector();
            using var frame = NewFrame(180, 100);
            float radius = BotConstants.MobGrouping.ExpectedMarkerRadiusPx;
            float verticalScale = 0.7071f;
            using (Graphics graphics = Graphics.FromImage(frame))
            {
                using var ringPen = new Pen(Color.Magenta, 2f);
                graphics.DrawEllipse(ringPen, 90 - radius, 50 - radius * verticalScale,
                    radius * 2, radius * 2 * verticalScale);
                using var centerBrush = new SolidBrush(Color.Cyan);
                graphics.FillEllipse(centerBrush, 90 - 2, 50 - 2, 5, 5);
            }

            MobMarker[] markers = detector.DetectBitmapForTesting(frame);
            Assert.Contains(markers, marker => marker.Source == MobMarkerSource.CyanCenter &&
                Math.Abs(marker.Center.X - 90) <= 2 && Math.Abs(marker.Center.Y - 50) <= 2);
        }

        [Fact]
        public void DetectorInfersPerspectiveProjectedEllipseWithoutCyanCenter()
        {
            using var detector = new MobMarkerDetector();
            using var frame = NewFrame(180, 100);
            float radius = BotConstants.MobGrouping.ExpectedMarkerRadiusPx;
            float verticalScale = 0.7071f;
            float innerRadius = radius * BotConstants.MobGrouping.InnerRingRadiusRatio;
            using (Graphics graphics = Graphics.FromImage(frame))
            using (var ringPen = new Pen(Color.Magenta, 2f))
            {
                graphics.DrawEllipse(ringPen, 90 - radius, 50 - radius * verticalScale,
                    radius * 2, radius * 2 * verticalScale);
                graphics.DrawEllipse(ringPen, 90 - innerRadius, 50 - innerRadius * verticalScale,
                    innerRadius * 2, innerRadius * 2 * verticalScale);
            }

            MobMarker[] markers = detector.DetectBitmapForTesting(frame);
            Assert.Contains(markers, marker => marker.Source == MobMarkerSource.MagentaRing &&
                Math.Abs(marker.Center.X - 90) <= 3 && Math.Abs(marker.Center.Y - 50) <= 3);
        }

        [Fact]
        public void KeyboardFeedbackReleasesTheTurnKeyWithinTheConfiguredTolerance()
        {
            // KeyboardTurn re-evaluates this pure decision against the live camera bearing
            // every tick; MoveTowards recomputes the desired waypoint bearing from the
            // player's current position before the decision runs.
            const float waypointBearing = 90f;
            Assert.Equal("RightD", DecideTurnKey(0f, waypointBearing));
            Assert.Equal("RightD", DecideTurnKey(60f, waypointBearing));
            Assert.Equal("None", DecideTurnKey(80f, waypointBearing));
            Assert.Equal("None", DecideTurnKey(90f, waypointBearing));
            Assert.Equal("None", DecideTurnKey(100f, waypointBearing));
            Assert.Equal("LeftA", DecideTurnKey(120f, waypointBearing));
        }

        [Fact]
        public void GroupingStopMovementClearsMovementOwnership()
        {
            MovementSystem movement = CreateMovementSystem();
            FieldInfo ownsField = typeof(MovementSystem).GetField("_mobGroupingOwnsMovement",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo movingField = typeof(MovementSystem).GetField("_isMovingForward",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            MethodInfo apply = typeof(MovementSystem).GetMethod("ApplyMobGroupingDecision",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            ownsField.SetValue(movement, true);
            movingField.SetValue(movement, true);

            apply.Invoke(movement, new object[]
            {
                new MobGroupingDecision(MobGroupingDirective.StopMovement, MoveNumber: 1),
                Utc()
            });

            Assert.False((bool)ownsField.GetValue(movement)!);
            Assert.False((bool)movingField.GetValue(movement)!);
            movement.DisposeMobGroupingDetector();
        }

        [Fact]
        public void GroupingResumeCombatReleasesMovementBeforeCombatResumes()
        {
            MovementSystem movement = CreateMovementSystem();
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
            Assert.False((bool)typeof(CombatHandler)
                .GetField("_externallySuspended", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(combatHandler)!);
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
            movement.DisposeMobGroupingDetector();
        }

        // ── MovementSystem loot ownership + combat lifecycle wiring ─────────────

        /// <summary>
        /// MovementSystem that records forward/attack key transitions instead of injecting
        /// real input, following this project's existing test convention.
        /// </summary>
        private sealed class RecordingMovementSystem : MovementSystem
        {
            public RecordingMovementSystem(GameMemoryService memory, Action<string> log, IEnumerable<Waypoint> path)
                : base(memory, log, 0, 0, customPath: path,
                    targetSafety: CombatSafetyFixtures.Monster(5000))
            {
            }

            public int ForwardKeyDowns { get; private set; }
            public int SkillThreeDowns { get; private set; }
            public int SkillThreeUps { get; private set; }

            protected override void SendSyntheticForwardKeyEvent(bool keyUp)
            {
                if (!keyUp)
                    ForwardKeyDowns++;
            }

            protected override void SendSkillThreeKeyEvent(bool keyUp)
            {
                if (keyUp)
                    SkillThreeUps++;
                else
                    SkillThreeDowns++;
            }
        }

        /// <summary>
        /// MoveAndAttackAndLoot movement with a real LootSystem attached, so grouping is
        /// verified against the authoritative mandatory loot obligation rather than a flag.
        /// </summary>
        private sealed class GroupingLootFixture : IDisposable
        {
            public List<string> Lines { get; } = new();
            public LootSystem Loot { get; }
            public RecordingMovementSystem Movement { get; }

            public GroupingLootFixture(
                BotMode mode = BotMode.MoveAndAttackAndLoot,
                MovementSteeringMode? steering = MovementSteeringMode.DirectCamera)
            {
                GameMemoryService memory = CreateStubMemory();
                Loot = new LootSystem(memory, Lines.Add);
                var path = new List<Waypoint>
                {
                    new(5000, 5000, MovementPrecision.Medium, mode, steeringMode: steering)
                };
                Movement = new RecordingMovementSystem(memory, Lines.Add, path)
                {
                    LootSystemRef = Loot,
                    InternalRepotEnabled = false
                };
            }

            /// <summary>Arms a real post-kill obligation through the production entry point.</summary>
            public long ArmPostKill()
            {
                InvokePrivate(Movement, "ArmMandatoryLootBarrier", LootBarrierReason.PostKill);
                return Movement.LootBarrier.RequestId;
            }

            public void RefreshBarrier() => InvokePrivate(Movement, "RefreshLootBarrier");

            /// <summary>Runs the real mandatory service and finishes it with a verified empty pass.</summary>
            public void RunAndCompleteMandatoryLoot()
            {
                long requestId = Movement.LootBarrier.RequestId;
                Loot.NotifyNavigationHeld(requestId);
                InvokePrivate(Loot, "ServiceMandatoryLootRequest", false);
                Assert.True(Loot.IsLootCycleActive);
                InvokePrivate(Loot, "CompleteMandatoryLootRequest", requestId,
                    new LootScanPassOutcome(LootScanPassResult.NoItem, LootScanPassResult.NoItem,
                        LootScanPassResult.NoItem, CameraVerified: true, "test empty pass"));
                RefreshBarrier();
            }

            public void Dispose()
            {
                Movement.DisposeMobGroupingDetector();
                Loot.Dispose();
            }
        }

        private static object? InvokePrivate(object target, string method, params object?[] args)
        {
            Type type = target is MovementSystem ? typeof(MovementSystem) : target.GetType();
            MethodInfo info = type.GetMethod(method, PrivateInstance)
                ?? throw new InvalidOperationException($"Method {method} not found on {type.Name}.");
            return info.Invoke(target, args);
        }

        private static object Controller(MovementSystem movement) =>
            typeof(MovementSystem).GetField("_mobGroupingController", PrivateInstance)!
                .GetValue(movement)!;

        private static void SetGroupingState(MovementSystem movement, MobGroupingState state) =>
            typeof(MobGroupingController).GetField("_state", PrivateInstance)!
                .SetValue(Controller(movement), (int)state);

        private static bool IsMovingForward(MovementSystem movement) =>
            (bool)typeof(MovementSystem).GetField("_isMovingForward", PrivateInstance)!
                .GetValue(movement)!;

        private static bool CombatSuspended(MovementSystem movement)
        {
            object handler = typeof(MovementSystem).GetField("_combatHandler", PrivateInstance)!
                .GetValue(movement)!;
            return (bool)typeof(CombatHandler).GetField("_externallySuspended", PrivateInstance)!
                .GetValue(handler)!;
        }

        /// <summary>Skips the first-step in-place turn so a gather step reaches forward input.</summary>
        private static void AllowGatherStep(MovementSystem movement) =>
            typeof(MovementSystem).GetField("_initialHeadingAlignmentPending", PrivateInstance)!
                .SetValue(movement, false);

        [Fact]
        public void MandatoryLootBarrierIsTheAuthoritativeGroupingLootHold()
        {
            using var fixture = new GroupingLootFixture();
            long requestId = fixture.ArmPostKill();

            Assert.True(requestId > 0);
            Assert.True(fixture.Movement.IsLootNavigationBlocked);
            Assert.True(fixture.Movement.IsMandatoryLootBarrierActive);
            Assert.True(fixture.Movement.IsMobGroupingLootHoldActive);

            // The combat-owned recovery authorization is never general permission for
            // grouping movement: the obligation still owns the input.
            typeof(MovementSystem).GetField("_combatRecoveryMovementAuthorized", PrivateInstance)!
                .SetValue(fixture.Movement, true);
            Assert.True(fixture.Movement.IsMobGroupingLootHoldActive);
            typeof(MovementSystem).GetField("_combatRecoveryMovementAuthorized", PrivateInstance)!
                .SetValue(fixture.Movement, false);

            fixture.RunAndCompleteMandatoryLoot();

            Assert.False(fixture.Movement.IsLootNavigationBlocked);
            Assert.False(fixture.Movement.IsMandatoryLootBarrierActive);
            Assert.False(fixture.Movement.IsMobGroupingLootHoldActive);
            Assert.True(fixture.Loot.TryGetMandatoryLootRequestStatus(requestId, out LootRequestStatus status));
            Assert.Equal(LootRequestState.Completed, status.State);
            Assert.True(status.CameraVerified);
        }

        [Fact]
        public void DeferredMandatoryObligationStillBlocksGroupingLootHold()
        {
            using var fixture = new GroupingLootFixture();
            fixture.ArmPostKill();

            // Combat holds the mob: the obligation defers and no loot cycle is running.
            InvokePrivate(fixture.Loot, "ServiceMandatoryLootRequest", true);
            fixture.RefreshBarrier();

            Assert.Equal(LootBarrierPhase.Deferred, fixture.Movement.LootBarrier.Phase);
            Assert.False(fixture.Loot.IsCollecting);
            Assert.False(fixture.Loot.IsLootCycleActive);
            Assert.True(fixture.Movement.IsMandatoryLootBarrierActive);
            Assert.True(fixture.Movement.IsMobGroupingLootHoldActive);
        }

        [Fact]
        public void LootRequestArmedByTheParallelLootTaskIsAdoptedForGroupingOwnership()
        {
            using var fixture = new GroupingLootFixture();

            // The passive pre-scan arms the obligation from the parallel loot task; only
            // MovementSystem's RefreshLootBarrier adopts it (the call the movement tick
            // performs before the next gather step of an active sequence).
            long requestId = fixture.Loot.BeginMandatoryLootRequest(
                LootBarrierReason.TravelCandidateVerification);
            Assert.True(requestId > 0);
            Assert.False(fixture.Movement.IsMandatoryLootBarrierActive);

            fixture.RefreshBarrier();

            Assert.Equal(requestId, fixture.Movement.LootBarrier.RequestId);
            Assert.True(fixture.Movement.IsMandatoryLootBarrierActive);
            Assert.True(fixture.Movement.IsMobGroupingLootHoldActive);
            Assert.True(fixture.Loot.TryGetMandatoryLootRequestStatus(requestId, out LootRequestStatus status));
            Assert.Equal(LootBarrierReason.TravelCandidateVerification, status.Reason);
            Assert.Equal(LootRequestState.Pending, status.State);
        }

        [Fact]
        public void MoveAndAttackKeepsTheActiveCollectionLootHold()
        {
            using var fixture = new GroupingLootFixture(BotMode.MoveAndAttack);

            Assert.False(fixture.Movement.IsMandatoryLootBarrierActive);
            Assert.False(fixture.Movement.IsMobGroupingLootHoldActive);

            typeof(LootSystem).GetField("_isCollecting", PrivateInstance)!
                .SetValue(fixture.Loot, true);

            Assert.True(fixture.Movement.IsMobGroupingLootHoldActive);
        }

        [Fact]
        public void GroupingCancellationNeverDiscardsOrCompletesTheLootObligation()
        {
            using var fixture = new GroupingLootFixture();
            long requestId = fixture.ArmPostKill();

            fixture.Movement.CancelMobGrouping("grouping test cancellation");

            Assert.Equal(requestId, fixture.Movement.LootBarrier.RequestId);
            Assert.True(fixture.Movement.IsMandatoryLootBarrierActive);
            Assert.True(fixture.Loot.TryGetActiveMandatoryLootRequest(
                out long activeId, out LootBarrierReason activeReason));
            Assert.Equal(requestId, activeId);
            Assert.Equal(LootBarrierReason.PostKill, activeReason);
            Assert.True(fixture.Loot.TryGetMandatoryLootRequestStatus(requestId, out LootRequestStatus status));
            Assert.Equal(LootRequestState.Pending, status.State);
        }

        [Fact]
        public void MandatoryLootBarrierSuppressesGatherMovementUntilVerifiedCompletion()
        {
            using var fixture = new GroupingLootFixture();
            fixture.ArmPostKill();

            // The live waypoint target is still resolved (steering unchanged), but the
            // gather step must never press W while the obligation holds navigation.
            Assert.True(fixture.Movement.TryGetGatherNavigationTarget(0, 0, out _, out _, out _));
            AllowGatherStep(fixture.Movement);
            Assert.True((bool)InvokePrivate(fixture.Movement, "ApplyGatherMovement", 0f, 0f)!);
            Assert.Equal(0, fixture.Movement.ForwardKeyDowns);
            Assert.False(IsMovingForward(fixture.Movement));

            fixture.RunAndCompleteMandatoryLoot();

            AllowGatherStep(fixture.Movement);
            Assert.True((bool)InvokePrivate(fixture.Movement, "ApplyGatherMovement", 0f, 0f)!);
            Assert.True(fixture.Movement.ForwardKeyDowns > 0);
            Assert.True(IsMovingForward(fixture.Movement));
        }

        [Theory]
        [InlineData(BotMode.MoveAndAttack)]
        [InlineData(BotMode.MoveAndAttackAndLoot)]
        public void GroupingLifecycleReleasesAndRestoresCombatInput(BotMode mode)
        {
            var path = new List<Waypoint>
            {
                new(5000, 5000, MovementPrecision.Medium, mode)
            };
            var movement = new RecordingMovementSystem(CreateStubMemory(), _ => { }, path);
            try
            {
                typeof(MovementSystem).GetField("_isSkillThreeHeld", PrivateInstance)!
                    .SetValue(movement, true);
                typeof(MovementSystem).GetField("_isMovingForward", PrivateInstance)!
                    .SetValue(movement, true);
                SetGroupingState(movement, MobGroupingState.GroupingMove);

                InvokePrivate(movement, "ApplyMobGroupingDecision",
                    new MobGroupingDecision(MobGroupingDirective.StartMove, MobGroupingDirection.North, 1),
                    Utc());

                Assert.True((bool)typeof(MovementSystem).GetField(
                    "_mobGroupingOwnsMovement", PrivateInstance)!.GetValue(movement)!);
                Assert.False(movement.IsAttackKeyHeld);
                Assert.Equal(1, movement.SkillThreeUps);
                Assert.False(IsMovingForward(movement));
                Assert.True(CombatSuspended(movement));

                // Finish() publishes ResumeCombat before MovementSystem applies the directive.
                SetGroupingState(movement, MobGroupingState.ResumeCombat);
                InvokePrivate(movement, "ApplyMobGroupingDecision",
                    new MobGroupingDecision(MobGroupingDirective.ResumeCombat, MoveNumber: 1, Reason: "complete"),
                    Utc(2000));

                Assert.False((bool)typeof(MovementSystem).GetField(
                    "_mobGroupingOwnsMovement", PrivateInstance)!.GetValue(movement)!);
                Assert.False(CombatSuspended(movement));
                Assert.True((bool)typeof(MovementSystem).GetField(
                    "_resumeCombatSkillAfterGrouping", PrivateInstance)!.GetValue(movement)!);
                Assert.Equal(MobGroupingState.Cooldown, movement.CurrentMobGroupingState);

                // The CombatWait branch reacquires the released hold through this production
                // method; the recorded key event proves the attack skill is held again.
                InvokePrivate(movement, "HoldSkillThree");
                Assert.True(movement.IsAttackKeyHeld);
                Assert.Equal(1, movement.SkillThreeDowns);
            }
            finally
            {
                movement.DisposeMobGroupingDetector();
            }
        }

        [Fact]
        public void CombatResumeIsNotScheduledForOnlyMove()
        {
            var path = new List<Waypoint>
            {
                new(5000, 5000, MovementPrecision.Medium, BotMode.OnlyMove)
            };
            var movement = new RecordingMovementSystem(CreateStubMemory(), _ => { }, path);
            try
            {
                InvokePrivate(movement, "FinishMobGroupingOwnership", true, Utc());

                Assert.False((bool)typeof(MovementSystem).GetField(
                    "_resumeCombatSkillAfterGrouping", PrivateInstance)!.GetValue(movement)!);
                Assert.False(movement.IsAttackKeyHeld);
            }
            finally
            {
                movement.DisposeMobGroupingDetector();
            }
        }

        [Fact]
        public void GroupingSuspensionDoesNotTripCombatWatchdogTimersInLootMode()
        {
            var path = new List<Waypoint>
            {
                new(5000, 5000, MovementPrecision.Medium, BotMode.MoveAndAttackAndLoot)
            };
            var movement = new RecordingMovementSystem(CreateStubMemory(), _ => { }, path);
            try
            {
                object handler = typeof(MovementSystem).GetField("_combatHandler", PrivateInstance)!
                    .GetValue(movement)!;
                var timers = new[]
                {
                    "_lastNonIdleActionTime", "_lastMoveModeTabTime", "_lastAttackSpeedCheck",
                    "_combatIdleStartTime", "_lastManaChangeAt", "_lastCombatPosChangeAt"
                };
                DateTime baseline = DateTime.Now.AddSeconds(-30);
                foreach (string name in timers)
                    typeof(CombatHandler).GetField(name, PrivateInstance)!.SetValue(handler, baseline);

                InvokePrivate(movement, "ApplyMobGroupingDecision",
                    new MobGroupingDecision(MobGroupingDirective.StartMove, MobGroupingDirection.North, 1),
                    Utc());
                Assert.True(CombatSuspended(movement));

                // Simulate a 1500 ms grouping walk deterministically instead of sleeping.
                typeof(CombatHandler).GetField("_externalSuspensionStartedAt", PrivateInstance)!
                    .SetValue(handler, DateTime.Now.AddMilliseconds(-1500));
                InvokePrivate(movement, "ApplyMobGroupingDecision",
                    new MobGroupingDecision(MobGroupingDirective.ResumeCombat, MoveNumber: 1, Reason: "complete"),
                    Utc(1500));

                Assert.False(CombatSuspended(movement));
                foreach (string name in timers)
                {
                    DateTime value = (DateTime)typeof(CombatHandler)
                        .GetField(name, PrivateInstance)!.GetValue(handler)!;
                    Assert.InRange((value - baseline).TotalMilliseconds, 1200, 1800);
                }
            }
            finally
            {
                movement.DisposeMobGroupingDetector();
            }
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
