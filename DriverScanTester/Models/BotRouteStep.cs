namespace DriverScanTester.Models
{
    /// <summary>
    /// A single route step of the bot workflow: a reference to a saved path segment
    /// plus an optional startup delay in milliseconds executed before the step starts.
    /// </summary>
    public sealed class BotRouteStep
    {
        /// <summary>Filename of the saved segment in SavedPaths (with or without .json).</summary>
        public string PathFile { get; set; } = "";

        /// <summary>
        /// Delay in milliseconds to wait before this stage starts.
        /// Zero means no wait. Negative values are invalid.
        /// </summary>
        public int StartDelayMs { get; set; } = 0;

        /// <summary>
        /// When true, the bot runs the start-position protection before executing THIS
        /// route: if the player is not on the profile's start coordinates
        /// (<see cref="BotProfile.StartPositionX"/>/<see cref="BotProfile.StartPositionY"/>,
        /// within the profile's tolerance), the bot uses the town teleport scroll,
        /// taps a step to refresh the position memory, and verifies the map against the
        /// profile's protected map. If the verification fails the workflow stops.
        /// Uses the same start position / protection settings as the profile-level
        /// start check.
        /// </summary>
        public bool StartCheckEnabled { get; set; } = false;
    }
}
