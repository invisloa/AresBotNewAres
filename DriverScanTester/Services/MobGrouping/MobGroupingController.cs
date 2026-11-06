using System;
using System.Collections.Generic;
using System.Threading;

namespace DriverScanTester.Services
{
    public readonly record struct MobGroupingTickInput(
        DateTime NowUtc,
        bool Enabled,
        BotMode CurrentMode,
        bool HasCombatContext,
        bool IsPaused,
        bool IsStopping,
        bool IsTransitioning,
        bool IsRecoveryActive,
        bool IsLootHoldActive,
        bool IsGameWindowValid,
        bool DetectionCaptured,
        MobGroupAnalysis? Analysis,
        bool PlayerPositionValid,
        float PlayerX,
        float PlayerY,
        bool HasGatherNavigationTarget = true,
        float GatherTargetX = 0f,
        float GatherTargetY = 0f,
        MovementSteeringMode GatherSteeringMode = MovementSteeringMode.KeyboardTurn,
        bool IsMandatoryLootBarrierActive = false,
        /// <summary>Camera yaw (radians) at the moment of the detection sample.</summary>
        float CameraYawRadians = 0f,
        /// <summary>False when no camera yaw could be observed (trend comparisons are then skipped).</summary>
        bool HasCameraYaw = false,
        int ClientWidth = 0,
        int ClientHeight = 0,
        /// <summary>
        /// True when a live mob-cluster direction is available, so a gather walk can steer
        /// toward the mobs even when the bot is NOT walking the route (the normal combat case).
        /// </summary>
        bool CanApproachMobs = false);

    /// <summary>
    /// Tick-driven, deterministic grouping state machine. It has no image, combat, keyboard,
    /// path or profile dependencies; MovementSystem executes the directives it returns.
    /// <para>
    /// CONSERVATIVE OPPORTUNISTIC GROUPING: a gather walk is started ONLY when an already
    /// useful, route-compatible walk is measurably improving a genuinely scattered local
    /// formation that does not already contain a compact pair. Two visible mobs never justify
    /// a separate movement and a single attempt per encounter is allowed. Anything the system
    /// cannot establish with reasonable confidence simply continues as ordinary combat.
    /// </para>
    /// </summary>
    public sealed class MobGroupingController
    {
        /// <summary>
        /// Block/cancel reason used when the mandatory MoveAndAttackAndLoot loot
        /// obligation (including a Deferred request that owns movement without an
        /// active collection) still holds navigation.
        /// </summary>
        internal const string LootBarrierBlockReason = "mandatory loot barrier owns control";

        private const float RadiansToDegrees = 180f / MathF.PI;

        /// <summary>One bounded world-position sample used for the progress/direction checks.</summary>
        private readonly struct PlayerSample
        {
            public PlayerSample(DateTime atUtc, float x, float y)
            {
                AtUtc = atUtc;
                X = x;
                Y = y;
            }

            public DateTime AtUtc { get; }
            public float X { get; }
            public float Y { get; }
        }

        private readonly Action<string> _log;
        private readonly MobFormationHistory _formationHistory = new();
        private readonly List<PlayerSample> _playerSamples =
            new(BotConstants.MobGrouping.PlayerProgressMaxSamples);

        private int _state = (int)MobGroupingState.Idle;
        private DateTime _combatSince = DateTime.MinValue;
        private DateTime _badFormationSince = DateTime.MinValue;
        private DateTime _lastBadFormationAt = DateTime.MinValue;
        private MobGroupAnalysis? _candidateAnalysis;
        private DateTime _cooldownUntil = DateTime.MinValue;
        private DateTime _moveUntil = DateTime.MinValue;
        /// <summary>When the current gather movement attempt started (for diagnostics).</summary>
        private DateTime _gatherStartedAt = DateTime.MinValue;
        /// <summary>When the next in-move grouping-quality rescan is due.</summary>
        private DateTime _nextGatherRescanAt = DateTime.MinValue;
        /// <summary>When the current continuous combat-context absence started (MinValue = combat present).</summary>
        private DateTime _combatAbsentSince = DateTime.MinValue;
        private bool _quietResetLogged;
        private bool _encounterLimitLogged;
        /// <summary>One-shot log guard for "blocked by the mandatory loot barrier".</summary>
        private bool _lootBarrierBlockLogged;
        /// <summary>Code of the last logged "gather not started" decision (event-driven logging).</summary>
        private string _lastSuppressionCode = "";
        /// <summary>Code of the last non-blocking advisory (trend/standstill approach) log line.</summary>
        private string _lastAdvisoryCode = "";
        /// <summary>
        /// Code of the last logged suppression, exposed read-only so the host can attach a
        /// diagnostic snapshot to exactly the moment a gather was refused.
        /// </summary>
        public string LastSuppressionCode => _lastSuppressionCode;
        private string _lastDecisionReason = "";
        private MobFormationTrendResult _lastTrend = MobFormationTrendResult.Unknown("no observation");
        private int _movesStarted;
        private int _groupingsThisEncounter;
        /// <summary>Consecutive unreliable detection samples while a gather walk is active.</summary>
        private int _unreliableGatherSamples;

        // Per-attempt diagnostics (one attempt per encounter, so a single record is enough).
        private float _gatherStartX;
        private float _gatherStartY;
        private float _gatherTargetX;
        private float _gatherTargetY;
        private float _gatherStartQuality;
        private float _gatherCurrentQuality;
        private int _gatherStartMarkerCount;
        private int _gatherStartClusterSize;

