using System;
using DriverScanTester.Models;

namespace DriverScanTester.Services
{
    /// <summary>Explicit state of the per-monster skill-4 opening sequence.</summary>
    internal enum DotOpeningPhase
    {
        /// <summary>No DoT encounter is active — ordinary combat behavior applies.</summary>
        Inactive,

        /// <summary>The current monster is receiving its configured skill-4 opening activations.</summary>
        OpeningWithSkill4,

        /// <summary>The opening finished; the existing skill-3 attack owns combat.</summary>
        MainAttackWithSkill3
    }

    /// <summary>
    /// Small, tick-driven state machine for the DoT opening attack.
    ///
    /// It only tracks which monster owns the sequence, how many skill-4 activations were
    /// initiated, when the next activation may be sent and which phase is active. It does
    /// not read memory, send input, start threads or create a second combat loop: the
    /// existing <see cref="MovementSystem"/> combat tick drives it and performs every
    /// safety validation immediately before the physical key press.
    /// </summary>
    internal sealed class DotOpeningController
    {
        private readonly Action<string> _log;

        public DotOpeningController(Action<string> log)
        {
            _log = log ?? (_ => { });
        }

        public DotOpeningPhase Phase { get; private set; } = DotOpeningPhase.Inactive;

        /// <summary>Monster id that owns the current sequence (0 when inactive).</summary>
        public int ActiveTargetId { get; private set; }

        /// <summary>Class resolved when the current encounter started.</summary>
        public PlayerClass ActiveClass { get; private set; } = PlayerClass.Unknown;

        public int RequiredOpeningAttacks { get; private set; }

        public int CompletedOpeningActivations { get; private set; }

        public int AttackIntervalMs { get; private set; }

        /// <summary>Earliest time the next skill-4 activation may be initiated (UTC).</summary>
        public DateTime NextAttackAllowedAt { get; private set; } = DateTime.MinValue;

        public bool IsOpeningActive => Phase == DotOpeningPhase.OpeningWithSkill4;

        public bool IsMainAttackActive => Phase == DotOpeningPhase.MainAttackWithSkill3;

        public bool OwnsAttackSequence => Phase != DotOpeningPhase.Inactive;

        public bool IsOpeningCompleteAwaitingSwitch =>
            Phase == DotOpeningPhase.OpeningWithSkill4 &&
            CompletedOpeningActivations >= RequiredOpeningAttacks;

        /// <summary>
        /// Starts an opening for <paramref name="targetId"/>. Starting a different monster
        /// discards the previous monster's progress (a direct A → B replacement is a new
        /// encounter). Starting the same monster is never done by the caller.
        /// </summary>
        public void BeginEncounter(
            int targetId,
            PlayerClass playerClass,
            int requiredOpeningAttacks,
            int attackIntervalMs,
            DateTime nowUtc)
        {
            int previousTargetId = ActiveTargetId;

            Phase = DotOpeningPhase.OpeningWithSkill4;
            ActiveTargetId = targetId;
            ActiveClass = playerClass;
            RequiredOpeningAttacks = requiredOpeningAttacks;
            CompletedOpeningActivations = 0;
            AttackIntervalMs = attackIntervalMs;
            NextAttackAllowedAt = nowUtc;

            if (previousTargetId > 0 && previousTargetId != targetId)
                _log($"[DoT] Target changed {previousTargetId} -> {targetId}, new opening");
            else
                _log($"[DoT] New target={targetId}, starting opening sequence");
        }

        public bool CanActivate(DateTime nowUtc) =>
            Phase == DotOpeningPhase.OpeningWithSkill4 &&
            CompletedOpeningActivations < RequiredOpeningAttacks &&
            nowUtc >= NextAttackAllowedAt;

        public bool CanSwitchToMainAttack(DateTime nowUtc) =>
            Phase == DotOpeningPhase.OpeningWithSkill4 &&
            CompletedOpeningActivations >= RequiredOpeningAttacks &&
            nowUtc >= NextAttackAllowedAt;

        /// <summary>
        /// Records one INITIATED skill-4 activation. The count is not a confirmed hit count
        /// (the memory API exposes no per-hit signal); it is exactly the number of key
        /// presses the sequence performed.
        /// </summary>
        public void RegisterActivation(DateTime nowUtc)
        {
            if (Phase != DotOpeningPhase.OpeningWithSkill4)
                return;

            CompletedOpeningActivations++;
            NextAttackAllowedAt = nowUtc.AddMilliseconds(AttackIntervalMs);
            _log($"[DoT] Target={ActiveTargetId}, skill 4 activation " +
                 $"{CompletedOpeningActivations}/{RequiredOpeningAttacks}");
        }

        public void CompleteOpening(DateTime nowUtc)
        {
            if (Phase != DotOpeningPhase.OpeningWithSkill4)
                return;

            Phase = DotOpeningPhase.MainAttackWithSkill3;
            NextAttackAllowedAt = DateTime.MinValue;
            _log($"[DoT] Target={ActiveTargetId}, opening complete; switching to skill 3");
        }

        /// <summary>
        /// Keeps progress but drops a stale activation timestamp after an interruption
        /// (grouping/loot/pause). The next allowed activation becomes "now", so the
        /// sequence resumes with normal spacing and never fires several delayed
        /// activations at once.
        /// </summary>
        public void Suspend(DateTime nowUtc)
        {
            if (!OwnsAttackSequence)
                return;
            if (NextAttackAllowedAt < nowUtc)
                NextAttackAllowedAt = nowUtc;
        }

        /// <summary>
        /// Cancels the sequence. Cancelling an inactive controller is silent so callers can
        /// invoke it idempotently on every cleanup path.
        /// </summary>
        public void Cancel(string reason, bool log = true)
        {
            if (Phase == DotOpeningPhase.Inactive)
                return;

            int targetId = ActiveTargetId;
            string label = Phase == DotOpeningPhase.OpeningWithSkill4 ? "opening" : "skill 3 sequence";
            if (log)
                _log($"[DoT] Target={targetId}, {label} cancelled: {reason}");

            Phase = DotOpeningPhase.Inactive;
            ActiveTargetId = 0;
            ActiveClass = PlayerClass.Unknown;
            RequiredOpeningAttacks = 0;
            CompletedOpeningActivations = 0;
            AttackIntervalMs = 0;
            NextAttackAllowedAt = DateTime.MinValue;
        }
    }
}
