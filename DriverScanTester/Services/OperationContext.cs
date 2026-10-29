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

        /// <summary>Shared W-camera owner for direct W input issued by bot operations.</summary>
        public WKeyCameraController? WCameraController { get; }

        /// <summary>
        /// 1-based attempt number of the custom operation currently being executed by
        /// <see cref="OperationRunnerService"/>. Operations use it to label per-attempt
        /// diagnostics (screenshots, failure reasons). Defaults to 1 for direct/manual runs.
        /// </summary>
        public int CurrentOperationAttempt { get; internal set; } = 1;

        /// <summary>Maximum attempts configured for the operation currently executing.</summary>
        public int CurrentOperationMaxAttempts { get; internal set; } = OperationRunnerService.MaxAttempts;

        /// <summary>
        /// Optional capture hook for operation failure diagnostics. Production leaves this
        /// null so <see cref="ScreenshotService.CaptureOperationFailureScreenshot"/> is used;
        /// tests can inject a deterministic capture that records calls without touching a
        /// real screen. Returns the saved path, or null when capture failed.
        /// </summary>
        public Func<string, string, int, string?>? DiagnosticScreenshotCapture { get; }

        private readonly Action<bool> _sendWKeyEvent;

        /// <summary>Sends a W key-up/down event through the configured input path.</summary>
        public void SendWKeyEvent(bool keyUp) => _sendWKeyEvent(keyUp);

        public OperationContext(
            GameMemoryService memory,
            PathRunnerService pathRunner,
            ItemSellerService itemSeller,
            BotProfile profile,
            Action<string> log,
            Action focusGameWindow,
            PositionSyncService? positionSync = null,
            WKeyCameraController? wKeyCameraController = null,
            Action<bool>? sendWKeyEvent = null,
            Func<string, string, int, string?>? diagnosticScreenshotCapture = null)
        {
            Memory = memory;
            PathRunner = pathRunner;
            ItemSeller = itemSeller;
            Profile = profile;
            Log = log;
            FocusGameWindow = focusGameWindow;
            PositionSync = positionSync;
            WCameraController = wKeyCameraController;
            DiagnosticScreenshotCapture = diagnosticScreenshotCapture;
            _sendWKeyEvent = sendWKeyEvent ?? (keyUp => GameInput.keybd_event(
                GameInput.VK_W,
                GameInput.SCAN_W,
                keyUp ? (uint)GameInput.KEYEVENTF_KEYUP : 0u,
                0));
        }
    }
}
