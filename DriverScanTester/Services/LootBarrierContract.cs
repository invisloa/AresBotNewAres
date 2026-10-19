using System;

namespace DriverScanTester.Services
{
    /// <summary>Why movement armed a loot obligation.</summary>
    public enum LootBarrierReason
    {
        /// <summary>A real combat target was killed; drops must be checked before walking on.</summary>
        PostKill,

        /// <summary>
        /// The passive moving pre-scan found plausible loot pixels. Movement stops only
        /// long enough to verify/collect those candidates with the proper loot camera.
        /// </summary>
        TravelCandidateVerification
    }

    /// <summary>
    /// Lifecycle of one mandatory loot request. Only <see cref="Completed"/> (a verified
    /// empty pass) or, for a travel candidate, <see cref="Unavailable"/> (verification
    /// could not run) releases waypoint navigation. A mandatory post-kill obligation
    /// never treats <see cref="Unavailable"/> as success.
    /// </summary>
    public enum LootRequestState
    {
        /// <summary>Request accepted, not yet executing a fresh loot cycle.</summary>
        Pending,

        /// <summary>A fresh area-loot / verified pixel-scan cycle is executing.</summary>
        Running,

        /// <summary>Combat interrupted the operation; the obligation remains pending.</summary>
        Deferred,

        /// <summary>A complete verified scan pass finished with no items.</summary>
        Completed,

        /// <summary>
        /// A travel-candidate verification could not run (capture/camera unavailable).
        /// This is NOT an empty result; navigation may resume, but the candidate is not
        /// reported as absent. Mandatory post-kill requests never use this state.
        /// </summary>
        Unavailable,

        /// <summary>Bounded recovery failed; navigation must stay blocked.</summary>
        Failed,

        /// <summary>Explicitly cancelled (stop/city/mode change); never success.</summary>
        Cancelled
    }

    /// <summary>
    /// Immutable snapshot of a mandatory loot request. <see cref="LootSystem"/> is the
    /// only writer; movement reads snapshots and never keeps a second mutable truth.
    /// </summary>
    public readonly record struct LootRequestStatus(
        long RequestId,
        LootBarrierReason Reason,
        LootRequestState State,
        bool CameraVerified,
        int ItemsCollected,
        string Detail,
        DateTime UpdatedAtUtc);

    internal enum LootBarrierPhase
    {
        Idle,
        Pending,
        Running,
        Deferred,
        Completed,
        Unavailable,
        Failed,
        Cancelled
    }

    /// <summary>
    /// Navigation-owned barrier for the mandatory MoveAndAttackAndLoot loot operation.
    ///
    /// The barrier stores the single request generation movement is waiting for and
    /// applies authoritative status snapshots supplied by <see cref="LootSystem"/>.
    /// "Loot is inactive" is never interpreted as success: only an explicit
    /// <see cref="LootRequestState.Completed"/> with camera verification releases a
    /// post-kill obligation, and only for the exact request id that was armed. A travel
    /// candidate may also be released as <see cref="LootRequestState.Unavailable"/> when
    /// verification cannot run; that state is never reported as an empty scan.
    ///
    /// All fields are guarded by one short-held lock; no lock is held across a wait.
    /// </summary>
    internal sealed class LootNavigationBarrier
    {
        private readonly object _gate = new();

        private long _requestId;
        private LootBarrierReason _reason;
        private LootBarrierPhase _phase = LootBarrierPhase.Idle;
        private bool _cameraVerified;
        private int _itemsCollected;
        private string _detail = string.Empty;
        private DateTime _updatedAtUtc = DateTime.MinValue;

        public long RequestId
        {
            get { lock (_gate) return _requestId; }
        }

        public LootBarrierReason Reason
        {
            get { lock (_gate) return _reason; }
        }

        public LootBarrierPhase Phase
        {
            get { lock (_gate) return _phase; }
        }

        public bool CameraVerified
        {
            get { lock (_gate) return _cameraVerified; }
        }

        public int ItemsCollected
        {
            get { lock (_gate) return _itemsCollected; }
        }

        public string Detail
        {
            get { lock (_gate) return _detail; }
        }

        public DateTime UpdatedAtUtc
        {
            get { lock (_gate) return _updatedAtUtc; }
        }

