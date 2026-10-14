using System;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Detects a likely dead/disconnected game session from the bot's own movement
    /// heartbeat. It only counts time while movement or a movement recovery is active;
    /// normal intentional standing still therefore does not look like a disconnect.
    /// </summary>
    public sealed class GameConnectionWatchdog
    {
        private readonly int _timeoutMs;
        private readonly float _minimumProgress;
        private (float X, float Y)? _lastProgressPosition;
        private DateTime _lastProgressAt;
        private bool _failureReported;

        public GameConnectionWatchdog(
            int timeoutMs = BotConstants.Connection.NoMovementTimeoutMs,
            float minimumProgress = BotConstants.Connection.MinimumMovementProgress)
        {
            if (timeoutMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            if (minimumProgress <= 0 || !float.IsFinite(minimumProgress))
                throw new ArgumentOutOfRangeException(nameof(minimumProgress));

            _timeoutMs = timeoutMs;
            _minimumProgress = minimumProgress;
        }

        /// <summary>
        /// Observe one game position. Returns true once when movement was expected but
        /// no meaningful position change occurred for the configured timeout.
        /// </summary>
        public bool Observe(bool movementExpected, float x, float y, DateTime now)
        {
            if (_failureReported)
                return false;

            if (!movementExpected || !float.IsFinite(x) || !float.IsFinite(y))
            {
                ResetTracking();
                return false;
            }

            if (!_lastProgressPosition.HasValue)
            {
                _lastProgressPosition = (x, y);
                _lastProgressAt = now;
                return false;
            }

            var previous = _lastProgressPosition.Value;
            float dx = x - previous.X;
            float dy = y - previous.Y;
            if (MathF.Sqrt(dx * dx + dy * dy) >= _minimumProgress)
            {
                _lastProgressPosition = (x, y);
                _lastProgressAt = now;
                return false;
            }

            if ((now - _lastProgressAt).TotalMilliseconds < _timeoutMs)
                return false;

            _failureReported = true;
            return true;
        }

        /// <summary>Clears the current no-progress interval (e.g. on a map transition).</summary>
        public void Reset()
        {
            _failureReported = false;
            ResetTracking();
        }

        private void ResetTracking()
        {
            _lastProgressPosition = null;
            _lastProgressAt = DateTime.MinValue;
        }
    }
}
