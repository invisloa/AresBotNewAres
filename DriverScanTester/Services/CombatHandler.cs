using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

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
        private DateTime _combatIdleStartTime = DateTime.MinValue;

        /// <summary>
        /// Minimum ms of continuous idle action (0/25/1) while a target is still selected
        /// before declaring the mob dead and TABbing. Prevents false triggers from brief
        /// animation pauses like hit-recovery or action transitions.
        /// </summary>
        private const double COMBAT_IDLE_TIMEOUT_MS = 1000.0;

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
            int attackVal = memoryService.GetAttackStatus();
            byte currentAction = memoryService.GetCurrentAction();

            if (anim1 > KnightAttackedMin && anim1 < KnightAttackedMax)
            {
                _log($"[Combat] Player is being attacked (Anim: {anim1})");
            }

            _log($"[Combat] Evaluate: anim1={anim1} attackVal={attackVal} currentAction={currentAction} mode={currentMode} isUnstuck={isUnstuckActive}");

            if (attackVal > 0)
            {
                // ── Player character check: skip attacking other players ──
                if (memoryService.IsPlayerSelected())
                {
                    CapturePlayerScreenshot();
                    _log("[Combat] Target is a player character — skipping attack. TAB.");
                    _wasAttacking = false;
                    _combatIdleStartTime = DateTime.MinValue;
                    return CombatAction.TabTarget;
                }

                // ── Attacking ──
                if (!_wasAttacking)
                {
                    _wasAttacking = true;
                    _lastNonIdleActionTime = DateTime.Now;
                    _combatIdleStartTime = DateTime.MinValue;
                    return CombatAction.Attack;
                }

                // ── Unified idle/stuck detection ──
                // If a mob is still selected, require 1000ms of continuous idle
                // action (0/25/1) before declaring it dead. Brief animation pauses
                // from hit-recovery, action transitions, etc. are ignored.
                if (memoryService.IsMobSelected())
                {
                    bool isIdle = StuckDetector.IsActionIdleOrStuck(currentAction) || currentAction == 0;
                    if (isIdle)
                    {
                        if (_combatIdleStartTime == DateTime.MinValue)
                        {
                            _combatIdleStartTime = DateTime.Now;
                            _log($"[Combat] Action idle — waiting {COMBAT_IDLE_TIMEOUT_MS}ms before declaring stuck.");
                            return CombatAction.CombatWait;
                        }

                        double idleMs = (DateTime.Now - _combatIdleStartTime).TotalMilliseconds;
                        if (idleMs >= COMBAT_IDLE_TIMEOUT_MS)
                        {
                            _log($"[Combat] Action stuck for {idleMs:F0}ms. Mob dead or stuck. TAB.");
                            _wasAttacking = false;
                            _combatIdleStartTime = DateTime.MinValue;
                            return CombatAction.TabTarget;
                        }

                        // Still waiting for idle timeout
                        return CombatAction.CombatWait;
                    }
                }

                // Any non-idle action, or no mob selected, resets the idle timer
                _combatIdleStartTime = DateTime.MinValue;

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
                _combatIdleStartTime = DateTime.MinValue;

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
        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(nint hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        /// <summary>
        /// Finds the game window and captures its client area as a PNG file
        /// in Screenshots/PlayerSelected/ with the current date/time.
        /// </summary>
        private void CapturePlayerScreenshot()
        {
            try
            {
                nint hwnd = FindWindow(null, "Legend of Ares");
                if (hwnd == nint.Zero) hwnd = FindWindow(null, "Ares");
                if (hwnd == nint.Zero) hwnd = FindWindow(null, "Nostalgia");
                if (hwnd == nint.Zero) hwnd = FindWindow(null, "Epic Of Ares Client");

                int captureX = 0, captureY = 0, captureW = BotConstants.Loot.BitmapWidth, captureH = BotConstants.Loot.BitmapHeight;

                if (hwnd != nint.Zero)
                {
                    if (GetClientRect(hwnd, out RECT clientRect))
                    {
                        POINT topLeft = new POINT { X = 0, Y = 0 };
                        if (ClientToScreen(hwnd, ref topLeft))
                        {
                            captureX = topLeft.X;
                            captureY = topLeft.Y;
                            captureW = clientRect.Right - clientRect.Left;
                            captureH = clientRect.Bottom - clientRect.Top;
                        }
                    }
                }

                using (Bitmap bitmap = new Bitmap(captureW, captureH))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(captureX, captureY, 0, 0, bitmap.Size);

                    string screenshotsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Screenshots", "PlayerSelected");
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
            _combatIdleStartTime = DateTime.MinValue;
        }
    }
}
