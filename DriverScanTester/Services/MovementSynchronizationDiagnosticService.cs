using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;

namespace DriverScanTester.Services
{
    public sealed class MovementDiagnosticOnlyMoveContext
    {
        public MovementDiagnosticOnlyMoveContext(
            GameMemoryService memoryService,
            float targetX,
            float targetY,
            MovementDiagnosticSettings settings,
            Action<string> log,
            Action<string> recordRelevantLog)
        {
            MemoryService = memoryService;
            TargetX = targetX;
            TargetY = targetY;
            Settings = settings;
            Log = log;
            RecordRelevantLog = recordRelevantLog;
        }

        public GameMemoryService MemoryService { get; }
        public float TargetX { get; }
        public float TargetY { get; }
        public MovementDiagnosticSettings Settings { get; }
        public Action<string> Log { get; }
        public Action<string> RecordRelevantLog { get; }
        public BotMode Mode => BotMode.OnlyMove;
        public MovementSteeringMode SteeringMode => MovementSteeringMode.KeyboardTurn;
    }

    public sealed class MovementDiagnosticMovementRunResult
    {
        public bool GoalReached { get; set; }
        public string SteeringMode { get; set; } = MovementSteeringMode.KeyboardTurn.ToString();
        public bool DirectCameraRecoveryInvoked { get; set; }
        public List<string> MovementRecoveryEvents { get; set; } = new();
        public int UpdateTicks { get; set; }
        public string Detail { get; set; } = "";
    }

    /// <summary>
    /// Independent movement-synchronization experiment runner. It never modifies the
    /// production movement controller: it either injects isolated diagnostic input or
    /// hosts an unmodified MovementSystem for TEST 9. Only one experiment can run at a
    /// time, every synthetic key is tracked, and the post-test observation phase sends
    /// no input and performs no camera writes.
    /// </summary>
    public sealed class MovementSynchronizationDiagnosticService : IDisposable
    {
        private const int MinDurationMs = 100;
        private const int MaxDurationMs = 300_000;
        private const int MinSampleIntervalMs = 20;
        private const int MaxSampleIntervalMs = 5_000;

        private readonly Action<string> _log;
        private readonly Func<bool> _isAttached;
        private readonly Func<uint> _getPid;
        private readonly Func<GameMemoryService?> _createMemoryService;
        private readonly Action _focusGame;
        private readonly Func<string?> _getConflictReason;
        private readonly Func<(float x, float y, bool success)>? _readPlayerPosition;
        private readonly Func<uint> _getForegroundProcessId;
        private readonly Func<MovementDiagnosticInputMode, IMovementDiagnosticKeyboardBackend> _backendFactory;
        private readonly Func<uint, Action<string>, IMovementDiagnosticMouseTurnAssist> _mouseAssistFactory;
        private readonly Func<MovementDiagnosticOnlyMoveContext, CancellationToken, Task<MovementDiagnosticMovementRunResult>> _onlyMoveRunner;
        private readonly MovementDiagnosticReportWriter _reportWriter;

        private readonly object _sync = new();
        private readonly List<MovementDiagnosticSession> _history = new();

        private MovementDiagnosticSession? _currentSession;
        private Task? _testTask;
        private Task? _observationTask;
        private Task? _sampleTask;
        private CancellationTokenSource? _testCts;
        private CancellationTokenSource? _observationCts;
        private CancellationTokenSource? _sampleCts;

        private DiagnosticKeyOwnership? _keys;
        private IMovementDiagnosticMouseTurnAssist? _mouseAssist;
        private IMovementDiagnosticKeyboardBackend? _inputBackend;
        private GameMemoryService? _sessionMemory;

        private MovementDiagnosticPhase _phase = MovementDiagnosticPhase.Idle;
        private string _message = "Idle — select a scenario and press Start Test.";
        private string _lastResult = "No test run yet.";
        private string _lastCheckpoint = "";
        private string _activeInputMethod = "";
        private TimeSpan? _countdownRemaining;
        private TimeSpan? _holdElapsed;
        private TimeSpan? _holdTarget;
        private string _holdLabel = "";
        private bool _suppressAutoObservation;
        private bool _lastCleanupComplete = true;
        private int _isDisposed;
        private readonly ConcurrentQueue<string> _logQueue = new();
        private readonly SemaphoreSlim _logSignal = new(0);
        private readonly CancellationTokenSource _logCts = new();
        private readonly Task _logPump;

        public MovementSynchronizationDiagnosticService(
            Action<string> log,
            Func<bool> isAttached,
            Func<uint> getPid,
            Func<GameMemoryService?> createMemoryService,
            Action focusGame,
            Func<string?> getConflictReason,
            Func<(float x, float y, bool success)>? readPlayerPosition = null,
            Func<uint>? getForegroundProcessId = null,
            Func<MovementDiagnosticInputMode, IMovementDiagnosticKeyboardBackend>? backendFactory = null,
            Func<uint, Action<string>, IMovementDiagnosticMouseTurnAssist>? mouseAssistFactory = null,
            Func<MovementDiagnosticOnlyMoveContext, CancellationToken, Task<MovementDiagnosticMovementRunResult>>? onlyMoveRunner = null,
            MovementDiagnosticReportWriter? reportWriter = null)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _isAttached = isAttached ?? throw new ArgumentNullException(nameof(isAttached));
            _getPid = getPid ?? throw new ArgumentNullException(nameof(getPid));
            _createMemoryService = createMemoryService ?? throw new ArgumentNullException(nameof(createMemoryService));
            _focusGame = focusGame ?? throw new ArgumentNullException(nameof(focusGame));
            _getConflictReason = getConflictReason ?? throw new ArgumentNullException(nameof(getConflictReason));
            _readPlayerPosition = readPlayerPosition;
            _getForegroundProcessId = getForegroundProcessId ?? MovementDiagnosticNative.GetForegroundProcessId;
            _backendFactory = backendFactory ?? MovementDiagnosticInputBackendFactory.Create;
            _mouseAssistFactory = mouseAssistFactory ??
                ((pid, serviceLog) => new MovementDiagnosticMouseTurnAssist(pid, serviceLog));
            _onlyMoveRunner = onlyMoveRunner ?? RunExistingMovementSystemOnlyMoveAsync;
            _reportWriter = reportWriter ?? new MovementDiagnosticReportWriter(log);
            _logPump = Task.Run(LogPumpAsync);
        }

        public string ReportRootDirectory => _reportWriter.RootDirectory;

        public bool BlocksOtherRuntimes
        {
            get
            {
                lock (_sync)
                    return IsTestActiveLocked() || IsObservationActiveLocked();
            }
        }

        public bool IsTestActive
        {
            get { lock (_sync) return IsTestActiveLocked(); }
        }

        public bool IsObservationActive
        {
            get { lock (_sync) return IsObservationActiveLocked(); }
        }

        public bool HasCurrentSession
        {
            get { lock (_sync) return _currentSession != null; }
        }

        public bool CanStartObservation
        {
            get
            {
                lock (_sync)
                    return _currentSession?.InputInjectionStoppedUtc != null &&
                           !IsTestActiveLocked() && !IsObservationActiveLocked();
            }
        }

        public bool CanMarkRecovery
        {
            get
            {
                lock (_sync)
                    return _currentSession?.InputInjectionStoppedUtc != null &&
                           _currentSession.RecoveryObservedUtc == null;
            }
        }

        public bool LastCleanupComplete
        {
            get { lock (_sync) return _lastCleanupComplete; }
        }

        /// <summary>Test/diagnostic inspection only; returns a detached snapshot.</summary>
        internal MovementDiagnosticSession? GetCurrentSessionSnapshotForTesting()
        {
            lock (_sync)
                return _currentSession?.CloneForReport(includeSamplesAndEvents: true);
        }

        public MovementDiagnosticStatus GetStatus()
        {
            lock (_sync)
            {
                MovementDiagnosticSession? session = _currentSession;
                return new MovementDiagnosticStatus
                {
                    Phase = _phase,
                    Scenario = session?.Scenario,
                    ScenarioDisplay = session == null
                        ? ""
                        : MovementDiagnosticScenarioCatalog.GetDisplayText(session.Scenario),
                    Message = _message,
                    InputMethodDescription = string.IsNullOrEmpty(_activeInputMethod)
                        ? session?.InputMethodDescription ?? ""
                        : _activeInputMethod,
                    SessionId = session?.SessionId ?? "",
                    Elapsed = session?.Stopwatch.Elapsed ?? TimeSpan.Zero,
                    CountdownRemaining = _countdownRemaining,
                    HoldElapsed = _holdElapsed,
                    HoldTarget = _holdTarget,
                    HoldLabel = _holdLabel,
                    LastCheckpoint = _lastCheckpoint,
                    LastResult = _lastResult,
                    OutputDirectory = session?.OutputDirectory,
                    InputInjectionStoppedUtc = session?.InputInjectionStoppedUtc,
                    RecoveryDurationMs = session?.RecoveryDurationMs,
                    IsTestActive = IsTestActiveLocked(),
                    IsObservationActive = IsObservationActiveLocked(),
                    HasCurrentSession = session != null,
                    HasUnreleasedKeys = _keys?.HasOwnedKeys == true
                };
            }
        }