        public MobGroupingController(Action<string>? log = null)
        {
            _log = log ?? (_ => { });
        }

        /// <summary>
        /// Single source of truth for the combat modes that may run mob grouping: both
        /// attack modes share the same algorithm, thresholds and configuration. OnlyMove
        /// never groups. MovementSystem applies the same predicate to both the effective
        /// waypoint mode and CurrentMode so no grouping decision can disagree with it.
        /// </summary>
        internal static bool IsSupportedCombatMode(BotMode mode) =>
            mode == BotMode.MoveAndAttack || mode == BotMode.MoveAndAttackAndLoot;

        public MobGroupingState State => (MobGroupingState)Volatile.Read(ref _state);
        public int GroupingsThisEncounter => Volatile.Read(ref _groupingsThisEncounter);
        public int MovesStarted => Volatile.Read(ref _movesStarted);

        /// <summary>Latest bounded formation trend (diagnostics / debug screenshots only).</summary>
        public MobFormationTrendResult LastTrend => _lastTrend;

        /// <summary>Last grouping decision code/description (diagnostics only).</summary>
        public string LastDecisionReason => _lastDecisionReason;

        public bool IsSequenceActive => State is MobGroupingState.GroupingMove or
            MobGroupingState.Settling or MobGroupingState.Verify or MobGroupingState.ResumeCombat;

        /// <summary>
        /// True when an active gather walk is due for a grouping-quality rescan. The caller
        /// (MovementSystem) captures one detection sample and feeds it back through
        /// <see cref="Tick"/>. False at or after the gather deadline so the deadline always
        /// wins over the rescan cadence and no scan is started past it.
        /// </summary>
        public bool NeedsGatherRescan(DateTime nowUtc) =>
            State == MobGroupingState.GroupingMove &&
            nowUtc < _moveUntil &&
            nowUtc >= _nextGatherRescanAt;

