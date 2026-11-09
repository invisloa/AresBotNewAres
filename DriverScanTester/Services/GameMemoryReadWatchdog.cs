using System;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Detects a game client that can no longer serve required memory reads.
    /// The connection monitor feeds it the success of the A_attack1 read
    /// (<c>[Ares.exe + 0x489BC8] + 0x47A</c>), which the lock tool renders as "?" when
    /// the pointer chain breaks. A single failed sample is ignored; only a continuously
    /// unreadable value for the configured timeout is treated as a crash.
    ///
    /// The watchdog latches after reporting so one crash produces exactly one bot stop.
    /// Call <see cref="Reset"/> before reusing it for a new run.
    /// </summary>
    public sealed class GameMemoryReadWatchdog
    {
        private readonly int _timeoutMs;
        private DateTime _failureStartedAt = DateTime.MinValue;
        private bool _failureReported;

        public GameMemoryReadWatchdog(
            int timeoutMs = BotConstants.Connection.AttackReadFailureTimeoutMs)
        {
            if (timeoutMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(timeoutMs));

            _timeoutMs = timeoutMs;
        }

        /// <summary>
        /// Observe one memory-read sample. Returns true once when the read has failed
        /// continuously for the configured timeout. A successful sample clears the
        /// current failure interval.
        /// </summary>
        public bool Observe(bool readSucceeded, DateTime now)
        {
            if (_failureReported)
                return false;

            if (readSucceeded)
            {
                _failureStartedAt = DateTime.MinValue;
                return false;
            }

            if (_failureStartedAt == DateTime.MinValue)
            {
                _failureStartedAt = now;
                return false;
            }

            if ((now - _failureStartedAt).TotalMilliseconds < _timeoutMs)
                return false;

            _failureReported = true;
            return true;
        }

        /// <summary>Clears the current failure interval and the one-shot report latch.</summary>
        public void Reset()
        {
            _failureReported = false;
            _failureStartedAt = DateTime.MinValue;
        }
    }
}
