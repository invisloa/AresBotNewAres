using System;
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
        bool HasGatherNavigationTarget = true);

    /// <summary>
    /// Tick-driven, deterministic grouping state machine. It has no image, combat, keyboard,
    /// path or profile dependencies; MovementSystem executes the directives it returns.
    /// </summary>
    public sealed class MobGroupingController
    {
        private readonly Action<string> _log;
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
        private int _movesStarted;
        private int _groupingsThisEncounter;
        private float _moveStartX;
        private float _moveStartY;
        /// <summary>Randomness source for the one-per-attempt gather deadline. Injectable for deterministic tests.</summary>
        private readonly Random _random;

        public MobGroupingController(Action<string>? log = null, Random? random = null)
        {
            _log = log ?? (_ => { });
            _random = random ?? Random.Shared;
        }

        public MobGroupingState State => (MobGroupingState)Volatile.Read(ref _state);
        public int GroupingsThisEncounter => Volatile.Read(ref _groupingsThisEncounter);
        public int MovesStarted => Volatile.Read(ref _movesStarted);

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
            if (active && blockReason != null)
                return CancelSequence(input.NowUtc, blockReason);

            // Losing the combat target during an active sequence must end grouping
            // immediately: the post-kill SOD/SOP flow (CombatHandler -> TabAfterKill ->
            // LootSystem.RequestPinkScan) must never be delayed by a temporary grouping
            // walk. This is a normal termination, not a failure.
            if (active && !input.HasCombatContext)
                return CancelSequence(input.NowUtc, "combat context ended");

            UpdateEncounterLifecycle(input.NowUtc, input.HasCombatContext);

            if (State == MobGroupingState.ResumeCombat)
                return new MobGroupingDecision(MobGroupingDirective.ResumeCombat, Reason: "complete");

            if (!input.Enabled)
            {
                ClearCandidate("disabled");
                _combatSince = DateTime.MinValue;
                if (State != MobGroupingState.Cooldown)
                    SetState(MobGroupingState.Idle);
                return default;
            }

            if (input.CurrentMode != BotMode.MoveAndAttack)
            {
                ClearCandidate("mode is not MoveAndAttack");
                _combatSince = DateTime.MinValue;
                if (!IsSequenceActive && State != MobGroupingState.Cooldown)
                    SetState(MobGroupingState.Idle);
                return default;
            }

            if (blockReason != null)
            {
                _combatSince = DateTime.MinValue;
                if (blockReason != "game window unavailable or unfocused")
                    ClearCandidate(blockReason);
                else if (_badFormationSince != DateTime.MinValue &&
                         input.NowUtc - _lastBadFormationAt > TimeSpan.FromMilliseconds(BotConstants.MobGrouping.BadFormationGraceMs))
                    ClearCandidate("detector unavailable beyond grace period");
                if (State != MobGroupingState.Cooldown)
                    SetState(MobGroupingState.Idle);
                return default;
            }

            if (State == MobGroupingState.GroupingMove)
            {
                // A valid positional route target is the only gather destination. When the
                // current waypoint becomes reached (or the route has no usable positional
                // target), hand control back to the ordinary path pipeline so it can run the
                // waypoint's arrival semantics. The controller never dequeues or advances
                // the path and never picks a replacement waypoint.
                if (!input.HasGatherNavigationTarget)
                {
                    double reachedMs = (input.NowUtc - _gatherStartedAt).TotalMilliseconds;
                    _log($"[MobGrouping] gather navigation target reached or unavailable at {reachedMs:F0}ms - releasing path control.");
                    return Finish();
                }

                // Periodic rescan answer: finish as soon as the detected formation is
                // already grouped enough for AOE. The success condition is the same
                // single predicate the gather verify step always used, fed by the
                // 500 ms rescans. Waypoint navigation continues live every tick through
                // the ordinary MovementSystem steering.
                bool groupedEnough = input.DetectionCaptured &&
                    input.Analysis is { IsValid: true } gatherAnalysis &&
                    IsGatheringComplete(gatherAnalysis);

                if (input.DetectionCaptured)
                {
                    if (input.Analysis is { IsValid: true } rescan)
                        _log($"[MobGrouping] gather rescan: mobs={rescan.DetectedMobCount}, avg={rescan.AverageSpread:F1}px, max={rescan.MaximumSpread:F1}px.");
                    else
                        _log("[MobGrouping] gather rescan: no valid detection this frame.");
                }

                if (!groupedEnough && input.NowUtc < _moveUntil)
                {
                    // Movement continues toward the live route target. Schedule the next rescan.
                    if (input.DetectionCaptured)
                        _nextGatherRescanAt = input.NowUtc.AddMilliseconds(
                            BotConstants.MobGrouping.GatherRescanIntervalMs);
                    return default;
                }

                if (!input.PlayerPositionValid ||
                    GeometryUtils.Distance(input.PlayerX, input.PlayerY, _moveStartX, _moveStartY) <
                    BotConstants.MobGrouping.MinimumMoveProgress)
                {
                    _log("[MobGrouping] grouping aborted: temporary movement made no measurable progress.");
                    return Finish();
                }

                double elapsedMs = (input.NowUtc - _gatherStartedAt).TotalMilliseconds;
                if (groupedEnough)
                    _log($"[MobGrouping] gather finished early at {elapsedMs:F0}ms: formation is sufficiently clustered - stopping movement and resuming combat.");
                else
                    _log($"[MobGrouping] gather deadline reached at {elapsedMs:F0}ms - stopping movement and resuming combat.");
                return Finish();
            }

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
                    input.NowUtc - _lastBadFormationAt > TimeSpan.FromMilliseconds(BotConstants.MobGrouping.BadFormationGraceMs))
                    ClearCandidate("combat context ended");
                return default;
            }

            if (_combatSince == DateTime.MinValue)
                _combatSince = input.NowUtc;

            if (_groupingsThisEncounter >= BotConstants.MobGrouping.MaxGroupingsPerEncounter)
            {
                ClearCandidate("encounter grouping limit reached");
                if (input.DetectionCaptured && input.Analysis is { IsScattered: true } && !_encounterLimitLogged)
                {
                    _encounterLimitLogged = true;
                    _log($"[MobGrouping] candidate blocked: encounter limit {_groupingsThisEncounter}/{BotConstants.MobGrouping.MaxGroupingsPerEncounter} reached.");
                }
                return default;
            }

            if (input.DetectionCaptured)
            {
                if (input.Analysis is { IsValid: true } analysis)
                {
                    // Gathering is only required for a scattered formation that does NOT
                    // already contain an attack-ready local cluster. With many visible mobs
                    // an existing four-mob cluster is enough to attack even when outliers
                    // would keep the whole formation scattered.
                    bool gatheringNeeded = analysis.IsScattered &&
                        !analysis.IsSufficientlyClustered &&
                        !analysis.HasAttackReadyCluster;
                    if (gatheringNeeded)
                    {
                        if (_badFormationSince == DateTime.MinValue)
                        {
                            _badFormationSince = input.NowUtc;
                            _log($"[MobGrouping] scattered-formation candidate started (mobs={analysis.DetectedMobCount}, avg={analysis.AverageSpread:F1}px, max={analysis.MaximumSpread:F1}px).");
                        }
                        _lastBadFormationAt = input.NowUtc;
                        _candidateAnalysis = analysis;
                    }
                    else if (analysis.HasAttackReadyCluster)
                    {
                        ClearCandidate("attack-ready cluster already exists");
                    }
                    else if (analysis.DetectedMobCount >= BotConstants.MobGrouping.MinimumMobs)
                    {
                        ClearCandidate("valid formation is not scattered");
                    }
                    else if (_badFormationSince != DateTime.MinValue &&
                             input.NowUtc - _lastBadFormationAt > TimeSpan.FromMilliseconds(BotConstants.MobGrouping.BadFormationGraceMs))
                    {
                        ClearCandidate("too few markers");
                    }
                }
                else if (_badFormationSince != DateTime.MinValue &&
                         input.NowUtc - _lastBadFormationAt > TimeSpan.FromMilliseconds(BotConstants.MobGrouping.BadFormationGraceMs))
                {
                    ClearCandidate("detector unavailable beyond grace period");
                }
            }

            if (_badFormationSince == DateTime.MinValue || _candidateAnalysis == null)
                return default;

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

            // No positional route target to walk toward: never invent an escape direction.
            // The candidate stays fresh so the gather can still start once the ordinary
            // path pipeline advances past a reached waypoint and exposes a valid target.
            if (!input.HasGatherNavigationTarget)
                return default;

            MobGroupAnalysis triggerAnalysis = _candidateAnalysis;
            _groupingsThisEncounter++;
            _movesStarted = 1;
            _moveStartX = input.PlayerX;
            _moveStartY = input.PlayerY;
            // One random deadline per gather attempt: nominal +/- variation, selected once
            // and kept for the whole walk (the periodic rescans never re-roll it).
            int gatherDurationMs = ChooseGatherDurationMs();
            _gatherStartedAt = input.NowUtc;
            _moveUntil = input.NowUtc.AddMilliseconds(gatherDurationMs);
            _nextGatherRescanAt = input.NowUtc.AddMilliseconds(
                BotConstants.MobGrouping.GatherRescanIntervalMs);
            SetState(MobGroupingState.GroupingMove);
            _log($"[MobGrouping] grouping triggered: mobs={triggerAnalysis.DetectedMobCount}, avg={triggerAnalysis.AverageSpread:F1}px, max={triggerAnalysis.MaximumSpread:F1}px.");
            _log($"[MobGrouping] gather movement started toward the current route waypoint: duration={gatherDurationMs}ms, rescan={BotConstants.MobGrouping.GatherRescanIntervalMs}ms.");
            return new MobGroupingDecision(
                MobGroupingDirective.StartMove,
                triggerAnalysis.RecommendedEscapeDirection,
                _movesStarted);
        }

        /// <summary>
        /// Selects the per-attempt gather movement deadline once: nominal duration +/- the
        /// configured variation, clamped to a positive range. The same value is used for the
        /// entire attempt; rescans only observe, they never re-randomize or extend it.
        /// </summary>
        internal int ChooseGatherDurationMs()
        {
            int nominal = BotConstants.MobGrouping.GatherMoveNominalDurationMs;
            int variation = BotConstants.MobGrouping.GatherMoveDurationVariationMs;
            int min = Math.Max(1, nominal - variation);
            int max = Math.Max(min, nominal + variation);
            return _random.Next(min, max + 1);
        }

        /// <summary>
        /// Single source of truth for "the gather succeeded": at least the existing minimum
        /// mob count is still detected AND the analyzer says either the WHOLE formation is
        /// clustered or (with many mobs) an attack-ready local cluster of
        /// <see cref="BotConstants.MobGrouping.AttackReadyClusterMobCount"/> exists. A frame
        /// that detects fewer markers (including none) never counts as success during the
        /// walk - the bounded gather deadline ends an attempt that cannot confirm a group.
        /// The spread/cluster thresholds themselves are unchanged.
        /// </summary>
        internal static bool IsGatheringComplete(MobGroupAnalysis analysis) =>
            analysis.DetectedMobCount >= BotConstants.MobGrouping.MinimumMobs &&
            (analysis.IsSufficientlyClustered || analysis.HasAttackReadyCluster);

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
                return CancelSequence(nowUtc, reason);

            if (State == MobGroupingState.Observing)
            {
                ClearCandidate(reason);
                _combatSince = DateTime.MinValue;
                SetState(MobGroupingState.Idle);
            }
            return default;
        }

        private MobGroupingDecision CancelSequence(DateTime nowUtc, string reason)
        {
            _log($"[MobGrouping] grouping cancelled: {reason}.");
            _cooldownUntil = nowUtc.AddMilliseconds(BotConstants.MobGrouping.CooldownMs);
            _badFormationSince = DateTime.MinValue;
            _lastBadFormationAt = DateTime.MinValue;
            _candidateAnalysis = null;
            SetState(MobGroupingState.Cooldown);
            _log($"[MobGrouping] cooldown started: {BotConstants.MobGrouping.CooldownMs}ms.");
            return new MobGroupingDecision(MobGroupingDirective.Cancelled, MoveNumber: _movesStarted, Reason: reason);
        }

        private MobGroupingDecision Finish()
        {
            _badFormationSince = DateTime.MinValue;
            _lastBadFormationAt = DateTime.MinValue;
            _candidateAnalysis = null;
            SetState(MobGroupingState.ResumeCombat);
            return new MobGroupingDecision(MobGroupingDirective.ResumeCombat, MoveNumber: _movesStarted, Reason: "complete");
        }

        /// <summary>
        /// Time-based combat-absence lifecycle for the encounter grouping count. It runs
        /// on every controller tick, before the enabled/mode gates, so the reset works
        /// even when detection is not running (feature disabled, mode not MoveAndAttack,
        /// still in MoveAndAttack but no target). A single missed frame only starts the
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
                nowUtc - _combatAbsentSince >= TimeSpan.FromMilliseconds(BotConstants.MobGrouping.EncounterQuietResetMs))
            {
                _groupingsThisEncounter = 0;
                _encounterLimitLogged = false;
                _combatSince = DateTime.MinValue;
                ClearCandidate("encounter quiet reset");
                _quietResetLogged = true;
                _log("[MobGrouping] combat context absent for the quiet reset period; encounter grouping count reset.");
            }
        }

        private void ClearCandidate(string reason)
        {
            if (_badFormationSince != DateTime.MinValue)
                _log($"[MobGrouping] candidate cancelled: {reason}.");
            _badFormationSince = DateTime.MinValue;
            _lastBadFormationAt = DateTime.MinValue;
            _candidateAnalysis = null;
        }

        private static string? GetBlockReason(MobGroupingTickInput input)
        {
            if (!input.Enabled) return "feature disabled";
            if (input.CurrentMode != BotMode.MoveAndAttack) return $"mode changed to {input.CurrentMode}";
            if (input.IsPaused) return "bot paused";
            if (input.IsStopping) return "bot stopping";
            if (input.IsTransitioning) return "Repot/teleport transition";
            if (input.IsRecoveryActive) return "unstuck or waypoint recovery owns control";
            if (input.IsLootHoldActive) return "loot hold owns control";
            if (!input.IsGameWindowValid) return "game window unavailable or unfocused";
            return null;
        }

        private void SetState(MobGroupingState state) => Volatile.Write(ref _state, (int)state);
    }
}
