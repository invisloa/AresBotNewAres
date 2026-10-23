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
        /// system. Standalone loot must also wait for the workflow task's final cleanup
        /// and for the previous standalone loot task, and while standalone movement is
        /// active its shared camera controller must be available before a loot loop can
        /// join it. Any other camera-owning session for the same game process (for
        /// example an independent operation test) is incompatible because it may change
        /// the vertical camera angle or distance while loot is scanning.
        /// </summary>
        internal static StandaloneLootStartupDecision Evaluate(
            bool workflowRunning,
            bool workflowTaskActive,
            bool movementRunning,
            bool movementCameraAvailable,
            bool lootTaskActive,
            bool foreignCameraSessionActive)
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

            if (lootTaskActive)
            {
                return new StandaloneLootStartupDecision(
                    canStart: false,
                    rejectionReason: "the previous standalone loot task is still cleaning up");
            }

            if (movementRunning && !movementCameraAvailable)
            {
                return new StandaloneLootStartupDecision(
                    canStart: false,
                    rejectionReason: "the movement bot's camera session is still active, closing, or unavailable");
            }

            if (foreignCameraSessionActive)
            {
                return new StandaloneLootStartupDecision(
                    canStart: false,
                    rejectionReason: "another camera-owning operation still owns the game camera session");
            }

            return new StandaloneLootStartupDecision(canStart: true, rejectionReason: null);
        }
    }
}
