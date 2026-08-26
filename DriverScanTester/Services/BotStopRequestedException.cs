using System;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Thrown by bot services when the bot must stop immediately — e.g. the game
    /// window is no longer the selected (foreground) window while an NPC scan is
    /// sweeping the game window. Propagates up to the workflow coordinator, which
    /// treats it as a clean stop request (same result as pressing the Stop button).
    /// </summary>
    public sealed class BotStopRequestedException : Exception
    {
        public BotStopRequestedException(string message) : base(message) { }
    }
}