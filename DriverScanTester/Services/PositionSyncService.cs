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
        MapNotReached,
        Timeout,
        Unconfirmed,
        Cancelled,
        Superseded
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
    }

    /// <summary>Result of a position synchronization request.</summary>
    public readonly record struct PositionSyncResult(
        PositionSyncResultKind Kind,
        PlayerStateSnapshot State,
        long TransitionId,
        string Message)
    {
        public bool IsReady => Kind == PositionSyncResultKind.Ready;
    }

    /// <summary>
    /// Owns post-transition position validation. A map read is NOT treated as proof
    /// that X/Y are usable: after a transition the game can expose the destination map
    /// while the coordinates still belong to the previous location.
    ///
    /// Freshness is established from observable evidence only:
    ///   • a coherent post-transition snapshot whose coordinates differ from the last
    ///     pre-transition snapshot captured immediately before the transition, or
    ///   • a coordinate change observed after one bounded W refresh press.
    ///
    /// Identical repeated coordinates are never used as proof of freshness. When no
    /// evidence can be established the result is <see cref="PositionSyncResultKind.Unconfirmed"/>
    /// — never a fabricated <see cref="PositionSyncResultKind.Ready"/>.
    /// </summary>
    public sealed class PositionSyncService
    {
        private readonly GameMemoryService _memory;
        private readonly Action<string> _log;
        private readonly Action _focusGameWindow;
        private readonly Func<CancellationToken, Task> _wRefreshAsync;
        private readonly Action<byte, byte, uint> _keyEvent;
        private readonly PositionSyncOptions _options;

        private readonly object _lock = new();
        private readonly SemaphoreSlim _syncGate = new(1, 1);

        private long _transitionId;
        private bool _transitionPending;
        private string _transitionReason = "";
        private PlayerStateSnapshot? _preTransitionState;
        private PlayerStateSnapshot? _lastValidatedState;
        private PlayerStateSnapshot? _lastObservedState;
        private PositionSyncState _state = PositionSyncState.Normal;

        public PositionSyncService(
            GameMemoryService memory,
            Action<string> log,
            Action focusGameWindow,
            Func<CancellationToken, Task>? wRefreshAsync = null,
            PositionSyncOptions? options = null,
            Action<byte, byte, uint>? keyEvent = null)
        {
            _memory = memory ?? throw new ArgumentNullException(nameof(memory));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _focusGameWindow = focusGameWindow ?? throw new ArgumentNullException(nameof(focusGameWindow));
            _wRefreshAsync = wRefreshAsync ?? PressWRefreshAsync;
            _options = options ?? new PositionSyncOptions();
            _keyEvent = keyEvent ?? ((vk, scan, flags) => GameInput.keybd_event(vk, scan, flags, 0));
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
        /// transition this only requires a coherent read (normal navigation is
        /// unaffected). An unmarked map change compared with the last validated map
        /// automatically starts a transition.
        /// </summary>
        public async Task<PositionSyncResult> EnsurePositionReadyAsync(
            string reason,
            int expectedMap,
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
                return await SynchronizeAsync(expectedMap, token);

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
                "Player pointer/map/position read failed.");
        }

        /// <summary>
        /// Validates the position for the current pending transition. Callers must
        /// inspect <see cref="PositionSyncResult.IsReady"/>; a non-ready result means the
        /// workflow must not compare coordinates or perform destructive recovery.
        /// </summary>
        public async Task<PositionSyncResult> SynchronizeAsync(int expectedMap, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            bool pending;
            lock (_lock)
                pending = _transitionPending;

            if (!pending)
            {
                PlayerStateSnapshot snapshot = CaptureCurrentState();
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
                        "No transition pending; coherent player state.");
                }

                return new PositionSyncResult(
                    PositionSyncResultKind.Unconfirmed,
                    snapshot,
                    TransitionId,
                    "No transition pending and player state is unreadable.");
            }

            return await SynchronizeInternalAsync(expectedMap, token);
        }

        /// <summary>
        /// Releases W in a finally block so cancellation during the press can never
        /// leave the movement key held down.
        /// </summary>
        public async Task PressWRefreshAsync(CancellationToken token)
        {
            _focusGameWindow();
            _log($"[PositionSync] Tapping W for {_options.NudgeKeyDownMs} ms...");

            _keyEvent(GameInput.VK_W, GameInput.SCAN_W, 0);
            try
            {
                await Task.Delay(_options.NudgeKeyDownMs, token);
            }
            finally
            {
                _keyEvent(GameInput.VK_W, GameInput.SCAN_W, (uint)GameInput.KEYEVENTF_KEYUP);
                _log("[PositionSync] W refresh completed: KeyReleased=True.");
            }
        }

        private async Task<PositionSyncResult> SynchronizeInternalAsync(int expectedMap, CancellationToken token)
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
                                    "Transition already completed.");
                            }

                            _state = PositionSyncState.PositionSynchronizationFailed;
                            return new PositionSyncResult(
                                PositionSyncResultKind.Unconfirmed,
                                snapshot,
                                _transitionId,
                                "Transition is no longer pending but the player state is unreadable.");
                        }
                    }

                    PositionSyncResult result = await RunTransitionSyncAsync(
                        id,
                        reason,
                        source,
                        expectedMap,
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
                            _state = PositionSyncState.PositionSynchronizationFailed;
                        }
                        else
                        {
                            _state = PositionSyncState.PositionSynchronizationFailed;
                        }
                    }

                    _log($"[PositionSync] Result={result.Kind} TransitionId={id} {result.State.Describe()} Message={result.Message}");
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
            CancellationToken token)
        {
            PlayerStateSnapshot latest = default;
            int stableMapReads = 0;
            int lastStableMap = 0;
            bool mapConfirmed = false;

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

                _log($"[PositionSync] Destination map not confirmed: Expected={expectedMap} {latest.Describe()} Reason={reason}.");
                return new PositionSyncResult(kind, latest, transitionId, message);
            }

            lock (_lock)
            {
                if (transitionId == _transitionId)
                    _state = PositionSyncState.MapConfirmed;
            }

            _log($"[PositionSync] Destination map confirmed: {latest.Map} (TransitionId={transitionId}, Reason={reason}).");
            _log($"[PositionSync] Waiting for post-transition position... {latest.Describe()}");

            // First allow the game itself to finish publishing the new position. This is
            // bounded polling, not a blind fixed sleep: a coordinate change ends it early.
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

                if (HasFreshnessEvidence(latest, source))
                {
                    _log($"[PositionSync] Position freshness established without a W refresh: {latest.Describe()}");
                    return ReadyResult(latest, transitionId, "Coordinates changed after the transition.");
                }

                await Task.Delay(_options.PollMs, token);
            }

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

                if (HasFreshnessEvidence(before, source))
                {
                    _log($"[PositionSync] Position freshness established before a W refresh: {before.Describe()}");
                    return ReadyResult(before, transitionId, "Coordinates changed after the transition.");
                }

                _log($"[PositionSync] W refresh requested: attempt {attempt}/{_options.MaxWRefreshAttempts} {before.Describe()}");

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

                    if (HasFreshnessEvidence(after, source) ||
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

                _log($"[PositionSync] W refresh completed: KeyReleased={released} Validation={(refreshed ? "PositionChanged" : "Unchanged")} PositionBefore={(before.PositionReadSucceeded ? $"({before.X},{before.Y})" : "(unreadable)")} PositionAfter={(after.PositionReadSucceeded ? $"({after.X},{after.Y})" : "(unreadable)")} MapAfter={(after.MapReadSucceeded ? after.Map.ToString() : "unreadable")}");

                if (refreshed)
                {
                    return ReadyResult(after, transitionId, "Coordinates updated by the bounded W refresh.");
                }

                if (attempt < _options.MaxWRefreshAttempts)
                    await Task.Delay(_options.WRefreshRetryDelayMs, token);
            }

            PlayerStateSnapshot finalState = ReadState();
            _log($"[PositionSync] Position could not be proven fresh after {_options.MaxWRefreshAttempts} bounded W refresh attempt(s): {finalState.Describe()}");
            return new PositionSyncResult(
                PositionSyncResultKind.Unconfirmed,
                finalState,
                transitionId,
                "Coordinates could not be proven fresh after the transition.");
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

        private bool HasFreshnessEvidence(PlayerStateSnapshot state, PlayerStateSnapshot? source)
        {
            if (!state.HasCoherentState)
                return false;

            if (source is { } preTransition &&
                preTransition.HasCoherentState &&
                state.PositionChangedFrom(preTransition))
            {
                return true;
            }

            lock (_lock)
            {
                if (_lastValidatedState is { } validated &&
                    validated.HasCoherentState &&
                    state.Map == validated.Map &&
                    state.PositionChangedFrom(validated))
                {
                    return true;
                }
            }

            return false;
        }

        private PositionSyncResult ReadyResult(PlayerStateSnapshot state, long transitionId, string message)
        {
            _log($"[PositionSync] Position ready: {state.Describe()} TransitionId={transitionId}");
            return new PositionSyncResult(PositionSyncResultKind.Ready, state, transitionId, message);
        }

        private void BeginTransitionCore(string reason, PlayerStateSnapshot? source)
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
                _lastValidatedState = null;
                if (effectiveSource.HasValue)
                    _lastObservedState = effectiveSource;
                _state = PositionSyncState.TransitionPending;
            }

            _log($"[PositionSync] Transition detected: TransitionId={id} Reason={transitionReason} Source={(source.HasValue ? source.Value.Describe() : "unknown")}.");
        }
    }
}
