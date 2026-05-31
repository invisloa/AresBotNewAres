using System;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Central tuning/configuration for movement unstuck and recovery behavior.
    /// All values are public static fields so they can be easily toggled/overridden
    /// during development without changing the public API of MovementSystem or
    /// any other service.
    /// </summary>
    public static class MovementTuning
    {
        // ── Feature flags ──────────────────────────────────────────────────────

        /// <summary>
        /// If true, use reverse-diagonal recovery (backward + strafe) when
        /// forward movement fails during unstuck.
        /// </summary>
        public static bool UseReverseDiagonalRecovery = true;

        // ── Reverse-diagonal recovery tuning ───────────────────────────────────

        /// <summary>Duration per reverse-diagonal attempt in milliseconds.</summary>
        public static int ReverseDiagonalAttemptMs = 200;

        /// <summary>Maximum number of reverse-diagonal attempts before giving up.</summary>
        public static int ReverseDiagonalMaxAttempts = 4;

        /// <summary>
        /// Minimum displacement (in game tiles) to consider a reverse-diagonal
        /// attempt successful.
        /// </summary>
        public static float ReverseDiagonalSuccessDisplacement = 0.60f;

        /// <summary>
        /// Minimum distance-to-target improvement (in game tiles) for a
        /// reverse-diagonal attempt to be considered successful.
        /// </summary>
        public static float ReverseDiagonalSuccessDistanceImprove = 0.50f;

        // ── Near-target stuck ignore ───────────────────────────────────────────

        /// <summary>
        /// Extra distance (in game tiles) added to the waypoint reach threshold
        /// when deciding whether to ignore a stuck state because the bot is
        /// already close to the current waypoint.
        /// </summary>
        public static float NearTargetStuckIgnoreExtra = 1.0f;
    }
}
