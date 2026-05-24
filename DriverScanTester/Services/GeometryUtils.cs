using System;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Static geometry, bearing, and line-of-sight utilities used by MovementSystem.
    /// </summary>
    internal static class GeometryUtils
    {
        internal const float PI = (float)Math.PI;
        internal const float TwoPI = PI * 2;

        // ─────────────────────── Bearing calibration data ───────────────────────

        internal struct BearingCalibrationPoint
        {
            public float BearingDeg;
            public float GameAngle;

            public BearingCalibrationPoint(float bearingDeg, float gameAngle)
            {
                BearingDeg = bearingDeg;
                GameAngle = gameAngle;
            }
        }

        /// <summary>
        /// Manual yaw/bearing calibration from measured data.
        /// Bearing convention: 0 = N / +Y, 90 = E / +X, 180 = S / -Y, 270 = W / -X.
        /// </summary>
        internal static readonly BearingCalibrationPoint[] BearingCalibration =
        {
            new BearingCalibrationPoint(0f,   BotConstants.BearingCalibration.North),          // Full N
            new BearingCalibrationPoint(30f,  BotConstants.BearingCalibration.Deg30),          // +30 to E
            new BearingCalibrationPoint(60f,  BotConstants.BearingCalibration.Deg60),          // +60 to E
            new BearingCalibrationPoint(90f,  BotConstants.BearingCalibration.East),           // Full E
            new BearingCalibrationPoint(120f, BotConstants.BearingCalibration.Deg120),         // +30 to S
            new BearingCalibrationPoint(150f, BotConstants.BearingCalibration.Deg150),         // +60 to S
            new BearingCalibrationPoint(180f, BotConstants.BearingCalibration.South),          // Full S
            new BearingCalibrationPoint(210f, BotConstants.BearingCalibration.Deg210),         // +30 to W
            new BearingCalibrationPoint(240f, BotConstants.BearingCalibration.Deg240),         // +60 to W
            new BearingCalibrationPoint(270f, BotConstants.BearingCalibration.West),           // Full W
            new BearingCalibrationPoint(300f, BotConstants.BearingCalibration.Deg300),         // +30 to N
            new BearingCalibrationPoint(330f, BotConstants.BearingCalibration.Deg330),         // +60 to N
            new BearingCalibrationPoint(360f, BotConstants.BearingCalibration.NorthFullCircle) // Full N again
        };

        internal const float ManualFullSpinGameUnits = BotConstants.BearingCalibration.FullSpinGameUnits; // 129

        // ─────────────────────── Obstacle constants ───────────────────────

        internal static readonly (float X, float Y) ObstacleCenter = BotConstants.Obstacle.Center;
        internal const float ObstacleSize = BotConstants.Obstacle.Size; // Assuming 200x200 square centered

        // ─────────────────────── Bearing / angle helpers ───────────────────────

        internal static float NormalizeBearingDeg(float deg)
        {
            while (deg < 0f) deg += 360f;
            while (deg >= 360f) deg -= 360f;
            return deg;
        }

        internal static float GetShortestBearingDiffDeg(float fromDeg, float toDeg)
        {
            float diff = NormalizeBearingDeg(toDeg) - NormalizeBearingDeg(fromDeg);

            while (diff > 180f) diff -= 360f;
            while (diff < -180f) diff += 360f;

            return diff;
        }

        internal static float RadToDeg(float radians)
        {
            return radians * 180f / PI;
        }

        internal static float DegToRad(float degrees)
        {
            return degrees * PI / 180f;
        }

        // ─────────────────────── Bearing → game angle ───────────────────────

        /// <summary>
        /// Converts a bearing (degrees from North) into the game's raw camera angle value.
        /// Uses the calibration table and wraps around full-spin revolutions to keep
        /// the angle close to the last-set game angle.
        /// </summary>
        internal static short ConvertBearingToGameAngle(float bearingDeg, float lastSetGameAngle, bool hasLastGameAngle)
        {
            bearingDeg = NormalizeBearingDeg(bearingDeg);
            float baseAngle = InterpolateBearingToGameAngle(bearingDeg);

            if (!hasLastGameAngle || ManualFullSpinGameUnits <= 0.001f)
            {
                return (short)Math.Round(baseAngle);
            }

            float best = baseAngle;
            float bestDiff = Math.Abs(best - lastSetGameAngle);

            for (int k = -3; k <= 3; k++)
            {
                float candidate = baseAngle + (ManualFullSpinGameUnits * k);
                float candidateDiff = Math.Abs(candidate - lastSetGameAngle);

                if (candidateDiff < bestDiff)
                {
                    best = candidate;
                    bestDiff = candidateDiff;
                }
            }

            return (short)Math.Round(best);
        }

        internal static float InterpolateBearingToGameAngle(float bearingDeg)
        {
            bearingDeg = NormalizeBearingDeg(bearingDeg);

            if (bearingDeg <= 0.0001f)
            {
                return BearingCalibration[0].GameAngle;
            }

            for (int i = 0; i < BearingCalibration.Length - 1; i++)
            {
                BearingCalibrationPoint a = BearingCalibration[i];
                BearingCalibrationPoint b = BearingCalibration[i + 1];

                if (bearingDeg >= a.BearingDeg && bearingDeg <= b.BearingDeg)
                {
                    float range = b.BearingDeg - a.BearingDeg;
                    if (Math.Abs(range) < 0.001f)
                    {
                        return a.GameAngle;
                    }

                    float t = (bearingDeg - a.BearingDeg) / range;
                    return a.GameAngle + ((b.GameAngle - a.GameAngle) * t);
                }
            }

            return BearingCalibration[BearingCalibration.Length - 1].GameAngle;
        }

        // ─────────────────────── Bearing → delta / target ───────────────────────

        /// <summary>
        /// Returns the bearing (degrees from North) from (currX, currY) to (targetX, targetY).
        /// </summary>
        internal static float GetBearingToTargetDeg(float currX, float currY, float targetX, float targetY)
        {
            float dx = targetX - currX;
            float dy = targetY - currY;

            // Bearing from North: N = 0 deg, E = 90 deg, S = 180 deg, W = 270 deg.
            float bearing = RadToDeg((float)Math.Atan2(dx, dy));
            return NormalizeBearingDeg(bearing);
        }

        internal static float BearingToDeltaX(float bearingDeg)
        {
            float rad = DegToRad(NormalizeBearingDeg(bearingDeg));
            return (float)Math.Sin(rad);
        }

        internal static float BearingToDeltaY(float bearingDeg)
        {
            float rad = DegToRad(NormalizeBearingDeg(bearingDeg));
            return (float)Math.Cos(rad);
        }

        // ─────────────────────── Distance ───────────────────────

        internal static float Distance(float x1, float y1, float x2, float y2)
        {
            float dx = x1 - x2;
            float dy = y1 - y2;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        internal static float ManhattanDistance(float x1, float y1, float x2, float y2)
        {
            return Math.Abs(x1 - x2) + Math.Abs(y1 - y2);
        }

        // ─────────────────────── Line-of-sight / obstacle ───────────────────────

        /// <summary>
        /// Checks whether the line from (startX,startY) to (endX,endY) intersects
        /// the rectangular obstacle defined by ObstacleCenter / ObstacleSize.
        /// Returns true if the path is BLOCKED (needs to go around).
        /// </summary>
        internal static bool CheckLineOfSight(float startX, float startY, float endX, float endY)
        {
            float minX = ObstacleCenter.X - (ObstacleSize / 2);
            float maxX = ObstacleCenter.X + (ObstacleSize / 2);
            float minY = ObstacleCenter.Y - (ObstacleSize / 2);
            float maxY = ObstacleCenter.Y + (ObstacleSize / 2);

            return LineIntersectsRect(startX, startY, endX, endY, minX, minY, maxX, maxY);
        }

        /// <summary>
        /// Liang-Barsky line clipping against an axis-aligned rectangle.
        /// Returns true if the line segment intersects the rectangle.
        /// </summary>
        internal static bool LineIntersectsRect(float x1, float y1, float x2, float y2,
            float minX, float minY, float maxX, float maxY)
        {
            float p1 = -(x2 - x1);
            float p2 = (x2 - x1);
            float p3 = -(y2 - y1);
            float p4 = (y2 - y1);

            float q1 = x1 - minX;
            float q2 = maxX - x1;
            float q3 = y1 - minY;
            float q4 = maxY - y1;

            float t0 = 0.0f;
            float t1 = 1.0f;

            if (ClipLine(-p1, q1, ref t0, ref t1) &&
                ClipLine(-p2, q2, ref t0, ref t1) &&
                ClipLine(-p3, q3, ref t0, ref t1) &&
                ClipLine(-p4, q4, ref t0, ref t1))
            {
                return true;
            }

            return false;
        }

        private static bool ClipLine(float p, float q, ref float t0, ref float t1)
        {
            if (p == 0)
            {
                return q >= 0;
            }

            float r = q / p;

            if (p < 0)
            {
                if (r > t1) return false;
                if (r > t0) t0 = r;
            }
            else
            {
                if (r < t0) return false;
                if (r < t1) t1 = r;
            }

            return t0 <= t1;
        }

        // ─────────────────────── Waypoint reach threshold ───────────────────────

        /// <summary>
        /// Returns the distance threshold at which a waypoint is considered "reached"
        /// based on its MovementPrecision.
        /// </summary>
        internal static float GetWaypointReachThreshold(MovementPrecision precision)
        {
            switch (precision)
            {
                case MovementPrecision.Exact:
                    return BotConstants.WaypointThresholds.Exact;
                case MovementPrecision.Accurate:
                    return BotConstants.WaypointThresholds.Accurate;
                case MovementPrecision.Medium:
                    return BotConstants.WaypointThresholds.Medium;
                case MovementPrecision.High:
                    return BotConstants.WaypointThresholds.High;
                default:
                    return (float)precision;
            }
        }
    }
}
