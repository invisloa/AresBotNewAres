using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Utils;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Built-in custom bot operations that can be referenced by NAME from a profile:
    ///   • TravelRouteStep.OperationBefore / OperationAfter (run around a travel leg)
    ///   • BotProfile.PreExpOperations (run once at the EXP map, before hunting)
    ///
    /// This is the "special class" where hardcoded game-specific logic lives.
    /// To add a new operation:
    ///   1. Write a public static method matching OperationDelegate (returns Task&lt;bool&gt;).
    ///   2. Register its name in the <see cref="Operations"/> dictionary below.
    /// The name you register is the name you type into the profile — nothing else to wire.
    ///
    /// Operations are cancellable (throw/short-circuit on the token) and should always
    /// return true on success, false on failure so the workflow can retry or fail.
    /// </summary>
    public static class BotOperations
    {
        /// <summary>Signature of a custom operation. Returns true on success.</summary>
        public delegate Task<bool> OperationDelegate(OperationContext ctx, CancellationToken token);

        /// <summary>
        /// Name → implementation registry. Names are case-insensitive.
        /// Add your own methods here so the operation runner and the profile editor can find them.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, OperationDelegate> Operations =
            new Dictionary<string, OperationDelegate>(StringComparer.OrdinalIgnoreCase)
            {
                ["Wait"] = Wait,
                ["TeleportToCity"] = TeleportToCity,
                ["TalkToNpc"] = TalkToNpc,
                ["WaitForInCity"] = WaitForInCity,
                ["GoInsideCOT"] = GoInsideCOT,
            };

        /// <summary>All registered operation names, for the profile editor and validation.</summary>
        public static IReadOnlyList<string> KnownNames { get; } = new List<string>(Operations.Keys);

        /// <summary>True when an operation with this name is registered.</summary>
        public static bool IsKnown(string name) =>
            !string.IsNullOrWhiteSpace(name) && Operations.ContainsKey(name);

        // ───────────────────────── Example operations ─────────────────────────

        /// <summary>
        /// Waits a fixed 10 seconds. A simple demonstration of a hardcoded operation.
        /// Adjust the delay or copy this method to make a more specific wait.
        /// </summary>
        public static async Task<bool> Wait(OperationContext ctx, CancellationToken token)
        {
            ctx.Log("[Operation] Wait: sleeping 10 seconds.");
            await Task.Delay(10_000, token);
            return true;
        }

        /// <summary>
        /// Presses the profile's town-teleport key (default '6').
        /// </summary>
        public static async Task<bool> TeleportToCity(OperationContext ctx, CancellationToken token)
        {
            ctx.Log("[Operation] TeleportToCity: pressing teleport key.");
            ctx.FocusGameWindow();
            GameInput.PressKey(
                (byte)ctx.Profile.TeleportKey,
                (byte)ctx.Profile.TeleportScanCode);
            await Task.Delay(500, token);
            return true;
        }

        /// <summary>
        /// Opens the chat window (Enter), types a fixed greeting and sends it (Enter).
        /// Edit the message to match your NPC dialogue. Coordinates are not needed —
        /// this assumes the NPC interaction is proximity/key based.
        /// </summary>
        public static async Task<bool> TalkToNpc(OperationContext ctx, CancellationToken token)
        {
            ctx.Log("[Operation] TalkToNpc: opening chat and saying 'hi'.");
            ctx.FocusGameWindow();

            GameInput.PressKey(GameInput.VK_ENTER, GameInput.SCAN_ENTER);
            await Task.Delay(400, token);

            GameInput.TypeText("hi", ctx.Log);
            await Task.Delay(300, token);

            GameInput.PressKey(GameInput.VK_ENTER, GameInput.SCAN_ENTER);
            await Task.Delay(600, token);

            ctx.Log("[Operation] TalkToNpc: done.");
            return true;
        }

        /// <summary>
        /// Polls the game memory until the player is flagged as in the city.
        /// Fails after a 30 second timeout.
        /// </summary>
        public static async Task<bool> WaitForInCity(OperationContext ctx, CancellationToken token)
        {
            ctx.Log("[Operation] WaitForInCity: waiting until player is in the city...");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                if (ctx.Memory.GetIsInCity())
                {
                    ctx.Log("[Operation] WaitForInCity: in city.");
                    return true;
                }
                await Task.Delay(500, token);
            }
            ctx.Log("[Operation] WaitForInCity: timed out.");
            return false;
        }

        /// <summary>
        /// Enters the COT (Cave of Trials) by talking to its entry NPC:
        ///   1. Scan the game window for the NPC (same seller scan as the repot flow)
        ///      and right-click it.
        ///   2. Wait for the NPC popup dialog to open and verify it is open.
        ///   3. Click the "enter" option at calibrated (570, 565), wait 2 s, click again.
        ///   4. Wait 5 s for the teleport, then succeed only when the map has changed.
        /// </summary>
        public static async Task<bool> GoInsideCOT(OperationContext ctx, CancellationToken token)
        {
            ctx.Log("[Operation] GoInsideCOT: starting.");
            ctx.FocusGameWindow();

            // Remember the map we are on — success means this map changes.
            int previousMap = ctx.Memory.GetMapNumber();
            ctx.Log($"[Operation] GoInsideCOT: current map before entering: {previousMap}.");

            // 1. Scan for the NPC and right-click it (same as the repot seller NPC).
            if (!ctx.ItemSeller.ScanAndRightClickNpc())
            {
                ctx.Log("[Operation] GoInsideCOT: NPC not found after scanning. Aborting.");
                return false;
            }

            // 2. Wait for the popup to appear and verify it is open.
            await Task.Delay(1000, token);
            if (!ctx.Memory.IsShopOpen())
            {
                ctx.Log("[Operation] GoInsideCOT: NPC popup did not open. Aborting.");
                return false;
            }
            ctx.Log("[Operation] GoInsideCOT: NPC popup is open.");

            // 3. Click the "enter" option twice (calibrated 570, 565), 2 s apart.
            ClickAtCalibrated(ctx, CotEnterClickX, CotEnterClickY);
            await Task.Delay(2000, token);
            ClickAtCalibrated(ctx, CotEnterClickX, CotEnterClickY);

            // 4. Wait for the teleport, then verify the map actually changed.
            await Task.Delay(5000, token);
            int currentMap = ctx.Memory.GetMapNumber();
            ctx.Log($"[Operation] GoInsideCOT: map after entering: {currentMap}.");

            if (currentMap != previousMap)
            {
                ctx.Log("[Operation] GoInsideCOT: map changed — success.");
                return true;
            }

            ctx.Log("[Operation] GoInsideCOT: map did not change. Failed.");
            return false;
        }

        // ─────────────────── COT calibrated-click helpers ───────────────────

        /// <summary>The "enter" button position in the COT NPC dialog, in calibrated
        /// absolute coordinates (reference window origin 445,105).</summary>
        private const int CotEnterClickX = 570;
        private const int CotEnterClickY = 565;

        /// <summary>Reference window origin at which the calibrated coordinates were measured.</summary>
        private const int RefWindowX = 445;
        private const int RefWindowY = 105;

        /// <summary>
        /// Converts a calibrated absolute screen position (measured at reference window
        /// 445,105) into current screen coordinates using the live game-window rect.
        /// Falls back to the calibrated position when the window cannot be found.
        /// </summary>
        private static (int X, int Y) CalibratedToScreen(int calibratedX, int calibratedY)
        {
            nint hwnd = FindWindow(null, "Legend of Ares");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Ares");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Nostalgia");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Epic Of Ares Client");
            if (hwnd != nint.Zero && GetWindowRect(hwnd, out RECT rect))
            {
                int screenX = rect.Left + (calibratedX - RefWindowX);
                int screenY = rect.Top + (calibratedY - RefWindowY);
                return (screenX, screenY);
            }
            return (calibratedX, calibratedY);
        }

        private static void ClickAtCalibrated(OperationContext ctx, int calibratedX, int calibratedY, int delay = 200)
        {
            var (screenX, screenY) = CalibratedToScreen(calibratedX, calibratedY);
            ctx.Log($"[GoInsideCOT] Clicking calibrated ({calibratedX},{calibratedY}) → screen ({screenX},{screenY}).");
            MouseOperations.MoveAndLeftClickAbsolute(screenX, screenY, delay);
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern nint FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}