using System;
using System.Threading;
using System.Threading.Tasks;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Central pause switch for every bot loop. Pause never cancels anything — it only
    /// suspends the update loops in place, so Resume continues exactly where the bot
    /// was paused (same flow step, same waypoint, same retry counters). Backed by a
    /// simple volatile flag polled by the loops; no thread blocking, so the UI stays
    /// responsive while paused.
    /// </summary>
    public sealed class BotPauseController
    {
        private volatile bool _isPaused;

        /// <summary>True while the bot is paused.</summary>
        public bool IsPaused => _isPaused;

        /// <summary>Fires on every Pause/Resume transition (with the new state).</summary>
        public event Action<bool>? PauseChanged;

        /// <summary>Freeze all bot loops at their next pause checkpoint.</summary>
        public void Pause()
        {
            if (_isPaused) return;
            _isPaused = true;
            PauseChanged?.Invoke(true);
        }

        /// <summary>Resume all bot loops where they were paused.</summary>
        public void Resume()
        {
            if (!_isPaused) return;
            _isPaused = false;
            PauseChanged?.Invoke(false);
        }

        /// <summary>
        /// Awaits while paused (returns immediately when running). The caller is expected
        /// to have already released held inputs (movement / attack keys) before waiting.
        /// Throws <see cref="OperationCanceledException"/> when <paramref name="token"/> is cancelled.
        /// </summary>
        public async Task WaitIfPausedAsync(CancellationToken token)
        {
            while (_isPaused)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(100, token);
            }
        }

        /// <summary>
        /// Delay whose countdown freezes while paused: the remaining time does not advance
        /// until <see cref="Resume"/> is called. Used for every timed wait in the bot so a
        /// pause in the middle of a wait (teleport settle, retry backoff, tick delay, ...)
        /// holds the wait instead of letting it expire in the background.
        /// </summary>
        public async Task PausableDelayAsync(int millisecondsDelay, CancellationToken token)
        {
            if (millisecondsDelay <= 0) return;
            int elapsed = 0;
            while (elapsed < millisecondsDelay)
            {
                if (_isPaused)
                {
                    await WaitIfPausedAsync(token);
                    continue;
                }
                int chunk = Math.Min(100, millisecondsDelay - elapsed);
                await Task.Delay(chunk, token);
                // Only count time that passed while unpaused.
                if (!_isPaused)
                    elapsed += chunk;
            }
        }
    }
}
