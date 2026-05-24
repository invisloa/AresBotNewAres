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
            LockPriority.Low => BotConstants.LockPriority.LowScanInterval,  // 10 seconds
            LockPriority.Mid => BotConstants.LockPriority.MidScanInterval,  // 1 second
            LockPriority.High => BotConstants.LockPriority.HighScanInterval, // 100 ms
            _ => BotConstants.LockPriority.HighScanInterval
        };

        /// <summary>
        /// Gets the lock write interval in milliseconds for the given priority.
        /// </summary>
        public static int GetLockInterval(LockPriority priority) => priority switch
        {
            LockPriority.Low => BotConstants.LockPriority.LowLockInterval,   // 10 seconds
            LockPriority.Mid => BotConstants.LockPriority.MidLockInterval,   // 100 ms
            LockPriority.High => BotConstants.LockPriority.HighLockInterval, // 1 ms
            _ => BotConstants.LockPriority.HighLockInterval
        };
    }
}
