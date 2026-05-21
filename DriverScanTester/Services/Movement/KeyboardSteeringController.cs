using System;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Central steering controller that replaces direct SetCameraAngle with
    /// physical A/D key presses. Reads the current camera angle from memory
    /// and holds A or D until the desired bearing is reached.
    ///
    /// Design principle: W is held independently; A/D are held only while
    /// the angular error exceeds the stop tolerance.
    /// </summary>
    internal sealed class KeyboardSteeringController
    {
        private readonly GameMemoryService _memory;
        private readonly Action<string> _log;

        // Raw game-angle circle circumference (calibrated full spin)
        private const float HalfCircleRaw = GeometryUtils.ManualFullSpinGameUnits / 2f; // ~64.5

        // ── State ──

        private bool _steeringLeft;
        private bool _steeringRight;
        private DateTime _turnStartTime;
        private bool _isTurning;

        /// <summary>True when A is being held.</summary>
        public bool IsSteeringLeft => _steeringLeft;

        /// <summary>True when D is being held.</summary>
        public bool IsSteeringRight => _steeringRight;

        public KeyboardSteeringController(GameMemoryService memory, Action<string> log)
        {
            _memory = memory ?? throw new ArgumentNullException(nameof(memory));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// Steer the bot toward the desired raw game angle by holding A or D.
        /// The bot keeps moving forward (W must be held separately).
        /// </summary>
        /// <param name="desiredBearingDeg">Target bearing in degrees (0=N, 90=E).</param>
        /// <param name="keepMovingForward">If true, caller ensures W is down.</param>
        public void SteerTowards(float desiredBearingDeg, bool keepMovingForward)
        {
            if (!MovementTuning.UseKeyboardSteering)
            {
                // Legacy: caller handles SetCameraAngle directly
                return;
            }

            // Read current camera angle from game memory
            short currentRawShort = _memory.GetCameraAngle();
            float currentRaw = currentRawShort;

            // Compute base game angle for the desired bearing
            float desiredBase = GeometryUtils.InterpolateBearingToGameAngle(desiredBearingDeg);

            // Adjust desired raw angle to be as close as possible to current raw
            float desiredRaw = FindClosestWrappedAngle(desiredBase, currentRaw);

            // Signed shortest-path difference
            float diff = desiredRaw - currentRaw;
            diff = NormalizeSignedDiff(diff);

            float stopTol = MovementTuning.SteeringStopToleranceRaw;
            float startTol = MovementTuning.SteeringStartToleranceRaw;

            float absDiff = Math.Abs(diff);

            // ── Decide whether to steer ──
            if (absDiff <= stopTol)
            {
                // Within tolerance: release steering keys
                ReleaseSteeringKeys();
                return;
            }

            if (absDiff < startTol && (_steeringLeft || _steeringRight))
            {
                // In the hysteresis zone and already steering: continue current direction
                // (no change — keep holding the current key)
            }
            else if (absDiff >= startTol || absDiff > stopTol)
            {
                // Outside hysteresis: start or adjust steering
                bool dIsClockwise = MovementTuning.SteerDIsClockwise;
                bool shouldSteerRight = (diff > 0) == dIsClockwise;

                if (shouldSteerRight)
                {
                    if (!_steeringRight)
                    {
                        ReleaseSteeringKeys();
                        GameInput.keybd_event(GameInput.VK_D, GameInput.SCAN_D, 0, 0);
                        _steeringRight = true;
                        _log($"[Steering] D down  desiredRaw={desiredRaw:F1} currentRaw={currentRaw:F1} diff={diff:F1}");
                    }
                }
                else
                {
                    if (!_steeringLeft)
                    {
                        ReleaseSteeringKeys();
                        GameInput.keybd_event(GameInput.VK_A, GameInput.SCAN_A, 0, 0);
                        _steeringLeft = true;
                        _log($"[Steering] A down  desiredRaw={desiredRaw:F1} currentRaw={currentRaw:F1} diff={diff:F1}");
                    }
                }

                // Safety: max continuous turn
                if (!_isTurning)
                {
                    _isTurning = true;
                    _turnStartTime = DateTime.Now;
                }
                else if ((DateTime.Now - _turnStartTime).TotalMilliseconds >= MovementTuning.SteeringMaxContinuousTurnMs)
                {
                    _log($"[Steering] safety timeout {MovementTuning.SteeringMaxContinuousTurnMs}ms reached, releasing A/D");
                    ReleaseSteeringKeys();
                    _isTurning = false;
                }
            }
        }

        /// <summary>
        /// Release A and D keys (call when movement stops or steering is no longer needed).
        /// </summary>
        public void ReleaseSteering()
        {
            ReleaseSteeringKeys();
            _isTurning = false;
        }

        /// <summary>
        /// Release all movement keys: A, D, and W.
        /// Use when bot should stop completely.
        /// </summary>
        public void StopAllMovement()
        {
            ReleaseSteeringKeys();
            GameInput.keybd_event(GameInput.VK_W, GameInput.SCAN_W, (uint)GameInput.KEYEVENTF_KEYUP, 0);
            _log("[Steering] stop all movement, releasing W/A/D");
            _isTurning = false;
        }

        // ── Private helpers ──

        private void ReleaseSteeringKeys()
        {
            if (_steeringLeft)
            {
                GameInput.keybd_event(GameInput.VK_A, GameInput.SCAN_A, (uint)GameInput.KEYEVENTF_KEYUP, 0);
                _steeringLeft = false;
            }
            if (_steeringRight)
            {
                GameInput.keybd_event(GameInput.VK_D, GameInput.SCAN_D, (uint)GameInput.KEYEVENTF_KEYUP, 0);
                _steeringRight = false;
            }
        }

        /// <summary>
        /// Finds the closest value to currentRaw by wrapping baseValue by ±k*ManualFullSpinGameUnits.
        /// </summary>
        private static float FindClosestWrappedAngle(float baseValue, float currentRaw)
        {
            float best = baseValue;
            float bestDist = Math.Abs(best - currentRaw);

            for (int k = -3; k <= 3; k++)
            {
                float candidate = baseValue + GeometryUtils.ManualFullSpinGameUnits * k;
                float dist = Math.Abs(candidate - currentRaw);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = candidate;
                }
            }
            return best;
        }

        /// <summary>
        /// Normalizes a signed raw-angle difference to the range [-HalfCircleRaw, HalfCircleRaw].
        /// </summary>
        private static float NormalizeSignedDiff(float diff)
        {
            float fullSpin = GeometryUtils.ManualFullSpinGameUnits;
            while (diff > HalfCircleRaw) diff -= fullSpin;
            while (diff < -HalfCircleRaw) diff += fullSpin;
            return diff;
        }
    }
}
