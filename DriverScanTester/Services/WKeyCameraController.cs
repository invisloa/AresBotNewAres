using System;
using System.Collections.Generic;
using System.Threading;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Coordinates the vertical camera lock and the physical W key with bot-generated
    /// W ownership. This controller does not infer physical movement and never changes
    /// yaw or zoom. One instance belongs to one active bot workflow and is shared by
    /// all input sources that belong to that workflow.
    /// </summary>
    public sealed class WKeyCameraController : IDisposable
    {
        private static long _nextGeneration;
        private static readonly object SessionGate = new();
        private static readonly Dictionary<(uint ProcessId, ulong ModuleBase), long> CurrentSessionByGame = new();

        private readonly object _gate = new();
        private readonly GameMemoryService _memory;
        private readonly Action<string> _log;
        private readonly HashSet<WKeyCameraOwner> _wOwners = new();
        private readonly HashSet<WKeyCameraOwner> _mandatoryLootBarrierOwners = new();
        private readonly HashSet<object> _externalCameraOwners =
            new(ReferenceEqualityComparer.Instance);
        private readonly (uint ProcessId, ulong ModuleBase) _cameraSessionIdentity;
        private readonly long _generation;

        private bool _stationaryRestorePending;
        private bool _closing;
        private bool _ended;

        public short StationaryVerticalLock { get; }
        public short MovementVerticalLock { get; }
        public int ActiveWOwnerCount { get { lock (_gate) return _wOwners.Count; } }

        /// <summary>True once this controller's session has fully terminated.</summary>
        public bool IsEnded { get { lock (_gate) return _ended; } }

        /// <summary>
        /// True while this controller may still accept new W or external-camera work.
        /// A closing session rejects new work but may still be releasing old owners.
        /// </summary>
        public bool IsAcceptingOperations
        {
            get
            {
                lock (_gate)
                    return !_closing && !_ended && IsCurrentGeneration();
            }
        }

        public WKeyCameraController(
            GameMemoryService memory,
            Action<string> log,
            short stationaryVerticalLock = BotConstants.Camera.StationaryVerticalLock,
            short movementVerticalLock = BotConstants.Camera.MovementVerticalLock)
        {
            _memory = memory ?? throw new ArgumentNullException(nameof(memory));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            StationaryVerticalLock = stationaryVerticalLock;
            MovementVerticalLock = movementVerticalLock;
            _cameraSessionIdentity = memory.CameraSessionIdentity;
            _generation = Interlocked.Increment(ref _nextGeneration);

            // A new controller may only become current when no other controller is
            // still active for this game process. Silently replacing an active session
            // would let a caller invalidate a running workflow's camera ownership.
            lock (SessionGate)
            {
                if (CurrentSessionByGame.TryGetValue(_cameraSessionIdentity, out long activeGeneration))
                {
                    throw new InvalidOperationException(
                        $"A W-camera session (generation {activeGeneration}) is already active for this game process. " +
                        "Stop the running bot/workflow and wait for its input cleanup before starting another camera-owning operation.");
                }

                CurrentSessionByGame[_cameraSessionIdentity] = _generation;
            }

            if (!TryInitializeStationaryView())
                _log($"[WCamera] Could not establish and verify stationary vertical lock {StationaryVerticalLock} at session start.");
        }

        /// <summary>Creates an identity token for one W-producing source.</summary>
        public WKeyCameraOwner CreateOwner(string source, Action<bool>? sendPhysicalW = null)
            => new(
                this,
                _generation,
                string.IsNullOrWhiteSpace(source) ? "W input" : source,
                sendPhysicalW ?? (keyUp => GameInput.keybd_event(
                    GameInput.VK_W,
                    GameInput.SCAN_W,
                    keyUp ? (uint)GameInput.KEYEVENTF_KEYUP : 0u,
                    0)));

        /// <summary>
        /// The first owner applies and verifies the movement lock and then presses W.
        /// Later owners share the already verified movement view and the existing
        /// physical W-down. Repeated acquisition by the same owner is idempotent.
        /// </summary>
        public bool TryAcquireW(WKeyCameraOwner owner, bool authorizedDuringLootBarrier = false)
        {
            lock (_gate)
            {
                if (!IsCurrentOwner(owner) || !IsCurrentGeneration() || _closing || _ended)
                    return false;

                if (_mandatoryLootBarrierOwners.Count != 0 &&
                    (!authorizedDuringLootBarrier || !_mandatoryLootBarrierOwners.Contains(owner)))
                {
                    _log($"[WCamera] W request from {owner.Source} blocked by a mandatory loot navigation barrier.");
                    return false;
                }

                if (_wOwners.Contains(owner))
                    return true;

                if (_externalCameraOwners.Count != 0)
                {
                    _log($"[WCamera] W request from {owner.Source} blocked while another operation owns the camera.");
                    return false;
                }

                bool firstOwner = _wOwners.Count == 0;
                if (firstOwner)
                {
                    if (!TryApplyLock(MovementVerticalLock, "movement"))
                    {
                        // A failed/partial movement-view write must never authorize W-down.
                        // Make one bounded attempt to leave the known stationary view in place.
                        TryApplyLock(StationaryVerticalLock, "failed movement setup cleanup");
                        return false;
                    }

                    if (!TrySendPhysicalW(owner, keyUp: false, "first W-down"))
                    {
                        // Do not register logical ownership when the physical key-down
                        // was not delivered. Clean up any partial physical state.
                        TrySendPhysicalW(owner, keyUp: true, "failed W-down cleanup");
                        TryApplyLock(StationaryVerticalLock, "failed W-down cleanup");
                        return false;
                    }
                }

                _wOwners.Add(owner);
                _stationaryRestorePending = false;
                if (firstOwner)
                    _log($"[WCamera] Movement vertical lock {MovementVerticalLock} active ({owner.Source}).");
                else
                    _log($"[WCamera] W owner added ({owner.Source}); active owners={_wOwners.Count}.");
                return true;
            }
        }

        /// <summary>
        /// Mirrors a movement system's mandatory loot barrier to all shared W sources.
        /// Registration is identity-scoped and idempotent, so a terminating movement
        /// instance can clear only its own barrier.
        /// </summary>
        public void SetMandatoryLootNavigationBlock(WKeyCameraOwner owner, bool blocked)
        {
            lock (_gate)
            {
                if (!IsCurrentOwner(owner))
                    return;

                if (blocked)
                    _mandatoryLootBarrierOwners.Add(owner);
                else
                    _mandatoryLootBarrierOwners.Remove(owner);
            }
        }

        /// <summary>
        /// Restarts the physical W key for ForceStartMoving. The movement camera stays
        /// active across the release gap. With another W owner present the restart is
        /// suppressed because a W-up would interrupt that owner's movement.
        /// </summary>
        /// <returns>
        /// True when this owner still has valid logical/physical forward ownership after
        /// the operation; false when ownership was released because W-down failed.
        /// </returns>
        public bool TryRestartW(WKeyCameraOwner owner, int releaseGapMs)
        {
            lock (_gate)
            {
                if (!IsCurrentOwner(owner) || !IsCurrentGeneration() || _closing || _ended)
                    return false;

                if (!_wOwners.Contains(owner))
                    return false;

                if (_wOwners.Count != 1)
                {
                    _log($"[WCamera] ForceStartMoving restart suppressed for {owner.Source}: " +
                         $"{_wOwners.Count} W owners are active; no W-up was sent.");
                    return true;
                }

                if (!TrySendPhysicalW(owner, keyUp: true, "ForceStartMoving W-up"))
                {
                    // The key may still be physically down and this owner still owns it.
                    // Do not send a second W-down and do not pretend the restart happened.
                    _log("[WCamera] ForceStartMoving restart aborted because W-up did not succeed; existing ownership preserved.");
                    return true;
                }

                if (releaseGapMs > 0)
                    Thread.Sleep(releaseGapMs);

                if (!TrySendPhysicalW(owner, keyUp: false, "ForceStartMoving W-down"))
                {
                    _wOwners.Remove(owner);
                    TrySendPhysicalW(owner, keyUp: true, "failed ForceStartMoving W-down cleanup");
                    if (_externalCameraOwners.Count == 0)
                        RestoreStationaryView("ForceStartMoving W-down failed");
                    else
                        _stationaryRestorePending = true;
                    FinishClosingIfNeeded();
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// Releases one W source and its physical key hold. A non-final owner release
        /// leaves both W and the movement camera untouched; the final release sends
        /// W-up before restoring stationary view. Releasing an old-generation token
        /// cannot affect a controller created for a newer workflow.
        /// </summary>
        public void ReleaseW(WKeyCameraOwner owner)
        {
            lock (_gate)
            {
                if (!IsCurrentOwner(owner) || !_wOwners.Remove(owner))
                    return;

                if (!IsCurrentGeneration())
                    return;

                if (_wOwners.Count != 0)
                {
                    _log($"[WCamera] W owner released ({owner.Source}); active owners={_wOwners.Count}.");
                    return;
                }

                // The physical key must be released before stationary camera restoration.
                TrySendPhysicalW(owner, keyUp: true, "last W owner release");

                if (_externalCameraOwners.Count != 0)
                {
                    _stationaryRestorePending = true;
                    return;
                }

                RestoreStationaryView("last W owner released");
                _stationaryRestorePending = false;
                FinishClosingIfNeeded();
            }
        }

        /// <summary>
        /// Claims the same camera-ownership gate used by the vertical-lock controller
        /// for a protected loot/seller camera operation. It fails while W is held, so
        /// a scan cannot race a movement-lock write and a scan-owned view cannot be
        /// interrupted by a new W request.
        /// </summary>
        public bool TryAcquireExternalCameraOwner(object owner)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));

            lock (_gate)
            {
                if (!IsCurrentGeneration() || _closing || _ended)
                    return false;

                if (_externalCameraOwners.Contains(owner))
                    return true;

                if (_wOwners.Count != 0)
                {
                    _log("[WCamera] Camera operation blocked while a bot W owner is active.");
                    return false;
                }

                if (_externalCameraOwners.Count != 0)
                {
                    _log("[WCamera] Camera operation blocked while another protected operation owns the camera.");
                    return false;
                }

                _externalCameraOwners.Add(owner);
                return true;
            }
        }

        /// <summary>
        /// Releases a protected camera operation. <paramref name="restoreStationary"/>
        /// is authoritative: true requests stationary restoration after the operation
        /// ends, false suppresses restoration for operations that restore their own
        /// captured camera values. A session shutdown may still restore separately.
        /// </summary>
        public void ReleaseExternalCameraOwner(object owner, bool restoreStationary = true)
        {
            if (owner == null) return;

            lock (_gate)
            {
                if (!_externalCameraOwners.Remove(owner))
                    return;

                if (!IsCurrentGeneration())
                {
                    if (_closing)
                    {
                        _ended = true;
                        UnregisterSession();
                    }
                    return;
                }

                bool shouldRestore = restoreStationary || _stationaryRestorePending || _closing;
                if (_externalCameraOwners.Count != 0 || _wOwners.Count != 0)
                {
                    if (shouldRestore)
                        _stationaryRestorePending = true;
                    return;
                }

                if (shouldRestore)
                    RestoreStationaryView("camera operation released");

                _stationaryRestorePending = false;
                FinishClosingIfNeeded();
            }
        }

        /// <summary>
        /// Applies stationary view for an idle camera operation only when no W or
        /// protected camera owner can be interrupted.
        /// </summary>
        public bool TrySetStationaryWhenIdle(string reason)
        {
            lock (_gate)
            {
                if (!IsCurrentGeneration() || _closing || _ended || _wOwners.Count != 0 || _externalCameraOwners.Count != 0)
                    return false;

                return TryApplyLock(StationaryVerticalLock, reason ?? "idle camera setup");
            }
        }

        /// <summary>
        /// Establishes the stationary view at the beginning of a valid session, before
        /// initial targeting or scanning. It never changes yaw or camera distance.
        /// </summary>
        public bool TryInitializeStationaryView()
        {
            lock (_gate)
            {
                if (!IsCurrentGeneration() || _closing || _ended || _wOwners.Count != 0 || _externalCameraOwners.Count != 0)
                    return false;

                return TryApplyLock(StationaryVerticalLock, "session start");
            }
        }

        /// <summary>
        /// Prevents stale path work from starting a new W press after workflow shutdown.
        /// Active owners are still allowed to release; the final release restores
        /// stationary view once protected camera work has also released ownership.
        /// </summary>
        public void EndSession()
        {
            lock (_gate)
            {
                if (_ended)
                    return;

                _closing = true;
                if (!IsCurrentGeneration())
                {
                    _ended = true;
                    return;
                }
                if (_wOwners.Count != 0 || _externalCameraOwners.Count != 0)
                    return;

                RestoreStationaryView("workflow ended");
                _stationaryRestorePending = false;
                _ended = true;
                UnregisterSession();
            }
        }

        /// <summary>Ends the controller session when disposed (idempotent).</summary>
        public void Dispose() => EndSession();

        /// <summary>
        /// True when another controller currently owns the camera session for the
        /// supplied game process/module pair.
        /// </summary>
        public static bool HasActiveSession(uint processId, ulong moduleBase)
        {
            lock (SessionGate)
                return CurrentSessionByGame.ContainsKey((processId, moduleBase));
        }

        private bool TrySendPhysicalW(WKeyCameraOwner owner, bool keyUp, string reason)
        {
            try
            {
                owner.SendPhysicalW(keyUp);
                return true;
            }
            catch (Exception ex)
            {
                _log($"[WCamera] Physical W-{(keyUp ? "up" : "down")} failed ({reason}, {owner.Source}): {ex.Message}");
                return false;
            }
        }

        private void FinishClosingIfNeeded()
        {
            if (!_closing || _ended)
                return;

            _ended = true;
            UnregisterSession();
        }

        private void UnregisterSession()
        {
            lock (SessionGate)
            {
                if (CurrentSessionByGame.TryGetValue(_cameraSessionIdentity, out long current) &&
                    current == _generation)
                {
                    CurrentSessionByGame.Remove(_cameraSessionIdentity);
                }
            }
        }

        private bool IsCurrentOwner(WKeyCameraOwner owner)
            => owner != null && ReferenceEquals(owner.Controller, this) && owner.Generation == _generation;

        private bool IsCurrentGeneration()
        {
            lock (SessionGate)
                return CurrentSessionByGame.TryGetValue(_cameraSessionIdentity, out long current) &&
                       current == _generation;
        }

        private void RestoreStationaryView(string reason)
        {
            if (!TryApplyLock(StationaryVerticalLock, reason))
                _log($"[WCamera] Could not verify stationary vertical lock {StationaryVerticalLock} ({reason}).");
        }

        private bool TryApplyLock(short value, string reason)
        {
            if (!_memory.TryGetCameraVerticalLock(out short current))
            {
                _log($"[WCamera] Camera vertical lock unreadable; cannot apply {value} ({reason}).");
                return false;
            }

            if (current == value)
                return true;

            if (!_memory.TrySetCameraVerticalLock(value) ||
                !_memory.TryGetCameraVerticalLock(out short verified) || verified != value)
            {
                _log($"[WCamera] Camera vertical lock write did not verify as {value} ({reason}).");
                return false;
            }

            return true;
        }
    }

    /// <summary>Identity-based W ownership token scoped to one controller generation.</summary>
    public sealed class WKeyCameraOwner
    {
        internal WKeyCameraController Controller { get; }
        internal long Generation { get; }
        internal Action<bool> SendPhysicalW { get; }
        public string Source { get; }

        internal WKeyCameraOwner(
            WKeyCameraController controller,
            long generation,
            string source,
            Action<bool> sendPhysicalW)
        {
            Controller = controller;
            Generation = generation;
            Source = source;
            SendPhysicalW = sendPhysicalW;
        }
    }
}
