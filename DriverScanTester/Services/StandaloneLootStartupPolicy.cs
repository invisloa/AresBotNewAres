namespace DriverScanTester.Services
{
    /// <summary>
    /// Decision seam for starting a standalone loot runtime. It never mutates any
    /// runtime: it only reports whether a separate loot loop may start and, when not,
    /// why the start must be rejected.
    /// </summary>
    internal readonly struct StandaloneLootStartupDecision
    {
        internal bool CanStart { get; }
        internal string? RejectionReason { get; }

        internal StandaloneLootStartupDecision(bool canStart, string? rejectionReason)
        {
            CanStart = canStart;
            RejectionReason = rejectionReason;
        }
    }

    internal static class StandaloneLootStartupPolicy
    {
        /// <summary>
        /// A profile workflow owns the movement/camera session, including its own loot
        /// system. Standalone loot must also wait for the workflow task's final cleanup,
        /// and while standalone movement is active its shared camera controller must be
        /// available before a loot loop can join it.
        /// </summary>
        internal static StandaloneLootStartupDecision Evaluate(
            bool workflowRunning,
            bool workflowTaskActive,
            bool movementRunning,
            bool movementCameraAvailable)
        {
            if (workflowRunning)
            {
                return new StandaloneLootStartupDecision(
                    canStart: false,
                    rejectionReason: "the active profile workflow owns movement and camera control");
            }

            if (workflowTaskActive)
            {
                return new StandaloneLootStartupDecision(
                    canStart: false,
                    rejectionReason: "the profile workflow is still cleaning up");
            }

            if (movementRunning && !movementCameraAvailable)
            {
                return new StandaloneLootStartupDecision(
                    canStart: false,
                    rejectionReason: "the movement bot's camera session is still active, closing, or unavailable");
            }

            return new StandaloneLootStartupDecision(canStart: true, rejectionReason: null);
        }
    }
}