        public MobGroupingDecision Tick(MobGroupingTickInput input)
        {
            bool active = IsSequenceActive;
            string? blockReason = GetBlockReason(input);

            // Event-driven diagnostic: a candidate formation that grouping would chase is
            // currently held back by the mandatory loot obligation. Logged once per
            // barrier period, never per tick.
            if (blockReason == LootBarrierBlockReason)
                LogLootBarrierBlock(input);
            else
                _lootBarrierBlockLogged = false;

            // One observation of the world per tick: bounded player-position samples plus
            // (only when a detection sample was captured) one bounded formation observation.
            ObservePlayerPosition(input);
            ObserveFormation(input);

            if (active && blockReason != null)
                return CancelSequence(input.NowUtc, BlockReasonCode(blockReason), blockReason);

            // Losing the combat target during an active sequence must end grouping
            // immediately: the post-kill SOD/SOP flow (CombatHandler -> TabAfterKill ->
            // LootSystem.RequestPinkScan) must never be delayed by a temporary grouping
            // walk. This is a normal termination, not a failure.
            if (active && !input.HasCombatContext)
                return CancelSequence(input.NowUtc, MobGroupingReason.CombatContextLost,
                    "combat context ended");

            UpdateEncounterLifecycle(input.NowUtc, input.HasCombatContext);

            if (State == MobGroupingState.ResumeCombat)
                return new MobGroupingDecision(MobGroupingDirective.ResumeCombat,
                    Reason: "complete");

            if (blockReason != null)
            {
                _combatSince = DateTime.MinValue;
                if (blockReason != "game window unavailable or unfocused")
                    ClearCandidate(blockReason);
                else if (_badFormationSince != DateTime.MinValue &&
                         input.NowUtc - _lastBadFormationAt > TimeSpan.FromMilliseconds(
                             BotConstants.MobGrouping.BadFormationGraceMs))
                    ClearCandidate("detector unavailable beyond grace period");
                if (State != MobGroupingState.Cooldown)
                    SetState(MobGroupingState.Idle);
                return default;
            }

            if (State == MobGroupingState.GroupingMove)
                return EvaluateGather(input);

            if (State == MobGroupingState.Cooldown)
            {
                if (input.NowUtc < _cooldownUntil)
                    return default;
                SetState(MobGroupingState.Observing);
            }

            if (State == MobGroupingState.Idle)
                SetState(MobGroupingState.Observing);

            if (!input.HasCombatContext)
            {
                _combatSince = DateTime.MinValue;
                if (_badFormationSince != DateTime.MinValue &&
                    input.NowUtc - _lastBadFormationAt > TimeSpan.FromMilliseconds(
                        BotConstants.MobGrouping.BadFormationGraceMs))
                    ClearCandidate("combat context ended");
                return default;
            }

            if (_combatSince == DateTime.MinValue)
                _combatSince = input.NowUtc;

            if (_groupingsThisEncounter >= BotConstants.MobGrouping.MaxGroupingsPerEncounter)
            {
                ClearCandidate("encounter grouping limit reached");
                if (input.DetectionCaptured &&
                    input.Analysis is { IsValid: true } limitAnalysis &&
                    IsGatherCandidate(limitAnalysis) &&
                    !_encounterLimitLogged)
                {
                    _encounterLimitLogged = true;
                    _log($"[MobGrouping] gather not started [{MobGroupingReason.AttemptConsumed}]: " +
                         $"encounter limit {_groupingsThisEncounter}/" +
                         $"{BotConstants.MobGrouping.MaxGroupingsPerEncounter} reached.");
                }
                return default;
            }

            if (input.DetectionCaptured)
                UpdateCandidate(input);

            if (_badFormationSince == DateTime.MinValue || _candidateAnalysis == null)
                return default;

            // ── Strict active-gather eligibility ───────────────────────────────
            bool candidateFresh = input.NowUtc - _lastBadFormationAt <=
                TimeSpan.FromMilliseconds(BotConstants.MobGrouping.BadFormationGraceMs);
            bool persisted = input.NowUtc - _badFormationSince >=
                TimeSpan.FromMilliseconds(BotConstants.MobGrouping.BadFormationPersistenceMs);
            bool combatLongEnough = input.NowUtc - _combatSince >=
                TimeSpan.FromMilliseconds(BotConstants.MobGrouping.MinimumCombatTimeMs);

            if (!candidateFresh || !persisted || !combatLongEnough)
                return default;

            if (!input.PlayerPositionValid)
            {
                ClearCandidate("player position unavailable");
                return default;
            }

            // No positional target to walk toward: never invent an escape direction.
            // The candidate stays fresh so the gather can still start once the ordinary
            // path pipeline advances past a reached waypoint and exposes a valid target -
            // or immediately, when a mob-cluster approach direction is available (the bot is
            // standing still in melee, which is the normal combat case).
            if (!input.HasGatherNavigationTarget && !input.CanApproachMobs)
            {
                LogSuppression(MobGroupingReason.NoNavigationTarget,
                    "no live route waypoint and no mob approach direction for a gather walk");
                return default;
            }

            // Temporal requirement. The formation trend is ADVISORY: screen-space geometry is
            // measured against a flapping pixel detector (marker counts 2-18 per second), so a
            // hard "two consecutive improvements" gate refused every gather in 124 recorded
            // evaluations. Only an explicitly deteriorating formation still blocks a walk.
            MobFormationTrendResult trend = _lastTrend;
            if (trend.Trend == MobFormationTrend.Deteriorating &&
                trend.ComparableObservations >= BotConstants.MobGrouping.MinimumTrendObservations)
            {
                LogSuppression(MobGroupingReason.FormationDeteriorated,
                    $"{trend.Reason} (trend={trend.Trend}, " +
                    $"samples={trend.ComparableObservations}, " +
                    $"change={trend.RelativeChange:P0})");
                return default;
            }

            if (trend.Trend != MobFormationTrend.Improving)
            {
                LogAdvisory(MobGroupingReason.NoConvergenceEvidence,
                    $"{trend.Reason} (trend={trend.Trend}, " +
                    $"samples={trend.ComparableObservations}, " +
                    $"change={trend.RelativeChange:P0})");
            }

            // A gather walk must have a usable steering target. Continuing an existing
            // route-compatible walk is preferred; otherwise the walk approaches the mob cluster
            // (the bot stands still while fighting, so requiring route progress made the whole
            // feature unreachable).
            bool routeCompatible = IsRouteCompatibleProgress(input, out string progressCode, out string progressDetail);
            if (!routeCompatible && !input.CanApproachMobs)
            {
                LogSuppression(progressCode, progressDetail);
                return default;
            }

            MobGroupingGatherTarget gatherTarget = routeCompatible
                ? MobGroupingGatherTarget.Route
                : MobGroupingGatherTarget.ApproachMobs;
            if (!routeCompatible)
            {
                LogAdvisory(MobGroupingReason.StationaryApproach,
                    $"{progressCode}: {progressDetail}; walking toward the mob cluster instead");
            }

            return StartGather(input, _candidateAnalysis, trend, gatherTarget);
        }

