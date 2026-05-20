using DriverScanTester.ViewModels;

namespace DriverScanTester.Models
{
    /// <summary>
    /// Configuration for lock and scan intervals per priority level.
    /// Scan = how often the live "Current" value is read from memory for display.
    /// Lock = how often the desired value is written to memory.
    /// </summary>
    public static class LockPriorityConfig
    {
        /// <summary>
        /// Gets the scan interval in milliseconds for the given priority.
        /// </summary>
        public static int GetScanInterval(LockPriority priority) => priority switch
        {
            LockPriority.Low => 10_000,  // 10 seconds
            LockPriority.Mid => 1_000,    // 1 second
            LockPriority.High => 100,     // 100 ms
            _ => 100
        };

        /// <summary>
        /// Gets the lock write interval in milliseconds for the given priority.
        /// </summary>
        public static int GetLockInterval(LockPriority priority) => priority switch
        {
            LockPriority.Low => 10_000,   // 10 seconds
            LockPriority.Mid => 100,      // 100 ms
            LockPriority.High => 1,       // 1 ms
            _ => 1
        };
    }
}
