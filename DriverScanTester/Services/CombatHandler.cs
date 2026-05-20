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
        private const int KnightStuckMin = 16500;
        private const int KnightStuckMax = 17000;
        private const int KnightAttackedMin = 17300;
        private const int KnightAttackedMax = 17400;

        // ── Timings ──
        private const double IDLE_TIMEOUT_SECONDS = 0.5;
        private const double MOVE_MODE_TAB_INTERVAL_SECONDS = 0.8;

        // ── State ──
        private bool _wasAttacking;
        private DateTime _lastNonIdleActionTime = DateTime.MinValue;
        private int _stuckInAttackCounter;
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
            if ((DateTime.Now - _lastAttackSpeedCheck).TotalSeconds >= 5)
            {
                short attackSpeed = memoryService.GetAttackSpeed();
                _lastAttackSpeedCheck = DateTime.Now;
                return attackSpeed == 16341;
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
                }

                // A_CurrentAction values: 25=idle, 27=running, 28=being hit, 39=attacking (knight sword)
                if (currentAction == 27 || currentAction == 28 || currentAction == 39)
                {
                    _lastNonIdleActionTime = DateTime.Now;
                    _stuckInAttackCounter = 0;
                }
                else if (currentAction == 0 && (DateTime.Now - _lastNonIdleActionTime).TotalSeconds >= IDLE_TIMEOUT_SECONDS)
                {
                    _log("[Combat] Action idle > 0.5s. Mob dead or stuck. TAB.");
                    _wasAttacking = false;
                    return CombatAction.TabTarget;
                }

                // ── Stuck-in-attack detection ──
                if (anim1 > KnightStuckMin && anim1 < KnightStuckMax)
                {
                    _stuckInAttackCounter++;

                    if (_stuckInAttackCounter < 20)
                    {
                        if (_stuckInAttackCounter % 5 == 0)
                            _log($"Stuck in attack (Anim: {anim1}). Switching target (TAB + 3). Attempt {_stuckInAttackCounter}");

                        _log("[Key] TAB (target cycle) — stuck in attack");
                        return CombatAction.StuckTabAttack;
                    }
                    else
                    {
                        _log("Stuck in attack persisted. Starting Unstuck Routine.");
                        return CombatAction.UnstuckNeeded;
                    }
                }
                else
                {
                    _stuckInAttackCounter = 0;
                }

                // ── Normal attack ──
                _log("[Key] 3 (attack skill)");
                return CombatAction.Attack;
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
            _stuckInAttackCounter = 0;
        }

        /// <summary>
        /// Resets all combat tracking state (called when waypoints advance or unstuck ends).
        /// </summary>
        public void ResetState()
        {
            _wasAttacking = false;
            _stuckInAttackCounter = 0;
        }
    }
}
