using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Central coordinator of the bot workflow. The workflow is a linear FLOW of mixed
    /// steps defined by the profile (BotProfile.FlowSteps):
    ///   • Path      — walk a saved segment once (final waypoint or expected map)
    ///   • Repot     — teleport to city if needed, walk to the repot point (cycling the
    ///                 step's repot path pool), then sell items and buy potions
    ///   • Operation — run a named custom operation (see BotOperations)
    ///   • ExpLoop   — loop the hunting path until the repot conditions are met
    ///
    /// Steps are executed top to bottom and wrap around at the end, so the flow cycles
    /// indefinitely. When the ExpLoop step ends (repot conditions met or the player is in
    /// the city) the flow simply continues to the next step; the Repot step is what
    /// returns the bot to the city and refills. Any mix of steps can be arranged freely,
    /// e.g.:
    ///   Repot → Operation (talk to NPC) → Path (go to exp) → Operation → ExpLoop
    ///
    /// MovementSystem is used ONLY for movement. All flow decisions are made here.
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
        private readonly OperationRunnerService _operationRunner;
        private readonly Action<string> _log;
        private readonly Action _focusGameWindow;

        private readonly BotProfile _profile;

        private CancellationTokenSource? _cts;
        private bool _isRunning;
        private BotPhase _currentPhase = BotPhase.Idle;

        /// <summary>Index of the flow step currently being executed.</summary>
        private int _flowIndex;

        /// <summary>
        /// Index of the next repot path in the Repot step's RepotPaths pool.
        /// Each repot trip walks to the repot using the next path in the list and then
        /// advances (wrapping around), so different repot routes are used over time.
        /// </summary>
        private int _repotPathIndex;

        /// <summary>
        /// Rotation index per flow step for its route group (<see cref="BotFlowStep.Routes"/>).
        /// Each time a step with a route group completes, its index advances (wrapping
        /// around), so a group like exp1 → exp2 → exp3 rotates one route per flow cycle.
        /// Steps with the same group size (e.g. a Repot step with repot1/2/3 and an
        /// ExpLoop step with exp1/2/3) stay in lockstep: cycle 1 uses repot1 + exp1,
        /// cycle 2 uses repot2 + exp2, etc.
        /// </summary>
        private readonly Dictionary<BotFlowStep, int> _stepRouteIndex = new();

        /// <summary>Consecutive repot-path failures. After 3 the workflow fails
        /// visibly instead of retrying forever (e.g. the player cannot move in the city).</summary>
        private int _moveToRepotRetryCount;

        /// <summary>Consecutive Path-step failures. After 3 the workflow fails visibly.</summary>
        private int _pathStepRetryCount;

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

        private string _currentStepText = "";
        /// <summary>Human-readable description of the flow step currently being executed.</summary>
        public string CurrentStepText
        {
            get => _currentStepText;
            private set
            {
                if (_currentStepText == value) return;
                _currentStepText = value;
                OnCurrentStepChanged?.Invoke(value);
            }
        }

        /// <summary>Fires whenever CurrentPhase changes.</summary>
        public Action<string>? OnPhaseChanged { get; set; }

        /// <summary>Fires whenever the current flow step description changes.</summary>
        public Action<string>? OnCurrentStepChanged { get; set; }

        /// <summary>Fires when the workflow stops.</summary>
        public Action? OnStopped { get; set; }

        /// <summary>Whether the coordinator is running.</summary>
        public bool IsRunning => _isRunning;

        /// <summary>The active profile.</summary>
        public BotProfile ActiveProfile => _profile;

        // ======================== Constructor ========================

        /// <summary>
        /// Profile-based constructor. The profile carries the whole flow (FlowSteps)
        /// plus the repot thresholds and potion buy targets.
        /// </summary>
        public BotWorkflowCoordinator(
            GameMemoryService memoryService,
            RepotSystem repotSystem,
            RepotDetectorService repotDetector,
            SavedPathLoader pathLoader,
            PathRunnerService pathRunner,
            OperationRunnerService operationRunner,
            BotProfile profile,
            Action<string> log,
            Action focusGameWindow)
        {
            _memoryService = memoryService;
            _repotSystem = repotSystem;
            _repotDetector = repotDetector;
            _pathLoader = pathLoader;
            _pathRunner = pathRunner;
            _operationRunner = operationRunner;
            _profile = profile;
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
            _flowIndex = 0;
            _repotPathIndex = 0;
            _stepRouteIndex.Clear();
            _moveToRepotRetryCount = 0;
            _pathStepRetryCount = 0;
            _teleportRetryCount = 0;

            _focusGameWindow();
            _log("[Coordinator] Workflow started.");
            _log($"[Coordinator] Profile: {_profile.Name}");
            LogFlowPlan();

            // ── Auto potion drinking ──
            var healSystem = new HealManaSystem(_memoryService, _log);
            HealManaSystem.Threshold1 = (short)Math.Clamp(_profile.MinHp, 0, short.MaxValue);
            HealManaSystem.Threshold2 = (short)Math.Clamp(_profile.MinMana, 0, short.MaxValue);
            _log($"[HealMana] Auto-drink enabled: HP < {HealManaSystem.Threshold1} → key 1, Mana < {HealManaSystem.Threshold2} → key 2.");

            Task? healTask = null;
            try
            {
                // ── Start position protection gate ──
                // Verifies the player stands on the profile's start position; if not,
                // uses the town teleport scroll and then verifies map + position against
                // the profile's protection settings. On failure the bot stops instead
                // of starting the flow (the finally block below performs the cleanup).
                if (!await RunStartProtectionAsync(token))
                {
                    _log("[Coordinator] Start protection failed — the bot will not start.");
                    CurrentPhase = BotPhase.Failed;
                    return;
                }

                healTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!token.IsCancellationRequested && healSystem != null)
                        {
                            await healSystem.Update(token);
                            await Task.Delay(100, token); // Update rate for heal/mana
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        _log($"[HealMana] Error: {ex.Message}");
                    }
                }, token);

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
                if (healTask != null)
                {
                    try
                    {
                        await healTask;
                    }
                    catch (OperationCanceledException) { }
                }
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

        private void LogFlowPlan()
        {
            var steps = _profile.FlowSteps;
            if (steps == null || steps.Count == 0)
            {
                _log("[Coordinator]   Flow: no steps configured!");
                return;
            }
            _log($"[Coordinator]   Flow ({steps.Count} steps):");
            for (int i = 0; i < steps.Count; i++)
            {
                _log($"[Coordinator]     {i + 1}. {DescribeStep(steps[i])}");
            }
        }

        // ======================== Main Loop ========================

        /// <summary>
        /// Drives the linear flow: executes the current step; on success advances to the
        /// next step (wrapping around at the end). Steps that return false handled the
        /// flow position themselves (e.g. a failed Path step restarted the flow).
        /// </summary>
        private async Task RunWorkflowLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (CurrentPhase == BotPhase.Stopping)
                    break;

                if (CurrentPhase == BotPhase.Failed)
                {
                    _log("[Coordinator] Bot in Failed state. Manual restart required.");
                    await Task.Delay(BotConstants.Delays.FailedStateMs, token);
                    continue;
                }

                var steps = _profile.FlowSteps;
                if (steps == null || steps.Count == 0)
                {
                    _log("[Coordinator] No flow steps configured. Failing.");
                    CurrentPhase = BotPhase.Failed;
                    continue;
                }

                if (_flowIndex < 0 || _flowIndex >= steps.Count)
                    _flowIndex = 0;

                var step = steps[_flowIndex];
                CurrentStepText = $"Step {_flowIndex + 1}/{steps.Count} · {DescribeStep(step)}";
                CurrentPhase = PhaseForStep(step);

                _log($"[Coordinator] Executing flow step {_flowIndex + 1}/{steps.Count}: {DescribeStep(step)}");

                bool advance = await ExecuteFlowStepAsync(step, token);
                if (token.IsCancellationRequested)
                    break;

                if (advance)
                {
                    _flowIndex++;
                }
                // When !advance the step already moved _flowIndex itself (e.g. a failed
                // Path step restarted the flow from the beginning, or the step failed).

                await Task.Delay(BotConstants.Delays.WorkflowMainLoopMs, token);
            }
        }

        // ======================== Flow Step Execution ========================

        /// <summary>
        /// Executes one flow step. Returns true when the flow should advance to the next
        /// step; false when the step handled the flow position itself (restart/failure).
        /// </summary>
        private async Task<bool> ExecuteFlowStepAsync(BotFlowStep step, CancellationToken token)
        {
            switch (step.Type)
            {
                case BotFlowStepType.Path:
                    return await ExecutePathStepAsync(step, token);
                case BotFlowStepType.Repot:
                    return await ExecuteRepotStepAsync(step, token);
                case BotFlowStepType.Operation:
                    return await ExecuteOperationStepAsync(step, token);
                case BotFlowStepType.ExpLoop:
                    return await ExecuteExpLoopStepAsync(step, token);
                default:
                    _log($"[Coordinator] Unknown flow step type '{step.Type}'. Failing.");
                    CurrentPhase = BotPhase.Failed;
                    return false;
            }
        }

        /// <summary>
        /// Path step: runs one saved segment once (final waypoint or expected map).
        /// When the step has a route group (<see cref="BotFlowStep.Routes"/>), the next
        /// route of the group is used on every execution (rotating per flow cycle).
        /// </summary>
        private async Task<bool> ExecutePathStepAsync(BotFlowStep step, CancellationToken token)
        {
            var pool = GetRoutePool(step);
            if (pool == null)
            {
                _log("[Path] Path step has no route configured. Failing.");
                CurrentPhase = BotPhase.Failed;
                return false;
            }

            var (route, routeIndex) = GetCurrentRoute(step, pool);
            _log($"[Path] Using route {routeIndex + 1}/{pool.Count} — '{route.PathFile}'.");

            var result = await RunTravelRouteAsync(step, route, _flowIndex + 1, _profile.FlowSteps.Count, token);
            if (token.IsCancellationRequested) return false;

            switch (result)
            {
                case TravelRouteRunResult.Completed:
                    _pathStepRetryCount = 0;
                    AdvanceRoute(step, pool);
                    _log("[Path] Step completed.");
                    return true;

                case TravelRouteRunResult.Cancelled:
                    return false;

                case TravelRouteRunResult.Incomplete:
                    _pathStepRetryCount++;
                    if (_pathStepRetryCount >= BotConstants.Repot.MaxMoveToRepotRetries)
                    {
                        _log($"[Path] Path did not complete {_pathStepRetryCount} times in a row. Giving up.");
                        _pathStepRetryCount = 0;
                        CurrentPhase = BotPhase.Failed;
                        return false;
                    }
                    _log($"[Path] Path did not complete (attempt {_pathStepRetryCount}/{BotConstants.Repot.MaxMoveToRepotRetries}). Restarting the flow.");
                    _flowIndex = 0;
                    return false;

                default:
                    // MissingSegment, ExpectedMapNotReached, UnexpectedMapReached, InvalidMapState
                    _log($"[Path] Path step failed ({result}).");

                    // If the player ended up in the city (e.g. teleported mid-route), do
                    // not fail the whole workflow — restart from the beginning of the flow.
                    if (_memoryService.GetIsInCity())
                    {
                        _log("[Path] Player is in the city — restarting the flow from the start.");
                        _flowIndex = 0;
                        return false;
                    }

                    CurrentPhase = BotPhase.Failed;
                    return false;
            }
        }

        /// <summary>
        /// Repot step: ensures the player is in the city (teleports if needed), walks to
        /// the repot point using the next path from the step's repot-path pool (cycling),
        /// then sells items and buys potions.
        /// </summary>
        private async Task<bool> ExecuteRepotStepAsync(BotFlowStep step, CancellationToken token)
        {
            // 1. Ensure in city.
            if (!_memoryService.GetIsInCity())
            {
                _log("[Repot] Not in city. Teleporting before repot.");
                await TeleportToCity(token);
                if (!_memoryService.GetIsInCity())
                {
                    _teleportRetryCount++;
                    if (_teleportRetryCount >= _profile.MaxTeleportRetries)
                    {
                        _log($"[Repot] Failed to reach city after {_profile.MaxTeleportRetries} teleport attempts. Giving up.");
                        _teleportRetryCount = 0;
                        CurrentPhase = BotPhase.Failed;
                        return false;
                    }
                    _log($"[Repot] Not in city after teleport (attempt {_teleportRetryCount}/{_profile.MaxTeleportRetries}). Retrying.");
                    return false; // stay on this step and retry
                }
                _teleportRetryCount = 0;
            }

            // 2. Walk to the repot point using the next repot path (cycling).
            var repotPaths = step.RepotPaths ?? new List<BotRouteStep>();
            if (repotPaths.Count == 0)
            {
                _log("[Repot] Repot step has no repot paths. Failing.");
                CurrentPhase = BotPhase.Failed;
                return false;
            }

            if (_repotPathIndex < 0 || _repotPathIndex >= repotPaths.Count)
                _repotPathIndex = 0;

            var repotPath = repotPaths[_repotPathIndex];
            _log($"[Repot] Walking to repot point using repot path {_repotPathIndex + 1}/{repotPaths.Count} — '{repotPath.PathFile}'.");

            var result = await RunRouteOnceAsync(repotPath, "Repot path", token);
            if (token.IsCancellationRequested) return false;

            if (result != RouteRunResult.Completed)
            {
                _moveToRepotRetryCount++;
                if (_moveToRepotRetryCount >= BotConstants.Repot.MaxMoveToRepotRetries)
                {
                    _log($"[Repot] Could not reach the repot point after {_moveToRepotRetryCount} tries (player likely stuck in the city). Giving up.");
                    _moveToRepotRetryCount = 0;
                    CurrentPhase = BotPhase.Failed;
                    return false;
                }
                _log($"[Repot] Could not reach the repot point (attempt {_moveToRepotRetryCount}/{BotConstants.Repot.MaxMoveToRepotRetries}). Retrying.");
                return false; // stay on this step and retry
            }
            _moveToRepotRetryCount = 0;

            // The next repot trip will use the next path in the pool.
            _repotPathIndex = (_repotPathIndex + 1) % repotPaths.Count;

            // 3. Perform the repot sequence.
            if (_profile.DryRunRepot)
            {
                _log("[Repot] DryRunRepot=true — skipping actual repot. Moving to the next flow step.");
                return true;
            }

            _log("[Repot] Performing repot sequence (sell + buy)...");
            try
            {
                _repotSystem.Repot();
                _log("[Repot] Repot completed.");
            }
            catch (Exception ex)
            {
                _log($"[Repot] Repot failed: {ex.Message}");
                CurrentPhase = BotPhase.Failed;
                return false;
            }

            var snapshot = _memoryService.GetSnapshot();
            _log($"[Repot] Post-repot: HP Pots: {snapshot.HpPotions}, Mana Pots: {snapshot.ManaPotions}");

            return true;
        }

        /// <summary>
        /// Operation step: runs the named custom operation with the runner's retry policy.
        /// </summary>
        private async Task<bool> ExecuteOperationStepAsync(BotFlowStep step, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(step.OperationName))
            {
                _log("[Operation] Operation step has no operation name. Failing.");
                CurrentPhase = BotPhase.Failed;
                return false;
            }

            if (!await RunOperationWithRetryAsync(step.OperationName, token))
            {
                _log($"[Operation] Operation '{step.OperationName}' failed after retries. Failing.");
                CurrentPhase = BotPhase.Failed;
                return false;
            }

            return true;
        }

        /// <summary>
        /// ExpLoop step: runs the looping hunting path (with loot collection) until the
        /// repot conditions are met or the player is detected in the city. Afterwards the
        /// bot returns to the city (if needed) and the flow restarts from the first step.
        /// When the step has a route group (<see cref="BotFlowStep.Routes"/>), the next
        /// route of the group is hunted on every flow cycle (rotating per cycle).
        /// </summary>
        private async Task<bool> ExecuteExpLoopStepAsync(BotFlowStep step, CancellationToken token)
        {
            var pool = GetRoutePool(step);
            if (pool == null)
            {
                _log("[ExpLoop] ExpLoop step has no route configured. Failing.");
                CurrentPhase = BotPhase.Failed;
                return false;
            }

            var (route, routeIndex) = GetCurrentRoute(step, pool);
            _log($"[ExpLoop] Using route {routeIndex + 1}/{pool.Count} — '{route.PathFile}'.");

            // Wait once before starting the looping route (not on every cycle).
            await WaitBeforeStepAsync(route.PathFile, route.StartDelayMs, "Exp Loop", token);
            if (token.IsCancellationRequested) return false;

            _log($"[ExpLoop] Loading hunting path '{route.PathFile}'...");
            var waypoints = _pathLoader.LoadSegment(route.PathFile);
            if (waypoints == null)
            {
                _log($"[ExpLoop] Missing required segment '{route.PathFile}'. Cannot proceed.");
                CurrentPhase = BotPhase.Failed;
                return false;
            }

            using var expCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var expToken = expCts.Token;

            var pathTask = _pathRunner.RunPathAsync(waypoints, loop: true, expToken);

            // The EXP route is a hunting route (MoveAndAttackAndLoot): run the loot
            // system in parallel so drops are collected while walking and after kills.
            LootSystem? lootSystem = null;
            Task? lootTask = null;
            if (waypoints.Any(w => w.Mode == BotMode.MoveAndAttackAndLoot))
            {
                lootSystem = new LootSystem(_memoryService, _log)
                {
                    LootPriorityMode = _profile.LootPriority
                };

                // Give the movement system a reference to the loot system so it can hold
                // waypoint advancement until a full loot scan pass finishes after a kill.
                if (_pathRunner.CurrentMovement != null)
                {
                    _pathRunner.CurrentMovement.LootSystemRef = lootSystem;
                    _pathRunner.CurrentMovement.LootPriorityMode = _profile.LootPriority;
                }

                lootTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!expToken.IsCancellationRequested && lootSystem != null)
                        {
                            await lootSystem.Update(expToken);
                            await Task.Delay(BotConstants.Delays.LootUpdateMs, expToken);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        _log($"[ExpLoop] Loot system error: {ex.Message}");
                    }
                }, expToken);
                _log("[ExpLoop] Loot system started (MoveAndAttackAndLoot route).");
                if (_profile.LootPriority)
                    _log("[ExpLoop] LOOT PRIORITY MODE ON — looting outranks combat; attack and waypoint movement are suspended while loot is being scanned/collected.");
            }

            bool repotNeeded = false;
            bool cityDetected = false;
            int inCityConsecutiveReads = 0;
            try
            {
                while (!expToken.IsCancellationRequested)
                {
                    await Task.Delay(BotConstants.Delays.ExpLoopRepotCheckIntervalMs, expToken);

                    var snapshot = _memoryService.GetSnapshot();

                    // Unexpected city teleport (manual teleport, death, portal, ...) while
                    // hunting: the player is in the city, so the bot must stop hunting and
                    // go repot instead of fighting inside the city forever.
                    if (snapshot.IsInCity)
                    {
                        inCityConsecutiveReads++;
                        if (inCityConsecutiveReads >= BotConstants.Delays.InCityDetectionStableReads)
                        {
                            _log("[ExpLoop] Player detected in city — stopping exp loop.");
                            cityDetected = true;
                            expCts.Cancel();
                            break;
                        }
                    }
                    else
                    {
                        inCityConsecutiveReads = 0;
                    }

                    if (_repotDetector.NeedsRepot(snapshot))
                    {
                        _log("[ExpLoop] Repot condition detected. Stopping exp loop.");
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

            // Stop the loot loop with the step — it must not run during repot/teleport.
            if (!expCts.IsCancellationRequested)
                expCts.Cancel();
            if (lootTask != null)
            {
                try
                {
                    await lootTask;
                }
                catch (OperationCanceledException) { }
            }

            _pathRunner.Stop();
            _log("[ExpLoop] Exp hunting loop ended.");

            if (token.IsCancellationRequested)
                return false;

            // The flow simply advances to the next step (and wraps around at the end).
            // The Repot step is responsible for returning to the city and refilling, so a
            // flow like Repot → ... → ExpLoop → Operation (after hunt) → Repot works:
            // after hunting, any following steps run, and the cycle returns to Repot.
            AdvanceRoute(step, pool);
            _log("[ExpLoop] Advancing to the next flow step.");
            return true;
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

        /// <summary>Maps a flow step type to the phase shown in the UI.</summary>
        private static BotPhase PhaseForStep(BotFlowStep step)
        {
            switch (step.Type)
            {
                case BotFlowStepType.Path: return BotPhase.PathStep;
                case BotFlowStepType.Repot: return BotPhase.Repot;
                case BotFlowStepType.Operation: return BotPhase.OperationStep;
                case BotFlowStepType.ExpLoop: return BotPhase.ExpLoop;
                default: return BotPhase.Failed;
            }
        }

        /// <summary>Human-readable description of a flow step, used in logs and the UI.</summary>
        private static string DescribeStep(BotFlowStep step)
        {
            switch (step.Type)
            {
                case BotFlowStepType.Path:
                    string mode = step.CompletionMode == TravelRouteCompletionMode.ExpectedMapReached
                        ? $"until map {step.ExpectedDestinationMapNumber}"
                        : "until last waypoint";
                    if (step.Routes != null && step.Routes.Count > 0)
                        return $"Path group ({step.Routes.Count} routes, {mode})";
                    return $"Path '{step.PathFile}' ({mode})";
                case BotFlowStepType.Repot:
                    return $"Repot ({step.RepotPaths?.Count ?? 0} repot paths)";
                case BotFlowStepType.Operation:
                    return $"Operation '{step.OperationName}'";
                case BotFlowStepType.ExpLoop:
                    if (step.Routes != null && step.Routes.Count > 0)
                        return $"ExpLoop group ({step.Routes.Count} routes)";
                    return $"ExpLoop '{step.PathFile}'";
                default:
                    return step.Type.ToString();
            }
        }

        /// <summary>
        /// Returns the route pool of a Path/ExpLoop step: the step's route group
        /// (<see cref="BotFlowStep.Routes"/>) when configured, otherwise the single route
        /// derived from <see cref="BotFlowStep.PathFile"/> / <see cref="BotFlowStep.StartDelayMs"/>.
        /// Returns null when no route is configured at all.
        /// </summary>
        private static List<BotRouteStep>? GetRoutePool(BotFlowStep step)
        {
            if (step.Routes != null && step.Routes.Count > 0)
                return step.Routes;

            if (string.IsNullOrWhiteSpace(step.PathFile))
                return null;

            return new List<BotRouteStep>
            {
                new BotRouteStep { PathFile = step.PathFile, StartDelayMs = step.StartDelayMs }
            };
        }

        /// <summary>
        /// Picks the next route of a step's route group without advancing the rotation.
        /// Returns the route and its zero-based index for logging.
        /// </summary>
        private (BotRouteStep Route, int Index) GetCurrentRoute(BotFlowStep step, List<BotRouteStep> pool)
        {
            int index = _stepRouteIndex.TryGetValue(step, out int i) ? i : 0;
            if (index < 0 || index >= pool.Count)
                index = 0;
            return (pool[index], index);
        }

        /// <summary>
        /// Advances the rotation of a step's route group so the NEXT execution of the
        /// step uses the following route (wrapping around at the end).
        /// </summary>
        private void AdvanceRoute(BotFlowStep step, List<BotRouteStep> pool)
        {
            int index = _stepRouteIndex.TryGetValue(step, out int i) ? i : 0;
            _stepRouteIndex[step] = (index + 1) % pool.Count;
        }

        /// <summary>
        /// Waits the configured startup delay of a step, if any.
        /// Zero (or an invalid negative) delay returns immediately.
        /// </summary>
        private async Task WaitBeforeStepAsync(string pathFile, int startDelayMs, string stepName, CancellationToken token)
        {
            if (startDelayMs <= 0) return;

            _log($"[Coordinator] {stepName}: waiting {startDelayMs} ms before start...");
            await Task.Delay(startDelayMs, token);
        }

        /// <summary>
        /// Runs a named custom operation with the runner's retry policy.
        /// Returns true when the operation ultimately succeeded.
        /// </summary>
        private Task<bool> RunOperationWithRetryAsync(string operationName, CancellationToken token)
            => _operationRunner.RunWithRetryAsync(operationName, token);

        /// <summary>
        /// Runs one non-loop BotRouteStep (e.g. one repot path): waits the configured
        /// startup delay, loads the segment via SavedPathLoader and runs it once.
        /// </summary>
        private async Task<RouteRunResult> RunRouteOnceAsync(BotRouteStep step, string stageName, CancellationToken token)
        {
            if (step == null || string.IsNullOrWhiteSpace(step.PathFile))
            {
                _log($"[Coordinator] {stageName}: no path configured. Cannot proceed.");
                return RouteRunResult.MissingSegment;
            }

            await WaitBeforeStepAsync(step.PathFile, step.StartDelayMs, stageName, token);
            if (token.IsCancellationRequested) return RouteRunResult.Incomplete;

            _log($"[Coordinator] {stageName} — loading path '{step.PathFile}'...");
            var waypoints = _pathLoader.LoadSegment(step.PathFile);
            if (waypoints == null)
            {
                _log($"[Coordinator] {stageName}: Missing required segment '{step.PathFile}'. Cannot proceed.");
                return RouteRunResult.MissingSegment;
            }

            bool completed = await _pathRunner.RunPathAsync(waypoints, loop: false, token);
            if (completed)
                _log($"[Coordinator] {stageName}: path completed.");
            else
                _log($"[Coordinator] {stageName}: path did not complete.");

            return completed ? RouteRunResult.Completed : RouteRunResult.Incomplete;
        }

        /// <summary>
        /// Executes one Path flow step. FinalWaypoint steps complete through normal
        /// non-loop path completion; ExpectedMapReached steps complete when the
        /// configured destination map is stably detected, without requiring the final waypoint.
        /// </summary>
        private async Task<TravelRouteRunResult> RunTravelRouteAsync(
            BotFlowStep step,
            BotRouteStep route,
            int stepIndex,
            int stepCount,
            CancellationToken token)
        {
            try
            {
                return await RunTravelRouteCoreAsync(step, route, stepIndex, stepCount, token);
            }
            catch (OperationCanceledException)
            {
                return TravelRouteRunResult.Cancelled;
            }
        }

        private async Task<TravelRouteRunResult> RunTravelRouteCoreAsync(
            BotFlowStep step,
            BotRouteStep route,
            int stepIndex,
            int stepCount,
            CancellationToken token)
        {
            if (step == null || route == null || string.IsNullOrWhiteSpace(route.PathFile))
            {
                _log($"[Path] Step {stepIndex}/{stepCount}: no path configured. Cannot proceed.");
                return TravelRouteRunResult.MissingSegment;
            }

            // Startup delay (cancellable).
            if (route.StartDelayMs > 0)
            {
                _log($"[Path] Step {stepIndex}/{stepCount}: waiting {route.StartDelayMs} ms before start...");
                await Task.Delay(route.StartDelayMs, token);
            }

            _log($"[Path] Step {stepIndex}/{stepCount}:");
            _log($"  Path='{route.PathFile}'");
            _log($"  Completion={step.CompletionMode}");
            if (step.CompletionMode == TravelRouteCompletionMode.ExpectedMapReached)
                _log($"  DestinationMap={step.ExpectedDestinationMapNumber}");

            var waypoints = _pathLoader.LoadSegment(route.PathFile);
            if (waypoints == null)
            {
                _log($"[Path] Step {stepIndex}/{stepCount}: Missing required segment '{route.PathFile}'. Cannot proceed.");
                return TravelRouteRunResult.MissingSegment;
            }

            if (step.CompletionMode != TravelRouteCompletionMode.ExpectedMapReached)
            {
                // FinalWaypoint: normal non-loop execution.
                bool completed = await _pathRunner.RunPathAsync(waypoints, loop: false, token);
                if (token.IsCancellationRequested) return TravelRouteRunResult.Cancelled;

                if (completed)
                    _log($"[Path] Step {stepIndex}/{stepCount}: path completed (final waypoint).");
                else
                    _log($"[Path] Step {stepIndex}/{stepCount}: path did not complete.");

                return completed ? TravelRouteRunResult.Completed : TravelRouteRunResult.Incomplete;
            }

            // ExpectedMapReached: portal-aware execution.
            return await RunMapTransitionRouteAsync(step, stepIndex, stepCount, waypoints, token);
        }

        /// <summary>
        /// Executes one portal route: runs the path while polling the map number, stops
        /// movement as soon as the expected destination map is confirmed, and settles the
        /// map/player-position reads before the next step starts.
        /// </summary>
        private async Task<TravelRouteRunResult> RunMapTransitionRouteAsync(
            BotFlowStep step,
            int stepIndex,
            int stepCount,
            List<Waypoint> waypoints,
            CancellationToken token)
        {
            int expectedMap = step.ExpectedDestinationMapNumber;

            // Wait for a valid nonzero source map before starting.
            int sourceMap = await WaitForValidMapAsync(BotConstants.Delays.ValidMapReadTimeoutMs, token);
            if (sourceMap == 0)
            {
                _log($"[Path] Step {stepIndex}/{stepCount}: no valid source map read within {BotConstants.Delays.ValidMapReadTimeoutMs} ms. Failing.");
                return TravelRouteRunResult.InvalidMapState;
            }

            _log($"[Path] Step {stepIndex}/{stepCount}: source map {sourceMap}.");

            // Already on the destination map: do not start its path, but still require
            // the destination map and player position to settle before the next step starts.
            if (sourceMap == expectedMap)
            {
                _log(
                    $"[Path] Step {stepIndex}/{stepCount}: " +
                    $"already on destination map {expectedMap}. Verifying settlement.");

                return await WaitForMapSettlementAsync(
                    expectedMap,
                    stepIndex,
                    stepCount,
                    token);
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
                        _log($"[Path] Step {stepIndex}/{stepCount}: destination map {expectedMap} confirmed. Stopping movement.");
                        destinationConfirmed = true;
                        _pathRunner.Stop();
                        routeCts.Cancel();
                        try { await pathTask; } catch (OperationCanceledException) { }
                    }
                }
                else if (map != sourceMap)
                {
                    _log($"[Path] Step {stepIndex}/{stepCount}: unexpected map {map} (expected {expectedMap}). Stopping route.");
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
                return await WaitForMapSettlementAsync(expectedMap, stepIndex, stepCount, token);

            // The path finished before the expected destination map was confirmed:
            // give the portal a bounded grace period to activate.
            _log($"[Path] Step {stepIndex}/{stepCount}: path finished before destination map confirmed. Waiting grace period for portal transition...");
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
                        _log($"[Path] Step {stepIndex}/{stepCount}: destination map {expectedMap} confirmed during grace period.");
                        return await WaitForMapSettlementAsync(expectedMap, stepIndex, stepCount, token);
                    }
                }
                else if (map != sourceMap)
                {
                    _log($"[Path] Step {stepIndex}/{stepCount}: unexpected map {map} during grace period (expected {expectedMap}).");
                    return TravelRouteRunResult.UnexpectedMapReached;
                }
                else
                {
                    graceConsecutiveReads = 0;
                }
            }

            _log($"[Path] Step {stepIndex}/{stepCount}: still on source map {sourceMap} after grace period. Expected map {expectedMap} not reached.");
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
        /// (two consecutive polls) so the next step never initializes while the player
        /// pointer or coordinates are temporarily unavailable during loading.
        /// </summary>
        private async Task<TravelRouteRunResult> WaitForMapSettlementAsync(
            int expectedMap,
            int stepIndex,
            int stepCount,
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
                        _log($"[Path] Step {stepIndex}/{stepCount}: destination map {expectedMap} settled (map and player position stable).");
                        return TravelRouteRunResult.Completed;
                    }
                }
                else
                {
                    stableReads = 0;
                }
            }

            _log($"[Path] Step {stepIndex}/{stepCount}: destination map {expectedMap} did not settle within {BotConstants.Delays.MapTransitionSettleTimeoutMs} ms.");
            return TravelRouteRunResult.InvalidMapState;
        }

        /// <summary>
        /// Start-position protection gate, run once when the workflow starts.
        ///
        /// When the profile has <see cref="BotProfile.EnableStartPositionCheck"/> disabled
        /// this returns immediately and the flow starts normally.
        ///
        /// When enabled and the player is NOT standing on the profile's start coordinates
        /// (<see cref="BotProfile.StartPositionX"/>/<see cref="BotProfile.StartPositionY"/>,
        /// within <see cref="BotProfile.StartPositionTolerance"/> tiles), the bot uses the
        /// town teleport scroll, waits for the game to settle
        /// (<see cref="BotConstants.Delays.PostTeleportUiLoadMs"/> — the ~10 s UI load
        /// wait), then taps W very briefly so the game refreshes the (stale, pre-teleport)
        /// position memory, and immediately verifies (fast polls, ~100 ms apart) that the
        /// current map matches the profile's protected map
        /// (<see cref="BotProfile.ProtectionMapNumber"/>) AND that the player is back on
        /// the start coordinates (within the tolerance). When the values are correct the
        /// bot proceeds in a blink of an eye; if the verification never passes within the
        /// fast window, the workflow stops (returns false).
        /// </summary>
        private async Task<bool> RunStartProtectionAsync(CancellationToken token)
        {
            if (!_profile.EnableStartPositionCheck)
                return true;

            int tolerance = Math.Max(0, _profile.StartPositionTolerance);

            var (x, y, posSuccess) = _memoryService.GetPlayerPosition();
            if (!posSuccess)
            {
                _log("[StartProtection] Cannot read the player position. Stopping.");
                return false;
            }

            if (Math.Abs(x - _profile.StartPositionX) <= tolerance &&
                Math.Abs(y - _profile.StartPositionY) <= tolerance)
            {
                _log($"[StartProtection] Player already on the start position ({x}, {y}). No teleport needed.");
                return true;
            }

            _log($"[StartProtection] Player at ({x}, {y}) — start position is ({_profile.StartPositionX}, {_profile.StartPositionY}) (tolerance {tolerance} tiles). Using the town teleport scroll.");
            await TeleportToCity(token);
            if (token.IsCancellationRequested) return false;

            // TeleportToCity already waits PostTeleportUiLoadMs (~10 s) for the game UI
            // to settle. The player position memory stays STALE (pre-teleport values)
            // until the player actually moves, so tap W very briefly to force the game
            // to refresh the coordinates — then verify immediately, no settle wait.
            _log("[StartProtection] Quick step tap to refresh the position memory...");
            await NudgeMoveAsync(token);
            if (token.IsCancellationRequested) return false;

            for (int attempt = 1; attempt <= BotConstants.Delays.StartProtectionVerifyAttempts; attempt++)
            {
                int map = _memoryService.GetMapNumber();
                var (vx, vy, verifyOk) = _memoryService.GetPlayerPosition();

                bool mapOk = _profile.ProtectionMapNumber <= 0 || map == _profile.ProtectionMapNumber;
                bool posOk = verifyOk &&
                             Math.Abs(vx - _profile.StartPositionX) <= tolerance &&
                             Math.Abs(vy - _profile.StartPositionY) <= tolerance;

                if (mapOk && posOk)
                {
                    _log($"[StartProtection] Protection OK — map {map}, position ({vx}, {vy}). Proceeding.");
                    return true;
                }

                _log($"[StartProtection] Verify attempt {attempt}/{BotConstants.Delays.StartProtectionVerifyAttempts} failed — map {map}, position ({vx}, {vy}).");
                if (attempt < BotConstants.Delays.StartProtectionVerifyAttempts)
                    await Task.Delay(BotConstants.Delays.StartProtectionRetryMs, token);
            }

            _log("[StartProtection] PROTECTION FAILED — the current map/position do not match the profile's protection. Stopping.");
            return false;
        }

        /// <summary>
        /// Taps the W key very briefly (like a real player taking a single step). Used to
        /// force the game to refresh the player position memory after a teleport (the
        /// read stays stale until the player moves).
        /// </summary>
        private async Task NudgeMoveAsync(CancellationToken token)
        {
            _focusGameWindow();

            _log($"[StartProtection] Tapping W for {BotConstants.Delays.StartProtectionNudgeKeyDownMs} ms...");
            keybd_event(BotConstants.Keyboard.VkW, BotConstants.Keyboard.ScanW, 0, 0);
            await Task.Delay(BotConstants.Delays.StartProtectionNudgeKeyDownMs, token);
            keybd_event(BotConstants.Keyboard.VkW, BotConstants.Keyboard.ScanW, KEYEVENTF_KEYUP, 0);
        }

        /// <summary>
        /// Teleports to the city with the profile's teleport key and waits for the game
        /// UI to settle afterwards.
        /// </summary>
        private async Task TeleportToCity(CancellationToken token)
        {
            byte vk = (byte)_profile.TeleportKey;
            byte scan = (byte)_profile.TeleportScanCode;

            // Make sure the game window is focused before injecting the teleport key,
            // otherwise keybd_event may deliver the keypress to another window.
            _focusGameWindow();

            _log($"[Teleport] Pressing key (vk={vk}) for town teleport...");
            keybd_event(vk, scan, 0, 0);
            await Task.Delay(BotConstants.Delays.TeleportKeyDownMs, token);
            keybd_event(vk, scan, KEYEVENTF_KEYUP, 0);

            bool arrived = false;
            for (int i = 0; i < BotConstants.Delays.TeleportWaitIterations; i++)
            {
                await Task.Delay(BotConstants.Delays.TeleportWaitIterationMs, token);
                if (_memoryService.GetIsInCity())
                {
                    _log("[Teleport] Arrived in city.");
                    arrived = true;
                    break;
                }
            }

            if (!arrived)
                _log("[Teleport] Teleport wait timeout — proceeding anyway.");

            // The game UI (loading screen → world render) needs time to finish after the
            // in-city flag becomes readable. Starting to move immediately makes the client
            // ignore input and the bot misreads the still-loading state as stuck (action=1).
            _log($"[Teleport] Waiting {BotConstants.Delays.PostTeleportUiLoadMs} ms for the game UI to load...");
            await Task.Delay(BotConstants.Delays.PostTeleportUiLoadMs, token);
        }
    }
}