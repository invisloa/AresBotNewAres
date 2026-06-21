using System;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Debounced action-based stuck detection for movement.
    ///
    /// Uses GetCurrentAction as the source of truth:
    /// - action 27 or 3 → running (not stuck)
    /// - action 28      → being hit (not stuck)
    /// - action 25 or 1 → idle/stuck
    ///
    /// A single idle/stuck sample does NOT trigger — the detector requires
    /// either 2 consecutive idle/stuck samples or 200 ms of continuous
    /// idle/stuck before declaring stuck.
    /// </summary>
    internal class StuckDetector
    {
        private readonly GameMemoryService _memory;
        private readonly Action<string> _log;
        private readonly Func<Waypoint, float> _getReachThreshold;
        private readonly float _nearTargetIgnoreExtra;

        // ── Debounce state ──

        private DateTime _actionStuckFirstSeenAt = DateTime.MinValue;
        private int _consecutiveActionStuckSamples = 0;

        public StuckDetector(
            GameMemoryService memory,
            Action<string> log,
            Func<Waypoint, float> getReachThreshold,
            float nearTargetIgnoreExtra)
        {
            _memory = memory ?? throw new ArgumentNullException(nameof(memory));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _getReachThreshold = getReachThreshold ?? throw new ArgumentNullException(nameof(getReachThreshold));
            _nearTargetIgnoreExtra = nearTargetIgnoreExtra;
        }

        // ── Static action helpers ──

        /// <summary>Returns true if the action byte indicates the character is running / moving / being hit (not stuck).</summary>
        public static bool IsActionRunning(byte currentAction)
        {
            return currentAction == 27 || currentAction == 3 || currentAction == 28;
        }

        /// <summary>Returns true if the action byte indicates the character is idle or stuck.</summary>
        public static bool IsActionIdleOrStuck(byte currentAction)
        {
            return currentAction == 25 || currentAction == 1;
        }

        // ── Core detection ──

        /// <summary>
        /// Evaluates whether the bot is stuck based on the game action byte,
        /// using a debounce threshold (consecutive samples + time window).
        /// </summary>
        /// <param name="currX">Current player X.</param>
        /// <param name="currY">Current player Y.</param>
        /// <param name="target">The current waypoint target.</param>
        /// <param name="isMovingForward">Whether W is currently held down.</param>
        /// <returns>True if the bot is confirmed action-stuck.</returns>
        public bool IsActionStuck(float currX, float currY, Waypoint target, bool isMovingForward)
        {
            // If not moving forward, cannot be stuck
            if (!isMovingForward)
            {
                ResetTracking();
                return false;
            }

            byte currentAction = _memory.GetCurrentAction();

            // Action running (27 or 3) or being hit (28) → not stuck, reset everything
            if (IsActionRunning(currentAction))
            {
                ResetTracking();
                return false;
            }

            // Near-target veto: if close to waypoint, don't trigger stuck
            float distToTarget = GeometryUtils.Distance(currX, currY, target.X, target.Y);
            float reachThreshold = _getReachThreshold(target);
            if (distToTarget <= reachThreshold + _nearTargetIgnoreExtra)
            {
                ResetTracking();
                return false;
            }

            // Action is idle/stuck (25 or 1)
            if (IsActionIdleOrStuck(currentAction))
            {
                if (_consecutiveActionStuckSamples == 0)
                {
                    // First stuck sample — record time but don't trigger yet
                    _actionStuckFirstSeenAt = DateTime.Now;
                }

                _consecutiveActionStuckSamples++;

                // Debounce: require either 2+ consecutive samples or >= 200ms elapsed
                if (_consecutiveActionStuckSamples >= 2 ||
                    (DateTime.Now - _actionStuckFirstSeenAt).TotalMilliseconds >= 200.0)
                {
                    return true;
                }

                return false;
            }

            // Unknown action — reset and not stuck
            ResetTracking();
            return false;
        }

        /// <summary>Resets debounce state (consecutive samples count and first-seen timestamp).</summary>
        public void ResetTracking()
        {
            _consecutiveActionStuckSamples = 0;
            _actionStuckFirstSeenAt = DateTime.MinValue;
        }
    }
}