        public async Task<MovementDiagnosticStartResult> StartTestAsync(
            MovementDiagnosticScenario scenario,
            MovementDiagnosticSettings requestedSettings)
        {
            if (Volatile.Read(ref _isDisposed) != 0)
                return MovementDiagnosticStartResult.Reject("The diagnostic service is disposed.");

            MovementDiagnosticSession session;
            MovementDiagnosticSettings settings;
            GameMemoryService memoryService;
            CancellationTokenSource testCts;

            lock (_sync)
            {
                if (IsTestActiveLocked())
                    return MovementDiagnosticStartResult.Reject("A diagnostic experiment is already running. Wait for it to finish or press Stop Test.");
                if (IsObservationActiveLocked())
                    return MovementDiagnosticStartResult.Reject("Post-test observation is active. Stop observation before starting another experiment.");
                if (!_lastCleanupComplete || _keys?.HasOwnedKeys == true)
                    return MovementDiagnosticStartResult.Reject("A previous diagnostic cleanup did not release every synthetic key. Restart the game/application before another experiment.");
                if (!_isAttached())
                    return MovementDiagnosticStartResult.Reject("Attach to the game before starting a movement diagnostic.");

                MovementDiagnosticScenarioInfo info = MovementDiagnosticScenarioCatalog.Get(scenario);
                if (info.RequiresAdvancedMode && !requestedSettings.AdvancedModeEnabled)
                    return MovementDiagnosticStartResult.Reject("DirectCamera memory steering is an advanced test. Enable the advanced mode toggle first.");

                string? conflict = _getConflictReason();
                if (!string.IsNullOrWhiteSpace(conflict))
                    return MovementDiagnosticStartResult.Reject($"Diagnostic refused: {conflict}");

                settings = NormalizeSettings(requestedSettings);
                memoryService = _createMemoryService()!;
                if (memoryService == null)
                    return MovementDiagnosticStartResult.Reject("Could not create a GameMemoryService (attach and resolve Ares.exe first).");

                if (scenario == MovementDiagnosticScenario.ExistingMovementSystemOnlyMove)
                {
                    string? waypointError = ValidateSafeWaypoint(settings, memoryService);
                    if (waypointError != null)
                        return MovementDiagnosticStartResult.Reject(waypointError);
                }

                _suppressAutoObservation = false;
                _currentSession = CreateSession(scenario, info, settings);
                _currentSession.GameProcessId = _getPid();
                session = _currentSession;
                _history.Add(session);
                _sessionMemory = memoryService;
                _activeInputMethod = info.InputMethodDescription;
                _phase = MovementDiagnosticPhase.Countdown;
                _message = $"{info.DisplayText} — preparing.";
                _countdownRemaining = null;
                _holdElapsed = null;
                _holdTarget = null;
                _holdLabel = "";
                _lastCheckpoint = "";
                _lastResult = "Test running...";

                StartSamplerLocked(session);

                testCts = new CancellationTokenSource();
                _testCts = testCts;
                CancellationToken token = testCts.Token;
                _testTask = Task.Run(() => RunTestSessionAsync(session, memoryService, token));
            }

            string accepted = $"{MovementDiagnosticScenarioCatalog.GetDisplayText(scenario)} accepted (session {session.SessionId}).";
            EnqueueLog($"[MovementDiag] {accepted}");
            return await Task.FromResult(MovementDiagnosticStartResult.Ok(accepted));
        }

        public async Task<MovementDiagnosticStartResult> StopTestAsync(string reason = "Stop Test")
        {
            Task? task;
            CancellationTokenSource? cts;
            lock (_sync)
            {
                task = _testTask;
                cts = _testCts;
                if (task == null || task.IsCompleted)
                    return MovementDiagnosticStartResult.Reject("No diagnostic experiment is currently running.");
                _message = $"Diagnostic test — stopping ({reason}) and releasing diagnostic input.";
                _phase = MovementDiagnosticPhase.CleaningUp;
            }

            EnqueueLog($"[MovementDiag] Stop requested: {reason}.");
            try { cts?.Cancel(); } catch { }

            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                EnqueueLog($"[MovementDiag] Stop Test observed a task error: {ex.Message}");
            }

