using System;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Central tuning/configuration for movement unstuck and recovery behavior.
    /// All values are public static fields so they can be easily toggled/overridden
    /// during development without changing the public API of MovementSystem or
    /// any other service.
    ///
    /// Usage (when wiring up the corresponding logic):
    ///   if (MovementTuning.UseDebouncedActionStuck) { ... }
    ///   int delay = MovementTuning.ReverseDiagonalAttemptMs;
    ///
    /// Defaults reflect the intended production values for the new recovery layer.
    /// Set to false to disable a feature without code changes.
    /// </summary>
    public static class MovementTuning
    {
        // ── Feature flags ──────────────────────────────────────────────────────

        /// <summary>
        /// If true, use debounced (multi-sample) action-stuck detection
        /// requiring <see cref="ActionStuckRequiredSamples"/> samples over
        /// <see cref="ActionStuckRequiredMs"/> milliseconds before declaring stuck.
        /// </summary>
        public static bool UseDebouncedActionStuck = true;

        /// <summary>
        /// If true, use reverse-diagonal recovery (backward + strafe) when
        /// forward movement fails during unstuck.
        /// </summary>
        public static bool UseReverseDiagonalRecovery = true;

        /// <summary>
        /// If true, use MovementProgressTracker-based displacement / distance
        /// confirmation in Bug2 candidate evaluation instead of relying solely
        /// on the game action byte.
        /// </summary>
        public static bool UseTrackerBug2Confirmation = true;

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

        // ── Debounced action-stuck detection ───────────────────────────────────

        /// <summary>
        /// Number of consecutive stuck-action samples required before confirming
        /// an action-stuck condition. Only used when <see cref="UseDebouncedActionStuck"/>
        /// is true.
        /// </summary>
        public static int ActionStuckRequiredSamples = 2;

        /// <summary>
        /// Minimum time window in milliseconds over which action-stuck samples
        /// must be collected to confirm stuck. Only used when
        /// <see cref="UseDebouncedActionStuck"/> is true.
        /// </summary>
        public static int ActionStuckRequiredMs = 200;
    }
}
