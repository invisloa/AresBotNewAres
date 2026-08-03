using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Central coordinator of the bot route workflow:
    ///   1. City → Repot (route stage 1)
    ///      [repot operation: sell items, buy potions]
    ///   2. Travel Routes: Repot → EXP (ordered chain; each leg completes by final
    ///      waypoint or by reaching its expected destination map, e.g. through portals)
    ///   3. Exp Loop (loops until repot is needed)
    ///
    /// MovementSystem is used ONLY for movement. All phase decisions are made here.
    /// A workflow requires a non-null valid BotProfile and a non-null active HuntDefinition;
    /// there are no hardcoded fallback paths.
    /// </summary>
    public class BotWorkflowCoordinator
    {
        #region Keyboard DLL

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        private const int KEYEVENTF_KEYUP = BotConstants.Keyboard.KeyEventKeyUp;

        #endregion

        private readonly GameMemoryService _memoryService;
        private readonly RepotSystem _repotSystem;
        private readonly RepotDetectorService _repotDetector;
        private readonly SavedPathLoader _pathLoader;
        private readonly PathRunnerService _pathRunner;
        private readonly Action<string> _log;
        private readonly Action _focusGameWindow;

        private readonly BotProfile _profile;
        private readonly HuntDefinition _activeHunt;

        private CancellationTokenSource? _cts;
        private bool _isRunning;
        private BotPhase _currentPhase = BotPhase.Idle;

        private int _teleportRetryCount;

        /// <summary>Current phase of the bot workflow.</summary>
        public BotPhase CurrentPhase
        {
            get => _currentPhase;
            private set
            {
                if (_currentPhase == value) return;
                _currentPhase = value;
                _log($"[Coordinator] Phase changed to: {value}");
                OnPhaseChanged?.Invoke(value.ToString());
            }
        }

        /// <summary>Fires whenever CurrentPhase changes.</summary>
        public Action<string>? OnPhaseChanged { get; set; }

        /// <summary>Fires when the workflow stops.</summary>
        public Action? OnStopped { get; set; }

        /// <summary>Whether the coordinator is running.</summary>
        public bool IsRunning => _isRunning;

        /// <summary>The active profile.</summary>
        public BotProfile ActiveProfile => _profile;

        // ======================== Constructor ========================

        /// <summary>
        /// Profile-based constructor. The activeHunt ties the travel-route chain
        /// (Repot → EXP) and the exp loop together so they always use a consistent
        /// set of paths.
        /// </summary>
        public BotWorkflowCoordinator(
            GameMemoryService memoryService,
            RepotSystem repotSystem,
            RepotDetectorService repotDetector,
            SavedPathLoader pathLoader,
            PathRunnerService pathRunner,
            BotProfile profile,
            HuntDefinition activeHunt,
            Action<string> log,
            Action focusGameWindow)
        {
            _memoryService = memoryService;
            _repotSystem = repotSystem;
            _repotDetector = repotDetector;
            _pathLoader = pathLoader;
            _pathRunner = pathRunner;
            _profile = profile;
            _activeHunt = activeHunt;
            _log = log;
            _focusGameWindow = focusGameWindow;

            // Apply profile thresholds to detector
            _repotDetector.MinHpPotions = profile.MinHpPotions;
            _repotDetector.MinManaPotions = profile.MinManaPotions;
            _repotDetector.MaxWeightRatio = profile.MaxWeightRatio;
            _repotDetector.MinHp = profile.MinHp;
            _repotDetector.MinMana = profile.MinMana;

            // Apply profile potion buy targets
            _repotSystem.HpBuyTarget = profile.HpBuyTarget;
            _repotSystem.ManaBuyTarget = profile.ManaBuyTarget;
            _repotSystem.RedBuyTarget = profile.RedBuyTarget;
            _repotSystem.WhiteBuyTarget = profile.WhiteBuyTarget;
        }

        // ======================== Lifecycle ========================

        /// <summary>
        /// Starts the workflow loop.
        /// </summary>
        public async Task StartAsync()
        {
            if (_isRunning)
            {
                _log("[Coordinator] Already running.");
                return;
            }

            _isRunning = true;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _focusGameWindow();
            _log("[Coordinator] Workflow started.");
            _log($"[Coordinator] Using profile: {_profile.Name}");
            _log($"[Coordinator] Active hunt: '{_activeHunt.Name}'");
            _log($"[Coordinator]   City → Repot: '{_profile.CityToRepot.PathFile}' (delay {_profile.CityToRepot.StartDelayMs} ms)");

            int routeCount = _activeHunt.TravelToExpRoutes?.Count ?? 0;
            for (int i = 0; i < routeCount; i++)
            {
                var route = _activeHunt.TravelToExpRoutes[i];
                if (route == null) continue;
                string mapInfo = route.CompletionMode == TravelRouteCompletionMode.ExpectedMapReached
                    ? $", map {route.ExpectedDestinationMapNumber}"
                    : "";
                _log($"[Coordinator]   Travel route {i + 1}/{routeCount}: '{route.PathFile}' (delay {route.StartDelayMs} ms, {route.CompletionMode}{mapInfo})");
            }

            _log($"[Coordinator]   EXP loop: '{_activeHunt.ExpLoop.PathFile}' (delay {_activeHunt.ExpLoop.StartDelayMs} ms)");

            try
            {
                await RunWorkflowLoop(token);
            }
            catch (OperationCanceledException)
            {
                _log("[Coordinator] Workflow cancelled.");
            }
            catch (Exception ex)
            {
                _log($"[Coordinator] Unhandled error: {ex.Message}");
                CurrentPhase = BotPhase.Failed;
            }
            finally
            {
                _pathRunner.Stop();
                _isRunning = false;
                if (CurrentPhase != BotPhase.Failed && CurrentPhase != BotPhase.Stopping)
                    CurrentPhase = BotPhase.Idle;
                _log("[Coordinator] Workflow stopped.");
                OnStopped?.Invoke();
            }
        }

        /// <summary>
        /// Stops the workflow gracefully.
        /// </summary>
        public void Stop()
        {
            _log("[Coordinator] Stopping...");
            CurrentPhase = BotPhase.Stopping;
            _cts?.Cancel();
            _pathRunner.Stop();
        }

        // ======================== Main Loop ========================

        private async Task RunWorkflowLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (CurrentPhase == BotPhase.Stopping)
                    break;

                switch (CurrentPhase)
                {
                    case BotPhase.Idle:
                        CurrentPhase = BotPhase.DetectCityStart;
                        break;
                    case BotPhase.DetectCityStart:
                        await PhaseDetectCityStart(token);
                        break;
                    case BotPhase.MoveToRepot:
                        await PhaseMoveToRepot(token);
                        break;
                    case BotPhase.Repot:
                        await PhaseRepot(token);
                        break;
                    case BotPhase.MoveToExp:
                        await PhaseMoveToExp(token);
                        break;
                    case BotPhase.ExpLoop:
                        await PhaseExpLoop(token);
                        break;
                    case BotPhase.NeedRepot:
                        await PhaseNeedRepot(token);
                        break;
                    case BotPhase.Stopping:
                        break;
                    case BotPhase.Failed:
                        _log("[Coordinator] Bot in Failed state. Manual restart required.");
                        await Task.Delay(BotConstants.Delays.FailedStateMs, token);
                        break;
                    default:
                        await Task.Delay(BotConstants.Delays.DefaultPhaseMs, token);
                        break;
                }

                await Task.Delay(BotConstants.Delays.WorkflowMainLoopMs, token);
            }
        }

        // ======================== Phase Implementations ========================

        private async Task PhaseDetectCityStart(CancellationToken token)
        {
            _log("[Phase] DetectCityStart — checking player state...");

            var snapshot = _memoryService.GetSnapshot();
            _log($"[Phase] Position: ({snapshot.X:F1}, {snapshot.Y:F1}), Map: {snapshot.MapNumber}, InCity: {snapshot.IsInCity}");
            _log($"[Phase] HP: {snapshot.Hp}, Mana: {snapshot.Mana}, HP Pots: {snapshot.HpPotions}, Mana Pots: {snapshot.ManaPotions}");
            _log($"[Phase] Weight: {snapshot.CurrentWeight}/{snapshot.MaxWeight}");

            if (!snapshot.IsInCity)
            {
                _log("[Phase] Player is NOT in city. Teleporting...");
                await TeleportToCity(token);
                snapshot = _memoryService.GetSnapshot();
                if (!snapshot.IsInCity)
                {
                    _log("[Phase] Failed to reach city after teleport.");
                    CurrentPhase = BotPhase.Failed;
                    return;
                }
            }

            _log("[Phase] Player is in city. Moving to repot.");
            CurrentPhase = BotPhase.MoveToRepot;
            await Task.CompletedTask;
        }

        private async Task PhaseMoveToRepot(CancellationToken token)
        {
            var result = await RunRouteOnceAsync(_profile.CityToRepot, "City → Repot", token);
            if (token.IsCancellationRequested) return;

            switch (result)
            {
                case RouteRunResult.MissingSegment:
                    _log("[Phase] MoveToRepot: City → Repot path missing or invalid. Failing.");
                    CurrentPhase = BotPhase.Failed;
                    break;
                case RouteRunResult.Completed:
                    _log("[Phase] MoveToRepot: Arrived at repot point.");
                    CurrentPhase = BotPhase.Repot;
                    break;
                default:
                    _log("[Phase] MoveToRepot: Path did not complete. Retrying from start.");
                    CurrentPhase = BotPhase.DetectCityStart;
                    break;
            }
        }

        private async Task PhaseRepot(CancellationToken token)
        {
            // Dry-run check
            if (_profile.DryRunRepot)
            {
                _log("[Phase] Repot: DryRunRepot=true — skipping actual repot. Moving to exp travel routes.");
                CurrentPhase = BotPhase.MoveToExp;
                return;
            }

            _log("[Phase] Repot — starting repot sequence...");

            try
            {
                _repotSystem.Repot();
                _log("[Phase] Repot completed.");
            }
            catch (Exception ex)
            {
                _log($"[Phase] Repot failed: {ex.Message}");
                CurrentPhase = BotPhase.Failed;
                return;
            }

            var snapshot = _memoryService.GetSnapshot();
            _log($"[Phase] Post-repot: HP Pots: {snapshot.HpPotions}, Mana Pots: {snapshot.ManaPotions}");

            CurrentPhase = BotPhase.MoveToExp;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Executes the complete ordered TravelToExpRoutes chain from the repot location
        /// to the EXP position. Route N + 1 is never started until route N has completed
        /// by its final waypoint or has reached and settled on its expected destination map.
        /// </summary>
        private async Task PhaseMoveToExp(CancellationToken token)
        {
            var routes = _activeHunt.TravelToExpRoutes;
            if (routes == null || routes.Count == 0)
            {
                _log("[Phase] MoveToExp: No travel routes configured. Failing.");
                CurrentPhase = BotPhase.Failed;
                return;
            }

            for (int i = 0; i < routes.Count; i++)
            {
                var result = await RunTravelRouteAsync(routes[i], i + 1, routes.Count, token);

                switch (result)
                {
                    case TravelRouteRunResult.Completed:
                        // Continue to the next route.
                        break;
                    case TravelRouteRunResult.Cancelled:
                        return;
                    case TravelRouteRunResult.Incomplete:
                        _log($"[Phase] MoveToExp: Route {i + 1}/{routes.Count} did not complete. Retrying from start.");
                        CurrentPhase = BotPhase.DetectCityStart;
                        return;
                    default:
                        // MissingSegment, ExpectedMapNotReached, UnexpectedMapReached, InvalidMapState
                        _log($"[Phase] MoveToExp: Route {i + 1}/{routes.Count} failed ({result}). Failing.");
                        CurrentPhase = BotPhase.Failed;
                        return;
                }
            }

            _log("[Phase] MoveToExp: All travel routes completed. Starting exp loop.");
            CurrentPhase = BotPhase.ExpLoop;
        }

        private async Task PhaseExpLoop(CancellationToken token)
        {
            var loopStep = _activeHunt.ExpLoop;
            if (loopStep == null || string.IsNullOrWhiteSpace(loopStep.PathFile))
            {
                _log("[Phase] ExpLoop: Exp Loop path missing or invalid. Failing.");
                CurrentPhase = BotPhase.Failed;
                return;
            }

            // Wait once before starting the looping route (not on every cycle).
            await WaitBeforeStepAsync(loopStep, "Exp Loop", token);
            if (token.IsCancellationRequested) return;

            _log($"[Phase] ExpLoop — loading path '{loopStep.PathFile}'...");
            var waypoints = _pathLoader.LoadSegment(loopStep.PathFile);
            if (waypoints == null)
            {
                _log($"[Phase] ExpLoop: Missing required segment '{loopStep.PathFile}'. Cannot proceed.");
                CurrentPhase = BotPhase.Failed;
                return;
            }

            using var expCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var expToken = expCts.Token;

            var pathTask = _pathRunner.RunPathAsync(waypoints, loop: true, expToken);

            bool repotNeeded = false;
            try
            {
                while (!expToken.IsCancellationRequested)
                {
                    await Task.Delay(BotConstants.Delays.ExpLoopRepotCheckIntervalMs, expToken);

                    var snapshot = _memoryService.GetSnapshot();
                    if (_repotDetector.NeedsRepot(snapshot))
                    {
                        _log("[Phase] ExpLoop: Repot condition detected. Stopping exp loop.");
                        repotNeeded = true;
                        expCts.Cancel();
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }

            try
            {
                await pathTask;
            }
            catch (OperationCanceledException) { }

            _pathRunner.Stop();
            _log("[Phase] ExpLoop: Exp hunting loop ended.");

            if (token.IsCancellationRequested)
                return;

            if (repotNeeded)
            {
                _log("[Phase] ExpLoop: Transitioning to NeedRepot.");
                CurrentPhase = BotPhase.NeedRepot;
            }
            else
            {
                _log("[Phase] ExpLoop: Path stopped for unknown reason. Going to city.");
                CurrentPhase = BotPhase.DetectCityStart;
            }
        }

        private async Task PhaseNeedRepot(CancellationToken token)
        {
            _log("[Phase] NeedRepot — teleporting to city...");

            await TeleportToCity(token);

            var snapshot = _memoryService.GetSnapshot();
            if (snapshot.IsInCity)
            {
                _log("[Phase] NeedRepot: Successfully arrived in city.");
                _teleportRetryCount = 0;
                CurrentPhase = BotPhase.DetectCityStart;
            }
            else
            {
                int maxRetries = _profile.MaxTeleportRetries;
                _teleportRetryCount++;
                if (_teleportRetryCount >= maxRetries)
                {
                    _log($"[Phase] NeedRepot: Failed after {maxRetries} teleport attempts. Giving up.");
                    _teleportRetryCount = 0;
                    CurrentPhase = BotPhase.Failed;
                }
                else
                {
                    _log($"[Phase] NeedRepot: Not in city after teleport (attempt {_teleportRetryCount}/{maxRetries}). Retrying.");
                    CurrentPhase = BotPhase.NeedRepot;
                }
            }
        }

        // ======================== Helpers ========================

        private enum RouteRunResult
        {
            Completed,
            MissingSegment,
            Incomplete
        }

        private enum TravelRouteRunResult
        {
            Completed,
            MissingSegment,
            Incomplete,
            ExpectedMapNotReached,
            UnexpectedMapReached,
            InvalidMapState,
            Cancelled
        }

        /// <summary>
        /// Waits the configured startup delay of a route step, if any.
        /// Zero (or an invalid negative) delay returns immediately.
        /// The wait is cancellable and happens every time the workflow enters the stage.
        /// </summary>
        private async Task WaitBeforeStepAsync(BotRouteStep step, string stepName, CancellationToken token)
        {
            if (step == null) return;
            if (step.StartDelayMs <= 0) return;

            _log($"[Coordinator] {stepName}: waiting {step.StartDelayMs} ms before start...");
            await Task.Delay(step.StartDelayMs, token);
        }

        /// <summary>
        /// Runs one non-loop BotRouteStep (e.g. City → Repot): waits the configured startup
        /// delay, loads the segment via SavedPathLoader and runs it once with loop: false.
        /// Returns whether the path completed, or whether the segment is missing/invalid.
        /// </summary>
        private async Task<RouteRunResult> RunRouteOnceAsync(BotRouteStep step, string stageName, CancellationToken token)
        {
            if (step == null || string.IsNullOrWhiteSpace(step.PathFile))
            {
                _log($"[Phase] {stageName}: no path configured. Cannot proceed.");
                return RouteRunResult.MissingSegment;
            }

            await WaitBeforeStepAsync(step, stageName, token);
            if (token.IsCancellationRequested) return RouteRunResult.Incomplete;

            _log($"[Phase] {stageName} — loading path '{step.PathFile}'...");
            var waypoints = _pathLoader.LoadSegment(step.PathFile);
            if (waypoints == null)
            {
                _log($"[Phase] {stageName}: Missing required segment '{step.PathFile}'. Cannot proceed.");
                return RouteRunResult.MissingSegment;
            }

            bool completed = await _pathRunner.RunPathAsync(waypoints, loop: false, token);
            if (completed)
                _log($"[Phase] {stageName}: path completed.");
            else
                _log($"[Phase] {stageName}: path did not complete.");

            return completed ? RouteRunResult.Completed : RouteRunResult.Incomplete;
        }

        /// <summary>
        /// Executes one travel route of the Repot → EXP chain.
        /// FinalWaypoint routes complete through normal non-loop path completion;
        /// ExpectedMapReached routes complete when the configured destination map is
        /// stably detected, without requiring the final waypoint.
        /// </summary>
        private async Task<TravelRouteRunResult> RunTravelRouteAsync(
            TravelRouteStep step,
            int routeIndex,
            int routeCount,
            CancellationToken token)
        {
            try
            {
                return await RunTravelRouteCoreAsync(step, routeIndex, routeCount, token);
            }
            catch (OperationCanceledException)
            {
                return TravelRouteRunResult.Cancelled;
            }
        }

        private async Task<TravelRouteRunResult> RunTravelRouteCoreAsync(
            TravelRouteStep step,
            int routeIndex,
            int routeCount,
            CancellationToken token)
        {
            if (step == null || string.IsNullOrWhiteSpace(step.PathFile))
            {
                _log($"[MoveToExp] Route {routeIndex}/{routeCount}: no path configured. Cannot proceed.");
                return TravelRouteRunResult.MissingSegment;
            }

            // Startup delay (cancellable).
            if (step.StartDelayMs > 0)
            {
                _log($"[MoveToExp] Route {routeIndex}/{routeCount}: waiting {step.StartDelayMs} ms before start...");
                await Task.Delay(step.StartDelayMs, token);
            }

            _log($"[MoveToExp] Route {routeIndex}/{routeCount}:");
            _log($"  Path='{step.PathFile}'");
            _log($"  Completion={step.CompletionMode}");
            if (step.CompletionMode == TravelRouteCompletionMode.ExpectedMapReached)
                _log($"  DestinationMap={step.ExpectedDestinationMapNumber}");

            var waypoints = _pathLoader.LoadSegment(step.PathFile);
            if (waypoints == null)
            {
                _log($"[MoveToExp] Route {routeIndex}/{routeCount}: Missing required segment '{step.PathFile}'. Cannot proceed.");
                return TravelRouteRunResult.MissingSegment;
            }

            if (step.CompletionMode != TravelRouteCompletionMode.ExpectedMapReached)
            {
                // FinalWaypoint: normal non-loop execution.
                bool completed = await _pathRunner.RunPathAsync(waypoints, loop: false, token);
                if (token.IsCancellationRequested) return TravelRouteRunResult.Cancelled;

                if (completed)
                    _log($"[MoveToExp] Route {routeIndex}/{routeCount}: path completed (final waypoint).");
                else
                    _log($"[MoveToExp] Route {routeIndex}/{routeCount}: path did not complete.");

                return completed ? TravelRouteRunResult.Completed : TravelRouteRunResult.Incomplete;
            }

            // ExpectedMapReached: portal-aware execution.
            return await RunMapTransitionRouteAsync(step, routeIndex, routeCount, waypoints, token);
        }

        /// <summary>
        /// Executes one portal route: runs the path while polling the map number, stops
        /// movement as soon as the expected destination map is confirmed, and settles the
        /// map/player-position reads before the next route starts.
        /// </summary>
        private async Task<TravelRouteRunResult> RunMapTransitionRouteAsync(
            TravelRouteStep step,
            int routeIndex,
            int routeCount,
            List<Waypoint> waypoints,
            CancellationToken token)
        {
            int expectedMap = step.ExpectedDestinationMapNumber;

            // Wait for a valid nonzero source map before starting.
            int sourceMap = await WaitForValidMapAsync(BotConstants.Delays.ValidMapReadTimeoutMs, token);
            if (sourceMap == 0)
            {
                _log($"[MoveToExp] Route {routeIndex}/{routeCount}: no valid source map read within {BotConstants.Delays.ValidMapReadTimeoutMs} ms. Failing.");
                return TravelRouteRunResult.InvalidMapState;
            }

            _log($"[MoveToExp] Route {routeIndex}/{routeCount}: source map {sourceMap}.");

            // Already on the destination map: the route is already complete, do not start its path.
            if (sourceMap == expectedMap)
            {
                _log($"[MoveToExp] Route {routeIndex}/{routeCount}: already on destination map {expectedMap}. Route considered complete.");
                return TravelRouteRunResult.Completed;
            }

            using var routeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var routeToken = routeCts.Token;

            var pathTask = _pathRunner.RunPathAsync(waypoints, loop: false, routeToken);

            int consecutiveDestReads = 0;
            bool destinationConfirmed = false;

            while (!routeToken.IsCancellationRequested && !destinationConfirmed)
            {
                if (pathTask.IsCompleted)
                    break; // path finished before the map changed — handled by the grace period below

                await Task.Delay(BotConstants.Delays.MapTransitionPollMs, routeToken);

                int map = _memoryService.GetMapNumber();
                if (map == 0)
                    continue; // ignore unreadable map reads

                if (map == expectedMap)
                {
                    consecutiveDestReads++;
                    if (consecutiveDestReads >= BotConstants.Delays.MapTransitionStableReadCount)
                    {
                        _log($"[MoveToExp] Route {routeIndex}/{routeCount}: destination map {expectedMap} confirmed. Stopping movement.");
                        destinationConfirmed = true;
                        _pathRunner.Stop();
                        routeCts.Cancel();
                        try { await pathTask; } catch (OperationCanceledException) { }
                    }
                }
                else if (map != sourceMap)
                {
                    _log($"[MoveToExp] Route {routeIndex}/{routeCount}: unexpected map {map} (expected {expectedMap}). Stopping route.");
                    _pathRunner.Stop();
                    routeCts.Cancel();
                    try { await pathTask; } catch (OperationCanceledException) { }
                    return TravelRouteRunResult.UnexpectedMapReached;
                }
                else
                {
                    consecutiveDestReads = 0;
                }
            }

            if (token.IsCancellationRequested)
                return TravelRouteRunResult.Cancelled;

            if (destinationConfirmed)
                return await WaitForMapSettlementAsync(expectedMap, routeIndex, routeCount, token);

            // The path finished before the expected destination map was confirmed:
            // give the portal a bounded grace period to activate.
            _log($"[MoveToExp] Route {routeIndex}/{routeCount}: path finished before destination map confirmed. Waiting grace period for portal transition...");
            _pathRunner.Stop();

            var graceDeadline = DateTime.UtcNow.AddMilliseconds(BotConstants.Delays.MapTransitionGraceAfterPathMs);
            int graceConsecutiveReads = 0;
            while (DateTime.UtcNow < graceDeadline)
            {
                await Task.Delay(BotConstants.Delays.MapTransitionPollMs, token);
                if (token.IsCancellationRequested)
                    return TravelRouteRunResult.Cancelled;

                int map = _memoryService.GetMapNumber();
                if (map == 0)
                    continue;

                if (map == expectedMap)
                {
                    graceConsecutiveReads++;
                    if (graceConsecutiveReads >= BotConstants.Delays.MapTransitionStableReadCount)
                    {
                        _log($"[MoveToExp] Route {routeIndex}/{routeCount}: destination map {expectedMap} confirmed during grace period.");
                        return await WaitForMapSettlementAsync(expectedMap, routeIndex, routeCount, token);
                    }
                }
                else if (map != sourceMap)
                {
                    _log($"[MoveToExp] Route {routeIndex}/{routeCount}: unexpected map {map} during grace period (expected {expectedMap}).");
                    return TravelRouteRunResult.UnexpectedMapReached;
                }
                else
                {
                    graceConsecutiveReads = 0;
                }
            }

            _log($"[MoveToExp] Route {routeIndex}/{routeCount}: still on source map {sourceMap} after grace period. Expected map {expectedMap} not reached.");
            return TravelRouteRunResult.ExpectedMapNotReached;
        }

        /// <summary>
        /// Polls until a valid nonzero map number can be read or the timeout elapses.
        /// Returns 0 on timeout. GameMemoryService.GetMapNumber() returns 0 when the
        /// pointer chain cannot be read, so zero reads are ignored.
        /// </summary>
        private async Task<int> WaitForValidMapAsync(int timeoutMs, CancellationToken token)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(BotConstants.Delays.MapTransitionPollMs, token);

                int map = _memoryService.GetMapNumber();
                if (map != 0)
                    return map;
            }

            return 0;
        }

        /// <summary>
        /// Waits until the destination map and the player position read are both stable
        /// (two consecutive polls) so the next route never initializes while the player
        /// pointer or coordinates are temporarily unavailable during loading.
        /// </summary>
        private async Task<TravelRouteRunResult> WaitForMapSettlementAsync(
            int expectedMap,
            int routeIndex,
            int routeCount,
            CancellationToken token)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(BotConstants.Delays.MapTransitionSettleTimeoutMs);
            int stableReads = 0;

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(BotConstants.Delays.MapTransitionPollMs, token);
                if (token.IsCancellationRequested)
                    return TravelRouteRunResult.Cancelled;

                int map = _memoryService.GetMapNumber();
                var (_, _, positionSuccess) = _memoryService.GetPlayerPosition();

                if (map == expectedMap && positionSuccess)
                {
                    stableReads++;
                    if (stableReads >= BotConstants.Delays.MapTransitionStableReadCount)
                    {
                        _log($"[MoveToExp] Route {routeIndex}/{routeCount}: destination map {expectedMap} settled (map and player position stable).");
                        return TravelRouteRunResult.Completed;
                    }
                }
                else
                {
                    stableReads = 0;
                }
            }

            _log($"[MoveToExp] Route {routeIndex}/{routeCount}: destination map {expectedMap} did not settle within {BotConstants.Delays.MapTransitionSettleTimeoutMs} ms.");
            return TravelRouteRunResult.InvalidMapState;
        }

        private async Task TeleportToCity(CancellationToken token)
        {
            byte vk = (byte)_profile.TeleportKey;
            byte scan = (byte)_profile.TeleportScanCode;

            _log($"[Teleport] Pressing key (vk={vk}) for town teleport...");
            keybd_event(vk, scan, 0, 0);
            await Task.Delay(BotConstants.Delays.TeleportKeyDownMs, token);
            keybd_event(vk, scan, KEYEVENTF_KEYUP, 0);

            for (int i = 0; i < BotConstants.Delays.TeleportWaitIterations; i++)
            {
                await Task.Delay(BotConstants.Delays.TeleportWaitIterationMs, token);
                if (_memoryService.GetIsInCity())
                {
                    _log("[Teleport] Arrived in city.");
                    return;
                }
            }

            _log("[Teleport] Teleport wait timeout — proceeding anyway.");
        }
    }
}
