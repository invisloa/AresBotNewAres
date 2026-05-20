namespace DriverScanTester.Models
{
    /// <summary>
    /// Result status of a phase or step execution within the bot workflow.
    /// </summary>
    public enum StepResult
    {
        /// <summary>Step is still executing.</summary>
        Running,

        /// <summary>Step completed successfully.</summary>
        Completed,

        /// <summary>Repot is needed (e.g. low potions, high weight).</summary>
        NeedRepot,

        /// <summary>Player is stuck and cannot progress.</summary>
        Stuck,

        /// <summary>Step failed with an error.</summary>
        Failed,

        /// <summary>Step was cancelled/stopped by user.</summary>
        Stopped
    }
}
