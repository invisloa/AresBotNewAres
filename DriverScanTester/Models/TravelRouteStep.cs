namespace DriverScanTester.Models
{
    /// <summary>
    /// Determines how a travel route (a leg of the Repot → EXP journey) is considered complete.
    /// </summary>
    public enum TravelRouteCompletionMode
    {
        /// <summary>
        /// Execute the path normally and complete when the final waypoint is reached
        /// (MovementSystem.IsGoalReached). ExpectedDestinationMapNumber must be 0.
        /// </summary>
        FinalWaypoint = 0,

        /// <summary>
        /// Follow the path, but complete as soon as the configured nonzero destination map
        /// is stably detected — the final waypoint does not need to be reached. Used for
        /// portal routes that teleport the player to another map mid-path.
        /// </summary>
        ExpectedMapReached = 1
    }

    /// <summary>
    /// One ordered leg of the journey from the repot location to the EXP position.
    /// A hunt's TravelToExpRoutes chain may contain any number of these steps.
    /// </summary>
    public sealed class TravelRouteStep
    {
        /// <summary>Filename of the saved segment in SavedPaths (with or without .json).</summary>
        public string PathFile { get; set; } = "";

        /// <summary>
        /// Delay in milliseconds to wait before this route starts.
        /// Zero means no wait. Negative values are invalid.
        /// </summary>
        public int StartDelayMs { get; set; }

        /// <summary>How this route completes: final waypoint or expected destination map.</summary>
        public TravelRouteCompletionMode CompletionMode { get; set; }
            = TravelRouteCompletionMode.FinalWaypoint;

        /// <summary>
        /// Destination map number for <see cref="TravelRouteCompletionMode.ExpectedMapReached"/>.
        /// Must be 0 for <see cref="TravelRouteCompletionMode.FinalWaypoint"/> and greater
        /// than 0 for <see cref="TravelRouteCompletionMode.ExpectedMapReached"/>.
        /// </summary>
        public int ExpectedDestinationMapNumber { get; set; }
    }
}
