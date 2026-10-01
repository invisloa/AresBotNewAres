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
        float PlayerY);

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
        private DateTime _settleUntil = DateTime.MinValue;
        private DateTime _moveUntil = DateTime.MinValue;
        private DateTime _nextVerificationCaptureAt = DateTime.MinValue;
        /// <summary>When the current continuous combat-context absence started (MinValue = combat present).</summary>
        private DateTime _combatAbsentSince = DateTime.MinValue;
        private bool _quietResetLogged;
        private bool _encounterLimitLogged;
        private int _movesStarted;
        private int _groupingsThisEncounter;
        private float _moveStartX;
        private float _moveStartY;

        public MobGroupingController(Action<string>? log = null)
        {
            _log = log ?? (_ => { });
        }

        public MobGroupingState State => (MobGroupingState)Volatile.Read(ref _state);
        public int GroupingsThisEncounter => Volatile.Read(ref _groupingsThisEncounter);
        public int MovesStarted => Volatile.Read(ref _movesStarted);

        public bool IsSequenceActive => State is MobGroupingState.GroupingMove or
            MobGroupingState.Settling or MobGroupingState.Verify or MobGroupingState.ResumeCombat;

        public bool NeedsVerificationCapture(DateTime nowUtc) =>
            State == MobGroupingState.Verify && nowUtc >= _nextVerificationCaptureAt;

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
                if (input.NowUtc < _moveUntil)
                    return default;

                if (!input.PlayerPositionValid ||
                    GeometryUtils.Distance(input.PlayerX, input.PlayerY, _moveStartX, _moveStartY) <
                    BotConstants.MobGrouping.MinimumMoveProgress)
                {
                    _log("[MobGrouping] grouping aborted: temporary movement made no measurable progress.");
                    return Finish();
                }

                _settleUntil = input.NowUtc.AddMilliseconds(BotConstants.MobGrouping.SettleDelayMs);
                SetState(MobGroupingState.Settling);
                return new MobGroupingDecision(MobGroupingDirective.StopMovement, MoveNumber: _movesStarted);
            }

            if (State == MobGroupingState.Settling)
            {
                if (input.NowUtc < _settleUntil)
                    return default;

                _nextVerificationCaptureAt = input.NowUtc;
                SetState(MobGroupingState.Verify);
                return new MobGroupingDecision(MobGroupingDirective.VerifyFormation, MoveNumber: _movesStarted);
            }

            if (State == MobGroupingState.Verify)
            {
                if (!input.DetectionCaptured)
                    return default;

                if (input.Analysis == null || !input.Analysis.IsValid)
                {
                    _nextVerificationCaptureAt = input.NowUtc.AddMilliseconds(BotConstants.MobGrouping.DetectionIntervalMs);
                    return default;
                }

                MobGroupAnalysis analysis = input.Analysis;
                _log($"[MobGrouping] verification #{_movesStarted}: mobs={analysis.DetectedMobCount}, avg={analysis.AverageSpread:F1}px, max={analysis.MaximumSpread:F1}px.");
                if (analysis.DetectedMobCount < BotConstants.MobGrouping.MinimumMobs || analysis.IsSufficientlyClustered)
                {
                    _log("[MobGrouping] grouping successful: formation is sufficiently clustered.");
                    return Finish();
                }

                if (_movesStarted < BotConstants.MobGrouping.MaxMovesPerGrouping)
                {
                    _movesStarted++;
                    _moveStartX = input.PlayerX;
                    _moveStartY = input.PlayerY;
                    _moveUntil = input.NowUtc.AddMilliseconds(BotConstants.MobGrouping.GroupingMoveDurationMs);
                    SetState(MobGroupingState.GroupingMove);
                    _log($"[MobGrouping] movement attempt #{_movesStarted}/{BotConstants.MobGrouping.MaxMovesPerGrouping}, direction={analysis.RecommendedEscapeDirection}.");
                    return new MobGroupingDecision(
                        MobGroupingDirective.StartMove,
                        analysis.RecommendedEscapeDirection,
                        _movesStarted);
                }

                _log($"[MobGrouping] grouping ended after {_movesStarted} moves; maximum attempts reached.");
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
                    if (analysis.IsScattered)
                    {
                        if (_badFormationSince == DateTime.MinValue)
                        {
                            _badFormationSince = input.NowUtc;
                            _log($"[MobGrouping] scattered-formation candidate started (mobs={analysis.DetectedMobCount}, avg={analysis.AverageSpread:F1}px, max={analysis.MaximumSpread:F1}px).");
                        }
                        _lastBadFormationAt = input.NowUtc;
                        _candidateAnalysis = analysis;
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

            MobGroupAnalysis triggerAnalysis = _candidateAnalysis;
            _groupingsThisEncounter++;
            _movesStarted = 1;
            _moveStartX = input.PlayerX;
            _moveStartY = input.PlayerY;
            _moveUntil = input.NowUtc.AddMilliseconds(BotConstants.MobGrouping.GroupingMoveDurationMs);
            SetState(MobGroupingState.GroupingMove);
            _log($"[MobGrouping] grouping triggered: mobs={triggerAnalysis.DetectedMobCount}, avg={triggerAnalysis.AverageSpread:F1}px, max={triggerAnalysis.MaximumSpread:F1}px, direction={triggerAnalysis.RecommendedEscapeDirection}.");
            _log($"[MobGrouping] movement attempt #1/{BotConstants.MobGrouping.MaxMovesPerGrouping}, direction={triggerAnalysis.RecommendedEscapeDirection}.");
            return new MobGroupingDecision(
                MobGroupingDirective.StartMove,
                triggerAnalysis.RecommendedEscapeDirection,
                _movesStarted);
        }

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