        /// <summary>
        /// Classifies one captured detection sample for candidate purposes. Only a genuinely
        /// scattered, reliable, outlier-free formation of at least three markers may create or
        /// keep a candidate. Every other sample clears it (with the existing short grace for
        /// lost/weak frames).
        /// </summary>
        private void UpdateCandidate(in MobGroupingTickInput input)
        {
            MobGroupAnalysis? analysis = input.Analysis is { IsValid: true } valid ? valid : null;

            if (analysis == null)
            {
                if (_badFormationSince != DateTime.MinValue &&
                    input.NowUtc - _lastBadFormationAt > TimeSpan.FromMilliseconds(
                        BotConstants.MobGrouping.BadFormationGraceMs))
                    ClearCandidate("detector unavailable beyond grace period");
                return;
            }

            if (analysis.DetectedMobCount < BotConstants.MobGrouping.MinimumMobs)
            {
                if (_badFormationSince != DateTime.MinValue &&
                    input.NowUtc - _lastBadFormationAt > TimeSpan.FromMilliseconds(
                        BotConstants.MobGrouping.BadFormationGraceMs))
                    ClearCandidate("too few markers");
                return;
            }

            // Below the gather-eligibility count no separate grouping movement may start. With the
            // current configuration (MinimumGatherMarkers == MinimumMobs == 2) every analyzed
            // formation passes this check, so a scattered pair may gather; a compact pair is still
            // rejected by the compact-local-group check below. Raising the constant restores the
            // "never gather on a pair" policy without any further code change.
            if (analysis.DetectedMobCount < BotConstants.MobGrouping.MinimumGatherMarkers)
            {
                ClearCandidate($"{analysis.DetectedMobCount} visible mob(s), at least " +
                    $"{BotConstants.MobGrouping.MinimumGatherMarkers} are required for a gather walk",
                    MobGroupingReason.TwoMobNoGather);
                return;
            }

            // A compact local pair (or a larger compact local group) already exists: a distant
            // outlier must never be chased at the cost of that local group.
            if (analysis.HasCompactLocalGroup ||
                analysis.IsSufficientlyClustered ||
                analysis.HasAttackReadyCluster)
            {
                ClearCandidate("compact local group already available",
                    MobGroupingReason.CompactLocalGroup);
                return;
            }

            if (!analysis.IsScattered)
            {
                ClearCandidate("formation is not scattered");
                return;
            }

            if (!analysis.IsMovementSampleReliable)
            {
                ClearCandidate("detection is not reliable for a movement decision",
                    MobGroupingReason.DetectionUnreliable);
                return;
            }

            if (_badFormationSince == DateTime.MinValue)
            {
                _badFormationSince = input.NowUtc;
                _lastSuppressionCode = "";
                _log($"[MobGrouping] scattered-formation candidate started " +
                     $"(mobs={analysis.DetectedMobCount}, localCluster={analysis.BestLocalClusterSize}, " +
                     $"bestPair={analysis.BestPairDistanceNormalized:F2}r, avg={analysis.AverageSpread:F1}px, " +
                     $"max={analysis.MaximumSpread:F1}px, quality={analysis.FormationQuality:F3}).");
            }
            _lastBadFormationAt = input.NowUtc;
            _candidateAnalysis = analysis;
        }

        /// <summary>
        /// Consumes the bounded gather attempt. The deadline is fixed once; the walk can only be
        /// shortened by a stop condition, never extended.
        /// </summary>
        private MobGroupingDecision StartGather(
            in MobGroupingTickInput input,
            MobGroupAnalysis analysis,
            MobFormationTrendResult trend,
            MobGroupingGatherTarget gatherTarget)
        {
            _groupingsThisEncounter++;
            _movesStarted = 1;
            _gatherStartX = input.PlayerX;
            _gatherStartY = input.PlayerY;
            _gatherTargetX = input.GatherTargetX;
            _gatherTargetY = input.GatherTargetY;
            _gatherStartQuality = analysis.FormationQuality;
            _gatherCurrentQuality = analysis.FormationQuality;
            _gatherStartMarkerCount = analysis.DetectedMobCount;
            _gatherStartClusterSize = analysis.BestLocalClusterSize;
            _unreliableGatherSamples = 0;
            _lastSuppressionCode = "";
            _gatherStartedAt = input.NowUtc;
            _moveUntil = input.NowUtc.AddMilliseconds(
                BotConstants.MobGrouping.MaximumGatherDurationMs);
            _nextGatherRescanAt = input.NowUtc.AddMilliseconds(
                BotConstants.MobGrouping.GatherRescanIntervalMs);

            _badFormationSince = DateTime.MinValue;
            _lastBadFormationAt = DateTime.MinValue;
            _candidateAnalysis = null;
            SetState(MobGroupingState.GroupingMove);

            // The decision code states HOW the walk was justified: an established convergence
            // trend, a bounded scattered approach along the route, or a standing approach toward
            // the mob cluster. Nothing ever claims confirmation without evidence.
            string startCode = gatherTarget == MobGroupingGatherTarget.ApproachMobs
                ? MobGroupingReason.StationaryApproach
                : trend.IsImproving
                    ? MobGroupingReason.ConvergenceConfirmed
                    : MobGroupingReason.ScatteredMove;

            string reason = MobGroupingReason.Describe(startCode,
                gatherTarget == MobGroupingGatherTarget.Route
                    ? trend.IsImproving ? "route-compatible convergence" : "route-compatible scattered approach"
                    : "standing approach toward the mob cluster");
            _lastDecisionReason = reason;

            _log($"[MobGrouping] gather started [{startCode}]: " +
                 $"target={gatherTarget} (trend={trend.Trend}, samples={trend.ComparableObservations}, " +
                 $"change={trend.RelativeChange:P0}); mobs={analysis.DetectedMobCount}, " +
                 $"localCluster={analysis.BestLocalClusterSize}, bestPair={analysis.BestPairDistanceNormalized:F2}r, " +
                 $"quality={analysis.FormationQuality:F3}, player=({input.PlayerX:F1},{input.PlayerY:F1}), " +
                 $"waypoint=({input.GatherTargetX:F1},{input.GatherTargetY:F1}), " +
                 $"steering={input.GatherSteeringMode}, " +
                 $"budget={BotConstants.MobGrouping.MaximumGatherDurationMs}ms/" +
                 $"{BotConstants.MobGrouping.MaximumGatherDisplacement:F1}u, " +
                 $"rescan={BotConstants.MobGrouping.GatherRescanIntervalMs}ms, mode={input.CurrentMode}.");

            return new MobGroupingDecision(
                MobGroupingDirective.StartMove,
                analysis.RecommendedEscapeDirection,
                _movesStarted,
                reason,
                startCode,
                gatherTarget);
        }

