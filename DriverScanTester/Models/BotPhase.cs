namespace DriverScanTester.Models
{
    /// <summary>
    /// Represents the current phase of the bot workflow.
    /// </summary>
    public enum BotPhase
    {
        /// <summary>Bot is stopped or not initialized.</summary>
        Idle,

        /// <summary>Bot is detecting current position and city, preparing to start the flow.</summary>
        DetectCityStart,

        /// <summary>Bot is walking a Path flow step (a saved segment, once).</summary>
        PathStep,

        /// <summary>Bot is performing the repot flow step (teleport, walk to repot, sell, buy).</summary>
        Repot,

        /// <summary>Bot is running a custom operation flow step (named BotOperations method).</summary>
        OperationStep,

        /// <summary>Bot is in the exp hunting loop flow step (move, attack, loot).</summary>
        ExpLoop,

        /// <summary>Bot detected that repot is needed; will teleport to city.</summary>
        NeedRepot,

        /// <summary>Bot is stopping due to user request.</summary>
        Stopping,

        /// <summary>Bot encountered an unrecoverable error.</summary>
        Failed
    }
}
