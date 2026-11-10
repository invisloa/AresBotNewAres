using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;
using DriverScanTester.Utils;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Built-in custom bot operations that can be referenced by NAME from a profile
    /// flow step (<see cref="Models.BotFlowStep.OperationName"/>).
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
                ["Enter_COT"] = EnterCOT,
                ["ScanForNpc"] = ScanForNpc,
                ["EtanaRepotUnstuck"] = EtanaRepotUnstuck,
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
        /// Presses the profile's town-teleport key (default '8') and saves a screenshot
        /// to Screenshots/TownPortals right after the scroll is triggered (before the
        /// town loads).
        /// </summary>
        public static async Task<bool> TeleportToCity(OperationContext ctx, CancellationToken token)
        {
            ctx.Log("[Operation] TeleportToCity: pressing teleport key.");
            ctx.FocusGameWindow();

            // Capture the pre-teleport position and mark the transition BEFORE the key
            // is sent. The destination coordinates can remain stale until the game has
            // loaded, so the workflow must validate them before comparing positions.
            // A town teleport can land on the SAME map, so the transition is marked as a
            // MANDATORY W refresh: PositionSync presses W, re-reads the settled position
            // and only then reports READY (a changed coordinate alone is not accepted).
            ctx.PositionSync?.MarkTransitionImminent("Operation TeleportToCity", requireWRefresh: true);

            GameInput.PressKey(
                (byte)ctx.Profile.TeleportKey,
                (byte)ctx.Profile.TeleportScanCode);

            // Screenshot immediately after the teleport scroll is triggered — before the
            // town starts loading — so the image shows the state at the moment of teleport.
            ScreenshotService.CaptureFullScreen(
                ScreenshotService.TownPortalsFolder, ctx.Log, "[Operation] TeleportToCity:");

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
        /// Every failed attempt captures one diagnostic screenshot at the failure point,
        /// before the operation returns so the runner can retry.
        /// </summary>
        public static Task<bool> GoInsideCOT(OperationContext ctx, CancellationToken token)
        {
            return GoInsideCOTCoreAsync(
                ctx,
                () => ctx.ItemSeller.ScanAndRightClickNpc(),
                (delayMs, cancellationToken) => Task.Delay(delayMs, cancellationToken),
                (calibratedX, calibratedY, logPrefix) =>
                    ClickAtCalibrated(ctx, calibratedX, calibratedY, logPrefix: logPrefix),
                token);
        }

        /// <summary>
        /// Testable GoInsideCOT core. The public operation supplies the real NPC scan,
        /// game delays and calibrated clicks; tests replace only those side effects so
        /// every failure/screenshot path can be verified deterministically.
        /// </summary>
        internal static async Task<bool> GoInsideCOTCoreAsync(
            OperationContext ctx,
            Func<bool> scanAndRightClickNpc,
            Func<int, CancellationToken, Task> delayAsync,
            Action<int, int, string> clickAtCalibrated,
            CancellationToken token)
        {
            ctx.Log("[Operation] GoInsideCOT: starting.");
            ctx.FocusGameWindow();

            // Remember the map we are on — success means this map changes.
            int previousMap = ctx.Memory.GetMapNumber();
            ctx.Log($"[Operation] GoInsideCOT: current map before entering: {previousMap}.");

            // 1. Scan for the NPC and right-click it (same as the repot seller NPC).
            if (!scanAndRightClickNpc())
            {
                ctx.Log("[Operation] GoInsideCOT: NPC not found after scanning. Aborting.");
                return FailGoInsideCOT(ctx, "NpcNotFound", npcDetected: false, clickPosition: null);
            }

            // 2. Wait for the popup to appear and verify it is open.
            await delayAsync(1000, token);
            bool dialogOpen = ctx.Memory.IsDialogOpen();
            if (!dialogOpen)
            {
                ctx.Log("[Operation] GoInsideCOT: NPC popup did not open. Aborting.");
                return FailGoInsideCOT(
                    ctx,
                    "NpcDialogNotOpened",
                    npcDetected: true,
                    clickPosition: ctx.ItemSeller.LastNpcRightClickScreenPosition,
                    isDialogOpen: false);
            }
            ctx.Log("[Operation] GoInsideCOT: NPC popup is open.");

            // 3. Click the "enter" option twice (calibrated 570, 565), 2 s apart.
            // Mark the impending map transition first so the pre-entry position is the
            // freshness baseline — not an older snapshot from before walking to the NPC.
            ctx.PositionSync?.MarkTransitionImminent("GoInsideCOT");
            clickAtCalibrated(CotEnterClickX, CotEnterClickY, "GoInsideCOT");
            await delayAsync(2000, token);
            clickAtCalibrated(CotEnterClickX, CotEnterClickY, "GoInsideCOT");

            // 4. Wait for the teleport, then verify the map actually changed.
            await delayAsync(5000, token);
            int currentMap = ctx.Memory.GetMapNumber();
            ctx.Log($"[Operation] GoInsideCOT: map after entering: {currentMap}.");

            if (currentMap != previousMap)
            {
                ctx.Log("[Operation] GoInsideCOT: map changed — success.");

                // The coordinator's position sync (and its bounded W refresh) runs right
                // after this operation returns. Set the COT exp path's configured start
                // camera now so that W is pressed at the configured angle instead of the
                // seller-scan yaw.
                ApplyCotExpStartCamera(ctx, currentMap);
                return true;
            }

            ctx.Log("[Operation] GoInsideCOT: map did not change. Failed.");
            return FailGoInsideCOT(
                ctx,
                "MapDidNotChange",
                npcDetected: true,
                clickPosition: ctx.ItemSeller.LastNpcRightClickScreenPosition);
        }

        /// <summary>
        /// Applies the start camera configured on the COT exp path's first waypoint after
        /// the map changed. Runs inside GoInsideCOT, before the coordinator's PositionSync
        /// W refresh, so the refresh W goes down at the configured yaw.
        /// </summary>
        private static void ApplyCotExpStartCamera(OperationContext ctx, int mapAfterEntering)
        {
            try
            {
                BotRouteStep? route = FindCotExpRoute(ctx.Profile, mapAfterEntering);
                if (route == null || string.IsNullOrWhiteSpace(route.PathFile))
                {
                    ctx.Log("[StartCam] GoInsideCOT: no COT exp route configured — start camera not applied.");
                    return;
                }

                int? angleBits = new SavedPathLoader(ctx.Log)
                    .LoadFirstPositionalStartCameraAngleBits(route.PathFile);
                if (!angleBits.HasValue)
                {
                    ctx.Log($"[StartCam] GoInsideCOT: route '{route.PathFile}' first waypoint has no StartCam — camera left unchanged.");
                    return;
                }

                string before = ctx.Memory.TryGetCameraAngleBits(out int beforeBits)
                    ? CameraAngleValue.Describe(beforeBits)
                    : "unreadable";
                bool applied = ctx.WCameraController != null
                    ? ctx.WCameraController.TrySetCameraAngleBits(angleBits.Value)
                    : ctx.Memory.TrySetCameraAngleBits(angleBits.Value);
                string description = CameraAngleValue.Describe(angleBits);

                if (applied)
                {
                    ctx.Log($"[StartCam] GoInsideCOT: COT exp route '{route.PathFile}' start camera {description} applied after entering map {mapAfterEntering} and BEFORE the position-refresh W (was {before}).");
                }
                else
                {
                    ctx.Log($"[StartCam] GoInsideCOT: COT exp route '{route.PathFile}' start camera {description} could NOT be applied (was {before}); the position-refresh W would run on the old camera.");
                }
            }
            catch (Exception ex)
            {
                ctx.Log($"[StartCam] GoInsideCOT: could not apply the COT exp start camera: {ex.Message}");
            }
        }

        /// <summary>
        /// Finds the profile's COT exp route: an ExpLoop route whose starting map matches
        /// the map the bot just entered. Falls back to the first ExpLoop route, then to a
        /// Path route for that map, so a profile without map metadata still gets its
        /// configured camera.
        /// </summary>
        private static BotRouteStep? FindCotExpRoute(BotProfile? profile, int mapAfterEntering)
        {
            if (profile?.FlowSteps == null)
                return null;

            BotRouteStep? mapMatch = null;
            BotRouteStep? firstExpLoop = null;
            BotRouteStep? firstPath = null;

            foreach (BotFlowStep? step in profile.FlowSteps)
            {
                if (step == null || step.Type is not (BotFlowStepType.ExpLoop or BotFlowStepType.Path))
                    continue;

                List<BotRouteStep>? routes = BotStartupActionSelector.GetRoutePool(step);
                if (routes == null)
                    continue;

                bool isExpLoop = step.Type == BotFlowStepType.ExpLoop;
                foreach (BotRouteStep? route in routes)
                {
                    if (route == null || string.IsNullOrWhiteSpace(route.PathFile))
                        continue;

                    int routeMap = route.StartMapNumber > 0 ? route.StartMapNumber : step.StartMapNumber;

                    if (routeMap == mapAfterEntering && isExpLoop)
                        return route;
                    if (routeMap == mapAfterEntering && mapMatch == null)
                        mapMatch = route;
                    if (isExpLoop && firstExpLoop == null)
                        firstExpLoop = route;
                    if (!isExpLoop && firstPath == null)
                        firstPath = route;
                }
            }

            return mapMatch ?? firstExpLoop ?? firstPath;
        }

        /// <summary>
        /// Handles one failed GoInsideCOT attempt: logs the failure context (map, position,
        /// NPC detection, click position, dialog state) and captures exactly one diagnostic
        /// screenshot before returning false to the retry loop. Never throws and never
        /// changes the operation outcome.
        /// </summary>
        private static bool FailGoInsideCOT(
            OperationContext ctx,
            string reason,
            bool npcDetected,
            (int X, int Y)? clickPosition,
            bool? isDialogOpen = null)
        {
            int attemptNumber = Math.Max(1, ctx.CurrentOperationAttempt);
            int maxAttempts = Math.Max(attemptNumber, ctx.CurrentOperationMaxAttempts);

            var (positionX, positionY, positionRead) = ctx.Memory.GetPlayerPosition();
            int map = ctx.Memory.GetMapNumber();
            bool dialogOpen = isDialogOpen ?? ctx.Memory.IsDialogOpen();

            string positionText = positionRead
                ? $"({positionX:0},{positionY:0})"
                : "unknown";
            string clickText = clickPosition.HasValue
                ? $"({clickPosition.Value.X},{clickPosition.Value.Y})"
                : "unknown";

            ctx.Log($"[GoInsideCOT] Attempt {attemptNumber}/{maxAttempts} FAILED. Reason={reason}");
            ctx.Log($"[GoInsideCOT] Map={map} Position={positionText} " +
                    $"NpcDetected={npcDetected} ClickPosition={clickText} IsDialogOpen={dialogOpen}");

            CaptureGoInsideCOTFailureScreenshot(ctx, reason, attemptNumber);
            return false;
        }

        /// <summary>
        /// Captures the diagnostic screenshot for a failed GoInsideCOT attempt. A capture
        /// error is logged with its exception details and swallowed: diagnostics must never
        /// break the existing failure/retry workflow. Exactly one capture is attempted.
        /// </summary>
        private static void CaptureGoInsideCOTFailureScreenshot(
            OperationContext ctx,
            string reason,
            int attemptNumber)
        {
            const string operationName = "GoInsideCOT";
            const string logPrefix = "[GoInsideCOT]";

            try
            {
                Func<string, string, int, string?>? capture = ctx.DiagnosticScreenshotCapture;
                if (capture == null)
                {
                    ScreenshotService.CaptureOperationFailureScreenshot(
                        operationName, reason, attemptNumber, ctx.Log, logPrefix);
                    return;
                }

                string? path = capture(operationName, reason, attemptNumber);
                if (string.IsNullOrWhiteSpace(path))
                {
                    ctx.Log($"{logPrefix} Diagnostic screenshot failed: capture returned no path.");
                    return;
                }

                ctx.Log($"{logPrefix} Diagnostic screenshot saved: {path}");
            }
            catch (Exception ex)
            {
                ctx.Log($"{logPrefix} Diagnostic screenshot failed: {ex}");
            }
        }

        /// <summary>
        /// Enters the COT (Cave of Trials) by clicking the "enter" option in its
        /// entry NPC dialog:
        ///   1. Scan the game window for the NPC — exactly ONE full sweep (same
        ///      mouseover scan as the repot flow). No NPC found → return false
        ///      (the workflow then repots and starts the flow all over).
        ///   2. Wait 200 ms, then verify the NPC dialog opened (S_IsDialogOpen1,
        ///      == 1 counts as open).
        ///   3. Move the mouse to the "enter" option at calibrated (570, 565)
        ///      (default window position) and left-click it.
        ///   4. Wait 50 ms, check the dialog is still open, then click the same
        ///      position again.
        ///   5. Wait 2 s for the teleport, then succeed only when the map changed.
        /// </summary>
        public static async Task<bool> EnterCOT(OperationContext ctx, CancellationToken token)
        {
            ctx.Log("[Operation] Enter_COT: starting.");
            ctx.FocusGameWindow();

            // Remember the map we are on — success means this map changes.
            int previousMap = ctx.Memory.GetMapNumber();
            ctx.Log($"[Operation] Enter_COT: current map before entering: {previousMap}.");

            // 1. Scan for the NPC and right-click it — one full scan only.
            //    If the NPC is not found, fail so the workflow repots and starts over.
            if (!ctx.ItemSeller.ScanAndRightClickNpc(maxFullScans: 1))
            {
                ctx.Log("[Operation] Enter_COT: NPC not found after one full scan. Aborting.");
                return false;
            }

            // 2. Wait 200 ms for the popup to appear, then verify it is open.
            await Task.Delay(200, token);
            if (!ctx.Memory.IsDialogOpen())
            {
                ctx.Log("[Operation] Enter_COT: NPC dialog did not open. Aborting.");
                return false;
            }
            ctx.Log("[Operation] Enter_COT: NPC dialog is open.");

            // 3. Move the mouse to the "enter" option (calibrated 570, 565) and left-click it.
            // Mark the impending map transition first so the pre-entry position is the
            // freshness baseline for post-transition position validation.
            ctx.PositionSync?.MarkTransitionImminent("Enter_COT");
            ClickAtCalibrated(ctx, CotEnterClickX, CotEnterClickY, logPrefix: "Enter_COT");

            // 4. Wait 50 ms, confirm the dialog is still open, then click again.
            await Task.Delay(50, token);
            if (ctx.Memory.IsDialogOpen())
            {
                ctx.Log("[Operation] Enter_COT: NPC dialog still open — clicking the option again.");
                ClickAtCalibrated(ctx, CotEnterClickX, CotEnterClickY, logPrefix: "Enter_COT");
            }
            else
            {
                ctx.Log("[Operation] Enter_COT: NPC dialog closed after the first click — option already activated.");
            }

            // 5. Wait 2 s for the teleport, then verify the map actually changed.
            await Task.Delay(2000, token);
            int currentMap = ctx.Memory.GetMapNumber();
            ctx.Log($"[Operation] Enter_COT: map after entering: {currentMap}.");

            if (currentMap != previousMap)
            {
                ctx.Log("[Operation] Enter_COT: map changed — success.");
                return true;
            }

            ctx.Log("[Operation] Enter_COT: map did not change. Failed.");
            return false;
        }

        /// <summary>
        /// DIAGNOSTIC NPC SCAN — runs the PRODUCTION NPC scan once, exactly as the repot
        /// seller flow does, including the automatic MouseOver NPC calibration:
        ///   1. fast center-outward scan with the active
        ///      <see cref="ItemSellerService.SellerPointedValue"/>,
        ///   2. when that finds nothing: slow discovery (stable memory reads while the cursor
        ///      moves) + right-click/dialogue validation of each candidate, then the confirmed
        ///      value becomes the active one,
        ///   3. logs the active value before/after — a change means it was calibrated.
        ///
        /// It is meant for the Tools tab ("Test Operation — diagnostic tool"): the log shows
        /// the '[NPC Scan]' and '[NPC Calibration]' stages, so NPC detection and calibration
        /// can be verified without starting a workflow. A dialogue opened by the test is
        /// closed again with Escape so the game is left in the pre-test state.
        ///
        /// The scan (production and diagnostic alike) sweeps the
        /// <see cref="ItemSellerService.SellerScanStepPx"/> grid: 54 px, i.e. ~1/10 of the
        /// points the former 17 px grid visited per sweep. That is ~10x faster but cannot
        /// guarantee it hits a small hover zone, so a negative result does not prove that the
        /// active value is wrong.
        /// </summary>
        public static async Task<bool> ScanForNpc(OperationContext ctx, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ctx.FocusGameWindow();

            int valueBefore = ItemSellerService.SellerPointedValue;
            bool shopOpenBefore = ctx.Memory.IsShopOpen();
            bool dialogOpenBefore = ctx.Memory.IsDialogOpen();

            ctx.Log($"[Operation] ScanForNpc: starting the production NPC scan (step={ItemSellerService.SellerScanStepPx}px ≈ 1/10 of the former 17 px scan points; active MouseOver NPC value {MouseOverValueDiscovery.Describe(valueBefore)}, automatic calibration enabled). Switching the game window away stops it (the same stop mechanism the workflow uses).");

            // ScanAndRightClickNpc is a blocking, mouse-driven routine (fast scan + slow
            // discovery). Run it off the calling (UI) thread so the log and the Tools tab
            // stay responsive while it sweeps the game window.
            bool found = await Task.Run(
                () => ctx.ItemSeller.ScanAndRightClickNpc(),
                token);

            int valueAfter = ItemSellerService.SellerPointedValue;
            bool calibrated = valueAfter != valueBefore;
            bool dialogOpenAfter = ctx.Memory.IsDialogOpen();

            if (shopOpenBefore)
            {
                ctx.Log($"[Operation] ScanForNpc: a shop/NPC window was already open before the test, so the scan was skipped (same behaviour as the workflow). Active MouseOver NPC value: {MouseOverValueDiscovery.Describe(valueAfter)}.");
                return true;
            }

            if (found)
            {
                ctx.Log(calibrated
                    ? $"[Operation] ScanForNpc: NPC found and right-clicked. IsDialogOpen={dialogOpenAfter}. MouseOver NPC value CALIBRATED: {MouseOverValueDiscovery.Describe(valueBefore)} -> {MouseOverValueDiscovery.Describe(valueAfter)}."
                    : $"[Operation] ScanForNpc: NPC found and right-clicked. IsDialogOpen={dialogOpenAfter}. Active MouseOver NPC value unchanged: {MouseOverValueDiscovery.Describe(valueAfter)}.");
            }
            else
            {
                ctx.Log($"[Operation] ScanForNpc: NPC NOT found with the active value and calibration did not confirm a new one. Active MouseOver NPC value: {MouseOverValueDiscovery.Describe(valueAfter)}. See the '[NPC Scan]' and '[NPC Calibration]' lines above for the failure details.");
            }

            // Leave the game as it was: close ONLY the dialogue this test opened (Escape —
            // the existing UI mechanism; never a click, which could move the character).
            if (!dialogOpenBefore && dialogOpenAfter && !ctx.Memory.IsShopOpen())
            {
                ctx.Log("[Operation] ScanForNpc: closing the NPC dialogue opened by the test (Escape).");
                GameInput.PressKey(GameInput.VK_ESCAPE, GameInput.SCAN_ESCAPE);
            }

            return found;
        }

        /// <summary>
        /// Hardcoded camera yaw captured LIVE at the Etana teleport landing spot
        /// (player 214,213, map 35, in city) on 2026-09-19: raw 0x3FC96E22.
        /// ~1.5736735 rad / ~90.16 deg — facing East toward the repot path.
        /// Written as raw bits via <see cref="GameMemoryService.WriteCameraAngleInt"/>
        /// so the exact captured facing is restored bit-for-bit.
        /// </summary>
        private const int EtanaRepotCameraAngleBits = 1070165538; // 0x3FC96E22

        /// <summary>
        /// Etana repot unstuck for the post-teleport stale-coordinates bug:
        /// right after the teleport the game does not refresh the player position
        /// memory, so no bearing can be computed. Etana has only one teleport landing
        /// spot, so the fix is to point the camera at the hardcoded captured yaw
        /// (the direction the character faces at the landing) and simply walk
        /// forward to force the position to refresh:
        ///   1. Set the camera to the hardcoded captured angle.
        ///   2. Hold W (walk forward) for 2 seconds.
        ///   3. Release W and return (always true unless cancelled).
        /// </summary>
        public static async Task<bool> EtanaRepotUnstuck(OperationContext ctx, CancellationToken token)
        {
            ctx.Log("[Operation] EtanaRepotUnstuck: starting (hardcoded camera + 2s walk). ");
            ctx.FocusGameWindow();

            // 1. Point the camera at the hardcoded captured value (exact bits). Use the
            //    shared camera lease so this operation cannot alter yaw during an NPC scan.
            WKeyCameraController? cameraController = ctx.WCameraController;
            if (cameraController == null)
            {
                ctx.Log("[Operation] EtanaRepotUnstuck: W input blocked because no shared W-camera controller is configured.");
                return false;
            }

            ctx.Log($"[Operation] EtanaRepotUnstuck: setting camera to hardcoded 0x{EtanaRepotCameraAngleBits:X8}.");
            if (!cameraController.TrySetCameraAngleBits(EtanaRepotCameraAngleBits))
            {
                ctx.Log("[Operation] EtanaRepotUnstuck: camera yaw write blocked or could not be verified.");
                return false;
            }
            await Task.Delay(100, token);

            // 2. Walk forward for 2 seconds. W is released in the finally block so
            //    the key can never get stuck down on cancel/timeout.
            ctx.Log("[Operation] EtanaRepotUnstuck: walking forward for 2s (W down). ");
            WKeyCameraOwner cameraOwner = cameraController.CreateOwner(
                "EtanaRepotUnstuck", ctx.SendWKeyEvent);
            if (!cameraController.TryAcquireW(cameraOwner))
            {
                ctx.Log("[Operation] EtanaRepotUnstuck: W input blocked because the movement camera lock was not verified or another operation owns the camera.");
                return false;
            }

            bool releaseResolved = true;
            try
            {
                // The shared controller owns the physical W transition as well as the
                // movement camera lock; this operation only owns its logical W lease.
                await Task.Delay(2400, token);
            }
            finally
            {
                cameraController.ReleaseW(cameraOwner);
                releaseResolved = !cameraController.HasUnresolvedPhysicalWRelease;
            }

            if (!releaseResolved)
            {
                ctx.Log("[Operation] EtanaRepotUnstuck: physical W-up is unresolved; " +
                        "the operation cannot continue with an uncertain W state.");
                return false;
            }

            ctx.Log("[Operation] EtanaRepotUnstuck: done (W-up callback completed; game-side release not independently verified). ");
            return true;
        }

        // ─────────────────── COT calibrated-click helpers ───────────────────

        /// <summary>The "enter" button position in the COT NPC dialog, in calibrated
        /// absolute coordinates (reference window origin 445,105). Shared by
        /// <see cref="GoInsideCOT"/> and <see cref="EnterCOT"/>.</summary>
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

        private static void ClickAtCalibrated(OperationContext ctx, int calibratedX, int calibratedY, int delay = 200, string logPrefix = "GoInsideCOT")
        {
            var (screenX, screenY) = CalibratedToScreen(calibratedX, calibratedY);
            ctx.Log($"[{logPrefix}] Clicking calibrated ({calibratedX},{calibratedY}) → screen ({screenX},{screenY}).");
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
