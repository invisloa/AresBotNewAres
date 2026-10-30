using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(nint hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        private const int KEYEVENTF_KEYUP = BotConstants.Keyboard.KeyEventKeyUp;

        #endregion

        private readonly GameMemoryService _memoryService;
        private readonly RepotSystem _repotSystem;
        private readonly RepotDetectorService _repotDetector;
        private readonly SavedPathLoader _pathLoader;
        private readonly PathRunnerService _pathRunner;
        private readonly OperationRunnerService _operationRunner;
        private readonly PositionSyncService _positionSync;
        private readonly Action<string> _log;
        private readonly Action _focusGameWindow;

        private readonly BotProfile _profile;

        private CancellationTokenSource? _cts;
        private bool _isRunning;
        private volatile bool _stopRequested;
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

        /// <summary>
        /// One-shot startup-entry context produced by the map-based startup detection in
        /// <see cref="StartAsync"/>. It is keyed to the ONE flow step + route selected for
        /// the initial resume, and is consumed by the first execution of exactly that route
        /// (where the route's entrance-coordinate start protection is replaced by a
        /// map-aware validated-position check). It never survives into retries, later route
        /// executions, repots or workflow cycles: an unrelated step execution discards it.
        /// Only Path / ExpLoop steps use it — the Repot step has its own in-city validation.
        /// </summary>
        private StartupRouteEntry? _startupRouteEntry;

        /// <summary>
        /// Human-readable description of the map-based startup selection applied by the
        /// current run. Empty when the detection did not select an action.
        /// </summary>
        public string StartupSelectionText { get; private set; } = "";

        /// <summary>
        /// The one flow step / route selected by the map-based startup detection.
        /// </summary>
        private sealed record StartupRouteEntry(BotFlowStep Step, int RouteIndex, int StartMapNumber);

        /// <summary>Consecutive repot-path failures. After 3 the workflow fails
        /// visibly instead of retrying forever (e.g. the player cannot move in the city).</summary>
        private int _moveToRepotRetryCount;

        /// <summary>Consecutive Path-step failures. After 3 the workflow fails visibly.</summary>
        private int _pathStepRetryCount;

        /// <summary>
        /// How long the workflow stops after the in-city unstuck limit (2 attempts) before it
        /// resets the failure bookkeeping and restarts from the Repot step. Default 10 min;
        /// tests override it to keep the regression suite fast.
        /// </summary>
        public int InCityStuckResetDelayMs { get; set; } = BotConstants.Repot.InCityStuckResetDelayMs;

        private int _teleportRetryCount;

        /// <summary>
        /// Consecutive post-repot verification failures (potion counts below the
        /// profile buy targets after the shop buy). After the max the workflow
        /// fails visibly instead of retrying forever.
        /// </summary>
        private int _postRepotVerifyRetryCount;

        /// <summary>
        /// True when the last start-protection run ended by performing its configured
        /// recovery town teleport and validating the town destination on its own. The
        /// flow must then return through the existing Repot path instead of comparing
        /// town coordinates with the route start.
        /// </summary>
        private bool _startProtectionRecoveredToTown;

        /// <summary>
        /// PositionSync result of the LAST town teleport performed by
        /// <see cref="TeleportToCity"/>. The in-city recovery uses its destination status
        /// to distinguish a verified town destination (which can legitimately be near the
        /// stuck location on the same map) from an unverified W response at the stuck spot.
        /// </summary>
        private PositionSyncResult? _lastTownTeleportSync;

        /// <summary>
        /// Player-marker detections since the last complete EXP loop. First incident gets
        /// the shorter cooldown; repeated incidents before natural repot get 30 minutes.
        /// </summary>
        private int _playerMarkerInterruptionsSinceFullExpCycle;

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

        /// <summary>Fires whenever a full repot-&gt;exp cycle completes (with the new total).</summary>
        public Action<int>? OnCycleCompleted { get; set; }

        /// <summary>
        /// How many full repot-&gt;exp cycles the bot has completed in this run.
        /// Incremented each time an ExpLoop step finishes its hunting session and the
        /// flow advances (i.e. repot happened earlier in the flow, exp is now done).
        /// Resets on every workflow start (new coordinator instance per run).
        /// </summary>
        public int CompletedCycles { get; private set; }

        /// <summary>Fires when the workflow stops.</summary>
        public Action? OnStopped { get; set; }

        /// <summary>Called when the heal monitor confirms that the player has died.</summary>
        public Action? OnPlayerDeathDetected { get; set; }

        // ── Death recovery ──
        // A confirmed character death is a RECOVERABLE workflow event: the coordinator releases
        // inputs, keeps itself alive, waits five minutes, teleports to town and restarts the whole
        // profile. It is never routed into the global shutdown path.
        private readonly object _deathGate = new();
        private CancellationTokenSource? _deathCts;
        private bool _deathRecoveryRequested;
        private DateTime _deathDetectedAtUtc = DateTime.MinValue;
        private DateTime _deathRecoveryDueUtc = DateTime.MinValue;

        /// <summary>
        /// Death recovery wait (ms). Production uses the five-minute
        /// <see cref="BotConstants.DeathRecovery.WaitMs"/>; tests set a short value so the
        /// real workflow can be exercised without waiting.
        /// </summary>
        public int DeathRecoveryWaitMs { get; set; } = BotConstants.DeathRecovery.WaitMs;

        /// <summary>Additional revival recheck wait (ms) used while HP still reads zero.</summary>
        public int DeathRecoveryRevivalRecheckWaitMs { get; set; } = BotConstants.DeathRecovery.RevivalRecheckWaitMs;

        /// <summary>Poll interval (ms) between two zero-HP checks while waiting for revival.</summary>
        public int DeathRecoveryRevivalPollMs { get; set; } = BotConstants.DeathRecovery.RevivalPollMs;

        /// <summary>Number of town-teleport attempts performed by the death recovery.</summary>
        public int DeathRecoveryTownTeleportAttempts { get; set; } = BotConstants.DeathRecovery.TownTeleportAttempts;

        /// <summary>Delay (ms) between two town-teleport attempts performed by the death recovery.</summary>
        public int DeathRecoveryTownTeleportRetryDelayMs { get; set; } = BotConstants.DeathRecovery.TownTeleportRetryDelayMs;

        /// <summary>True once a confirmed death requested recovery (until the recovery completes).</summary>
        public bool IsDeathRecoveryRequested
        {
            get { lock (_deathGate) return _deathRecoveryRequested; }
        }

        /// <summary>
        /// True while the death recovery owns the workflow (requested or executing). Used by the
        /// host to suppress movement-derived "unresponsive game" shutdowns during the wait.
        /// </summary>
        public bool IsDeathRecoveryActive =>
            IsDeathRecoveryRequested || CurrentPhase == BotPhase.DeathRecovery;

        /// <summary>Reads the live UI setting for stopping on a confirmed other-player marker.</summary>
        public Func<bool>? ShouldStopOnOtherPlayerDetected { get; set; }

        /// <summary>Whether the coordinator is running.</summary>
        public bool IsRunning => _isRunning;

        public string MobGroupingStatus => _pathRunner.CurrentMovement?.MobGroupingStatus ?? "Idle";

        /// <summary>The active profile.</summary>
        public BotProfile ActiveProfile => _profile;

        /// <summary>
        /// Shared pause switch (set by MainViewModel). While paused the workflow holds
        /// its position: flow index, route rotation and retry counters are kept, so
        /// resume continues with the same step. Null = never paused.
        /// </summary>
        public BotPauseController? PauseController { get; set; }

        /// <summary>
        /// Test/extensibility seam for the town teleport. Production leaves this null and
        /// the real key injection + arrival wait runs. When set, it replaces ONLY the
        /// input/arrival phase of <see cref="TeleportToCity"/>; the post-teleport
        /// position synchronization still runs through
        /// <see cref="PositionSyncService"/>. Returns true when the town was reached.
        /// </summary>
        public Func<CancellationToken, Task<bool>>? TownTeleportOverride { get; set; }

        private bool _pauseLogged;

        /// <summary>
        /// Releases held movement/combat inputs immediately (called on Pause so the
        /// character halts instead of running while the loops suspend).
        /// </summary>
        public void RequestPauseInputRelease()
        {
            try { _pathRunner.CurrentMovement?.CancelMobGrouping("workflow paused"); } catch { }
            try { _pathRunner.CurrentMovement?.StopMoving(); } catch { }
            try { _pathRunner.CurrentMovement?.ReleaseCombatKeys(); } catch { }
        }

        /// <summary>
        /// Entry point for a confirmed character death (invoked by the workflow's own heal
        /// monitor; also callable by hosts). It only MARKS the death and stops the current child
        /// work — it never stops the coordinator. The workflow loop performs the recovery
        /// (wait → town teleport → restart the whole profile).
        ///
        /// Repeated observations of the same death are ignored so the countdown starts exactly
        /// once and no duplicate recovery task is created.
        /// </summary>
        public void NotifyPlayerDeathDetected()
        {
            if (!_isRunning)
            {
                _log("[DeathRecovery] Death reported while the workflow is not running — no recovery started.");
                return;
            }

            lock (_deathGate)
            {
                if (_deathRecoveryRequested)
                    return;

                _deathRecoveryRequested = true;
                _deathDetectedAtUtc = DateTime.UtcNow;
                _deathRecoveryDueUtc = _deathDetectedAtUtc.AddMilliseconds(Math.Max(0, DeathRecoveryWaitMs));

                // Cancel ONLY the current child work (movement/loot/exp route). The workflow
                // token is untouched, so the coordinator and the active profile survive.
                try { _deathCts?.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            _log("[DeathRecovery] Death confirmed.");

            // Release every bot input (3 / W / A / D) and stop the current child work. Each
            // step is independent so one failing cleanup can never keep a key held or prevent
            // the death recovery from taking over.
            try { _pathRunner.CurrentMovement?.CancelMobGrouping("death recovery"); }
            catch (Exception ex) { _log($"[DeathRecovery] Grouping release failed: {ex.Message}"); }
            try { _pathRunner.CurrentMovement?.StopMoving(); }
            catch (Exception ex) { _log($"[DeathRecovery] Movement key release failed: {ex.Message}"); }
            try { _pathRunner.CurrentMovement?.ReleaseCombatKeys(); }
            catch (Exception ex) { _log($"[DeathRecovery] Combat key release failed: {ex.Message}"); }
            try { _pathRunner.Stop(); }
            catch (Exception ex) { _log($"[DeathRecovery] Child work cancellation failed: {ex.Message}"); }
            _log("[DeathRecovery] Active actions released.");

            try
            {
                OnPlayerDeathDetected?.Invoke();
            }
            catch (Exception ex)
            {
                _log($"[DeathRecovery] External death handler failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Pause checkpoint: suspends the workflow in place until resumed. Flow state
        /// (step index, route rotation, retry counters) is preserved.
        /// </summary>
        private async Task WaitIfPausedAsync(CancellationToken token)
        {
            if (PauseController == null || !PauseController.IsPaused) return;
            if (!_pauseLogged)
            {
                _log("[Coordinator] Paused — holding workflow position.");
                _pauseLogged = true;
            }
            RequestPauseInputRelease();
            await PauseController.WaitIfPausedAsync(token);
            if (!token.IsCancellationRequested)
            {
                _log("[Coordinator] Resumed — continuing workflow.");
                _focusGameWindow();
            }
            _pauseLogged = false;
        }

        /// <summary>Delay whose countdown freezes while paused (see BotPauseController).</summary>
        private Task PausableDelayAsync(int millisecondsDelay, CancellationToken token)
            => PauseController != null
                ? PauseController.PausableDelayAsync(millisecondsDelay, token)
                : Task.Delay(millisecondsDelay, token);

        private Task PausableDelayAsync(TimeSpan delay, CancellationToken token)
        {
            long ms = (long)delay.TotalMilliseconds;
            if (ms < 0) ms = 0;
            if (ms > int.MaxValue) ms = int.MaxValue;
            return PausableDelayAsync((int)ms, token);
        }

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
            Action focusGameWindow,
            PositionSyncService? positionSync = null)
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
            _positionSync = positionSync ?? new PositionSyncService(
                memoryService, log, focusGameWindow,
                wKeyCameraController: pathRunner.WCameraController);

            // Apply profile thresholds to detector
            _repotDetector.MinHpPotions = profile.MinHpPotions;
            _repotDetector.MinManaPotions = profile.MinManaPotions;
            _repotDetector.MaxWeightRatio = profile.MaxWeightRatio;
            _repotDetector.MinHp = profile.MinHp;
            _repotDetector.MinMana = profile.MinMana;

            // Weight-based repot only applies with loot priority selected in the profile:
            // loot fills the bag up to the weight limit, so reaching it means it is time
            // to repot. Without loot priority the bot just exp's as the profile states
            // and weight never forces a repot.
            _repotDetector.WeightRepotEnabled = profile.LootPriority;

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
            _stopRequested = false;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _flowIndex = 0;
            _repotPathIndex = 0;
            _stepRouteIndex.Clear();
            _moveToRepotRetryCount = 0;
            _pathStepRetryCount = 0;
            _teleportRetryCount = 0;
            _postRepotVerifyRetryCount = 0;
            _playerMarkerInterruptionsSinceFullExpCycle = 0;
            _startupRouteEntry = null;
            StartupSelectionText = "";
            _positionSync.Reset();
            ResetDeathRecoveryState(createNewCancellationSource: true);

            _focusGameWindow();
            _log("[Coordinator] Workflow started.");
            _log($"[Coordinator] Profile: {_profile.Name}");
            LogFlowPlan();

            // ── Auto potion drinking ──
            // The workflow uses the profile's explicit thresholds, so the player-based
            // auto detection (60% max HP / 20% max mana) is disabled for this instance.
            var healSystem = new HealManaSystem(_memoryService, _log, usePlayerBasedThresholds: false)
            {
                // A confirmed death is handed to the coordinator's death recovery, never to a
                // global shutdown. The heal monitor latches one notification per death episode.
                PlayerDeathDetected = NotifyPlayerDeathDetected
            };
            HealManaSystem.Threshold1 = (short)Math.Clamp(_profile.MinHp, 0, short.MaxValue);
            HealManaSystem.Threshold2 = (short)Math.Clamp(_profile.MinMana, 0, short.MaxValue);
            _log($"[HealMana] Auto-drink enabled: HP < {HealManaSystem.Threshold1} → key 1, Mana < {HealManaSystem.Threshold2} → key 2.");

            Task? healTask = null;
            try
            {
                // ── Map-based startup action detection (exactly once per explicit start) ──
                // Only this StartAsync path runs the detection: normal loop iterations,
                // repot, recovery, pause/resume and flow wrapping never do.
                StartupEntryOutcome startupEntry = await ApplyStartupSelectionAsync(token);
                if (startupEntry != StartupEntryOutcome.Selected)
                {
                    if (!token.IsCancellationRequested)
                    {
                        _log("[Startup] The bot will not start: the startup action selection did not resolve a safe entry.");
                        CurrentPhase = BotPhase.Failed;
                    }
                    return;
                }

                // The map-based startup entry already validated the player's map and position
                // for the selected action, so the profile-wide spawn-point requirement is NOT
                // enforced for that entry: being away from the profile's first configured spawn
                // point must not trigger a destructive town teleport. The validated map +
                // position of the startup location are the mandatory gate instead, and the
                // route-level start checks keep running for every later entry.
                _log("[StartProtection] Map-based startup entry in effect — the profile-wide start position check is not enforced for this initial action " +
                     "(the observed map and position were validated for the selected action instead). Normal route protection stays active.");

                healTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!token.IsCancellationRequested && healSystem != null)
                        {
                            if (PauseController?.IsPaused == true)
                            {
                                await PauseController.WaitIfPausedAsync(token);
                                if (token.IsCancellationRequested) break;
                                continue;
                            }
                            await healSystem.Update(token);
                            await PausableDelayAsync(100, token); // Update rate for heal/mana
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
            catch (BotStopRequestedException ex)
            {
                // A service requested an immediate stop (e.g. the game window is no
                // longer the selected window during an NPC scan). Same result as the
                // user pressing Stop — the finally block performs the cleanup.
                _log($"[Coordinator] Bot stop requested: {ex.Message}");
                CurrentPhase = BotPhase.Stopping;
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
                _pathRunner.WCameraController?.EndSession();
                _isRunning = false;
                if (IsDeathRecoveryRequested)
                {
                    // A stop while the death recovery owns the workflow is authoritative: the
                    // countdown is cancelled and nothing restarts automatically. The state is
                    // always cleared so a later start begins from a clean latch.
                    ResetDeathRecoveryState(createNewCancellationSource: false);
                    if (!_stopRequested)
                        _log("[DeathRecovery] Death recovery cancelled by the workflow stop.");
                }
                if (!_stopRequested && CurrentPhase != BotPhase.Failed && CurrentPhase != BotPhase.Stopping)
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
            // Latch the workflow stop before cancelling; the runner's finally must not
            // race the phase back to Idle while movement/input cleanup is underway.
            _stopRequested = true;
            // Cancel and latch movement before logging/phase notifications can hand
            // control to another callback. Recovery input is unable to start after this point.
            _cts?.Cancel();
            _pathRunner.Stop();
            CurrentPhase = BotPhase.Stopping;
            _log("[Coordinator] Stopping...");
            _pathRunner.WCameraController?.EndSession();
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
        /// <summary>
        /// Resets the death-recovery latch and, when requested, creates a FRESH death-cancellation
        /// source for the next episode. The workflow token is never reused after it was cancelled
        /// for a death, and a cancelled death token is never reused for the subsequent workflow.
        /// </summary>
        private void ResetDeathRecoveryState(bool createNewCancellationSource)
        {
            lock (_deathGate)
            {
                _deathRecoveryRequested = false;
                _deathDetectedAtUtc = DateTime.MinValue;
                _deathRecoveryDueUtc = DateTime.MinValue;

                CancellationTokenSource? previous = _deathCts;
                _deathCts = createNewCancellationSource ? new CancellationTokenSource() : null;
                try { previous?.Dispose(); }
                catch (ObjectDisposedException) { }
            }
        }

        /// <summary>Token cancelled when a confirmed death must abort the current child work.</summary>
        private CancellationToken DeathRecoveryCancellationToken
        {
            get { lock (_deathGate) return _deathCts?.Token ?? CancellationToken.None; }
        }

        /// <summary>
        /// Reads the verified death/revival state: returns true when a trustworthy answer exists
        /// and reports whether the character is alive. A zero HP read with an unreadable max HP
        /// is NOT treated as a confirmed death.
        /// </summary>
        private bool TryReadPlayerVitals(out bool alive, out string detail)
        {
            alive = false;
            var heal = _memoryService.GetHealManaValues();
            MaxHpManaReadResult maxStats = _memoryService.GetMaxHpMana();

            if (!heal.value1.HasValue)
            {
                detail = "current HP read failed";
                return false;
            }

            if (!maxStats.MaxHpReadSucceeded || maxStats.MaxHp <= 0)
            {
                detail = $"current HP={heal.value1.Value} but max HP is unreadable";
                return false;
            }

            alive = heal.value1.Value > 0;
            detail = $"HP={heal.value1.Value}/{maxStats.MaxHp}";
            return true;
        }

        /// <summary>
        /// Recoverable death workflow (the single authoritative owner of death recovery):
        ///   wait → verify revival/session → town teleport via the existing mechanism → verify
        ///   town arrival → reset the ACTIVE profile to its first action → continue the normal
        ///   flow (Repot → paths → ExpLoop).
        /// It never resumes the pre-death combat/waypoint queue and it never invokes the global
        /// stop. While the character still reads dead, no teleport and no profile restart is
        /// reported as successful (bounded, explicitly logged waits only).
        /// </summary>
        private async Task RunDeathRecoveryAsync(CancellationToken token)
        {
            DateTime detectedAt;
            DateTime dueAt;
            lock (_deathGate)
            {
                detectedAt = _deathDetectedAtUtc;
                dueAt = _deathRecoveryDueUtc;
            }
            if (detectedAt == DateTime.MinValue)
                detectedAt = DateTime.UtcNow;
            if (dueAt == DateTime.MinValue)
                dueAt = detectedAt.AddMilliseconds(Math.Max(0, DeathRecoveryWaitMs));

            CurrentPhase = BotPhase.DeathRecovery;
            CurrentStepText = "Death recovery · waiting for revival, then town teleport and profile restart";

            _log($"[DeathRecovery] Waiting {Math.Max(0, DeathRecoveryWaitMs) / 1000.0:F0} seconds before restart " +
                 $"(detected {detectedAt:HH:mm:ss}, recovery due {dueAt:HH:mm:ss}).");

            try
            {
                await PausableDelayAsync(Math.Max(0, DeathRecoveryWaitMs), token);
                _log("[DeathRecovery] Wait completed.");

                // 1. Verify the game/session is accessible and the character is actually revived
                //    before any game input is issued. Still-dead/unreadable states are handled
                //    explicitly with bounded waits — never with combat and never with a teleport.
                int rechecks = 0;
                int extraWaits = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();

                    bool readable = TryReadPlayerVitals(out bool alive, out string vitals);
                    if (readable && alive)
                    {
                        _log($"[DeathRecovery] Player revived ({vitals}) — proceeding with the recovery sequence.");
                        break;
                    }

                    string state = readable ? vitals : $"unreadable: {vitals}";

                    int waitMs;
                    if (rechecks < BotConstants.DeathRecovery.MaxRevivalRecheckRounds)
                    {
                        waitMs = rechecks == 0
                            ? Math.Max(0, DeathRecoveryRevivalPollMs)
                            : Math.Max(0, DeathRecoveryRevivalRecheckWaitMs);
                        rechecks++;
                        _log($"[DeathRecovery] Player is still dead after the wait ({state}) — waiting " +
                             $"{waitMs / 1000.0:F0}s for revival (recheck {rechecks}/{BotConstants.DeathRecovery.MaxRevivalRecheckRounds}).");
                    }
                    else
                    {
                        extraWaits++;
                        if (extraWaits > BotConstants.DeathRecovery.MaxAdditionalDeathWaits)
                        {
                            _log("[DeathRecovery] Revival was not observed after every bounded wait. No town teleport and no " +
                                 "profile restart is performed while the character still reads dead; the workflow stays alive. " +
                                 "Manual check/restart required.");
                            ResetDeathRecoveryState(createNewCancellationSource: true);
                            CurrentPhase = BotPhase.Failed;
                            return;
                        }

                        waitMs = Math.Max(0, DeathRecoveryWaitMs);
                        _log($"[DeathRecovery] Player is still dead ({state}) — staying in death recovery; " +
                             $"waiting another {waitMs / 1000.0:F0}s (extra wait {extraWaits}/{BotConstants.DeathRecovery.MaxAdditionalDeathWaits}).");
                    }

                    await PausableDelayAsync(waitMs, token);
                }

                // 2. Town teleport through the existing mechanism (profile teleport key = 6).
                bool arrivedInCity;
                if (_memoryService.GetIsInCity())
                {
                    _log("[DeathRecovery] Player is already in the city — skipping the town teleport.");
                    arrivedInCity = true;
                }
                else
                {
                    arrivedInCity = false;
                    for (int attempt = 1; attempt <= DeathRecoveryTownTeleportAttempts; attempt++)
                    {
                        token.ThrowIfCancellationRequested();
                        _log($"[DeathRecovery] Town teleport attempt {attempt}/{DeathRecoveryTownTeleportAttempts} " +
                             $"using teleport key {_profile.TeleportKey}.");

                        bool positionValidated = await TeleportToCity(token);
                        if (token.IsCancellationRequested)
                            return;

                        bool inCity = _memoryService.GetIsInCity();
                        if (positionValidated && inCity)
                        {
                            arrivedInCity = true;
                            _log("[DeathRecovery] Town arrival verified.");
                            break;
                        }

                        _log($"[DeathRecovery] Town arrival NOT verified (PositionValidated={positionValidated}, InCity={inCity}) " +
                             $"— attempt {attempt}/{DeathRecoveryTownTeleportAttempts} failed.");
                        if (attempt < DeathRecoveryTownTeleportAttempts)
                        {
                            await PausableDelayAsync(DeathRecoveryTownTeleportRetryDelayMs, token);
                        }
                    }
                }

                if (!arrivedInCity)
                {
                    _log("[DeathRecovery] Town teleport could not be verified — the profile is NOT restarted and no combat is started. " +
                         "Manual check required.");
                    ResetDeathRecoveryState(createNewCancellationSource: true);
                    CurrentPhase = BotPhase.Failed;
                    return;
                }

                // 3. Restart the COMPLETE active profile from its first action. The pre-death
                //    combat context and waypoint queue are discarded (PathRunner already stopped
                //    the MovementSystem; the next step creates a fresh one from its route file).
                //    The user-start map detection is deliberately NOT used here.
                _flowIndex = 0;
                _repotPathIndex = 0;
                _stepRouteIndex.Clear();
                _startupRouteEntry = null;
                _startProtectionRecoveredToTown = false;
                _moveToRepotRetryCount = 0;
                _pathStepRetryCount = 0;
                _teleportRetryCount = 0;
                _postRepotVerifyRetryCount = 0;
                _playerMarkerInterruptionsSinceFullExpCycle = 0;

                int stepCount = _profile.FlowSteps?.Count ?? 0;
                string firstStep = stepCount > 0 ? DescribeStep(_profile.FlowSteps[0]) : "(no flow steps)";
                _log($"[DeathRecovery] Profile '{_profile.Name}' reset to its first action (1/{stepCount}: {firstStep}). " +
                     "The normal repot/path/exp flow runs again; the pre-death waypoint queue is not resumed.");

                // Fresh death-cancellation source for the next episode; the workflow token itself
                // was never cancelled, so the coordinator stays alive.
                ResetDeathRecoveryState(createNewCancellationSource: true);
                CurrentPhase = BotPhase.Idle;
                _log("[DeathRecovery] Recovery complete — restarting the workflow.");
            }
            catch (OperationCanceledException)
            {
                // Manual stop (or a global stop) during the countdown: no automatic restart.
                _log("[DeathRecovery] Recovery interrupted by stop/cancellation — no automatic restart.");
            }
        }

        private async Task RunWorkflowLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                await WaitIfPausedAsync(token);
                if (token.IsCancellationRequested) break;

                if (CurrentPhase == BotPhase.Stopping || _stopRequested)
                    break;

                // ── Recoverable death recovery (highest priority) ──
                // The current child work was already cancelled by NotifyPlayerDeathDetected.
                // The coordinator stays alive: wait, verify revival, teleport to town and restart
                // the whole active profile from its first action.
                if (IsDeathRecoveryRequested)
                {
                    await RunDeathRecoveryAsync(token);
                    if (token.IsCancellationRequested) break;
                    continue;
                }

                if (CurrentPhase == BotPhase.Failed)
                {
                    _log("[Coordinator] Bot in Failed state. Manual restart required.");
                    await PausableDelayAsync(BotConstants.Delays.FailedStateMs, token);
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

                // A death during the step makes its result meaningless: never advance the flow
                // or count a failure — the loop top performs the death recovery instead.
                if (IsDeathRecoveryRequested)
                    continue;

                if (advance)
                {
                    _flowIndex++;
                }
                // When !advance the step already moved _flowIndex itself (e.g. a failed
                // Path step restarted the flow from the beginning, or the step failed).

                await PausableDelayAsync(BotConstants.Delays.WorkflowMainLoopMs, token);
            }
        }

        // ======================== Flow Step Execution ========================

        /// <summary>
        /// Executes one flow step. Returns true when the flow should advance to the next
        /// step; false when the step handled the flow position itself (restart/failure).
        /// </summary>
        private async Task<bool> ExecuteFlowStepAsync(BotFlowStep step, CancellationToken token)
        {
            await WaitIfPausedAsync(token);
            if (token.IsCancellationRequested) return false;

            // The startup entry exception belongs to exactly one route execution. If the flow
            // position points somewhere else, drop it instead of carrying it forward.
            DropForeignStartupRouteEntry(step);

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

            if (IsStartupRouteEntry(step, routeIndex))
            {
                // Initial resume of the map the user started the bot on: map + validated
                // position only, the configured entrance coordinates are not required.
                var startupEntry = _startupRouteEntry!;
                if (!await EnsureStartupRouteEntryValidatedAsync(route, startupEntry.StartMapNumber, "Path", token))
                {
                    CurrentPhase = BotPhase.Failed;
                    return false;
                }
                ConsumeStartupRouteEntry(step, routeIndex);
            }
            else if (!await RunRouteStartProtectionAsync(route, token))
            {
                HandleRouteStartProtectionFailure("Path step");
                return false;
            }

            var result = await RunTravelRouteAsync(step, route, _flowIndex + 1, _profile.FlowSteps.Count, token);
            if (token.IsCancellationRequested) return false;

            // A confirmed death during the route must not be recorded as a path failure: the
            // workflow loop performs the death recovery from the pre-death profile position.
            if (IsDeathRecoveryRequested) return false;

            switch (result)
            {
                case TravelRouteRunResult.Completed:
                    _pathStepRetryCount = 0;
                    AdvanceRoute(step, pool);
                    _log("[Path] Step completed.");
                    return true;

                case TravelRouteRunResult.Cancelled:
                    return false;

                case TravelRouteRunResult.WaypointRepotRequested:
                    if (!RedirectToRepotStep("waypoint special recovery requested Repot"))
                        return false;
                    return false;

                case TravelRouteRunResult.NavigationFailure:
                    // In the city the unstuck limit is only 2 attempts: do not redirect into
                    // back-to-back repot retries — stop, wait 10 min, reset and start again.
                    if (_memoryService.GetIsInCity())
                    {
                        await WaitOutInCityStuckAndResetAsync("Path step", token);
                        return false;
                    }
                    _log("[NavigationFailure] Route stopped after repeated failure to reach its waypoint; routing through the existing Repot step.");
                    RedirectToRepotStep("navigation failure escalation");
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
            // A pending transition means a previous teleport/portal changed the map but
            // its coordinates were not validated yet. Never walk to the repot point with
            // data whose freshness is unproven.
            // A failed town-teleport attempt deliberately leaves PositionSync pending.
            // On the bounded retry, do not spend the attempt validating the old pending
            // transition before issuing the next teleport; TeleportToCity marks a fresh
            // transition and validates that attempt before any repot-path movement.
            if (_teleportRetryCount == 0 &&
                !await EnsurePendingTransitionResolvedAsync("Repot step", token))
                return false;

            // 1. Ensure in city and that the prior town transition, if any, was
            // position-validated. An InCity flag alone cannot release a pending sync.
            bool retryingUnvalidatedTownArrival =
                _teleportRetryCount > 0 && _positionSync.HasPendingTransition;
            if (!_memoryService.GetIsInCity() || retryingUnvalidatedTownArrival)
            {
                _log(retryingUnvalidatedTownArrival
                    ? "[Repot] Town flag was seen but PositionSync is still pending; retrying town teleport/validation before any movement."
                    : "[Repot] Not in city. Teleporting before repot.");
                CaptureLowHpPotionsScreenshotIfNeeded();
                bool teleportPositionReady = await TeleportToCity(token);
                if (token.IsCancellationRequested)
                    return false;

                bool arrivedInCity = teleportPositionReady && _memoryService.GetIsInCity();
                _log($"[Teleport] Navigation recovery arrival result: PositionSyncReady={teleportPositionReady} InCity={_memoryService.GetIsInCity()}.");
                if (!arrivedInCity)
                {
                    _teleportRetryCount++;
                    if (_teleportRetryCount >= _profile.MaxTeleportRetries)
                    {
                        _log($"[Repot] Town teleport/arrival validation failed after {_teleportRetryCount}/{_profile.MaxTeleportRetries} attempts. Giving up and stopping the workflow.");
                        _teleportRetryCount = 0;
                        CurrentPhase = BotPhase.Failed;
                        return false;
                    }
                    _log($"[Repot] Town teleport not validated (PositionSyncReady={teleportPositionReady}, InCity={_memoryService.GetIsInCity()}); bounded retry {_teleportRetryCount}/{_profile.MaxTeleportRetries}.");
                    return false; // stay on this step and retry
                }

                _log("[NavigationFailure] Town arrival validated; continuing the existing Repot workflow.");
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

            if (!await RunRouteStartProtectionAsync(repotPath, token))
            {
                HandleRouteStartProtectionFailure("Repot path");
                return false;
            }

            var result = await RunRouteOnceAsync(repotPath, "Repot path", token);
            if (token.IsCancellationRequested) return false;
            if (IsDeathRecoveryRequested) return false;

            if (result == RouteRunResult.WaypointRepotRequested)
            {
                _log("[WaypointRecovery] Repot was requested inside the Repot step path; treating it as a failed repot-path attempt.");
            }

            if (result != RouteRunResult.Completed)
            {
                // In-city stuck escalation (or any route aborted by the in-city unstuck limit):
                // never burn more back-to-back repot retries. Stop, wait 10 min, reset, repot again.
                if ((result == RouteRunResult.CityStuck || result == RouteRunResult.NavigationFailure) &&
                    _memoryService.GetIsInCity())
                {
                    await WaitOutInCityStuckAndResetAsync("Repot path", token);
                    return false;
                }

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
            catch (BotStopRequestedException)
            {
                // The game window lost focus during the seller scan — the whole bot
                // must stop cleanly. Rethrow so StartAsync handles it as a stop.
                throw;
            }
            catch (Exception ex)
            {
                _log($"[Repot] Repot failed: {ex.Message}");
                CurrentPhase = BotPhase.Failed;
                return false;
            }

            if (IsDeathRecoveryRequested)
                return false;

            var snapshot = _memoryService.GetSnapshot();
            _log($"[Repot] Post-repot: HP Pots: {snapshot.HpPotions}, Mana Pots: {snapshot.ManaPotions}");

            // Post-repot verification: the shop buy is fire-and-forget (a missed
            // click or a rejected quantity leaves the count unchanged), so a silent
            // buy failure must not look like success — otherwise the bot walks back
            // to exp with 0 potions. On mismatch: wait 5 minutes, then redo the
            // whole repot step from the top (teleport check + walk + shop + buy),
            // exactly like an exp-triggered repot. Already in the city, the teleport
            // phase is a no-op (no scroll wasted).
            int redPots = _memoryService.GetRedPotionCount();
            int whitePots = _memoryService.GetWhitePotionCount();
            if (snapshot.HpPotions < _profile.HpBuyTarget ||
                snapshot.ManaPotions < _profile.ManaBuyTarget ||
                redPots < _profile.RedBuyTarget ||
                whitePots < _profile.WhiteBuyTarget)
            {
                _postRepotVerifyRetryCount++;
                if (_postRepotVerifyRetryCount >= BotConstants.Repot.MaxPostRepotVerifyRetries)
                {
                    _log($"[Repot] Potion targets still not met after {_postRepotVerifyRetryCount} tries " +
                         $"(HP {snapshot.HpPotions}/{_profile.HpBuyTarget}, Mana {snapshot.ManaPotions}/{_profile.ManaBuyTarget}, " +
                         $"Red {redPots}/{_profile.RedBuyTarget}, White {whitePots}/{_profile.WhiteBuyTarget}). Giving up.");
                    _postRepotVerifyRetryCount = 0;
                    CurrentPhase = BotPhase.Failed;
                    return false;
                }
                _log($"[Repot] Potion targets not met " +
                     $"(HP {snapshot.HpPotions}/{_profile.HpBuyTarget}, Mana {snapshot.ManaPotions}/{_profile.ManaBuyTarget}, " +
                     $"Red {redPots}/{_profile.RedBuyTarget}, White {whitePots}/{_profile.WhiteBuyTarget}) — " +
                     $"waiting {BotConstants.Repot.PostRepotVerifyRetryDelayMs / 60000} min before redoing the full repot " +
                     $"(attempt {_postRepotVerifyRetryCount}/{BotConstants.Repot.MaxPostRepotVerifyRetries}).");
                try
                {
                    await PausableDelayAsync(BotConstants.Repot.PostRepotVerifyRetryDelayMs, token);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                if (token.IsCancellationRequested)
                    return false;
                return false; // stay on this step: next run starts from the top (teleport check + walk + shop + buy)
            }
            _postRepotVerifyRetryCount = 0;

            return true;
        }

        /// <summary>
        /// Operation step: runs the named custom operation with the runner's retry policy.
        /// When the operation ultimately fails (returns false, e.g. Enter_COT could not
        /// find the NPC), the flow restarts from the beginning — i.e. the bot goes back
        /// to the Repot step and starts the whole flow over instead of failing hard.
        /// </summary>
        private async Task<bool> ExecuteOperationStepAsync(BotFlowStep step, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(step.OperationName))
            {
                _log("[Operation] Operation step has no operation name. Failing.");
                CurrentPhase = BotPhase.Failed;
                return false;
            }

            PlayerStateSnapshot beforeOperation = _positionSync.CaptureCurrentState();

            if (!await RunOperationWithRetryAsync(step.OperationName, token))
            {
                // A confirmed death owns the workflow decision; never treat the stopped
                // operation as a flow failure or restart the flow while the player is dead.
                if (IsDeathRecoveryRequested)
                    return false;

                // An operation may have marked an impending transition and then failed
                // without the map actually changing (e.g. the portal click was ignored).
                // Do not leave that stale marker blocking the next start check.
                PlayerStateSnapshot afterFailure = _positionSync.CaptureCurrentState();
                if (_positionSync.HasPendingTransition &&
                    beforeOperation.HasCoherentState &&
                    afterFailure.HasCoherentState &&
                    beforeOperation.Map == afterFailure.Map)
                {
                    _positionSync.CancelPendingTransition(
                        $"Operation '{step.OperationName}' failed without a map change");
                }

                _log($"[Operation] Operation '{step.OperationName}' failed after retries. Restarting the flow from the start (repot + start over).");
                _flowIndex = 0;
                return false;
            }

            PlayerStateSnapshot afterOperation = _positionSync.CaptureCurrentState();

            bool operationChangedMap =
                beforeOperation.HasCoherentState &&
                afterOperation.HasCoherentState &&
                beforeOperation.Map != afterOperation.Map;

            bool transitionWasMarked = _positionSync.HasPendingTransition;
            if (operationChangedMap || transitionWasMarked)
            {
                if (operationChangedMap && !transitionWasMarked)
                {
                    _positionSync.BeginTransition(
                        $"Operation '{step.OperationName}' map change",
                        beforeOperation);
                }

                // The operation completed, but its completion means only "map changed".
                // Coordinates are validated before the next workflow step may depend on
                // them; an unconfirmed position blocks the flow instead of teleporting.
                // When the destination map was not observed yet (the map read can lag the
                // operation), do not demand a specific map — wait for any stable map and
                // require fresh coordinates.
                int expectedMap = operationChangedMap && afterOperation.HasCoherentState
                    ? afterOperation.Map
                    : 0;
                PositionSyncResult sync = await _positionSync.SynchronizeAsync(expectedMap, token);
                if (token.IsCancellationRequested)
                    return false;

                if (!sync.IsReady)
                {
                    string observedMap = sync.State.MapReadSucceeded ? sync.State.Map.ToString() : "unreadable";
                    _log($"[PositionSync] Operation '{step.OperationName}' completed, but the position is not ready ({sync.Kind}: {sync.Message}; observed map {observedMap}). Stopping before the next step.");
                    CurrentPhase = BotPhase.Failed;
                    return false;
                }

                _log($"[PositionSync] Operation '{step.OperationName}' position ready: {sync.State.Describe()}.");
            }
            else if (transitionWasMarked)
            {
                _positionSync.CancelPendingTransition(
                    $"Operation '{step.OperationName}' completed without a map change");
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

            if (IsStartupRouteEntry(step, routeIndex))
            {
                // Initial resume of the map the user started the bot on (e.g. the middle of
                // the COT hunting route): map + validated position only. The configured
                // entrance coordinates are NOT demanded and no town teleport may be triggered.
                var startupEntry = _startupRouteEntry!;
                if (!await EnsureStartupRouteEntryValidatedAsync(route, startupEntry.StartMapNumber, "ExpLoop", token))
                {
                    CurrentPhase = BotPhase.Failed;
                    return false;
                }
                ConsumeStartupRouteEntry(step, routeIndex);
            }
            else
            {
                if (!await RunRouteStartProtectionAsync(route, token))
                {
                    HandleRouteStartProtectionFailure("ExpLoop");
                    return false;
                }

                // Destination gate: the EXP path runner (including its initial RouteResync)
                // must not initialize until the post-transition position was validated for
                // this route's destination. A failed gate stops the step safely instead of
                // selecting waypoints from coordinates that may belong to the previous map.
                if (!await EnsureRouteEntryValidatedAsync(route, "ExpLoop", token))
                {
                    CurrentPhase = BotPhase.Failed;
                    return false;
                }
            }

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

            // The EXP route is cancelled by the workflow token OR by a confirmed death. The
            // death token is cancelled only by NotifyPlayerDeathDetected, so the coordinator
            // token stays alive and the profile can be recovered.
            using var expCts = CancellationTokenSource.CreateLinkedTokenSource(token, DeathRecoveryCancellationToken);
            var expToken = expCts.Token;
            int otherPlayerMarkerDetected = 0;

            var pathTask = _pathRunner.RunPathAsync(
                waypoints,
                loop: true,
                token: expToken,
                expectedMap: route.ProtectionMapNumber,
                routeLabel: route.PathFile,
                disableAllLoot: _profile.DisableAllLoot);

            // The EXP route is a hunting route: run the loot system in parallel so drops
            // are collected while walking and after kills. MoveAndAttackAndLoot routes use
            // the normal white-pixel loot cycle; MoveAndAttack routes use the SOD/SOP
            // pink-only post-kill scan (single big region, repeated until no pink left).
            // A profile-level or path-level Disable All Loot policy suppresses every
            // automatic loot operation while keeping the component available for the
            // existing player-marker safety callbacks.
            LootSystem? lootSystem = null;
            Task? lootTask = null;
            bool hasAttackLootRoute = waypoints.Any(w => w.Mode == BotMode.MoveAndAttackAndLoot);
            bool hasAttackOnlyRoute = waypoints.Any(w => w.Mode == BotMode.MoveAndAttack);
            bool pathDisablesAllLoot = waypoints.TrueForAll(w => w.DisableAllLoot);
            bool disableAllLoot = _profile.DisableAllLoot || pathDisablesAllLoot;
            if (hasAttackLootRoute || hasAttackOnlyRoute)
            {
                lootSystem = new LootSystem(_memoryService, _log, _pathRunner.WCameraController)
                {
                    LootPriorityMode = _profile.LootPriority && !disableAllLoot,
                    PinkLootOnlyMode = !hasAttackLootRoute && hasAttackOnlyRoute,
                    AutomaticLootDisabled = disableAllLoot,
                    ShouldStopOnOtherPlayerMarker = () => ShouldStopOnOtherPlayerDetected?.Invoke() ?? true,
                    OtherPlayerMarkerDetected = () =>
                    {
                        if (Interlocked.Exchange(ref otherPlayerMarkerDetected, 1) == 0)
                        {
                            _log("[ExpLoop] Other player pixels detected — interrupting EXP and returning to the city.");
                            expCts.Cancel();
                        }
                    }
                };

                // Give the movement system a reference to the loot system so it can hold
                // waypoint advancement until a full loot scan pass finishes after a kill.
                if (_pathRunner.CurrentMovement != null)
                {
                    _pathRunner.CurrentMovement.LootSystemRef = lootSystem;
                    _pathRunner.CurrentMovement.LootPriorityMode = _profile.LootPriority;
                }

                if (disableAllLoot)
                {
                    _log("[ExpLoop] Automatic loot disabled by profile/path policy — no loot scans, camera changes, screenshots or collection. Movement and combat continue; player safety detection stays active.");
                }
                else
                {
                    lootTask = Task.Run(async () =>
                    {
                        try
                        {
                            while (!expToken.IsCancellationRequested && lootSystem != null)
                            {
                                // Profile/path policy is authoritative for this tick. It is
                                // re-evaluated so a mixed path can suspend loot on a disabled
                                // waypoint and resume normally on an enabled one.
                                bool automaticLootDisabled =
                                    _profile.DisableAllLoot ||
                                    _pathRunner.CurrentMovement?.IsAutomaticLootDisabled == true;
                                lootSystem.AutomaticLootDisabled = automaticLootDisabled;
                                if (automaticLootDisabled)
                                {
                                    lootSystem.PinkLootOnlyMode = false;
                                    lootSystem.MoveAndAttackAndLootMode = false;
                                    lootSystem.PlayerMarkerScanEnabled = false;
                                    await PausableDelayAsync(BotConstants.Delays.LootUpdateMs, expToken);
                                    continue;
                                }

                                // OnlyMove is mandatory above all movement-option actions:
                                // never try to loot (or attack) while the current waypoint
                                // is OnlyMove. Heal stays always on (separate task).
                                if (_pathRunner.CurrentMovement?.IsMoveOnlyActive == true)
                                {
                                    lootSystem.PinkLootOnlyMode = false;
                                    lootSystem.MoveAndAttackAndLootMode = false;
                                    lootSystem.PlayerMarkerScanEnabled = false;
                                    await PausableDelayAsync(BotConstants.Delays.LootUpdateMs, expToken);
                                    continue;
                                }
                                if (_pathRunner.CurrentMovement?.IsRecoveryInputOwnershipActive == true)
                                {
                                    // Recovery (reverse diagonal, waypoint special recovery,
                                    // map-transition hold) owns movement and camera exclusively:
                                    // no new scan may start, and the MovementSystem has already
                                    // quiesced any in-flight scan when it took ownership.
                                    lootSystem.PinkLootOnlyMode = false;
                                    lootSystem.MoveAndAttackAndLootMode = false;
                                    lootSystem.PlayerMarkerScanEnabled = false;
                                    await PausableDelayAsync(BotConstants.Delays.LootUpdateMs, expToken);
                                    continue;
                                }
                                if (_pathRunner.CurrentMovement?.IsWaypointSpecialRecoveryActive == true)
                                {
                                    lootSystem.PinkLootOnlyMode = false;
                                    lootSystem.MoveAndAttackAndLootMode = false;
                                    lootSystem.PlayerMarkerScanEnabled = false;
                                    await PausableDelayAsync(BotConstants.Delays.LootUpdateMs, expToken);
                                    continue;
                                }

                                // Track the current waypoint mode: MoveAndAttack waypoints use
                                // the SOD/SOP pink-only scan; MoveAndAttackAndLoot is
                                // request-driven (passive moving pre-scan + post-kill scans).
                                BotMode? currentMode = _pathRunner.CurrentMovement?.CurrentMode;
                                lootSystem.PinkLootOnlyMode = currentMode == BotMode.MoveAndAttack;
                                lootSystem.MoveAndAttackAndLootMode =
                                    currentMode == BotMode.MoveAndAttackAndLoot;
                                lootSystem.PlayerMarkerScanEnabled =
                                    currentMode == BotMode.MoveAndAttack || currentMode == BotMode.MoveAndAttackAndLoot;

                                await lootSystem.Update(expToken);
                                await PausableDelayAsync(BotConstants.Delays.LootUpdateMs, expToken);
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            _log($"[ExpLoop] Loot system error: {ex.Message}");
                        }
                        finally
                        {
                            lootSystem?.Stop();
                        }
                    }, expToken);
                    _log(hasAttackLootRoute
                        ? "[ExpLoop] Loot system started (MoveAndAttackAndLoot route — normal white-pixel loot)."
                        : "[ExpLoop] Loot system started (MoveAndAttack route — SOD/SOP pink-pixel scan only, big region, after each kill).");
                    if (_profile.LootPriority)
                        _log("[ExpLoop] LOOT PRIORITY MODE ON — looting outranks combat; attack and waypoint movement are suspended while loot is being scanned/collected.");
                }
            }

            bool repotNeeded = false;
            bool cityDetected = false;
            bool waypointRepotRequested = false;
            bool reportAndGoBackDetected = false;
            bool navigationFailureDetected = false;
            bool mapTransitionDetected = false;
            int inCityConsecutiveReads = 0;
            DateTime lastRepotCheckAt = DateTime.MinValue;
            try
            {
                // Fast poll (500ms) for teleport / path-abort / city so a ReportAndGoBack
                // teleport kills the stale Exp route immediately instead of running the old
                // unstuck in town for seconds. The expensive repot-condition check stays
                // throttled to ExpLoopRepotCheckIntervalMs.
                while (!expToken.IsCancellationRequested)
                {
                    await WaitIfPausedAsync(token);
                    if (token.IsCancellationRequested || expToken.IsCancellationRequested) break;

                    if (CheckExpPathTeleportAbort(pathTask, out bool isWaypointRepot))
                    {
                        if (isWaypointRepot)
                        {
                            waypointRepotRequested = true;
                            _log("[WaypointRecovery] ExpLoop path completed for a waypoint Repot request.");
                        }
                        else if (_pathRunner.CurrentMovement?.IsNavigationFailureEscalationRequested == true ||
                                 _pathRunner.LastStopReason == PathRunStopReason.NavigationFailure)
                        {
                            navigationFailureDetected = true;
                            _log("[NavigationFailure] ExpLoop route aborted for a blocked waypoint — stopping loot and routing to the existing Repot workflow.");
                        }
                        else
                        {
                            reportAndGoBackDetected = true;
                            _log("[ExpLoop] ReportAndGoBack teleport detected — stopping exp loop, going to Repot from scratch.");
                        }
                        expCts.Cancel();
                        break;
                    }

                    // A confirmed unexpected map change terminates the obsolete path task.
                    // Detect it immediately so the old route can never keep polling and so
                    // loot work is cancelled before the post-transition recovery decision.
                    if (CheckExpPathMapTransition(pathTask))
                    {
                        mapTransitionDetected = true;
                        _log("[ExpLoop] Confirmed unexpected map change — terminating the obsolete EXP route.");
                        expCts.Cancel();
                        break;
                    }

                    await PausableDelayAsync(500, expToken);

                    if (CheckExpPathTeleportAbort(pathTask, out bool isWaypointRepotAfter))
                    {
                        if (isWaypointRepotAfter)
                        {
                            waypointRepotRequested = true;
                            _log("[WaypointRecovery] ExpLoop path completed for a waypoint Repot request.");
                        }
                        else if (_pathRunner.CurrentMovement?.IsNavigationFailureEscalationRequested == true ||
                                 _pathRunner.LastStopReason == PathRunStopReason.NavigationFailure)
                        {
                            navigationFailureDetected = true;
                            _log("[NavigationFailure] ExpLoop route aborted for a blocked waypoint — stopping loot and routing to the existing Repot workflow.");
                        }
                        else
                        {
                            reportAndGoBackDetected = true;
                            _log("[ExpLoop] ReportAndGoBack teleport detected — stopping exp loop, going to Repot from scratch.");
                        }
                        expCts.Cancel();
                        break;
                    }

                    if (CheckExpPathMapTransition(pathTask))
                    {
                        mapTransitionDetected = true;
                        _log("[ExpLoop] Confirmed unexpected map change — terminating the obsolete EXP route.");
                        expCts.Cancel();
                        break;
                    }

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

                    if ((DateTime.UtcNow - lastRepotCheckAt).TotalMilliseconds < BotConstants.Delays.ExpLoopRepotCheckIntervalMs)
                        continue;
                    lastRepotCheckAt = DateTime.UtcNow;

                    if (_repotDetector.NeedsRepot(snapshot))
                    {
                        _log("[ExpLoop] Repot condition detected. Stopping exp loop.");
                        if (_repotDetector.IsLowHpPotions(snapshot))
                            CaptureLowHpPotionsScreenshot(snapshot);
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
            else
            {
                // Disabled loot keeps its component only for safety notifications; it is
                // stopped at the phase boundary so no ownership can leak into Repot.
                lootSystem?.Stop();
            }

            // Phase-boundary guarantee: the EXP loop may have been interrupted while
            // the combat attack key (3) was held — MovementSystem holds it across ticks
            // and only releases it on a later combat tick, which never runs once the
            // loop is cancelled. Release combat-owned keys through the existing
            // ownership mechanism BEFORE handing control to the next flow step
            // (Repot/teleport), so the "[Key] 3 up" log always precedes the Repot
            // phase. Idempotent: the PathRunner finally performs the same cleanup
            // centrally for cancellation/exception paths, so a duplicate call here
            // sends nothing when the key is already released.
            _pathRunner.CurrentMovement?.ReleaseCombatKeys();

            _pathRunner.Stop();
            _log("[ExpLoop] Exp hunting loop ended.");

            if (token.IsCancellationRequested)
                return false;

            // A confirmed death owns the next workflow decision: never interpret the cancelled
            // EXP route as a repot/teleport/navigation failure and never resume the queue.
            if (IsDeathRecoveryRequested)
            {
                _log("[ExpLoop] Confirmed death — EXP work stopped; the workflow performs death recovery.");
                return false;
            }

            // ── Confirmed unexpected map transition (highest workflow priority) ──
            // The obsolete route is already dead. The position is synchronized through
            // the existing PositionSync lifecycle before ANY new position-dependent
            // action; a failed synchronization stops the workflow instead of guessing.
            if (mapTransitionDetected ||
                _pathRunner.LastStopReason == PathRunStopReason.MapTransition ||
                _pathRunner.CurrentMovement?.IsMapRouteInvalidated == true ||
                _pathRunner.CurrentMovement?.IsMapTransitionPending == true)
            {
                AdvanceRoute(step, pool);
                if (!await SynchronizeAfterExpMapTransitionAsync(route, token))
                    return false;

                bool arrivedInCity = _memoryService.GetIsInCity();
                _log(arrivedInCity
                    ? "[ExpLoop] Workflow decision: confirmed city arrival — routing to the Repot step."
                    : "[ExpLoop] Workflow decision: confirmed unexpected non-city map — routing to the Repot step for validated recovery.");
                RedirectToRepotStep(arrivedInCity
                    ? "ExpLoop map transition to city"
                    : "ExpLoop map transition to another map");
                return false;
            }

            if (Volatile.Read(ref otherPlayerMarkerDetected) != 0)
            {
                CurrentPhase = BotPhase.NeedRepot;
                _playerMarkerInterruptionsSinceFullExpCycle++;
                int cooldownMs = _playerMarkerInterruptionsSinceFullExpCycle == 1
                    ? BotConstants.Delays.OtherPlayerInitialCooldownMs
                    : BotConstants.Delays.OtherPlayerRepeatCooldownMs;

                _log("[PlayerPixels] EXP interrupted because another player marker was detected. Teleporting to city.");
                await TeleportToCity(token);
                if (token.IsCancellationRequested)
                    return false;

                if (_memoryService.GetIsInCity())
                {
                    _log($"[PlayerPixels] Waiting {cooldownMs / 60000} minutes in the city before restarting the profile cycle.");
                    try
                    {
                        await PausableDelayAsync(cooldownMs, token);
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }
                    if (token.IsCancellationRequested)
                        return false;
                }
                else
                {
                    _log("[PlayerPixels] City arrival was not confirmed; skipping the cooldown and handing recovery to the Repot step.");
                }

                AdvanceRoute(step, pool);
                RedirectToRepotStep("other player marker detected during EXP");
                return false;
            }

            if (waypointRepotRequested ||
                _pathRunner.LastStopReason == PathRunStopReason.WaypointRepotRequested)
            {
                AdvanceRoute(step, pool);
                RedirectToRepotStep("ExpLoop waypoint Repot request");
                return false;
            }

            if (navigationFailureDetected ||
                _pathRunner.LastStopReason == PathRunStopReason.NavigationFailure)
            {
                AdvanceRoute(step, pool);

                // Escalation inside the city means the in-city unstuck limit (2) is spent:
                // stop, wait 10 min, reset and start again from the Repot step.
                if (_memoryService.GetIsInCity())
                {
                    await WaitOutInCityStuckAndResetAsync("ExpLoop", token);
                    return false;
                }

                _log("[NavigationFailure] Workflow decision: routing from the failed waypoint to Repot; the Repot step will perform and validate the existing town teleport if needed.");
                RedirectToRepotStep("navigation failure escalation");
                return false;
            }

            // Teleport during Exp (ReportAndGoBack stuck-escalation or any city teleport):
            // the old Exp queue is dead. Never advance to the next flow step (e.g. an
            // Operation) — jump straight to Repot so it starts from its first command.
            if (reportAndGoBackDetected ||
                _pathRunner.LastStopReason == PathRunStopReason.ReportAndGoBack ||
                cityDetected)
            {
                AdvanceRoute(step, pool);
                RedirectToRepotStep(reportAndGoBackDetected ||
                    _pathRunner.LastStopReason == PathRunStopReason.ReportAndGoBack
                    ? "ExpLoop ReportAndGoBack teleport"
                    : "ExpLoop city teleport");
                return false;
            }

            // The flow simply advances to the next step (and wraps around at the end).
            // The Repot step is responsible for returning to the city and refilling, so a
            // flow like Repot → ... → ExpLoop → Operation (after hunt) → Repot works:
            // after hunting, any following steps run, and the cycle returns to Repot.
            AdvanceRoute(step, pool);
            if (repotNeeded)
                _playerMarkerInterruptionsSinceFullExpCycle = 0;
            CompletedCycles++;
            _log($"[ExpLoop] Full repot->exp cycle #{CompletedCycles} completed. Advancing to the next flow step.");
            OnCycleCompleted?.Invoke(CompletedCycles);
            return true;
        }

        // ======================== Helpers ========================

        /// <summary>
        /// Movement gate used by steps that can run before any start-position
        /// protection: if a transition is still pending, synchronize it (bounded) before
        /// allowing movement. A failed synchronization sets the Failed phase so the step
        /// cannot be retried in a loop with unvalidated coordinates.
        /// </summary>
        private async Task<bool> EnsurePendingTransitionResolvedAsync(string stepDescription, CancellationToken token)
        {
            if (!_positionSync.HasPendingTransition)
                return true;

            _log($"[PositionSync] Pending transition before {stepDescription} — waiting for position synchronization...");
            PositionSyncResult sync = await _positionSync.SynchronizeAsync(expectedMap: 0, token);
            if (token.IsCancellationRequested)
                return false;

            if (!sync.IsReady)
            {
                _log($"[PositionSync] {stepDescription} blocked: position not ready ({sync.Kind}: {sync.Message}; {sync.State.Describe()}).");
                CurrentPhase = BotPhase.Failed;
                return false;
            }

            _log($"[PositionSync] {stepDescription} position ready: {sync.State.Describe()}.");
            return true;
        }

        /// <summary>
        /// Fast teleport-abort check for the ExpLoop monitor. Returns true when the Exp
        /// path is dead because of a teleport/repot escalation and the loop must stop
        /// immediately (old route discarded, coordinator goes straight to Repot).
        /// </summary>
        /// <param name="pathTask">The running PathRunner task.</param>
        /// <param name="isWaypointRepot">True = waypoint Repot request, false = ReportAndGoBack teleport.</param>
        private bool CheckExpPathTeleportAbort(System.Threading.Tasks.Task pathTask, out bool isWaypointRepot)
        {
            isWaypointRepot = false;
            // Escalation is signalled by MovementSystem immediately and observed before
            // waiting for PathRunner to unwind. A manual cancellation never sets this flag.
            if (_pathRunner.CurrentMovement?.IsNavigationFailureEscalationRequested == true)
                return true;
            if (_pathRunner.CurrentMovement?.IsReportAndGoBackRequested == true)
                return true;
            if (pathTask == null || !pathTask.IsCompleted)
                return false;
            if (_pathRunner.LastStopReason == PathRunStopReason.WaypointRepotRequested)
            {
                isWaypointRepot = true;
                return true;
            }
            if (_pathRunner.LastStopReason == PathRunStopReason.ReportAndGoBack ||
                _pathRunner.LastStopReason == PathRunStopReason.NavigationFailure)
                return true;
            return false;
        }

        /// <summary>
        /// True when the Exp path task terminated because a confirmed unexpected map
        /// change invalidated the route's map invariant. Also catches the case where
        /// the MovementSystem already marked the route invalid but the path task has not
        /// yet unwound, so the coordinator never keeps polling forever after the path
        /// task has finished.
        /// </summary>
        private bool CheckExpPathMapTransition(System.Threading.Tasks.Task pathTask)
        {
            if (_pathRunner.CurrentMovement?.IsMapRouteInvalidated == true)
                return true;
            if (pathTask == null || !pathTask.IsCompleted)
                return false;
            return _pathRunner.LastStopReason == PathRunStopReason.MapTransition;
        }

        /// <summary>
        /// Records and resolves the post-transition position through the existing
        /// <see cref="PositionSyncService"/> lifecycle. The last coherent snapshot from
        /// the original map is preserved when available; a failed synchronization blocks
        /// all movement and teleport decisions and surfaces the actual failure reason.
        /// </summary>
        private async Task<bool> SynchronizeAfterExpMapTransitionAsync(
            BotRouteStep route,
            CancellationToken token)
        {
            PlayerStateSnapshot? source = _pathRunner.CurrentMovement?.LastCoherentSnapshotBeforeMapChange;
            if (!_positionSync.HasPendingTransition)
            {
                _positionSync.BeginTransition(
                    $"ExpLoop unexpected map change (route '{route.PathFile}', expected map {route.ProtectionMapNumber})",
                    source);
            }

            PositionSyncResult sync = await _positionSync.SynchronizeAsync(expectedMap: 0, token);
            _log($"[PositionSync] ExpLoop transition outcome={sync.Kind} TransitionId={sync.TransitionId} {sync.State.Describe()}.");

            if (token.IsCancellationRequested)
                return false;

            if (!sync.IsReady)
            {
                _log($"[ExpLoop] Post-transition position not ready ({sync.Kind}: {sync.Message}) — " +
                     "stopping without movement or town teleport.");
                CurrentPhase = BotPhase.Failed;
                return false;
            }

            _log($"[ExpLoop] Post-transition position ready (TransitionId={sync.TransitionId}, map={sync.State.Map}, position=({sync.State.X},{sync.State.Y})).");
            return true;
        }

        /// <summary>
        /// In-city stuck reset. The MovementSystem stops a city route after
        /// STUCK_MAX_ATTEMPTS_IN_CITY (2) unstuck attempts instead of hammering the same spot.
        /// The workflow therefore stops, teleports BACK TO TOWN through the existing town
        /// teleport workflow (profile teleport key + validated arrival), and only after that
        /// waits <see cref="InCityStuckResetDelayMs"/> (10 min) before clearing the failure
        /// bookkeeping and restarting from the Repot step. The cooldown is never spent at the
        /// location where the player got stuck.
        /// </summary>
        /// <returns>True when the caller must yield the current step (reset performed,
        /// cancelled, or terminal recovery failure after the town teleport was not verified).</returns>
        private async Task<bool> WaitOutInCityStuckAndResetAsync(string stage, CancellationToken token)
        {
            _log($"[Unstuck] {stage}: in-city unstuck limit reached — stopping the bot, teleporting to town, then waiting {InCityStuckResetDelayMs / 60000} min before resetting and starting again from the Repot step.");

            // The diagnostic stuck screenshot is captured by MovementSystem before it raises
            // the fatal escalation (Screenshots/Stuck). Preserve that existing capture here
            // and take no new one: the recovery must not change the stuck-location evidence.
            _log("[Recovery] Stuck screenshot: existing diagnostic capture (Screenshots/Stuck) preserved before the route abort.");

            // The route is already dead (PathRunner released W/A/D and combat keys).
            // Stop again so an in-flight run can never survive the pause, and release any
            // movement/steering/camera/attack input still owned by the aborted route.
            _log("[Recovery] Stopping current path and releasing movement/attack inputs.");
            _pathRunner.Stop();
            if (token.IsCancellationRequested)
                return true;

            // Teleport FIRST: the cooldown must only ever run in town. The existing
            // TeleportToCity workflow presses the profile's town teleport key (key 6),
            // waits for the town UI and validates the destination through PositionSync
            // (mandatory post-teleport W refresh). A teleport without that validation is
            // never treated as success.
            bool inTown;
            try
            {
                inTown = await TeleportToTownForCityStuckRecoveryAsync(token);
            }
            catch (OperationCanceledException)
            {
                _log("[Recovery] City-stuck recovery cancelled before the cooldown started.");
                return true;
            }

            if (token.IsCancellationRequested)
                return true;

            if (!inTown)
            {
                // Never spend the 10 minutes at the stuck location and never pretend the
                // player reached town. The teleport input was already retried within the
                // profile's existing MaxTeleportRetries budget; fail visibly instead.
                _log("[Recovery] Terminal recovery failure: the player could not be verified in town. " +
                     $"NOT starting the {InCityStuckResetDelayMs / 60000}-minute cooldown at the stuck location.");
                CurrentPhase = BotPhase.Failed;
                return true;
            }

            _log("[Recovery] Player safely returned to town.");
            _log($"[Recovery] Starting {InCityStuckResetDelayMs / 60000}-minute cooldown in town.");

            // The existing 10-minute wait starts only now — after the verified town arrival.
            try
            {
                await PausableDelayAsync(InCityStuckResetDelayMs, token);
            }
            catch (OperationCanceledException)
            {
                _log("[Recovery] City-stuck cooldown cancelled.");
                return true;
            }

            if (token.IsCancellationRequested)
                return true;

            _log("[Recovery] Cooldown completed.");

            // Reset the failure bookkeeping so the restart gets a clean budget.
            _moveToRepotRetryCount = 0;
            _pathStepRetryCount = 0;
            _teleportRetryCount = 0;
            _postRepotVerifyRetryCount = 0;
            _log("[Recovery] Resetting profile execution.");
            _log("[Unstuck] Wait over — failure counters reset. Going to the Repot step and starting again.");

            bool redirected = RedirectToRepotStep($"{stage}: in-city stuck reset");
            if (!redirected)
                return true;

            _log("[Recovery] Restarting profile from Repot step.");
            _log("[Unstuck] In-city stuck reset complete — repot first, then the flow starts again.");
            return true;
        }

        /// <summary>
        /// Runs the existing <see cref="TeleportToCity"/> workflow for the in-city stuck
        /// recovery and verifies the result BEFORE any cooldown may start. The attempt
        /// budget reuses the profile's existing <see cref="BotProfile.MaxTeleportRetries"/>;
        /// there is no new retry mechanism.
        ///
        /// Success requires all three of:
        ///   1. <see cref="TeleportToCity"/> validated the post-teleport position
        ///      (PositionSync + mandatory W refresh),
        ///   2. the in-city flag is set,
        ///   3. the validated position is outside the stuck location's neighbourhood (or
        ///      the map changed), so a plain W refresh at the stuck spot can never be
        ///      mistaken for a town arrival.
        /// </summary>
        private async Task<bool> TeleportToTownForCityStuckRecoveryAsync(CancellationToken token)
        {
            int maxAttempts = Math.Max(1, _profile.MaxTeleportRetries);
            PlayerStateSnapshot stuckState = _positionSync.CaptureCurrentState();
            _log($"[Recovery] Captured stuck position: {stuckState.Describe()}.");

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();

                string keyText = _profile.TeleportKey is >= 0x30 and <= 0x39
                    ? ((char)_profile.TeleportKey).ToString()
                    : $"0x{_profile.TeleportKey:X2}";
                _log($"[Recovery] Executing Scroll to Town (key {keyText}, vk={_profile.TeleportKey}) — attempt {attempt}/{maxAttempts}.");

                bool positionReady = await TeleportToCity(token);
                if (token.IsCancellationRequested)
                    return false;

                bool inCity = _memoryService.GetIsInCity();
                PositionSyncResult? townSync = _lastTownTeleportSync;
                PlayerStateSnapshot? destination = _positionSync.LastValidatedState;
                bool destinationMatched =
                    townSync?.DestinationStatus == DestinationValidationStatus.MatchedDestination;
                string transitionFailure = DescribeTownTransitionFailure(stuckState, destination, destinationMatched);

                if (positionReady && inCity && transitionFailure.Length == 0)
                {
                    _log($"[Recovery] Teleport verification: SUCCESS — {destination?.Describe() ?? "PositionSync ready"} InCity={inCity} DestinationMatched={destinationMatched}.");
                    return true;
                }

                string reason = !positionReady
                    ? "PositionSync did not validate the post-teleport position"
                    : !inCity
                        ? "the in-city flag is not set"
                        : transitionFailure;
                _log($"[Recovery] Teleport verification: FAILED — {reason} (PositionSyncReady={positionReady}, InCity={inCity}, DestinationMatched={destinationMatched}, Attempt={attempt}/{maxAttempts}).");

                if (attempt < maxAttempts)
                    _log($"[Recovery] Scroll to Town retry {attempt + 1}/{maxAttempts}.");
            }

            _log($"[Recovery] Scroll to Town failed after {maxAttempts}/{maxAttempts} attempts — the player is not verified in town.");
            return false;
        }

        /// <summary>
        /// Empty when the validated post-teleport position proves a real town transition,
        /// otherwise the human-readable failure reason.
        ///
        /// A position that matches the destination expectation supplied to PositionSync is
        /// a SUCCESS even when it is near the stuck location: a town teleport can stay on
        /// the same map and return the player to the same spawn, and W can be blocked
        /// there. The proximity check is only the fallback for teleports without a known
        /// destination, where a one-tile W response at the stuck spot must not be mistaken
        /// for a town arrival. A non-coherent stuck snapshot cannot be compared, so the
        /// existing PositionSync result decides in that case.
        /// </summary>
        private static string DescribeTownTransitionFailure(
            PlayerStateSnapshot stuckState,
            PlayerStateSnapshot? destination,
            bool destinationMatched)
        {
            if (destinationMatched)
                return "";

            if (!stuckState.HasCoherentState)
                return "";

            if (!destination.HasValue || !destination.Value.HasCoherentState)
                return "no coherent validated town position was published";

            PlayerStateSnapshot town = destination.Value;
            if (town.Map != stuckState.Map)
                return "";

            int radius = BotConstants.Delays.PositionSyncStaleSourceRadiusTiles;
            int deltaX = Math.Abs((int)town.X - stuckState.X);
            int deltaY = Math.Abs((int)town.Y - stuckState.Y);
            return deltaX <= radius && deltaY <= radius
                ? $"the validated position ({town.X},{town.Y}) is still inside the stuck location neighbourhood"
                : "";
        }

        private bool RedirectToRepotStep(string reason)
        {
            var steps = _profile.FlowSteps;
            if (steps == null || steps.Count == 0)
            {
                _log($"[WaypointRecovery] Cannot redirect to Repot ({reason}): profile has no flow steps.");
                CurrentPhase = BotPhase.Failed;
                return false;
            }

            for (int offset = 1; offset <= steps.Count; offset++)
            {
                int candidate = (_flowIndex + offset) % steps.Count;
                if (steps[candidate]?.Type != BotFlowStepType.Repot)
                    continue;

                _flowIndex = candidate;
                _log($"[WaypointRecovery] Redirecting workflow to Repot step {candidate + 1}/{steps.Count}: {reason}.");
                return true;
            }

            _log($"[WaypointRecovery] Configuration error: no Repot step exists; cannot redirect ({reason}).");
            CurrentPhase = BotPhase.Failed;
            return false;
        }

        private enum RouteRunResult
        {
            Completed,
            MissingSegment,
            WaypointRepotRequested,
            NavigationFailure,
            CityStuck,
            Incomplete
        }

        private enum TravelRouteRunResult
        {
            Completed,
            MissingSegment,
            WaypointRepotRequested,
            NavigationFailure,
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
            => BotStartupActionSelector.GetRoutePool(step);

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
            await PausableDelayAsync(startDelayMs, token);
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

            bool completed = await _pathRunner.RunPathAsync(
                waypoints, loop: false, token, disableAllLoot: _profile.DisableAllLoot);
            if (completed)
                _log($"[Coordinator] {stageName}: path completed.");
            else
                _log($"[Coordinator] {stageName}: path did not complete.");

            if (!completed && _pathRunner.LastStopReason == PathRunStopReason.WaypointRepotRequested)
                return RouteRunResult.WaypointRepotRequested;
            if (!completed && _pathRunner.LastStopReason == PathRunStopReason.NavigationFailure)
                return RouteRunResult.NavigationFailure;
            if (!completed && _pathRunner.LastStopReason == PathRunStopReason.CityStuck)
                return RouteRunResult.CityStuck;

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
                await PausableDelayAsync(route.StartDelayMs, token);
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
                bool completed = await _pathRunner.RunPathAsync(
                    waypoints, loop: false, token, disableAllLoot: _profile.DisableAllLoot);
                if (token.IsCancellationRequested) return TravelRouteRunResult.Cancelled;

                if (completed)
                    _log($"[Path] Step {stepIndex}/{stepCount}: path completed (final waypoint).");
                else
                    _log($"[Path] Step {stepIndex}/{stepCount}: path did not complete.");

                if (!completed && _pathRunner.LastStopReason == PathRunStopReason.WaypointRepotRequested)
                    return TravelRouteRunResult.WaypointRepotRequested;
                if (!completed && _pathRunner.LastStopReason == PathRunStopReason.NavigationFailure)
                    return TravelRouteRunResult.NavigationFailure;

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

            // Last coherent snapshot observed on the source map. It is the freshness
            // baseline for the destination position; it is refreshed while walking so
            // it reflects the player's position just before the portal is crossed.
            PlayerStateSnapshot? lastSourceState = _positionSync.CaptureCurrentState();

            var pathTask = _pathRunner.RunPathAsync(
                waypoints, loop: false, routeToken, disableAllLoot: _profile.DisableAllLoot);

            int consecutiveDestReads = 0;
            bool destinationConfirmed = false;

            while (!routeToken.IsCancellationRequested && !destinationConfirmed)
            {
                if (pathTask.IsCompleted)
                    break; // path finished before the map changed — handled by the grace period below

                await PausableDelayAsync(BotConstants.Delays.MapTransitionPollMs, routeToken);

                PlayerStateSnapshot state = _memoryService.GetPlayerStateSnapshot();
                int map = state.Map;
                if (!state.MapReadSucceeded || map == 0)
                    continue; // ignore unreadable map reads

                if (map == sourceMap)
                    lastSourceState = state;

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
                return await WaitForMapSettlementAsync(expectedMap, stepIndex, stepCount, token, lastSourceState);

            // The path finished before the expected destination map was confirmed:
            // give the portal a bounded grace period to activate.
            if (_pathRunner.LastStopReason == PathRunStopReason.WaypointRepotRequested)
            {
                _log("[WaypointRecovery] Portal route stopped for a waypoint Repot request; skipping portal grace period.");
                _pathRunner.Stop();
                return TravelRouteRunResult.WaypointRepotRequested;
            }
            if (_pathRunner.LastStopReason == PathRunStopReason.NavigationFailure)
            {
                _log("[NavigationFailure] Portal route stopped by navigation escalation; skipping portal grace period and routing to town.");
                _pathRunner.Stop();
                return TravelRouteRunResult.NavigationFailure;
            }

            _log($"[Path] Step {stepIndex}/{stepCount}: path finished before destination map confirmed. Waiting grace period for portal transition...");
            _pathRunner.Stop();

            var graceDeadline = DateTime.UtcNow.AddMilliseconds(BotConstants.Delays.MapTransitionGraceAfterPathMs);
            int graceConsecutiveReads = 0;
            PlayerStateSnapshot? graceSourceState = lastSourceState;
            while (DateTime.UtcNow < graceDeadline)
            {
                await PausableDelayAsync(BotConstants.Delays.MapTransitionPollMs, token);
                if (token.IsCancellationRequested)
                    return TravelRouteRunResult.Cancelled;

                PlayerStateSnapshot state = _memoryService.GetPlayerStateSnapshot();
                int map = state.Map;
                if (!state.MapReadSucceeded || map == 0)
                    continue;

                if (map == sourceMap)
                    graceSourceState = state;

                if (map == expectedMap)
                {
                    graceConsecutiveReads++;
                    if (graceConsecutiveReads >= BotConstants.Delays.MapTransitionStableReadCount)
                    {
                        _log($"[Path] Step {stepIndex}/{stepCount}: destination map {expectedMap} confirmed during grace period.");
                        return await WaitForMapSettlementAsync(expectedMap, stepIndex, stepCount, token, graceSourceState);
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
                await PausableDelayAsync(BotConstants.Delays.MapTransitionPollMs, token);

                int map = _memoryService.GetMapNumber();
                if (map != 0)
                    return map;
            }

            return 0;
        }

        /// <summary>
        /// Waits until the destination map and the player position read are both stable
        /// (two consecutive coherent polls), then validates that the coordinates belong
        /// to the destination rather than to the pre-transition location. The next step
        /// never initializes while the player pointer, map or coordinates are temporarily
        /// unavailable during loading.
        /// </summary>
        private async Task<TravelRouteRunResult> WaitForMapSettlementAsync(
            int expectedMap,
            int stepIndex,
            int stepCount,
            CancellationToken token,
            PlayerStateSnapshot? transitionSource = null)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(BotConstants.Delays.MapTransitionSettleTimeoutMs);
            int stableReads = 0;

            while (DateTime.UtcNow < deadline)
            {
                await PausableDelayAsync(BotConstants.Delays.MapTransitionPollMs, token);
                if (token.IsCancellationRequested)
                    return TravelRouteRunResult.Cancelled;

                PlayerStateSnapshot state = _memoryService.GetPlayerStateSnapshot();

                if (state.HasCoherentState && state.Map == expectedMap)
                {
                    stableReads++;
                    if (stableReads >= BotConstants.Delays.MapTransitionStableReadCount)
                    {
                        if (transitionSource.HasValue)
                        {
                            if (!_positionSync.HasPendingTransition)
                            {
                                _positionSync.BeginTransition(
                                    $"Path step {stepIndex}/{stepCount} destination map {expectedMap}",
                                    transitionSource);
                            }

                            PositionSyncResult sync = await _positionSync.SynchronizeAsync(expectedMap, token);
                            if (token.IsCancellationRequested)
                                return TravelRouteRunResult.Cancelled;

                            if (!sync.IsReady)
                            {
                                _log($"[Path] Step {stepIndex}/{stepCount}: destination map {expectedMap} was reached, but position synchronization did not complete ({sync.Kind}: {sync.Message}).");
                                return TravelRouteRunResult.InvalidMapState;
                            }

                            _log($"[Path] Step {stepIndex}/{stepCount}: destination map {expectedMap} position ready — {sync.State.Describe()}.");
                            return TravelRouteRunResult.Completed;
                        }

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
        /// Routes control after a failed route start check. When the failure ended with
        /// the configured recovery town teleport (validated against the town destination
        /// on its own), the flow is redirected to the existing Repot step instead of
        /// resetting to step 0 / comparing town coordinates with the route start. Every
        /// other failure keeps the existing "restart the flow from the beginning" path.
        /// </summary>
        private void HandleRouteStartProtectionFailure(string context)
        {
            if (_startProtectionRecoveredToTown)
            {
                _startProtectionRecoveredToTown = false;
                _log($"[StartProtection] {context}: recovery town teleport completed and validated — routing the flow to the Repot step for the normal return sequence.");
                if (RedirectToRepotStep($"{context} start protection town recovery"))
                    return;

                // No Repot step exists — fall back to the ordinary restart.
                _flowIndex = 0;
                return;
            }

            _log("[StartProtection] Route start check failed — resetting the flow from the beginning.");
            _flowIndex = 0;
        }

        /// <summary>
        /// Movement gate for a route that is about to start its path (the EXP path runner
        /// and its initial RouteResync in particular). The route is only allowed to start
        /// when the current position is validated for the route's destination context:
        /// its start position when the start check is enabled, otherwise its map
        /// invariant. A pending/unconfirmed transition keeps navigation stopped.
        /// </summary>
        private async Task<bool> EnsureRouteEntryValidatedAsync(
            BotRouteStep route,
            string stageName,
            CancellationToken token)
        {
            if (route == null)
                return false;

            int expectedMap = route.ProtectionMapNumber;

            DestinationValidationContext? destination = route.StartCheckEnabled
                ? DestinationValidationContext.ForStartPosition(
                    expectedMap,
                    route.StartPositionX,
                    route.StartPositionY,
                    $"{stageName} route '{route.PathFile}' start")
                : expectedMap > 0
                    ? DestinationValidationContext.ForMap(expectedMap, $"{stageName} route '{route.PathFile}' map")
                    : null;

            PositionSyncResult sync = await _positionSync.EnsurePositionReadyAsync(
                $"{stageName} route entry '{route.PathFile}'",
                expectedMap,
                destination,
                token);

            if (token.IsCancellationRequested)
                return false;

            if (sync.Kind == PositionSyncResultKind.Ready)
            {
                _log($"[{stageName}] Route entry position validated for '{route.PathFile}': {sync.State.Describe()} DestinationStatus={sync.DestinationStatus}.");
                return true;
            }

            _log($"[{stageName}] Route entry position NOT validated ({sync.Kind}: {sync.Message}; DestinationStatus={sync.DestinationStatus}) — the path runner and initial RouteResync stay blocked so no waypoint is selected from unvalidated coordinates.");
            return false;
        }

        // ======================== Map-based startup entry ========================

        /// <summary>Outcome of the one-time map-based startup detection.</summary>
        private enum StartupEntryOutcome
        {
            /// <summary>A matching action was selected; the flow starts on it.</summary>
            Selected,

            /// <summary>No safe action could be determined; the workflow must not start.</summary>
            Failed
        }

        /// <summary>
        /// Runs the map-based startup detection ONCE for an explicit user-initiated workflow
        /// start and initializes the flow position from it:
        ///   1. Resolve a trustworthy player map/position (coherent reads, stable map,
        ///      post-transition freshness).
        ///   2. Select the FIRST flow action (and the FIRST route inside it) whose starting
        ///      map matches the observed map — see <see cref="BotStartupActionSelector"/>.
        ///   3. Initialize <see cref="_flowIndex"/> plus <see cref="_repotPathIndex"/> or
        ///      <see cref="_stepRouteIndex"/> for the selected action.
        ///
        /// It is called from <see cref="StartAsync"/> only. Routes that were skipped are NOT
        /// marked as completed; after the selected action finishes the normal workflow
        /// progression (and route rotation) continues exactly as before.
        /// </summary>
        private async Task<StartupEntryOutcome> ApplyStartupSelectionAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            var steps = _profile.FlowSteps;
            if (steps == null || steps.Count == 0)
            {
                _log("[Startup] The profile has no flow steps — the workflow will not start.");
                return StartupEntryOutcome.Failed;
            }

            // Every flow action that may be started must declare the map it starts on; the
            // startup never falls back to step 1 and never invents starting maps.
            if (!BotStartupActionSelector.HasConfiguredStartupMaps(_profile))
            {
                _log("[Startup] No flow step or route declares a starting map — the workflow will not start. " +
                     "Configure the starting map of every action/route that may be started (the first action whose starting map matches the player's map is used).");
                return StartupEntryOutcome.Failed;
            }

            _log("[Startup] Map-based startup detection running — resolving the player's current map and position...");
            PlayerStateSnapshot? location = await ResolveStartupLocationAsync(token);
            if (token.IsCancellationRequested || _stopRequested)
                return StartupEntryOutcome.Failed;

            if (location is not { HasCoherentState: true } observed)
            {
                _log("[Startup] No trustworthy player location (map/position) could be validated — the workflow will not start.");
                return StartupEntryOutcome.Failed;
            }

            BotStartupSelection selection = BotStartupActionSelector.Select(_profile, observed.Map);
            switch (selection.Status)
            {
                case BotStartupSelectionStatus.NoStartupMapConfigured:
                    _log($"[Startup] Startup aborted: {selection.Reason} Configure the starting map of the actions that may be started.");
                    return StartupEntryOutcome.Failed;

                case BotStartupSelectionStatus.NoMatchingAction:
                    _log($"[Startup] Startup aborted: observed map {observed.Map} at ({observed.X},{observed.Y}) does not match any configured starting action. {selection.Reason}");
                    _log("[Startup] No unrelated action was chosen and no teleport was attempted. Configure the starting map for the action(s) that belong to this location.");
                    return StartupEntryOutcome.Failed;

                case BotStartupSelectionStatus.InvalidInput:
                    _log($"[Startup] Startup aborted: {selection.Reason}");
                    return StartupEntryOutcome.Failed;
            }

            var step = selection.Step;
            if (step == null)
            {
                _log("[Startup] Startup aborted: the selector returned no step.");
                return StartupEntryOutcome.Failed;
            }

            _flowIndex = selection.FlowIndex;

            if (step.Type == BotFlowStepType.Repot)
            {
                // Alternative repot routes: start with the first matching repot route and let
                // the existing _repotPathIndex advancement rotate from there.
                _repotPathIndex = Math.Max(0, selection.RouteIndex);
            }
            else if (selection.RouteIndex >= 0 && step.Type is BotFlowStepType.Path or BotFlowStepType.ExpLoop)
            {
                // Route group: start with the first matching route; AdvanceRoute() keeps the
                // existing rotation behavior after this execution.
                _stepRouteIndex[step] = selection.RouteIndex;
            }

            if (step.Type is BotFlowStepType.Path or BotFlowStepType.ExpLoop)
            {
                _startupRouteEntry = new StartupRouteEntry(step, Math.Max(0, selection.RouteIndex), selection.StartMapNumber);
            }

            StartupSelectionText = $"flow step {selection.FlowIndex + 1}/{steps.Count}" +
                                    (selection.RouteIndex >= 0 ? $", route {selection.RouteIndex + 1}" : "") +
                                    $", start map {selection.StartMapNumber}";

            _log($"[Startup] Selected {StartupSelectionText}: {DescribeStep(step)}.");
            _log($"[Startup] Reason: {selection.Reason}");
            _log($"[Startup] Player location: map {observed.Map}, position ({observed.X},{observed.Y}). Earlier flow steps are not executed on this start; " +
                 "the route index is initialized for map-based resume and skipped routes are not marked as completed.");

            return StartupEntryOutcome.Selected;
        }

        /// <summary>
        /// Resolves the player's current map/position for the startup detection and proves it
        /// trustworthy before a single waypoint may be selected from it:
        ///
        ///   1. Coherent snapshot gate — the player pointer, the map read, a nonzero map and
        ///      the X/Y read must all succeed (<see cref="GameMemoryService.GetPlayerStateSnapshot"/>;
        ///      one pointer read for map + position, never a pair of independent reads).
        ///   2. Stable map confirmation — the same map must be observed for
        ///      <see cref="BotConstants.Delays.MapTransitionStableReadCount"/> consecutive reads
        ///      inside the existing map-settlement window.
        ///   3. Post-transition freshness — handled through the existing
        ///      <see cref="PositionSyncService"/> lifecycle. A pending transition is resolved,
        ///      and when the profile declares a baseline map/location the player is NOT standing
        ///      on, the location is treated as a post-transition one: its coordinates must be
        ///      proven live (bounded mini-W refresh through PositionSync), so the confirmed COT
        ///      failure — map 36 exposed while X/Y still belong to Etana — can never select a
        ///      waypoint from stale data.
        ///
        /// Returns the validated snapshot, or null when the location could not be proven
        /// (the caller must stop the startup without navigation or teleport).
        /// </summary>
        private async Task<PlayerStateSnapshot?> ResolveStartupLocationAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            // ── 1. Coherent snapshot gate ──
            var coherentDeadline = DateTime.UtcNow.AddMilliseconds(BotConstants.Delays.ValidMapReadTimeoutMs);
            PlayerStateSnapshot snapshot = default;
            bool coherent = false;

            while (DateTime.UtcNow < coherentDeadline)
            {
                token.ThrowIfCancellationRequested();
                snapshot = _memoryService.GetPlayerStateSnapshot();
                if (IsStartupLocationReadCoherent(snapshot))
                {
                    coherent = true;
                    break;
                }

                await PausableDelayAsync(BotConstants.Delays.MapTransitionPollMs, token);
            }

            if (!coherent)
            {
                _log($"[Startup] Player location is not readable ({DescribeStartupLocationFailure(snapshot)}) within {BotConstants.Delays.ValidMapReadTimeoutMs} ms.");
                return null;
            }

            // ── 2. Stable map confirmation (existing synchronization configuration) ──
            int stableMap = snapshot.Map;
            int stableReads = 1;
            var settleDeadline = DateTime.UtcNow.AddMilliseconds(BotConstants.Delays.MapTransitionSettleTimeoutMs);

            while (stableReads < BotConstants.Delays.MapTransitionStableReadCount &&
                   DateTime.UtcNow < settleDeadline)
            {
                await PausableDelayAsync(BotConstants.Delays.MapTransitionPollMs, token);
                token.ThrowIfCancellationRequested();

                PlayerStateSnapshot next = _memoryService.GetPlayerStateSnapshot();
                if (!IsStartupLocationReadCoherent(next))
                {
                    _log($"[Startup] Ignoring an incoherent location read while confirming the map ({DescribeStartupLocationFailure(next)}).");
                    stableReads = 0;
                    continue;
                }

                if (next.Map != stableMap)
                {
                    _log($"[Startup] Map changed during confirmation ({stableMap} -> {next.Map}); restarting map stability confirmation.");
                    stableMap = next.Map;
                    stableReads = 1;
                    snapshot = next;
                    continue;
                }

                stableReads++;
                snapshot = next;
            }

            if (stableReads < BotConstants.Delays.MapTransitionStableReadCount)
            {
                _log($"[Startup] Map did not stabilize within {BotConstants.Delays.MapTransitionSettleTimeoutMs} ms (pending map transition?) — the workflow will not start from an unconfirmed map.");
                return null;
            }

            _log($"[Startup] Location confirmed: map {snapshot.Map}, position ({snapshot.X},{snapshot.Y}) (map read {stableReads}x stable).");

            // ── 3. Post-transition freshness through the existing PositionSync lifecycle ──
            PositionSyncResult sync;

            if (_positionSync.HasPendingTransition)
            {
                _log("[Startup] A position transition is still pending — validating it before any startup decision.");
                sync = await _positionSync.SynchronizeAsync(snapshot.Map, token);
            }
            else if (IsStartupLocationAtDeclaredBaseline(snapshot))
            {
                _log("[Startup] The player stands on the profile's configured start location — no post-transition freshness proof is needed.");
                sync = await _positionSync.EnsurePositionReadyAsync(
                    "startup location detection",
                    snapshot.Map,
                    token);
            }
            else if (ProfileDeclaresStartBaseline)
            {
                // The player is NOT standing where the profile says the run starts. This is
                // exactly the case where the game may still expose the previous map's X/Y
                // after a portal (COT map 36 with Etana coordinates). Treat the observation
                // as a post-transition position: anchor a transition on it and require
                // PositionSync to prove the coordinates are live (bounded mini-W refresh) or
                // stop the startup instead of guessing a waypoint.
                string declaredBaseline = _profile.EnableStartPositionCheck
                    ? $"map {_profile.ProtectionMapNumber} at ({_profile.StartPositionX},{_profile.StartPositionY}) " +
                      $"within tolerance {Math.Max(0, _profile.StartPositionTolerance)}"
                    : $"map {_profile.ProtectionMapNumber} (profile start-position check disabled)";
                _log($"[Startup] The player is not at the profile's declared baseline ({declaredBaseline}) — " +
                     "proving that the observed coordinates belong to the current map before selecting an action.");
                _positionSync.BeginTransition(
                    $"startup location detection (map {snapshot.Map})",
                    snapshot);
                sync = await _positionSync.SynchronizeAsync(snapshot.Map, token);
            }
            else
            {
                // No spawn baseline configured: keep the normal, context-free readiness check
                // (a coherent read is enough when nothing points at a transition).
                sync = await _positionSync.EnsurePositionReadyAsync(
                    "startup location detection",
                    snapshot.Map,
                    token);
            }

            if (token.IsCancellationRequested || _stopRequested)
                return null;

            if (!sync.IsReady)
            {
                _log($"[Startup] Position NOT validated ({sync.Kind}: {sync.Message}; {sync.State.Describe()}) — " +
                     "no waypoint will be selected and no teleport is attempted.");
                return null;
            }

            if (!IsStartupLocationReadCoherent(sync.State))
            {
                _log($"[Startup] Post-validation snapshot is incomplete ({DescribeStartupLocationFailure(sync.State)}) — the workflow will not start.");
                return null;
            }

            if (sync.State.Map != snapshot.Map)
            {
                _log($"[Startup] Map changed during position validation ({snapshot.Map} -> {sync.State.Map}) — restarting the startup decision is not attempted automatically; the workflow will not start.");
                return null;
            }

            _log($"[Startup] Position validated: {sync.State.Describe()} DestinationStatus={sync.DestinationStatus}.");
            return sync.State;
        }

        /// <summary>
        /// True when a startup location read is usable: the player pointer, the map read, a
        /// nonzero map number and the X/Y read all succeeded in the SAME snapshot.
        /// </summary>
        private static bool IsStartupLocationReadCoherent(PlayerStateSnapshot snapshot)
        {
            return snapshot.HasCoherentState &&
                   snapshot.PlayerPointerReadSucceeded &&
                   snapshot.MapReadSucceeded &&
                   snapshot.Map != 0 &&
                   snapshot.PositionReadSucceeded;
        }

        private static string DescribeStartupLocationFailure(PlayerStateSnapshot snapshot)
        {
            if (!snapshot.PlayerPointerReadSucceeded)
                return "player pointer read failed";
            if (!snapshot.MapReadSucceeded)
                return "map read failed";
            if (snapshot.Map == 0)
                return "map read returned 0 (not a valid map)";
            if (!snapshot.PositionReadSucceeded)
                return "X/Y read failed";
            return snapshot.Describe();
        }

        /// <summary>
        /// True when the profile declares the map its run is based on
        /// (<see cref="BotProfile.ProtectionMapNumber"/>). Such a profile says "the bot belongs
        /// to this map", so a startup anywhere else is treated as a post-transition location
        /// whose coordinates must be proven live instead of being trusted from a coherent read
        /// alone. Profiles without a protection map use the plain readiness check.
        /// </summary>
        private bool ProfileDeclaresStartBaseline => _profile.ProtectionMapNumber > 0;

        /// <summary>
        /// True when the snapshot already satisfies the profile's configured start position
        /// (same map + coordinates within the configured tolerance) — i.e. the profile's own
        /// start gate would pass, so no post-transition freshness proof is required. Only an
        /// ENABLED profile start check declares such a position; without it the liveness proof
        /// still runs for a map-based start.
        /// </summary>
        private bool IsStartupLocationAtDeclaredBaseline(PlayerStateSnapshot snapshot)
        {
            if (!ProfileDeclaresStartBaseline || !_profile.EnableStartPositionCheck)
                return false;

            return StartPositionMatches(
                snapshot,
                _profile.StartPositionX,
                _profile.StartPositionY,
                _profile.ProtectionMapNumber,
                Math.Max(0, _profile.StartPositionTolerance),
                out _,
                out _);
        }

        /// <summary>
        /// True when the one-shot startup entry belongs to THIS flow step + route, i.e. the next
        /// execution of this route is the initial resume of the map selected at startup.
        /// </summary>
        private bool IsStartupRouteEntry(BotFlowStep step, int routeIndex)
            => _startupRouteEntry is { } entry &&
               ReferenceEquals(entry.Step, step) &&
               entry.RouteIndex == routeIndex;

        /// <summary>
        /// Consumes the one-shot startup entry exception. It must not survive into retries,
        /// later route executions, repots or workflow cycles, so it is cleared as soon as the
        /// initial route execution has been validated.
        /// </summary>
        private void ConsumeStartupRouteEntry(BotFlowStep step, int routeIndex)
        {
            if (!IsStartupRouteEntry(step, routeIndex))
                return;

            _startupRouteEntry = null;
            _log($"[Startup] The one-shot startup route entry for '{DescribeStep(step)}' (route {routeIndex + 1}) is consumed — " +
                 "all further route entries use the normal configured start protection.");
        }

        /// <summary>
        /// Discards the one-shot startup entry when the workflow position no longer points at the
        /// selected step, so it can never bypass protection for an unrelated later action.
        /// </summary>
        private void DropForeignStartupRouteEntry(BotFlowStep step)
        {
            if (_startupRouteEntry is not { } entry)
                return;
            if (ReferenceEquals(entry.Step, step))
                return;

            _startupRouteEntry = null;
            _log("[Startup] The one-shot startup route entry was discarded because the workflow position moved to a different step before that route started.");
        }

        /// <summary>
        /// Startup-entry variant of the route start gates. The startup detection selected this
        /// route BECAUSE the player already stands on its map, and the startup location was
        /// validated, so the route's configured ENTRANCE coordinates are not demanded (they only
        /// describe one valid point of the hunting route). The map invariant and a validated,
        /// trustworthy position stay mandatory, and no teleport is ever attempted here.
        /// </summary>
        private async Task<bool> EnsureStartupRouteEntryValidatedAsync(
            BotRouteStep route,
            int startupMap,
            string stageName,
            CancellationToken token)
        {
            if (route == null)
                return false;

            int expectedMap = route.ProtectionMapNumber > 0
                ? route.ProtectionMapNumber
                : startupMap;

            if (expectedMap <= 0)
            {
                _log($"[Startup] {stageName} route '{route.PathFile}' has no usable map invariant (route map {route.ProtectionMapNumber}, startup map {startupMap}) — refusing to start the route.");
                return false;
            }

            if (startupMap > 0 && startupMap != expectedMap)
            {
                _log($"[Startup] {stageName} route '{route.PathFile}' validates map {expectedMap} but the startup location is map {startupMap} — refusing to start the route.");
                return false;
            }

            DestinationValidationContext destination = DestinationValidationContext.ForMap(
                expectedMap,
                $"startup {stageName} entry '{route.PathFile}'");

            PositionSyncResult sync = await _positionSync.EnsurePositionReadyAsync(
                $"{stageName} startup entry '{route.PathFile}'",
                expectedMap,
                destination,
                token);

            if (token.IsCancellationRequested)
                return false;

            if (sync.Kind == PositionSyncResultKind.Ready && sync.State.Map == expectedMap)
            {
                _log($"[Startup] {stageName} route '{route.PathFile}' entry validated on map {sync.State.Map} at ({sync.State.X},{sync.State.Y}) " +
                     $"(DestinationStatus={sync.DestinationStatus}). The configured entrance position ({route.StartPositionX},{route.StartPositionY}) is not required for this initial startup resume; " +
                     "MovementSystem resynchronizes to the appropriate waypoint.");
                return true;
            }

            _log($"[Startup] {stageName} route '{route.PathFile}' entry NOT validated ({sync.Kind}: {sync.Message}; {sync.State.Describe()}) — " +
                 "the path runner stays blocked so no waypoint is selected from unvalidated coordinates.");
            return false;
        }

        /// <summary>
        /// Runs the start-position protection for one route that has the start check
        /// enabled (<see cref="BotRouteStep.StartCheckEnabled"/>), using the ROUTE's OWN
        /// start position / protected map / tolerance. Returns true when the route may
        /// proceed; false when the protection failed and the workflow stops.
        /// </summary>
        private async Task<bool> RunRouteStartProtectionAsync(BotRouteStep route, CancellationToken token)
        {
            if (route == null || !route.StartCheckEnabled)
                return true;

            _log($"[StartProtection] Route '{route.PathFile}' has the start check enabled — verifying position before it starts (own start ({route.StartPositionX}, {route.StartPositionY}), map {route.ProtectionMapNumber}, tolerance {route.StartPositionTolerance}).");
            return await RunStartProtectionCoreAsync(
                route.StartPositionX,
                route.StartPositionY,
                route.ProtectionMapNumber,
                Math.Max(0, route.StartPositionTolerance),
                token);
        }

        /// <summary>
        /// Core start-position protection check, used by both the profile-level start
        /// gate and individual routes with the start check enabled. The values checked
        /// (start position, protected map, tolerance) are passed in by the caller —
        /// every route carries its own.
        ///
        /// Before ANY position comparison the check waits for
        /// <see cref="PositionSyncService"/> to prove that the coordinates belong to the
        /// current location AND satisfy the destination expectation of this start check
        /// (map + start position, validated with the fixed synchronization radius).
        /// A map read alone is not enough and neither is a small coordinate shift from
        /// the pre-transition snapshot: immediately after a portal the game can expose
        /// the new map while X/Y still belong to the previous location. In that case a
        /// bounded mini-W refresh runs and the position is only accepted when it matches
        /// the destination expectation or is proven live while genuinely different.
        ///
        /// Only a validated position may produce a genuine mismatch. When validation is
        /// unconfirmed/timed out the check is postponed and the town teleport is BLOCKED
        /// — a stale position must never trigger destructive recovery.
        ///
        /// When the attempt ultimately fails, the check is retried up to
        /// <see cref="BotConstants.Delays.StartProtectionMaxRetries"/> times: each retry
        /// waits (15 s, then x10 per retry: 15s → 150s → 1500s), performs a repot and
        /// starts the whole protection over from scratch. Synchronization failures do
        /// not enter this repot/teleport retry chain. A recovery town teleport ends the
        /// check immediately and hands control back to the flow's normal return path.
        /// Only when every retry fails does the check return false.
        /// </summary>
        private async Task<bool> RunStartProtectionCoreAsync(
            int startX,
            int startY,
            int mapNumber,
            int tolerance,
            CancellationToken token)
        {
            _startProtectionRecoveredToTown = false;

            DestinationValidationContext destination = DestinationValidationContext.ForStartPosition(
                mapNumber,
                startX,
                startY,
                $"start check map {(mapNumber > 0 ? mapNumber.ToString() : "any")} ({startX},{startY})");

            // Attempt 0 is the initial pass; retries 1..MaxRetries add the wait + repot.
            for (int retry = 0; retry <= BotConstants.Delays.StartProtectionMaxRetries; retry++)
            {
                if (retry > 0)
                {
                    // Wait before the retry — 15s, then x10 after each retry
                    // (15s → 150s → 1500s), so a persistently failing protection
                    // backs off progressively instead of hammering the server.
                    double waitSeconds = BotConstants.Delays.StartProtectionRetryBaseWaitSeconds * Math.Pow(10, retry - 1);
                    _log($"[StartProtection] Protection failed — retry {retry}/{BotConstants.Delays.StartProtectionMaxRetries}: waiting {waitSeconds:F0}s, then repot and start over.");
                    await PausableDelayAsync(TimeSpan.FromSeconds(waitSeconds), token);
                    if (token.IsCancellationRequested) return false;

                    await RepotBeforeStartProtectionRetryAsync(token);
                    if (token.IsCancellationRequested) return false;
                }

                _log($"[StartProtection] Waiting for PositionSync (destination validation: {destination.Describe()})...");
                PositionSyncResult sync = await _positionSync.EnsurePositionReadyAsync(
                    $"StartProtection (retry {retry})",
                    mapNumber,
                    destination,
                    token);

                if (token.IsCancellationRequested) return false;

                if (!sync.IsValidated)
                {
                    _log($"[StartProtection] PositionSync {sync.Kind.ToString().ToUpperInvariant()} — {sync.Message}");
                    _log($"[StartProtection] Destination validation UNCONFIRMED (DestinationStatus={sync.DestinationStatus}, Destination={destination.Describe()}, Candidate={sync.State.Describe()}) — Start check postponed.");
                    if (sync.Kind == PositionSyncResultKind.MapNotReached)
                        _log("[StartProtection] Destination map not confirmed — Town teleport BLOCKED (map transition failure, not a position mismatch).");
                    else
                        _log("[StartProtection] Town teleport BLOCKED: position validity not established.");
                    CurrentPhase = BotPhase.Failed;
                    return false;
                }

                if (sync.Kind == PositionSyncResultKind.DestinationMismatch)
                {
                    _log($"[StartProtection] Position VALIDATED but genuinely outside the expected start — recovery ALLOWED (DestinationStatus={sync.DestinationStatus}, Candidate={sync.State.Describe()}, Destination={destination.Describe()}, OffsetX={destination.OffsetX(sync.State)}, OffsetY={destination.OffsetY(sync.State)}).");
                }
                else
                {
                    _log($"[StartProtection] PositionSync READY — {sync.State.Describe()}.");
                }

                StartProtectionAttemptOutcome outcome = await RunStartProtectionAttemptAsync(
                    startX, startY, mapNumber, tolerance, sync.State, token);

                if (outcome == StartProtectionAttemptOutcome.Passed)
                    return true;

                if (token.IsCancellationRequested) return false;

                // A synchronization failure is not a genuine route-position mismatch:
                // do not run the repot/teleport retry chain on it. Stop at this safe
                // boundary — movement must not resume with unvalidated coordinates.
                if (outcome == StartProtectionAttemptOutcome.PositionNotReady)
                {
                    CurrentPhase = BotPhase.Failed;
                    return false;
                }

                // The configured recovery teleported the player to town, and that town
                // destination was validated on its own. The original route start cannot
                // be judged from town — hand control back to the flow's normal return
                // sequence instead of comparing town coordinates with the route start.
                if (outcome == StartProtectionAttemptOutcome.RecoveryTeleported)
                {
                    _startProtectionRecoveredToTown = true;
                    _log("[StartProtection] Recovery teleport validated against the town destination — the start check is not re-evaluated from town and no repeated repot is scheduled here. Returning to the flow's normal recovery sequence.");
                    return false;
                }
            }

            _log("[StartProtection] PROTECTION FAILED — the current map/position do not match the expected start position/protected map after all retries. Stopping.");
            return false;
        }

        private enum StartProtectionAttemptOutcome
        {
            Passed,
            GenuineMismatch,
            PositionNotReady,

            /// <summary>
            /// The configured recovery town teleport completed and its own destination
            /// was validated. The original route start is NOT checked from town.
            /// </summary>
            RecoveryTeleported
        }

        /// <summary>
        /// One start-protection pass using a snapshot whose post-transition freshness has
        /// already been validated by <see cref="PositionSyncService"/> (either against the
        /// start-check destination or as a live, genuinely different position).
        ///
        /// - Map and coordinates match the configured start (and protected map) → PASS.
        /// - Validated coordinates genuinely differ → the existing town-teleport recovery
        ///   is eligible. The teleport is then validated against the TOWN destination
        ///   (in-city flag + synchronized position), never against the original route
        ///   start coordinates of a different map.
        /// - Validation fails after the teleport → POSITION_NOT_READY: no route, no
        ///   further recovery, and no mismatch counter based on untrustworthy data.
        /// </summary>
        private async Task<StartProtectionAttemptOutcome> RunStartProtectionAttemptAsync(
            int startX,
            int startY,
            int mapNumber,
            int tolerance,
            PlayerStateSnapshot trusted,
            CancellationToken token)
        {
            if (!trusted.HasCoherentState)
            {
                _log($"[StartProtection] Start check postponed — no validated player snapshot ({trusted.Describe()}).");
                return StartProtectionAttemptOutcome.PositionNotReady;
            }

            bool mapConfigured = mapNumber > 0;
            bool mapMatches;
            bool posMatches;
            bool matches = StartPositionMatches(
                trusted, startX, startY, mapNumber, tolerance, out mapMatches, out posMatches);

            _log($"[StartProtection] Actual=({trusted.X},{trusted.Y}) Map={trusted.Map}");
            _log($"[StartProtection] Expected=({startX},{startY}) Map={(mapConfigured ? mapNumber.ToString() : "any")}");

            if (matches)
            {
                _log("[StartProtection] PASS");
                return StartProtectionAttemptOutcome.Passed;
            }

            // The snapshot was validated before the comparison, so this is a genuine
            // mismatch — not a stale post-transition read.
            _log("[StartProtection] Genuine mismatch confirmed (validated position, not a stale read)");
            _log($"[StartProtection] Starting configured recovery (mapConfig={mapConfigured}, mapMatches={mapMatches}, positionMatches={posMatches}, tolerance={tolerance}).");
            bool teleportPositionReady = await TeleportToCity(token);
            if (token.IsCancellationRequested)
                return StartProtectionAttemptOutcome.PositionNotReady;

            if (!teleportPositionReady)
                _log("[StartProtection] Town teleport position is not ready yet — waiting for PositionSync before the final decision.");

            // After a town teleport the destination is the TOWN, not the route start.
            // Never demand the route's map here (that made the check compare town
            // coordinates with COT coordinates and schedule endless repots).
            _log("[StartProtection] Waiting for PositionSync after town teleport (town destination validation, map and position of the route are NOT demanded)...");
            PositionSyncResult sync = await _positionSync.EnsurePositionReadyAsync(
                "StartProtection after town teleport",
                expectedMap: 0,
                destination: null,
                token);

            if (token.IsCancellationRequested)
                return StartProtectionAttemptOutcome.PositionNotReady;

            if (!sync.IsReady)
            {
                _log($"[StartProtection] PositionSync {sync.Kind.ToString().ToUpperInvariant()} after town teleport — {sync.Message}");
                _log($"[StartProtection] Start check postponed — {sync.State.Describe()}.");
                _log("[StartProtection] Further recovery BLOCKED: town destination position validity not established.");
                return StartProtectionAttemptOutcome.PositionNotReady;
            }

            PlayerStateSnapshot actual = sync.State;
            bool inCity = _memoryService.GetIsInCity();
            bool actualMapMatches;
            bool actualPosMatches;
            bool actualMatches = StartPositionMatches(
                actual, startX, startY, mapNumber, tolerance, out actualMapMatches, out actualPosMatches);

            _log($"[StartProtection] Actual after teleport=({actual.X},{actual.Y}) Map={actual.Map} InCity={inCity}");
            _log($"[StartProtection] Expected=({startX},{startY}) Map={(mapConfigured ? mapNumber.ToString() : "any")}");

            if (actualMatches)
            {
                _log("[StartProtection] PASS");
                return StartProtectionAttemptOutcome.Passed;
            }

            if (mapConfigured && actual.MapReadSucceeded && actual.Map != mapNumber)
            {
                // The town teleport landed on a DIFFERENT map than the expected start.
                // Comparing its coordinates with the route start is logically invalid.
                // Validate the teleport against its OWN destination (the town) instead.
                if (!inCity)
                {
                    _log($"[StartProtection] Town teleport NOT validated: InCity=False Map={actual.Map} Position=({actual.X},{actual.Y}) — recovery BLOCKED, no further teleport. Route start check postponed.");
                    return StartProtectionAttemptOutcome.PositionNotReady;
                }

                _log($"[StartProtection] Town teleport validated against the town destination: InCity=True Map={actual.Map} Position=({actual.X},{actual.Y}) (COT/route start ({startX},{startY}) map {mapNumber} is NOT compared with town coordinates).");
                return StartProtectionAttemptOutcome.RecoveryTeleported;
            }

            // The recovery teleport stayed on (or returned to) the expected map: the
            // existing comparison remains legitimate and keeps its retry behavior.
            _log($"[StartProtection] CHECK BAD — HERE ({actual.X}, {actual.Y}) map {actual.Map} | EXPECTED ({startX}, {startY}) map {mapNumber} | off {Math.Abs((int)actual.X - startX):F0}/{Math.Abs((int)actual.Y - startY):F0} tiles (tol {tolerance}) even after the town teleport. Not proceeding to the next step.");
            return StartProtectionAttemptOutcome.GenuineMismatch;
        }

        /// <summary>
        /// Pure start-position comparison shared by the pre-teleport check and the
        /// post-teleport re-check. The map and the coordinates are evaluated
        /// independently: reaching the expected map does not make a far position match,
        /// and a position near the start does not make the wrong map acceptable.
        /// </summary>
        private static bool StartPositionMatches(
            PlayerStateSnapshot state,
            int startX,
            int startY,
            int mapNumber,
            int tolerance,
            out bool mapMatches,
            out bool positionMatches)
        {
            mapMatches = mapNumber <= 0 || (state.MapReadSucceeded && state.Map == mapNumber);
            positionMatches = state.PositionReadSucceeded &&
                              Math.Abs((int)state.X - startX) <= tolerance &&
                              Math.Abs((int)state.Y - startY) <= tolerance;
            return mapMatches && positionMatches;
        }

        /// <summary>
        /// Best-effort repot before a start-protection retry: teleport to the city, WALK
        /// the profile's repot path to the shop, and ONLY when that path reports success
        /// (Completed) run the sell/buy sequence — the repot must never pop up while the
        /// player is still standing somewhere else in the city. Failures are logged but
        /// never abort the retry chain — the next protection pass re-checks everything.
        /// </summary>
        private async Task RepotBeforeStartProtectionRetryAsync(CancellationToken token)
        {
            _log("[StartProtection] Repotting before the retry...");
            try
            {
                if (!_memoryService.GetIsInCity())
                {
                    _log("[StartProtection] Not in city — teleporting before repot.");
                    bool teleportPositionReady = await TeleportToCity(token);
                    if (token.IsCancellationRequested) return;
                    if (!teleportPositionReady)
                    {
                        _log("[StartProtection] Position synchronization after the town teleport did not complete — skipping the repot path and shop sequence.");
                        return;
                    }
                }

                // Walk to the repot point FIRST using the profile's Repot step path.
                // The shop sequence runs ONLY after the path completed successfully —
                // never while the player is still god-knows-where in the city.
                var repotRoute = GetRepotRouteForRetry();
                if (repotRoute == null)
                {
                    _log("[StartProtection] No repot path available for the retry — skipping the shop sequence.");
                    return;
                }

                var result = await RunRouteOnceAsync(repotRoute, "StartProtection retry — repot path", token);
                if (token.IsCancellationRequested) return;

                if (result != RouteRunResult.Completed)
                {
                    _log($"[StartProtection] Repot path '{repotRoute.PathFile}' did not complete ({result}) — the player is not at the repot point, skipping the shop sequence.");
                    return;
                }

                if (_profile.DryRunRepot)
                {
                    _log("[StartProtection] DryRunRepot=true — skipping the actual shop sequence.");
                    return;
                }

                _repotSystem.Repot();
                _log("[StartProtection] Repot completed.");
            }
            catch (BotStopRequestedException)
            {
                // The game window lost focus during the seller scan — the whole bot
                // must stop. Rethrow so StartAsync handles it as a stop; the retry
                // chain must not continue.
                throw;
            }
            catch (Exception ex)
            {
                _log($"[StartProtection] Repot failed: {ex.Message} — continuing with the retry.");
            }
        }

        /// <summary>
        /// Returns the next repot path from the profile's Repot flow step (cycling on
        /// each call, mirroring <see cref="ExecuteRepotStepAsync"/>), or null when the
        /// profile has no Repot step / no repot paths configured.
        /// </summary>
        private BotRouteStep? GetRepotRouteForRetry()
        {
            var repotStep = _profile.FlowSteps?.FirstOrDefault(s => s != null && s.Type == BotFlowStepType.Repot);
            var repotPaths = repotStep?.RepotPaths ?? new List<BotRouteStep>();
            if (repotPaths.Count == 0) return null;

            if (_repotPathIndex < 0 || _repotPathIndex >= repotPaths.Count)
                _repotPathIndex = 0;

            var route = repotPaths[_repotPathIndex];
            _repotPathIndex = (_repotPathIndex + 1) % repotPaths.Count;
            return route;
        }

        /// <summary>
        /// Taps the W key very briefly (like a real player taking a single step). The
        /// release is guaranteed by <see cref="PositionSyncService.PressWRefreshAsync"/>,
        /// including cancellation and exceptions. Position synchronization owns the
        /// actual refresh decision; this method remains as the StartProtection-facing
        /// wrapper for the existing mini-W capability.
        /// </summary>
        private async Task NudgeMoveAsync(CancellationToken token)
        {
            await WaitIfPausedAsync(token);
            if (token.IsCancellationRequested) return;

            _log("[StartProtection] Quick step W refresh requested...");
            await _positionSync.PressWRefreshAsync(token);
        }

        /// <summary>
        /// Takes a fresh snapshot and captures a low-HP-potions screenshot when the
        /// teleport is needed because of low HP potions. Used right before teleporting
        /// from the Repot step (which can be reached without going through ExpLoop,
        /// e.g. the flow starts with an empty potion stock).
        /// </summary>
        private void CaptureLowHpPotionsScreenshotIfNeeded()
        {
            try
            {
                var snapshot = _memoryService.GetSnapshot();
                if (_repotDetector.IsLowHpPotions(snapshot))
                    CaptureLowHpPotionsScreenshot(snapshot);
            }
            catch (Exception ex)
            {
                _log($"[Repot] Failed to check HP potions before screenshot: {ex.Message}");
            }
        }

        /// <summary>
        /// Captures ALL screens (full virtual screen across every monitor) and saves it
        /// to Screenshots/LowHpPotions/ when the bot needs to teleport because of low HP potions.
        /// </summary>
        private void CaptureLowHpPotionsScreenshot(GameSnapshot snapshot)
        {
            try
            {
                _log($"[Repot] Low HP potions ({snapshot.HpPotions} <= {_repotDetector.MinHpPotions}) — capturing screenshot before teleport.");

                var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;

                using (Bitmap bitmap = new Bitmap(virtualScreen.Width, virtualScreen.Height))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(virtualScreen.X, virtualScreen.Y, 0, 0, bitmap.Size);

                    string screenshotsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Screenshots", "LowHpPotions");
                    Directory.CreateDirectory(screenshotsDir);

                    string fileName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";
                    string filePath = Path.Combine(screenshotsDir, fileName);

                    bitmap.Save(filePath, ImageFormat.Png);
                    _log($"[Repot] Low HP potions screenshot saved: {filePath}");
                }
            }
            catch (Exception ex)
            {
                _log($"[Repot] Failed to capture low HP potions screenshot: {ex.Message}");
            }
        }

        /// <summary>
        /// Destination evidence for a town arrival. The profile's start baseline is the
        /// known town position (see <see cref="BotProfile.EnableStartPositionCheck"/>): the
        /// town teleport can stay on the SAME map and return the player to the same spawn,
        /// so a map change or a coordinate delta cannot be the only acceptance signal.
        /// Reusing the fixed synchronization radius keeps the user-configurable start
        /// tolerance out of the freshness decision. Profiles without a configured town
        /// baseline keep the legacy context-free behavior (a changed response to W or a
        /// position clearly outside the pre-teleport neighbourhood).
        /// </summary>
        private DestinationValidationContext? CreateTownDestinationValidationContext()
        {
            if (!_profile.EnableStartPositionCheck)
                return null;

            if (_profile.ProtectionMapNumber <= 0)
                return null;

            if (_profile.StartPositionX == 0 && _profile.StartPositionY == 0)
                return null;

            return DestinationValidationContext.ForStartPosition(
                _profile.ProtectionMapNumber,
                _profile.StartPositionX,
                _profile.StartPositionY,
                "town start position (profile)");
        }

        /// <summary>
        /// Teleports to the city with the profile's teleport key, waits for the game UI
        /// to settle and validates the post-transition position. A screenshot is captured
        /// right after the teleport scroll is triggered (before the town loads) into
        /// Screenshots/TownPortals. The pre-teleport snapshot is marked BEFORE the key is
        /// sent so stale city coordinates can be detected instead of being compared with
        /// the route start.
        ///
        /// The transition is marked as a MANDATORY W refresh: an in-town teleport can keep
        /// the same map and publish a partially stale coordinate, so
        /// <see cref="PositionSyncService"/> always presses W, re-reads the settled
        /// position and validates it before the route / RouteResync may start from it.
        /// </summary>
        private async Task<bool> TeleportToCity(CancellationToken token)
        {
            await WaitIfPausedAsync(token);
            if (token.IsCancellationRequested) return false;

            byte vk = (byte)_profile.TeleportKey;
            byte scan = (byte)_profile.TeleportScanCode;

            _lastTownTeleportSync = null;
            _positionSync.MarkTransitionImminent("TeleportToCity", requireWRefresh: true);

            bool arrived;
            if (TownTeleportOverride is { } townTeleport)
            {
                _log("[Teleport] Town teleport input/arrival provided by the override; the post-teleport position validation still runs.");
                arrived = await townTeleport(token);
            }
            else
            {
                // Make sure the game window is focused before injecting the teleport
                // key, otherwise keybd_event may deliver the keypress to another window.
                _focusGameWindow();

                _log($"[Teleport] Pressing key (vk={vk}) for town teleport...");
                keybd_event(vk, scan, 0, 0);
                try
                {
                    await PausableDelayAsync(BotConstants.Delays.TeleportKeyDownMs, token);
                }
                finally
                {
                    // Stop/cancellation during the key-down interval must never leave the
                    // teleport key physically held or allow another phase to inherit it.
                    keybd_event(vk, scan, KEYEVENTF_KEYUP, 0);
                }

                token.ThrowIfCancellationRequested();
                // Screenshot immediately after the teleport scroll is triggered — before the
                // town starts loading — so the image shows the state at the moment of teleport.
                ScreenshotService.CaptureFullScreen(ScreenshotService.TownPortalsFolder, _log, "[Teleport]");

                arrived = false;
                for (int i = 0; i < BotConstants.Delays.TeleportWaitIterations; i++)
                {
                    await PausableDelayAsync(BotConstants.Delays.TeleportWaitIterationMs, token);
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
                await PausableDelayAsync(BotConstants.Delays.PostTeleportUiLoadMs, token);
            }

            // The town teleport is validated against its OWN destination: the arrival flag
            // plus a coherent, fresh position on whatever map the town is. When the profile
            // declares its town baseline, that baseline is the destination expectation, so
            // a same-map teleport to the same spawn with a blocked W is not rejected as
            // stale merely because the coordinates did not change. The route start of the
            // location we teleported AWAY from is never used here.
            DestinationValidationContext? townDestination = CreateTownDestinationValidationContext();
            _log($"[Teleport] Waiting for PositionSync after town teleport... InCity={_memoryService.GetIsInCity()} Destination={(townDestination != null ? townDestination.Describe() : "none")}");
            PositionSyncResult sync = await _positionSync.SynchronizeAsync(expectedMap: 0, destination: townDestination, token);
            _lastTownTeleportSync = sync;
            if (token.IsCancellationRequested)
                return false;

            if (sync.IsReady)
            {
                _log($"[Teleport] Town destination validated: {sync.State.Describe()} InCity={_memoryService.GetIsInCity()} DestinationStatus={sync.DestinationStatus}.");
                return true;
            }

            _log($"[Teleport] PositionSync {sync.Kind.ToString().ToUpperInvariant()} — {sync.Message} ({sync.State.Describe()}). Movement/start checks stay blocked until the position is validated.");
            return false;
        }
    }
}
