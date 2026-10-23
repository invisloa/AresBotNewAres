using System;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Lifecycle of a post-transition position synchronization. The normal workflow
    /// resumes only after <see cref="PositionReady"/>; an unconfirmed position is a
    /// hard blocker, never "probably wrong, teleport".
    /// </summary>
    public enum PositionSyncState
    {
        Normal,
        TransitionPending,
        MapConfirmed,
        PositionReady,
        PositionSynchronizationFailed
    }

    /// <summary>Outcome of one bounded position synchronization.</summary>
    public enum PositionSyncResultKind
    {
        Ready,

        /// <summary>
        /// The coordinates were proven to be live (the game responded to a bounded W
        /// refresh) but they genuinely do not match the supplied destination context.
        /// The caller may compare them / route them into an already configured recovery;
        /// stale coordinates never produce this outcome.
        /// </summary>
        DestinationMismatch,

        MapNotReached,
        Timeout,
        Unconfirmed,
        Cancelled,
        Superseded
    }

    /// <summary>
    /// How the optional destination expectation was satisfied (or not). This separates
    /// the three concepts the workflow needs: 1) destination map detected, 2) position
    /// updated after the transition, 3) position validated for the destination context.
    /// </summary>
    public enum DestinationValidationStatus
    {
        /// <summary>No destination expectation was supplied; the legacy contract applies.</summary>
        NoContext,

        /// <summary>The observed position matches the supplied destination expectation.</summary>
        MatchedDestination,

        /// <summary>Proven live by a bounded W response, but genuinely away from the destination expectation.</summary>
        ConfirmedMismatch,

        /// <summary>No usable evidence about the destination position could be established.</summary>
        Unconfirmed
    }

    /// <summary>
    /// Bounded timings for <see cref="PositionSyncService"/>. Defaults come from
    /// <see cref="BotConstants.Delays"/>; tests can shorten them without touching
    /// production behavior.
    /// </summary>
    public sealed class PositionSyncOptions
    {
        public int MapConfirmationTimeoutMs { get; set; } = BotConstants.Delays.PositionSyncMapConfirmationTimeoutMs;
        public int MapStableReadCount { get; set; } = BotConstants.Delays.MapTransitionStableReadCount;
        public int PollMs { get; set; } = BotConstants.Delays.PositionSyncPollMs;
        public int SettleTimeoutMs { get; set; } = BotConstants.Delays.PositionSyncSettleTimeoutMs;
        public int WRefreshSettleTimeoutMs { get; set; } = BotConstants.Delays.PositionSyncWRefreshSettleTimeoutMs;
        public int WRefreshPollMs { get; set; } = BotConstants.Delays.PositionSyncWRefreshPollMs;
        public int MaxWRefreshAttempts { get; set; } = BotConstants.Delays.PositionSyncMaxWRefreshAttempts;
        public int WRefreshRetryDelayMs { get; set; } = BotConstants.Delays.PositionSyncWRefreshRetryDelayMs;
        public int MaxTransitionRestarts { get; set; } = BotConstants.Delays.PositionSyncMaxTransitionRestarts;
        public int NudgeKeyDownMs { get; set; } = BotConstants.Delays.StartProtectionNudgeKeyDownMs;

        /// <summary>
        /// Chebyshev radius around the pre-transition position treated as the previous
        /// location. A candidate inside it is never accepted as fresh destination data.
        /// </summary>
        public int StaleSourceRadiusTiles { get; set; } = BotConstants.Delays.PositionSyncStaleSourceRadiusTiles;

        /// <summary>
        /// Consecutive bounded W refreshes showing a live position away from both the
        /// stale neighbourhood and the destination expectation required before a genuine
        /// destination mismatch may be confirmed.
        /// </summary>
        public int LiveMismatchConfirmations { get; set; } = BotConstants.Delays.PositionSyncLiveMismatchConfirmations;

        /// <summary>
        /// Largest coordinate delta (tiles) produced by ONE bounded W refresh that still
        /// looks like a real movement step. A much larger delta means the game published
        /// a different location (the stale→real jump after a portal) and is therefore not
        /// counted as a "live position" confirmation on its own.
        /// </summary>
        public int MaxPlausibleStepTiles { get; set; } = BotConstants.Delays.PositionSyncMaxPlausibleStepTiles;
    }

    /// <summary>Result of a position synchronization request.</summary>
    public readonly record struct PositionSyncResult(
        PositionSyncResultKind Kind,
        PlayerStateSnapshot State,
        long TransitionId,
        string Message)
    {
        public bool IsReady => Kind == PositionSyncResultKind.Ready;

        /// <summary>How the optional destination expectation was satisfied.</summary>
        public DestinationValidationStatus DestinationStatus { get; init; }

        /// <summary>The destination expectation used for this synchronization, when any.</summary>
        public DestinationValidationContext? Destination { get; init; }

        /// <summary>
        /// True when the coordinates may be used for a workflow decision: either they
        /// are ready, or they were proven live and genuinely differ from the destination
        /// expectation (a confirmed mismatch the caller may route into configured
        /// recovery). Unconfirmed / cancelled / superseded results are never validated.
        /// </summary>
        public bool IsValidated =>
            Kind == PositionSyncResultKind.Ready ||
            Kind == PositionSyncResultKind.DestinationMismatch;
    }

    /// <summary>
    /// Owns post-transition position validation. A map read is NOT treated as proof
    /// that X/Y are usable: after a transition the game can expose the destination map
    /// while the coordinates still belong to the previous location.
    ///
    /// Three concepts are kept separate:
    ///   1. the destination map is detected (map confirmation),
    ///   2. a post-transition position update is observed,
    ///   3. the position is validated for the destination context supplied by the
    ///      workflow (route start, protected map, ...) when one is available.
    ///
    /// Freshness is established from observable evidence only:
    ///   • the snapshot matches the supplied destination context, or
    ///   • (without a context) a coherent post-transition snapshot lies clearly OUTSIDE
    ///     the pre-transition neighbourhood — a shift of a couple of tiles around the
    ///     portal is exactly what stale coordinates look like and is never accepted, or
    ///   • a coordinate change is observed after one bounded W refresh press.
    ///
    /// Identical repeated coordinates are never used as proof of freshness. When the
    /// destination context cannot be satisfied the result is
    /// <see cref="PositionSyncResultKind.Unconfirmed"/> — never a fabricated
    /// <see cref="PositionSyncResultKind.Ready"/>. Only coordinates proven live while
    /// genuinely away from the destination may be reported as
    /// <see cref="PositionSyncResultKind.DestinationMismatch"/>.
    /// </summary>
    public sealed class PositionSyncService
    {
        private readonly GameMemoryService _memory;
        private readonly Action<string> _log;
        private readonly Action _focusGameWindow;
        private readonly Func<CancellationToken, Task> _wRefreshAsync;
        private readonly Action<byte, byte, uint> _keyEvent;
        private readonly PositionSyncOptions _options;
        private readonly WKeyCameraController? _wKeyCameraController;
        private readonly WKeyCameraOwner? _wKeyCameraOwner;

        private readonly object _lock = new();
        private readonly SemaphoreSlim _syncGate = new(1, 1);

        private long _transitionId;
        private bool _transitionPending;
        private string _transitionReason = "";
        private PlayerStateSnapshot? _preTransitionState;

        /// <summary>
        /// Last snapshot from BEFORE a real map transition. Unlike
        /// <see cref="_preTransitionState"/> it survives a completed transition, so a
        /// later destination validation can still recognise coordinates that stayed in
        /// the previous location's neighbourhood (the confirmed COT failure).
        /// </summary>
        private PlayerStateSnapshot? _staleReference;
        private PlayerStateSnapshot? _lastValidatedState;
        private PlayerStateSnapshot? _lastObservedState;
        private PositionSyncState _state = PositionSyncState.Normal;

        public PositionSyncService(
            GameMemoryService memory,
            Action<string> log,
            Action focusGameWindow,
            Func<CancellationToken, Task>? wRefreshAsync = null,
            PositionSyncOptions? options = null,
            Action<byte, byte, uint>? keyEvent = null,
            WKeyCameraController? wKeyCameraController = null)
        {
            _memory = memory ?? throw new ArgumentNullException(nameof(memory));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _focusGameWindow = focusGameWindow ?? throw new ArgumentNullException(nameof(focusGameWindow));
            _wRefreshAsync = wRefreshAsync ?? PressWRefreshAsync;
            _options = options ?? new PositionSyncOptions();
            _keyEvent = keyEvent ?? ((vk, scan, flags) => GameInput.keybd_event(vk, scan, flags, 0));
            _wKeyCameraController = wKeyCameraController;
            _wKeyCameraOwner = wKeyCameraController?.CreateOwner(
                "PositionSync W refresh",
                keyUp => _keyEvent(
                    GameInput.VK_W,
                    GameInput.SCAN_W,
                    keyUp ? (uint)GameInput.KEYEVENTF_KEYUP : 0u));
        }

        /// <summary>Current state of the transition/position lifecycle.</summary>
        public PositionSyncState State
        {
            get { lock (_lock) return _state; }
        }

        /// <summary>True while a transition still needs its position validated.</summary>
        public bool HasPendingTransition
        {
            get { lock (_lock) return _transitionPending; }
        }

        /// <summary>ID of the newest observed transition (monotonically increasing).</summary>
        public long TransitionId
        {
            get { lock (_lock) return _transitionId; }
        }

        /// <summary>The latest position snapshot that passed validation.</summary>
        public PlayerStateSnapshot? LastValidatedState
        {
            get { lock (_lock) return _lastValidatedState; }
        }

        /// <summary>The latest coherent/partial snapshot actually observed.</summary>
        public PlayerStateSnapshot? LastObservedState
        {
            get { lock (_lock) return _lastObservedState; }
        }

        /// <summary>Clears all state for a fresh workflow run.</summary>
        public void Reset()
        {
            lock (_lock)
            {
                _transitionId = 0;
                _transitionPending = false;
                _transitionReason = "";
                _preTransitionState = null;
                _staleReference = null;
                _lastValidatedState = null;
                _lastObservedState = null;
                _state = PositionSyncState.Normal;
            }
        }

        /// <summary>
        /// Reads a coherent live snapshot and remembers it as the latest observation.
        /// This never changes trust/transition state by itself.
        /// </summary>
        public PlayerStateSnapshot CaptureCurrentState()
        {
            PlayerStateSnapshot snapshot = _memory.GetPlayerStateSnapshot();
            RememberObservation(snapshot);
            return snapshot;
        }

        /// <summary>
        /// Marks the point immediately BEFORE an operation that can change the map (a
        /// portal click, a town-teleport key, ...). Capturing the source snapshot here
        /// is what makes stale destination coordinates detectable: the game may keep
        /// showing the old X/Y after the map number has already changed.
        /// </summary>
        public void MarkTransitionImminent(string reason)
        {
            PlayerStateSnapshot source = CaptureCurrentState();
            BeginTransitionCore(reason, source);
        }

        /// <summary>
        /// Starts a transition that was detected after the fact (e.g. an operation
        /// completed and its map changed). <paramref name="source"/> should be the last
        /// state observed before the transition when available.
        /// </summary>
        public void BeginTransition(string reason, PlayerStateSnapshot? source = null)
        {
            PlayerStateSnapshot effectiveSource = source ?? CaptureCurrentState();
            BeginTransitionCore(reason, effectiveSource);
        }

        /// <summary>
        /// Drops a transition that was marked but did not actually occur (for example an
        /// operation that clicked an entry option but the map never changed). This keeps
        /// a stale pending marker from blocking the next unrelated start check.
        /// </summary>
        public void CancelPendingTransition(string reason)
        {
            lock (_lock)
            {
                if (!_transitionPending)
                    return;

                _transitionPending = false;
                _preTransitionState = null;
                _state = PositionSyncState.Normal;
            }

            _log($"[PositionSync] Pending transition cancelled: {reason}.");
        }

        /// <summary>
        /// Ensures the current location has trustworthy coordinates. With no pending
        /// transition and no destination context this only requires a coherent read
        /// (normal navigation is unaffected). An unmarked map change compared with the
        /// last validated map automatically starts a transition.
        ///
        /// When <paramref name="destination"/> is supplied the position must satisfy the
        /// destination expectation — a coherent read alone is not enough. A snapshot that
        /// does not satisfy it starts a bounded validation (settle window + mini-W
        /// refresh) instead of being reported READY.
        /// </summary>
        public Task<PositionSyncResult> EnsurePositionReadyAsync(
            string reason,
            int expectedMap,
            CancellationToken token)
            => EnsurePositionReadyAsync(reason, expectedMap, null, token);

        /// <inheritdoc cref="EnsurePositionReadyAsync(string, int, CancellationToken)"/>
        public async Task<PositionSyncResult> EnsurePositionReadyAsync(
            string reason,
            int expectedMap,
            DestinationValidationContext? destination,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            PlayerStateSnapshot observed = CaptureCurrentState();
            bool autoTransition = false;
            PlayerStateSnapshot? source = null;

            lock (_lock)
            {
                if (!_transitionPending &&
                    _lastValidatedState is { } validated &&
                    validated.HasCoherentState &&
                    observed.HasCoherentState &&
                    observed.Map != validated.Map)
                {
                    autoTransition = true;
                    source = validated;
                }
            }

            if (autoTransition)
                BeginTransitionCore($"Map change detected by {reason}", source);

            bool pending;
            lock (_lock)
                pending = _transitionPending;

            if (pending)
                return await SynchronizeAsync(expectedMap, destination, token);

            if (destination != null)
            {
                // No transition marker, but the workflow knows what the destination has
                // to look like. Coherence is NOT validation: an earlier context-free
                // check may have accepted coordinates that still belonged to the
                // previous map (the confirmed COT failure).
                if (destination.Matches(observed))
                {
                    lock (_lock)
                    {
                        _lastValidatedState = observed;
                        _lastObservedState = observed;
                        _state = PositionSyncState.PositionReady;
                    }

                    _log($"[PositionSync] Destination accepted without a transition: Destination={destination.Describe()} {observed.Describe()}");
                    return new PositionSyncResult(
                        PositionSyncResultKind.Ready,
                        observed,
                        TransitionId,
                        $"Position matches the destination expectation ({destination.Label}).")
                    {
                        DestinationStatus = DestinationValidationStatus.MatchedDestination,
                        Destination = destination
                    };
                }

                _log($"[PositionSync] Destination not satisfied by the current snapshot — starting a bounded position validation: Destination={destination.Describe()} Source={observed.Describe()}");
                BeginTransitionCore($"Destination validation ({destination.Label})", observed, preserveStaleReference: true);
                return await SynchronizeAsync(expectedMap, destination, token);
            }

            if (observed.HasCoherentState)
            {
                lock (_lock)
                {
                    _lastValidatedState = observed;
                    _lastObservedState = observed;
                    _state = PositionSyncState.PositionReady;
                }

                _log($"[PositionSync] No transition pending — current player state is coherent ({observed.Describe()}).");
                return new PositionSyncResult(
                    PositionSyncResultKind.Ready,
                    observed,
                    TransitionId,
                    "No transition pending; coherent player state.");
            }

            lock (_lock)
                _state = PositionSyncState.PositionSynchronizationFailed;

            _log($"[PositionSync] Player state is not readable and no transition is pending ({observed.Describe()}).");
            return new PositionSyncResult(
                PositionSyncResultKind.Unconfirmed,
                observed,
                TransitionId,
                "Player pointer/map/position read failed.")
            {
                DestinationStatus = destination != null
                    ? DestinationValidationStatus.Unconfirmed
                    : DestinationValidationStatus.NoContext,
                Destination = destination
            };
        }

        /// <summary>
        /// Validates the position for the current pending transition. Callers must
        /// inspect <see cref="PositionSyncResult.IsReady"/> (or <see cref="PositionSyncResult.IsValidated"/>)
        /// — a non-validated result means the workflow must not compare coordinates or
        /// perform destructive recovery.
        /// </summary>
        public Task<PositionSyncResult> SynchronizeAsync(int expectedMap, CancellationToken token)
            => SynchronizeAsync(expectedMap, null, token);

        /// <summary>
        /// Destination-aware synchronization. When <paramref name="destination"/> is
        /// supplied, a coherent-but-unvalidated position starts a bounded validation
        /// instead of being reported READY.
        /// </summary>
        public async Task<PositionSyncResult> SynchronizeAsync(
            int expectedMap,
            DestinationValidationContext? destination,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            bool pending;
            lock (_lock)
                pending = _transitionPending;

            if (!pending)
            {
                PlayerStateSnapshot snapshot = CaptureCurrentState();

                if (destination != null && !destination.Matches(snapshot))
                {
                    _log($"[PositionSync] No transition pending, but the destination is not satisfied — starting a bounded position validation: Destination={destination.Describe()} Observed={snapshot.Describe()}");
                    BeginTransitionCore($"Destination validation ({destination.Label})", snapshot, preserveStaleReference: true);
                    return await SynchronizeInternalAsync(expectedMap, destination, token);
                }

                if (snapshot.HasCoherentState)
                {
                    lock (_lock)
                    {
                        _lastValidatedState = snapshot;
                        _state = PositionSyncState.PositionReady;
                    }

                    return new PositionSyncResult(
                        PositionSyncResultKind.Ready,
                        snapshot,
                        TransitionId,
                        "No transition pending; coherent player state.")
                    {
                        DestinationStatus = destination != null
                            ? DestinationValidationStatus.MatchedDestination
                            : DestinationValidationStatus.NoContext,
                        Destination = destination
                    };
                }

                return new PositionSyncResult(
                    PositionSyncResultKind.Unconfirmed,
                    snapshot,
                    TransitionId,
                    "No transition pending and player state is unreadable.")
                {
                    DestinationStatus = destination != null
                        ? DestinationValidationStatus.Unconfirmed
                        : DestinationValidationStatus.NoContext,
                    Destination = destination
                };
            }

            return await SynchronizeInternalAsync(expectedMap, destination, token);
        }

        /// <summary>
        /// Releases W in a finally block so cancellation during the press can never
        /// leave the movement key held down. The client processes movement on its own
        /// input ticks, so the tap is a fixed, bounded hold (long enough for one real
        /// step) — never an open-ended movement loop.
        /// </summary>
        public async Task PressWRefreshAsync(CancellationToken token)
        {
            _focusGameWindow();
            _log($"[PositionSync] Tapping W for {_options.NudgeKeyDownMs} ms (bounded mini-W refresh)...");

            bool cameraOwnerAcquired = false;
            if (_wKeyCameraController != null && _wKeyCameraOwner != null)
            {
                if (!_wKeyCameraController.TryAcquireW(_wKeyCameraOwner))
                    throw new InvalidOperationException("W refresh was blocked because the movement camera lock could not be verified or the camera is owned by another operation.");
                cameraOwnerAcquired = true;
            }

            try
            {
                // The shared controller applies the movement view and performs the
                // physical W-down only after that lock verifies. The legacy path keeps
                // its direct bounded key-down when no controller is configured.
                if (!cameraOwnerAcquired)
                    _keyEvent(GameInput.VK_W, GameInput.SCAN_W, 0);
                await Task.Delay(_options.NudgeKeyDownMs, token);
            }
            finally
            {
                if (cameraOwnerAcquired && _wKeyCameraController != null && _wKeyCameraOwner != null)
                    _wKeyCameraController.ReleaseW(_wKeyCameraOwner);
                else
                    _keyEvent(GameInput.VK_W, GameInput.SCAN_W, (uint)GameInput.KEYEVENTF_KEYUP);
                _log("[PositionSync] W refresh completed: KeyReleased=True.");
            }
        }

        private async Task<PositionSyncResult> SynchronizeInternalAsync(
            int expectedMap,
            DestinationValidationContext? destination,
            CancellationToken token)
        {
            bool gateTaken = false;
            long activeTransitionId = 0;

            try
            {
                await _syncGate.WaitAsync(token);
                gateTaken = true;

                for (int restart = 0; restart <= _options.MaxTransitionRestarts; restart++)
                {
                    bool noPending;
                    long id;
                    string reason;
                    PlayerStateSnapshot? source;

                    lock (_lock)
                    {
                        noPending = !_transitionPending;
                        id = _transitionId;
                        activeTransitionId = id;
                        reason = _transitionReason;
                        source = _preTransitionState;
                    }

                    if (noPending)
                    {
                        PlayerStateSnapshot snapshot = CaptureCurrentState();

                        if (destination != null && !destination.Matches(snapshot))
                        {
                            // The transition marker was already cleared (e.g. by an
                            // earlier context-free check) but the workflow's destination
                            // expectation is still not satisfied: validate it through the
                            // bounded refresh instead of returning READY.
                            BeginTransitionCore($"Destination validation ({destination.Label})", snapshot, preserveStaleReference: true);

                            lock (_lock)
                            {
                                id = _transitionId;
                                activeTransitionId = id;
                                reason = _transitionReason;
                                source = _preTransitionState;
                            }
                        }
                        else
                        {
                            lock (_lock)
                            {
                                if (snapshot.HasCoherentState)
                                {
                                    _lastValidatedState = snapshot;
                                    _state = PositionSyncState.PositionReady;
                                    return new PositionSyncResult(
                                        PositionSyncResultKind.Ready,
                                        snapshot,
                                        _transitionId,
                                        "Transition already completed.")
                                    {
                                        DestinationStatus = destination != null
                                            ? DestinationValidationStatus.MatchedDestination
                                            : DestinationValidationStatus.NoContext,
                                        Destination = destination
                                    };
                                }

                                _state = PositionSyncState.PositionSynchronizationFailed;
                                return new PositionSyncResult(
                                    PositionSyncResultKind.Unconfirmed,
                                    snapshot,
                                    _transitionId,
                                    "Transition is no longer pending but the player state is unreadable.")
                                {
                                    DestinationStatus = destination != null
                                        ? DestinationValidationStatus.Unconfirmed
                                        : DestinationValidationStatus.NoContext,
                                    Destination = destination
                                };
                            }
                        }
                    }

                    PositionSyncResult result = await RunTransitionSyncAsync(
                        id,
                        reason,
                        source,
                        expectedMap,
                        destination,
                        token);

                    if (result.Kind == PositionSyncResultKind.Superseded &&
                        !token.IsCancellationRequested &&
                        restart < _options.MaxTransitionRestarts)
                    {
                        _log($"[PositionSync] Transition {id} was superseded by a newer transition — restarting synchronization ({restart + 1}/{_options.MaxTransitionRestarts}).");
                        continue;
                    }

                    lock (_lock)
                    {
                        // A newer transition arrived after the result was produced: it must
                        // never inherit this attempt's success.
                        if (id != _transitionId)
                        {
                            _state = PositionSyncState.TransitionPending;
                            return new PositionSyncResult(
                                PositionSyncResultKind.Superseded,
                                result.State,
                                _transitionId,
                                "A newer map transition started while synchronizing.");
                        }

                        if (result.IsReady)
                        {
                            _transitionPending = false;
                            _transitionReason = "";
                            _preTransitionState = null;
                            _lastValidatedState = result.State;
                            _lastObservedState = result.State;
                            _state = PositionSyncState.PositionReady;
                        }
                        else if (result.Kind != PositionSyncResultKind.Cancelled)
                        {
                            // A confirmed destination mismatch keeps the transition
                            // pending: the workflow resolves it through its configured
                            // recovery (which starts a newer transition), never by
                            // silently trusting the coordinates.
                            _state = PositionSyncState.PositionSynchronizationFailed;
                        }
                        else
                        {
                            _state = PositionSyncState.PositionSynchronizationFailed;
                        }
                    }

                    _log(destination is null
                        ? $"[PositionSync] Result={result.Kind} TransitionId={id} {result.State.Describe()} Message={result.Message}"
                        : $"[PositionSync] Result={result.Kind} TransitionId={id} DestinationStatus={result.DestinationStatus} Destination={destination.Describe()} {result.State.Describe()} Message={result.Message}");
                    return result;
                }

                PlayerStateSnapshot last = LastObservedState ?? default;
                lock (_lock)
                    _state = PositionSyncState.PositionSynchronizationFailed;

                _log($"[PositionSync] Result=Superseded TransitionId={activeTransitionId} {last.Describe()} Message=transition restart budget exhausted.");
                return new PositionSyncResult(
                    PositionSyncResultKind.Superseded,
                    last,
                    activeTransitionId,
                    "Transition restart budget exhausted.");
            }
            catch (OperationCanceledException)
            {
                PlayerStateSnapshot last = LastObservedState ?? default;
                lock (_lock)
                    _state = PositionSyncState.PositionSynchronizationFailed;

                _log($"[PositionSync] Result=Cancelled TransitionId={activeTransitionId} {last.Describe()} Message=cancellation requested; W is released by the refresh helper.");
                return new PositionSyncResult(
                    PositionSyncResultKind.Cancelled,
                    last,
                    activeTransitionId,
                    "Position synchronization cancelled.");
            }
            finally
            {
                if (gateTaken)
                    _syncGate.Release();
            }
        }

        private async Task<PositionSyncResult> RunTransitionSyncAsync(
            long transitionId,
            string reason,
            PlayerStateSnapshot? source,
            int expectedMap,
            DestinationValidationContext? destination,
            CancellationToken token)
        {
            PlayerStateSnapshot latest = default;
            int stableMapReads = 0;
            int lastStableMap = 0;
            bool mapConfirmed = false;

            PlayerStateSnapshot? staleReference;
            lock (_lock)
                staleReference = _staleReference;

            DateTime mapDeadline = DateTime.UtcNow.AddMilliseconds(_options.MapConfirmationTimeoutMs);
            while (DateTime.UtcNow < mapDeadline)
            {
                token.ThrowIfCancellationRequested();
                latest = ReadState();

                if (IsSuperseded(transitionId))
                {
                    return new PositionSyncResult(
                        PositionSyncResultKind.Superseded,
                        latest,
                        transitionId,
                        "A newer transition started while waiting for the destination map.");
                }

                if (latest.PlayerPointerReadSucceeded &&
                    latest.MapReadSucceeded &&
                    latest.Map != 0 &&
                    latest.PositionReadSucceeded)
                {
                    bool expectedMapOk = expectedMap <= 0 || latest.Map == expectedMap;
                    if (expectedMapOk)
                    {
                        if (stableMapReads == 0 || latest.Map != lastStableMap)
                        {
                            stableMapReads = 1;
                            lastStableMap = latest.Map;
                        }
                        else
                        {
                            stableMapReads++;
                        }

                        if (stableMapReads >= _options.MapStableReadCount)
                        {
                            mapConfirmed = true;
                            break;
                        }
                    }
                    else
                    {
                        stableMapReads = 0;
                        lastStableMap = latest.Map;
                    }
                }
                else
                {
                    stableMapReads = 0;
                }

                await Task.Delay(_options.PollMs, token);
            }

            if (!mapConfirmed)
            {
                token.ThrowIfCancellationRequested();

                if (IsSuperseded(transitionId))
                {
                    return new PositionSyncResult(
                        PositionSyncResultKind.Superseded,
                        latest,
                        transitionId,
                        "A newer transition started while waiting for the destination map.");
                }

                bool wrongMap = expectedMap > 0 &&
                                 latest.MapReadSucceeded &&
                                 latest.Map != 0 &&
                                 latest.Map != expectedMap;

                PositionSyncResultKind kind = wrongMap
                    ? PositionSyncResultKind.MapNotReached
                    : PositionSyncResultKind.Timeout;

                string message = wrongMap
                    ? $"Destination map {expectedMap} was not reached (last map {latest.Map})."
                    : expectedMap > 0
                        ? $"Destination map {expectedMap} did not stabilize within {_options.MapConfirmationTimeoutMs} ms."
                        : $"Map did not stabilize within {_options.MapConfirmationTimeoutMs} ms.";

                _log($"[PositionSync] Destination map not confirmed: Expected={expectedMap} {latest.Describe()} Reason={reason} Destination={(destination != null ? destination.Describe() : "none")}.");
                return new PositionSyncResult(kind, latest, transitionId, message)
                {
                    DestinationStatus = destination != null
                        ? DestinationValidationStatus.Unconfirmed
                        : DestinationValidationStatus.NoContext,
                    Destination = destination
                };
            }

            lock (_lock)
            {
                if (transitionId == _transitionId)
                    _state = PositionSyncState.MapConfirmed;
            }

            _log($"[PositionSync] Destination map confirmed: {latest.Map} (TransitionId={transitionId}, Reason={reason}).");
            _log($"[PositionSync] Waiting for post-transition position... {latest.Describe()}");

            // Classify the FIRST post-transition observation. If it still sits in the
            // pre-transition location's neighbourhood it is stale/suspicious: such a
            // state may only ever resolve into a destination match or a safe
            // UNCONFIRMED — never into a confirmed mismatch (destructive recovery must
            // not be reachable from a stale read).
            bool initialPositionSuspicious = IsStaleLooking(latest, source, staleReference);

            if (destination != null)
            {
                _log($"[PositionSync] Destination validation active: Destination={destination.Describe()} Source={(source.HasValue ? source.Value.Describe() : "unknown")} StaleReference={(staleReference.HasValue ? staleReference.Value.Describe() : "unknown")} StaleRadius={_options.StaleSourceRadiusTiles} InitialSuspicious={initialPositionSuspicious}");
            }

            // First allow the game itself to finish publishing the new position. This is
            // bounded polling, not a blind fixed sleep: a destination match (or, without
            // a destination context, a coordinate change clearly outside the previous
            // location) ends it early.
            DateTime settleDeadline = DateTime.UtcNow.AddMilliseconds(_options.SettleTimeoutMs);
            while (DateTime.UtcNow < settleDeadline)
            {
                token.ThrowIfCancellationRequested();
                latest = ReadState();

                if (IsSuperseded(transitionId))
                {
                    return new PositionSyncResult(
                        PositionSyncResultKind.Superseded,
                        latest,
                        transitionId,
                        "A newer transition started while waiting for post-transition coordinates.");
                }

                if (latest.HasCoherentState && latest.Map != lastStableMap)
                {
                    if (TrySupersedeForMapChange(transitionId, latest))
                    {
                        return new PositionSyncResult(
                            PositionSyncResultKind.Superseded,
                            latest,
                            transitionId,
                            $"Map changed again to {latest.Map} while synchronizing.");
                    }
                }

                if (destination != null)
                {
                    if (destination.Matches(latest))
                    {
                        _log($"[PositionSync] Destination position published by the game without a W refresh: Destination={destination.Describe()} {latest.Describe()}");
                        return ReadyResult(latest, transitionId, $"Position matches the destination expectation ({destination.Label}).", destination, DestinationValidationStatus.MatchedDestination);
                    }
                }
                else if (HasFreshnessEvidence(latest, source))
                {
                    _log($"[PositionSync] Position freshness established without a W refresh: {latest.Describe()}");
                    return ReadyResult(latest, transitionId, "Coordinates changed after the transition.", null, DestinationValidationStatus.NoContext);
                }

                await Task.Delay(_options.PollMs, token);
            }

            int liveMismatchReads = 0;

            for (int attempt = 1; attempt <= _options.MaxWRefreshAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();

                if (IsSuperseded(transitionId))
                {
                    return new PositionSyncResult(
                        PositionSyncResultKind.Superseded,
                        latest,
                        transitionId,
                        "A newer transition started before the W refresh.");
                }

                PlayerStateSnapshot before = ReadState();
                latest = before;

                if (before.HasCoherentState && before.Map != lastStableMap)
                {
                    if (TrySupersedeForMapChange(transitionId, before))
                    {
                        return new PositionSyncResult(
                            PositionSyncResultKind.Superseded,
                            before,
                            transitionId,
                            $"Map changed again to {before.Map} before the W refresh.");
                    }
                }

                if (destination != null)
                {
                    if (destination.Matches(before))
                    {
                        _log($"[PositionSync] Destination position published before the W refresh: Destination={destination.Describe()} {before.Describe()}");
                        return ReadyResult(before, transitionId, $"Position matches the destination expectation ({destination.Label}).", destination, DestinationValidationStatus.MatchedDestination);
                    }
                }
                else if (HasFreshnessEvidence(before, source))
                {
                    _log($"[PositionSync] Position freshness established before a W refresh: {before.Describe()}");
                    return ReadyResult(before, transitionId, "Coordinates changed after the transition.", null, DestinationValidationStatus.NoContext);
                }

                _log($"[PositionSync] W refresh requested: attempt {attempt}/{_options.MaxWRefreshAttempts} Reason={(destination != null ? "destination position not satisfied" : "no fresh coordinate change yet")} MapBefore={(before.MapReadSucceeded ? before.Map.ToString() : "unreadable")} PositionBefore={(before.PositionReadSucceeded ? $"({before.X},{before.Y})" : "(unreadable)")} Source={(source.HasValue ? source.Value.Describe() : "unknown")} Destination={(destination != null ? destination.Describe() : "none")}");

                bool released = false;
                try
                {
                    await _wRefreshAsync(token);
                    released = true;
                }
                catch (OperationCanceledException)
                {
                    _log("[PositionSync] W refresh cancelled during the press; the input helper releases W in a finally block.");
                    throw;
                }
                catch (Exception ex)
                {
                    _log($"[PositionSync] W refresh failed: {ex.Message}");
                }

                PlayerStateSnapshot after = before;
                bool refreshed = false;
                DateTime refreshDeadline = DateTime.UtcNow.AddMilliseconds(_options.WRefreshSettleTimeoutMs);

                // Poll the whole bounded window instead of stopping at the first change:
                // the game can publish an intermediate position for a few frames before
                // the real destination spawn. Only the settled result is classified.
                while (DateTime.UtcNow < refreshDeadline)
                {
                    token.ThrowIfCancellationRequested();
                    after = ReadState();

                    if (IsSuperseded(transitionId))
                    {
                        return new PositionSyncResult(
                            PositionSyncResultKind.Superseded,
                            after,
                            transitionId,
                            "A newer transition started after the W refresh.");
                    }

                    if (after.HasCoherentState && after.Map != lastStableMap)
                    {
                        if (TrySupersedeForMapChange(transitionId, after))
                        {
                            return new PositionSyncResult(
                                PositionSyncResultKind.Superseded,
                                after,
                                transitionId,
                                $"Map changed again to {after.Map} after the W refresh.");
                        }
                    }

                    if (destination != null)
                    {
                        if (destination.Matches(after))
                            break;
                    }
                    else if (HasFreshnessEvidence(after, source) ||
                             (before.HasCoherentState &&
                              after.HasCoherentState &&
                              after.Map == before.Map &&
                              after.PositionChangedFrom(before)))
                    {
                        refreshed = true;
                        break;
                    }

                    await Task.Delay(_options.WRefreshPollMs, token);
                }

                bool destinationMatched = destination != null && destination.Matches(after);
                bool liveChanged = before.HasCoherentState &&
                                   after.HasCoherentState &&
                                   after.Map == before.Map &&
                                   after.PositionChangedFrom(before);
                int stepTiles = before.HasCoherentState && after.HasCoherentState && after.Map == before.Map
                    ? Math.Max(Math.Abs((int)after.X - before.X), Math.Abs((int)after.Y - before.Y))
                    : int.MaxValue;
                bool plausibleStep = liveChanged && stepTiles <= Math.Max(0, _options.MaxPlausibleStepTiles);
                bool destinationMapOk = destination == null ||
                                        !destination.HasMapExpectation ||
                                        (after.HasCoherentState && after.Map == destination.ExpectedMap);
                // Coordinates that stayed in the pre-transition location's neighbourhood
                // while the map changed are suspicious, not proof of the new location.
                bool inStaleNeighbourhood = staleReference is { HasCoherentState: true } stale &&
                                            stale.Map != after.Map &&
                                            IsWithinStaleNeighbourhood(after, stale);

                _log($"[PositionSync] W refresh completed: Attempt={attempt}/{_options.MaxWRefreshAttempts} KeyReleased={released} MapBefore={(before.MapReadSucceeded ? before.Map.ToString() : "unreadable")} PositionBefore={(before.PositionReadSucceeded ? $"({before.X},{before.Y})" : "(unreadable)")} MapAfter={(after.MapReadSucceeded ? after.Map.ToString() : "unreadable")} PositionAfter={(after.PositionReadSucceeded ? $"({after.X},{after.Y})" : "(unreadable)")} Changed={liveChanged} StepTiles={stepTiles} PlausibleStep={plausibleStep} StaleNeighbourhood={inStaleNeighbourhood} DestinationMapOk={destinationMapOk} DestinationMatch={destinationMatched} Destination={(destination != null ? destination.Describe() : "none")} LiveMismatchConfirmations={liveMismatchReads}/{_options.LiveMismatchConfirmations}");

                if (destinationMatched)
                {
                    return ReadyResult(after, transitionId, $"Destination position confirmed after the bounded W refresh ({destination!.Label}).", destination, DestinationValidationStatus.MatchedDestination);
                }

                if (destination != null)
                {
                    // A bounded W response that moves the player by a real STEP while
                    // clearly away from the destination expectation is evidence of a
                    // live, genuinely different position. A stale→real location jump or
                    // a candidate still sitting in the previous location's neighbourhood
                    // never counts. Repeated observations are required so a lingering
                    // stale read can never be routed into destructive recovery.
                    if (plausibleStep && destinationMapOk && !inStaleNeighbourhood)
                        liveMismatchReads++;
                    else
                        liveMismatchReads = 0;

                    if (liveMismatchReads >= _options.LiveMismatchConfirmations && !initialPositionSuspicious)
                    {
                        return DestinationMismatchResult(
                            after,
                            transitionId,
                            destination,
                            $"Position is live but genuinely outside the destination expectation ({destination.Label}) after {liveMismatchReads} bounded W refresh response(s).");
                    }

                    if (initialPositionSuspicious)
                    {
                        _log($"[PositionSync] Genuine-mismatch recovery NOT allowed: the initial post-transition position was inside the previous location's neighbourhood (InitialSuspicious=True), so the coordinates must not drive destructive recovery. Destination={destination.Describe()} PositionAfter={(after.PositionReadSucceeded ? $"({after.X},{after.Y})" : "(unreadable)")}");
                    }
                }
                else if (refreshed)
                {
                    return ReadyResult(after, transitionId, "Coordinates updated by the bounded W refresh.", null, DestinationValidationStatus.NoContext);
                }

                if (attempt < _options.MaxWRefreshAttempts)
                    await Task.Delay(_options.WRefreshRetryDelayMs, token);
            }

            PlayerStateSnapshot finalState = ReadState();
            string finalMessage = destination != null
                ? $"Destination position ({destination.Label}) could not be validated after {_options.MaxWRefreshAttempts} bounded W refresh attempt(s); the coordinates were not accepted" +
                  (initialPositionSuspicious ? " (the initial post-transition position was suspicious/stale, so recovery was blocked)." : ".")
                : "Coordinates could not be proven fresh after the transition.";

            _log($"[PositionSync] Position could not be proven fresh after {_options.MaxWRefreshAttempts} bounded W refresh attempt(s): Result=Unconfirmed DestinationStatus={(destination != null ? DestinationValidationStatus.Unconfirmed.ToString() : DestinationValidationStatus.NoContext.ToString())} Destination={(destination != null ? destination.Describe() : "none")} Source={(source.HasValue ? source.Value.Describe() : "unknown")} InitialSuspicious={initialPositionSuspicious} {finalState.Describe()} Reason={finalMessage}");
            return new PositionSyncResult(
                PositionSyncResultKind.Unconfirmed,
                finalState,
                transitionId,
                finalMessage)
            {
                DestinationStatus = destination != null
                    ? DestinationValidationStatus.Unconfirmed
                    : DestinationValidationStatus.NoContext,
                Destination = destination
            };
        }

        private PlayerStateSnapshot ReadState()
        {
            PlayerStateSnapshot snapshot = _memory.GetPlayerStateSnapshot();
            RememberObservation(snapshot);
            return snapshot;
        }

        private void RememberObservation(PlayerStateSnapshot snapshot)
        {
            lock (_lock)
                _lastObservedState = snapshot;
        }

        private bool IsSuperseded(long transitionId)
        {
            lock (_lock)
                return transitionId != _transitionId;
        }

        private bool TrySupersedeForMapChange(long transitionId, PlayerStateSnapshot state)
        {
            lock (_lock)
            {
                if (transitionId != _transitionId)
                    return true;
                if (!_transitionPending)
                    return false;
            }

            BeginTransitionCore(
                $"Map changed again during position synchronization (was transition {transitionId})",
                state);
            return true;
        }

        /// <summary>
        /// Context-free freshness evidence. A map read alone is never enough and a tiny
        /// coordinate shift around the pre-transition position is exactly what stale
        /// coordinates look like, so a candidate only counts when it lies clearly OUTSIDE
        /// the previous location neighbourhood. When the workflow knows the destination
        /// (route start, protected map) it supplies a
        /// <see cref="DestinationValidationContext"/> and this method is not used.
        /// </summary>
        private bool HasFreshnessEvidence(PlayerStateSnapshot state, PlayerStateSnapshot? source)
        {
            if (!state.HasCoherentState)
                return false;

            if (source is { } preTransition &&
                preTransition.HasCoherentState)
            {
                return IsBeyondStaleNeighbourhood(state, preTransition);
            }

            lock (_lock)
            {
                if (_lastValidatedState is { } validated &&
                    validated.HasCoherentState &&
                    state.Map == validated.Map)
                {
                    return IsBeyondStaleNeighbourhood(state, validated);
                }
            }

            return false;
        }

        /// <summary>
        /// True when the candidate is farther than the configured jitter radius from the
        /// reference position (the previous location). Coordinates inside that radius are
        /// never accepted as proof of a new location.
        /// </summary>
        private bool IsWithinStaleNeighbourhood(PlayerStateSnapshot candidate, PlayerStateSnapshot? reference)
        {
            if (reference is not { HasCoherentState: true } source)
                return false;

            if (!candidate.HasCoherentState)
                return false;

            int offsetX = Math.Abs((int)candidate.X - source.X);
            int offsetY = Math.Abs((int)candidate.Y - source.Y);
            return Math.Max(offsetX, offsetY) <= Math.Max(0, _options.StaleSourceRadiusTiles);
        }

        private bool IsBeyondStaleNeighbourhood(PlayerStateSnapshot candidate, PlayerStateSnapshot reference)
            => !IsWithinStaleNeighbourhood(candidate, reference);

        /// <summary>
        /// True when the candidate still looks like the PREVIOUS location: it lies inside
        /// the stale radius of a reference snapshot taken on a DIFFERENT map. Such a
        /// position is suspicious and may never be used to authorise destructive recovery.
        /// </summary>
        private bool IsStaleLooking(
            PlayerStateSnapshot candidate,
            PlayerStateSnapshot? transitionSource,
            PlayerStateSnapshot? staleReference)
        {
            if (!candidate.HasCoherentState)
                return true;

            return IsNearDifferentMapReference(candidate, transitionSource) ||
                   IsNearDifferentMapReference(candidate, staleReference);
        }

        private bool IsNearDifferentMapReference(PlayerStateSnapshot candidate, PlayerStateSnapshot? reference)
            => reference is { HasCoherentState: true } snapshot &&
               snapshot.Map != candidate.Map &&
               IsWithinStaleNeighbourhood(candidate, snapshot);

        private PositionSyncResult ReadyResult(
            PlayerStateSnapshot state,
            long transitionId,
            string message,
            DestinationValidationContext? destination,
            DestinationValidationStatus status)
        {
            _log($"[PositionSync] Position ready: {state.Describe()} TransitionId={transitionId} DestinationStatus={status}{(destination != null ? $" Destination={destination.Describe()}" : string.Empty)}");
            return new PositionSyncResult(PositionSyncResultKind.Ready, state, transitionId, message)
            {
                DestinationStatus = status,
                Destination = destination
            };
        }

        private PositionSyncResult DestinationMismatchResult(
            PlayerStateSnapshot state,
            long transitionId,
            DestinationValidationContext destination,
            string message)
        {
            _log($"[PositionSync] Destination mismatch confirmed (live position, not stale): {state.Describe()} TransitionId={transitionId} DestinationStatus={DestinationValidationStatus.ConfirmedMismatch} Destination={destination.Describe()} OffsetX={destination.OffsetX(state)} OffsetY={destination.OffsetY(state)} Message={message}");
            return new PositionSyncResult(PositionSyncResultKind.DestinationMismatch, state, transitionId, message)
            {
                DestinationStatus = DestinationValidationStatus.ConfirmedMismatch,
                Destination = destination
            };
        }

        private void BeginTransitionCore(
            string reason,
            PlayerStateSnapshot? source,
            bool preserveStaleReference = false)
        {
            long id;
            string transitionReason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason;

            lock (_lock)
            {
                // If the caller could not capture a coherent source snapshot, the last
                // validated state is the best available pre-transition reference.
                PlayerStateSnapshot? effectiveSource =
                    source is { HasCoherentState: true } ? source : _lastValidatedState;

                _transitionId++;
                id = _transitionId;
                _transitionPending = true;
                _transitionReason = transitionReason;
                _preTransitionState = effectiveSource;

                // A destination-only validation does not move the player, so it must not
                // overwrite the real pre-transition location used to recognise stale data.
                if (!preserveStaleReference)
                    _staleReference = effectiveSource;

                _lastValidatedState = null;
                if (effectiveSource.HasValue)
                    _lastObservedState = effectiveSource;
                _state = PositionSyncState.TransitionPending;
            }

            _log($"[PositionSync] Transition detected: TransitionId={id} Reason={transitionReason} Source={(source.HasValue ? source.Value.Describe() : "unknown")} PreservedStaleReference={preserveStaleReference}.");
        }
    }
}
