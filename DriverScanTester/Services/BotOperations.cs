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
                ["EtanaConditionalMove"] = EtanaConditionalMove,
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

        // ───────────────────── EtanaConditionalMove ─────────────────────

        /// <summary>Etana map id, shared with the repot/vendor logic (map 35).</summary>
        internal const int EtanaConditionalMoveMapId = ItemSellerService.MapEtana;

        /// <summary>Diagnostic route label of the temporary navigation leg.</summary>
        internal const string EtanaConditionalMoveRouteLabel = "EtanaConditionalMove";

        /// <summary>
        /// Independent per-axis arrival tolerance (tiles) of the conditional move. This is a
        /// rectangular check (|dx| &lt;= 1 AND |dy| &lt;= 1), not a Euclidean distance check.
        /// </summary>
        internal const float EtanaConditionalMoveTolerance = 1f;

        /// <summary>
        /// Upper bound of navigation legs for one destination. The destination is selected
        /// once; a further leg is only started when the existing navigation reported the
        /// temporary waypoint as finished while the real position is still outside the ±1
        /// tolerance (so a plain re-run can never reselect the second destination).
        /// </summary>
        internal const int EtanaConditionalMoveMaxNavigationLegs = 3;

        /// <summary>
        /// 0 = idle, 1 = an EtanaConditionalMove is executing. This action can itself be
        /// configured as the Unstuck/StuckRecovery action; this guard makes sure its own
        /// movement can never start the action again (no recursive unstuck execution).
        /// </summary>
        private static int _etanaConditionalMoveActive;

        /// <summary>
        /// Etana-only conditional move used as a custom operation (selectable as a normal
        /// flow operation and as a waypoint Unstuck/StuckRecovery operation):
        ///
        ///   1. Read one coherent map + X/Y snapshot and refuse to act on unreadable data.
        ///   2. Skip (no movement) unless the player is on Etana (map 35).
        ///   3. Select the destination exactly once from the strict coordinate condition.
        ///   4. Navigate to it with the existing waypoint movement
        ///      (<see cref="PathRunnerService"/> → <see cref="MovementSystem"/>) using the
        ///      existing DirectCamera recovery steering mode.
        ///
        /// The existing stuck detection and the standard unstuck (ReverseDiagonalRecovery)
        /// of that movement system stay fully active: the temporary waypoint is a Default
        /// recovery waypoint, so a stuck during this action always dispatches the standard
        /// unstuck routine and never a configurable recovery operation (which could re-enter
        /// this action). Because the waypoint list is created once and reused, a successful
        /// unstuck only resumes the movement toward the same, unchanged destination.
        /// </summary>
        public static Task<bool> EtanaConditionalMove(OperationContext ctx, CancellationToken token)
            => EtanaConditionalMoveAsync(ctx, token, navigationOverride: null);

        /// <summary>
        /// Execution core. <paramref name="navigationOverride"/> exists so tests can run the
        /// complete decision/recovery/resume logic against a deterministic navigation leg
        /// without touching a live game. Production always passes null and therefore runs the
        /// existing host-provided <see cref="OperationContext.PathRunner"/>.
        /// </summary>
        internal static async Task<bool> EtanaConditionalMoveAsync(
            OperationContext ctx,
            CancellationToken token,
            Func<List<Waypoint>, CancellationToken, Task<bool>>? navigationOverride)
        {
            if (Interlocked.CompareExchange(ref _etanaConditionalMoveActive, 1, 0) != 0)
            {
                ctx.Log("[EtanaConditionalMove] Re-entrant call rejected — this action is already running. " +
                        "No new movement was started (the active instance keeps its destination and its standard unstuck).");
                return false;
            }

            try
            {
                return await EtanaConditionalMoveCoreAsync(ctx, token, navigationOverride);
            }
            finally
            {
                Volatile.Write(ref _etanaConditionalMoveActive, 0);
            }
        }

        private static async Task<bool> EtanaConditionalMoveCoreAsync(
            OperationContext ctx,
            CancellationToken token,
            Func<List<Waypoint>, CancellationToken, Task<bool>>? navigationOverride)
        {
            ctx.Log("[EtanaConditionalMove] Started.");
            token.ThrowIfCancellationRequested();

            // ── 1. Validated map + position reading (single coherent snapshot) ──
            PlayerStateSnapshot snapshot = await ReadValidatedEtanaConditionalMoveStateAsync(ctx, token);
            if (!snapshot.HasCoherentState)
            {
                ctx.Log("[EtanaConditionalMove] Invalid map/position reading — no destination decision is made " +
                        "and no movement is started. The existing position/failure handling applies. " + snapshot.Describe());
                return false;
            }

            int map = snapshot.Map;
            float currentX = snapshot.X;
            float currentY = snapshot.Y;
            ctx.Log($"[EtanaConditionalMove] Current Map={map}, position=({currentX:F0},{currentY:F0}).");

            // ── 2. Etana-only guard: never navigate to Etana coordinates elsewhere ──
            if (map != EtanaConditionalMoveMapId)
            {
                ctx.Log($"[EtanaConditionalMove] Skipped (not applicable) — map {map} is not Etana " +
                        $"({EtanaConditionalMoveMapId}); no movement started.");
                return false;
            }

            // ── 3. Destination selection: strictly once, at the beginning ──
            bool inCondition = IsEtanaConditionalMoveConditionMet(currentX, currentY);
            (float targetX, float targetY) = SelectEtanaConditionalMoveDestination(currentX, currentY);
            ctx.Log($"[EtanaConditionalMove] Condition (267<X<270 && 190<Y<194) on ({currentX:F0},{currentY:F0}) " +
                    $"→ {inCondition}. Destination selected: ({targetX:F0},{targetY:F0}) — fixed for the whole action " +
                    "(including any internal unstuck).");

            if (IsEtanaConditionalMoveDestinationReached(currentX, currentY, targetX, targetY))
            {
                ctx.Log($"[EtanaConditionalMove] Already within ±{EtanaConditionalMoveTolerance:F0} of " +
                        $"({targetX:F0},{targetY:F0}) at ({currentX:F0},{currentY:F0}) — no movement needed. Result=Success.");
                return true;
            }

            // ── 4. Temporary navigation target (no saved path / configured route is touched) ──
            // Input goes to the game window, exactly like the other movement operations.
            ctx.FocusGameWindow();
            var waypoints = new List<Waypoint> { CreateEtanaConditionalMoveWaypoint(targetX, targetY) };
            float lastX = currentX;
            float lastY = currentY;

            for (int leg = 1; leg <= EtanaConditionalMoveMaxNavigationLegs; leg++)
            {
                token.ThrowIfCancellationRequested();
                ctx.Log($"[EtanaConditionalMove] Navigation started toward the retained destination " +
                        $"({targetX:F0},{targetY:F0}) — leg {leg}/{EtanaConditionalMoveMaxNavigationLegs}; " +
                        "the existing stuck detection and standard unstuck stay active.");

                bool completed = navigationOverride != null
                    ? await navigationOverride(waypoints, token)
                    : await RunEtanaConditionalMoveNavigationLegAsync(ctx, waypoints, token);

                // Stop All / Pause interruption / operation timeout must end the action here,
                // after the existing runner released its own inputs.
                token.ThrowIfCancellationRequested();

                snapshot = await ReadValidatedEtanaConditionalMoveStateAsync(ctx, token);
                if (!snapshot.HasCoherentState)
                {
                    ctx.Log($"[EtanaConditionalMove] Final map/position could not be read after navigation " +
                            $"(leg {leg}, completed={completed}); no success is reported. " + snapshot.Describe());
                    return false;
                }

                lastX = snapshot.X;
                lastY = snapshot.Y;
                ctx.Log($"[EtanaConditionalMove] Navigation leg {leg}/{EtanaConditionalMoveMaxNavigationLegs} finished: " +
                        $"completed={completed}, Map={snapshot.Map}, position=({lastX:F0},{lastY:F0}), " +
                        $"destination=({targetX:F0},{targetY:F0}) (retained).");

                // Teleport escalation / portal may have left Etana — never navigate
                // Etana coordinates on another map.
                if (snapshot.Map != EtanaConditionalMoveMapId)
                {
                    ctx.Log($"[EtanaConditionalMove] Map changed from Etana ({EtanaConditionalMoveMapId}) to " +
                            $"{snapshot.Map} — action terminated; no further movement on this map. Result=Failure.");
                    return false;
                }

                if (IsEtanaConditionalMoveDestinationReached(lastX, lastY, targetX, targetY))
                {
                    ctx.Log($"[EtanaConditionalMove] Destination reached — final position=({lastX:F0},{lastY:F0}) " +
                            $"is within ±{EtanaConditionalMoveTolerance:F0} of ({targetX:F0},{targetY:F0}). " +
                            $"Result=Success (leg {leg}/{EtanaConditionalMoveMaxNavigationLegs}).");
                    return true;
                }

                if (!completed)
                {
                    ctx.Log($"[EtanaConditionalMove] Navigation stopped without reaching the destination " +
                            $"(completed=False, position=({lastX:F0},{lastY:F0}), destination=({targetX:F0},{targetY:F0})). " +
                            $"{DescribeLegStop(ctx)} The existing navigation/recovery failure handling applies. Result=Failure.");
                    return false;
                }

                if (leg < EtanaConditionalMoveMaxNavigationLegs)
                {
                    ctx.Log($"[EtanaConditionalMove] Destination not reached yet — resuming navigation toward the " +
                            $"retained destination ({targetX:F0},{targetY:F0}) after the existing navigation/stuck handling.");
                }
            }

            ctx.Log($"[EtanaConditionalMove] Destination ({targetX:F0},{targetY:F0}) was not reached after " +
                    $"{EtanaConditionalMoveMaxNavigationLegs} navigation legs (final position=({lastX:F0},{lastY:F0})). " +
                    "Result=Failure.");
            return false;
        }

        /// <summary>
        /// Production navigation leg: runs the temporary waypoint list through the existing
        /// <see cref="PathRunnerService"/> (host-provided through the operation context, which
        /// is the auxiliary DirectCamera recovery runner when this action runs as a waypoint
        /// recovery operation). The Etana map invariant aborts the leg as soon as the player is
        /// confirmed on another map; cancel/stop/pause are the existing runner mechanisms.
        /// </summary>
        internal static async Task<bool> RunEtanaConditionalMoveNavigationLegAsync(
            OperationContext ctx,
            List<Waypoint> waypoints,
            CancellationToken token)
        {
            PathRunnerService runner = ctx.PathRunner;

            // Movement ownership: never create a second movement system on a runner that is
            // already navigating (e.g. the action selected as an On-Arrival operation of a
            // running route). The active route keeps its ownership untouched.
            if (runner.IsRunning)
            {
                ctx.Log("[EtanaConditionalMove] The host path runner is already navigating — refusing to start a " +
                        "competing movement system. Result=Failure.");
                return false;
            }

            // Pause must hold this leg in place, exactly like a route. The main runner
            // already carries the workflow's pause controller; the auxiliary recovery
            // runner receives it from the operation context.
            if (ctx.PauseController != null)
                runner.PauseController = ctx.PauseController;

            return await runner.RunPathAsync(
                waypoints,
                loop: false,
                token,
                expectedMap: EtanaConditionalMoveMapId,
                routeLabel: EtanaConditionalMoveRouteLabel,
                disableAllLoot: ctx.Profile.DisableAllLoot,
                ignoreAlliancePlayers: ctx.Profile.IgnoreAlliancePlayers,
                ignoreEmpirePlayers: ctx.Profile.IgnoreEmpirePlayers);
        }

        /// <summary>
        /// Diagnostic summary of the existing navigation/recovery termination state, so the
        /// action's failure log shows why the standard recovery policy stopped the leg.
        /// </summary>
        private static string DescribeLegStop(OperationContext ctx)
        {
            PathRunnerService runner = ctx.PathRunner;
            MovementSystem? movement = runner.CurrentMovement;
            if (movement == null)
                return $"PathRunner stopReason={runner.LastStopReason}.";

            return $"PathRunner stopReason={runner.LastStopReason}, " +
                   $"cityStuck={movement.IsCityStuckFatal}, " +
                   $"navigationFailure={movement.IsNavigationFailureEscalationRequested}, " +
                   $"reportAndGoBack={movement.IsReportAndGoBackRequested}, " +
                   $"zoneBlocked={movement.IsZoneBlocked}, " +
                   $"specialRecoveryActive={movement.IsWaypointSpecialRecoveryActive}.";
        }

        /// <summary>
        /// Builds the temporary navigation waypoint for one destination:
        /// Exact precision (tightest existing reach threshold), OnlyMove (no combat / no
        /// loot obligation during the recovery movement), DirectCamera (the existing
        /// recovery steering mode) and <see cref="WaypointStuckRecoveryType.Default"/>
        /// so a stuck inside this action always uses the standard unstuck instead of a
        /// configurable recovery operation (recursion guard).
        /// </summary>
        private static Waypoint CreateEtanaConditionalMoveWaypoint(float targetX, float targetY)
            => new Waypoint(
                targetX,
                targetY,
                MovementPrecision.Exact,
                BotMode.OnlyMove,
                zoneRestriction: ZoneRestriction.Both,
                stuckRecoveryType: WaypointStuckRecoveryType.Default,
                steeringMode: MovementSteeringMode.DirectCamera);

        /// <summary>
        /// Strict coordinate condition that selects the first destination:
        /// <c>currentX &gt; 267 &amp;&amp; currentX &lt; 270 &amp;&amp; currentY &gt; 190 &amp;&amp; currentY &lt; 194</c>.
        /// </summary>
        internal static bool IsEtanaConditionalMoveConditionMet(float currentX, float currentY)
            => currentX > 267 && currentX < 270 && currentY > 190 && currentY < 194;

        /// <summary>
        /// The single destination decision: (274,191) inside the strict condition,
        /// (263,199) everywhere else.
        /// </summary>
        internal static (float X, float Y) SelectEtanaConditionalMoveDestination(float currentX, float currentY)
            => IsEtanaConditionalMoveConditionMet(currentX, currentY)
                ? (274f, 191f)
                : (263f, 199f);

        /// <summary>
        /// Rectangular arrival check: both axes must be within ±1 simultaneously
        /// (|dx| &lt;= 1 AND |dy| &lt;= 1) — never a Euclidean radius.
        /// </summary>
        internal static bool IsEtanaConditionalMoveDestinationReached(
            float currentX, float currentY, float targetX, float targetY)
            => Math.Abs(currentX - targetX) <= EtanaConditionalMoveTolerance &&
               Math.Abs(currentY - targetY) <= EtanaConditionalMoveTolerance;

        /// <summary>
        /// Reads one coherent map + X/Y snapshot through the existing memory reader, with a
        /// small bounded retry so a single transient failed read is not treated as a position.
        /// No coordinate is ever fabricated or reused from an earlier read.
        /// </summary>
        private static async Task<PlayerStateSnapshot> ReadValidatedEtanaConditionalMoveStateAsync(
            OperationContext ctx, CancellationToken token)
        {
            PlayerStateSnapshot snapshot = ctx.Memory.GetPlayerStateSnapshot();
            for (int attempt = 1; attempt < 3 && !snapshot.HasCoherentState; attempt++)
            {
                ctx.Log($"[EtanaConditionalMove] Map/position read attempt {attempt}/3 was not coherent " +
                        $"({snapshot.Describe()}); re-reading in 150 ms.");
                await Task.Delay(150, token);
                snapshot = ctx.Memory.GetPlayerStateSnapshot();
            }

            return snapshot;
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
