using System;
using System.Collections.Generic;
using System.Linq;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Tracks a rolling window of movement state to detect stalls, hard-stuck,
    /// and wrong-way conditions without relying solely on game action byte.
    /// All distances are in game tiles, all times are in seconds.
    /// </summary>
    internal class MovementProgressTracker
    {
        // ── Configuration ──
        public double SampleIntervalSeconds { get; set; } = 0.100;    // 100ms between samples
        public double ProgressWindowSeconds { get; set; } = 1.50;     // rolling window length
        public double HardNoMovementWindowSeconds { get; set; } = 1.25;
        public float MinDistanceProgress { get; set; } = 0.75f;       // per window
        public float MinProjectionProgress { get; set; } = 0.50f;     // per window
        public float HardNoMovementDistance { get; set; } = 0.35f;    // per 1.25s
        public float WrongWayWorsenThreshold { get; set; } = 2.0f;    // distance worsened per short window
        public double WrongWayWindowSeconds { get; set; } = 0.80;

        // ── Rolling state ──
        private readonly List<(DateTime Time, float X, float Y, float DistToTarget, float ProjAlongSegment, byte Action)> _samples
            = new List<(DateTime, float, float, float, float, byte)>();

        // ── Current target info (for projection) ──
        private (float X, float Y) _segmentStart;
        private (float X, float Y) _segmentEnd;
        private float _segmentLength;
        private bool _hasSegment = false;

        // ── Warmup / status ──
        public bool HasEnoughSamples => _samples.Count >= 2;
        public bool IsWarmingUp => !HasEnoughSamples || GetWindowElapsed() < 0.5;
        public bool HasSegment => _hasSegment;
        public float SegmentEndX => _segmentEnd.X;
        public float SegmentEndY => _segmentEnd.Y;

        // ── Last meaningful progress ──
        public DateTime LastProgressTime { get; private set; } = DateTime.MinValue;
        public float BestDistToTarget { get; private set; } = float.MaxValue;
        public (float X, float Y) BestPosition { get; private set; }
        public bool HasBestPosition { get; private set; } = false;

        // ── Grace / cooldown ──
        public DateTime GraceUntil { get; set; } = DateTime.MinValue;
        public bool InGrace => DateTime.Now < GraceUntil;

        // ── Last sample ──
        public DateTime LastSampleTime { get; private set; } = DateTime.MinValue;
        public float LastX { get; private set; }
        public float LastY { get; private set; }
        public float LastDistToTarget { get; private set; } = float.MaxValue;
        public float LastProjection { get; private set; }
        public byte LastAction { get; private set; }
        public bool HasSamples => _samples.Count > 0;

        private readonly Action<string> _log;
        private int _logThrottle = 0;

        public MovementProgressTracker(Action<string> log)
        {
            _log = log;
        }

        /// <summary>
        /// Call every tick when the bot intends to move and has a target.
        /// </summary>
        public void RecordSample(float x, float y, float targetX, float targetY, byte action)
        {
            DateTime now = DateTime.Now;

            // Prune old samples outside the window
            double maxWindow = Math.Max(ProgressWindowSeconds, Math.Max(HardNoMovementWindowSeconds, WrongWayWindowSeconds)) + 0.5;
            DateTime cutoff = now.AddSeconds(-maxWindow);
            _samples.RemoveAll(s => s.Time < cutoff);

            float dist = GeometryUtils.Distance(x, y, targetX, targetY);

            // Compute projection along route segment
            float proj = 0f;
            if (_hasSegment && _segmentLength > 0.001f)
            {
                float dx = x - _segmentStart.X;
                float dy = y - _segmentStart.Y;
                float segDx = _segmentEnd.X - _segmentStart.X;
                float segDy = _segmentEnd.Y - _segmentStart.Y;
                float dot = dx * segDx + dy * segDy;
                proj = Math.Clamp(dot / _segmentLength, 0f, _segmentLength);
            }

            // Always append sample (no replacement). The rolling window pruning
            // and GetWindow() selection ensure only the relevant time range is used.
            _samples.Add((now, x, y, dist, proj, action));

            // Hard cap to prevent unbounded growth in edge cases
            while (_samples.Count > 200)
                _samples.RemoveAt(0);

            LastSampleTime = now;
            LastX = x;
            LastY = y;
            LastDistToTarget = dist;
            LastProjection = proj;
            LastAction = action;

            // Track best distance
            if (dist < BestDistToTarget - 0.01f)
            {
                BestDistToTarget = dist;
                BestPosition = (x, y);
                HasBestPosition = true;
                LastProgressTime = now;
            }
        }

        /// <summary>
        /// Set the current route segment for projection calculation.
        /// </summary>
        public void SetSegment(float startX, float startY, float endX, float endY)
        {
            _segmentStart = (startX, startY);
            _segmentEnd = (endX, endY);
            _segmentLength = GeometryUtils.Distance(startX, startY, endX, endY);
            _hasSegment = true;
        }

        public void ClearSegment()
        {
            _hasSegment = false;
            _segmentLength = 0f;
        }

        /// <summary>
        /// Returns all samples in the last <paramref name="windowSeconds"/>.
        /// </summary>
        private List<(DateTime Time, float X, float Y, float Dist, float Proj, byte Action)> GetWindow(double windowSeconds)
        {
            DateTime cutoff = DateTime.Now.AddSeconds(-windowSeconds);
            return _samples.Where(s => s.Time >= cutoff).ToList();
        }

        /// <summary>
        /// Total displacement (tiles) across the rolling window.
        /// Returns 0 when fewer than 2 samples are available (warming up).
        /// </summary>
        public float GetWindowDisplacement(double? windowSeconds = null)
        {
            var window = GetWindow(windowSeconds ?? ProgressWindowSeconds);
            if (window.Count < 2) return 0f; // not enough data — still warming up
            return GeometryUtils.Distance(window[0].X, window[0].Y, window[^1].X, window[^1].Y);
        }

        /// <summary>
        /// Change in distance-to-target across the window (negative = improving).
        /// </summary>
        public float GetWindowDistDelta(double? windowSeconds = null)
        {
            var window = GetWindow(windowSeconds ?? ProgressWindowSeconds);
            if (window.Count < 2) return 0f;
            return window[^1].Dist - window[0].Dist;
        }

        /// <summary>
        /// Change in projection along route segment across the window (positive = advancing).
        /// </summary>
        public float GetWindowProjDelta(double? windowSeconds = null)
        {
            var window = GetWindow(windowSeconds ?? ProgressWindowSeconds);
            if (window.Count < 2 || !_hasSegment) return 0f;
            return window[^1].Proj - window[0].Proj;
        }

        /// <summary>
        /// Window elapsed time (seconds).
        /// </summary>
        public double GetWindowElapsed(double? windowSeconds = null)
        {
            var window = GetWindow(windowSeconds ?? ProgressWindowSeconds);
            if (window.Count < 2) return 0.0;
            return (window[^1].Time - window[0].Time).TotalSeconds;
        }

        /// <summary>
        /// True: displacement less than HardNoMovementDistance over HardNoMovementWindow.
        /// </summary>
        public bool IsHardStuck(bool isMovingForward, bool nearTarget)
        {
            if (nearTarget) return false;
            if (!isMovingForward) return false;
            if (InGrace) return false;

            float disp = GetWindowDisplacement(HardNoMovementWindowSeconds);
            float distDelta = GetWindowDistDelta(HardNoMovementWindowSeconds);
            double elapsed = GetWindowElapsed(HardNoMovementWindowSeconds);

            bool noDisplacement = disp < HardNoMovementDistance;
            bool noImprovement = distDelta >= -MinDistanceProgress * 0.3f; // not improving meaningfully

            if (noDisplacement && noImprovement && elapsed >= HardNoMovementWindowSeconds * 0.8)
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// True: player moves but makes no useful progress toward target over ProgressWindow.
        /// </summary>
        public bool IsSoftStuck(bool isMovingForward, bool nearTarget)
        {
            if (nearTarget) return false;
            if (!isMovingForward) return false;
            if (InGrace) return false;

            float disp = GetWindowDisplacement(ProgressWindowSeconds);
            float distDelta = GetWindowDistDelta(ProgressWindowSeconds);
            float projDelta = GetWindowProjDelta(ProgressWindowSeconds);
            double elapsed = GetWindowElapsed(ProgressWindowSeconds);

            bool movingButNoProgress = disp >= HardNoMovementDistance // player is moving
                && distDelta > -MinDistanceProgress * 0.5f             // not improving enough
                && projDelta < MinProjectionProgress * 0.5f            // not projecting forward
                && elapsed >= ProgressWindowSeconds * 0.8;

            return movingButNoProgress;
        }

        /// <summary>
        /// True: distance-to-target is getting worse over a short window.
        /// </summary>
        public bool IsWrongWay(bool isMovingForward, bool nearTarget)
        {
            if (nearTarget) return false;
            if (!isMovingForward) return false;
            if (InGrace) return false;

            float distDelta = GetWindowDistDelta(WrongWayWindowSeconds);
            double elapsed = GetWindowElapsed(WrongWayWindowSeconds);

            if (distDelta > WrongWayWorsenThreshold && elapsed >= WrongWayWindowSeconds * 0.7)
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Returns a detailed status string for periodic logging.
        /// Returns a warming indicator when insufficient data is available.
        /// </summary>
        public string GetStatusString()
        {
            if (_samples.Count < 2)
            {
                return $"warming samples={_samples.Count} elapsed=0.00s";
            }
            float disp = GetWindowDisplacement(ProgressWindowSeconds);
            float dDist = GetWindowDistDelta(ProgressWindowSeconds);
            float dProj = GetWindowProjDelta(ProgressWindowSeconds);
            double elapsed = GetWindowElapsed(ProgressWindowSeconds);
            if (elapsed < 0.5)
            {
                return $"warming samples={_samples.Count} elapsed={elapsed:F2}s";
            }
            return $"window={elapsed:F2}s moved={disp:F2} distDelta={dDist:F2} projDelta={dProj:F2} action={LastAction}";
        }

        /// <summary>
        /// Reset all tracking state.
        /// </summary>
        public void Reset()
        {
            _samples.Clear();
            LastProgressTime = DateTime.MinValue;
            BestDistToTarget = float.MaxValue;
            HasBestPosition = false;
            BestPosition = (0f, 0f);
            LastSampleTime = DateTime.MinValue;
            _hasSegment = false;
            _segmentLength = 0f;
            GraceUntil = DateTime.MinValue;
        }

        /// <summary>
        /// Reset just the best-distance tracking (called on target change).
        /// </summary>
        public void ResetBestDistance()
        {
            BestDistToTarget = float.MaxValue;
            HasBestPosition = false;
            LastProgressTime = DateTime.MinValue;
        }
    }
}
