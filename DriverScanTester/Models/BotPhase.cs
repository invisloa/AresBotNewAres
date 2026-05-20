namespace DriverScanTester.Models
{
    /// <summary>
    /// Represents the current phase of the bot workflow.
    /// </summary>
    public enum BotPhase
    {
        /// <summary>Bot is stopped or not initialized.</summary>
        Idle,

        /// <summary>Bot is detecting current position and city, preparing to move to repot.</summary>
        DetectCityStart,

        /// <summary>Bot is moving from city to the repot NPC.</summary>
        MoveToRepot,

        /// <summary>Bot is performing repot actions (sell items, buy potions).</summary>
        Repot,

        /// <summary>Bot is moving from repot to the exp hunting area.</summary>
        MoveToExp,

        /// <summary>Bot is in the exp hunting loop (move, attack, loot).</summary>
        ExpLoop,

        /// <summary>Bot detected that repot is needed; will teleport to city.</summary>
        NeedRepot,

        /// <summary>Bot is stopping due to user request.</summary>
        Stopping,

        /// <summary>Bot encountered an unrecoverable error.</summary>
        Failed
    }
}
