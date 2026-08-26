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
        /// route, using THIS route's OWN settings: if the player is not on
        /// <see cref="StartPositionX"/>/<see cref="StartPositionY"/> (within
        /// <see cref="StartPositionTolerance"/> tiles), the bot uses the town teleport
        /// scroll, taps a step to refresh the position memory, and verifies the map
        /// against <see cref="ProtectionMapNumber"/>. If the verification fails the
        /// workflow stops. These values are per-route — every route with the start
        /// check enabled carries its own position, map and tolerance.
        /// </summary>
        public bool StartCheckEnabled { get; set; } = false;

        /// <summary>Expected X coordinate of the player before this route starts (own start check data).</summary>
        public int StartPositionX { get; set; } = 0;

        /// <summary>Expected Y coordinate of the player before this route starts (own start check data).</summary>
        public int StartPositionY { get; set; } = 0;

        /// <summary>
        /// Map number the player must be on after the start teleport for THIS route
        /// (own start check data). 0 disables the map check for this route.
        /// </summary>
        public int ProtectionMapNumber { get; set; } = 0;

        /// <summary>
        /// Tolerance in game tiles for THIS route's start-position comparison (own start
        /// check data, default 5).
        /// </summary>
        public int StartPositionTolerance { get; set; } = 5;
    }
}