        public bool IsActive
        {
            get
            {
                lock (_gate)
                {
                    return _phase == LootBarrierPhase.Pending ||
                           _phase == LootBarrierPhase.Running ||
                           _phase == LootBarrierPhase.Deferred;
                }
            }
        }

        public bool HasTerminalFailure
        {
            get
            {
                lock (_gate)
                {
                    return _phase == LootBarrierPhase.Failed ||
                           _phase == LootBarrierPhase.Cancelled;
                }
            }
        }

        public bool IsBlockingNavigation => IsActive || HasTerminalFailure;

        public bool TryGetRequestId(out long requestId)
        {
            lock (_gate)
            {
                requestId = _requestId;
                return requestId > 0;
            }
        }

        /// <summary>
        /// Binds the barrier to a request generation. Re-arming with the same active
        /// request only updates its reason; it never invents a duplicate obligation.
        /// </summary>
        public void Arm(long requestId, LootBarrierReason reason)
        {
            if (requestId <= 0)
                return;

            lock (_gate)
            {
                if (_requestId == requestId && IsActiveLocked())
                {
                    // A real post-kill obligation is the stronger reason; never let a
                    // travel-candidate pass downgrade it.
                    if (reason == LootBarrierReason.PostKill)
                        _reason = reason;
                    _updatedAtUtc = DateTime.UtcNow;
                    return;
                }

                _requestId = requestId;
                _reason = reason;
                _phase = LootBarrierPhase.Pending;
                _cameraVerified = false;
                _itemsCollected = 0;
                _detail = "request armed";
                _updatedAtUtc = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Applies a status only when it belongs to the armed request. Stale completion
        /// from an older generation is ignored.
        /// </summary>
        public LootBarrierPhase ApplyStatus(in LootRequestStatus status)
        {
            lock (_gate)
            {
                if (_requestId <= 0 || status.RequestId != _requestId)
                    return _phase;

                _cameraVerified = status.CameraVerified;
                _itemsCollected = status.ItemsCollected;
                _detail = status.Detail ?? string.Empty;
                _updatedAtUtc = status.UpdatedAtUtc == DateTime.MinValue
                    ? DateTime.UtcNow
                    : status.UpdatedAtUtc;

                switch (status.State)
                {
                    case LootRequestState.Pending:
                        _phase = LootBarrierPhase.Pending;
                        break;
                    case LootRequestState.Running:
                        _phase = LootBarrierPhase.Running;
                        break;
                    case LootRequestState.Deferred:
                        _phase = LootBarrierPhase.Deferred;
                        break;
                    case LootRequestState.Completed:
                        if (!status.CameraVerified)
                        {
                            // A completion without verified camera/scan state is a
                            // contract violation, never a navigable success.
                            _phase = LootBarrierPhase.Failed;
                            _detail = $"completed request {status.RequestId} without verified loot camera";
                        }
                        else
                        {
                            _phase = LootBarrierPhase.Completed;
                        }
                        break;
                    case LootRequestState.Unavailable:
                        if (_reason == LootBarrierReason.PostKill)
                        {
                            // Capture/camera failure is not an empty post-kill result.
                            _phase = LootBarrierPhase.Failed;
                            _detail = $"post-kill request {status.RequestId} unavailable: {_detail}";
                        }
                        else
                        {
                            _phase = LootBarrierPhase.Unavailable;
                        }
                        break;
                    case LootRequestState.Failed:
                        _phase = LootBarrierPhase.Failed;
                        break;
                    case LootRequestState.Cancelled:
                        _phase = LootBarrierPhase.Cancelled;
                        break;
                }

                return _phase;
            }
        }

        /// <summary>Explicitly abandons the obligation (mode change / host cancellation).</summary>
        public void Abandon(string detail)
        {
            lock (_gate)
            {
                _phase = LootBarrierPhase.Idle;
                _detail = detail ?? string.Empty;
                _updatedAtUtc = DateTime.UtcNow;
            }
        }

        /// <summary>Terminal failure: navigation stays blocked until external intervention.</summary>
        public void Fail(string detail)
        {
            lock (_gate)
            {
                _phase = LootBarrierPhase.Failed;
                _detail = detail ?? "loot barrier failure";
                _updatedAtUtc = DateTime.UtcNow;
            }
        }

        private bool IsActiveLocked()
            => _phase == LootBarrierPhase.Pending ||
               _phase == LootBarrierPhase.Running ||
               _phase == LootBarrierPhase.Deferred;
    }
}