        /// <summary>
        /// One tick of an active gather walk. The walk ends on the FIRST satisfied stop
        /// condition, always releasing movement ownership: compact local group, no measurable
        /// progress, stagnation, deterioration, unreliable detection, the fixed time budget,
        /// the displacement budget, the reached waypoint, or a lost combat context (handled by
        /// the caller).
        /// </summary>
        private MobGroupingDecision EvaluateGather(in MobGroupingTickInput input)
        {
            // A valid positional route target is the only gather destination. When the
            // current waypoint becomes reached (or the route has no usable positional
            // target), hand control back to the ordinary path pipeline so it can run the
            // waypoint's arrival semantics. The controller never dequeues or advances
            // the path and never picks a replacement waypoint.
            if (!input.HasGatherNavigationTarget)
                return FinishGather(input, MobGroupingReason.WaypointReached,
                    "gather navigation target reached or unavailable");

            // World position is required for both movement budgets and for progress.
            if (!input.PlayerPositionValid)
                return FinishGather(input, MobGroupingReason.DetectionUnreliable,
                    "player world position unavailable");

            double elapsedMs = Math.Max(0, (input.NowUtc - _gatherStartedAt).TotalMilliseconds);
            float travelled = GeometryUtils.Distance(
                input.PlayerX, input.PlayerY, _gatherStartX, _gatherStartY);

            if (travelled >= BotConstants.MobGrouping.MaximumGatherDisplacement)
                return FinishGather(input, MobGroupingReason.DistanceBudgetReached,
                    $"displacement budget {BotConstants.MobGrouping.MaximumGatherDisplacement:F1} " +
                    "world units exhausted");

            // The deadline is chosen once. It is never re-rolled and never extended by a rescan.
            if (input.NowUtc >= _moveUntil)
            {
                string code = HasMeasurableProgress(elapsedMs, travelled)
                    ? MobGroupingReason.TimeBudgetReached
                    : MobGroupingReason.NoMovementProgress;
                return FinishGather(input, code, code == MobGroupingReason.TimeBudgetReached
                    ? $"time budget {BotConstants.MobGrouping.MaximumGatherDurationMs}ms exhausted"
                    : "no meaningful movement progress during the gather");
            }

            if (input.DetectionCaptured)
            {
                MobGroupAnalysis? analysis = input.Analysis is { IsValid: true } valid ? valid : null;

                // A compact local group (or the existing whole/attack-ready cluster) is the
                // success signal: stop immediately and attack the local group.
                if (analysis != null && IsGatheringComplete(analysis))
                {
                    _gatherCurrentQuality = analysis.FormationQuality;
                    return FinishGather(input, MobGroupingReason.CompactLocalGroup,
                        $"compact local group formed (mobs={analysis.DetectedMobCount}, " +
                        $"localCluster={analysis.BestLocalClusterSize}, " +
                        $"bestPair={analysis.BestPairDistanceNormalized:F2}r)");
                }

                if (analysis == null || !analysis.IsMovementSampleReliable)
                {
                    _unreliableGatherSamples++;
                    if (_unreliableGatherSamples >= 2)
                        return FinishGather(input, MobGroupingReason.DetectionUnreliable,
                            "two consecutive unreliable detection samples");
                }
                else
                {
                    _unreliableGatherSamples = 0;
                    _gatherCurrentQuality = analysis.FormationQuality;

                    _log($"[MobGrouping] gather rescan: mobs={analysis.DetectedMobCount}, " +
                         $"localCluster={analysis.BestLocalClusterSize}, " +
                         $"bestPair={analysis.BestPairDistanceNormalized:F2}r, " +
                         $"quality={analysis.FormationQuality:F3}, trend={_lastTrend.Trend}, " +
                         $"change={_lastTrend.RelativeChange:P0}, " +
                         $"displacement={travelled:F2}u.");

                    if (_lastTrend.Trend == MobFormationTrend.Deteriorating)
                        return FinishGather(input, MobGroupingReason.FormationDeteriorated,
                            "formation quality deteriorated during the gather");

                    if (_lastTrend.Trend == MobFormationTrend.Stagnant)
                        return FinishGather(input, MobGroupingReason.NoFormationImprovement,
                            "formation quality stopped improving during the gather");

                    // Neither a positive trend nor an explicitly worse one: the earlier
                    // convergence evidence was invalidated by this reliable sample (camera
                    // change, client size change, marker-count change, lost frame). A walk that
                    // can no longer prove it is improving must not continue speculatively.
                    if (!_lastTrend.IsImproving)
                        return FinishGather(input, MobGroupingReason.NoConvergenceEvidence,
                            $"convergence evidence invalidated ({_lastTrend.Reason})");
                }

                // The next rescan is scheduled on every captured sample (also for an unusable
                // one) so an unreliable frame can never turn the scan cadence into a per-tick
                // capture loop.
                _nextGatherRescanAt = input.NowUtc.AddMilliseconds(
                    BotConstants.MobGrouping.GatherRescanIntervalMs);
            }

            if (elapsedMs >= BotConstants.MobGrouping.GatherProgressCheckDelayMs &&
                !HasMeasurableProgress(elapsedMs, travelled))
                return FinishGather(input, MobGroupingReason.NoMovementProgress,
                    "player stopped making meaningful world-position progress");

            return default;
        }

