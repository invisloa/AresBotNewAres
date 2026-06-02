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

        // ─────────────────────── Bearing → camera angle (radians) ───────────────────────

        /// <summary>
        /// Converts a bearing (degrees from North) into the game's camera angle
        /// (radians). This is a pure unit conversion: the camera in memory is a
        /// 32-bit float in radians with the same convention as our bearings
        /// (0 = N, π/2 = E, π = S, 3π/2 = W), so the math is just degrees → radians.
        /// No calibration table is involved.
        /// </summary>
        internal static float ConvertBearingToRadians(float bearingDeg)
        {
            bearingDeg = NormalizeBearingDeg(bearingDeg);
            return DegToRad(bearingDeg);
        }

        /// <summary>
        /// Converts a camera angle in radians (0 = N, π/2 = E, …) back into a
        /// bearing in degrees (0 = N, 90 = E, …). Pure unit conversion.
        /// </summary>
        internal static float ConvertRadiansToBearingDeg(float radians)
        {
            float deg = RadToDeg(radians);
            return NormalizeBearingDeg(deg);
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
