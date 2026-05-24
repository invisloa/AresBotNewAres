using System;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Decides whether the current waypoint can be soft-skipped when the bot
    /// is action-stuck very close to it, avoiding unnecessary obstacle marking
    /// and Bug2 entry.
    /// </summary>
    internal class WaypointSkipPolicy
    {
        private readonly GameMemoryService _memory;
        private readonly Action<string> _log;
        private readonly Func<Waypoint, float> _getReachThreshold;

        private const float STUCK_SOFT_SKIP_DISTANCE = BotConstants.Movement.StuckSoftSkipDistance;

        public WaypointSkipPolicy(
            GameMemoryService memory,
            Action<string> log,
            Func<Waypoint, float> getReachThreshold)
        {
            _memory = memory ?? throw new ArgumentNullException(nameof(memory));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _getReachThreshold = getReachThreshold ?? throw new ArgumentNullException(nameof(getReachThreshold));
        }

        /// <summary>
        /// If the bot is action-stuck very close to the current waypoint and
        /// there are more waypoints ahead, skip this waypoint.
        /// </summary>
        /// <param name="currX">Current player X.</param>
        /// <param name="currY">Current player Y.</param>
        /// <param name="target">The current waypoint target.</param>
        /// <param name="waypoints">The waypoint queue (will be mutated on skip).</param>
        /// <param name="resetBearingState">Action to reset bearing state.</param>
        /// <param name="resetProgressTracker">Action to reset the progress tracker.</param>
        /// <param name="resetActionStuckTracking">Action to reset action-stuck tracking.</param>
        /// <returns>True if the waypoint was skipped.</returns>
        public bool TrySkip(
            float currX, float currY,
            Waypoint target,
            System.Collections.Generic.Queue<Waypoint> waypoints,
            Action resetBearingState,
            Action resetProgressTracker,
            Action resetActionStuckTracking)
        {
            if (waypoints == null || waypoints.Count <= 1)
                return false;

            // Only skip when action confirms stuck (25 or 1)
            byte currentAction = _memory.GetCurrentAction();
            if (!StuckDetector.IsActionIdleOrStuck(currentAction))
                return false;

            float dist = GeometryUtils.Distance(currX, currY, target.X, target.Y);
            float normalThreshold = _getReachThreshold(target);
            float effectiveSkipDistance = Math.Max(normalThreshold, STUCK_SOFT_SKIP_DISTANCE);

            if (dist <= effectiveSkipDistance)
            {
                _log($"[SoftSkip] Action stuck near waypoint. Action={currentAction} Dist={dist:F2} <= {effectiveSkipDistance:F2}. Skipping waypoint ({target.X},{target.Y}).");
                waypoints.Dequeue();
                resetBearingState?.Invoke();
                resetProgressTracker?.Invoke();
                resetActionStuckTracking?.Invoke();
                _log($"[SoftSkip] Waypoint skipped. Queue now has {waypoints.Count} waypoints.");
                return true;
            }

            return false;
        }
    }
}
