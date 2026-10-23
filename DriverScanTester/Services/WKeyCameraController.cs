using System;
using System.Collections.Generic;
using System.Threading;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Coordinates the vertical camera lock with bot-generated W key ownership.
    /// This controller does not infer physical movement and never changes yaw or zoom.
    /// One instance belongs to one active bot workflow and is shared by its input sources.
    /// </summary>
    public sealed class WKeyCameraController
    {
        private static long _nextGeneration;
        private static readonly object SessionGate = new();
        private static readonly Dictionary<(uint ProcessId, ulong ModuleBase), long> CurrentSessionByGame = new();

        private readonly object _gate = new();
        private readonly GameMemoryService _memory;
        private readonly Action<string> _log;
        private readonly HashSet<WKeyCameraOwner> _wOwners = new();
        private readonly HashSet<WKeyCameraOwner> _mandatoryLootBarrierOwners = new();
        private readonly Dictionary<object, bool> _externalCameraOwners =
            new(ReferenceEqualityComparer.Instance);
        private readonly (uint ProcessId, ulong ModuleBase) _cameraSessionIdentity;
        private readonly long _generation;

        private bool _stationaryRestorePending;
        private bool _closing;
        private bool _ended;

        public short StationaryVerticalLock { get; }
        public short MovementVerticalLock { get; }
        public int ActiveWOwnerCount { get { lock (_gate) return _wOwners.Count; } }

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
            lock (SessionGate)
                CurrentSessionByGame[_cameraSessionIdentity] = _generation;
        }

        /// <summary>Creates an identity token for one W-producing source.</summary>
        public WKeyCameraOwner CreateOwner(string source)
            => new(this, _generation, string.IsNullOrWhiteSpace(source) ? "W input" : source);

        /// <summary>
        /// Applies and verifies the movement lock before recording a new W owner.
        /// Repeated acquisition by the same owner is idempotent; later owners share the
        /// already verified movement view without another camera-memory write.
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

                if (_wOwners.Count == 0 && !TryApplyLock(MovementVerticalLock, "movement"))
                {
                    // A failed/partial movement-view write must never authorize W-down.
                    // Make one bounded attempt to leave the known stationary view in place.
                    TryApplyLock(StationaryVerticalLock, "failed movement setup cleanup");
                    return false;
                }

                _wOwners.Add(owner);
                _stationaryRestorePending = false;
                if (_wOwners.Count == 1)
                    _log($"[WCamera] Movement vertical lock {MovementVerticalLock} active ({owner.Source}).");
                else
                    _log($"[WCamera] W owner added ({owner.Source}); active owners={_wOwners.Count}.");
                return true;
            }
        }

        /// <summary>Mirrors a movement system's mandatory loot barrier to all shared W sources.</summary>
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
        /// Releases one W source after its physical key-up. Only the last active source
        /// restores stationary view. Releasing an old-generation token cannot affect a
        /// controller created for a newer workflow.
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

                if (_externalCameraOwners.Count != 0)
                {
                    _stationaryRestorePending = true;
                    return;
                }

                RestoreStationaryView("last W owner released");
                if (_closing)
                    _ended = true;
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

                if (_externalCameraOwners.ContainsKey(owner))
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

                _externalCameraOwners.Add(owner, true);
                return true;
            }
        }

        /// <summary>Releases a protected camera operation and restores stationary view.</summary>
        public void ReleaseExternalCameraOwner(object owner, bool restoreStationary = true)
        {
            if (owner == null) return;

            lock (_gate)
            {
                if (!_externalCameraOwners.Remove(owner, out bool restoreOnRelease))
                    return;

                if (!IsCurrentGeneration())
                {
                    if (_closing)
                        _ended = true;
                    return;
                }

                _stationaryRestorePending |= restoreStationary || restoreOnRelease;
                if (_externalCameraOwners.Count != 0 || _wOwners.Count != 0)
                    return;

                if (_stationaryRestorePending || _closing)
                    RestoreStationaryView("camera operation released");

                _stationaryRestorePending = false;
                if (_closing)
                    _ended = true;
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
        /// Prevents stale path work from starting a new W press after workflow shutdown.
        /// Active owners are still allowed to release, and the final release restores
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
                _ended = true;
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
        public string Source { get; }

        internal WKeyCameraOwner(WKeyCameraController controller, long generation, string source)
        {
            Controller = controller;
            Generation = generation;
            Source = source;
        }
    }
}
