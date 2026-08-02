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
    }
}
