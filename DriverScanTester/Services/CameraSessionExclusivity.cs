namespace DriverScanTester.Services
{
    /// <summary>
    /// Pure exclusivity decision shared by entry points that create a new
    /// W-camera session. It only describes an already-running runtime; it does
    /// not start, stop or poll anything.
    /// </summary>
    internal static class CameraSessionExclusivity
    {
        internal static string? FindConflict(
            bool workflowRunning,
            bool movementRunning,
            bool lootRunning,
            bool movementDiagnosticBlocking,
            bool workflowTaskActive,
            bool movementTaskActive,
            bool lootTaskActive)
        {
            if (workflowRunning) return "profile workflow is running";
            if (movementRunning) return "standalone movement bot is running";
            if (lootRunning) return "standalone loot bot is running";
            if (movementDiagnosticBlocking) return "movement diagnostic requires exclusive input";
            if (workflowTaskActive) return "previous workflow is still cleaning up";
            if (movementTaskActive) return "previous movement bot is still cleaning up";
            if (lootTaskActive) return "previous loot bot is still cleaning up";
            return null;
        }
    }
}
