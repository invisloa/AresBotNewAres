using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Central coordinator of the bot four-stage route workflow:
    ///   1. City → Repot (route stage 1)
    ///      [repot operation: sell items, buy potions]
    ///   2. Repot → Outside City (route stage 2)
    ///   3. Outside City → Exp Spot (route stage 3)
    ///   4. Exp Loop (route stage 4, loops until repot is needed)
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
        /// Profile-based constructor. The activeHunt ties stages 2-4 (Repot → Outside City,
        /// Outside City → Exp Spot, Exp Loop) together so they always use a consistent
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
            _log($"[Coordinator]   City → Repot:            '{_profile.CityToRepot.PathFile}' (delay {_profile.CityToRepot.StartDelayMs} ms)");
            _log($"[Coordinator]   Repot → Outside City:    '{_activeHunt.RepotToCityExit.PathFile}' (delay {_activeHunt.RepotToCityExit.StartDelayMs} ms)");
            _log($"[Coordinator]   Outside City → Exp Spot: '{_activeHunt.CityExitToExp.PathFile}' (delay {_activeHunt.CityExitToExp.StartDelayMs} ms)");
            _log($"[Coordinator]   Exp Loop:                '{_activeHunt.ExpLoop.PathFile}' (delay {_activeHunt.ExpLoop.StartDelayMs} ms)");

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
                    case BotPhase.MoveToCityExit:
                        await PhaseMoveToCityExit(token);
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
                _log("[Phase] Repot: DryRunRepot=true — skipping actual repot. Moving to city exit.");
                CurrentPhase = BotPhase.MoveToCityExit;
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

            CurrentPhase = BotPhase.MoveToCityExit;
            await Task.CompletedTask;
        }

        private async Task PhaseMoveToCityExit(CancellationToken token)
        {
            var result = await RunRouteOnceAsync(_activeHunt.RepotToCityExit, "Repot → Outside City", token);
            if (token.IsCancellationRequested) return;

            switch (result)
            {
                case RouteRunResult.MissingSegment:
                    _log("[Phase] MoveToCityExit: Repot → Outside City path missing or invalid. Failing.");
                    CurrentPhase = BotPhase.Failed;
                    break;
                case RouteRunResult.Completed:
                    _log("[Phase] MoveToCityExit: Arrived outside the city.");
                    CurrentPhase = BotPhase.MoveToExp;
                    break;
                default:
                    _log("[Phase] MoveToCityExit: Path did not complete. Retrying from start.");
                    CurrentPhase = BotPhase.DetectCityStart;
                    break;
            }
        }

        private async Task PhaseMoveToExp(CancellationToken token)
        {
            var result = await RunRouteOnceAsync(_activeHunt.CityExitToExp, "Outside City → Exp Spot", token);
            if (token.IsCancellationRequested) return;

            switch (result)
            {
                case RouteRunResult.MissingSegment:
                    _log("[Phase] MoveToExp: Outside City → Exp Spot path missing or invalid. Failing.");
                    CurrentPhase = BotPhase.Failed;
                    break;
                case RouteRunResult.Completed:
                    _log("[Phase] MoveToExp: Arrived at exp area.");
                    CurrentPhase = BotPhase.ExpLoop;
                    break;
                default:
                    _log("[Phase] MoveToExp: Path did not complete. Retrying.");
                    CurrentPhase = BotPhase.DetectCityStart;
                    break;
            }
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
        /// Runs one non-loop route stage (stages 1-3): waits the configured startup delay,
        /// loads the segment via SavedPathLoader and runs it once with loop: false.
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
