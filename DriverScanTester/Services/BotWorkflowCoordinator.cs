using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Central coordinator of the bot 3-phase workflow:
    ///   1. City → Repot (sell items, buy potions)
    ///   2. Repot → Exp area
    ///   3. Exp loop (hunt, check for repot condition, teleport back)
    ///
    /// MovementSystem is used ONLY for movement. All phase decisions are made here.
    /// Can work with a BotProfile (preferred) or with hardcoded fallback paths for testing.
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

        // Profile-based components (optional — null when using hardcoded fallback)
        private readonly BotProfile? _profile;
        private readonly CityToRepotRouteSelector? _routeSelector;

        // Active hunt definition (ties phase 2 + phase 3 together)
        private readonly HuntDefinition? _activeHunt;

        private CancellationTokenSource? _cts;
        private bool _isRunning;
        private BotPhase _currentPhase = BotPhase.Idle;

        // State set during DetectCityStart and consumed by MoveToRepot
        private string _selectedCityToRepotPath = "";
        private int _teleportRetryCount;

        // Hardcoded fallback path names (used when _profile is null)
        public string FallbackCityToRepot { get; set; } = BotConstants.Workflow.FallbackCityToRepot;
        public string FallbackRepotToExp { get; set; } = BotConstants.Workflow.FallbackRepotToExp;
        public string FallbackExpLoop { get; set; } = BotConstants.Workflow.FallbackExpLoop;

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

        /// <summary>The active profile, if any.</summary>
        public BotProfile? ActiveProfile => _profile;

        // ======================== Constructors ========================

        /// <summary>
        /// Profile-based constructor (preferred). Validates the profile before starting.
        /// The activeHunt ties phase 2 (move to exp) and phase 3 (exp loop) together
        /// so they always use a consistent pair of paths.
        /// </summary>
        public BotWorkflowCoordinator(
            GameMemoryService memoryService,
            RepotSystem repotSystem,
            RepotDetectorService repotDetector,
            SavedPathLoader pathLoader,
            PathRunnerService pathRunner,
            BotProfile profile,
            CityToRepotRouteSelector routeSelector,
            HuntDefinition? activeHunt,
            Action<string> log,
            Action focusGameWindow)
        {
            _memoryService = memoryService;
            _repotSystem = repotSystem;
            _repotDetector = repotDetector;
            _pathLoader = pathLoader;
            _pathRunner = pathRunner;
            _profile = profile;
            _routeSelector = routeSelector;
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

        /// <summary>
        /// Legacy constructor without profile (uses hardcoded fallback paths).
        /// </summary>
        public BotWorkflowCoordinator(
            GameMemoryService memoryService,
            RepotSystem repotSystem,
            RepotDetectorService repotDetector,
            SavedPathLoader pathLoader,
            PathRunnerService pathRunner,
            Action<string> log,
            Action focusGameWindow)
        {
            _memoryService = memoryService;
            _repotSystem = repotSystem;
            _repotDetector = repotDetector;
            _pathLoader = pathLoader;
            _pathRunner = pathRunner;
            _log = log;
            _focusGameWindow = focusGameWindow;
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
            if (_profile != null)
                _log($"[Coordinator] Using profile: {_profile.Name}");

            if (_activeHunt != null)
            {
                _log($"[Coordinator] Active hunt: '{_activeHunt.Name}'");
                _log($"[Coordinator]   RepotToExpPath: '{_activeHunt.RepotToExpPath}'");
                _log($"[Coordinator]   ExpLoopPath:    '{_activeHunt.ExpLoopPath}'");
            }
            else if (_profile != null)
            {
                _log("[Coordinator] ERROR: Profile workflow started without active hunt.");
            }
            else
            {
                _log("[Coordinator] No profile selected — using hardcoded fallback paths.");
            }

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

            // Determine which city→repot path to use
            string? pathToRepot = ResolveCityToRepotPath(snapshot);
            if (pathToRepot == null)
            {
                _log("[Phase] DetectCityStart: Could not determine city→repot path. Failing.");
                CurrentPhase = BotPhase.Failed;
                return;
            }

            _selectedCityToRepotPath = pathToRepot;
            _log($"[Phase] Selected city→repot path: {_selectedCityToRepotPath}");
            CurrentPhase = BotPhase.MoveToRepot;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Resolves the city→repot segment filename.
        /// With a profile, uses the route selector. Without, uses FallbackCityToRepot.
        /// </summary>
        private string? ResolveCityToRepotPath(GameSnapshot snapshot)
        {
            if (_profile != null && _routeSelector != null)
            {
                var route = _routeSelector.SelectRoute(_profile, snapshot);
                if (route == null)
                {
                    _log($"[Phase] No matching StartRoute for Map={snapshot.MapNumber}, X={snapshot.X:F1}, Y={snapshot.Y:F1}");
                    return null;
                }
                _log($"[Phase] Matched StartRoute '{route.Name}' -> path '{route.PathFile}'");
                return route.PathFile;
            }

            // No profile — use hardcoded fallback
            _log($"[Phase] No profile active; using fallback city→repot path: {FallbackCityToRepot}");
            return FallbackCityToRepot;
        }

        private async Task PhaseMoveToRepot(CancellationToken token)
        {
            string pathName = _selectedCityToRepotPath;
            if (string.IsNullOrEmpty(pathName))
                pathName = FallbackCityToRepot;

            _log($"[Phase] MoveToRepot — loading path '{pathName}'...");
            var waypoints = _pathLoader.LoadSegment(pathName);
            if (waypoints == null)
            {
                _log($"[Phase] MoveToRepot: Missing required segment '{pathName}'. Cannot proceed.");
                CurrentPhase = BotPhase.Failed;
                return;
            }

            bool completed = await _pathRunner.RunPathAsync(waypoints, loop: false, token);

            if (token.IsCancellationRequested) return;

            if (completed)
            {
                _log("[Phase] MoveToRepot: Arrived at repot point.");
                CurrentPhase = BotPhase.Repot;
            }
            else
            {
                _log("[Phase] MoveToRepot: Path did not complete. Retrying from start.");
                CurrentPhase = BotPhase.DetectCityStart;
            }
        }

        private async Task PhaseRepot(CancellationToken token)
        {
            // Dry-run check
            if (_profile != null && _profile.DryRunRepot)
            {
                _log("[Phase] Repot: DryRunRepot=true — skipping actual repot. Moving to exp.");
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

        private async Task PhaseMoveToExp(CancellationToken token)
        {
            // Phase 2: MUST use active hunt's RepotToExpPath when profile is loaded.
            // Fallback to hardcoded path only when _profile is null (no-profile mode).
            string pathName;

            if (_activeHunt != null && !string.IsNullOrWhiteSpace(_activeHunt.RepotToExpPath))
            {
                pathName = _activeHunt.RepotToExpPath;
                _log($"[Phase] MoveToExp — using active hunt '{_activeHunt.Name}', path '{pathName}'...");
            }
            else if (_profile == null)
            {
                pathName = FallbackRepotToExp;
                _log($"[Phase] MoveToExp — no profile, hardcoded fallback path '{pathName}'...");
            }
            else
            {
                _log($"[Phase] MoveToExp: Profile loaded but no active hunt or empty RepotToExpPath. Failing.");
                CurrentPhase = BotPhase.Failed;
                return;
            }

            var waypoints = _pathLoader.LoadSegment(pathName);
            if (waypoints == null)
            {
                _log($"[Phase] MoveToExp: Missing required segment '{pathName}'. Cannot proceed.");
                CurrentPhase = BotPhase.Failed;
                return;
            }

            bool completed = await _pathRunner.RunPathAsync(waypoints, loop: false, token);

            if (token.IsCancellationRequested) return;

            if (completed)
            {
                _log("[Phase] MoveToExp: Arrived at exp area.");
                CurrentPhase = BotPhase.ExpLoop;
            }
            else
            {
                _log("[Phase] MoveToExp: Path did not complete. Retrying.");
                CurrentPhase = BotPhase.DetectCityStart;
            }
        }

        private async Task PhaseExpLoop(CancellationToken token)
        {
            // Phase 3: MUST use active hunt's ExpLoopPath when profile is loaded.
            // Fallback to hardcoded path only when _profile is null (no-profile mode).
            string pathName;

            if (_activeHunt != null && !string.IsNullOrWhiteSpace(_activeHunt.ExpLoopPath))
            {
                pathName = _activeHunt.ExpLoopPath;
                _log($"[Phase] ExpLoop — using active hunt '{_activeHunt.Name}', path '{pathName}'...");
            }
            else if (_profile == null)
            {
                pathName = FallbackExpLoop;
                _log($"[Phase] ExpLoop — no profile, hardcoded fallback path '{pathName}'...");
            }
            else
            {
                _log($"[Phase] ExpLoop: Profile loaded but no active hunt or empty ExpLoopPath. Failing.");
                CurrentPhase = BotPhase.Failed;
                return;
            }

            var waypoints = _pathLoader.LoadSegment(pathName);
            if (waypoints == null)
            {
                _log($"[Phase] ExpLoop: Missing required segment '{pathName}'. Cannot proceed.");
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
                int maxRetries = _profile?.MaxTeleportRetries ?? BotConstants.Repot.MaxTeleportRetries;
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

        private async Task TeleportToCity(CancellationToken token)
        {
            byte vk;
            byte scan;

            if (_profile != null)
            {
                vk = (byte)_profile.TeleportKey;
                scan = (byte)_profile.TeleportScanCode;
            }
            else
            {
                vk = (byte)BotConstants.Workflow.DefaultTeleportKey; // '6'
                scan = (byte)BotConstants.Workflow.DefaultTeleportScanCode;
            }

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
