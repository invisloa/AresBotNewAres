using System;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Result returned by <see cref="CombatHandler.EvaluateCombatAction"/>.
    /// Tells the caller what input action, if any, should be performed next.
    /// </summary>
    internal enum CombatAction
    {
        /// <summary>No combat action required — proceed with normal movement.</summary>
        None,
        /// <summary>Press TAB to cycle target (non-attacking move mode).</summary>
        TabTarget,
        /// <summary>Press 3 to use attack skill (and stop moving).</summary>
        Attack,
        /// <summary>Waiting during combat (attack cooldown / stuck debounce) — skip movement entirely.</summary>
        CombatWait,
        /// <summary>Press TAB (stuck in attack) to switch target.</summary>
        StuckTabAttack,
        /// <summary>Start the unstuck routine — too many stuck-in-attack attempts.</summary>
        UnstuckNeeded,
        /// <summary>Potion keys were pressed — caller should delay briefly.</summary>
        PotionsUsed
    }

    /// <summary>
    /// Encapsulates all combat-related logic extracted from MovementSystem.
    /// Evaluates game state and returns <see cref="CombatAction"/> instructions.
    /// </summary>
    internal class CombatHandler
    {
        // ── Knight Animation Constants ──
        private const int KnightAttackedMin = BotConstants.Combat.KnightAttackedMin;
        private const int KnightAttackedMax = BotConstants.Combat.KnightAttackedMax;

        // ── Timings ──
        private const double IDLE_TIMEOUT_SECONDS = BotConstants.Combat.IdleTimeoutSeconds;
        private const double MOVE_MODE_TAB_INTERVAL_SECONDS = BotConstants.Combat.MoveModeTabIntervalSeconds;

        // ── Combat stuck detection (same pattern as StuckDetector) ──
        // Uses GetCurrentAction as the source of truth:
        // - action 27 or 3 → running (not stuck)
        // - action 25 or 1 → idle/stuck
        private const int CombatStuckRequiredSamples = BotConstants.Combat.StuckRequiredSamples;
        private const double CombatStuckRequiredMs = BotConstants.Combat.StuckRequiredMs;

        // ── State ──
        private bool _wasAttacking;
        private DateTime _lastNonIdleActionTime = DateTime.MinValue;
        private int _combatStuckSamples;
        private DateTime _combatStuckFirstSeenAt = DateTime.MinValue;
        private DateTime _lastMoveModeTabTime = DateTime.MinValue;
        private DateTime _lastAttackSpeedCheck = DateTime.MinValue;

        // ── Logging ──
        private readonly Action<string> _log;

        public CombatHandler(Action<string> log)
        {
            _log = log;
        }

        /// <summary>
        /// Checks whether it is time to drink attack-speed potions.
        /// Returns true when potion keys should be pressed.
        /// </summary>
        public bool CheckAttackSpeed(GameMemoryService memoryService)
        {
            if ((DateTime.Now - _lastAttackSpeedCheck).TotalSeconds >= BotConstants.SpeedPotion.CheckIntervalSeconds)
            {
                short attackSpeed = memoryService.GetAttackSpeed();
                _lastAttackSpeedCheck = DateTime.Now;
                return attackSpeed == BotConstants.SpeedPotion.AttackSpeedThreshold;
            }
            return false;
        }

        /// <summary>
        /// Evaluates the current combat situation and returns the action MovementSystem should take.
        /// This method is synchronous — no async or I/O operations.
        /// </summary>
        /// <param name="memoryService">Game memory service for reading state.</param>
        /// <param name="currentMode">The bot mode of the current waypoint.</param>
        /// <param name="isUnstuckActive">Whether the unstuck routine is currently active.</param>
        /// <returns>A <see cref="CombatAction"/> describing what input to perform next.</returns>
        public CombatAction EvaluateCombatAction(
            GameMemoryService memoryService,
            BotMode currentMode,
            bool isUnstuckActive)
        {
            if (currentMode != BotMode.MoveAndAttack && currentMode != BotMode.MoveAndAttackAndLoot)
            {
                return CombatAction.None;
            }

            if (isUnstuckActive)
            {
                return CombatAction.None;
            }

            int anim1 = memoryService.GetAnimation1();
            if (anim1 > KnightAttackedMin && anim1 < KnightAttackedMax)
            {
                _log($"[Combat] Player is being attacked (Anim: {anim1})");
            }

            int attackVal = memoryService.GetAttackStatus();
            byte currentAction = memoryService.GetCurrentAction();
            _log($"[Combat] Evaluate: anim1={anim1} attackVal={attackVal} currentAction={currentAction} mode={currentMode} isUnstuck={isUnstuckActive}");

            if (attackVal > 0)
            {
                // ── Attacking ──
                if (!_wasAttacking)
                {
                    _wasAttacking = true;
                    _lastNonIdleActionTime = DateTime.Now;
                    _combatStuckSamples = 0;
                    return CombatAction.Attack;
                }

                // ── Stuck detection (same pattern as StuckDetector) ──
                // Uses GetCurrentAction as the source of truth:
                // - action 27 or 3 → running (not stuck)
                // - action 25 or 1 → idle/stuck
                if (StuckDetector.IsActionIdleOrStuck(currentAction))
                {
                    if (_combatStuckSamples == 0)
                        _combatStuckFirstSeenAt = DateTime.Now;
                    _combatStuckSamples++;

                    if (_combatStuckSamples >= CombatStuckRequiredSamples ||
                        (DateTime.Now - _combatStuckFirstSeenAt).TotalMilliseconds >= CombatStuckRequiredMs)
                    {
                        _log("[Combat] Action stuck. Mob dead or stuck. TAB.");
                        _wasAttacking = false;
                        return CombatAction.TabTarget;
                    }

                    // Waiting for debounce threshold — skip movement but keep W released
                    return CombatAction.CombatWait;
                }

                // ── Fallback: action 0 idle timeout ──
                if (currentAction == 0 && (DateTime.Now - _lastNonIdleActionTime).TotalSeconds >= IDLE_TIMEOUT_SECONDS)
                {
                    _log("[Combat] Action idle > 0.5s. Mob dead or stuck. TAB.");
                    _wasAttacking = false;
                    return CombatAction.TabTarget;
                }

                // ── Update non-idle timestamp for running/attacking actions ──
                if (currentAction == 27 || currentAction == 3 || currentAction == 28 || currentAction == 39)
                {
                    _lastNonIdleActionTime = DateTime.Now;
                    _combatStuckSamples = 0;
                }

                // Skill 3 is held by MovementSystem — skip movement, keep waiting in combat
                return CombatAction.CombatWait;
            }
            else
            {
                // ── Not attacking — cycle target periodically ──
                _wasAttacking = false;

                if ((DateTime.Now - _lastMoveModeTabTime).TotalSeconds >= MOVE_MODE_TAB_INTERVAL_SECONDS)
                {
                    _log("[Key] TAB (target cycle — move mode)");
                    _lastMoveModeTabTime = DateTime.Now;
                    return CombatAction.TabTarget;
                }
            }

            return CombatAction.None;
        }

        /// <summary>
        /// Resets the stuck-in-attack counter (called when unstuck routine starts due to stuck in attack).
        /// </summary>
        public void ResetStuckInAttack()
        {
            _combatStuckSamples = 0;
        }

        /// <summary>
        /// Resets all combat tracking state (called when waypoints advance or unstuck ends).
        /// </summary>
        public void ResetState()
        {
            _wasAttacking = false;
            _combatStuckSamples = 0;
        }
    }
}