        /// <summary>
        /// True when the attempt kept up with the configured fraction of the eligibility
        /// progress rate. Evaluated from the world-coordinate displacement since gather start.
        /// </summary>
        private static bool HasMeasurableProgress(double elapsedMs, float travelled)
        {
            if (elapsedMs <= 0)
                return true;

            float expected = BotConstants.MobGrouping.MinimumMovementProgressForGather *
                (float)(elapsedMs / BotConstants.MobGrouping.PlayerProgressWindowMs) *
                BotConstants.MobGrouping.GatherProgressTolerance;
            return travelled >= expected;
        }

        private MobGroupingDecision FinishGather(
            in MobGroupingTickInput input, string code, string detail)
        {
            double elapsedMs = Math.Max(0, (input.NowUtc - _gatherStartedAt).TotalMilliseconds);
            float displacement = input.PlayerPositionValid
                ? GeometryUtils.Distance(input.PlayerX, input.PlayerY, _gatherStartX, _gatherStartY)
                : 0f;
            bool improvedMeaningfully = _gatherStartQuality > 0.0001f &&
                (_gatherCurrentQuality - _gatherStartQuality) / _gatherStartQuality >=
                BotConstants.MobGrouping.FormationImprovementNoise;

            _log($"[MobGrouping] gather finished [{code}]: {detail}; duration={elapsedMs:F0}ms, " +
                 $"displacement={displacement:F2}u, mobs={_gatherStartMarkerCount}, " +
                 $"localCluster={_gatherStartClusterSize}, " +
                 $"quality={_gatherStartQuality:F3}->{_gatherCurrentQuality:F3}, " +
                 $"improved={improvedMeaningfully}, waypoint=({_gatherTargetX:F1},{_gatherTargetY:F1}) " +
                 "- stopping movement and resuming combat.");

            _unreliableGatherSamples = 0;
            return Finish(code, detail);
        }

        /// <summary>
        /// Single source of truth for "the gather succeeded": at least two mobs are still
        /// detected AND the analyzer reports a compact local group (a pair is enough), the
        /// whole formation clustered, or an attack-ready local subset. A frame that detects
        /// fewer markers never counts as success; the bounded budget ends such an attempt.
        /// </summary>
        internal static bool IsGatheringComplete(MobGroupAnalysis analysis) =>
            analysis.DetectedMobCount >= BotConstants.MobGrouping.MinimumMobs &&
            (analysis.HasCompactLocalGroup ||
             analysis.IsSufficientlyClustered ||
             analysis.HasAttackReadyCluster);

        /// <summary>True when a formation justifies (but does not yet start) a gather walk.</summary>
        internal static bool IsGatherCandidate(MobGroupAnalysis analysis) =>
            analysis.DetectedMobCount >= BotConstants.MobGrouping.MinimumGatherMarkers &&
            analysis.IsMovementSampleReliable &&
            analysis.IsScattered &&
            !analysis.HasCompactLocalGroup &&
            !analysis.IsSufficientlyClustered &&
            !analysis.HasAttackReadyCluster;

        /// <summary>Completes post-group cleanup and begins the shared runtime cooldown.</summary>
        public void CompleteResume(DateTime nowUtc)
        {
            if (State != MobGroupingState.ResumeCombat)
                return;

            _cooldownUntil = nowUtc.AddMilliseconds(BotConstants.MobGrouping.CooldownMs);
            _badFormationSince = DateTime.MinValue;
            _lastBadFormationAt = DateTime.MinValue;
            _candidateAnalysis = null;
            SetState(MobGroupingState.Cooldown);
            _log($"[MobGrouping] cooldown started: {BotConstants.MobGrouping.CooldownMs}ms.");
        }

        /// <summary>Synchronously cancels a sequence for pause/stop or another external safety owner.</summary>
        public MobGroupingDecision Cancel(DateTime nowUtc, string reason)
        {
            if (IsSequenceActive)
                return CancelSequence(nowUtc, MobGroupingReason.ExternalCancellation, reason);

            if (State == MobGroupingState.Observing)
            {
                ClearCandidate(reason);
                _combatSince = DateTime.MinValue;
                SetState(MobGroupingState.Idle);
            }
            return default;
        }

        private MobGroupingDecision CancelSequence(DateTime nowUtc, string code, string detail)
        {
            double elapsedMs = (nowUtc - _gatherStartedAt).TotalMilliseconds;
            bool wasGathering = State == MobGroupingState.GroupingMove;
            _log($"[MobGrouping] grouping cancelled [{code}]: {detail}" +
                 (wasGathering ? $" after {elapsedMs:F0}ms." : "."));
            _cooldownUntil = nowUtc.AddMilliseconds(BotConstants.MobGrouping.CooldownMs);
            _badFormationSince = DateTime.MinValue;
            _lastBadFormationAt = DateTime.MinValue;
            _candidateAnalysis = null;
            _unreliableGatherSamples = 0;
            SetState(MobGroupingState.Cooldown);
            _log($"[MobGrouping] cooldown started: {BotConstants.MobGrouping.CooldownMs}ms.");
            _lastDecisionReason = DetailOrCode(code, detail);
            return new MobGroupingDecision(MobGroupingDirective.Cancelled,
                MoveNumber: _movesStarted, Reason: DetailOrCode(code, detail), Code: code);
        }

        private MobGroupingDecision Finish(string code, string detail)
        {
            _badFormationSince = DateTime.MinValue;
            _lastBadFormationAt = DateTime.MinValue;
            _candidateAnalysis = null;
            SetState(MobGroupingState.ResumeCombat);
            _lastDecisionReason = DetailOrCode(code, detail);
            return new MobGroupingDecision(MobGroupingDirective.ResumeCombat,
                MoveNumber: _movesStarted, Reason: DetailOrCode(code, detail), Code: code);
        }

