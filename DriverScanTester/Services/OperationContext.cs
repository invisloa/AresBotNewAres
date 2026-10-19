using System;
using DriverScanTester.Models;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Services and settings handed to a custom bot operation so it can interact with
    /// the game (memory reads, movement, input) and report back to the user.
    /// A new context is built per workflow run and shared by all operations.
    /// </summary>
    public sealed class OperationContext
    {
        /// <summary>Reads live game state (position, map, city flag, HP/mana, potions, ...).</summary>
        public GameMemoryService Memory { get; }

        /// <summary>Runs saved path segments (useful for operations that move the player).</summary>
        public PathRunnerService PathRunner { get; }

        /// <summary>Handles NPC interaction (seller scan / dialog) and item selling.</summary>
        public ItemSellerService ItemSeller { get; }

        /// <summary>The active profile (teleport keys, thresholds, ...).</summary>
        public BotProfile Profile { get; }

        /// <summary>Logging callback shown in the bot UI.</summary>
        public Action<string> Log { get; }

        /// <summary>Brings the game window into focus before injecting input.</summary>
        public Action FocusGameWindow { get; }

        /// <summary>
        /// Tracks map transitions and post-transition position validation. Operations
        /// that can change the map should call <see cref="PositionSyncService.MarkTransitionImminent"/>
        /// immediately before triggering the transition, so stale destination X/Y can
        /// be detected reliably. Null when the caller did not supply a synchronization
        /// service (e.g. a standalone operation test).
        /// </summary>
        public PositionSyncService? PositionSync { get; }

        public OperationContext(
            GameMemoryService memory,
            PathRunnerService pathRunner,
            ItemSellerService itemSeller,
            BotProfile profile,
            Action<string> log,
            Action focusGameWindow,
            PositionSyncService? positionSync = null)
        {
            Memory = memory;
            PathRunner = pathRunner;
            ItemSeller = itemSeller;
            Profile = profile;
            Log = log;
            FocusGameWindow = focusGameWindow;
            PositionSync = positionSync;
        }
    }
}