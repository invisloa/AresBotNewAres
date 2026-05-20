using System;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Tick-driven reverse-diagonal recovery that runs before Bug2.
    /// Tries a deterministic sequence of backward+strafe offsets to unstick the bot.
    ///
    /// Each attempt lasts <see cref="AttemptMs"/> milliseconds.
    /// After all <see cref="MaxAttempts"/> fail, the caller (MovementSystem)
    /// escalates to obstacle marking and Bug2.
    ///
    /// Thread.Sleep / blocking is NOT used — the caller invokes Tick() each
    /// movement update cycle.
    /// </summary>
    internal enum RecoveryResult
    {
        /// <summary>Recovery is still in progress (within current attempt).</summary>
        InProgress,

        /// <summary>An attempt succeeded — bot is unstuck.</summary>
        Recovered,

        /// <summary>All attempts exhausted without success.</summary>
        Failed
    }

    internal class ReverseDiagonalRecovery
    {
        private readonly GameMemoryService _memory;
        private readonly Action<string> _log;

        // ── Configuration ──

        private const int AttemptMs = 200;
        private const int MaxAttempts = 4;
        private const float SuccessDisplacement = 0.60f;
        private const float SuccessDistImprove = 0.50f;

        /// <summary>Maximum age of a healthy bearing for it to be reused.</summary>
        private static readonly TimeSpan HealthyBearingMaxAge = TimeSpan.FromSeconds(5.0);

        // Offsets applied to the base bearing, in exact order.
        private static readonly float[] AttemptOffsets = { 135f, -135f, 150f, -150f };

        // ── State ──

        private bool _active;
        private int _attemptIndex;
        private DateTime _attemptStartTime;
        private (float X, float Y) _attemptStartPos;
        private float _attemptStartDist;
        private float _baseBearing;
        private (float X, float Y) _target;
        private float _currentBearing;

        /// <summary>Whether the recovery sequence is currently running.</summary>
        public bool IsActive => _active;

        /// <summary>Bearing (degrees from North) of the current diagonal attempt.</summary>
        public float CurrentBearingDeg => _currentBearing;

        public ReverseDiagonalRecovery(GameMemoryService memory, Action<string> log)
        {
            _memory = memory ?? throw new ArgumentNullException(nameof(memory));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// Start a new reverse-diagonal recovery sequence.
        /// </summary>
        /// <param name="currX">Current player X.</param>
        /// <param name="currY">Current player Y.</param>
        /// <param name="targetX">Target waypoint X.</param>
        /// <param name="targetY">Target waypoint Y.</param>
        /// <param name="healthyBearingDeg">
        /// Last known healthy movement bearing, or null if unavailable.
        /// Used as base bearing if not too old.
        /// </param>
        /// <param name="healthyBearingTime">
        /// Time when <paramref name="healthyBearingDeg"/> was recorded.
        /// </param>
        public void Start(
            float currX, float currY,
            float targetX, float targetY,
            float? healthyBearingDeg,
            DateTime? healthyBearingTime)
        {
            _active = true;
            _attemptIndex = 0;
            _target = (targetX, targetY);

            // Determine base bearing: prefer fresh healthy bearing, fall back to
            // bearing from current position toward target.
            if (healthyBearingDeg.HasValue && healthyBearingTime.HasValue &&
                (DateTime.Now - healthyBearingTime.Value) <= HealthyBearingMaxAge)
            {
                _baseBearing = healthyBearingDeg.Value;
                _log($"[ReverseDiagonal] Base bearing from healthy move: {_baseBearing:F1}°");
            }
            else
            {
                _baseBearing = GeometryUtils.GetBearingToTargetDeg(currX, currY, targetX, targetY);
                _log($"[ReverseDiagonal] Base bearing from target direction: {_baseBearing:F1}°");
            }

            _attemptStartPos = (currX, currY);
            _attemptStartDist = GeometryUtils.Distance(currX, currY, targetX, targetY);
            _attemptStartTime = DateTime.Now;

            // Fire the first attempt
            float offset = AttemptOffsets[0];
            _currentBearing = GeometryUtils.NormalizeBearingDeg(_baseBearing + offset);

            _log($"[ReverseDiagonal] Started. Attempt=1/{MaxAttempts} offset={offset:F0}° " +
                 $"bearing={_currentBearing:F1} target=({targetX:F1},{targetY:F1})");
        }

        /// <summary>
        /// Call every update tick while <see cref="IsActive"/> is true.
        /// Evaluates the current attempt and advances if needed.
        /// </summary>
        /// <param name="currX">Current player X.</param>
        /// <param name="currY">Current player Y.</param>
        /// <returns>
        /// <see cref="RecoveryResult.InProgress"/> if still waiting,
        /// <see cref="RecoveryResult.Recovered"/> if a attempt succeeded, or
        /// <see cref="RecoveryResult.Failed"/> if all attempts exhausted.
        /// When <see cref="RecoveryResult.InProgress"/> is returned and a new
        /// attempt just started, <see cref="CurrentBearingDeg"/> has already been
        /// updated — the caller should apply it immediately.
        /// </returns>
        public RecoveryResult Tick(float currX, float currY)
        {
            if (!_active)
                return RecoveryResult.Failed;

            double elapsedMs = (DateTime.Now - _attemptStartTime).TotalMilliseconds;

            // Still within the current attempt window
            if (elapsedMs < AttemptMs)
                return RecoveryResult.InProgress;

            // ── Attempt finished — evaluate success ──
            float displacement = GeometryUtils.Distance(
                currX, currY, _attemptStartPos.X, _attemptStartPos.Y);
            float distNow = GeometryUtils.Distance(
                currX, currY, _target.X, _target.Y);
            float distImprove = _attemptStartDist - distNow; // positive = closer

            if (displacement >= SuccessDisplacement || distImprove >= SuccessDistImprove)
            {
                _log($"[ReverseDiagonal] SUCCESS attempt {_attemptIndex + 1}. " +
                     $"disp={displacement:F2} dImprove={distImprove:F2}");
                _active = false;
                return RecoveryResult.Recovered;
            }

            // ── This attempt failed ──
            _log($"[ReverseDiagonal] FAIL attempt {_attemptIndex + 1}. " +
                 $"disp={displacement:F2} dImprove={distImprove:F2}");

            _attemptIndex++;

            if (_attemptIndex >= MaxAttempts)
            {
                _log($"[ReverseDiagonal] All {MaxAttempts} attempts failed.");
                _active = false;
                return RecoveryResult.Failed;
            }

            // ── Start next attempt ──
            _attemptStartPos = (currX, currY);
            _attemptStartDist = GeometryUtils.Distance(currX, currY, _target.X, _target.Y);
            _attemptStartTime = DateTime.Now;

            float offset = AttemptOffsets[_attemptIndex];
            _currentBearing = GeometryUtils.NormalizeBearingDeg(_baseBearing + offset);

            _log($"[ReverseDiagonal] Attempt {_attemptIndex + 1}/{MaxAttempts}: " +
                 $"offset={offset:F0}° bearing={_currentBearing:F1}");

            return RecoveryResult.InProgress; // caller must apply the new bearing
        }

        /// <summary>Immediately stops the recovery sequence.</summary>
        public void Stop()
        {
            if (_active)
            {
                _log("[ReverseDiagonal] Stopped.");
                _active = false;
            }
        }
    }
}