        private static string DetailOrCode(string code, string detail) =>
            string.IsNullOrEmpty(detail) ? code : detail;

        // ── Temporal + world observation ───────────────────────────────────────

        /// <summary>
        /// Adds one bounded formation observation (only when a detection sample was captured)
        /// and re-evaluates the trend. An unreliable sample invalidates the trend instead of
        /// producing a fabricated result.
        /// </summary>
        private void ObserveFormation(in MobGroupingTickInput input)
        {
            if (!input.DetectionCaptured)
                return;

            MobGroupAnalysis? analysis = input.Analysis is { IsValid: true } valid ? valid : null;
            bool reliable = analysis?.IsMovementSampleReliable == true;

            _formationHistory.Add(new MobFormationObservation(
                input.NowUtc,
                reliable,
                reliable ? analysis!.FormationQuality : 0f,
                analysis?.DetectedMobCount ?? 0,
                analysis?.Confidence ?? 0f,
                input.PlayerPositionValid,
                input.PlayerX,
                input.PlayerY,
                input.HasCameraYaw,
                input.CameraYawRadians * RadiansToDegrees,
                input.ClientWidth,
                input.ClientHeight));

            _lastTrend = _formationHistory.Evaluate(input.NowUtc);
        }

        /// <summary>Keeps a bounded window of world-position samples (no screenshots, no identities).</summary>
        private void ObservePlayerPosition(in MobGroupingTickInput input)
        {
            if (!input.PlayerPositionValid)
            {
                _playerSamples.Clear();
                return;
            }

            PrunePlayerSamples(input.NowUtc);

            if (_playerSamples.Count > 0 && _playerSamples[^1].AtUtc == input.NowUtc)
                _playerSamples[^1] = new PlayerSample(input.NowUtc, input.PlayerX, input.PlayerY);
            else
                _playerSamples.Add(new PlayerSample(input.NowUtc, input.PlayerX, input.PlayerY));

            while (_playerSamples.Count > BotConstants.MobGrouping.PlayerProgressMaxSamples)
                _playerSamples.RemoveAt(0);
        }

        private void PrunePlayerSamples(DateTime nowUtc)
        {
            int remove = 0;
            DateTime cutoff = nowUtc.AddMilliseconds(
                -BotConstants.MobGrouping.PlayerProgressWindowMs);
            while (remove < _playerSamples.Count && _playerSamples[remove].AtUtc < cutoff)
                remove++;
            if (remove > 0)
                _playerSamples.RemoveRange(0, remove);
        }

        private bool TryGetRecentMovement(
            DateTime nowUtc, out float deltaX, out float deltaY,
            out float distance, out double spanMs)
        {
            deltaX = 0f;
            deltaY = 0f;
            distance = 0f;
            spanMs = 0;

            PrunePlayerSamples(nowUtc);
            if (_playerSamples.Count < 2)
                return false;

            PlayerSample oldest = _playerSamples[0];
            PlayerSample newest = _playerSamples[^1];
            deltaX = newest.X - oldest.X;
            deltaY = newest.Y - oldest.Y;
            distance = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);
            spanMs = (newest.AtUtc - oldest.AtUtc).TotalMilliseconds;
            return spanMs > 0;
        }

        /// <summary>
        /// Verifies that the bot is already walking, in a direction compatible with the live
        /// route waypoint. This is the "continue an already useful movement" requirement: a
        /// stationary player never starts a speculative gather run.
        /// </summary>
        private bool IsRouteCompatibleProgress(
            in MobGroupingTickInput input, out string code, out string detail)
        {
            code = MobGroupingReason.NoMovementProgress;
            detail = "";

            if (!TryGetRecentMovement(input.NowUtc,
                    out float deltaX, out float deltaY, out float distance, out double spanMs))
            {
                detail = "not enough world-position samples";
                return false;
            }

            if (spanMs < BotConstants.MobGrouping.MinimumPlayerProgressSpanMs)
            {
                detail = $"progress window too short ({spanMs:F0}ms)";
                return false;
            }

            if (distance < BotConstants.MobGrouping.MinimumMovementProgressForGather)
            {
                detail = $"player is not making progress ({distance:F2} < " +
                    $"{BotConstants.MobGrouping.MinimumMovementProgressForGather:F2} world units in " +
                    $"{spanMs:F0}ms)";
                return false;
            }

            float waypointX = input.GatherTargetX - input.PlayerX;
            float waypointY = input.GatherTargetY - input.PlayerY;
            float waypointLength = MathF.Sqrt(waypointX * waypointX + waypointY * waypointY);
            if (waypointLength < 0.0001f)
            {
                code = MobGroupingReason.NoNavigationTarget;
                detail = "waypoint direction is undefined";
                return false;
            }

            float alignment = (deltaX * waypointX + deltaY * waypointY) /
                (distance * waypointLength);
            if (alignment < BotConstants.MobGrouping.MinimumDirectionAlignmentDot)
            {
                code = MobGroupingReason.MovementNotAligned;
                detail = $"movement direction is not route-compatible " +
                    $"(alignment {alignment:F2} < " +
                    $"{BotConstants.MobGrouping.MinimumDirectionAlignmentDot:F2})";
                return false;
            }

            code = "";
            return true;
        }

