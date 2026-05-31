using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

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
        /// <summary>Waiting during combat (attack cooldown) — skip movement entirely.</summary>
        CombatWait,
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

        // ── State ──
        private bool _wasAttacking;
        private DateTime _lastNonIdleActionTime = DateTime.MinValue;
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
                // ── Player character check: skip attacking other players ──
                if (memoryService.IsPlayerSelected())
                {
                    CapturePlayerScreenshot();
                    _log("[Combat] Target is a player character — skipping attack. TAB.");
                    _wasAttacking = false;
                    return CombatAction.TabTarget;
                }

                // ── Attacking ──
                if (!_wasAttacking)
                {
                    _wasAttacking = true;
                    _lastNonIdleActionTime = DateTime.Now;
                    return CombatAction.Attack;
                }

                // ── Stuck detection via StuckDetector ──
                // Action 25 or 1 = idle/stuck → mob dead or stuck, TAB to retarget
                if (StuckDetector.IsActionIdleOrStuck(currentAction))
                {
                    _log("[Combat] Action stuck. Mob dead or stuck. TAB.");
                    _wasAttacking = false;
                    return CombatAction.TabTarget;
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
        /// Captures the current screen and saves it as a PNG file in the Screenshots subfolder
        /// with the prefix "PlayerSelectedSS_" and the current date/time.
        /// Called when a player character (not a mob/NPC) is selected as a target.
        /// </summary>
        private void CapturePlayerScreenshot()
        {
            try
            {
                using (Bitmap bitmap = new Bitmap(BotConstants.Loot.BitmapWidth, BotConstants.Loot.BitmapHeight))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(0, 0, 0, 0, bitmap.Size);

                    string screenshotsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Screenshots", "PlayerSelected");
                    Directory.CreateDirectory(screenshotsDir);

                    string fileName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";
                    string filePath = Path.Combine(screenshotsDir, fileName);

                    bitmap.Save(filePath, ImageFormat.Png);
                    _log($"[Combat] Player screenshot saved: {filePath}");
                }
            }
            catch (Exception ex)
            {
                _log($"[Combat] Failed to save player screenshot: {ex.Message}");
            }
        }

        /// <summary>
        /// Resets all combat tracking state (called when waypoints advance or unstuck ends).
        /// </summary>
        public void ResetState()
        {
            _wasAttacking = false;
        }
    }
}
