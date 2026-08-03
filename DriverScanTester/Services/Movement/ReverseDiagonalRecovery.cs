using System;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Tick-driven reverse-diagonal recovery.
    /// Tries a deterministic sequence of camera angles to unstick the bot.
    ///
    /// Per attempt:
    ///   1. Set camera to new angle (W NOT pressed)
    ///   2. Wait 200ms (camera settle)
    ///   3. Press W
    ///   4. Wait 200ms (movement attempt)
    ///   5. Check real displacement:
    ///      - Moved ≥ SuccessDisplacement → success (regardless of the action byte)
    ///      - Still idle/stuck (25/1) → stop W, try next angle
    ///      - Action claims running but no displacement (ghost-walk stuck spot) →
    ///        treated as failed, try next angle
    ///
    /// Thread.Sleep / blocking is NOT used — the caller invokes Tick() each
    /// movement update cycle.
    /// </summary>
    internal enum RecoveryResult
    {
        /// <summary>Recovery is still in progress.</summary>
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
        private readonly Action _startMoving;
        private readonly Action _stopMoving;

        // ── Configuration ──

        private const int CameraSettleMs = 200;
        private const int MoveAttemptMs = 200;
        private const int SuccessCooldownMs = 300;
        private const int MaxAttempts = BotConstants.ReverseDiagonal.MaxAttempts;

        private static readonly TimeSpan HealthyBearingMaxAge = TimeSpan.FromSeconds(BotConstants.Timeouts.HealthyBearingMaxAgeSeconds);

        // Offsets applied to the base bearing, in exact order.
        private static readonly float[] AttemptOffsets = BotConstants.ReverseDiagonal.AttemptOffsets;

        // ── Attempt stages ──

        private enum AttemptStage
        {
            /// <summary>Camera was just set. Waiting CameraSettleMs (W released).</summary>
            CameraSettle,
            /// <summary>W pressed. Waiting MoveAttemptMs.</summary>
            Moving,
            /// <summary>Character is running. Waiting SuccessCooldownMs before declaring success.</summary>
            SuccessCooldown
        }

        // ── State ──

        private bool _active;
        private int _attemptIndex;
        private AttemptStage _stage;
        private DateTime _stageStartTime;
        private float _baseBearing;
        private (float X, float Y) _target;
        private float _currentBearing;

        /// <summary>Player position when the current attempt started pressing W.
        /// Success is decided by real displacement from this point, not the action byte —
        /// in ghost-walk stuck spots the animation keeps playing while the player stands still.</summary>
        private (float X, float Y)? _attemptStartPos;

        /// <summary>Whether the recovery sequence is currently running.</summary>
        public bool IsActive => _active;

        /// <summary>Bearing (degrees from North) of the current diagonal attempt.</summary>
        public float CurrentBearingDeg => _currentBearing;

        public ReverseDiagonalRecovery(GameMemoryService memory, Action<string> log, Action startMoving, Action stopMoving)
        {
            _memory = memory ?? throw new ArgumentNullException(nameof(memory));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _startMoving = startMoving ?? throw new ArgumentNullException(nameof(startMoving));
            _stopMoving = stopMoving ?? throw new ArgumentNullException(nameof(stopMoving));
        }

        /// <summary>
        /// Start a new reverse-diagonal recovery sequence.
        /// Sets the first camera angle and releases W (camera settle phase).
        /// </summary>
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

            // Fire the first attempt
            float offset = AttemptOffsets[0];
            _currentBearing = GeometryUtils.NormalizeBearingDeg(_baseBearing + offset);
            _stage = AttemptStage.CameraSettle;
            _stageStartTime = DateTime.Now;

            // Ensure W is released during camera settle phase
            _stopMoving();

            _log($"[ReverseDiagonal] Started. Attempt=1/{MaxAttempts} offset={offset:F0}° " +
                 $"bearing={_currentBearing:F1} target=({targetX:F1},{targetY:F1})");
        }

        /// <summary>
        /// Call every update tick while <see cref="IsActive"/> is true.
        /// Evaluates the current stage and advances if needed.
        /// When a new attempt begins, <see cref="CurrentBearingDeg"/> is updated —
        /// the caller should apply the new camera angle.
        /// </summary>
        public RecoveryResult Tick(float currX, float currY)
        {
            if (!_active)
                return RecoveryResult.Failed;

            double elapsedMs = (DateTime.Now - _stageStartTime).TotalMilliseconds;

            switch (_stage)
            {
                case AttemptStage.CameraSettle:
                    if (elapsedMs >= CameraSettleMs)
                    {
                        // Camera settled — press W and try to move
                        _startMoving();
                        _stage = AttemptStage.Moving;
                        _stageStartTime = DateTime.Now;
                        _attemptStartPos = (currX, currY);

                        _log($"[ReverseDiagonal] Attempt {_attemptIndex + 1}/{MaxAttempts}: " +
                             $"offset={AttemptOffsets[_attemptIndex]:F0}° bearing={_currentBearing:F1} — W pressed");
                    }
                    return RecoveryResult.InProgress;

                case AttemptStage.Moving:
                    if (elapsedMs < MoveAttemptMs)
                        return RecoveryResult.InProgress;

                    // Movement window elapsed — evaluate REAL progress. Success is decided
                    // by actual displacement, not just the action byte: in some stuck spots
                    // the walk animation keeps playing (action stays "running") while the
                    // player is pushing against an obstacle and standing still.
                    byte currentAction = _memory.GetCurrentAction();
                    float movedSinceAttempt = _attemptStartPos.HasValue
                        ? GeometryUtils.Distance(currX, currY, _attemptStartPos.Value.X, _attemptStartPos.Value.Y)
                        : 0f;

                    if (movedSinceAttempt >= BotConstants.ReverseDiagonal.SuccessDisplacement)
                    {
                        // Real displacement — success regardless of what the action byte claims.
                        _log($"[ReverseDiagonal] Attempt {_attemptIndex + 1} succeeded. Moved {movedSinceAttempt:F2} units. Cooldown {SuccessCooldownMs}ms.");
                        _stage = AttemptStage.SuccessCooldown;
                        _stageStartTime = DateTime.Now;
                        return RecoveryResult.InProgress;
                    }

                    // No real displacement — the attempt failed. Distinguish the classic
                    // idle-stuck from the ghost-walk stuck (action claims running) for the log.
                    bool actionClaimsRunning = !StuckDetector.IsActionIdleOrStuck(currentAction);
                    _stopMoving();
                    _log(actionClaimsRunning
                        ? $"[ReverseDiagonal] FAIL attempt {_attemptIndex + 1}. Action={currentAction} running but moved only {movedSinceAttempt:F2} units — ghost-walk stuck."
                        : $"[ReverseDiagonal] FAIL attempt {_attemptIndex + 1}. Action={currentAction} still stuck.");

                    _attemptIndex++;
                    _attemptStartPos = null;

                    if (_attemptIndex >= MaxAttempts)
                    {
                        _log($"[ReverseDiagonal] All {MaxAttempts} attempts failed.");
                        _active = false;
                        return RecoveryResult.Failed;
                    }

                    // Start next attempt
                    float offset = AttemptOffsets[_attemptIndex];
                    _currentBearing = GeometryUtils.NormalizeBearingDeg(_baseBearing + offset);
                    _stage = AttemptStage.CameraSettle;
                    _stageStartTime = DateTime.Now;
                    _stopMoving(); // ensure W is released for camera settle

                    _log($"[ReverseDiagonal] Next attempt {_attemptIndex + 1}/{MaxAttempts}: " +
                         $"offset={offset:F0}° bearing={_currentBearing:F1}");
                    return RecoveryResult.InProgress; // caller should apply new camera bearing

                case AttemptStage.SuccessCooldown:
                    if (elapsedMs >= SuccessCooldownMs)
                    {
                        _log($"[ReverseDiagonal] SUCCESS. Cooldown finished.");
                        _active = false;
                        return RecoveryResult.Recovered;
                    }
                    return RecoveryResult.InProgress;
            }

            return RecoveryResult.Failed;
        }

        /// <summary>Immediately stops the recovery sequence.</summary>
        public void Stop()
        {
            if (_active)
            {
                _log("[ReverseDiagonal] Stopped.");
                _active = false;
                _stopMoving();
            }
        }
    }
}