            return MovementDiagnosticStartResult.Ok("Diagnostic stop completed; cleanup result is shown in the status/log.");
        }

        public async Task<MovementDiagnosticStartResult> StartPostTestObservationAsync()
        {
            MovementDiagnosticSession? session;
            lock (_sync)
            {
                session = _currentSession;
                if (session == null)
                    return MovementDiagnosticStartResult.Reject("Run an experiment first.");
                if (session.InputInjectionStoppedUtc == null)
                    return MovementDiagnosticStartResult.Reject("The experiment has not stopped its synthetic input yet.");
                if (IsTestActiveLocked())
                    return MovementDiagnosticStartResult.Reject("Wait until the test task and its input cleanup have finished.");
                if (IsObservationActiveLocked())
                    return MovementDiagnosticStartResult.Reject("Post-test observation is already active.");

                StartObservationLocked(session, respectGlobalSuppression: false);
            }

            EnqueueLog($"[MovementDiag] Post-test observation started manually for {session.SessionId}.");
            return await Task.FromResult(MovementDiagnosticStartResult.Ok("Post-test observation started."));
        }

        public async Task<MovementDiagnosticStartResult> StopObservationAsync(string reason = "Stop Observation")
        {
            Task? observation;
            CancellationTokenSource? cts;
            lock (_sync)
            {
                observation = _observationTask;
                cts = _observationCts;
                if (observation == null || observation.IsCompleted)
                    return MovementDiagnosticStartResult.Reject("No post-test observation is currently active.");
                _message = $"Post-test observation — stopping ({reason}).";
            }

            EnqueueLog($"[MovementDiag] Post-test observation stop requested: {reason}.");
            try { cts?.Cancel(); } catch { }

            try
            {
                await observation.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                EnqueueLog($"[MovementDiag] Observation stop observed an error: {ex.Message}");
            }

            await StopSamplerAsync().ConfigureAwait(false);
            WriteReport("observation stopped");
            return MovementDiagnosticStartResult.Ok("Post-test observation stopped.");
        }

        public MovementDiagnosticMarkResult MarkRecoveryObserved()
        {
            MovementDiagnosticSession? session;
            DateTime utcNow = DateTime.UtcNow;
            double durationMs;
            string checkpoint;

            lock (_sync)
            {
                session = _currentSession;
                if (session == null)
                    return MovementDiagnosticMarkResult.Reject("No diagnostic session exists.");
                if (session.InputInjectionStoppedUtc == null)
                    return MovementDiagnosticMarkResult.Reject("Synthetic input has not stopped yet; wait for INPUT_INJECTION_STOPPED.");

                if (session.RecoveryObservedUtc.HasValue)
                {
                    return MovementDiagnosticMarkResult.Ok(
                        $"Recovery was already marked at {session.RecoveryObservedUtc.Value:O}.",
                        session.RecoveryDurationMs ?? 0);
                }

                durationMs = (utcNow - session.InputInjectionStoppedUtc.Value).TotalMilliseconds;
                session.RecoveryObservedUtc = utcNow;
                session.RecoveryObservedElapsedMs = session.Stopwatch.Elapsed.TotalMilliseconds;
                session.RecoveryDurationMs = durationMs;
                checkpoint = string.IsNullOrEmpty(_lastCheckpoint) ? "Manual mark" : _lastCheckpoint;
                AddEventLocked(
                    session,
                    "RECOVERY_OBSERVED_MANUAL",
                    $"Movement recovered per user observation after {durationMs:F0} ms from confirmed input stop.",
                    details: $"checkpoint={checkpoint}; inputStoppedUtc={session.InputInjectionStoppedUtc.Value:O}; recoveryObservedUtc={utcNow:O}",
                    durationMs: durationMs);
                _message = $"Recovery marked — {durationMs:F0} ms after confirmed input stop.";
            }

            WriteReport("manual recovery marked");
            EnqueueLog($"[MovementDiag] RECOVERY_OBSERVED_MANUAL session={session.SessionId} duration={durationMs:F0} ms.");
            return MovementDiagnosticMarkResult.Ok(
                $"Recovery marked: {durationMs:F0} ms after input stopped.",
                durationMs);
        }

        public MovementDiagnosticStartResult RecordObserverFeedback(
            MovementRemoteObservationRating rating,
            bool persistedAfterStop,
            bool observedDuringManualMovement,
            string? notes)
        {
            MovementDiagnosticSession? session;
            string checkpoint;
            lock (_sync)
            {
                session = _currentSession;
                if (session == null)
                    return MovementDiagnosticStartResult.Reject("Run a diagnostic experiment first.");
                if (session.InputInjectionStoppedUtc == null)
                    return MovementDiagnosticStartResult.Reject("Record observer feedback after the experiment stops its synthetic input.");
                if (rating == MovementRemoteObservationRating.NotRecorded)
                    return MovementDiagnosticStartResult.Reject("Select what the second client showed before saving the observation.");

                checkpoint = string.IsNullOrEmpty(_lastCheckpoint)
                    ? (IsObservationActiveLocked() ? "During observation" : "Manual")
                    : _lastCheckpoint;
                var record = new MovementDiagnosticObserverRecord
                {
                    UtcTimestamp = DateTime.UtcNow,
                    ElapsedMs = session.Stopwatch.Elapsed.TotalMilliseconds,
                    Checkpoint = checkpoint,
                    Rating = rating,
                    PersistedAfterSyntheticInputStopped = persistedAfterStop,
                    ObservedDuringSubsequentManualMovement = observedDuringManualMovement,
                    Notes = notes?.Trim() ?? ""
                };
                session.Observations.Add(record);

                AddEventLocked(
                    session,
                    "OBSERVER_RECORDED",
                    $"Observer rating saved: {MovementDiagnosticScenarioCatalog.GetObservationLabel(rating)}.",
                    details:
                    $"checkpoint={record.Checkpoint}; persistedAfterStop={persistedAfterStop}; " +
                    $"duringSubsequentManualMovement={observedDuringManualMovement}; notes={record.Notes}");
                _message = "Observer feedback saved. The report can be exported independently of test outcome.";
            }

            WriteReport("observer feedback saved");
            EnqueueLog($"[MovementDiag] OBSERVER_RECORDED session={session.SessionId} rating={MovementDiagnosticScenarioCatalog.GetObservationLabel(rating)} checkpoint={checkpoint}.");
            return MovementDiagnosticStartResult.Ok("Observer feedback saved.");
        }

        public async Task<string?> ExportReportAsync()
        {
            string? path = await Task.Run(() => WriteReport("manual export")).ConfigureAwait(false);
            EnqueueLog(path == null
                ? "[MovementDiag] Export failed — no session is available."
                : $"[MovementDiag] Export completed: {path}");
            return path;
        }

        public async Task<bool> WaitForTestCompletionAsync(TimeSpan timeout)
        {
            Task? task;
            lock (_sync)
                task = _testTask;

            if (task == null)
                return true;

            Task completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
            return ReferenceEquals(completed, task);
        }

        public async Task<bool> WaitForObservationCompletionAsync(TimeSpan timeout)
        {
            Task? task;
            lock (_sync)
                task = _observationTask;

            if (task == null)
                return true;

            Task completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
            return ReferenceEquals(completed, task);
        }

        /// <summary>
        /// Global Stop All integration: aborts the test and observation, waits a bounded
        /// time for real cleanup, and reports honestly when the wait did not complete.
        /// </summary>
        public void StopForGlobalStop(string reason)
        {
            _suppressAutoObservation = true;
            Task? testTask;
            Task? observationTask;
            CancellationTokenSource? testCts;
            CancellationTokenSource? observationCts;

            lock (_sync)
            {
                testTask = _testTask;
                observationTask = _observationTask;
                testCts = _testCts;
                observationCts = _observationCts;
                _message = $"Global Stop All — stopping diagnostic ({reason}).";
            }

            if (testTask == null && observationTask == null)
                return;

            EnqueueLog($"[MovementDiag] Global Stop All is stopping the diagnostic ({reason}).");
            try { testCts?.Cancel(); } catch { }
            try { observationCts?.Cancel(); } catch { }

            bool testDone = WaitBounded(testTask, TimeSpan.FromSeconds(5), "test");
            bool observationDone = WaitBounded(observationTask, TimeSpan.FromSeconds(2), "observation");
            if (testDone && observationDone)
                EnqueueLog("[MovementDiag] Global Stop All cleanup complete: diagnostic tasks ended.");
            else
                EnqueueLog("[MovementDiag] Global Stop All cleanup INCOMPLETE: one or more diagnostic tasks did not end within the bounded wait.");
        }

        /// <summary>Closing the Bot window also safely stops an active experiment.</summary>
        public void StopForWindowClose()
        {
            _suppressAutoObservation = true;
            StopForWindowCloseCore();
        }

        public void StopForApplicationExit()
        {
            _suppressAutoObservation = true;
            StopForWindowCloseCore();
        }

        private void StopForWindowCloseCore()
        {
            Task? testTask;
            Task? observationTask;
            CancellationTokenSource? testCts;
            CancellationTokenSource? observationCts;
            lock (_sync)
            {
                testTask = _testTask;
                observationTask = _observationTask;
                testCts = _testCts;
                observationCts = _observationCts;
                if (testTask == null && observationTask == null)
                    return;
                _message = "Closing diagnostic owner — stopping active experiment.";
            }

            EnqueueLog("[MovementDiag] Diagnostic owner is closing; performing bounded cleanup.");
            try { testCts?.Cancel(); } catch { }
            try { observationCts?.Cancel(); } catch { }

            bool testDone = WaitBounded(testTask, TimeSpan.FromSeconds(2), "test");
            bool observationDone = WaitBounded(observationTask, TimeSpan.FromSeconds(1), "observation");
            if (testDone && observationDone)
                EnqueueLog("[MovementDiag] Window-close cleanup complete: diagnostic tasks ended.");
            else
                EnqueueLog("[MovementDiag] Window-close cleanup INCOMPLETE: a diagnostic task did not end within the bounded wait.");
        }

        private bool WaitBounded(Task? task, TimeSpan timeout, string label)
        {
            if (task == null)
                return true;
            try
            {
                return task.Wait(timeout);
            }
            catch (Exception ex)
            {
                EnqueueLog($"[MovementDiag] Waiting for {label} task failed: {ex.GetBaseException().Message}");
                return task.IsCompleted;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
                return;
            StopForApplicationExit();
            StopLogPump();
        }

        private void EnqueueLog(string message)
        {
            if (string.IsNullOrEmpty(message) || _logCts.IsCancellationRequested)
                return;
            _logQueue.Enqueue(message);
            try { _logSignal.Release(); } catch (ObjectDisposedException) { }
        }

        private async Task LogPumpAsync()
        {
            try
            {
                while (!_logCts.IsCancellationRequested)
                {
                    await _logSignal.WaitAsync(_logCts.Token).ConfigureAwait(false);
                    DrainLogQueue();
                }
            }
            catch (OperationCanceledException)
            {
                DrainLogQueue();
            }
            catch
            {
                // The diagnostic log pump must never break input cleanup.
            }
        }

        private void DrainLogQueue()
        {
            while (_logQueue.TryDequeue(out string? line))
            {
                try { _log(line); }
                catch { }
            }
        }

        private void StopLogPump()
        {
            try { _logCts.Cancel(); } catch { }
            try { _logSignal.Release(); } catch (ObjectDisposedException) { }
            try { _logPump.Wait(TimeSpan.FromSeconds(1)); } catch { }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Session execution
        // ─────────────────────────────────────────────────────────────────────

        private async Task RunTestSessionAsync(
            MovementDiagnosticSession session,
            GameMemoryService memoryService,
            CancellationToken token)
        {
            double movementStartedElapsedMs = session.Stopwatch.Elapsed.TotalMilliseconds;

            try
            {
                AddEvent(
                    session,
                    "SESSION_STARTED",
                    $"Diagnostic session {session.SessionId} started.",
                    details: $"scenario={MovementDiagnosticScenarioCatalog.GetDisplayText(session.Scenario)}; pid={session.GameProcessId}");
                RecordGameContext(session, memoryService);
                AddEvent(
                    session,
                    "SCENARIO_STARTED",
                    MovementDiagnosticScenarioCatalog.GetDisplayText(session.Scenario),
                    details: BuildSettingsDetails(session.Settings));

                // Focus is requested up front so the countdown is visible on the game.
                _focusGame();
                AddEvent(session, "GAME_FOCUS_REQUESTED", "Requested foreground ownership for the game window.");

                if (session.Scenario == MovementDiagnosticScenario.ManualKeyboardBaseline)
                {
                    await FocusAndValidateAsync(session, strict: false, token).ConfigureAwait(false);
                    AddEvent(
                        session,
                        "MANUAL_BASELINE_INSTRUCTION",
                        "Move manually with W, A and D. Observe the same movement on the second client. No synthetic input will be sent.");
                    await CountdownAsync(session, MovementDiagnosticScenarioCatalog.GetDisplayText(session.Scenario), token)
                        .ConfigureAwait(false);
                }
                else
                {
                    await CountdownAsync(session, MovementDiagnosticScenarioCatalog.GetDisplayText(session.Scenario), token)
                        .ConfigureAwait(false);
                    bool focused = await FocusAndValidateAsync(session, strict: true, token).ConfigureAwait(false);
                    if (!focused)
                        throw new InvalidOperationException(
                            "Foreground validation failed immediately before injection; no synthetic movement input was sent.");
                }

                if (session.Scenario != MovementDiagnosticScenario.ManualKeyboardBaseline &&
                    session.Scenario != MovementDiagnosticScenario.ExistingMovementSystemOnlyMove)
                {
                    MovementDiagnosticInputMode mode = MovementDiagnosticScenarioCatalog.GetPrimaryInputMode(session.Scenario);
                    _inputBackend = _backendFactory(mode);
                    _keys = new DiagnosticKeyOwnership(_inputBackend);
                    _activeInputMethod = $"{_inputBackend.Name} (diagnostic-only)";
                }
                else if (session.Scenario == MovementDiagnosticScenario.ExistingMovementSystemOnlyMove)
                {
                    _activeInputMethod = "Existing MovementSystem — production keybd_event path";
                }

                movementStartedElapsedMs = session.Stopwatch.Elapsed.TotalMilliseconds;
                await ExecuteScenarioAsync(session, memoryService, token).ConfigureAwait(false);
                session.Outcome = MovementDiagnosticOutcome.Completed;
                AddEvent(
                    session,
                    "SCENARIO_COMPLETED",
                    $"{MovementDiagnosticScenarioCatalog.GetDisplayText(session.Scenario)} completed.",
                    durationMs: session.Stopwatch.Elapsed.TotalMilliseconds - movementStartedElapsedMs);
            }
            catch (OperationCanceledException)
            {
                session.Outcome = MovementDiagnosticOutcome.Stopped;
                session.FailureReason = "Cancellation requested.";
                AddEvent(session, "SCENARIO_CANCELLED", "Cancellation requested; synthetic input cleanup follows.");
            }
            catch (Exception ex)
            {
                session.Outcome = MovementDiagnosticOutcome.Failed;
                session.FailureReason = ex.Message;
                AddEvent(
                    session,
                    "SCENARIO_FAILED",
                    $"Scenario failed: {ex.Message}",
                    details: ex.ToString());
                EnqueueLog($"[MovementDiag] Scenario failed: {ex.Message}");
            }
            finally
            {
                lock (_sync)
                {
                    _phase = MovementDiagnosticPhase.CleaningUp;
                    _message = "Releasing diagnostic-owned input and waiting for cleanup...";
                    _countdownRemaining = null;
                    _holdElapsed = null;
                    _holdTarget = null;
                    _holdLabel = "";
                }

                bool cleanupComplete = CleanupOwnedInputs(session, "scenario end/cancellation");

                DateTime inputStoppedUtc = DateTime.UtcNow;
                session.InputInjectionStoppedUtc = inputStoppedUtc;
                session.InputInjectionStoppedElapsedMs = session.Stopwatch.Elapsed.TotalMilliseconds;
                session.MovementDurationMs = Math.Max(
                    0,
                    session.InputInjectionStoppedElapsedMs.Value - movementStartedElapsedMs);
                session.InputCleanupComplete = cleanupComplete;
                session.EndedUtc = DateTime.UtcNow;

                AddEvent(
                    session,
                    "INPUT_INJECTION_STOPPED",
                    session.Scenario == MovementDiagnosticScenario.ManualKeyboardBaseline
                        ? "No synthetic input was ever injected in this manual baseline. Recording continues during manual observation."
                        : $"Confirmed synthetic input cleanup: {(cleanupComplete ? "complete" : "INCOMPLETE")}. No further input will be sent by this diagnostic.",
                    details: session.CleanupDetail);

                RecordFinalGameContext(session, memoryService);
                _lastCleanupComplete = cleanupComplete;
                if (!cleanupComplete)
                    EnqueueLog("[MovementDiag] INPUT CLEANUP INCOMPLETE — diagnostic start is blocked until the application/game is restarted.");

                if (session.Outcome == MovementDiagnosticOutcome.Completed &&
                    session.TotalRepeatEvents > 0)
                {
                    AddEvent(
                        session,
                        "INPUT_SUMMARY",
                        $"Total repeated W-down events: {session.TotalRepeatEvents}.",
                        details: $"keyDown={session.TotalKeyDownEvents}; keyUp={session.TotalKeyUpEvents}; failures={session.InputFailureCount}");
                }

                _lastResult = BuildLastResult(session);
                WriteReport("test finished");

                bool autoObserve;
                lock (_sync)
                {
                    autoObserve = !_suppressAutoObservation &&
                                  session.Settings.AutoStartRecoveryObservation &&
                                  cleanupComplete;
                }

                if (autoObserve)
                {
                    lock (_sync)
                    {
                        _message = "Test finished and input stopped. Observe the second client, then move manually and report what you see.";
                        StartObservationLocked(session, respectGlobalSuppression: true);
                    }
                }
                else
                {
                    await StopSamplerAsync().ConfigureAwait(false);
                    lock (_sync)
                    {
                        _phase = OutcomePhase(session.Outcome);
                        _message = cleanupComplete
                            ? "Test finished. No synthetic input is active. Start Post-Test Observation when ready."
                            : "Test finished with INCOMPLETE input cleanup. Restart the application/game before further tests.";
                        _activeInputMethod = session.InputMethodDescription;
                    }
                }
            }
        }

        private async Task ExecuteScenarioAsync(
            MovementDiagnosticSession session,
            GameMemoryService memoryService,
            CancellationToken token)
        {
            double duration = session.Settings.DurationMs;
            MovementDiagnosticSettings settings = session.Settings;

            switch (session.Scenario)
            {
                case MovementDiagnosticScenario.ManualKeyboardBaseline:
                {
                    await WaitWithProgressAsync(
                        duration,
                        "Manual baseline — move manually with W, A and D. Observe the second client.",
                        "Manual observation",
                        token).ConfigureAwait(false);
                    AddEvent(session, "MANUAL_BASELINE_COMPLETED", "Manual baseline observation period completed.");
                    break;
                }

                case MovementDiagnosticScenario.KeybdEventContinuousW:
                case MovementDiagnosticScenario.SendInputVirtualKey:
                case MovementDiagnosticScenario.SendInputScanCode:
                {
                    PressKey(session, MovementDiagnosticKey.W, repeat: false);
                    AddEvent(session, "INPUT_PHASE_STARTED", "W down — once, then logically held without additional key-down events.");
                    await WaitWithProgressAsync(
                        duration,
                        $"{session.InputMethodDescription}: W held — observe the second client.",
                        "W held",
                        token).ConfigureAwait(false);
                    ReleaseKey(session, MovementDiagnosticKey.W);
                    AddEvent(session, "INPUT_PHASE_ENDED", "W up — synthetic forward input ended.");
                    break;
                }

                case MovementDiagnosticScenario.KeybdEventRepeatedW:
                {
                    PressKey(session, MovementDiagnosticKey.W, repeat: false);
                    AddEvent(session, "INPUT_PHASE_STARTED", "W down — starting repeated W-down events at the configured interval.");

                    var repeatClock = Stopwatch.StartNew();
                    double lastEventMs = 0;
                    int repeats = 0;
                    while (repeatClock.Elapsed.TotalMilliseconds < duration)
                    {
                        token.ThrowIfCancellationRequested();
                        double elapsed = repeatClock.Elapsed.TotalMilliseconds;
                        int waitMs = Math.Max(
                            5,
                            settings.RepeatIntervalMs - (int)Math.Round(elapsed - lastEventMs));
                        int remaining = (int)Math.Max(5, duration - elapsed);
                        await Task.Delay(Math.Min(waitMs, remaining), token).ConfigureAwait(false);

                        double now = repeatClock.Elapsed.TotalMilliseconds;
                        if (now >= duration)
                            break;

                        double actualInterval = now - lastEventMs;
                        PressKey(session, MovementDiagnosticKey.W, repeat: true, actualIntervalMs: actualInterval);
                        lastEventMs = now;
                        repeats++;
                        SetStatus(
                            $"W held — repeated W-down #{repeats}. Observe the second client.",
                            "W held + repeats",
                            TimeSpan.FromMilliseconds(now),
                            TimeSpan.FromMilliseconds(duration));
                    }

                    ReleaseKey(session, MovementDiagnosticKey.W);
                    AddEvent(
                        session,
                        "INPUT_PHASE_ENDED",
                        $"W up after {repeats} repeated key-down events.",
                        durationMs: repeatClock.Elapsed.TotalMilliseconds,
                        details: $"configuredIntervalMs={settings.RepeatIntervalMs}; repeatedEvents={repeats}");
                    break;
                }

                case MovementDiagnosticScenario.KeybdEventPulsedW:
                {
                    AddEvent(
                        session,
                        "PULSE_PATTERN_STARTED",
                        $"Pulsed W: down {settings.PulseDownMs} ms / up {settings.PulseUpMs} ms.");
                    var pulseClock = Stopwatch.StartNew();
                    int transitions = 0;
                    while (pulseClock.Elapsed.TotalMilliseconds < duration)
                    {
                        token.ThrowIfCancellationRequested();
                        PressKey(session, MovementDiagnosticKey.W, repeat: false);
                        transitions++;
                        SetStatus(
                            $"Pulsed W — key DOWN (transition {transitions}).",
                            "Pulsed W",
                            pulseClock.Elapsed,
                            TimeSpan.FromMilliseconds(duration));

                        int downRemaining = (int)Math.Max(5, duration - pulseClock.Elapsed.TotalMilliseconds);
                        await Task.Delay(Math.Min(settings.PulseDownMs, downRemaining), token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (pulseClock.Elapsed.TotalMilliseconds >= duration)
                            break;

                        ReleaseKey(session, MovementDiagnosticKey.W);
                        transitions++;
                        SetStatus(
                            $"Pulsed W — key UP (transition {transitions}).",
                            "Pulsed W",
                            pulseClock.Elapsed,
                            TimeSpan.FromMilliseconds(duration));

                        int upRemaining = (int)Math.Max(5, duration - pulseClock.Elapsed.TotalMilliseconds);
                        await Task.Delay(Math.Min(settings.PulseUpMs, upRemaining), token).ConfigureAwait(false);
                    }

                    if (_keys?.IsDown(MovementDiagnosticKey.W) == true)
                        ReleaseKey(session, MovementDiagnosticKey.W);
                    session.PulseTransitions = transitions;
                    AddEvent(
                        session,
                        "PULSE_PATTERN_ENDED",
                        $"Pulsed W ended after {transitions} transitions.",
                        durationMs: pulseClock.Elapsed.TotalMilliseconds);
                    break;
                }

                case MovementDiagnosticScenario.KeybdEventKeyboardSteering:
                {
                    int stageMs = Math.Max(250, (int)Math.Round(duration / 2));
                    int pauseMs = 750;
                    session.StageCount = 2;

                    AddEvent(session, "STEERING_STAGE_STARTED", "Stage 1: W + A held for a keyboard left turn. No camera memory writes.");
                    PressKey(session, MovementDiagnosticKey.W, repeat: false);
                    PressKey(session, MovementDiagnosticKey.A, repeat: false);
                    await WaitWithProgressAsync(
                        stageMs,
                        "Stage 1 — W + A held (left turn). Observe the second client.",
                        "Stage 1: W + A",
                        token).ConfigureAwait(false);
                    ReleaseKey(session, MovementDiagnosticKey.A);
                    ReleaseKey(session, MovementDiagnosticKey.W);
                    AddEvent(session, "STEERING_STAGE_ENDED", "Stage 1 ended; W and A released.");

                    await PauseAllKeysAsync(pauseMs, "Pause between steering stages.", token).ConfigureAwait(false);

                    AddEvent(session, "STEERING_STAGE_STARTED", "Stage 2: W + D held for a keyboard right turn. No camera memory writes.");
                    PressKey(session, MovementDiagnosticKey.W, repeat: false);
                    PressKey(session, MovementDiagnosticKey.D, repeat: false);
                    await WaitWithProgressAsync(
                        stageMs,
                        "Stage 2 — W + D held (right turn). Observe the second client.",
                        "Stage 2: W + D",
                        token).ConfigureAwait(false);
                    ReleaseKey(session, MovementDiagnosticKey.D);
                    ReleaseKey(session, MovementDiagnosticKey.W);
                    AddEvent(session, "STEERING_STAGE_ENDED", "Stage 2 ended; W and D released.");
                    break;
                }

                case MovementDiagnosticScenario.KeyboardAndMouseFastTurning:
                {
                    int stageMs = Math.Max(250, (int)Math.Round(duration / 2));
                    int pauseMs = 750;
                    session.StageCount = 2;
                    _mouseAssist = _mouseAssistFactory(session.GameProcessId, EnqueueLog);

                    AddEvent(session, "STEERING_STAGE_STARTED", "Stage 1: W + A with left-edge mouse assistance.");
                    PressKey(session, MovementDiagnosticKey.W, repeat: false);
                    PressKey(session, MovementDiagnosticKey.A, repeat: false);
                    RecordMouseAssistResult(
                        session,
                        "ACQUIRE_LEFT",
                        "left fast-turn stage",
                        _mouseAssist.Acquire(leftSide: true, "left fast-turn stage"));
                    await WaitWithProgressAsync(
                        stageMs,
                        "Stage 1 — W + A with left-edge mouse assistance.",
                        "Stage 1: W + A + mouse",
                        token).ConfigureAwait(false);
                    ReleaseKey(session, MovementDiagnosticKey.A);
                    ReleaseKey(session, MovementDiagnosticKey.W);
                    RecordMouseAssistResult(
                        session,
                        "RELEASE_LEFT",
                        "left fast-turn stage",
                        _mouseAssist.Release("left stage ended"));
                    AddEvent(session, "STEERING_STAGE_ENDED", "Stage 1 ended; W/A/mouse assistance released.");

                    await PauseAllKeysAsync(pauseMs, "Pause between mouse-turn stages.", token).ConfigureAwait(false);

                    AddEvent(session, "STEERING_STAGE_STARTED", "Stage 2: W + D with right-edge mouse assistance.");
                    PressKey(session, MovementDiagnosticKey.W, repeat: false);
                    PressKey(session, MovementDiagnosticKey.D, repeat: false);
                    RecordMouseAssistResult(
                        session,
                        "ACQUIRE_RIGHT",
                        "right fast-turn stage",
                        _mouseAssist.Acquire(leftSide: false, "right fast-turn stage"));
                    await WaitWithProgressAsync(
                        stageMs,
                        "Stage 2 — W + D with right-edge mouse assistance.",
                        "Stage 2: W + D + mouse",
                        token).ConfigureAwait(false);
                    ReleaseKey(session, MovementDiagnosticKey.D);
                    ReleaseKey(session, MovementDiagnosticKey.W);
                    RecordMouseAssistResult(
                        session,
                        "RELEASE_RIGHT",
                        "right fast-turn stage",
                        _mouseAssist.Release("right stage ended"));
                    AddEvent(session, "STEERING_STAGE_ENDED", "Stage 2 ended; W/D/mouse assistance released.");
                    break;
                }

                case MovementDiagnosticScenario.DirectCameraMemorySteering:
                {
                    if (!settings.AdvancedModeEnabled)
                        throw new InvalidOperationException("DirectCamera test requires advanced mode.");

                    float before = memoryService.GetCameraAngle();
                    float deltaDegrees = 35f;
                    float targetDegrees = NormalizeDegrees(
                        GameMemoryService.RadiansToDegrees(before) + deltaDegrees);
                    float targetRadians = GameMemoryService.DegreesToRadians(targetDegrees);

                    session.CameraAngleBefore = before;
                    AddEvent(
                        session,
                        "CAMERA_WRITE_REQUESTED",
                        $"DirectCamera: writing a bounded {deltaDegrees:F1}° turn.",
                        details: $"beforeRad={before:F6}; targetRad={targetRadians:F6}; targetDeg={targetDegrees:F2}; address=GameMemoryService.SetCameraAngle");

                    memoryService.SetCameraAngle(targetRadians);
                    session.CameraWriteCount++;
                    AddEvent(
                        session,
                        "CAMERA_WRITE_ISSUED",
                        "GameMemoryService.SetCameraAngle() write issued once. No other memory field is written.",
                        details: $"targetRad={targetRadians:F6}");

                    await Task.Delay(200, token).ConfigureAwait(false);
                    float after = memoryService.GetCameraAngle();
                    session.CameraAngleAfter = after;
                    float afterDegrees = GameMemoryService.RadiansToDegrees(after);
                    float error = Math.Abs(GeometryUtils.GetShortestBearingDiffDeg(afterDegrees, targetDegrees));
                    session.CameraReadbackVerified = error <= 5f;
                    AddEvent(
                        session,
                        session.CameraReadbackVerified ? "CAMERA_WRITE_READBACK_VERIFIED" : "CAMERA_WRITE_READBACK_MISMATCH",
                        $"Camera readback after direct write: error={error:F2}° (tolerance 5°).",
                        details: $"afterRad={after:F6}; afterDeg={afterDegrees:F2}; targetDeg={targetDegrees:F2}; verified={session.CameraReadbackVerified}",
                        apiSucceeded: session.CameraReadbackVerified);

                    double forwardMs = Math.Min(2000, Math.Max(500, duration / 3));
                    AddEvent(session, "INPUT_PHASE_STARTED", "Short forward movement after the bounded direct-camera turn.");
                    PressKey(session, MovementDiagnosticKey.W, repeat: false);
                    await WaitWithProgressAsync(
                        forwardMs,
                        "W held briefly after the direct-camera turn. Observe the second client.",
                        "W held after camera write",
                        token).ConfigureAwait(false);
                    ReleaseKey(session, MovementDiagnosticKey.W);
                    AddEvent(session, "INPUT_PHASE_ENDED", "W up after direct-camera stage.");
                    break;
                }

                case MovementDiagnosticScenario.ExistingMovementSystemOnlyMove:
                {
                    if (!settings.SafeWaypointX.HasValue || !settings.SafeWaypointY.HasValue)
                        throw new InvalidOperationException("No safe test waypoint is configured for the MovementSystem scenario.");

                    float tx = settings.SafeWaypointX.Value;
                    float ty = settings.SafeWaypointY.Value;
                    AddEvent(
                        session,
                        "MOVEMENT_SYSTEM_STARTED",
                        $"Existing MovementSystem: OnlyMove, KeyboardTurn, one safe waypoint ({tx:F1},{ty:F1}), max {duration:F0} ms.",
                        details: "enableWaypointSpecialRecoveries=false; InternalRepotEnabled=false; no loot/combat/route continuation");

                    var context = new MovementDiagnosticOnlyMoveContext(
                        memoryService,
                        tx,
                        ty,
                        settings,
                        msg => EnqueueLog($"[MovementDiag][MovementSystem] {msg}"),
                        msg => AddEvent(session, msg.Contains("[Camera]", StringComparison.Ordinal)
                            ? "DIRECT_CAMERA_RECOVERY_SIGNAL"
                            : "MOVEMENT_RECOVERY_EVENT", msg));
                    MovementDiagnosticMovementRunResult result =
                        await _onlyMoveRunner(context, token).ConfigureAwait(false);

                    session.SteeringModeUsed = result.SteeringMode;
                    session.DirectCameraRecoveryInvoked = result.DirectCameraRecoveryInvoked;
                    session.MovementRecoveryEvents.AddRange(result.MovementRecoveryEvents);
                    AddEvent(
                        session,
                        "MOVEMENT_SYSTEM_COMPLETED",
                        $"OnlyMove controller ended: goalReached={result.GoalReached}, ticks={result.UpdateTicks}.",
                        details: $"steering={result.SteeringMode}; directCameraRecovery={result.DirectCameraRecoveryInvoked}; " +
                                 $"recoveryEvents={session.MovementRecoveryEvents.Count}; detail={result.Detail}",
                        durationMs: duration);
                    break;
                }

                default:
                    throw new InvalidOperationException($"Unsupported diagnostic scenario: {session.Scenario}.");
            }
        }

        private async Task CountdownAsync(
            MovementDiagnosticSession session,
            string scenarioDisplay,
            CancellationToken token)
        {
            int countdownMs = session.Settings.CountdownMs;
            AddEvent(session, "COUNTDOWN_STARTED", $"Countdown {countdownMs} ms before any scenario action.", durationMs: countdownMs);

            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalMilliseconds < countdownMs)
            {
                token.ThrowIfCancellationRequested();
                int remainingSeconds = (int)Math.Ceiling((countdownMs - clock.Elapsed.TotalMilliseconds) / 1000.0);
                lock (_sync)
                {
                    _phase = MovementDiagnosticPhase.Countdown;
                    _countdownRemaining = TimeSpan.FromMilliseconds(Math.Max(0, countdownMs - clock.Elapsed.TotalMilliseconds));
                    _message = $"{scenarioDisplay} — starting in {Math.Max(0, remainingSeconds)}...";
                }
                await Task.Delay(100, token).ConfigureAwait(false);
            }

            lock (_sync)
            {
                _countdownRemaining = null;
                _holdElapsed = null;
                _holdTarget = null;
            }
            AddEvent(session, "COUNTDOWN_COMPLETED", "Countdown completed; input phase follows.", durationMs: clock.Elapsed.TotalMilliseconds);
        }

        private async Task<bool> FocusAndValidateAsync(
            MovementDiagnosticSession session,
            bool strict,
            CancellationToken token)
        {
            _focusGame();
            AddEvent(session, "GAME_FOCUS_REQUESTED", "Requested foreground ownership immediately before the input phase.");

            var clock = Stopwatch.StartNew();
            uint targetPid = session.GameProcessId;
            uint foregroundPid = 0;
            while (clock.Elapsed.TotalMilliseconds < session.Settings.FocusValidationTimeoutMs)
            {
                token.ThrowIfCancellationRequested();
                foregroundPid = _getForegroundProcessId();
                if (foregroundPid != 0 && foregroundPid == targetPid)
                    break;
                await Task.Delay(50, token).ConfigureAwait(false);
            }

            foregroundPid = _getForegroundProcessId();
            bool validated = foregroundPid != 0 && foregroundPid == targetPid;
            session.FocusValidated = validated;
            AddEvent(
                session,
                validated ? "GAME_FOCUS_VALIDATED" : "GAME_FOCUS_VALIDATION_FAILED",
                validated
                    ? "Game process owns the foreground window."
                    : $"Foreground validation failed (game pid={targetPid}, foreground pid={foregroundPid}).",
                details: $"window={session.GameWindowHandle ?? "Unknown"}; strict={strict}",
                apiSucceeded: validated);

            if (!validated)
            {
                EnqueueLog($"[MovementDiag] Focus validation failed: game pid={targetPid}, foreground pid={foregroundPid}.");
                if (strict)
                    return false;
            }

            return validated;
        }

        private async Task WaitWithProgressAsync(
            double durationMs,
            string message,
            string holdLabel,
            CancellationToken token)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalMilliseconds < durationMs)
            {
                token.ThrowIfCancellationRequested();
                SetStatus(
                    message,
                    holdLabel,
                    clock.Elapsed,
                    TimeSpan.FromMilliseconds(durationMs));
                int remaining = (int)Math.Max(10, durationMs - clock.Elapsed.TotalMilliseconds);
                await Task.Delay(Math.Min(100, remaining), token).ConfigureAwait(false);
            }

            SetStatus(
                message,
                holdLabel,
                TimeSpan.FromMilliseconds(durationMs),
                TimeSpan.FromMilliseconds(durationMs));
        }

        private async Task PauseAllKeysAsync(int pauseMs, string message, CancellationToken token)
        {
            SetStatus(message, "Pause (all keys released)", TimeSpan.Zero, TimeSpan.FromMilliseconds(pauseMs));
            await Task.Delay(pauseMs, token).ConfigureAwait(false);
        }

        private void SetStatus(string message, string holdLabel, TimeSpan holdElapsed, TimeSpan holdTarget)
        {
            lock (_sync)
            {
                _phase = MovementDiagnosticPhase.Running;
                _message = message;
                _holdLabel = holdLabel;
                _holdElapsed = holdElapsed;
                _holdTarget = holdTarget;
            }
        }

        private void PressKey(
            MovementDiagnosticSession session,
            MovementDiagnosticKey key,
            bool repeat,
            double? actualIntervalMs = null)
        {
            DiagnosticKeyOwnership? keys = _keys;
            IMovementDiagnosticKeyboardBackend? backend = _inputBackend;
            if (keys == null || backend == null)
                throw new InvalidOperationException("No diagnostic input backend is active.");

            MovementDiagnosticInputCallResult result = keys.SendDown(key, repeat);
            if (repeat)
                session.TotalRepeatEvents++;
            else
                session.TotalKeyDownEvents++;

            AddInputEvent(session, backend, key, isDown: true, isRepeat: repeat, result, actualIntervalMs);
        }

        private void ReleaseKey(MovementDiagnosticSession session, MovementDiagnosticKey key)
        {
            DiagnosticKeyOwnership? keys = _keys;
            IMovementDiagnosticKeyboardBackend? backend = _inputBackend;
            if (keys == null || backend == null)
                return;

            if (!keys.IsDown(key))
                return;

            MovementDiagnosticInputCallResult result = keys.SendUp(key);
            session.TotalKeyUpEvents++;
            AddInputEvent(session, backend, key, isDown: false, isRepeat: false, result, null);
        }

        private void AddInputEvent(
            MovementDiagnosticSession session,
            IMovementDiagnosticKeyboardBackend backend,
            MovementDiagnosticKey key,
            bool isDown,
            bool isRepeat,
            MovementDiagnosticInputCallResult result,
            double? actualIntervalMs)
        {
            string eventType = isDown
                ? (isRepeat ? "KEY_REPEAT" : "KEY_DOWN")
                : "KEY_UP";
            string action = isRepeat ? "repeat down" : (isDown ? "down" : "up");
            AddEvent(
                session,
                eventType,
                $"{MovementDiagnosticKeyInfo.GetKeyText(key)} {action} ({backend.Name}).",
                inputBackend: backend.Name,
                virtualKey: MovementDiagnosticKeyInfo.GetVirtualKeyText(key),
                scanCode: MovementDiagnosticKeyInfo.GetScanCodeText(key),
                apiSucceeded: result.ApiSucceeded,
                insertedCount: result.InsertedCount,
                win32Error: result.Win32Error > 0 ? result.Win32Error : null,
                actualIntervalMs: actualIntervalMs,
                details: result.Detail);

            if (!result.ApiSucceeded)
            {
                session.InputFailureCount++;
                AddEvent(
                    session,
                    "INPUT_FAILURE",
                    $"{MovementDiagnosticKeyInfo.GetKeyText(key)} {action} failed in {backend.Name}.",
                    inputBackend: backend.Name,
                    virtualKey: MovementDiagnosticKeyInfo.GetVirtualKeyText(key),
                    scanCode: MovementDiagnosticKeyInfo.GetScanCodeText(key),
                    apiSucceeded: false,
                    insertedCount: result.InsertedCount,
                    win32Error: result.Win32Error > 0 ? result.Win32Error : null,
                    details: result.Detail);
            }
        }

        private bool CleanupOwnedInputs(MovementDiagnosticSession session, string reason)
        {
            bool complete = true;
            string detail = "";

            DiagnosticKeyOwnership? keys = _keys;
            if (keys != null)
            {
                foreach (var released in keys.ReleaseAll())
                {
                    session.TotalKeyUpEvents++;
                    AddInputEvent(
                        session,
                        _inputBackend ?? new KeybdEventDiagnosticKeyboardBackend(),
                        released.Key,
                        isDown: false,
                        isRepeat: false,
                        released.Result,
                        actualIntervalMs: null);
                    if (!released.Result.ApiSucceeded)
                        complete = false;
                }

                if (keys.HasOwnedKeys)
                    complete = false;
                detail += complete
                    ? "all diagnostic-owned keys released; "
                    : "one or more diagnostic-owned keys could not be released; ";
                keys.DisposeBackend();
                _keys = null;
                _inputBackend = null;
            }

            IMovementDiagnosticMouseTurnAssist? mouse = _mouseAssist;
            if (mouse != null)
            {
                try
                {
                    MovementDiagnosticMouseAssistResult cleanup = mouse.Cleanup(reason);
                    RecordMouseAssistResult(session, "CLEANUP", "scenario cleanup", cleanup);
                    detail += cleanup.Detail + "; ";
                    if (mouse.IsActive)
                        complete = false;
                }
                catch (Exception ex)
                {
                    complete = false;
                    detail += $"mouse cleanup threw: {ex.Message}; ";
                    AddEvent(session, "MOUSE_ASSIST_CLEANUP_FAILED", ex.Message);
                }
                finally
                {
                    mouse.Dispose();
                    _mouseAssist = null;
                }
            }

            if (string.IsNullOrEmpty(detail))
                detail = "no diagnostic-owned input was active";

            session.CleanupDetail = detail;
            AddEvent(
                session,
                complete ? "INPUT_CLEANUP_COMPLETE" : "INPUT_CLEANUP_INCOMPLETE",
                $"Diagnostic input cleanup {(complete ? "complete" : "incomplete")}: {detail}",
                apiSucceeded: complete,
                details: reason);
            return complete;
        }

        private void RecordMouseAssistResult(
            MovementDiagnosticSession session,
            string eventTypeSuffix,
            string stageLabel,
            MovementDiagnosticMouseAssistResult result)
        {
            AddEvent(
                session,
                $"MOUSE_ASSIST_{eventTypeSuffix}",
                $"Cursor assistance {eventTypeSuffix.ToLowerInvariant()}: {result.Detail}",
                apiSucceeded: result.Applied,
                details:
                $"gameForeground={result.GameForeground}; cursorInsideClient={result.CursorInsideClient}; " +
                $"cursorBefore=({result.CursorX},{result.CursorY}); cursorTarget=({result.TargetX},{result.TargetY}); " +
                $"stage={stageLabel}");
            EnqueueLog($"[MovementDiag][MouseAssist] {eventTypeSuffix}: {result.Detail}");
        }

        private void RecordGameContext(MovementDiagnosticSession session, GameMemoryService memoryService)
        {
            try
            {
                Process process = Process.GetProcessById((int)session.GameProcessId);
                session.GameWindowHandle = process.MainWindowHandle == nint.Zero
                    ? "Unknown"
                    : $"0x{process.MainWindowHandle:X}";
                session.ClientVersion = SafeGetFileVersion(process);
            }
            catch
            {
                session.GameWindowHandle ??= "Unknown";
                session.ClientVersion ??= "Unknown";
            }

            PlayerStateSnapshot snapshot = memoryService.GetPlayerStateSnapshot();
            session.InitialMap = snapshot.MapReadSucceeded ? snapshot.Map : 0;
            session.InitialPositionReadSucceeded = snapshot.PositionReadSucceeded;
            session.InitialX = snapshot.X;
            session.InitialY = snapshot.Y;

            bool selfElevated = false;
            bool gameElevated = false;
            try
            {
                selfElevated = ElevationCheck.IsCurrentProcessElevated();
                gameElevated = ElevationCheck.IsProcessElevated((int)session.GameProcessId);
            }
            catch
            {
            }

            AddEvent(
                session,
                "GAME_CONTEXT",
                $"pid={session.GameProcessId}; hwnd={session.GameWindowHandle}; clientVersion={session.ClientVersion}; " +
                $"map={(snapshot.MapReadSucceeded ? snapshot.Map.ToString() : "unreadable")}; " +
                $"localPosition={(snapshot.PositionReadSucceeded ? $"({snapshot.X},{snapshot.Y})" : "unreadable")}; " +
                $"cameraRad={memoryService.GetCameraAngle():F6}; action={memoryService.GetCurrentAction()}; " +
                $"animation1={memoryService.GetAnimation1()}; selfElevated={selfElevated}; gameElevated={gameElevated}; " +
                $"elevationMismatch={(gameElevated && !selfElevated)}.",
                details: "Local memory reads only. Not server-authoritative positions. Elevation mismatch can silently block keybd_event.");
        }

        private static void RecordFinalGameContext(MovementDiagnosticSession session, GameMemoryService memoryService)
        {
            try
            {
                PlayerStateSnapshot snapshot = memoryService.GetPlayerStateSnapshot();
                session.FinalPositionReadSucceeded = snapshot.PositionReadSucceeded;
                session.FinalX = snapshot.X;
                session.FinalY = snapshot.Y;
            }
            catch
            {
                session.FinalPositionReadSucceeded = false;
            }
        }

        private static string SafeGetFileVersion(Process process)
        {
            try
            {
                string? version = process.MainModule?.FileVersionInfo?.FileVersion;
                return string.IsNullOrWhiteSpace(version) ? "Unknown" : version!;
            }
            catch
            {
                return "Unknown";
            }
        }

        private string? ValidateSafeWaypoint(MovementDiagnosticSettings settings, GameMemoryService memoryService)
        {
            if (!settings.SafeWaypointX.HasValue || !settings.SafeWaypointY.HasValue)
                return "TEST 9 requires an explicitly configured safe waypoint X/Y. No waypoint is invented automatically.";

            (float x, float y, bool success) position;
            try
            {
                position = _readPlayerPosition != null
                    ? _readPlayerPosition()
                    : memoryService.GetPlayerPosition();
            }
            catch (Exception ex)
            {
                return $"Could not read the local player position to validate the safe waypoint: {ex.Message}";
            }

            if (!position.success)
                return "Could not read the local player position to validate the safe waypoint. TEST 9 was not started.";

            float dx = settings.SafeWaypointX.Value - position.x;
            float dy = settings.SafeWaypointY.Value - position.y;
            float distance = (float)Math.Sqrt(dx * dx + dy * dy);
            if (distance < 0.5f)
                return "The configured TEST 9 waypoint is the current position. Configure a waypoint a few units ahead so movement is actually exercised.";
            if (distance > settings.MaxSafeWaypointDistance)
                return $"The configured TEST 9 waypoint is {distance:F1} units away; the safe maximum is {settings.MaxSafeWaypointDistance:F1}. Do not make the diagnostic travel a large distance.";
            return null;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Sampling and observation
        // ─────────────────────────────────────────────────────────────────────

        private void StartSamplerLocked(MovementDiagnosticSession session)
        {
            if (_sampleTask != null && !_sampleTask.IsCompleted)
                return;

            _sampleCts = new CancellationTokenSource();
            CancellationToken token = _sampleCts.Token;
            _sampleTask = Task.Run(() => SampleLoopAsync(session, token));
        }

        private async Task SampleLoopAsync(MovementDiagnosticSession session, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var clock = Stopwatch.StartNew();
                MovementDiagnosticSample? sample = ReadSample(session);
                if (sample != null)
                {
                    lock (_sync)
                    {
                        sample.Sequence = session.Samples.Count + 1;
                        session.Samples.Add(sample);

                        if (!sample.PositionReadSucceeded)
                        {
                            session.ReadFailureCount++;
                            if (!session.ReadFailureEventRecorded)
                            {
                                session.ReadFailureEventRecorded = true;
                                AddEventLocked(
                                    session,
                                    "SAMPLE_READ_FAILURE",
                                    "Local player position could not be read; the sample is marked as unreadable (no fabricated 0,0 position).",
                                    details: $"phase={sample.Phase}; elapsedMs={sample.ElapsedMs:F0}; mapRead={sample.MapReadSucceeded}");
                            }
                        }
                    }
                }

                int remaining = Math.Max(
                    MinSampleIntervalMs,
                    session.Settings.SampleIntervalMs - (int)clock.Elapsed.TotalMilliseconds);
                try
                {
                    await Task.Delay(remaining, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private MovementDiagnosticSample? ReadSample(MovementDiagnosticSession session)
        {
            GameMemoryService? memory = _sessionMemory;
            if (memory == null)
                return null;

            var clock = Stopwatch.StartNew();
            try
            {
                PlayerStateSnapshot snapshot = memory.GetPlayerStateSnapshot();
                float camera = memory.GetCameraAngle();
                byte action = memory.GetCurrentAction();
                int animation = memory.GetAnimation1();
                clock.Stop();

                string phase;
                lock (_sync)
                    phase = _phase.ToString();

                return new MovementDiagnosticSample
                {
                    UtcTimestamp = DateTime.UtcNow,
                    ElapsedMs = session.Stopwatch.Elapsed.TotalMilliseconds,
                    Phase = phase,
                    PlayerPointerReadSucceeded = snapshot.PlayerPointerReadSucceeded,
                    PositionReadSucceeded = snapshot.PositionReadSucceeded,
                    PlayerX = snapshot.X,
                    PlayerY = snapshot.Y,
                    MapReadSucceeded = snapshot.MapReadSucceeded,
                    Map = snapshot.Map,
                    CameraAngleRadians = camera,
                    CameraAngleDegrees = GameMemoryService.RadiansToDegrees(camera),
                    CurrentAction = action,
                    Animation1 = animation,
                    ReadDurationMs = clock.Elapsed.TotalMilliseconds
                };
            }
            catch
            {
                return new MovementDiagnosticSample
                {
                    UtcTimestamp = DateTime.UtcNow,
                    ElapsedMs = session.Stopwatch.Elapsed.TotalMilliseconds,
                    Phase = _phase.ToString(),
                    ReadDurationMs = clock.Elapsed.TotalMilliseconds
                };
            }
        }

        private void StartObservationLocked(MovementDiagnosticSession session, bool respectGlobalSuppression)
        {
            if (_observationTask != null && !_observationTask.IsCompleted)
                return;
            if (respectGlobalSuppression && _suppressAutoObservation)
                return;

            StartSamplerLocked(session);
            _observationCts = new CancellationTokenSource();
            CancellationToken token = _observationCts.Token;
            _phase = MovementDiagnosticPhase.Observing;
            _message = "Post-Test Manual Recovery Observation — move manually and observe the second client. No diagnostic input is active.";
            _holdElapsed = null;
            _holdTarget = null;
            _holdLabel = "";
            _lastCheckpoint = "Immediately after input stopped";
            _observationTask = Task.Run(() => RunObservationAsync(session, token));
        }

        private async Task RunObservationAsync(MovementDiagnosticSession session, CancellationToken token)
        {
            DateTime inputStoppedUtc = session.InputInjectionStoppedUtc
                ?? throw new InvalidOperationException("Observation cannot start before input-injection-stopped is recorded.");
            string stopReason = "stopped by user";

            AddEvent(
                session,
                "POST_TEST_OBSERVATION_STARTED",
                "Post-test manual recovery observation started. The diagnostic will not inject input or write camera memory.",
                details: $"inputInjectionStoppedUtc={inputStoppedUtc:O}");
            session.ObservationStartedUtc ??= DateTime.UtcNow;

            AddEvent(
                session,
                "RECOVERY_CHECKPOINT",
                "Checkpoint immediately after test: record what the second client shows.",
                details: "automatic-checkpoint; checkpoint=immediate");

            try
            {
                int[] checkpoints = (session.Settings.RecoveryCheckpointsSeconds ?? Array.Empty<int>())
                    .Where(s => s > 0)
                    .Distinct()
                    .OrderBy(s => s)
                    .ToArray();

                foreach (int seconds in checkpoints)
                {
                    DateTime targetUtc = inputStoppedUtc.AddSeconds(seconds);
                    bool observationStartedLate = DateTime.UtcNow > targetUtc.AddSeconds(1);
                    await WaitUntilUtcAsync(targetUtc, seconds, token).ConfigureAwait(false);

                    lock (_sync)
                    {
                        _lastCheckpoint = $"{seconds}s after input stop";
                        _message = observationStartedLate
                            ? $"Checkpoint {seconds}s is already past (observation started late). Record what the second client shows or mark recovery."
                            : $"Recovery checkpoint {seconds}s reached. Record what the second client shows (or mark recovery).";
                    }

                    AddEvent(
                        session,
                        "RECOVERY_CHECKPOINT",
                        observationStartedLate
                            ? $"Checkpoint {seconds}s was already past when observation started. No diagnostic input has been sent."
                            : $"Checkpoint {seconds}s after confirmed input stop. No diagnostic input has been sent.",
                        details:
                        $"automatic-checkpoint; checkpoint={seconds}s; targetUtc={targetUtc:O}; " +
                        $"observedUtc={DateTime.UtcNow:O}; observationStartedLate={observationStartedLate}");
                }

                DateTime observationEnd = inputStoppedUtc.AddMinutes(Math.Max(1, session.Settings.MaxObservationMinutes));
                await WaitUntilUtcAsync(observationEnd, -1, token).ConfigureAwait(false);
                stopReason = "maximum observation duration reached";
                AddEvent(
                    session,
                    "POST_TEST_OBSERVATION_TIMEOUT",
                    $"Maximum observation duration ({session.Settings.MaxObservationMinutes} min) reached.",
                    details: $"inputInjectionStoppedUtc={inputStoppedUtc:O}");
            }
            catch (OperationCanceledException)
            {
                stopReason = "stopped by user";
            }
            catch (Exception ex)
            {
                stopReason = $"observation error: {ex.Message}";
                AddEvent(session, "POST_TEST_OBSERVATION_FAILED", ex.Message, details: ex.ToString());
            }
            finally
            {
                AddEvent(
                    session,
                    "POST_TEST_OBSERVATION_STOPPED",
                    $"Post-test observation ended ({stopReason}).",
                    details: $"inputInjectionStoppedUtc={inputStoppedUtc:O}; recoveryMarked={session.RecoveryObservedUtc.HasValue}");
                await StopSamplerAsync().ConfigureAwait(false);
                WriteReport("observation ended");

                lock (_sync)
                {
                    _phase = OutcomePhase(session.Outcome);
                    _message = session.RecoveryObservedUtc.HasValue
                        ? $"Observation ended ({stopReason}). Recovery duration: {session.RecoveryDurationMs:F0} ms."
                        : $"Observation ended ({stopReason}). Recovery time is still Unknown.";
                    _activeInputMethod = session.InputMethodDescription;
                }
            }
        }

        private async Task WaitUntilUtcAsync(DateTime targetUtc, int checkpointSeconds, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                TimeSpan remaining = targetUtc - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    return;

                lock (_sync)
                {
                    if (checkpointSeconds > 0)
                    {
                        _message = $"Post-test observation — next checkpoint in {Math.Ceiling(remaining.TotalSeconds)}s. Move manually; no diagnostic input is active.";
                    }
                    else
                    {
                        _message = $"Post-test observation active — {Math.Ceiling(remaining.TotalMinutes)} min until the maximum observation limit. No diagnostic input is active.";
                    }
                }

                await Task.Delay((int)Math.Min(500, Math.Max(50, remaining.TotalMilliseconds)), token)
                    .ConfigureAwait(false);
            }
        }

        private async Task StopSamplerAsync()
        {
            Task? sampleTask;
            CancellationTokenSource? cts;
            lock (_sync)
            {
                sampleTask = _sampleTask;
                cts = _sampleCts;
                _sampleTask = null;
                _sampleCts = null;
            }

            try { cts?.Cancel(); } catch { }
            if (sampleTask != null)
            {
                try { await sampleTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { EnqueueLog($"[MovementDiag] Sample task ended with: {ex.Message}"); }
            }
            cts?.Dispose();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Events, status, reports
        // ─────────────────────────────────────────────────────────────────────

        private void AddEvent(
            MovementDiagnosticSession session,
            string eventType,
            string message,
            string? inputBackend = null,
            string? virtualKey = null,
            string? scanCode = null,
            bool? apiSucceeded = null,
            uint? insertedCount = null,
            int? win32Error = null,
            double? durationMs = null,
            double? actualIntervalMs = null,
            string? details = null,
            bool writeToBotLog = true)
        {
            lock (_sync)
            {
                AddEventLocked(
                    session,
                    eventType,
                    message,
                    inputBackend,
                    virtualKey,
                    scanCode,
                    apiSucceeded,
                    insertedCount,
                    win32Error,
                    durationMs,
                    actualIntervalMs,
                    details);
            }

            if (writeToBotLog)
                EnqueueLog($"[MovementDiag] {eventType}: {message}");
        }

        private static void AddEventLocked(
            MovementDiagnosticSession session,
            string eventType,
            string message,
            string? inputBackend = null,
            string? virtualKey = null,
            string? scanCode = null,
            bool? apiSucceeded = null,
            uint? insertedCount = null,
            int? win32Error = null,
            double? durationMs = null,
            double? actualIntervalMs = null,
            string? details = null)
        {
            session.Events.Add(new MovementDiagnosticEventEntry
            {
                Sequence = session.Events.Count + 1,
                UtcTimestamp = DateTime.UtcNow,
                ElapsedMs = session.Stopwatch.Elapsed.TotalMilliseconds,
                EventType = eventType,
                Message = message,
                InputBackend = inputBackend,
                VirtualKey = virtualKey,
                ScanCode = scanCode,
                ApiSucceeded = apiSucceeded,
                InsertedCount = insertedCount,
                Win32Error = win32Error,
                DurationMs = durationMs,
                ActualIntervalMs = actualIntervalMs,
                Details = details
            });
        }

        private string? WriteReport(string reason)
        {
            MovementDiagnosticSession? session;
            MovementDiagnosticSession? snapshot;
            List<MovementDiagnosticSessionSummary> history;
            lock (_sync)
            {
                session = _currentSession;
                if (session == null)
                    return null;

                snapshot = session.CloneForReport(includeSamplesAndEvents: true);
                snapshot.OutputDirectory = _reportWriter.GetSessionDirectory(snapshot.SessionId);
                history = _history
                    .Select(MovementDiagnosticReportWriter.BuildSummary)
                    .ToList();
            }

            string directory = _reportWriter.WriteReport(snapshot!, history);
            lock (_sync)
            {
                if (_currentSession != null && _currentSession.SessionId == snapshot!.SessionId)
                    _currentSession.OutputDirectory = directory;
                snapshot!.OutputDirectory = directory;
            }

            EnqueueLog($"[MovementDiag] Report updated ({reason}): {directory}");
            return directory;
        }

        private static string BuildLastResult(MovementDiagnosticSession session)
        {
            MovementDiagnosticObserverRecord? latest = session.LatestObservation;
            return
                $"{MovementDiagnosticScenarioCatalog.GetDisplayText(session.Scenario)} | outcome={session.Outcome} | " +
                $"keys down/up/repeat={session.TotalKeyDownEvents}/{session.TotalKeyUpEvents}/{session.TotalRepeatEvents} | " +
                $"failures={session.InputFailureCount} | cleanup={(session.InputCleanupComplete ? "complete" : "INCOMPLETE")} | " +
                $"observer={(latest == null ? "Unknown" : MovementDiagnosticScenarioCatalog.GetObservationLabel(latest.Rating))} | " +
                $"recovery={(session.RecoveryDurationMs.HasValue ? $"{session.RecoveryDurationMs:F0} ms" : "Unknown")}";
        }

        private static MovementDiagnosticPhase OutcomePhase(MovementDiagnosticOutcome outcome) => outcome switch
        {
            MovementDiagnosticOutcome.Completed => MovementDiagnosticPhase.Completed,
            MovementDiagnosticOutcome.Stopped => MovementDiagnosticPhase.Stopped,
            MovementDiagnosticOutcome.Failed => MovementDiagnosticPhase.Failed,
            _ => MovementDiagnosticPhase.Idle
        };

        private bool IsTestActiveLocked()
            => _testTask != null && !_testTask.IsCompleted;

        private bool IsObservationActiveLocked()
            => _observationTask != null && !_observationTask.IsCompleted;

        private static MovementDiagnosticSession CreateSession(
            MovementDiagnosticScenario scenario,
            MovementDiagnosticScenarioInfo info,
            MovementDiagnosticSettings settings)
        {
            string sessionId = $"{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_T{(int)scenario}_{Guid.NewGuid():N}".Substring(0, 31);
            var session = new MovementDiagnosticSession
            {
                SessionId = sessionId,
                StartedUtc = DateTime.UtcNow,
                Scenario = scenario,
                ScenarioLabel = info.Label,
                InputMethodDescription = info.InputMethodDescription,
                PrimaryInputMode = MovementDiagnosticScenarioCatalog.GetPrimaryInputMode(scenario),
                Settings = settings,
                GameProcessId = 0,
                Outcome = MovementDiagnosticOutcome.NotRun
            };
            session.Stopwatch = Stopwatch.StartNew();
            return session;
        }

        private MovementDiagnosticSettings NormalizeSettings(MovementDiagnosticSettings requested)
        {
            MovementDiagnosticSettings settings = requested?.Clone() ?? new MovementDiagnosticSettings();
            settings.DurationMs = Math.Clamp(settings.DurationMs, MinDurationMs, MaxDurationMs);
            settings.CountdownMs = Math.Clamp(settings.CountdownMs, 0, 10_000);
            settings.RepeatIntervalMs = Math.Clamp(settings.RepeatIntervalMs, 20, 2_000);
            settings.SampleIntervalMs = Math.Clamp(settings.SampleIntervalMs, MinSampleIntervalMs, MaxSampleIntervalMs);
            settings.PulseDownMs = Math.Clamp(settings.PulseDownMs, 20, 2_000);
            settings.PulseUpMs = Math.Clamp(settings.PulseUpMs, 20, 2_000);
            settings.FocusValidationTimeoutMs = Math.Clamp(settings.FocusValidationTimeoutMs, 0, 5_000);
            settings.MaxSafeWaypointDistance = Math.Clamp(settings.MaxSafeWaypointDistance, 1f, 100f);
            settings.MaxObservationMinutes = Math.Clamp(settings.MaxObservationMinutes, 1, 180);
            settings.RecoveryCheckpointsSeconds = (settings.RecoveryCheckpointsSeconds ?? Array.Empty<int>())
                .Where(s => s > 0 && s <= 3_600)
                .Distinct()
                .OrderBy(s => s)
                .ToArray();
            return settings;
        }

        private static string BuildSettingsDetails(MovementDiagnosticSettings settings)
            => $"durationMs={settings.DurationMs}; countdownMs={settings.CountdownMs}; " +
               $"repeatIntervalMs={settings.RepeatIntervalMs}; sampleIntervalMs={settings.SampleIntervalMs}; " +
               $"pulseDownMs={settings.PulseDownMs}; pulseUpMs={settings.PulseUpMs}; " +
               $"advanced={settings.AdvancedModeEnabled}; autoObservation={settings.AutoStartRecoveryObservation}; " +
               $"waypoint=({settings.SafeWaypointX?.ToString() ?? "none"},{settings.SafeWaypointY?.ToString() ?? "none"}); " +
               $"checkpoints={string.Join(",", settings.RecoveryCheckpointsSeconds)}";

        private static float NormalizeDegrees(float degrees)
        {
            float normalized = degrees % 360f;
            return normalized < 0 ? normalized + 360f : normalized;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  TEST 9 default runner — unmodified production MovementSystem
        // ─────────────────────────────────────────────────────────────────────

        private async Task<MovementDiagnosticMovementRunResult> RunExistingMovementSystemOnlyMoveAsync(
            MovementDiagnosticOnlyMoveContext context,
            CancellationToken token)
        {
            var waypoint = new Waypoint(
                context.TargetX,
                context.TargetY,
                MovementPrecision.Medium,
                BotMode.OnlyMove,
                steeringMode: MovementSteeringMode.KeyboardTurn);

            var recoveryEvents = new List<string>();
            var movement = new MovementSystem(
                context.MemoryService,
                line =>
                {
                    context.Log(line);
                    if (line.Contains("[Camera]", StringComparison.Ordinal) ||
                        line.Contains("[Unstuck]", StringComparison.Ordinal) ||
                        line.Contains("[Stuck", StringComparison.Ordinal) ||
                        line.Contains("[Recovery", StringComparison.Ordinal) ||
                        line.Contains("[ReverseDiagonal", StringComparison.Ordinal) ||
                        line.Contains("[WaypointRecovery", StringComparison.Ordinal))
                    {
                        lock (recoveryEvents)
                        {
                            recoveryEvents.Add(line);
                            context.RecordRelevantLog(line);
                        }
                    }
                },
                targetX: context.TargetX,
                targetY: context.TargetY,
                precision: MovementPrecision.Medium,
                customPath: new[] { waypoint },
                initialMode: BotMode.OnlyMove,
                loopPath: false,
                enableWaypointSpecialRecoveries: false,
                steeringMode: MovementSteeringMode.KeyboardTurn)
            {
                InternalRepotEnabled = false,
                AllowSpeedPotionBuff = false,
                ExpectedMap = 0,
                LootSystemRef = null
            };

            var clock = Stopwatch.StartNew();
            bool goalReached = false;
            int ticks = 0;
            try
            {
                while (clock.Elapsed.TotalMilliseconds < context.Settings.DurationMs)
                {
                    token.ThrowIfCancellationRequested();
                    await movement.Update(token).ConfigureAwait(false);
                    ticks++;

                    if (movement.IsGoalReached || movement.IsFinalStandbyActive)
                    {
                        goalReached = true;
                        break;
                    }

                    await Task.Delay(BotConstants.Delays.PathRunnerTickMs, token).ConfigureAwait(false);
                }
            }
            finally
            {
                movement.CancelMobGrouping("movement diagnostic finished");
                movement.CancelWaypointSpecialRecovery();
                movement.ReleaseCombatKeys();
                movement.StopMoving();
            }

            List<string> snapshot;
            lock (recoveryEvents)
                snapshot = recoveryEvents.ToList();

            bool directCamera = snapshot.Any(e =>
                e.Contains("[Camera] Apply", StringComparison.Ordinal) ||
                e.Contains("direct camera", StringComparison.OrdinalIgnoreCase));

            return new MovementDiagnosticMovementRunResult
            {
                GoalReached = goalReached,
                SteeringMode = MovementSteeringMode.KeyboardTurn.ToString(),
                DirectCameraRecoveryInvoked = directCamera,
                MovementRecoveryEvents = snapshot,
                UpdateTicks = ticks,
                Detail = $"unmodified MovementSystem; runtime={clock.Elapsed.TotalMilliseconds:F0} ms; stopped={movement.IsGoalReached || movement.IsFinalStandbyActive}"
            };
        }

        private static class MovementDiagnosticNative
        {
            [DllImport("user32.dll")]
            private static extern nint GetForegroundWindow();

            [DllImport("user32.dll")]
            private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

            public static uint GetForegroundProcessId()
            {
                try
                {
                    nint foreground = GetForegroundWindow();
                    if (foreground == nint.Zero)
                        return 0;
                    _ = GetWindowThreadProcessId(foreground, out uint pid);
                    return pid;
                }
                catch
                {
                    return 0;
                }
            }
        }
    }
}
