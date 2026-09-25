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
        PotionsUsed,
        /// <summary>
        /// The selected mob is unreachable/not dying (combat watchdog fired) — the caller
        /// must perform the STANDARD unstuck action (reverse-diagonal recovery) so the
        /// player really walks away from the stuck spot. Combat is suppressed while the
        /// unstuck routine is active so TAB/attack cannot interrupt it.
        /// </summary>
        Unstuck,
        /// <summary>
        /// The attack animation plays but mana is not consumed for the mana-stuck
        /// timeout — the attack is NOT connecting (phantom/unreachable target, mob HP
        /// never drops). The caller must physically reposition: walk toward the next
        /// waypoint for a short time, then TAB and attack the (re-selected) target again.
        /// </summary>
        RepositionAndRetry
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
        private const double COMBAT_STUCK_TIMEOUT_MS = BotConstants.Combat.CombatStuckTimeoutMs;
        private const float COMBAT_STUCK_POS_EPSILON = BotConstants.Combat.CombatStuckPosEpsilon;
        private const double COMBAT_MANA_STUCK_TIMEOUT_MS = BotConstants.Combat.CombatManaStuckTimeoutMs;

        // ── State ──
        private bool _wasAttacking;
        private DateTime _lastNonIdleActionTime = DateTime.MinValue;
        private DateTime _lastMoveModeTabTime = DateTime.MinValue;
        private DateTime _lastAttackSpeedCheck = DateTime.MinValue;
        private DateTime _combatIdleStartTime = DateTime.MinValue;

        // ── Combat stuck detection (mana + position based) ──
        // A real attack consumes mana (skill 3), and standing still is the normal combat
        // posture — so a static position alone proves nothing. But position AND mana both
        // stale for the timeout means the player is NOT actually fighting (unreachable
        // mob / attack not connecting, while the animation may still play). That triggers
        // the standard unstuck action.
        // NOTE: mana regenerates over time and potions raise it — only a DECREASE proves
        // the skill actually consumed mana (attack connecting). Increases are ignored.
        /// <summary>Last time mana DROPPED (was consumed) while a mob was selected (MinValue = not yet sampled).</summary>
        private DateTime _lastManaChangeAt = DateTime.MinValue;
        /// <summary>Previous mana sample (for consumption detection — decreases only).</summary>
        private int _lastManaValue = int.MinValue;
        /// <summary>Last time the player position changed while a mob was selected.</summary>
        private DateTime _lastCombatPosChangeAt = DateTime.MinValue;
        /// <summary>Previous position sample (for change detection).</summary>
        private (float X, float Y)? _lastCombatPos = null;

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

        /// <summary>Last attack-speed value read by <see cref="CheckAttackSpeed"/> (short.MinValue = never read).</summary>
        public short LastAttackSpeed { get; private set; } = short.MinValue;

        /// <summary>
        /// Checks whether it is time to drink attack-speed potions.
        /// Returns true when potion keys should be pressed.
        /// </summary>
        public bool CheckAttackSpeed(GameMemoryService memoryService)
        {
            if ((DateTime.Now - _lastAttackSpeedCheck).TotalSeconds >= BotConstants.SpeedPotion.CheckIntervalSeconds)
            {
                short attackSpeed = memoryService.GetAttackSpeed();
                LastAttackSpeed = attackSpeed;
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
        /// <param name="currX">Current player X (used by the mana+position stuck detection).</param>
        /// <param name="currY">Current player Y (used by the mana+position stuck detection).</param>
        /// <returns>A <see cref="CombatAction"/> describing what input to perform next.</returns>
        public CombatAction EvaluateCombatAction(
            GameMemoryService memoryService,
            BotMode currentMode,
            bool isUnstuckActive,
            float currX,
            float currY)
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
            int targetId = memoryService.GetSelectedTargetId();
            byte currentAction = memoryService.GetCurrentAction();

            if (anim1 > KnightAttackedMin && anim1 < KnightAttackedMax)
            {
                _log($"[Combat] Player is being attacked (Anim: {anim1})");
            }

            if (targetId > 0)
            {
                // ── Player character check: skip attacking other players ──
                // Classify the exact target ID read above. Reading a second, separate
                // pointer chain here could describe a different/stale target.
                if (GameMemoryService.IsPlayerTargetId(targetId))
                {
                    CapturePlayerScreenshot();
                    _log($"[Combat] Target ID {targetId} is a player character — skipping attack. TAB.");
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

                    // Start the mana+position stuck-detection baseline at attack start:
                    // if NEITHER changes for the timeout, the attack is not connecting.
                    _lastManaChangeAt = DateTime.Now;
                    _lastCombatPosChangeAt = DateTime.Now;
                    _lastManaValue = int.MinValue;
                    _lastCombatPos = (currX, currY);
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

                // ── Attack-not-connecting detection: mana not consumed ──
                // A real attack consumes mana (skill) with every swing. If the attack
                // animation keeps playing (a target ID is present) but mana does NOT drop for
                // COMBAT_MANA_STUCK_TIMEOUT_MS, the attack is not connecting — the mob
                // looks attacked but its HP never drops (phantom/unreachable target).
                // Position is deliberately NOT part of this check: the attack lunge
                // animation moves the player every swing, so a static-position test can
                // never fire while this is happening. The bot must physically reposition:
                // walk toward the next waypoint for a short time, then TAB and attack again.
                //
                // IMPORTANT: only a mana DECREASE counts as attack progress. Natural
                // mana regeneration / mana potions raise the value; treating those as
                // progress used to reset the stall timer forever (a slow +1 MP regen
                // every few seconds) so the phantom-attack hang never triggered a
                // reposition or unstuck.
                if (memoryService.TryGetMana(out short mana))
                {
                    if (_lastManaValue != int.MinValue && mana < _lastManaValue)
                    {
                        _lastManaChangeAt = DateTime.Now;
                    }
                    _lastManaValue = mana;
                }

                // Only fire when mana was actually sampled at least once (otherwise the
                // unreadable-mana case falls through to the position-based check below).
                if (_lastManaValue != int.MinValue &&
                    _lastManaChangeAt != DateTime.MinValue &&
                    (DateTime.Now - _lastManaChangeAt).TotalMilliseconds >= COMBAT_MANA_STUCK_TIMEOUT_MS)
                {
                    _log($"[Combat] Mana not consumed for {COMBAT_MANA_STUCK_TIMEOUT_MS:F0}ms while attacking (last mana {_lastManaValue}) — attack animation plays but no mana consumed (attack not connecting, mob HP not dropping). Repositioning toward next waypoint.");
                    return CombatAction.RepositionAndRetry;
                }

                // ── Combat stuck detection fallback: mana + position unchanged ──
                // Only reachable when the mana read is unavailable (otherwise the mana-only
                // check above fires first). Position AND mana both unchanged for the longer
                // timeout means the player is NOT actually fighting (unreachable mob / attack
                // not connecting, while the animation may still play). Trigger the STANDARD
                // unstuck action so the player really walks away from the stuck spot.
                if (_lastCombatPos.HasValue &&
                    GeometryUtils.Distance(currX, currY, _lastCombatPos.Value.X, _lastCombatPos.Value.Y) > COMBAT_STUCK_POS_EPSILON)
                {
                    _lastCombatPosChangeAt = DateTime.Now;
                }
                _lastCombatPos = (currX, currY);

                if (_lastManaChangeAt != DateTime.MinValue &&
                    _lastCombatPosChangeAt != DateTime.MinValue &&
                    (DateTime.Now - _lastManaChangeAt).TotalMilliseconds >= COMBAT_STUCK_TIMEOUT_MS &&
                    (DateTime.Now - _lastCombatPosChangeAt).TotalMilliseconds >= COMBAT_STUCK_TIMEOUT_MS)
                {
                    _log($"[Combat] Position and mana unchanged for {COMBAT_STUCK_TIMEOUT_MS:F0}ms while a mob is selected — attack not consuming mana, player is stuck. Starting standard unstuck.");
                    return CombatAction.Unstuck;
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
                _combatIdleStartTime = DateTime.MinValue;

                // Reset the combat stuck detection — no combat in progress.
                _lastManaValue = int.MinValue;
                _lastCombatPos = null;
                _lastManaChangeAt = DateTime.MinValue;
                _lastCombatPosChangeAt = DateTime.MinValue;

                // Loot mode (MoveAndAttackAndLoot) with no mob selected: keep cycling
                // targets on the normal interval. Attacking always has priority — the
                // loot system runs in a parallel task and cancels itself the moment a
                // mob gets selected, and the post-combat loot pause in MovementSystem
                // gives the loot scan its window after a kill. Suppressing TAB here
                // entirely would leave the bot with no way to ever acquire a target.
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
            _lastManaValue = int.MinValue;
            _lastCombatPos = null;
            _lastManaChangeAt = DateTime.MinValue;
            _lastCombatPosChangeAt = DateTime.MinValue;
        }
    }
}