        /// <summary>
        /// Time-based combat-absence lifecycle for the encounter grouping count. It runs
        /// on every controller tick, before the enabled/mode gates, so the reset works
        /// even when detection is not running (feature disabled, a non-grouping mode,
        /// still an attack mode but no target). A single missed frame only starts the
        /// absence window; the count resets only after EncounterQuietResetMs of
        /// CONTINUOUS combat-context absence. No mob identity tracking is involved.
        /// The bad-detection grace (BadFormationGraceMs) remains independent.
        /// </summary>
        private void UpdateEncounterLifecycle(DateTime nowUtc, bool hasCombatContext)
        {
            if (hasCombatContext)
            {
                _combatAbsentSince = DateTime.MinValue;
                _quietResetLogged = false;
                return;
            }

            if (_combatAbsentSince == DateTime.MinValue)
                _combatAbsentSince = nowUtc;

            if (!_quietResetLogged &&
                nowUtc - _combatAbsentSince >= TimeSpan.FromMilliseconds(
                    BotConstants.MobGrouping.EncounterQuietResetMs))
            {
                _groupingsThisEncounter = 0;
                _encounterLimitLogged = false;
                _combatSince = DateTime.MinValue;
                ClearCandidate("encounter quiet reset");
                _lastSuppressionCode = "";
                _formationHistory.Clear();
                _lastTrend = MobFormationTrendResult.Unknown("encounter reset");
                _playerSamples.Clear();
                _quietResetLogged = true;
                _log($"[MobGrouping] combat context absent for the quiet reset period; " +
                     $"encounter grouping count reset [{MobGroupingReason.EncounterReset}].");
            }
        }

        private void ClearCandidate(string reason, string? suppressionCode = null)
        {
            if (_badFormationSince != DateTime.MinValue)
                _log($"[MobGrouping] candidate cancelled: {reason}.");
            _badFormationSince = DateTime.MinValue;
            _lastBadFormationAt = DateTime.MinValue;
            _candidateAnalysis = null;

            if (suppressionCode != null)
                LogSuppression(suppressionCode, reason);
        }

        /// <summary>Event-driven suppression logging: one line per distinct decision code.</summary>
        private void LogSuppression(string code, string detail)
        {
            if (string.Equals(_lastSuppressionCode, code, StringComparison.Ordinal))
                return;
            _lastSuppressionCode = code;
            _log($"[MobGrouping] gather not started [{code}]: {detail}.");
        }

        /// <summary>
        /// Logs a non-blocking observation (trend unavailable, standstill approach) once per code
        /// change. It deliberately does NOT touch <see cref="LastSuppressionCode"/>: the gather is
        /// proceeding, so nothing was suppressed.
        /// </summary>
        private void LogAdvisory(string code, string detail)
        {
            if (string.Equals(_lastAdvisoryCode, code, StringComparison.Ordinal))
                return;
            _lastAdvisoryCode = code;
            _log($"[MobGrouping] advisory [{code}]: {detail}.");
        }

        /// <summary>
        /// Logs once per barrier period when a candidate formation is waiting for the
        /// mandatory loot obligation to finish. It never starts a gather movement: the
        /// loot hold always outranks grouping.
        /// </summary>
        private void LogLootBarrierBlock(in MobGroupingTickInput input)
        {
            if (_lootBarrierBlockLogged || !input.DetectionCaptured)
                return;

            if (input.Analysis is not { IsValid: true } analysis)
                return;

            if (!IsGatherCandidate(analysis))
                return;

            _lootBarrierBlockLogged = true;
            _log("[MobGrouping] gather blocked: mandatory loot obligation owns movement - " +
                 "grouping waits for the verified loot completion.");
        }

        private static string? GetBlockReason(MobGroupingTickInput input)
        {
            if (!input.Enabled) return "feature disabled";
            if (!IsSupportedCombatMode(input.CurrentMode)) return $"mode changed to {input.CurrentMode}";
            if (input.IsPaused) return "bot paused";
            if (input.IsStopping) return "bot stopping";
            if (input.IsTransitioning) return "Repot/teleport transition";
            if (input.IsRecoveryActive) return "unstuck or waypoint recovery owns control";
            if (input.IsMandatoryLootBarrierActive) return LootBarrierBlockReason;
            if (input.IsLootHoldActive) return "loot hold owns control";
            if (!input.IsGameWindowValid) return "game window unavailable or unfocused";
            return null;
        }

        private static string BlockReasonCode(string blockReason) => blockReason switch
        {
            "feature disabled" => MobGroupingReason.FeatureDisabled,
            LootBarrierBlockReason => MobGroupingReason.LootOwnership,
            "loot hold owns control" => MobGroupingReason.LootOwnership,
            "bot paused" => MobGroupingReason.PauseOwnership,
            "bot stopping" => MobGroupingReason.PauseOwnership,
            "Repot/teleport transition" => MobGroupingReason.TransitionOwnership,
            "unstuck or waypoint recovery owns control" => MobGroupingReason.RecoveryOwnership,
            "game window unavailable or unfocused" => MobGroupingReason.WindowUnavailable,
            _ => MobGroupingReason.ModeUnsupported
        };

        private void SetState(MobGroupingState state) => Volatile.Write(ref _state, (int)state);
    }
}
