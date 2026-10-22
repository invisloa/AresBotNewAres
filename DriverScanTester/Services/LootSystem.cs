using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;
using DriverScanTester.Utils;

namespace DriverScanTester.Services
{
    public class LootSystem : IDisposable
    {
        internal enum LootScanPassResult
        {
            Collected,
            NoItem,
            CaptureFailed,
            CameraVerificationFailed,
            InvalidGeometry,
            Incomplete,
            Cancelled
        }
        private enum LootProbeResult { Miss, Collected, CaptureInvalid, Cancelled }

        /// <summary>
        /// Outcome of one normal white-loot pass. The result preserves the distinction
        /// between a verified empty pass, a collected item, a capture failure and a
        /// cancelled/invalid scan so failures can never be reported as "no items".
        /// </summary>
        internal readonly record struct LootScanPassOutcome(
            LootScanPassResult Result,
            LootScanPassResult SmallScan,
            LootScanPassResult? BigScan,
            bool CameraVerified,
            string Detail)
        {
            /// <summary>Only this state may complete a mandatory loot request.</summary>
            public bool IsValidComplete =>
                Result == LootScanPassResult.NoItem &&
                SmallScan == LootScanPassResult.NoItem &&
                BigScan == LootScanPassResult.NoItem &&
                CameraVerified;
        }

        private sealed class MandatoryLootRequest
        {
            public long Id { get; init; }
            public LootBarrierReason Reason { get; set; }
            public LootRequestState State { get; set; }
            public bool CameraVerified { get; set; }
            public int ItemsCollected { get; set; }
            public string Detail { get; set; } = string.Empty;
            public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
            public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
            public bool NeedsFreshCycle { get; set; } = true;
            public int CaptureAttempts { get; set; }
            public int CameraAttempts { get; set; }

            /// <summary>
            /// True once MovementSystem has released W/steering for this request. The
            /// stationary cycle must never move the camera before this acknowledgement
            /// (or the bounded grace fallback) to avoid zooming while the player walks.
            /// </summary>
            public bool NavigationHeld { get; set; }

            public LootRequestStatus Snapshot() => new(
                Id, Reason, State, CameraVerified, ItemsCollected,
                Detail ?? string.Empty, UpdatedAtUtc);
        }

        /// <summary>Result of one lightweight passive pre-scan of the current game view.</summary>
        internal enum LootPreScanOutcome
        {
            /// <summary>Nothing loot-like was visible; navigation must continue.</summary>
            NoCandidate,

            /// <summary>Plausible white-loot pixels were found; verify with the loot camera.</summary>
            PotentialCandidate,

            /// <summary>The frame could not be captured/analyzed; never treated as a candidate.</summary>
            CaptureUnavailable
        }

        /// <summary>
        /// Candidate evidence from the passive pre-scan. Bounds are bitmap-local screen
        /// coordinates and are used only for duplicate suppression of repeated false
        /// positives; they are not a definitive loot result.
        /// </summary>
        internal readonly record struct LootPreScanResult(
            LootPreScanOutcome Outcome,
            int CandidateComponents,
            int WhitePixels,
            Rectangle CandidateBounds,
            string Detail);

        /// <summary>Loot operation exercised by the Tools tab diagnostics.</summary>
        public enum LootDiagnosticMode { SodSopPink, NormalWhite }

        /// <summary>Outcome of one manual diagnostic loot run.</summary>
        public enum LootDiagnosticOutcome
        {
            Collected,
            NoItem,
            CaptureFailed,
            Cancelled,
            NotAttached,
            NotFocused,
            InCity,
            Busy,
            Error
        }

        /// <summary>
        /// Result of a manual diagnostic loot run started from the Tools tab.
        /// <see cref="ItemsCollected"/> is the number of mouseover-confirmed pickups;
        /// <see cref="Passes"/> is the number of scan passes performed.
        /// </summary>
        public readonly record struct LootDiagnosticResult(
            LootDiagnosticMode Mode,
            LootDiagnosticOutcome Outcome,
            int ItemsCollected,
            int Passes,
            string Detail)
        {
            /// <summary>True when the scan itself completed (even if no item was found).</summary>
            public bool ScanCompleted =>
                Outcome is LootDiagnosticOutcome.Collected or LootDiagnosticOutcome.NoItem;
        }

        internal readonly record struct PixelCollectionStats(
            int PixelsExamined,
            int RawPinkPixels,
            int MarkerPixelsRejected,
            int MaskPixels);

        internal sealed record PinkComponentSummary(
            Rectangle Bounds,
            int PixelCount,
            float FillRatio,
            bool IsLootCandidate,
            IReadOnlyList<Point> ProbePoints);

        internal sealed class PinkFrameAnalysisResult
        {
            internal int Width { get; init; }
            internal int Height { get; init; }
            internal int TotalPixelsExamined { get; init; }
            internal int RawPinkPixels { get; init; }
            internal int MarkerPixelsRejected { get; init; }
            internal int PinkPixels { get; init; }
            internal int Components { get; init; }
            internal int LootCandidates { get; init; }
            internal int SizeOrShapeRejectedComponents { get; init; }
            internal int MobMarkerRings { get; init; }
            internal int TinyComponents { get; init; }
            internal int FinalProbeCount { get; init; }
            internal IReadOnlyList<PinkComponentSummary> CandidateDetails { get; init; } = Array.Empty<PinkComponentSummary>();
            internal IReadOnlyList<WhiteComponent> CandidateComponents { get; init; } = Array.Empty<WhiteComponent>();
            internal IReadOnlyList<WhiteComponent> AllComponents { get; init; } = Array.Empty<WhiteComponent>();
        }

        private const int MaxDiagnosticPinkComponentDetails = 8;
        private readonly GameMemoryService _memoryService;
        private readonly Action<string> _log;

        // Pixel Scan Constants
        private static readonly int[] smallX = BotConstants.Loot.SmallScanX;
        private static readonly int[] smallY = BotConstants.Loot.SmallScanY;
        private static readonly int[] bigX = BotConstants.Loot.BigScanX;
        private static readonly int[] bigY = BotConstants.Loot.BigScanY;

        // Character exclude zone
        private const int ExcludeXMin = BotConstants.Loot.ExcludeXMin;
        private const int ExcludeXMax = BotConstants.Loot.ExcludeXMax;
        private const int ExcludeYMin = BotConstants.Loot.ExcludeYMin;
        private const int ExcludeYMax = BotConstants.Loot.ExcludeYMax;

        private Bitmap _bitmap;
        private Graphics _graphics;
        private readonly object _captureGate = new();
        private readonly object _cameraCursorOwnershipGate = new();
        private StableLootCaptureResult _lastCaptureResult;
        private LootClientGeometry? _lastStableCaptureGeometry;
        private string _lastLoggedCaptureFailure = string.Empty;
        private DateTime _lastCaptureFailureLogAt = DateTime.MinValue;
        private bool _focusCaptureSkipLogged;

        // ── Scan-phase spacebar spam ──
        // A background task spams spacebar while the pixel scan is running,
        // collecting items under the character (the exclude zone) that the
        // pixel scan skips.
        private CancellationTokenSource? _scanSpacebarCts;

        // ── Loot state machine ──
        // Phases:
        //   PostMobTab  → after mob death, press TAB to check for more mobs
        //   AreaLoot    → press spacebar x3, snapshot inventory, check after 100ms
        //   AreaLootWait→ compare inventory snapshot; if changed → keep spacebar-looting;
        //                  if no change after several tries → switch to Scan
        //   Scan        → pixel-scan: label shaded-white blobs and probe only
        //                  sufficiently large, concentrated loot-like components
        //   PinkScan   → SOD/SOP-only mode (MoveAndAttack waypoints): after the close-mob
        //                  check, sweep the WHOLE captured client bitmap for PINK pixels
        //                  in ONE continuous mouse pass over every candidate pixel,
        //                  found, and repeat a fresh sweep after each successful pickup.
        //                  When a confirmed SOD/SOP pickup cannot fit (bag at the weight
        //                  limit) the player first drinks the potion type it has more of
        //                  (HP/mana) — one potion frees enough weight for one 1-lb scroll.
        //   ScanComplete→ a full scan pass found no items; hold briefly so the movement
        //                  system can walk away, then restart the cycle from Idle
        private enum LootMachineState { Idle, PostMobTab, AreaLoot, AreaLootWait, Scan, PinkScan, ScanComplete }
        private volatile LootMachineState _lootState = LootMachineState.Idle;
        private DateTime _nextActionTime = DateTime.MinValue;
        private int _inventoryChecksumBefore;
        private int _consecutiveEmptySpacePresses;
        private int _consecutivePinkCaptureFailures;
        private const int MaxEmptySpacePressesBeforeScan = 3;
        private const int MaxImmediatePinkCaptureFailures = 3;

        /// <summary>Until this time the ScanComplete state holds (no area-loot/scan).</summary>
        private DateTime _scanCompleteUntil = DateTime.MinValue;

        /// <summary>Last time an item was actually collected (spacebar pickup or pixel-scan click).</summary>
        private DateTime _lastItemCollectedAt = DateTime.MinValue;

        // ── SOD/SOP pink-scan request (MoveAndAttack post-kill) ──
        // The movement system requests the immediate pink scan only after its close-camera
        // TAB check finds no nearby mob. The independent generation scheduler still arms
        // delayed background work for every kill, including kills followed by a close retarget.
        private CancellationToken _scanToken;

        /// <summary>0 = idle, 1 = a manual diagnostic scan owns the capture and cursor.</summary>
        private int _diagnosticScanActive;

        /// <summary>Maximum number of pickup passes in one manual diagnostic run.</summary>
        private const int MaxDiagnosticPickupPasses = 10;

        /// <summary>Delay after the diagnostic camera zoom before the first capture.</summary>
        private const int DiagnosticCameraSettleMs = 250;

        /// <summary>
        /// Reusable raw-pixel buffer for the bulk LockBits region scan (Phase A).
        /// Kept between scans so the captured frame is read in one copy instead of a
        /// <see cref="Bitmap.GetPixel"/> call per pixel.
        /// </summary>
        private byte[] _scanBuffer = Array.Empty<byte>();

        // ── White-loot probe continuation (bounded multi-pass coverage) ──
        // Coordinates already probed during an Incomplete pass. They are skipped only
        // while the screen/candidate key is unchanged; the key is reset whenever the
        // capture geometry or the accepted component geometry changes.
        private readonly HashSet<Point> _probedWhiteProbeProgress = new HashSet<Point>();
        private string _whiteProbeProgressKey = string.Empty;
        private const int OtherPlayerMarkerRequiredChecks = 3;
        private readonly PlayerMarkerPixelDetector _playerMarkerPixelDetector = new();
        private bool _otherPlayerMarkerWasVisible;
        private bool _playerMarkerConfirmationPending;
        private int _playerMarkerChecksConfirmed;
        private DateTime _playerMarkerCandidateDetectedAt = DateTime.MinValue;
        private bool _playerMarkerScanEnabled;
        private volatile bool _pinkScanRequested;
        private DateTime _pinkScanRequestedAt = DateTime.MinValue;
        private readonly PinkRescanScheduler _pinkRescanScheduler = new();
        private bool _pinkScanOwnsCameraCursor;
        private short? _pinkScanRestoreDistance;

        /// <summary>True while a background rescan pass owns the camera and cursor.</summary>
        private volatile bool _pinkRescanActive;
        private volatile bool _pinkLootOnlyMode;

        // ── Mandatory loot requests (MoveAndAttackAndLoot navigation barrier) ──
        // The movement system owns the navigation obligation; these requests are the
        // authoritative lifecycle. Only a verified complete empty scan transitions a
        // request to Completed. "Loot cycle inactive" is never treated as success.
        private long _nextMandatoryLootRequestId;
        private MandatoryLootRequest? _activeMandatoryLootRequest;
        private MandatoryLootRequest? _lastMandatoryLootRequest;
        private readonly Dictionary<long, MandatoryLootRequest> _mandatoryLootRequestHistory = new();
        private readonly Queue<long> _mandatoryLootRequestHistoryOrder = new();
        private const int MaxMandatoryLootRequestHistory = 32;

        // Combat-owned physical recovery movement (RepositionAndRetry / combat unstuck)
        // under a Deferred post-kill obligation. While this holds the id of the active
        // Deferred request, the request stays Deferred and no stationary loot cycle may
        // start (the character is physically walking under combat ownership). Guarded by
        // _cameraCursorOwnershipGate; 0 = no combat-owned recovery movement is active.
        private long _combatRecoveryMovementRequestId;

        // Exclusive camera ownership for normal loot scans. This is set under
        // _cameraCursorOwnershipGate before camera writes and included in
        // LootOwnsCameraOrCursorLocked, so no movement/retarget write can interleave
        // with the verified scan view. The lock is never held across a wait.
        private volatile bool _lootScanCameraOwned;
        private bool _lootCameraReady;
        private DateTime _lootCameraSettledAtUtc = DateTime.MinValue;

        // ── Passive moving pre-scan (MoveAndAttackAndLoot travel) ──
        // This is a lightweight candidate detector, never a collection operation. It
        // captures the current view without touching the camera/cursor/navigation and
        // only decides whether a stationary verification is worth starting.
        private volatile bool _moveAndAttackAndLootMode;
        private DateTime _nextPassivePreScanAt = DateTime.MinValue;
        private DateTime _lastPassivePreScanLogAt = DateTime.MinValue;
        private DateTime _lastPassivePreScanFailureLogAt = DateTime.MinValue;
        private string _lastPassivePreScanFailure = string.Empty;

        /// <summary>Evidence from the travel-candidate request that is currently executing.</summary>
        private TravelCandidateContext? _activeTravelCandidate;

        /// <summary>Bounded duplicate suppression for travel candidates that failed verification.</summary>
        private readonly CandidateSuppressionTracker _candidateSuppression = new();

        private sealed class TravelCandidateContext
        {
            public long RequestId { get; init; }
            public Rectangle Bounds { get; init; }
            public int CandidateComponents { get; init; }
            public float PlayerX { get; init; }
            public float PlayerY { get; init; }
            public DateTime ObservedAtUtc { get; init; }
            public bool ItemCollected { get; set; }
        }

        /// <summary>
        /// Evidence-based duplicate suppression for pre-scan candidates. A candidate is
        /// suppressed only while the same screen region keeps failing verification; a
        /// candidate whose bounds move is new evidence and is allowed to stop navigation.
        /// Repeated failures grow a bounded backoff, so a static UI artifact cannot
        /// produce a stop/start loop, but no location is blacklisted permanently.
        /// </summary>
        internal sealed class CandidateSuppressionTracker
        {
            private readonly object _gate = new();
            private Rectangle _bounds;
            private DateTime _suppressedUntilUtc = DateTime.MinValue;
            private int _failureCount;

            public int FailureCount
            {
                get { lock (_gate) return _failureCount; }
            }

            public bool IsSuppressed(Rectangle bounds, DateTime now)
            {
                lock (_gate)
                {
                    if (_failureCount <= 0)
                        return false;
                    if (now >= _suppressedUntilUtc)
                    {
                        _failureCount = 0;
                        return false;
                    }
                    if (!IsSameRegion(_bounds, bounds))
                    {
                        // The visual evidence moved: this may be a different drop.
                        _failureCount = 0;
                        return false;
                    }
                    return true;
                }
            }

            public (int FailureCount, int DurationMs) RecordFailure(Rectangle bounds, DateTime now)
            {
                lock (_gate)
                {
                    bool sameRegion = _failureCount > 0 &&
                                      now < _suppressedUntilUtc &&
                                      IsSameRegion(_bounds, bounds);
                    _failureCount = sameRegion ? _failureCount + 1 : 1;
                    _bounds = bounds;
                    int shift = Math.Min(_failureCount - 1, 5);
                    int durationMs = Math.Min(
                        BotConstants.Loot.PassivePreScanSuppressionBaseMs * (1 << shift),
                        BotConstants.Loot.PassivePreScanSuppressionMaxMs);
                    _suppressedUntilUtc = now.AddMilliseconds(durationMs);
                    return (_failureCount, durationMs);
                }
            }

            public void Clear()
            {
                lock (_gate)
                {
                    _failureCount = 0;
                    _suppressedUntilUtc = DateTime.MinValue;
                }
            }

            private static bool IsSameRegion(Rectangle a, Rectangle b)
                => LootSystem.IsSameCandidateRegion(a, b);
        }

        /// <summary>
        /// Time since the last item was collected. Used by the movement system to extend
        /// its post-combat loot wait while the loot system is still making progress
        /// (TimeSpan.MaxValue when nothing was collected yet this session).
        /// </summary>
        public TimeSpan TimeSinceLastItemCollected =>
            _lastItemCollectedAt == DateTime.MinValue
                ? TimeSpan.MaxValue
                : DateTime.UtcNow - _lastItemCollectedAt;

        /// <summary>
        /// True while the loot machine is actively collecting (area-loot / pixel scan).
        /// False when idle or after a full scan pass completed with no confirmed items.
        /// The movement system waits for this to become false after a kill, so it does
        /// not walk away before all drops are collected.
        /// </summary>
        public bool IsLootCycleActive =>
            _lootState == LootMachineState.PostMobTab ||
            _lootState == LootMachineState.AreaLoot ||
            _lootState == LootMachineState.AreaLootWait ||
            _lootState == LootMachineState.Scan ||
            _lootState == LootMachineState.PinkScan;

        /// <summary>
        /// True while the effective waypoint mode is MoveAndAttackAndLoot. In this mode
        /// the normal white-loot collector is request-driven: it never starts a full
        /// stationary cycle on a timer. While travelling it runs only the passive
        /// pre-scan; a mandatory post-kill request or a travel-candidate verification
        /// explicitly starts the full scan.
        /// </summary>
        public bool MoveAndAttackAndLootMode
        {
            get => _moveAndAttackAndLootMode;
            set => _moveAndAttackAndLootMode = value;
        }

        /// <summary>True when a mandatory request still owns the navigation obligation.</summary>
        public bool HasActiveMandatoryLootRequest
        {
            get
            {
                lock (_cameraCursorOwnershipGate)
                {
                    return _activeMandatoryLootRequest != null &&
                           IsMandatoryRequestActiveLocked(_activeMandatoryLootRequest);
                }
            }
        }

        /// <summary>
        /// Reads the request that currently blocks navigation (if any). Movement uses
        /// this to adopt a travel-candidate request created by the passive pre-scan;
        /// it never needs to create a second obligation for the same evidence.
        /// </summary>
        public bool TryGetActiveMandatoryLootRequest(
            out long requestId,
            out LootBarrierReason reason)
        {
            lock (_cameraCursorOwnershipGate)
            {
                if (_activeMandatoryLootRequest != null &&
                    IsMandatoryRequestActiveLocked(_activeMandatoryLootRequest))
                {
                    requestId = _activeMandatoryLootRequest.Id;
                    reason = _activeMandatoryLootRequest.Reason;
                    return true;
                }
            }

            requestId = 0;
            reason = default;
            return false;
        }

        /// <summary>
        /// MovementSystem confirms that W and ordinary steering were released for this
        /// request. The stationary camera is only applied after this acknowledgement (or
        /// the bounded fallback) so the loot zoom can never race a walking character.
        /// </summary>
        public void NotifyNavigationHeld(long requestId)
        {
            if (requestId <= 0)
                return;

            lock (_cameraCursorOwnershipGate)
            {
                if (_activeMandatoryLootRequest == null ||
                    _activeMandatoryLootRequest.Id != requestId ||
                    _activeMandatoryLootRequest.NavigationHeld)
                    return;

                _activeMandatoryLootRequest.NavigationHeld = true;
                _activeMandatoryLootRequest.UpdatedAtUtc = DateTime.UtcNow;
                _log($"[LootVerify] Navigation held request={requestId} reason={_activeMandatoryLootRequest.Reason}.");
            }
        }

        /// <summary>
        /// Creates (or coalesces into) a mandatory loot request. Consecutive kills while
        /// the request is still pending/running/deferred reuse the same generation so the
        /// obligation cannot be lost; a request resolved as Completed/Failed/Cancelled/
        /// Unavailable yields a new id. A post-kill reason always wins over a travel
        /// candidate so a kill can never be downgraded to a lighter obligation.
        /// </summary>
        public long BeginMandatoryLootRequest(LootBarrierReason reason)
        {
            lock (_cameraCursorOwnershipGate)
            {
                if (_activeMandatoryLootRequest != null &&
                    IsMandatoryRequestActiveLocked(_activeMandatoryLootRequest))
                {
                    if (reason == LootBarrierReason.PostKill ||
                        _activeMandatoryLootRequest.Reason != LootBarrierReason.PostKill)
                        _activeMandatoryLootRequest.Reason = reason;
                    if (reason == LootBarrierReason.PostKill)
                        _activeTravelCandidate = null;
                    _activeMandatoryLootRequest.UpdatedAtUtc = DateTime.UtcNow;
                    _log($"[LootBarrier] obligation preserved request={_activeMandatoryLootRequest.Id} " +
                         $"state={_activeMandatoryLootRequest.State} reason={_activeMandatoryLootRequest.Reason}.");
                    return _activeMandatoryLootRequest.Id;
                }

                var request = new MandatoryLootRequest
                {
                    Id = ++_nextMandatoryLootRequestId,
                    Reason = reason,
                    State = LootRequestState.Pending,
                    Detail = "request armed",
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow,
                    NeedsFreshCycle = true
                };
                _activeMandatoryLootRequest = request;
                _lastMandatoryLootRequest = request;
                // A brand new generation can never be owned by a stale combat-recovery handoff.
                _combatRecoveryMovementRequestId = 0;
                RememberMandatoryRequestLocked(request);
                _log($"[LootBarrier] Armed reason={reason} request={request.Id}");
                return request.Id;
            }
        }

        /// <summary>Reads the authoritative lifecycle snapshot for one request id.</summary>
        public bool TryGetMandatoryLootRequestStatus(long requestId, out LootRequestStatus status)
        {
            lock (_cameraCursorOwnershipGate)
            {
                if (requestId > 0 && _mandatoryLootRequestHistory.TryGetValue(requestId, out MandatoryLootRequest? request))
                {
                    status = request.Snapshot();
                    return true;
                }
            }

            status = default;
            return false;
        }

        /// <summary>
        /// Cancels one request without ever producing a success state. Used for host
        /// stop/mode changes; movement must keep navigation blocked on this result.
        /// </summary>
        public void CancelMandatoryLootRequest(long requestId, string reason)
        {
            lock (_cameraCursorOwnershipGate)
            {
                if (requestId <= 0 || _activeMandatoryLootRequest == null ||
                    _activeMandatoryLootRequest.Id != requestId ||
                    !IsMandatoryRequestActiveLocked(_activeMandatoryLootRequest))
                    return;

                MandatoryLootRequest request = _activeMandatoryLootRequest;
                request.State = LootRequestState.Cancelled;
                request.Detail = reason ?? "cancelled";
                request.UpdatedAtUtc = DateTime.UtcNow;
                _activeMandatoryLootRequest = null;
                if (_activeTravelCandidate?.RequestId == requestId)
                    _activeTravelCandidate = null;
                if (_combatRecoveryMovementRequestId == requestId)
                    _combatRecoveryMovementRequestId = 0;
                ReleaseLootScanCameraLocked($"request {requestId} cancelled");
                _log($"[LootBarrier] Cancelled request={requestId} reason={request.Detail}");
            }
        }

        /// <summary>
        /// Atomically hands the character to a combat-owned physical recovery for one
        /// Deferred post-kill request. While the handoff is held the request stays
        /// Deferred and no stationary loot cycle/camera may start, so a zoom can never
        /// race the recovery walk. Only the exact Deferred post-kill generation qualifies;
        /// a request that is Pending/Running/terminal is refused.
        /// </summary>
        internal bool TryBeginCombatRecoveryMovement(long requestId)
        {
            if (requestId <= 0)
                return false;

            lock (_cameraCursorOwnershipGate)
            {
                MandatoryLootRequest? request = _activeMandatoryLootRequest;
                if (request == null || request.Id != requestId ||
                    request.Reason != LootBarrierReason.PostKill ||
                    request.State != LootRequestState.Deferred)
                    return false;

                if (_combatRecoveryMovementRequestId != 0)
                    return false;

                // Never begin the handoff while a capture/collection still drives the
                // character or the camera.
                if (_isCollecting || _lootState == LootMachineState.Scan ||
                    _lootState == LootMachineState.PinkScan)
                    return false;

                _combatRecoveryMovementRequestId = requestId;
                _log($"[LootBarrier] Combat recovery movement authorized request={requestId} — obligation stays Deferred.");
                return true;
            }
        }

        /// <summary>
        /// Releases the combat-owned recovery handoff. The obligation is untouched: it
        /// stays Deferred with its original id and resumes normally once combat ends.
        /// Idempotent and safe to call for an already released/foreign generation.
        /// </summary>
        internal void EndCombatRecoveryMovement(long requestId)
        {
            lock (_cameraCursorOwnershipGate)
            {
                if (_combatRecoveryMovementRequestId == 0 ||
                    (requestId > 0 && _combatRecoveryMovementRequestId != requestId))
                    return;

                long released = _combatRecoveryMovementRequestId;
                _combatRecoveryMovementRequestId = 0;
                _log($"[LootBarrier] Combat recovery movement released request={released} — obligation preserved.");
            }
        }

        /// <summary>True while combat-owned recovery movement holds the character for this request.</summary>
        internal bool IsCombatRecoveryMovementActiveFor(long requestId)
        {
            lock (_cameraCursorOwnershipGate)
                return requestId > 0 && _combatRecoveryMovementRequestId == requestId;
        }

        /// <summary>Most recently resolved game window handle.</summary>
        private nint _hwnd;

        /// <summary>
        /// Loot-priority mode (profile flag "Loot Priority"): when a mob is killed, loot
        /// starts immediately instead of TABbing to check for more mobs first. The loot
        /// machine waits ~200ms for the drops to appear, then area-loots / pixel-scans
        /// until everything is collected (<see cref="IsLootingActive"/>); only then may
        /// the combat system select the next target and movement resume. While a mob is
        /// TARGETED the loot machine pauses completely (no scans during combat — it just
        /// waits), and a mob selected mid-loot interrupts the loot phase so combat takes
        /// over; the next kill re-arms it.
        /// </summary>
        public bool LootPriorityMode { get; set; } = false;

        /// <summary>
        /// SOD/SOP-only loot mode, used for MoveAndAttack waypoints: after each mob kill
        /// the loot machine runs ONE full-window pixel scan that looks ONLY for PINK
        /// (SOD/SOP) blobs — no small-region pass, no white normal-loot scanning and no
        /// spacebar area-loot cycle. Every candidate pixel is probed in one continuous
        /// mouse sweep; a fresh sweep starts after each successful pickup, and a sweep
        /// that collects nothing over its whole candidate set ends the post-kill check.
        /// While no kill is being processed the loot machine stays idle (it never starts
        /// the usual loot cycle). Set per-tick by the host from the current waypoint
        /// mode; while false the normal MoveAndAttackAndLoot behaviour is unchanged.
        /// </summary>
        public bool PinkLootOnlyMode
        {
            get => _pinkLootOnlyMode;
            set
            {
                lock (_cameraCursorOwnershipGate)
                {
                    bool changed = _pinkLootOnlyMode != value;
                    _pinkLootOnlyMode = value;
                    if (!value &&
                        (changed || _pinkScanRequested || _pinkScanOwnsCameraCursor ||
                         _lootState == LootMachineState.PinkScan))
                    {
                        CancelPinkModeWorkLocked();
                    }
                }
            }
        }

        /// <summary>
        /// Enables player-marker pixel checks during loot scan passes. The host sets this
        /// for MoveAndAttack and MoveAndAttackAndLoot waypoints; loot scanning also
        /// requires that the player is outside the city.
        /// </summary>
        public bool PlayerMarkerScanEnabled
        {
            get => _playerMarkerScanEnabled;
            set
            {
                _playerMarkerScanEnabled = value;
                if (!value)
                    ResetPlayerMarkerConfirmation();
            }
        }

        /// <summary>Called when a confirmed other-player marker should trigger bot safety behavior.</summary>
        public Action? OtherPlayerMarkerDetected { get; set; }

        /// <summary>
        /// Optional live setting for whether a confirmed marker should trigger bot safety
        /// behavior. Null preserves the default behavior (stop/return to city).
        /// </summary>
        public Func<bool>? ShouldStopOnOtherPlayerMarker { get; set; }

        /// <summary>
        /// Runs the confirmed-marker safety behavior for a marker detected outside the
        /// periodic loot scan — the MovementSystem attack-start player-pixel check. The
        /// caller has already saved the annotated diagnostic screenshot.
        /// </summary>
        internal void ReportImmediateOtherPlayerMarker(PlayerMarkerCandidate candidate, string source)
        {
            _log($"[PlayerPixels] {source}: other-player marker detected — " +
                 $"{candidate.Faction} bounds={FormatRectangle(candidate.Bounds)} " +
                 $"pixels={candidate.PixelCount} fill={candidate.FillRatio:P0} aspect={candidate.AspectRatio:F2}.");
            if (ShouldStopOnOtherPlayerMarker?.Invoke() ?? true)
            {
                OtherPlayerMarkerDetected?.Invoke();
            }
            else
            {
                _log("[PlayerPixels] Stop/return-to-city behavior is disabled; continuing normally.");
            }
        }

        /// <summary>
        /// Requests the immediate post-kill SOD/SOP pink scan after MovementSystem has
        /// tried a close-camera TAB and found no nearby mob. No-op unless
        /// <see cref="PinkLootOnlyMode"/> is enabled. The request is consumed by
        /// <see cref="Update"/>.
        /// </summary>
        public void RequestPinkScan()
        {
            DateTime now = DateTime.UtcNow;
            lock (_cameraCursorOwnershipGate)
            {
                if (!_pinkLootOnlyMode)
                    return;

                _pinkScanRequestedAt = now;
                _pinkScanRequested = true;
                _pinkRescanScheduler.ScheduleKill(
                    now, TimeSpan.FromMilliseconds(BotConstants.Loot.PinkRescanDelayMs));
            }
            _log("[Loot] SOD/SOP pink scan requested (no close mob found after kill).");
        }

        /// <summary>
        /// Arms the delayed background SOD/SOP rescan for a kill without starting the
        /// immediate scan. Movement first checks for a nearby mob and requests the
        /// immediate scan only when that close-range TAB finds none.
        /// </summary>
        public void SchedulePinkRescanAfterKill()
        {
            DateTime now = DateTime.UtcNow;
            lock (_cameraCursorOwnershipGate)
            {
                if (!_pinkLootOnlyMode)
                    return;

                _lastKillAt = now;
                _pinkRescanScheduler.ScheduleKill(
                    now, TimeSpan.FromMilliseconds(BotConstants.Loot.PinkRescanDelayMs));
            }
        }

        /// <summary>
        /// True while a post-kill SOD/SOP pink scan is requested or running. The
        /// movement system holds combat/movement while this is true, so the drop check
        /// always finishes before the bot walks away or starts the next fight.
        /// </summary>
        public bool IsPinkScanPendingOrActive =>
            _pinkScanRequested || _lootState == LootMachineState.PinkScan;

        /// <summary>
        /// True while the background SOD/SOP rescan pass runs. Movement yields the camera
        /// and cursor and suspends target-cycle evaluation for the pass, so TAB is never
        /// sent while the scan zoom is active.
        /// </summary>
        public bool IsPinkRescanActive => _pinkRescanActive;

        /// <summary>
        /// Runs a movement camera write only when loot does not own the camera. The
        /// shared gate makes the ownership check and write atomic with scan startup.
        /// </summary>
        internal bool TryRunMovementCameraAction(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            lock (_cameraCursorOwnershipGate)
            {
                if (LootOwnsCameraOrCursorLocked())
                    return false;
                action();
                return true;
            }
        }

        /// <summary>
        /// Runs a movement cursor write only when loot does not own the cursor. The
        /// shared gate prevents fast-turn mouse positioning from racing a loot probe.
        /// </summary>
        internal bool TryRunMovementCursorAction(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            lock (_cameraCursorOwnershipGate)
            {
                if (LootOwnsCameraOrCursorLocked())
                    return false;
                action();
                return true;
            }
        }

        internal bool TryRunMovementTargetAction(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            lock (_cameraCursorOwnershipGate)
            {
                bool pinkOwnsTargeting = _pinkScanRequested || _pinkScanOwnsCameraCursor ||
                                         _pinkRescanActive || _isCollecting ||
                                         _lootState == LootMachineState.PinkScan;
                if (pinkOwnsTargeting)
                    return false;
                action();
                return true;
            }
        }

        private bool LootOwnsCameraOrCursorLocked()
            => _pinkScanRequested || _pinkScanOwnsCameraCursor || _pinkRescanActive ||
               _lootScanCameraOwned || _isCollecting || IsLootCycleActive;

        private void CancelPinkModeWorkLocked()
        {
            _pinkScanRequested = false;
            _pinkScanRequestedAt = DateTime.MinValue;
            _pinkRescanScheduler.Cancel();

            // A currently executing rescan retains ownership until its finally block
            // restores the camera. A queued/immediate pass can be safely released now.
            if (_pinkRescanActive)
                return;

            if (_pinkScanOwnsCameraCursor || _lootState == LootMachineState.PinkScan)
                RestorePinkCameraLocked();
            _pinkScanOwnsCameraCursor = false;
            if (_lootState == LootMachineState.PinkScan)
            {
                _lootState = LootMachineState.Idle;
                _nextActionTime = DateTime.MinValue;
                _isCollecting = false;
                IsLootingActive = false;
            }
            _consecutivePinkCaptureFailures = 0;
        }

        /// <summary>
        /// True while the loot machine is in its post-kill looting phase (loot priority
        /// mode only): set the moment a mob dies (mob selected → no target) and cleared
        /// when a full scan pass finds no more items (everything looted) or the loot is
        /// cancelled (city entered / focus lost). While true, the movement system must
        /// NOT select the next target (no TAB / no attack) and must NOT move to the next
        /// waypoint — loot is the priority.
        /// </summary>
        public bool IsLootingActive { get; private set; } = false;

        /// <summary>
        /// True while the loot machine is in the pixel-scan state (Scan): it is actively
        /// looking for, clicking and walking to ground items.
        /// </summary>
        public bool IsScanActive => _lootState == LootMachineState.Scan ||
                                    _lootState == LootMachineState.PinkScan;

        /// <summary>
        /// True while the loot system is actually collecting a found item (mouseover
        /// confirmed): it left-clicked and the character is auto-walking to the item.
        /// The movement system suspends combat and waypoint movement ONLY while this is
        /// true — scanning alone never interrupts the attack.
        /// </summary>
        public bool IsCollecting => _isCollecting;
        private volatile bool _isCollecting = false;

        /// <summary>Previous tick's IsMobSelected value — used to detect mob death.</summary>
        private bool _wasMobSelectedPrev = false;

        /// <summary>When the last mob-kill was detected (UtcNow) — for elapsed-time logging through the loot chain.</summary>
        private DateTime _lastKillAt = DateTime.MinValue;

        /// <summary>Elapsed-time suffix for loot-chain logs ("" when no recent kill).</summary>
        private string KillElapsed()
        {
            if (_lastKillAt == DateTime.MinValue) return "";
            return $" (+{(DateTime.UtcNow - _lastKillAt).TotalMilliseconds:F0}ms since kill)";
        }

        // ── Client-area tracking (window-position independent) ──
        private int _clientOriginX;
        private int _clientOriginY;
        private int _clientWidth;
        private int _clientHeight;
        private bool _hasClientOrigin;
        private LootClientGeometry? _lastLoggedGeometry;

        // ── Coordinate offset from reference window position ──
        // The hardcoded scan coordinates in BotConstants.Loot are SCREEN-ABSOLUTE values
        // from the old bot, designed for when the game window was at reference position
        // (ExpectedWindowX=447, ExpectedWindowY=77).  The reference client origin (what
        // GetClientRect+ClientToScreen returned at that reference position) is stored in
        // _referenceClientOriginX/Y.  The difference between the CURRENT client origin and
        // the REFERENCE client origin is _coordOffsetX/Y.
        //
        // To convert old screen-absolute coords → current screen coords:
        //     screenX = oldScreenAbsX + _coordOffsetX
        //
        // To convert old screen-absolute coords → bitmap-local (capture-relative) coords:
        //     bitmapX = oldScreenAbsX - _referenceClientOriginX
        private int _referenceClientOriginX;
        private int _referenceClientOriginY;
        private int _coordOffsetX;
        private int _coordOffsetY;

        /// <summary>Startup time — skip scans for the first 5s so loot doesn't run before movement.</summary>
        private readonly DateTime _createdAt = DateTime.UtcNow;

        public LootSystem(GameMemoryService memoryService, Action<string> log)
        {
            _memoryService = memoryService;
            _log = log;

            // Bitmap will be created with actual client dimensions once ResolveClientOrigin succeeds.
            _clientWidth = 0;
            _clientHeight = 0;
            _bitmap = null!;
            _graphics = null!;

            // Resolve once for startup diagnostics. CaptureScreen refreshes all geometry
            // before every frame, even when this initial resolution succeeded.
            RefreshClientOrigin();
        }

        // ════════════════════════════════════════════════════════════════
        //  WINDOW CLIENT-AREA RESOLUTION
        //  Uses GetClientRect + ClientToScreen to find where the game
        //  window's client area sits on screen. All scan coordinates in
        //  BotConstants.Loot are treated as client-relative; they are
        //  converted to absolute screen coordinates at capture/click time.
        // ════════════════════════════════════════════════════════════════

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(nint hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern nint FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint GetForegroundWindow();

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        /// <summary>
        /// Refreshes the game handle, window rectangle, client origin and client size.
        /// Failed geometry is invalidated; no screen-origin fallback is allowed for a loot
        /// capture because it would turn a bad capture into a false empty scan.
        /// </summary>
        private LootClientGeometry? RefreshClientOrigin()
        {
            nint hwnd = FindGameWindow();
            if (hwnd == nint.Zero)
            {
                InvalidateClientGeometry();
                return null;
            }

            _hwnd = hwnd;
            if (!GetClientRect(hwnd, out RECT clientRect) ||
                !GetWindowRect(hwnd, out RECT windowRect))
            {
                InvalidateClientGeometry(hwnd);
                return new LootClientGeometry(hwnd, Rectangle.Empty, Point.Empty, Size.Empty);
            }

            POINT topLeft = new() { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref topLeft))
            {
                InvalidateClientGeometry(hwnd);
                return new LootClientGeometry(hwnd, Rectangle.Empty, Point.Empty, Size.Empty);
            }

            int width = clientRect.Right - clientRect.Left;
            int height = clientRect.Bottom - clientRect.Top;
            var geometry = new LootClientGeometry(
                hwnd,
                Rectangle.FromLTRB(windowRect.Left, windowRect.Top, windowRect.Right, windowRect.Bottom),
                new Point(topLeft.X, topLeft.Y),
                new Size(width, height));
            if (!geometry.IsValid)
            {
                InvalidateClientGeometry(hwnd);
                return geometry;
            }

            try
            {
                ApplyClientGeometry(geometry);
            }
            catch
            {
                InvalidateClientGeometry(hwnd);
                throw;
            }
            if (_lastLoggedGeometry != geometry)
            {
                _lastLoggedGeometry = geometry;
                _log($"[LootSystem] Game window at screen ({windowRect.Left}, {windowRect.Top}), " +
                     $"client area ({_clientOriginX}, {_clientOriginY}) size {_clientWidth}x{_clientHeight}");
                _log($"[LootSystem] Coord offset from reference: ({_coordOffsetX}, {_coordOffsetY}), " +
                     $"reference client origin: ({_referenceClientOriginX}, {_referenceClientOriginY})");
            }
            return geometry;
        }

        private static nint FindGameWindow()
        {
            nint hwnd = FindWindow(null, "Legend of Ares");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Ares");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Nostalgia");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Epic Of Ares Client");
            return hwnd;
        }

        private void ApplyClientGeometry(LootClientGeometry geometry)
        {
            _hwnd = geometry.WindowHandle;
            _clientOriginX = geometry.ClientScreenOrigin.X;
            _clientOriginY = geometry.ClientScreenOrigin.Y;
            _clientWidth = geometry.ClientSize.Width;
            _clientHeight = geometry.ClientSize.Height;

            Point offset = geometry.CoordinateOffset;
            Point referenceOrigin = geometry.ReferenceClientOrigin;
            _coordOffsetX = offset.X;
            _coordOffsetY = offset.Y;
            _referenceClientOriginX = referenceOrigin.X;
            _referenceClientOriginY = referenceOrigin.Y;

            // Keep the capture resources when the client size is unchanged. Build the
            // replacement before disposing the previous bitmap so a failed allocation
            // cannot leave a partially initialized capture pair.
            if (_bitmap == null || _bitmap.Width != _clientWidth || _bitmap.Height != _clientHeight)
            {
                var replacement = new Bitmap(_clientWidth, _clientHeight, PixelFormat.Format32bppArgb);
                Graphics replacementGraphics;
                try
                {
                    replacementGraphics = Graphics.FromImage(replacement);
                }
                catch
                {
                    replacement.Dispose();
                    throw;
                }

                _graphics?.Dispose();
                _bitmap?.Dispose();
                _bitmap = replacement;
                _graphics = replacementGraphics;
                _log($"[LootSystem] Bitmap resized to actual client area: {_clientWidth} x {_clientHeight}");
            }

            _hasClientOrigin = true;
        }

        private void InvalidateClientGeometry(nint hwnd = default)
        {
            _hwnd = hwnd;
            _clientOriginX = 0;
            _clientOriginY = 0;
            _clientWidth = 0;
            _clientHeight = 0;
            _referenceClientOriginX = 0;
            _referenceClientOriginY = 0;
            _coordOffsetX = 0;
            _coordOffsetY = 0;
            _hasClientOrigin = false;
        }

        // ── Coordinate conversion helpers ──
        // The hardcoded values in BotConstants.Loot are screen-absolute coordinates
        // from the old bot, designed for reference client origin (450, 103).

        /// <summary>
        /// Converts a hardcoded-old-screen-absolute X to bitmap-local X coordinate
        /// (relative to the current capture origin).
        /// </summary>
        private int OldScreenXToBitmapLocal(int oldScreenAbsX) =>
            oldScreenAbsX - _referenceClientOriginX;

        /// <summary>
        /// Converts a hardcoded-old-screen-absolute Y to bitmap-local Y coordinate
        /// (relative to the current capture origin).
        /// </summary>
        private int OldScreenYToBitmapLocal(int oldScreenAbsY) =>
            oldScreenAbsY - _referenceClientOriginY;

        /// <summary>
        /// Converts bitmap-local coordinates BACK to screen-absolute coordinates
        /// by applying the reference-to-current offset.
        /// Equivalent to: oldScreenAbs + _coordOffset.
        /// </summary>
        private (int ScreenX, int ScreenY) BitmapLocalToScreen(int bitmapLocalX, int bitmapLocalY)
        {
            // The stable capture geometry owns the current origin; this is equivalent
            // to legacy reference-origin + current offset, without stale-offset drift.
            return (bitmapLocalX + _clientOriginX, bitmapLocalY + _clientOriginY);
        }

        /// <summary>
        /// Checks whether the game window is the foreground (focused) window.
        /// If it is not, the loot bot must NOT scan or click to avoid acting on other windows.
        /// </summary>
        private bool IsGameWindowFocused()
        {
            nint currentGameWindow = FindGameWindow();
            _hwnd = currentGameWindow;
            return currentGameWindow != nint.Zero && GetForegroundWindow() == currentGameWindow;
        }

        /// <summary>
        /// Logs the scan region details: client origin and all scan ranges
        /// in both client-relative and absolute screen coordinates.
        /// </summary>
        private void LogScanArea()
        {
            _log($"[LootSystem] === Scan Area Report ===");
            _log($"[LootSystem] Client origin (screen): ({_clientOriginX}, {_clientOriginY})");
            _log($"[LootSystem] Client area (actual): {_clientWidth} x {_clientHeight}");
            _log($"[LootSystem] Bitmap size: {(_bitmap?.Width ?? 0)} x {(_bitmap?.Height ?? 0)}");

            LogRegion("SmallScan", smallX, smallY);
            LogRegion("BigScan", bigX, bigY);
            LogRegion("ExcludeZone",
                new[] { BotConstants.Loot.ExcludeXMin, BotConstants.Loot.ExcludeXMax },
                new[] { BotConstants.Loot.ExcludeYMin, BotConstants.Loot.ExcludeYMax });
            LogRegion("UnderCharScan",
                new[] { BotConstants.Loot.UnderCharScanStartX, BotConstants.Loot.UnderCharScanEndX },
                new[] { BotConstants.Loot.UnderCharScanStartY, BotConstants.Loot.UnderCharScanEndY });
            _log($"[LootSystem] === End Scan Area Report ===");
        }

        private void LogRegion(string name, int[] xRange, int[] yRange)
        {
            int x1 = xRange[0], x2 = xRange[1];
            int y1 = yRange[0], y2 = yRange[1];
            // Convert hardcoded screen-absolute ranges → bitmap-local intervals
            int bx1 = OldScreenXToBitmapLocal(x1), bx2 = OldScreenXToBitmapLocal(x2);
            int by1 = OldScreenYToBitmapLocal(y1), by2 = OldScreenYToBitmapLocal(y2);
            // Convert bitmap-local → current screen
            var (sx1, sy1) = BitmapLocalToScreen(bx1, by1);
            var (sx2, sy2) = BitmapLocalToScreen(bx2, by2);
            _log($"[LootSystem] {name}: (old screen-abs X=[{x1}..{x2}) Y=[{y1}..{y2}))  →  " +
                 $"(bitmap-local X=[{bx1}..{bx2}) Y=[{by1}..{by2}))  →  " +
                 $"(screen X=[{sx1}..{sx2}) Y=[{sy1}..{sy2}))");
        }

        public void Stop()
        {
            StopScanSpacebarSpam();
            CancelAllLootWork("loot loop stopped");
        }

        /// <summary>
        /// Quiesces in-flight pixel scanning before recovery movement/camera takes
        /// exclusive ownership (ReverseDiagonal, waypoint special recovery, map-transition
        /// hold). Any active scan pass, pink rescan or camera/cursor lock is released
        /// synchronously under the ownership gate. A pending mandatory post-kill
        /// obligation is only Deferred — it is never marked Completed or silently lost,
        /// and the ownership remains blocked until the recovery releases it.
        /// </summary>
        public void SuspendForRecovery(string reason)
        {
            StopScanSpacebarSpam();
            CancelAllLootWork(reason, deferMandatoryRequest: true);
        }

        /// <summary>
        /// Releases the capture bitmap/graphics used by a short-lived diagnostic
        /// <see cref="LootSystem"/>. Long-running loot systems live for the whole bot session.
        /// </summary>
        public void Dispose()
        {
            try { Stop(); }
            catch (Exception ex) { _log($"[LootTest] Dispose stop failed: {ex.Message}"); }

            lock (_captureGate)
            {
                _graphics?.Dispose();
                _graphics = null!;
                _bitmap?.Dispose();
                _bitmap = null!;
            }
        }

        /// <summary>
        /// Runs one manual loot diagnostic from the Tools tab: zooms the loot camera,
        /// captures the game client, scans for SOD/SOP pink pixels or normal white-loot
        /// pixels, probes the candidates and performs the same mouseover-confirmed click
        /// as the running bot. This does not start or alter the loot state machine.
        /// </summary>
        public async Task<LootDiagnosticResult> RunDiagnosticScanAsync(
            LootDiagnosticMode mode, CancellationToken token)
        {
            bool pinkOnly = mode == LootDiagnosticMode.SodSopPink;
            if (Interlocked.CompareExchange(ref _diagnosticScanActive, 1, 0) != 0)
                return BuildDiagnosticResult(mode, LootDiagnosticOutcome.Busy, 0, 0,
                    "Another diagnostic loot scan is already running.");

            bool previousPinkMode = _pinkLootOnlyMode;
            bool previousCaptureEnabled = ScreenshotService.LootScanCaptureEnabled;
            bool previousProjectScreenshotCapture = ScreenshotService.LootScanCaptureToProjectScreenshots;
            short previousCamera = BotConstants.Camera.DefaultDistanceLock;
            if (_memoryService.TryGetCameraDistance(out short readDistance))
                previousCamera = readDistance;
            else
                _log("[LootTest] Previous camera distance unreadable; the default lock will be restored after the test.");

            short previousVerticalLock = BotConstants.Camera.DefaultVerticalLock;
            bool havePreviousVerticalLock = _memoryService.TryGetCameraVerticalLock(out short readVerticalLock);
            if (havePreviousVerticalLock)
                previousVerticalLock = readVerticalLock;
            else
                _log($"[LootTest] Previous camera vertical lock unreadable; normal loot view {BotConstants.Camera.DefaultVerticalLock} will be left in place.");

            short scanCamera = pinkOnly
                ? BotConstants.Camera.SodSopLootScanDistance
                : BotConstants.Camera.LootScanDistance;
            short scanVerticalLock = BotConstants.Camera.DefaultVerticalLock;
            bool cameraSet = false;
            bool cursorSaved = MouseOperations.TryGetCursorPosition(out int cursorX, out int cursorY);
            int items = 0;
            int passes = 0;

            _scanToken = token;
            try
            {
                // Manual diagnostics always keep the analyzed frame(s) so a failed
                // pickup can be inspected in LootSSFolder afterwards.
                ScreenshotService.ResetLootScanDiagnosticBudgets();
                ScreenshotService.LootScanCaptureEnabled = true;
                ScreenshotService.LootScanCaptureToProjectScreenshots = true;

                if (!IsGameWindowFocused())
                    return BuildDiagnosticResult(mode, LootDiagnosticOutcome.NotFocused, 0, 0,
                        "Game window is not in the foreground — focus the game and retry.");

                if (_memoryService.GetIsInCity())
                    return BuildDiagnosticResult(mode, LootDiagnosticOutcome.InCity, 0, 0,
                        "Player is in the city — loot scans are disabled there.");

                if (RefreshClientOrigin() is null)
                    return BuildDiagnosticResult(mode, LootDiagnosticOutcome.CaptureFailed, 0, 0,
                        "Game window/client geometry could not be resolved.");

                int mouseOverCalibration = BotConstants.GameMagicValues.LootMouseOverValue;
                _log($"[LootTest] {DiagnosticModeName(mode)}: manual scan starting " +
                     $"(client {_clientWidth}x{_clientHeight}, camera {scanCamera}, verticalLock {scanVerticalLock}, " +
                     $"mode={(pinkOnly ? "pink-only" : "white-loot")}, " +
                     $"item mouseover calibration={mouseOverCalibration} (0x{(uint)mouseOverCalibration:X8})" +
                     (mouseOverCalibration == 0
                         ? " — CAPTURE IT with 'Capture Item Mouseover' or no probe can ever confirm a pickup)."
                         : ")."));
                _log($"[LootTest] Diagnostic screenshots will be saved to: {ScreenshotService.GetDiagnosticLootScanOutputDirectory()}");

                // Match normal bot loot conditions exactly: the movement system always
                // applies the waypoint camera distance together with this vertical lock.
                // The old diagnostic only changed the distance, so the captured view
                // could differ from what the running loot machine actually sees.
                _memoryService.SetCameraDistance(scanCamera);
                _memoryService.SetCameraVerticalLock(scanVerticalLock);
                cameraSet = true;
                if (!_memoryService.TryGetCameraDistance(out short appliedDistance) ||
                    appliedDistance != scanCamera)
                {
                    _log($"[LootTest] WARNING: camera distance {scanCamera} could not be verified; the scan view may differ from normal loot.");
                }
                if (!_memoryService.TryGetCameraVerticalLock(out short appliedVerticalLock) ||
                    appliedVerticalLock != scanVerticalLock)
                {
                    _log($"[LootTest] WARNING: camera vertical lock {scanVerticalLock} could not be verified; the scan view may differ from normal loot.");
                }
                await Task.Delay(DiagnosticCameraSettleMs, token).ConfigureAwait(false);

                if (pinkOnly)
                {
                    // The pink scan accepts probes only while the host reports SOD/SOP mode.
                    _pinkLootOnlyMode = true;

                    while (passes < MaxDiagnosticPickupPasses)
                    {
                        token.ThrowIfCancellationRequested();
                        passes++;
                        LootScanPassResult pass = PinkPixelScan("manual-diagnostic");
                        if (pass == LootScanPassResult.Collected)
                        {
                            items++;
                            continue;
                        }
                        return CompleteDiagnosticPass(mode, pass, items, passes);
                    }
                }
                else
                {
                    while (passes < MaxDiagnosticPickupPasses)
                    {
                        token.ThrowIfCancellationRequested();
                        passes++;

                        LootScanPassResult small = ScanRegion(smallX, smallY, "SmallScan-Diag");
                        if (small == LootScanPassResult.Collected)
                        {
                            items++;
                            continue;
                        }
                        if (small != LootScanPassResult.NoItem)
                            return CompleteDiagnosticPass(mode, small, items, passes);

                        LootScanPassResult big = ScanRegion(bigX, bigY, "BigScan-Diag");
                        if (big == LootScanPassResult.Collected)
                        {
                            items++;
                            continue;
                        }
                        return CompleteDiagnosticPass(mode, big, items, passes);
                    }
                }

                return BuildDiagnosticResult(mode, LootDiagnosticOutcome.Collected, items, passes,
                    $"Collected {items} item(s) in {passes} pass(es); manual pickup pass limit reached.");
            }
            catch (OperationCanceledException)
            {
                return BuildDiagnosticResult(mode, LootDiagnosticOutcome.Cancelled, items, passes,
                    "Scan cancelled (timeout or stop request).");
            }
            catch (Exception ex)
            {
                return BuildDiagnosticResult(mode, LootDiagnosticOutcome.Error, items, passes,
                    $"Scan error: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (cameraSet &&
                        (!_memoryService.TryGetCameraDistance(out short currentDistance) ||
                         currentDistance == scanCamera))
                    {
                        RestoreCameraDistance(previousCamera);
                    }

                    // Restore the vertical lock only if the diagnostic still owns the
                    // value it set; a human/movement camera change is left untouched.
                    if (cameraSet &&
                        havePreviousVerticalLock &&
                        (!_memoryService.TryGetCameraVerticalLock(out short currentVerticalLock) ||
                         currentVerticalLock == scanVerticalLock))
                    {
                        if (!_memoryService.TrySetCameraVerticalLock(previousVerticalLock))
                            _memoryService.SetCameraVerticalLock(previousVerticalLock);
                    }

                    if (cursorSaved)
                        MouseOperations.SetCursorPositionAbsolute(cursorX, cursorY);
                }
                catch (Exception ex)
                {
                    _log($"[LootTest] Cleanup failed (camera/cursor restore): {ex.Message}");
                }
                _pinkLootOnlyMode = previousPinkMode;
                ScreenshotService.LootScanCaptureEnabled = previousCaptureEnabled;
                ScreenshotService.LootScanCaptureToProjectScreenshots = previousProjectScreenshotCapture;
                _scanToken = default;
                Interlocked.Exchange(ref _diagnosticScanActive, 0);
            }
        }

        private LootDiagnosticResult CompleteDiagnosticPass(
            LootDiagnosticMode mode, LootScanPassResult pass, int items, int passes)
        {
            return pass switch
            {
                LootScanPassResult.NoItem => BuildDiagnosticResult(
                    mode, LootDiagnosticOutcome.NoItem, items, passes,
                    $"Scan completed with no matching item collected ({passes} pass(es), {items} item(s) already collected)."),
                LootScanPassResult.CaptureFailed => BuildDiagnosticResult(
                    mode, LootDiagnosticOutcome.CaptureFailed, items, passes,
                    "Game-client capture failed — see the [LootCapture] log lines above."),
                LootScanPassResult.CameraVerificationFailed => BuildDiagnosticResult(
                    mode, LootDiagnosticOutcome.CaptureFailed, items, passes,
                    "Loot scan camera could not be verified — the frame was not analyzed."),
                LootScanPassResult.InvalidGeometry => BuildDiagnosticResult(
                    mode, LootDiagnosticOutcome.CaptureFailed, items, passes,
                    "Loot scan geometry was invalid — the scan was not treated as empty."),
                LootScanPassResult.Incomplete => BuildDiagnosticResult(
                    mode, LootDiagnosticOutcome.CaptureFailed, items, passes,
                    "Loot scan pass was incomplete — the scan was not treated as empty."),
                LootScanPassResult.Cancelled => BuildDiagnosticResult(
                    mode, LootDiagnosticOutcome.Cancelled, items, passes,
                    "Scan cancelled (timeout, focus loss, or city entry)."),
                _ => BuildDiagnosticResult(
                    mode, LootDiagnosticOutcome.CaptureFailed, items, passes,
                    $"Scan ended without a valid empty result ({passes} pass(es)).")
            };
        }

        private LootDiagnosticResult BuildDiagnosticResult(
            LootDiagnosticMode mode, LootDiagnosticOutcome outcome,
            int items, int passes, string detail)
        {
            _log($"[LootTest] {DiagnosticModeName(mode)}: {detail}");
            return new LootDiagnosticResult(mode, outcome, items, passes, detail);
        }

        private static string DiagnosticModeName(LootDiagnosticMode mode) =>
            mode == LootDiagnosticMode.SodSopPink ? "SOD/SOP" : "White";

        public async Task Update(CancellationToken token)
        {
            try
            {
                await UpdateCore(token);
            }
            catch (OperationCanceledException)
            {
                StopScanSpacebarSpam();
                CancelAllLootWork("stop/cancellation during scan");
                throw;
            }
        }

        private async Task UpdateCore(CancellationToken token)
        {
            _scanToken = token;
            if (token.IsCancellationRequested)
            {
                CancelAllLootWork("stop/cancellation");
                StopScanSpacebarSpam();
                return;
            }

            // ── Focus guard: resolve the current game handle before checking focus.
            //    Do not capture or click if another window is foreground. ──
            if (!IsGameWindowFocused())
            {
                ResetPlayerMarkerConfirmation();
                StopScanSpacebarSpam();
                CancelAllLootWork("game window is not foreground", deferMandatoryRequest: true);
                if (!_focusCaptureSkipLogged)
                {
                    _focusCaptureSkipLogged = true;
                    _log("[LootCapture] CAPTURE SKIPPED status=NotForeground (no scan result recorded).");
                }
                await Task.Delay(BotConstants.Delays.LootUpdateMs, token);
                return;
            }
            _focusCaptureSkipLogged = false;

            // ── Startup guard: skip scans for the first 5 seconds so loot
            //    doesn't start before the movement bot begins moving. ──
            if ((DateTime.UtcNow - _createdAt).TotalSeconds < 5.0)
            {
                await Task.Delay(BotConstants.Delays.LootUpdateMs, token);
                return;
            }

            // ── If in city, NEVER scan — cancel any loot in progress. ──
            if (_memoryService.GetIsInCity())
            {
                ResetPlayerMarkerConfirmation();
                StopScanSpacebarSpam();
                CancelAllLootWork("city entry");
                await Task.Delay(BotConstants.Delays.LootUpdateMs, token);
                return;
            }

            if (!PinkLootOnlyMode && _lootState == LootMachineState.PinkScan)
                CancelPinkScanWork("waypoint mode changed");

            if (ConfirmPlayerMarkerIfDue())
            {
                if (!token.IsCancellationRequested)
                    await Task.Delay(BotConstants.Delays.LootUpdateMs, token);
                return;
            }

            // ── Track mob selection to detect mob death ──
            bool isMobSelected = _memoryService.IsMobSelected();

            // ── Post-kill SOD/SOP pink-scan request (MoveAndAttack) ──
            // Keep a fresh request marked pending until it is atomically transferred to
            // the PinkScan state, so movement cannot steal camera/cursor ownership in
            // the handoff. Delayed background work is independently generation-tracked.
            bool pinkScanRequested = false;
            bool droppedPinkRequest = false;
            bool modeChangedPinkRequest = false;
            lock (_cameraCursorOwnershipGate)
            {
                if (_pinkScanRequested)
                {
                    bool fresh = (DateTime.UtcNow - _pinkScanRequestedAt).TotalMilliseconds <=
                                 BotConstants.Loot.PinkScanRequestTtlMs;
                    if (!_pinkLootOnlyMode || !fresh)
                    {
                        _pinkScanRequested = false;
                        _pinkScanRequestedAt = DateTime.MinValue;
                        droppedPinkRequest = !fresh;
                        modeChangedPinkRequest = fresh && !_pinkLootOnlyMode;
                    }
                    else
                    {
                        pinkScanRequested = true;
                    }
                }
            }
            if (modeChangedPinkRequest)
                _log("[Loot] Pink scan request cancelled because SOD/SOP mode changed.");
            else if (droppedPinkRequest)
                _log("[Loot] Stale pink-scan request dropped (expired before it could start).");

            // Mob just died (was selected → no longer selected). Movement owns the
            // close-camera TAB check in SOD/SOP mode, so the loot loop only arms the
            // delayed background rescan here; it must not start the immediate pink scan
            // before Movement has checked for nearby mobs. Loot-priority and normal loot
            // modes retain their existing behavior.
            if (_wasMobSelectedPrev && !isMobSelected)
            {
                _wasMobSelectedPrev = false;
                _lastKillAt = DateTime.UtcNow;
                if (PinkLootOnlyMode)
                {
                    SchedulePinkRescanAfterKill();
                    _log("[Loot] Mob killed — immediate SOD/SOP scan deferred until the close-camera mob check finishes.");
                }
                else if (MoveAndAttackAndLootMode)
                {
                    // The mandatory post-kill request belongs to MovementSystem.
                    // Never start the legacy timer-driven collector here: that was the
                    // source of repeated stationary scans during ordinary travel.
                    _lootState = LootMachineState.Idle;
                    _nextActionTime = DateTime.UtcNow;
                    _consecutiveEmptySpacePresses = 0;
                    IsLootingActive = false;
                    _log("[Loot] Mob killed — waiting for the mandatory post-kill loot request (reason=PostKillMandatory).");
                }
                else if (LootPriorityMode)
                {
                    _log($"[Loot] Mob killed (loot priority) — waiting {BotConstants.Delays.LootPostKillDelayMs}ms for drops, then looting everything.");
                    IsLootingActive = true;
                    _lootState = LootMachineState.AreaLoot;
                    _nextActionTime = DateTime.UtcNow.AddMilliseconds(BotConstants.Delays.LootPostKillDelayMs);
                    _consecutiveEmptySpacePresses = 0;
                }
                else
                {
                    _log("[Loot] Mob killed — pressing TAB to check for more mobs.");
                    _lootState = LootMachineState.PostMobTab;
                    _nextActionTime = DateTime.UtcNow;
                    _consecutiveEmptySpacePresses = 0;
                }
            }

            // ── Start the post-kill SOD/SOP pink scan ──
            // Transfer request ownership atomically. Re-requests during an existing pass
            // are coalesced; the scheduler retains any newer kill generation separately.
            bool pinkScanStarted = false;
            if (pinkScanRequested)
            {
                lock (_cameraCursorOwnershipGate)
                {
                    if (_pinkLootOnlyMode && _pinkScanRequested && _lootState != LootMachineState.PinkScan)
                    {
                        _pinkScanRequested = false;
                        _pinkScanRequestedAt = DateTime.MinValue;
                        _pinkScanOwnsCameraCursor = true;
                        IsLootingActive = true;
                        _lootState = LootMachineState.PinkScan;
                        _nextActionTime = DateTime.UtcNow.AddMilliseconds(BotConstants.Delays.LootPostKillDelayMs);
                        _consecutiveEmptySpacePresses = 0;
                        pinkScanStarted = true;
                    }
                    else if (_lootState == LootMachineState.PinkScan)
                    {
                        _pinkScanRequested = false;
                        _pinkScanRequestedAt = DateTime.MinValue;
                    }
                }
            }
            if (pinkScanStarted)
                _log($"[Loot] SOD/SOP pink scan starting after close-mob check (big region, pink pixels; waiting {BotConstants.Delays.LootPostKillDelayMs}ms for drops).");

            // ── Background SOD/SOP rescan (MoveAndAttack) ──
            // Work is coalesced by earliest due time and generation. A target may remain
            // selected; that never cancels a due background scan. Immediate and background
            // passes share one ownership gate, so their captures/cursor probes cannot overlap.
            PinkRescanSnapshot schedulerSnapshot = _pinkRescanScheduler.Snapshot;
            PinkRescanAction rescan = DecidePinkRescan(
                PinkLootOnlyMode,
                _lootState == LootMachineState.PinkScan || _pinkScanRequested,
                schedulerSnapshot.DueAtUtc,
                DateTime.UtcNow,
                TimeSpan.FromMilliseconds(BotConstants.Loot.PinkRescanExpireMs));
            if (rescan == PinkRescanAction.Expire)
            {
                _pinkRescanScheduler.Cancel();
            }
            else if (rescan == PinkRescanAction.Run)
            {
                PinkRescanTicket ticket = default;
                bool began;
                lock (_cameraCursorOwnershipGate)
                {
                    began = _pinkLootOnlyMode && !_pinkScanRequested &&
                            _lootState != LootMachineState.PinkScan && !_pinkRescanActive &&
                            _pinkRescanScheduler.TryBegin(
                                DateTime.UtcNow, _pinkLootOnlyMode,
                                _lootState == LootMachineState.PinkScan || _pinkScanRequested,
                                out ticket);
                    if (began)
                        _pinkRescanActive = true;
                }

                if (began)
                {
                    PinkRescanPassOutcome outcome = RunPinkRescan(ticket);
                    _pinkRescanScheduler.Complete(
                        ticket,
                        DateTime.UtcNow,
                        outcome,
                        TimeSpan.FromMilliseconds(50),
                        TimeSpan.FromMilliseconds(Math.Max(
                            BotConstants.Delays.LootUpdateMs,
                            BotConstants.Delays.SodSopCameraSettleMs)));
                    await Task.Delay(BotConstants.Delays.LootUpdateMs, token);
                    return;
                }
            }

            // ── Mandatory post-kill / travel-candidate obligation ──
            // The request is serviced only after the combat pause and pink-scan
            // handling above. A selected mob defers it; the obligation stays alive.
            ServiceMandatoryLootRequest(isMobSelected);

            // ── Passive moving pre-scan (MoveAndAttackAndLoot) ──
            // This is the ONLY loot activity during ordinary travel. It observes the
            // current view, never changes the camera/cursor/input, and never starts a
            // full cycle by itself: a PotentialCandidate creates a travel-verification
            // request that MovementSystem turns into a navigation hold.
            if (MoveAndAttackAndLootMode &&
                !isMobSelected &&
                !PinkLootOnlyMode &&
                !HasActiveMandatoryLootRequest &&
                _lootState == LootMachineState.Idle)
            {
                RunPassiveLootPreScanIfDue();
            }

            // ── A selected mob pauses loot ──
            // While a mob is targeted (combat in progress) the loot machine does NOT
            // scan — it just waits for the fight to end. Looting while fighting looks
            // unnatural. Loot resumes when the mob dies (death detection above) or the
            // target is lost. Exception: a requested/active SOD/SOP pink scan is never
            // cancelled — it belongs to the mob that just died and must finish.
            if (isMobSelected)
            {
                // Remember the selection for mob-death detection.
                _wasMobSelectedPrev = true;

                if (_lootState != LootMachineState.PinkScan)
                {
                    if (_lootState != LootMachineState.Idle)
                    {
                        StopScanSpacebarSpam();
                        _lootState = LootMachineState.Idle;
                        _consecutiveEmptySpacePresses = 0;
                    }

                    // A mob selected mid-loot interrupts the loot phase — combat takes
                    // over and the next kill re-arms the phase. The mandatory request (if
                    // any) is only Deferred: it is never completed by this interruption.
                    IsLootingActive = false;
                    lock (_cameraCursorOwnershipGate)
                        ReleaseLootScanCameraLocked("combat target selected — loot deferred");

                    await Task.Delay(BotConstants.Delays.LootUpdateMs, token);
                    return;
                }
            }

            // ── Loot state machine ──
            // PostMobTab→ press TAB (best-effort), then immediately start loot
            //   (combat bot's own targeting interrupts loot via IsMobSelected check;
            //   in loot-priority mode loot never interrupts)
            // AreaLoot → press spacebar x3, snapshot inventory → AreaLootWait → compare →
            //   (items collected) → AreaLoot again (keep spacebar-looting)
            //   (no items after N tries) → Scan (with 200ms delay)
            // Scan → pixel-scan for items (meanwhile spam spacebar), collect them → back to AreaLoot
            // PinkScan → SOD/SOP-only mode: big region, pink pixels, repeat until none left
            // ===================================================================

            switch (_lootState)
            {
                case LootMachineState.Idle:
                    if (PinkLootOnlyMode)
                    {
                        // SOD/SOP-only mode scans solely after a kill. While walking
                        // there is nothing to do — no area-loot, no white-pixel scan.
                        break;
                    }

                    if (MoveAndAttackAndLootMode)
                    {
                        // Request-driven only: the passive pre-scan runs while walking,
                        // and a mandatory request starts the full cycle. Idle must never
                        // start the legacy timer-based collector.
                        break;
                    }

                    // Start the loot cycle.
                    _lootState = LootMachineState.AreaLoot;
                    _nextActionTime = DateTime.UtcNow;
                    _consecutiveEmptySpacePresses = 0;
                    goto case LootMachineState.AreaLoot;

                case LootMachineState.PostMobTab:
                    if (DateTime.UtcNow >= _nextActionTime)
                    {
                        // Press TAB (best-effort to check for more mobs), then immediately
                        // start area loot. If the combat bot acquires a new target, the
                        // IsMobSelected check at the top of Update() will cancel loot.
                        // Do NOT wait and check the result — that would conflict with
                        // the combat bot's own TAB cycle and might keep loot stuck in Idle.
                        GameInput.PressKey(GameInput.VK_TAB, GameInput.SCAN_TAB);
                        _log("[Loot] TAB pressed — starting area loot.");
                        _lootState = LootMachineState.AreaLoot;
                        _nextActionTime = DateTime.UtcNow;
                    }
                    break;

                case LootMachineState.AreaLoot:
                    if (DateTime.UtcNow >= _nextActionTime)
                    {
                        // Snapshot inventory before pressing spacebar.
                        _inventoryChecksumBefore = _memoryService.ComputeInventoryChecksum();

                        // Press spacebar THREE times with 30ms gap.
                        // Check combat between each press so we cancel immediately
                        // if a mob gets selected during the presses (unless loot
                        // priority mode, where looting outranks combat).
                        GameInput.PressKey(GameInput.VK_SPACE, GameInput.SCAN_SPACE);
                        if (ShouldAbortLoot()) { _lootState = LootMachineState.Idle; break; }
                        Thread.Sleep(30);
                        GameInput.PressKey(GameInput.VK_SPACE, GameInput.SCAN_SPACE);
                        if (ShouldAbortLoot()) { _lootState = LootMachineState.Idle; break; }
                        Thread.Sleep(30);
                        GameInput.PressKey(GameInput.VK_SPACE, GameInput.SCAN_SPACE);
                        _log($"[Loot] Spacebar x3 (round {_consecutiveEmptySpacePresses + 1}/3{KillElapsed()}).");

                        // Wait ~100ms for the game to process the pickup,
                        // then check if anything was collected.
                        _nextActionTime = DateTime.UtcNow.AddMilliseconds(100);
                        _lootState = LootMachineState.AreaLootWait;
                    }
                    break;

                case LootMachineState.AreaLootWait:
                    if (DateTime.UtcNow >= _nextActionTime)
                    {
                        int checksumAfter = _memoryService.ComputeInventoryChecksum();
                        if (checksumAfter != _inventoryChecksumBefore)
                        {
                            // Items were collected — press spacebar again soon.
                            _log($"[Loot] Items collected (chk {_inventoryChecksumBefore} → {checksumAfter}) — repeating spacebar{KillElapsed()}.");
                            _lastItemCollectedAt = DateTime.UtcNow;
                            _consecutiveEmptySpacePresses = 0;
                            _nextActionTime = DateTime.UtcNow.AddMilliseconds(50);
                            _lootState = LootMachineState.AreaLoot;
                        }
                        else
                        {
                            _consecutiveEmptySpacePresses++;
                            if (_consecutiveEmptySpacePresses >= MaxEmptySpacePressesBeforeScan)
                            {
                                // No more items via spacebar → switch to scan after 100ms delay.
                                _log($"[Loot] No more items via spacebar ({_consecutiveEmptySpacePresses} empty, chk {checksumAfter}) — switching to pixel scan in 100ms{KillElapsed()}.");
                                _consecutiveEmptySpacePresses = 0;
                                _lootState = LootMachineState.Scan;
                                _nextActionTime = DateTime.UtcNow.AddMilliseconds(100);
                            }
                            else
                            {
                                // Try spacebar again after a brief pause.
                                _log($"[Loot] Space check empty ({_consecutiveEmptySpacePresses}/{MaxEmptySpacePressesBeforeScan}, chk {checksumAfter}) — retry in {BotConstants.Delays.LootSpacePressMs}ms{KillElapsed()}.");
                                _nextActionTime = DateTime.UtcNow.AddMilliseconds(
                                    BotConstants.Delays.LootSpacePressMs);
                                _lootState = LootMachineState.AreaLoot;
                            }
                        }
                    }
                    break;

                case LootMachineState.Scan:
                {
                    // ── Wait 100ms before the pixel scan. During this wait,
                    //    spam spacebar to opportunistically collect items that
                    //    are already under / near the character via area-loot. ──
                    if (DateTime.UtcNow < _nextActionTime)
                    {
                        GameInput.PressKey(GameInput.VK_SPACE, GameInput.SCAN_SPACE);
                        Thread.Sleep(30);
                        GameInput.PressKey(GameInput.VK_SPACE, GameInput.SCAN_SPACE);
                        break;
                    }

                    // A pass belongs to the mandatory request only when that exact
                    // request was already Running when the pass started. A request
                    // created while a pass is in flight must start a fresh cycle.
                    MandatoryLootRequest? mandatoryRequest = GetRunningMandatoryRequestForScan();
                    long mandatoryRequestId = mandatoryRequest?.Id ?? 0;

                    // ── Pixel scan with spacebar spam on a background thread.
                    //    Each successful scan loots one (or more) items, then we
                    //    rescan to catch any other items that may be visible.
                    //    Items under the character (exclude zone) get collected
                    //    via the background spacebar spam simultaneously. ──
                    StartScanSpacebarSpam();
                    LootScanPassOutcome outcome;
                    try
                    {
                        _log($"[Loot] PixelScan starting (request={(mandatoryRequestId > 0 ? mandatoryRequestId.ToString() : "none")}).");
                        outcome = PixelScan();
                    }
                    finally
                    {
                        StopScanSpacebarSpam();
                    }

                    if (outcome.Result == LootScanPassResult.Collected)
                    {
                        // Item was found and collected — rescan next tick to drain remaining items.
                        _lastItemCollectedAt = DateTime.UtcNow;
                        _nextActionTime = DateTime.UtcNow.AddMilliseconds(50);
                        if (mandatoryRequestId > 0)
                        {
                            RecordMandatoryItemCollected(mandatoryRequestId);
                            _log($"[Loot] Item collected — mandatory request={mandatoryRequestId} requires another scan.");
                        }
                    }
                    else if (outcome.Result == LootScanPassResult.NoItem && outcome.IsValidComplete)
                    {
                        if (mandatoryRequestId > 0)
                        {
                            CompleteMandatoryLootRequest(mandatoryRequestId, outcome);
                        }
                        else
                        {
                            // Full scan pass finished with no items — enter ScanComplete and
                            // hold briefly so the movement system can walk away before the
                            // next area-loot cycle restarts.
                            _log($"[Loot] Scan complete — no more visible items{KillElapsed()}.");
                            IsLootingActive = false;
                            _lastKillAt = DateTime.MinValue;
                            _lootState = LootMachineState.ScanComplete;
                            _scanCompleteUntil = DateTime.UtcNow.AddMilliseconds(
                                BotConstants.Delays.LootScanCompleteHoldMs);
                            _consecutiveEmptySpacePresses = 0;
                            lock (_cameraCursorOwnershipGate)
                                ReleaseLootScanCameraLocked("normal scan complete");
                        }
                    }
                    else if (outcome.Result == LootScanPassResult.Cancelled)
                    {
                        if (mandatoryRequestId > 0)
                        {
                            DeferMandatoryLootRequest(mandatoryRequestId, outcome.Detail ?? "scan cancelled");
                        }
                        else
                        {
                            _log("[Loot] Scan cancelled — not treated as an empty result.");
                            _lootState = LootMachineState.Idle;
                            _nextActionTime = DateTime.UtcNow;
                            _consecutiveEmptySpacePresses = 0;
                            lock (_cameraCursorOwnershipGate)
                                ReleaseLootScanCameraLocked("scan cancelled");
                        }
                    }
                    else
                    {
                        // Capture failures, camera verification failures, invalid
                        // geometry and incomplete passes are never empty results.
                        string failureDetail = $"SmallScan={outcome.SmallScan}, BigScan={outcome.BigScan?.ToString() ?? "not-run"}, detail={outcome.Detail}";
                        _log($"[Loot] Scan pass invalid status={outcome.Result} ({failureDetail}) — not treating as no items.");

                        if (mandatoryRequestId > 0)
                        {
                            bool retry;
                            int attempt;
                            if (outcome.Result == LootScanPassResult.CameraVerificationFailed)
                            {
                                retry = IncrementMandatoryCameraAttempt(
                                    mandatoryRequestId,
                                    BotConstants.Loot.BarrierCameraVerificationAttempts,
                                    out attempt);
                            }
                            else
                            {
                                retry = IncrementMandatoryCaptureAttempt(
                                    mandatoryRequestId,
                                    BotConstants.Loot.BarrierCaptureAttempts,
                                    out attempt);
                            }

                            if (retry)
                            {
                                _log($"[Loot] Mandatory request={mandatoryRequestId} retrying after invalid pass " +
                                     $"({attempt}/{BotConstants.Loot.BarrierCaptureAttempts}).");
                                _nextActionTime = DateTime.UtcNow.AddMilliseconds(
                                    Math.Max(BotConstants.Delays.LootUpdateMs,
                                             BotConstants.Camera.LootScanCameraSettleMs));
                            }
                            else if (mandatoryRequest?.Reason == LootBarrierReason.TravelCandidateVerification)
                            {
                                MarkTravelVerificationUnavailable(
                                    mandatoryRequestId,
                                    $"scan failed status={outcome.Result} after {attempt} attempts ({failureDetail})");
                            }
                            else
                            {
                                FailMandatoryLootRequest(
                                    mandatoryRequestId,
                                    $"scan failed status={outcome.Result} after {attempt} attempts ({failureDetail})");
                            }
                        }
                        else
                        {
                            _lootState = LootMachineState.Idle;
                            _nextActionTime = DateTime.UtcNow.AddMilliseconds(100);
                            _consecutiveEmptySpacePresses = 0;
                            lock (_cameraCursorOwnershipGate)
                                ReleaseLootScanCameraLocked($"scan failed status={outcome.Result}");
                        }
                    }
                    break;
                }

                case LootMachineState.PinkScan:
                    // ── SOD/SOP-only post-kill scan (MoveAndAttack waypoints) ──
                    // ONE whole-client pink pass. A valid empty pass completes normally;
                    // capture failures are retried and never reported as zero-pink frames.
                    if (DateTime.UtcNow < _nextActionTime)
                        break;

                    SetPinkScanCameraDistance();
                    if (_scanToken.WaitHandle.WaitOne(BotConstants.Delays.SodSopCameraSettleMs))
                    {
                        CancelPinkScanWork("cancelled during camera settle");
                        break;
                    }

                    LootScanPassResult pinkResult = PinkPixelScan("post-kill");
                    if (pinkResult == LootScanPassResult.Collected)
                    {
                        _consecutivePinkCaptureFailures = 0;
                        _lastItemCollectedAt = DateTime.UtcNow;
                        _nextActionTime = DateTime.UtcNow.AddMilliseconds(50);
                    }
                    else if (pinkResult == LootScanPassResult.NoItem)
                    {
                        string elapsed = KillElapsed();
                        _lastKillAt = DateTime.MinValue;
                        CompleteImmediatePinkScan(
                            $"[Loot] Pink scan complete — valid frame had no remaining collectable SOD/SOP{elapsed}.");
                    }
                    else if (pinkResult == LootScanPassResult.CaptureFailed)
                    {
                        _consecutivePinkCaptureFailures++;
                        if (_consecutivePinkCaptureFailures < MaxImmediatePinkCaptureFailures)
                        {
                            _log($"[Loot] SOD/SOP frame capture failed " +
                                 $"({_consecutivePinkCaptureFailures}/{MaxImmediatePinkCaptureFailures}); retrying, not treating it as empty.");
                            _nextActionTime = DateTime.UtcNow.AddMilliseconds(
                                Math.Max(BotConstants.Delays.LootUpdateMs, BotConstants.Delays.SodSopCameraSettleMs));
                        }
                        else
                        {
                            _log($"[Loot] Immediate pink scan released after {MaxImmediatePinkCaptureFailures} capture failures; " +
                                 "no empty result was recorded and the delayed scheduler remains pending.");
                            CompleteImmediatePinkScan("[Loot] Immediate pink scan ended as unavailable; background retry remains queued.");
                        }
                    }
                    else
                    {
                        CancelPinkScanWork("scan cancelled");
                    }
                    break;

                case LootMachineState.ScanComplete:
                    // Full scan found nothing. Hold (no spacebar spam / no captures) until
                    // the hold expires, then restart the cycle from Idle.
                    if (DateTime.UtcNow >= _scanCompleteUntil)
                    {
                        _lootState = LootMachineState.Idle;
                        _nextActionTime = DateTime.UtcNow;
                        _consecutiveEmptySpacePresses = 0;
                    }
                    break;
            }

            await Task.Delay(BotConstants.Delays.LootUpdateMs, token);
        }

        /// <summary>
        /// Starts a background task that spams spacebar (double-press every ~130ms)
        /// while the pixel scan is active. This collects items under the character
        /// (the exclude zone that the pixel scan skips) and any items reachable
        /// via mouseover + spacebar area-loot.
        /// </summary>
        private void StartScanSpacebarSpam()
        {
            StopScanSpacebarSpam(); // Ensure any previous spam task is stopped.
            _scanSpacebarCts = new CancellationTokenSource();
            var token = _scanSpacebarCts.Token;

            Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    // If the game window lost focus, stop spamming to avoid
                    // sending keypresses to whatever window is on top.
                    if (GetForegroundWindow() != _hwnd)
                        break;

                    // Double-press spacebar with 30ms gap (same pattern as AreaLoot).
                    GameInput.PressKey(GameInput.VK_SPACE, GameInput.SCAN_SPACE);
                    Thread.Sleep(30);
                    if (token.IsCancellationRequested || GetForegroundWindow() != _hwnd)
                        break;
                    GameInput.PressKey(GameInput.VK_SPACE, GameInput.SCAN_SPACE);
                    Thread.Sleep(100);
                }
            }, token);
        }

        /// <summary>
        /// Stops the scan-phase spacebar spam task.
        /// Safe to call even if no spam task is running.
        /// </summary>
        private void StopScanSpacebarSpam()
        {
            if (_scanSpacebarCts != null)
            {
                _scanSpacebarCts.Cancel();
                _scanSpacebarCts.Dispose();
                _scanSpacebarCts = null;
            }
        }

        /// <summary>
        /// True when the loot machine must abort its current action immediately:
        /// the player entered the city. A selected mob never reaches this point —
        /// the top of <see cref="Update"/> pauses the loot machine entirely while a
        /// mob is targeted.
        /// </summary>
        private bool ShouldAbortLoot() => _memoryService.GetIsInCity();

        private static bool IsMandatoryRequestActiveLocked(MandatoryLootRequest request)
            => request.State == LootRequestState.Pending ||
               request.State == LootRequestState.Running ||
               request.State == LootRequestState.Deferred;

        private void RememberMandatoryRequestLocked(MandatoryLootRequest request)
        {
            _mandatoryLootRequestHistory[request.Id] = request;
            _mandatoryLootRequestHistoryOrder.Enqueue(request.Id);
            while (_mandatoryLootRequestHistoryOrder.Count > MaxMandatoryLootRequestHistory)
            {
                long evicted = _mandatoryLootRequestHistoryOrder.Dequeue();
                _mandatoryLootRequestHistory.Remove(evicted);
            }
        }

        /// <summary>
        /// One passive pre-scan tick. Captures the CURRENT view, classifies white-loot
        /// candidates with the production pixel/shape/text filters, and arms at most one
        /// travel-verification request. It never changes the camera, cursor or navigation
        /// input and never starts the stationary collector by itself.
        /// </summary>
        private void RunPassiveLootPreScanIfDue()
        {
            DateTime now = DateTime.UtcNow;
            if (now < _nextPassivePreScanAt)
                return;
            _nextPassivePreScanAt = now.AddMilliseconds(BotConstants.Loot.PassivePreScanIntervalMs);

            if (!HasActiveMandatoryLootRequest && _lootState != LootMachineState.Idle)
                return;

            LootPreScanResult result = CaptureAndAnalyzePassiveLootFrame();
            if (result.Outcome == LootPreScanOutcome.CaptureUnavailable)
            {
                LogPassivePreScanFailure(result.Detail);
                return;
            }

            if (result.Outcome == LootPreScanOutcome.NoCandidate)
            {
                LogPassivePreScan($"[LootPreScan] NoCandidate whitePixels={result.WhitePixels} " +
                                 $"components={result.CandidateComponents} detail={result.Detail}");
                return;
            }

            if (IsCandidateSuppressed(result.CandidateBounds, now))
            {
                LogPassivePreScan($"[LootPreScan] Candidate suppressed (same region previously failed " +
                                 $"verification) bounds={FormatRectangle(result.CandidateBounds)}");
                return;
            }

            if (HasActiveMandatoryLootRequest)
                return;

            long requestId = BeginMandatoryLootRequest(LootBarrierReason.TravelCandidateVerification);
            if (requestId <= 0)
                return;

            bool isTravelRequest = TryGetMandatoryLootRequestStatus(requestId, out LootRequestStatus status) &&
                                   status.Reason == LootBarrierReason.TravelCandidateVerification;
            if (isTravelRequest)
            {
                var (playerX, playerY) = GetPlayerPositionForSuppression();
                _activeTravelCandidate = new TravelCandidateContext
                {
                    RequestId = requestId,
                    Bounds = result.CandidateBounds,
                    CandidateComponents = result.CandidateComponents,
                    PlayerX = playerX,
                    PlayerY = playerY,
                    ObservedAtUtc = now
                };
                _log($"[LootPreScan] PotentialCandidate detected components={result.CandidateComponents} " +
                     $"whitePixels={result.WhitePixels} bounds={FormatRectangle(result.CandidateBounds)}");
                _log($"[LootVerify] Starting stationary verification request={requestId} " +
                     "reason=TravelCandidateVerification");
            }
        }

        private LootPreScanResult CaptureAndAnalyzePassiveLootFrame()
        {
            int xStart = Math.Clamp(bigX[0] - _referenceClientOriginX, 0, Math.Max(_clientWidth - 1, 0));
            int xEnd = Math.Clamp(bigX[1] - _referenceClientOriginX, 0, _clientWidth);
            int yStart = Math.Clamp(bigY[0] - _referenceClientOriginY, 0, Math.Max(_clientHeight - 1, 0));
            int yEnd = Math.Clamp(bigY[1] - _referenceClientOriginY, 0, _clientHeight);
            if (xStart >= xEnd || yStart >= yEnd)
            {
                return new LootPreScanResult(
                    LootPreScanOutcome.CaptureUnavailable, 0, 0, Rectangle.Empty,
                    "invalid scan geometry");
            }

            if (!CaptureScreen())
            {
                return new LootPreScanResult(
                    LootPreScanOutcome.CaptureUnavailable, 0, 0, Rectangle.Empty,
                    $"capture failed status={_lastCaptureResult.Status}");
            }

            if (_bitmap == null || _lastStableCaptureGeometry == null)
            {
                return new LootPreScanResult(
                    LootPreScanOutcome.CaptureUnavailable, 0, 0, Rectangle.Empty,
                    "capture frame unavailable");
            }

            var scanRegion = new Rectangle(xStart, yStart, xEnd - xStart, yEnd - yStart);
            var excludeZone = new Rectangle(
                ExcludeXMin - _referenceClientOriginX,
                ExcludeYMin - _referenceClientOriginY,
                ExcludeXMax - ExcludeXMin + 1,
                ExcludeYMax - ExcludeYMin + 1);

            try
            {
                return AnalyzeWhiteLootCandidatesCore(
                    _bitmap,
                    scanRegion,
                    excludeZone,
                    BotConstants.Loot.PassivePreScanMaxCandidateComponents,
                    ref _scanBuffer);
            }
            catch (Exception ex)
            {
                return new LootPreScanResult(
                    LootPreScanOutcome.CaptureUnavailable, 0, 0, Rectangle.Empty,
                    $"analysis failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Pure passive candidate classifier. It reuses the exact full-scan pixel masks,
        /// connected-component labeling, shape filters, character exclude zone and
        /// text-glyph filter, but never probes, moves the cursor or validates mouseover.
        /// </summary>
        internal static LootPreScanResult AnalyzeWhiteLootCandidates(
            Bitmap bitmap,
            Rectangle scanRegion,
            Rectangle? excludeZone,
            int maxCandidateComponents)
        {
            byte[] reusableBuffer = Array.Empty<byte>();
            return AnalyzeWhiteLootCandidatesCore(
                bitmap, scanRegion, excludeZone, maxCandidateComponents, ref reusableBuffer);
        }

        private static LootPreScanResult AnalyzeWhiteLootCandidatesCore(
            Bitmap bitmap,
            Rectangle scanRegion,
            Rectangle? excludeZone,
            int maxCandidateComponents,
            ref byte[] reusableBuffer)
        {
            ArgumentNullException.ThrowIfNull(bitmap);

            int xStart = Math.Clamp(scanRegion.Left, 0, bitmap.Width - 1);
            int xEnd = Math.Clamp(scanRegion.Right, 0, bitmap.Width);
            int yStart = Math.Clamp(scanRegion.Top, 0, bitmap.Height - 1);
            int yEnd = Math.Clamp(scanRegion.Bottom, 0, bitmap.Height);
            if (xStart >= xEnd || yStart >= yEnd)
            {
                return new LootPreScanResult(
                    LootPreScanOutcome.CaptureUnavailable, 0, 0, Rectangle.Empty,
                    "invalid scan geometry");
            }

            bool applyExcludeZone = excludeZone.HasValue && excludeZone.Value.Width > 0 &&
                                    excludeZone.Value.Height > 0;
            Rectangle exclude = excludeZone ?? Rectangle.Empty;
            var exactWhitePoints = new List<Point>();
            var shadedWhitePoints = new List<Point>();

            CollectBitmapTargetPoints(
                bitmap, xStart, xEnd, yStart, yEnd,
                applyExcludeZone, exclude.Left, exclude.Right - 1, exclude.Top, exclude.Bottom - 1,
                pinkOnly: false, exactWhitePoints, ref reusableBuffer, out _, out _,
                shadedWhiteOnly: false);
            CollectBitmapTargetPoints(
                bitmap, xStart, xEnd, yStart, yEnd,
                applyExcludeZone, exclude.Left, exclude.Right - 1, exclude.Top, exclude.Bottom - 1,
                pinkOnly: false, shadedWhitePoints, ref reusableBuffer, out _, out _,
                shadedWhiteOnly: true);

            int whitePixels = exactWhitePoints.Count + shadedWhitePoints.Count;
            var candidates = new List<WhiteComponent>();
            foreach (WhiteComponent component in LabelWhiteComponents(exactWhitePoints))
            {
                if (IsLegacyWhiteSparkleCandidate(component))
                    candidates.Add(component);
            }
            foreach (WhiteComponent component in LabelWhiteComponents(shadedWhitePoints))
            {
                if (IsShadedWhiteLootCandidate(component))
                    candidates.Add(component);
            }

            HashSet<WhiteComponent> textGlyphs = FindTextGlyphs(candidates);
            if (textGlyphs.Count > 0)
                candidates.RemoveAll(textGlyphs.Contains);

            if (candidates.Count == 0)
            {
                return new LootPreScanResult(
                    LootPreScanOutcome.NoCandidate, 0, whitePixels, Rectangle.Empty,
                    $"scanRegion={FormatRectangle(scanRegion)}");
            }

            int left = candidates.Min(c => c.XMin);
            int top = candidates.Min(c => c.YMin);
            int right = candidates.Max(c => c.XMax);
            int bottom = candidates.Max(c => c.YMax);
            var bounds = Rectangle.FromLTRB(left, top, right + 1, bottom + 1);

            if (candidates.Count > maxCandidateComponents)
            {
                return new LootPreScanResult(
                    LootPreScanOutcome.NoCandidate, candidates.Count, whitePixels, bounds,
                    "candidate count looks like UI/effect clutter");
            }

            return new LootPreScanResult(
                LootPreScanOutcome.PotentialCandidate, candidates.Count, whitePixels, bounds,
                "plausible white-loot candidates");
        }

        private (float X, float Y) GetPlayerPositionForSuppression()
        {
            var (x, y, success) = _memoryService.GetPlayerPosition();
            return success ? (x, y) : (0f, 0f);
        }

        private bool IsCandidateSuppressed(Rectangle bounds, DateTime now)
            => _candidateSuppression.IsSuppressed(bounds, now);

        private static bool IsSameCandidateRegion(Rectangle a, Rectangle b)
        {
            if (a.IsEmpty || b.IsEmpty)
                return false;

            float ax = a.Left + a.Width / 2f;
            float ay = a.Top + a.Height / 2f;
            float bx = b.Left + b.Width / 2f;
            float by = b.Top + b.Height / 2f;
            float dx = ax - bx;
            float dy = ay - by;
            float tolerance = BotConstants.Loot.PassivePreScanSuppressionRegionTolerancePx;
            return dx * dx + dy * dy <= tolerance * tolerance;
        }

        private void RecordTravelCandidateVerificationResult(long requestId, bool unavailable)
        {
            TravelCandidateContext? context;
            lock (_cameraCursorOwnershipGate)
            {
                context = _activeTravelCandidate;
                if (context == null || context.RequestId != requestId)
                    return;
                _activeTravelCandidate = null;
            }

            if (context.ItemCollected)
            {
                _candidateSuppression.Clear();
                return;
            }

            (int failureCount, int durationMs) = _candidateSuppression.RecordFailure(
                context.Bounds, DateTime.UtcNow);

            _log($"[LootVerify] Candidate not confirmed request={requestId} " +
                 $"unavailable={unavailable} — suppressing the same screen region for {durationMs}ms " +
                 $"(failure {failureCount}).");
        }

        private void LogPassivePreScan(string message)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastPassivePreScanLogAt).TotalMilliseconds < 1000)
                return;
            _lastPassivePreScanLogAt = now;
            _log(message);
        }

        private void LogPassivePreScanFailure(string detail)
        {
            DateTime now = DateTime.UtcNow;
            bool changed = !string.Equals(detail, _lastPassivePreScanFailure, StringComparison.Ordinal);
            if (!changed && (now - _lastPassivePreScanFailureLogAt).TotalSeconds < 5)
                return;
            _lastPassivePreScanFailure = detail;
            _lastPassivePreScanFailureLogAt = now;
            _log($"[LootPreScan] CaptureUnavailable detail={detail} — navigation continues.");
        }

        /// <summary>
        /// Starts the fresh area-loot + verified scan cycle for the active mandatory
        /// request. A request created while a scan/collection is already running is
        /// never satisfied by that older pass: it starts a fresh cycle afterwards.
        /// </summary>
        private void ServiceMandatoryLootRequest(bool isMobSelected)
        {
            MandatoryLootRequest? request;
            lock (_cameraCursorOwnershipGate)
            {
                // Combat-owned recovery movement currently walks the character under this
                // obligation. It stays Deferred and no stationary cycle/camera may start —
                // the loot zoom must never race the recovery walk.
                if (_activeMandatoryLootRequest != null &&
                    _combatRecoveryMovementRequestId == _activeMandatoryLootRequest.Id)
                    return;

                request = _activeMandatoryLootRequest;
            }

            if (request == null || !IsMandatoryRequestActiveLocked(request))
                return;

            if (PinkLootOnlyMode)
            {
                CancelMandatoryLootRequest(request.Id, "mode changed to MoveAndAttack (SOD/SOP only)");
                return;
            }

            if (isMobSelected)
            {
                DeferMandatoryLootRequest(request.Id, "combat target selected");
                return;
            }

            if (!request.NavigationHeld &&
                (DateTime.UtcNow - request.CreatedAtUtc).TotalMilliseconds <
                BotConstants.Loot.PassivePreScanNavigationStopGraceMs)
            {
                // Give MovementSystem one tick to release W before the camera moves.
                return;
            }

            // Never interrupt an in-flight capture/collection: the current pass is
            // simply not tagged with this request, so it cannot satisfy the obligation.
            lock (_cameraCursorOwnershipGate)
            {
                if (_isCollecting || _lootState == LootMachineState.Scan ||
                    _lootState == LootMachineState.PinkScan)
                    return;

                request = _activeMandatoryLootRequest;
                if (request == null || !IsMandatoryRequestActiveLocked(request) ||
                    _combatRecoveryMovementRequestId == request.Id)
                    return;

                bool needsFreshStart = request.NeedsFreshCycle ||
                                       request.State != LootRequestState.Running ||
                                       _lootState == LootMachineState.Idle ||
                                       _lootState == LootMachineState.ScanComplete ||
                                       _lootState == LootMachineState.PostMobTab;
                if (!needsFreshStart)
                    return;

                request.State = LootRequestState.Running;
                request.NeedsFreshCycle = false;
                request.Detail = "fresh mandatory loot cycle running";
                request.UpdatedAtUtc = DateTime.UtcNow;
                _lootState = LootMachineState.AreaLoot;
                _nextActionTime = DateTime.UtcNow;
                _consecutiveEmptySpacePresses = 0;
                _lastKillAt = DateTime.UtcNow;
                _log($"[LootVerify] Starting stationary verification request={request.Id} " +
                     $"reason={(request.Reason == LootBarrierReason.PostKill ? "PostKillMandatory" : "TravelCandidateVerification")} " +
                     "— area loot starting.");
            }
        }

        private MandatoryLootRequest? GetRunningMandatoryRequestForScan()
        {
            lock (_cameraCursorOwnershipGate)
            {
                if (_activeMandatoryLootRequest != null &&
                    _activeMandatoryLootRequest.State == LootRequestState.Running &&
                    !_activeMandatoryLootRequest.NeedsFreshCycle)
                    return _activeMandatoryLootRequest;
            }
            return null;
        }

        private void DeferMandatoryLootRequest(long requestId, string reason)
        {
            lock (_cameraCursorOwnershipGate)
            {
                if (_activeMandatoryLootRequest == null ||
                    _activeMandatoryLootRequest.Id != requestId ||
                    !IsMandatoryRequestActiveLocked(_activeMandatoryLootRequest))
                    return;

                MandatoryLootRequest request = _activeMandatoryLootRequest;
                bool changed = request.State != LootRequestState.Deferred;
                request.State = LootRequestState.Deferred;
                request.NeedsFreshCycle = true;
                request.Detail = reason ?? "loot deferred";
                request.UpdatedAtUtc = DateTime.UtcNow;
                ReleaseLootScanCameraLocked($"request {requestId} deferred");
                if (_lootState != LootMachineState.PinkScan)
                    _lootState = LootMachineState.Idle;
                _consecutiveEmptySpacePresses = 0;
                IsLootingActive = false;
                if (changed)
                    _log($"[LootBarrier] Deferred request={requestId} reason={request.Detail} — obligation preserved.");
            }
        }

        private void RecordMandatoryItemCollected(long requestId)
        {
            lock (_cameraCursorOwnershipGate)
            {
                if (_activeMandatoryLootRequest != null &&
                    _activeMandatoryLootRequest.Id == requestId &&
                    _activeMandatoryLootRequest.State == LootRequestState.Running)
                {
                    _activeMandatoryLootRequest.ItemsCollected++;
                    _activeMandatoryLootRequest.CaptureAttempts = 0;
                    _activeMandatoryLootRequest.CameraAttempts = 0;
                    _activeMandatoryLootRequest.UpdatedAtUtc = DateTime.UtcNow;
                    if (_activeTravelCandidate != null &&
                        _activeTravelCandidate.RequestId == requestId)
                        _activeTravelCandidate.ItemCollected = true;
                    _candidateSuppression.Clear();
                }
            }
        }

        private bool IncrementMandatoryCaptureAttempt(long requestId, int maxAttempts, out int attempt)
        {
            lock (_cameraCursorOwnershipGate)
            {
                attempt = 0;
                if (_activeMandatoryLootRequest == null ||
                    _activeMandatoryLootRequest.Id != requestId ||
                    _activeMandatoryLootRequest.State != LootRequestState.Running)
                    return false;

                attempt = ++_activeMandatoryLootRequest.CaptureAttempts;
                _activeMandatoryLootRequest.UpdatedAtUtc = DateTime.UtcNow;
                return attempt < maxAttempts;
            }
        }

        private bool IncrementMandatoryCameraAttempt(long requestId, int maxAttempts, out int attempt)
        {
            lock (_cameraCursorOwnershipGate)
            {
                attempt = 0;
                if (_activeMandatoryLootRequest == null ||
                    _activeMandatoryLootRequest.Id != requestId ||
                    _activeMandatoryLootRequest.State != LootRequestState.Running)
                    return false;

                attempt = ++_activeMandatoryLootRequest.CameraAttempts;
                _activeMandatoryLootRequest.UpdatedAtUtc = DateTime.UtcNow;
                return attempt < maxAttempts;
            }
        }

        private void CompleteMandatoryLootRequest(long requestId, LootScanPassOutcome outcome)
        {
            bool completed = false;
            bool travelCandidate = false;
            int itemsCollected = 0;
            lock (_cameraCursorOwnershipGate)
            {
                if (_activeMandatoryLootRequest != null &&
                    _activeMandatoryLootRequest.Id == requestId &&
                    _activeMandatoryLootRequest.State == LootRequestState.Running &&
                    outcome.IsValidComplete)
                {
                    MandatoryLootRequest request = _activeMandatoryLootRequest;
                    travelCandidate = request.Reason == LootBarrierReason.TravelCandidateVerification;
                    itemsCollected = request.ItemsCollected;
                    request.State = LootRequestState.Completed;
                    request.CameraVerified = true;
                    request.Detail = travelCandidate
                        ? "travel candidate verified empty (SmallScan + BigScan)"
                        : "verified full scan pass (SmallScan + BigScan)";
                    request.UpdatedAtUtc = DateTime.UtcNow;
                    _activeMandatoryLootRequest = null;
                    if (_combatRecoveryMovementRequestId == requestId)
                        _combatRecoveryMovementRequestId = 0;
                    IsLootingActive = false;
                    if (!travelCandidate)
                        _lastKillAt = DateTime.MinValue;
                    _consecutiveEmptySpacePresses = 0;

                    // Only a real post-kill scan keeps the legacy ScanComplete hold.
                    // A travel verification immediately returns to the passive pre-scan.
                    _lootState = travelCandidate ? LootMachineState.Idle : LootMachineState.ScanComplete;
                    _scanCompleteUntil = travelCandidate
                        ? DateTime.UtcNow
                        : DateTime.UtcNow.AddMilliseconds(BotConstants.Delays.LootScanCompleteHoldMs);
                    _nextPassivePreScanAt = DateTime.UtcNow.AddMilliseconds(
                        BotConstants.Loot.PassivePreScanIntervalMs);
                    ReleaseLootScanCameraLocked($"request {requestId} completed");
                    completed = true;
                }
            }

            if (!completed)
                return;

            if (travelCandidate)
            {
                RecordTravelCandidateVerificationResult(requestId, unavailable: false);
                _log($"[LootVerify] No confirmed item request={requestId} — navigation released.");
                return;
            }

            _log($"[LootBarrier] PostKill completed request={requestId} items={itemsCollected} " +
                 "cameraVerified=True.");
            _log("[Loot] Full scan completed: valid empty pass");
        }

        /// <summary>
        /// Travel verification could not run (capture/camera unavailable after bounded
        /// retries). This is NOT an empty scan: the candidate is suppressed for a
        /// bounded window and navigation is released. Mandatory post-kill requests
        /// never use this path — they fail closed instead.
        /// </summary>
        private void MarkTravelVerificationUnavailable(long requestId, string reason)
        {
            bool marked = false;
            lock (_cameraCursorOwnershipGate)
            {
                if (_activeMandatoryLootRequest != null &&
                    _activeMandatoryLootRequest.Id == requestId &&
                    _activeMandatoryLootRequest.Reason == LootBarrierReason.TravelCandidateVerification &&
                    IsMandatoryRequestActiveLocked(_activeMandatoryLootRequest))
                {
                    MandatoryLootRequest request = _activeMandatoryLootRequest;
                    request.State = LootRequestState.Unavailable;
                    request.Detail = reason ?? "travel verification unavailable";
                    request.UpdatedAtUtc = DateTime.UtcNow;
                    _activeMandatoryLootRequest = null;
                    if (_combatRecoveryMovementRequestId == requestId)
                        _combatRecoveryMovementRequestId = 0;
                    _lootState = LootMachineState.Idle;
                    _consecutiveEmptySpacePresses = 0;
                    IsLootingActive = false;
                    _nextPassivePreScanAt = DateTime.UtcNow.AddMilliseconds(
                        BotConstants.Loot.PassivePreScanIntervalMs);
                    ReleaseLootScanCameraLocked($"request {requestId} unavailable");
                    marked = true;
                }
            }

            if (!marked)
                return;

            RecordTravelCandidateVerificationResult(requestId, unavailable: true);
            _log($"[LootVerify] Verification unavailable request={requestId} reason={reason} — " +
                 "not treated as an empty result; navigation released.");
        }

        private void FailMandatoryLootRequest(long requestId, string reason)
        {
            lock (_cameraCursorOwnershipGate)
            {
                if (_activeMandatoryLootRequest == null ||
                    _activeMandatoryLootRequest.Id != requestId)
                    return;

                MandatoryLootRequest request = _activeMandatoryLootRequest;
                request.State = LootRequestState.Failed;
                request.Detail = reason ?? "loot operation failed";
                request.UpdatedAtUtc = DateTime.UtcNow;
                _activeMandatoryLootRequest = null;
                if (_combatRecoveryMovementRequestId == requestId)
                    _combatRecoveryMovementRequestId = 0;
                _lootState = LootMachineState.Idle;
                _consecutiveEmptySpacePresses = 0;
                IsLootingActive = false;
                ReleaseLootScanCameraLocked($"request {requestId} failed");
                _log($"[LootBarrier] Failed request={requestId} reason={request.Detail} — " +
                     "no empty result was recorded; navigation stays blocked.");
            }
        }

        private void LogMandatoryCameraFailure(long requestId, string detail)
        {
            if (requestId > 0)
                _log($"[LootCamera] Verification failed request={requestId}: {detail}");
            else
                _log($"[LootCamera] Verification failed: {detail}");
        }

        /// <summary>The normal white-loot scan always uses the configured constant.</summary>
        internal static short NormalLootScanDistance => BotConstants.Camera.LootScanDistance;

        /// <summary>
        /// Pure capture-authorization rule: the requested and actual camera values must
        /// match, stabilization must have elapsed and loot must still own the camera.
        /// </summary>
        internal static bool IsLootCaptureAuthorized(
            short requestedDistance,
            short actualDistance,
            short requestedVerticalLock,
            short actualVerticalLock,
            bool stabilizationElapsed,
            bool ownershipHeld)
            => ownershipHeld &&
               stabilizationElapsed &&
               actualDistance == requestedDistance &&
               actualVerticalLock == requestedVerticalLock;

        /// <summary>
        /// Combines the two normal scan regions into one pass result. An empty small
        /// region alone is never a complete pass, and any invalid big-region result
        /// cannot be ignored because the small region was empty.
        /// </summary>
        internal static LootScanPassResult CombineNormalLootScanResults(
            LootScanPassResult small,
            LootScanPassResult? big,
            bool cameraVerified)
        {
            if (!cameraVerified)
                return LootScanPassResult.CameraVerificationFailed;
            if (small == LootScanPassResult.Collected)
                return LootScanPassResult.Collected;
            if (small != LootScanPassResult.NoItem)
                return small;
            if (!big.HasValue)
                return LootScanPassResult.Incomplete;
            if (big.Value == LootScanPassResult.Collected)
                return LootScanPassResult.Collected;
            if (big.Value != LootScanPassResult.NoItem)
                return big.Value;
            return LootScanPassResult.NoItem;
        }

        /// <summary>
        /// Acquires exclusive ownership of the loot camera and applies + verifies the
        /// configured scan distance and vertical lock. Retries are bounded; a failure
        /// never authorizes a scan.
        /// </summary>
        private bool EnsureLootScanCamera(out string detail)
        {
            short requestedDistance = NormalLootScanDistance;
            short requestedVerticalLock = BotConstants.Camera.DefaultVerticalLock;
            detail = string.Empty;

            long requestId = _activeMandatoryLootRequest?.Id ?? 0;
            lock (_cameraCursorOwnershipGate)
            {
                if (!_lootScanCameraOwned)
                    _log($"[LootCamera] Acquiring camera for loot scan (request={requestId}).");
                _lootScanCameraOwned = true;
                _lootCameraReady = false;
                _lootCameraSettledAtUtc = DateTime.MinValue;
            }

            const int maxAttempts = BotConstants.Loot.BarrierCameraVerificationAttempts;
            short actualDistance = 0;
            short actualVerticalLock = 0;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                lock (_cameraCursorOwnershipGate)
                {
                    _memoryService.SetCameraDistance(requestedDistance);
                    _memoryService.SetCameraVerticalLock(requestedVerticalLock);
                }

                bool distanceRead = _memoryService.TryGetCameraDistance(out actualDistance);
                bool lockRead = _memoryService.TryGetCameraVerticalLock(out actualVerticalLock);
                bool matches = distanceRead && lockRead &&
                    actualDistance == requestedDistance &&
                    actualVerticalLock == requestedVerticalLock;

                _log($"[LootCamera] Requested distance={requestedDistance} actual={(distanceRead ? actualDistance.ToString() : "unreadable")}; " +
                     $"requested verticalLock={requestedVerticalLock} actual={(lockRead ? actualVerticalLock.ToString() : "unreadable")} " +
                     $"(attempt {attempt}/{maxAttempts}, request={requestId}).");

                if (matches)
                    break;

                if (attempt >= maxAttempts)
                {
                    detail = $"camera readback mismatch after {maxAttempts} attempts " +
                             $"(distance requested={requestedDistance} actual={(distanceRead ? actualDistance.ToString() : "unreadable")}, " +
                             $"verticalLock requested={requestedVerticalLock} actual={(lockRead ? actualVerticalLock.ToString() : "unreadable")})";
                    LogMandatoryCameraFailure(requestId, detail);
                    return false;
                }

                _log($"[LootCamera] Readback mismatch — reapplying scan camera (attempt {attempt}/{maxAttempts}, request={requestId}).");
                if (_scanToken.WaitHandle.WaitOne(BotConstants.Camera.LootScanCameraSettleMs))
                {
                    detail = "cancelled while retrying the loot camera";
                    return false;
                }
            }

            // Let the client render the verified view. The capture path re-reads the
            // camera immediately before the frame is taken.
            if (_scanToken.WaitHandle.WaitOne(BotConstants.Camera.LootScanCameraSettleMs))
            {
                detail = "cancelled during loot-camera stabilization";
                return false;
            }

            lock (_cameraCursorOwnershipGate)
            {
                _lootCameraReady = true;
                _lootCameraSettledAtUtc = DateTime.UtcNow;
            }

            if (!VerifyLootScanCamera(out actualDistance, out actualVerticalLock, out string verifyDetail))
            {
                detail = verifyDetail;
                LogMandatoryCameraFailure(requestId, detail);
                return false;
            }

            _log($"[LootCamera] Camera settled — distance={actualDistance}, verticalLock={actualVerticalLock}, request={requestId}.");
            return true;
        }

        /// <summary>Reads the live camera and applies the mandatory capture gate.</summary>
        private bool VerifyLootScanCamera(
            out short actualDistance,
            out short actualVerticalLock,
            out string detail)
        {
            short requestedDistance = NormalLootScanDistance;
            short requestedVerticalLock = BotConstants.Camera.DefaultVerticalLock;

            bool distanceRead = _memoryService.TryGetCameraDistance(out actualDistance);
            bool lockRead = _memoryService.TryGetCameraVerticalLock(out actualVerticalLock);

            bool ownershipHeld;
            bool stabilizationElapsed;
            lock (_cameraCursorOwnershipGate)
            {
                ownershipHeld = _lootScanCameraOwned;
                stabilizationElapsed = _lootCameraReady &&
                                       DateTime.UtcNow >= _lootCameraSettledAtUtc;
            }

            bool authorized = distanceRead && lockRead &&
                IsLootCaptureAuthorized(
                    requestedDistance, actualDistance,
                    requestedVerticalLock, actualVerticalLock,
                    stabilizationElapsed, ownershipHeld);

            detail = authorized
                ? string.Empty
                : $"requested distance={requestedDistance} actual={(distanceRead ? actualDistance.ToString() : "unreadable")}; " +
                  $"requested verticalLock={requestedVerticalLock} actual={(lockRead ? actualVerticalLock.ToString() : "unreadable")}; " +
                  $"stabilized={stabilizationElapsed}; owned={ownershipHeld}";
            return authorized;
        }

        private void ReleaseLootScanCameraLocked(string reason)
        {
            if (!_lootScanCameraOwned && !_lootCameraReady)
                return;

            _lootScanCameraOwned = false;
            _lootCameraReady = false;
            _lootCameraSettledAtUtc = DateTime.MinValue;
            _log($"[LootCamera] Camera ownership released — {reason}.");
        }

        private LootScanPassOutcome PixelScan()
        {
            if (!EnsureLootScanCamera(out string cameraDetail))
            {
                return new LootScanPassOutcome(
                    LootScanPassResult.CameraVerificationFailed,
                    LootScanPassResult.CameraVerificationFailed,
                    null,
                    CameraVerified: false,
                    cameraDetail);
            }

            _log("[Loot] SmallScan started");
            LootScanPassResult small = ScanRegion(
                smallX, smallY, "SmallScan", verifyLootCamera: true);
            _log($"[Loot] SmallScan completed status={small}");
            if (small == LootScanPassResult.Collected)
            {
                return new LootScanPassOutcome(
                    LootScanPassResult.Collected, small, null, CameraVerified: true, "small-region item collected");
            }
            if (small != LootScanPassResult.NoItem)
            {
                return new LootScanPassOutcome(
                    small, small, null,
                    CameraVerified: small != LootScanPassResult.CameraVerificationFailed,
                    $"SmallScan status={small}");
            }

            _log("[Loot] BigScan started");
            LootScanPassResult big = ScanRegion(
                bigX, bigY, "BigScan", verifyLootCamera: true);
            _log($"[Loot] BigScan completed status={big}");

            bool cameraVerified = big != LootScanPassResult.CameraVerificationFailed &&
                                  small != LootScanPassResult.CameraVerificationFailed;
            LootScanPassResult result = CombineNormalLootScanResults(small, big, cameraVerified);
            return new LootScanPassOutcome(
                result, small, big, cameraVerified,
                $"SmallScan={small}, BigScan={big}");
        }

        /// <summary>
        /// SOD/SOP-only pixel scan (MoveAndAttack post-kill): a single pass over the
        /// WHOLE captured game window looking only for pink pixels. It ignores the
        /// BigScan rectangle and the character exclude zone, because a pink drop can
        /// land anywhere on screen (including under the player after a kill). There
        /// is no small-region pass and no white normal-loot detection. Every candidate
        /// pixel is probed in one continuous mouse sweep; the caller repeats a fresh
        /// sweep after each successful pickup so remaining drops are still collected,
        /// and a sweep that collects nothing ends the post-kill check.
        /// </summary>
        private LootScanPassResult PinkPixelScan(string scanReason)
        {
            return ScanRegion(bigX, bigY, "BigScan-Pink", pinkOnly: true, scanReason: scanReason);
        }

        /// <summary>What the loot loop should do with a pending background rescan this tick.</summary>
        internal enum PinkRescanAction { None, Wait, Run, Expire }

        /// <summary>
        /// Pure decision for a coalesced background scan. A late pass is never expired:
        /// pending kill work remains actionable until completed or explicitly cancelled.
        /// </summary>
        internal static PinkRescanAction DecidePinkRescan(
            bool pinkLootOnly, bool pinkPassActive, DateTime dueAt, DateTime now, TimeSpan expireAfter)
        {
            if (dueAt == DateTime.MinValue)
                return PinkRescanAction.None;
            if (!pinkLootOnly)
                return PinkRescanAction.Expire;
            if (pinkPassActive || now < dueAt)
                return PinkRescanAction.Wait;
            return PinkRescanAction.Run;
        }

        /// <summary>
        /// Runs exactly one background SOD/SOP pass. The scheduler ticket identifies
        /// which kill generation the capture can cover; later kills stay pending.
        /// Camera restoration and cursor ownership are released in every exit path.
        /// </summary>
        private PinkRescanPassOutcome RunPinkRescan(PinkRescanTicket ticket)
        {
            short previousCameraDistance = 0;
            bool havePreviousCameraDistance = false;
            _log($"[Loot] SOD/SOP background rescan starting for kill generation {ticket.Generation}; later kills remain queued.");
            try
            {
                lock (_cameraCursorOwnershipGate)
                {
                    _pinkRescanActive = true;
                    havePreviousCameraDistance = _memoryService.TryGetCameraDistance(out previousCameraDistance);
                    if (!havePreviousCameraDistance)
                    {
                        previousCameraDistance = BotConstants.Camera.DefaultDistanceLock;
                        havePreviousCameraDistance = true;
                        _log("[LootCamera] Previous camera distance unreadable; will restore the default lock after the rescan.");
                    }
                    _memoryService.SetCameraDistance(BotConstants.Camera.SodSopLootScanDistance);
                }

                if (_scanToken.IsCancellationRequested || !PinkLootOnlyMode)
                    return PinkRescanPassOutcome.Cancelled;
                if (_scanToken.WaitHandle.WaitOne(BotConstants.Delays.SodSopCameraSettleMs))
                    return PinkRescanPassOutcome.Cancelled;

                LootScanPassResult result = PinkPixelScan("background");
                return result switch
                {
                    LootScanPassResult.Collected => PinkRescanPassOutcome.ItemCollected,
                    LootScanPassResult.NoItem => PinkRescanPassOutcome.NoItem,
                    LootScanPassResult.CaptureFailed => PinkRescanPassOutcome.CaptureFailed,
                    _ => PinkRescanPassOutcome.Cancelled
                };
            }
            catch (Exception ex)
            {
                _log($"[Loot] SOD/SOP background scan failed: {ex.Message}");
                return PinkRescanPassOutcome.CaptureFailed;
            }
            finally
            {
                lock (_cameraCursorOwnershipGate)
                {
                    if (havePreviousCameraDistance &&
                        (!_memoryService.TryGetCameraDistance(out short currentDistance) ||
                         currentDistance == BotConstants.Camera.SodSopLootScanDistance))
                    {
                        RestoreCameraDistance(previousCameraDistance);
                    }
                    _pinkRescanActive = false;
                }
            }
        }

        // ── SOD/SOP hot-pink pixel classifier thresholds ──
        // The custom SOD/SOP scroll texture is shaded/anti-aliased, so on screen it
        // shows MANY pink shades (observed roughly R 239..255, G 21..109, B 148..255;
        // typical centre ≈ 255,34,175), not one literal RGB value. These thresholds
        // isolate that hot-pink/magenta color family: strong red, significant blue,
        // substantially weaker green and clear pink/magenta dominance. The absolute
        // minima let the darker shaded parts of the texture through; the
        // channel-difference minima reject pale whites/greys.
        private const int PinkMinR = 200;
        private const int PinkMinB = 120;
        private const int PinkMaxG = 150;
        private const int PinkMinRedOverGreen = 80;
        private const int PinkMinBlueOverGreen = 40;
        private const int PinkMinRedPlusBlue = 380;

        /// <summary>
        /// True when a pixel belongs to the SOD/SOP hot-pink color family. The
        /// custom scroll texture renders as a shaded gradient, so no exact RGB
        /// match can work: a pixel qualifies when red is strong, blue is
        /// significant, green is substantially weaker and the pixel has clear
        /// magenta/pink dominance (channel-difference + channel-sum minima).
        /// Plain integer arithmetic on the RGB channels — no exact equality, no
        /// hue/HSL/HSV conversion and no allocation, because the scan loop
        /// examines hundreds of thousands of pixels.
        /// </summary>
        internal static bool IsSodSopPinkPixel(Color pixelColor)
            => IsSodSopPinkPixel(pixelColor.R, pixelColor.G, pixelColor.B);

        /// <summary>
        /// Raw-channel overload of <see cref="IsSodSopPinkPixel(Color)"/> used by the
        /// bulk LockBits scan loop (no <see cref="Color"/> construction per pixel).
        /// </summary>
        internal static bool IsSodSopPinkPixel(int r, int g, int b)
        {
            return r >= PinkMinR &&
                   b >= PinkMinB &&
                   g <= PinkMaxG &&
                   r - g >= PinkMinRedOverGreen &&
                   b - g >= PinkMinBlueOverGreen &&
                   r + b >= PinkMinRedPlusBlue;
        }

        /// <summary>
        /// True when a pixel participates in the pink-only loot connected-component mask.
        /// The narrow artificial mob-marker magenta predicate is excluded FIRST, so a
        /// single marker ring, several touching rings or a wide merged/overlapping ring
        /// structure can never become a pink-loot component. Every other SOD/SOP
        /// hot-pink shade keeps the existing classifier behavior — SOD/SOP detection is
        /// not weakened globally.
        /// </summary>
        internal static bool IsPinkLootMaskPixel(Color pixelColor)
            => IsPinkLootMaskPixel(pixelColor.R, pixelColor.G, pixelColor.B);

        /// <summary>
        /// Raw-channel overload of <see cref="IsPinkLootMaskPixel(Color)"/> used by the
        /// bulk pixel scan.
        /// </summary>
        internal static bool IsPinkLootMaskPixel(int r, int g, int b)
            => IsSodSopPinkPixel(r, g, b) && !MobMarkerDetector.IsMagentaMarkerPixel(r, g, b);

        // White ground textures are modulated by scene lighting and distance fog.
        // Accept bright, low-chroma grays as shaded white while rejecting strongly
        // tinted pixels. Component size/density filtering below rejects isolated noise.
        private const int WhiteLootMinChannel = 145;
        private const int WhiteLootMaxChannelSpread = 36;

        internal static bool IsShadedWhiteLootPixel(Color pixelColor)
            => IsShadedWhiteLootPixel(pixelColor.R, pixelColor.G, pixelColor.B);

        internal static bool IsShadedWhiteLootPixel(int r, int g, int b)
        {
            int min = Math.Min(r, Math.Min(g, b));
            int max = Math.Max(r, Math.Max(g, b));
            return min >= WhiteLootMinChannel && max - min <= WhiteLootMaxChannelSpread;
        }

        /// <summary>
        /// Analyzes a supplied frame with the exact Phase A reader, pink mask, connected
        /// component labeler and production candidate filters. It performs no game input.
        /// </summary>
        internal static PinkFrameAnalysisResult AnalyzePinkFrame(Bitmap bitmap)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            var targetPoints = new List<Point>();
            PixelCollectionStats stats = CollectTargetPointsFromBitmap(bitmap, pinkOnly: true, targetPoints);
            return AnalyzePinkMask(bitmap, targetPoints, stats);
        }

        private static PinkFrameAnalysisResult AnalyzePinkMask(
            Bitmap bitmap,
            List<Point> maskPoints,
            PixelCollectionStats stats)
        {
            List<WhiteComponent> components = LabelWhiteComponents(maskPoints);
            var candidates = new List<WhiteComponent>();
            var candidateDetails = new List<PinkComponentSummary>(MaxDiagnosticPinkComponentDetails);
            var rejectedDetails = new List<PinkComponentSummary>(MaxDiagnosticPinkComponentDetails);
            int markerRings = 0;
            int tinyComponents = 0;
            int rejected = 0;
            int probeCount = 0;

            foreach (WhiteComponent component in components)
            {
                bool candidate = IsPinkLootCandidate(component);
                if (candidate && IsMobMarkerRingComponent(component, bitmap))
                {
                    markerRings++;
                    candidate = false;
                }

                if (candidate)
                {
                    candidates.Add(component);
                    probeCount += component.Area; // Pink mode probes every component pixel.
                }
                else
                {
                    rejected++;
                    if (component.Width < BotConstants.Loot.PinkMinCandidateWidth ||
                        component.Height < BotConstants.Loot.PinkMinCandidateHeight)
                        tinyComponents++;
                }

                List<PinkComponentSummary> destination = candidate ? candidateDetails : rejectedDetails;
                if (destination.Count < MaxDiagnosticPinkComponentDetails)
                {
                    Rectangle bounds = Rectangle.FromLTRB(
                        component.XMin, component.YMin, component.XMax + 1, component.YMax + 1);
                    destination.Add(new PinkComponentSummary(
                        bounds,
                        component.Area,
                        component.Area / (float)Math.Max(component.Width * component.Height, 1),
                        candidate,
                        candidate
                            ? component.Points.Take(MaxDiagnosticPinkComponentDetails).ToArray()
                            : Array.Empty<Point>()));
                }
            }

            return new PinkFrameAnalysisResult
            {
                Width = bitmap.Width,
                Height = bitmap.Height,
                TotalPixelsExamined = stats.PixelsExamined,
                RawPinkPixels = stats.RawPinkPixels,
                MarkerPixelsRejected = stats.MarkerPixelsRejected,
                PinkPixels = stats.MaskPixels,
                Components = components.Count,
                LootCandidates = candidates.Count,
                SizeOrShapeRejectedComponents = rejected,
                MobMarkerRings = markerRings,
                TinyComponents = tinyComponents,
                FinalProbeCount = probeCount,
                CandidateDetails = candidateDetails
                    .Concat(rejectedDetails.Take(Math.Max(0, MaxDiagnosticPinkComponentDetails - candidateDetails.Count)))
                    .ToArray(),
                CandidateComponents = candidates,
                AllComponents = components
            };
        }

        /// <summary>
        /// Captures the frame used by one scan region. When <paramref name="verifyLootCamera"/>"
        /// is set, the camera is re-read under the ownership gate immediately before the
        /// capture so no movement/retarget writer can replace the verified view between
        /// the check and the frame grab.
        /// </summary>
        private bool CaptureScanFrame(
            bool verifyLootCamera,
            string regionName,
            out LootScanPassResult failureResult)
        {
            failureResult = LootScanPassResult.CaptureFailed;

            if (verifyLootCamera)
            {
                lock (_cameraCursorOwnershipGate)
                {
                    if (!VerifyLootScanCamera(out short actualDistance, out short actualVerticalLock, out string detail))
                    {
                        _log($"[LootCamera] {regionName}: capture refused — camera not verified ({detail}).");
                        failureResult = LootScanPassResult.CameraVerificationFailed;
                        return false;
                    }

                    _log($"[LootCamera] {regionName}: capture authorized — distance={actualDistance}, " +
                         $"verticalLock={actualVerticalLock}.");

                    if (!CaptureScreen())
                    {
                        failureResult = LootScanPassResult.CaptureFailed;
                        return false;
                    }

                    return true;
                }
            }

            if (!CaptureScreen())
            {
                failureResult = LootScanPassResult.CaptureFailed;
                return false;
            }

            return true;
        }

        private LootScanPassResult ScanRegion(
            int[] xRange, int[] yRange, string regionName,
            bool pinkOnly = false, string scanReason = "white-loot",
            bool verifyLootCamera = false)
        {
            string pixelNoun = pinkOnly ? "pink" : "white";

            // ── Loot diagnostics ──
            // Clone the exact frame that is scanned and save it (annotated) to
            // LootSSFolder once the region pass finishes, so loot detection problems
            // can be diagnosed from what the bot actually saw. The clone MUST be taken
            // right after CaptureScreen(): _bitmap is reused by every capture
            // (including the post-collection refresh) and would otherwise be gone.
            Bitmap? diagnosticFrame = null;
            var diagnosticWhiteHits = new List<Point>();
            // The detection mask and the cursor-probe positions are separate datasets:
            // the annotated frame must never present the handful of mouse positions as
            // if they were the complete detected white-pixel mask.
            var diagnosticDetectedWhitePixels = new List<Point>();
            var diagnosticCandidatePixels = new List<Point>();
            var diagnosticCandidateBounds = new List<Rectangle>();
            Point? diagnosticSuccessProbe = null;
            Rectangle diagnosticScanRegion = Rectangle.Empty;
            Rectangle diagnosticExcludeZone = Rectangle.Empty;
            int diagnosticWhiteCount = 0;
            int diagnosticExactWhiteCount = 0;
            int diagnosticShadedWhiteCount = 0;
            int diagnosticCandidatePixelCount = 0;
            int diagnosticComponentCount = 0;
            int diagnosticCandidateCount = 0;
            int diagnosticPreviouslyProbedSkipped = 0;
            string diagnosticOutcome = "scan-error";
            LootClientGeometry? diagnosticGeometry = null;
            PixelFormat diagnosticPixelFormat = PixelFormat.Undefined;
            int diagnosticStride = 0;
            PixelCollectionStats pixelStats = default;
            PinkFrameAnalysisResult? pinkAnalysis = null;
            int actualCursorProbes = 0;
            DateTime scanStartedUtc = DateTime.UtcNow;

            try
            {
                if (!CaptureScanFrame(verifyLootCamera, regionName, out LootScanPassResult captureFailure))
                {
                    diagnosticOutcome = captureFailure == LootScanPassResult.CameraVerificationFailed
                        ? "camera-verification-failed"
                        : "capture-failed";
                    if (pinkOnly && ScreenshotService.LootScanCaptureEnabled)
                        LogPinkCaptureFailureDiagnostics(scanStartedUtc, scanReason, _lastCaptureResult);
                    return captureFailure;
                }
                if (_bitmap == null || _clientWidth <= 0 || _clientHeight <= 0 ||
                    _lastStableCaptureGeometry == null)
                {
                    diagnosticOutcome = "capture-failed";
                    if (pinkOnly && ScreenshotService.LootScanCaptureEnabled)
                        LogPinkCaptureFailureDiagnostics(scanStartedUtc, scanReason, _lastCaptureResult);
                    _log($"[Loot] {regionName}: capture failed; scan was not interpreted as empty.");
                    return LootScanPassResult.CaptureFailed;
                }

                diagnosticGeometry = _lastStableCaptureGeometry;
                diagnosticPixelFormat = _bitmap.PixelFormat;
                if (ScreenshotService.LootScanCaptureEnabled)
                    diagnosticFrame = pinkOnly
                        ? ScreenshotService.ClonePinkLootScanFrame(_bitmap)
                        : ScreenshotService.CloneLootScanFrame(_bitmap);

                // Check the full client frame for another player's faction marker on
                // every loot pixel-scan pass, regardless of the loot scan rectangle.
                ScanForOtherPlayerPixels();
                if (_scanToken.IsCancellationRequested)
                {
                    diagnosticOutcome = "cancelled-after-player-detection";
                    return LootScanPassResult.Cancelled;
                }

                // Convert hardcoded screen-absolute ranges → bitmap-local coordinates,
                // then clamp to actual bitmap dimensions as a safety net.
                int refX = _referenceClientOriginX;
                int refY = _referenceClientOriginY;

                int xStart = Math.Clamp(xRange[0] - refX, 0, _bitmap.Width - 1);
                int xEnd   = Math.Clamp(xRange[1] - refX, 0, _bitmap.Width);
                int yStart = Math.Clamp(yRange[0] - refY, 0, _bitmap.Height - 1);
                int yEnd   = Math.Clamp(yRange[1] - refY, 0, _bitmap.Height);

                // The SOD/SOP pink pass sweeps the WHOLE captured client area: a
                // drop can land anywhere on screen (a corner outside BigScan, or
                // under the character after a kill), so the BigScan rectangle does
                // not bound this pass. Override the clamped range with the full
                // bitmap; normal white-loot scans keep using their region.
                if (pinkOnly)
                {
                    xStart = 0;
                    xEnd = _bitmap.Width;
                    yStart = 0;
                    yEnd = _bitmap.Height;
                }

                // The character exclude zone is a white-loot optimisation only.
                // The pink SOD/SOP scan must see the WHOLE scan region — a drop
                // lying under the character after a kill would otherwise be
                // invisible — so the zone is not applied in pink mode.
                bool applyExcludeZone = !pinkOnly;

                // Also adjust exclude zone to bitmap-local coords.
                int exclXMin = ExcludeXMin - refX;
                int exclXMax = ExcludeXMax - refX;
                int exclYMin = ExcludeYMin - refY;
                int exclYMax = ExcludeYMax - refY;

                if (xStart >= xEnd || yStart >= yEnd)
                {
                    _log($"[Loot] {regionName}: clamped range is empty (out of client area); scan is invalid, not empty.");
                    diagnosticOutcome = "invalid-geometry";
                    return LootScanPassResult.InvalidGeometry;
                }

                // diagnosticFrame was cloned immediately after this successful capture,
                // before a later capture, annotation or cursor action can change the pixels.
                diagnosticScanRegion = new Rectangle(xStart, yStart, xEnd - xStart, yEnd - yStart);
                diagnosticExcludeZone = applyExcludeZone
                    ? new Rectangle(exclXMin, exclYMin, exclXMax - exclXMin + 1, exclYMax - exclYMin + 1)
                    : Rectangle.Empty;

                int scanPixels = (xEnd - xStart) * (yEnd - yStart);

                // Check once before starting the scan — if we're in city, abort
                // immediately. A mob being selected only aborts the scan outside
                // loot-priority mode; in priority mode the scan keeps running even
                // during the attack phase. During the scan the top-level Update()
                // will cancel on the next tick if the city is entered, so we don't
                // need to check on every pixel.
                if (ShouldAbortLoot())
                {
                    _log($"[Loot] {regionName}: city/combat detected before scan — aborting.");
                    diagnosticOutcome = "city-abort";
                    ResetPlayerMarkerConfirmation();
                    return LootScanPassResult.Cancelled;
                }

                // ── Phase A: collect pixels without moving the cursor. Normal loot
                //    keeps its exact-white sparkle mask and adds a separate shaded-white
                //    mask; SOD/SOP keeps the existing pink-family mask with artificial
                //    marker magenta removed before labeling. Both normal masks are read
                //    from this captured frame; only concentrated shade components become
                //    new candidates. ──
                var targetPoints = new List<Point>();
                List<Point>? shadedWhitePoints = pinkOnly ? null : new List<Point>();
                pixelStats = CollectScanTargetPoints(
                    xStart, xEnd, yStart, yEnd,
                    applyExcludeZone, exclXMin, exclXMax, exclYMin, exclYMax,
                    pinkOnly, targetPoints,
                    out diagnosticPixelFormat,
                    out diagnosticStride);
                diagnosticExactWhiteCount = targetPoints.Count;
                diagnosticWhiteCount = diagnosticExactWhiteCount;
                if (!pinkOnly)
                {
                    CollectScanTargetPoints(
                        xStart, xEnd, yStart, yEnd,
                        applyExcludeZone, exclXMin, exclXMax, exclYMin, exclYMax,
                        false, shadedWhitePoints!,
                        out _, out _, shadedWhiteOnly: true);
                    diagnosticShadedWhiteCount = shadedWhitePoints!.Count;
                    diagnosticWhiteCount = diagnosticExactWhiteCount + diagnosticShadedWhiteCount;

                    if (ScreenshotService.LootScanCaptureEnabled)
                    {
                        diagnosticDetectedWhitePixels.AddRange(targetPoints);
                        diagnosticDetectedWhitePixels.AddRange(shadedWhitePoints!);
                    }
                }

                // ── Phase B: label 8-connected components and keep loot-shaped
                //    blobs. White mode preserves compact sparkles and adds dense large
                //    candidates; pink mode shares the AnalyzePinkFrame production path. ──
                List<WhiteComponent> components;
                var candidates = new List<WhiteComponent>();
                int filteredComponents;
                int tinyPinkComponents;
                int mobMarkerComponents;
                if (pinkOnly)
                {
                    pinkAnalysis = AnalyzePinkMask(_bitmap, targetPoints, pixelStats);
                    components = pinkAnalysis.AllComponents.ToList();
                    candidates.AddRange(pinkAnalysis.CandidateComponents);
                    filteredComponents = pinkAnalysis.SizeOrShapeRejectedComponents;
                    tinyPinkComponents = pinkAnalysis.TinyComponents;
                    mobMarkerComponents = pinkAnalysis.MobMarkerRings;

                    if (ScreenshotService.LootScanCaptureEnabled && diagnosticGeometry.HasValue)
                    {
                        if (diagnosticFrame != null)
                        {
                            string rawMetadata = BuildPinkFrameDiagnosticMetadata(
                                scanStartedUtc, scanReason, diagnosticGeometry.Value,
                                diagnosticPixelFormat, diagnosticStride, diagnosticScanRegion,
                                pinkAnalysis, finalCursorProbes: -1);
                            ScreenshotService.SaveLootScanRawFrame(
                                diagnosticFrame, regionName, rawMetadata, _log);
                            diagnosticFrame = null;
                        }
                    }
                }
                else
                {
                    List<WhiteComponent> exactWhiteComponents = LabelWhiteComponents(targetPoints);
                    List<WhiteComponent> shadedWhiteComponents = LabelWhiteComponents(shadedWhitePoints!);
                    components = exactWhiteComponents.Concat(shadedWhiteComponents).ToList();
                    filteredComponents = 0;
                    tinyPinkComponents = 0;
                    mobMarkerComponents = 0;
                    foreach (WhiteComponent component in exactWhiteComponents)
                    {
                        if (IsLegacyWhiteSparkleCandidate(component))
                            candidates.Add(component);
                        else
                            filteredComponents++;
                    }
                    foreach (WhiteComponent component in shadedWhiteComponents)
                    {
                        if (IsShadedWhiteLootCandidate(component))
                            candidates.Add(component);
                        else
                            filteredComponents++;
                    }
                }

                diagnosticComponentCount = components.Count;

                // Mob names are glyph rows — drop them so a name can never eat the
                // probe budget before the loot below/next to it is reached. In pink
                // SOD/SOP mode the pink name/glow itself is the target, so no text
                // filtering is applied there.
                HashSet<WhiteComponent> textGlyphs = pinkOnly
                    ? new HashSet<WhiteComponent>()
                    : FindTextGlyphs(candidates);
                if (textGlyphs.Count > 0)
                    candidates.RemoveAll(textGlyphs.Contains);

                // Probe left → right, like the old column-major pixel scan.
                candidates.Sort((a, b) => a.XMin != b.XMin ? a.XMin.CompareTo(b.XMin) : a.YMin.CompareTo(b.YMin));
                diagnosticCandidateCount = candidates.Count;

                bool captureCandidateMask = !pinkOnly && ScreenshotService.LootScanCaptureEnabled;
                foreach (WhiteComponent candidate in candidates)
                {
                    diagnosticCandidatePixelCount += candidate.Area;
                    if (!captureCandidateMask)
                        continue;
                    diagnosticCandidatePixels.AddRange(candidate.Points);
                    diagnosticCandidateBounds.Add(Rectangle.FromLTRB(
                        candidate.XMin, candidate.YMin, candidate.XMax + 1, candidate.YMax + 1));
                }

                // ── Phase C: probe only the surviving candidate blobs. White components
                //    use a deterministic two-stage plan: Stage A covers every occupied
                //    spatial cell with an actual white pixel; Stage B then exhausts the
                //    remaining white pixels of the same component. Probes are interleaved
                //    round-robin between candidates so one large component cannot starve
                //    the others. Tiny pink blobs keep the old ±1 diagonal search.
                //
                //    The normal white-loot scan stays bounded by MaxLootProbesPerRegion
                //    so UI clutter can never stall it; hitting that budget returns
                //    Incomplete (never NoItem) and the next pass continues with the
                //    already-examined coordinates skipped while the frame is unchanged.
                //    The SOD/SOP pink pass has NO probe budget: it probes EVERY candidate
                //    pixel in this single pass, in the unchanged candidate order. ──
                string probeProgressKey = pinkOnly
                    ? string.Empty
                    : BuildWhiteProbeProgressKey(regionName, diagnosticGeometry, candidates);
                if (!pinkOnly &&
                    !string.Equals(probeProgressKey, _whiteProbeProgressKey, StringComparison.Ordinal))
                {
                    // Screen/candidate geometry changed — do not trust old coordinates.
                    _probedWhiteProbeProgress.Clear();
                    _whiteProbeProgressKey = probeProgressKey;
                }

                LootProbePlanner probePlanner = pinkOnly
                    ? LootProbePlanner.CreateForPink(candidates)
                    : LootProbePlanner.CreateForWhite(candidates, _probedWhiteProbeProgress);
                diagnosticPreviouslyProbedSkipped = probePlanner.PreviouslyProbedSkipped;

                int probesUsed = 0;
                while (probePlanner.TryNext(out int candidateIndex, out Point probe))
                {
                    if (ShouldAbortLoot() || _scanToken.IsCancellationRequested ||
                        (pinkOnly && !PinkLootOnlyMode))
                    {
                        diagnosticOutcome = "cancelled-before-probe";
                        ClearWhiteProbeProgress();
                        return LootScanPassResult.Cancelled;
                    }

                    WhiteComponent component = candidates[candidateIndex];
                    bool addDiagonals = pinkOnly && component.Area <= TinyProbeWithDiagonalsArea;
                    actualCursorProbes++;
                    probesUsed++;
                    if (!pinkOnly)
                        _probedWhiteProbeProgress.Add(probe);

                    LootProbeResult probeResult = TryCollectAt(probe.X, probe.Y, pinkOnly);
                    diagnosticWhiteHits.Add(probe);
                    if (probeResult == LootProbeResult.Collected)
                    {
                        diagnosticSuccessProbe = probe;
                        diagnosticOutcome = "collected";
                        _log($"[Loot] {regionName}: mouseover confirmed at bitmap-local ({probe.X},{probe.Y}) — " +
                             $"candidate {candidateIndex + 1}/{candidates.Count} {FormatWhiteComponent(component)}, " +
                             $"probe {probePlanner.AttemptsFor(candidateIndex)}/{probePlanner.InitialRemainingFor(candidateIndex)} of this candidate, " +
                             $"{probesUsed} probe(s) this pass{KillElapsed()}.");
                        ClearWhiteProbeProgress();
                        return LootScanPassResult.Collected;
                    }
                    if (probeResult == LootProbeResult.CaptureInvalid)
                    {
                        diagnosticOutcome = "capture-geometry-changed-during-probes";
                        ClearWhiteProbeProgress();
                        return LootScanPassResult.CaptureFailed;
                    }
                    if (probeResult == LootProbeResult.Cancelled)
                    {
                        diagnosticOutcome = "probe-cancelled-or-focus-lost";
                        ClearWhiteProbeProgress();
                        return LootScanPassResult.Cancelled;
                    }

                    if (addDiagonals)
                    {
                        LootProbeResult ProbeDiagonal(int dx, int dy)
                        {
                            var diagonal = new Point(probe.X + dx, probe.Y + dy);
                            LootProbeResult result = TryCollectAtWithCount(
                                diagonal.X, diagonal.Y, pinkOnly, ref actualCursorProbes);
                            if (result == LootProbeResult.Collected)
                                diagnosticSuccessProbe = diagonal;
                            return result;
                        }

                        probesUsed += 4;
                        LootProbeResult diagonalResult = ProbeDiagonal(1, 1);
                        if (diagonalResult != LootProbeResult.Miss)
                            return HandleProbeResult(diagonalResult, ref diagnosticOutcome);
                        diagonalResult = ProbeDiagonal(-1, -1);
                        if (diagonalResult != LootProbeResult.Miss)
                            return HandleProbeResult(diagonalResult, ref diagnosticOutcome);
                        diagonalResult = ProbeDiagonal(1, -1);
                        if (diagonalResult != LootProbeResult.Miss)
                            return HandleProbeResult(diagonalResult, ref diagnosticOutcome);
                        diagonalResult = ProbeDiagonal(-1, 1);
                        if (diagonalResult != LootProbeResult.Miss)
                            return HandleProbeResult(diagonalResult, ref diagnosticOutcome);
                    }

                    // Only the normal white-loot scan keeps the probe budget.
                    if (!pinkOnly && probesUsed >= MaxLootProbesPerRegion)
                    {
                        _log($"[Loot] {regionName}: probe budget ({MaxLootProbesPerRegion}) exhausted — " +
                             $"{probesUsed} probes, {probePlanner.RemainingProbeCount} of {probePlanner.InitialRemainingProbes} candidate pixels still unexamined " +
                             $"across {probePlanner.CandidatesWithRemainingProbes} candidate(s) " +
                             $"({diagnosticPreviouslyProbedSkipped} already examined in a previous pass); " +
                             $"region incomplete, not treated as empty.");
                        diagnosticOutcome = "probe-budget-exhausted";
                        return LootScanPassResult.Incomplete;
                    }
                }

                LootScanPassResult sweepResult = ResolveProbeSweepResult(probePlanner.RemainingProbeCount);
                if (diagnosticWhiteCount > 0)
                {
                    string pinkNote = pinkOnly
                        ? $", {tinyPinkComponents} below {BotConstants.Loot.PinkMinCandidateWidth}x{BotConstants.Loot.PinkMinCandidateHeight} px skipped"
                        : "";
                    string whiteDetail = pinkOnly
                        ? ""
                        : $", exact-white={diagnosticExactWhiteCount}px, shaded-white={diagnosticShadedWhiteCount}px";
                    _log($"[Loot] {regionName}: scanned {scanPixels} px, {diagnosticWhiteCount} {pixelNoun} px in {components.Count} components{whiteDetail} — " +
                         $"{filteredComponents} filtered (shape/size; mob-marker rings={mobMarkerComponents}){pinkNote}, {textGlyphs.Count} text glyphs skipped, " +
                         $"{candidates.Count} loot candidates ({diagnosticCandidatePixelCount} candidate px), " +
                         $"{probesUsed} probes, no item collected; result={sweepResult}{KillElapsed()}.");
                    if (!pinkOnly && candidates.Count > 0)
                    {
                        _log($"[Loot] {regionName}: candidates {DescribeWhiteCandidates(candidates)}; " +
                             $"probe attempts {DescribeProbeAttempts(probePlanner, candidates.Count)}; " +
                             $"previously examined={diagnosticPreviouslyProbedSkipped}px; remaining candidates={probePlanner.CandidatesWithRemainingProbes}.");
                    }
                }
                else
                {
                    _log($"[Loot] {regionName}: scanned {scanPixels} px, no {pixelNoun} pixels.");
                }
                if (!pinkOnly)
                    ClearWhiteProbeProgress();
                diagnosticOutcome = sweepResult == LootScanPassResult.NoItem ? "no-item" : "probe-sequence-incomplete";
                return sweepResult;
            }
            catch (Exception ex)
            {
                _log($"PixelScan error: {ex.Message}");
                diagnosticOutcome = "scan-error";
                return LootScanPassResult.CaptureFailed;
            }
            finally
            {
                if (pinkOnly && ScreenshotService.LootScanCaptureEnabled &&
                    pinkAnalysis != null && diagnosticGeometry.HasValue)
                {
                    LogPinkFrameDiagnostics(
                        scanStartedUtc, scanReason, diagnosticGeometry.Value,
                        diagnosticPixelFormat, diagnosticStride, diagnosticScanRegion,
                        pinkAnalysis, actualCursorProbes, diagnosticOutcome);
                }

                // Keep probe progress ONLY for a bounded Incomplete pass. Every other
                // outcome either finished the candidate sweep, collected an item (the
                // screen changes) or lost a trustworthy frame.
                if (!pinkOnly && diagnosticOutcome != "probe-budget-exhausted")
                    ClearWhiteProbeProgress();

                // White-loot retains the existing annotated frame (and now also saves an
                // unmodified raw frame from the same clone). Pink diagnostics save an
                // exact unmodified clone separately, before it can be annotated.
                if (diagnosticFrame != null)
                {
                    if (pinkOnly)
                    {
                        string metadata = diagnosticGeometry.HasValue && pinkAnalysis != null
                            ? BuildPinkFrameDiagnosticMetadata(
                                scanStartedUtc, scanReason, diagnosticGeometry.Value,
                                diagnosticPixelFormat, diagnosticStride, diagnosticScanRegion,
                                pinkAnalysis, actualCursorProbes)
                            : $"timestampUtc={scanStartedUtc:O}; reason={scanReason}; captureStatus={_lastCaptureResult.Status}; outcome={diagnosticOutcome}";
                        ScreenshotService.SaveLootScanRawFrame(diagnosticFrame, regionName, metadata, _log);
                    }
                    else
                    {
                        string geometryText = diagnosticGeometry.HasValue
                            ? $"hwnd=0x{diagnosticGeometry.Value.WindowHandle.ToInt64():X}; " +
                              $"window={FormatRectangle(diagnosticGeometry.Value.WindowRectangle)}; " +
                              $"clientOrigin=({diagnosticGeometry.Value.ClientScreenOrigin.X},{diagnosticGeometry.Value.ClientScreenOrigin.Y}); " +
                              $"client={diagnosticGeometry.Value.ClientSize.Width}x{diagnosticGeometry.Value.ClientSize.Height}"
                            : "geometry=unavailable";
                        string rawMetadata =
                            $"timestampUtc={scanStartedUtc:O}; reason=white-loot; region={regionName}; " +
                            $"captureStatus={_lastCaptureResult.Status}; outcome={diagnosticOutcome}; {geometryText}; " +
                            $"scanRegion={FormatRectangle(diagnosticScanRegion)}; " +
                            $"excludeZone={FormatRectangle(diagnosticExcludeZone)}; " +
                            $"exactWhite={diagnosticExactWhiteCount}; shadedWhite={diagnosticShadedWhiteCount}; " +
                            $"components={diagnosticComponentCount}; candidates={diagnosticCandidateCount}; " +
                            $"candidatePixels={diagnosticCandidatePixelCount}; probes={actualCursorProbes}";
                        ScreenshotService.SaveLootScanFrame(
                            diagnosticFrame, regionName, diagnosticOutcome, _log,
                            diagnosticWhiteCount, diagnosticScanRegion, diagnosticExcludeZone,
                            diagnosticWhiteHits, pixelNoun,
                            detectedPixels: diagnosticDetectedWhitePixels,
                            candidatePixels: diagnosticCandidatePixels,
                            candidateBounds: diagnosticCandidateBounds,
                            confirmedProbe: diagnosticSuccessProbe,
                            exactWhiteCount: diagnosticExactWhiteCount,
                            shadedWhiteCount: diagnosticShadedWhiteCount,
                            candidatePixelCount: diagnosticCandidatePixelCount,
                            saveRawFrame: true,
                            rawMetadata: rawMetadata);
                    }
                }
            }
        }

        private void ScanForOtherPlayerPixels()
        {
            if (!PlayerMarkerScanEnabled || _bitmap == null || _memoryService.GetIsInCity())
            {
                ResetPlayerMarkerConfirmation();
                return;
            }

            // Do not act on repeated intermediate frames. The scheduled confirmation
            // below deliberately takes a fresh frame after the full delay.
            if (_playerMarkerConfirmationPending)
                return;

            try
            {
                Rectangle characterExclusionArea = PlayerMarkerPixelDetector.GetLootCharacterExclusionArea(
                    _bitmap.Width, _bitmap.Height,
                    _referenceClientOriginX, _referenceClientOriginY);
                bool candidateVisible = _playerMarkerPixelDetector.TryFindOtherPlayerMarker(
                    _bitmap, characterExclusionArea, out PlayerMarkerCandidate candidate);
                if (!candidateVisible)
                {
                    _otherPlayerMarkerWasVisible = false;
                    return;
                }
                if (_otherPlayerMarkerWasVisible)
                    return;

                _otherPlayerMarkerWasVisible = true;
                _playerMarkerConfirmationPending = true;
                _playerMarkerChecksConfirmed = 1;
                _playerMarkerCandidateDetectedAt = DateTime.UtcNow;
                _log($"[PlayerPixels] Candidate marker detected: {DescribePlayerMarkerCandidate(candidate)}; " +
                     $"character-exclusion={FormatRectangle(characterExclusionArea)}; " +
                     $"HUD exclusions={FormatRectangles(PlayerMarkerPixelDetector.GetHudExclusionAreas(_bitmap.Width, _bitmap.Height))}. " +
                     $"Check 1/{OtherPlayerMarkerRequiredChecks}; will recheck in {BotConstants.Delays.OtherPlayerMarkerConfirmMs} ms.");
            }
            catch (Exception ex)
            {
                ResetPlayerMarkerConfirmation();
                _log($"[PlayerPixels] Marker scan failed: {ex.Message}");
            }
        }

        private bool ConfirmPlayerMarkerIfDue()
        {
            if (!PlayerMarkerScanEnabled || !_playerMarkerConfirmationPending)
                return false;

            int delayMs = _playerMarkerChecksConfirmed == 1
                ? BotConstants.Delays.OtherPlayerMarkerConfirmMs
                : BotConstants.Delays.OtherPlayerMarkerFinalConfirmMs;
            if ((DateTime.UtcNow - _playerMarkerCandidateDetectedAt).TotalMilliseconds < delayMs)
                return false;

            try
            {
                if (!CaptureScreen() || _bitmap == null || _clientWidth <= 0 || _clientHeight <= 0)
                {
                    ResetPlayerMarkerConfirmation();
                    _log("[PlayerPixels] Confirmation capture failed; ignoring the candidate.");
                    return false;
                }

                if (_memoryService.GetIsInCity())
                {
                    ResetPlayerMarkerConfirmation();
                    return false;
                }

                Rectangle characterExclusionArea = PlayerMarkerPixelDetector.GetLootCharacterExclusionArea(
                    _bitmap.Width, _bitmap.Height,
                    _referenceClientOriginX, _referenceClientOriginY);
                bool stillVisible = _playerMarkerPixelDetector.TryFindOtherPlayerMarker(
                    _bitmap, characterExclusionArea, out PlayerMarkerCandidate confirmedCandidate);
                if (!stillVisible)
                {
                    _log($"[PlayerPixels] Candidate disappeared during check " +
                         $"{_playerMarkerChecksConfirmed + 1}/{OtherPlayerMarkerRequiredChecks}; continuing normally.");
                    ResetPlayerMarkerConfirmation();
                    return false;
                }

                _otherPlayerMarkerWasVisible = true;
                _playerMarkerChecksConfirmed++;
                if (_playerMarkerChecksConfirmed < OtherPlayerMarkerRequiredChecks)
                {
                    _playerMarkerCandidateDetectedAt = DateTime.UtcNow;
                    _log($"[PlayerPixels] Other-player marker confirmed " +
                         $"({_playerMarkerChecksConfirmed}/{OtherPlayerMarkerRequiredChecks}): " +
                         $"{DescribePlayerMarkerCandidate(confirmedCandidate)}; " +
                         $"character-exclusion={FormatRectangle(characterExclusionArea)}. " +
                         $"Will perform the final check in {BotConstants.Delays.OtherPlayerMarkerFinalConfirmMs} ms.");
                    return false;
                }

                _playerMarkerConfirmationPending = false;
                _playerMarkerCandidateDetectedAt = DateTime.MinValue;
                _log($"[PlayerPixels] Other-player marker confirmed on all " +
                     $"{OtherPlayerMarkerRequiredChecks} consecutive checks: " +
                     $"{DescribePlayerMarkerCandidate(confirmedCandidate)}; " +
                     $"character-exclusion={FormatRectangle(characterExclusionArea)}.");
                ScreenshotService.SavePlayerMarkerScreenshot(
                    _bitmap,
                    confirmedCandidate,
                    characterExclusionArea,
                    PlayerMarkerPixelDetector.GetHudExclusionAreas(_bitmap.Width, _bitmap.Height),
                    _log);
                if (ShouldStopOnOtherPlayerMarker?.Invoke() ?? true)
                {
                    OtherPlayerMarkerDetected?.Invoke();
                }
                else
                {
                    _log("[PlayerPixels] Stop/return-to-city behavior is disabled; screenshot saved and continuing normally.");
                }
                return true;
            }
            catch (Exception ex)
            {
                ResetPlayerMarkerConfirmation();
                _log($"[PlayerPixels] Confirmation scan failed: {ex.Message}");
                return false;
            }
        }

        private static string DescribePlayerMarkerCandidate(PlayerMarkerCandidate candidate)
        {
            return $"{candidate.Faction} bounds={FormatRectangle(candidate.Bounds)} " +
                   $"pixels={candidate.PixelCount} fill={candidate.FillRatio:P0} " +
                   $"aspect={candidate.AspectRatio:F2} " +
                   $"center=({candidate.Center.X:F1},{candidate.Center.Y:F1})";
        }

        private static string FormatRectangle(Rectangle rectangle)
            => $"({rectangle.X},{rectangle.Y},{rectangle.Width}x{rectangle.Height})";

        private static string BuildPinkFrameDiagnosticMetadata(
            DateTime timestampUtc,
            string scanReason,
            LootClientGeometry geometry,
            PixelFormat pixelFormat,
            int stride,
            Rectangle scannedRectangle,
            PinkFrameAnalysisResult analysis,
            int finalCursorProbes,
            string scanOutcome = "probes-pending")
        {
            string probes = finalCursorProbes < 0 ? "not-yet-run" : finalCursorProbes.ToString();
            return $"timestampUtc={timestampUtc:O}; reason={scanReason}; frameCaptureStatus=Success; " +
                   $"hwnd=0x{geometry.WindowHandle.ToInt64():X}; window={FormatRectangle(geometry.WindowRectangle)}; " +
                   $"clientOrigin=({geometry.ClientScreenOrigin.X},{geometry.ClientScreenOrigin.Y}); " +
                   $"client={geometry.ClientSize.Width}x{geometry.ClientSize.Height}; " +
                   $"bitmap={analysis.Width}x{analysis.Height}; pixelFormat={pixelFormat}; stride={stride}; " +
                   $"scannedRect={FormatRectangle(scannedRectangle)}; pixelsExamined={analysis.TotalPixelsExamined}; " +
                   $"rawPink={analysis.RawPinkPixels}; markerRejected={analysis.MarkerPixelsRejected}; " +
                   $"maskPixels={analysis.PinkPixels}; components={analysis.Components}; " +
                   $"candidates={analysis.LootCandidates}; rejectedBySizeOrShape={analysis.SizeOrShapeRejectedComponents}; " +
                   $"markerRingComponents={analysis.MobMarkerRings}; plannedProbes={analysis.FinalProbeCount}; " +
                   $"actualProbes={probes}; scanOutcome={scanOutcome}";
        }

        private void LogPinkCaptureFailureDiagnostics(
            DateTime timestampUtc,
            string scanReason,
            StableLootCaptureResult capture)
        {
            LootClientGeometry geometry = capture.Geometry;
            string captureStatus = capture.Succeeded
                ? "UnusableBitmapAfterCapture"
                : $"Failed:{capture.Status}";
            _log($"[LootPinkDiagnostic] timestampUtc={timestampUtc:O}; reason={scanReason}; " +
                 $"frameCaptureStatus={captureStatus}; hwnd=0x{geometry.WindowHandle.ToInt64():X}; " +
                 $"window={FormatRectangle(geometry.WindowRectangle)}; " +
                 $"clientOrigin=({geometry.ClientScreenOrigin.X},{geometry.ClientScreenOrigin.Y}); " +
                 $"client={geometry.ClientSize.Width}x{geometry.ClientSize.Height}; " +
                 $"pixelFormat={(_bitmap?.PixelFormat.ToString() ?? "Undefined")}; stride=0; scannedRect=none; " +
                 $"pixelsExamined=0; rawPink=0; markerRejected=0; maskPixels=0; components=0; " +
                 $"candidates=0; rejectedBySizeOrShape=0; plannedProbes=0; actualProbes=0; " +
                 $"scanOutcome=capture-failed; error={capture.Error}");
        }

        private void LogPinkFrameDiagnostics(
            DateTime timestampUtc,
            string scanReason,
            LootClientGeometry geometry,
            PixelFormat pixelFormat,
            int stride,
            Rectangle scannedRectangle,
            PinkFrameAnalysisResult analysis,
            int finalCursorProbes,
            string scanOutcome)
        {
            _log("[LootPinkDiagnostic] " + BuildPinkFrameDiagnosticMetadata(
                timestampUtc, scanReason, geometry, pixelFormat, stride,
                scannedRectangle, analysis, finalCursorProbes, scanOutcome));
            for (int i = 0; i < analysis.CandidateDetails.Count; i++)
            {
                PinkComponentSummary component = analysis.CandidateDetails[i];
                _log($"[LootPinkDiagnostic] component[{i}] bounds={FormatRectangle(component.Bounds)} " +
                     $"pixels={component.PixelCount} fill={component.FillRatio:F3} candidate={component.IsLootCandidate} " +
                     $"probePoints={component.ProbePoints.Count}");
            }
        }

        private static string FormatRectangles(Rectangle[] rectangles)
            => string.Join(";", Array.ConvertAll(rectangles, FormatRectangle));

        private void ResetPlayerMarkerConfirmation()
        {
            _otherPlayerMarkerWasVisible = false;
            _playerMarkerConfirmationPending = false;
            _playerMarkerChecksConfirmed = 0;
            _playerMarkerCandidateDetectedAt = DateTime.MinValue;
        }

        private void CancelAllLootWork(string reason, bool deferMandatoryRequest = false)
        {
            bool hadWork;
            lock (_cameraCursorOwnershipGate)
            {
                PinkRescanSnapshot pendingRescan = _pinkRescanScheduler.Snapshot;
                MandatoryLootRequest? mandatoryRequest = _activeMandatoryLootRequest;
                hadWork = _pinkScanRequested || _pinkScanOwnsCameraCursor || _pinkRescanActive ||
                          _lootState != LootMachineState.Idle || IsLootingActive ||
                          pendingRescan.Pending || pendingRescan.Active ||
                          (mandatoryRequest != null && IsMandatoryRequestActiveLocked(mandatoryRequest));

                if (mandatoryRequest != null && IsMandatoryRequestActiveLocked(mandatoryRequest))
                {
                    if (deferMandatoryRequest)
                    {
                        mandatoryRequest.State = LootRequestState.Deferred;
                        mandatoryRequest.NeedsFreshCycle = true;
                        mandatoryRequest.Detail = reason ?? "loot temporarily cancelled";
                        mandatoryRequest.UpdatedAtUtc = DateTime.UtcNow;
                    }
                    else
                    {
                        mandatoryRequest.State = LootRequestState.Cancelled;
                        mandatoryRequest.Detail = reason ?? "loot cancelled";
                        mandatoryRequest.UpdatedAtUtc = DateTime.UtcNow;
                        if (_activeTravelCandidate?.RequestId == mandatoryRequest.Id)
                            _activeTravelCandidate = null;
                        if (_combatRecoveryMovementRequestId == mandatoryRequest.Id)
                            _combatRecoveryMovementRequestId = 0;
                        _activeMandatoryLootRequest = null;
                    }
                }

                _pinkScanRequested = false;
                _pinkScanRequestedAt = DateTime.MinValue;
                _pinkRescanScheduler.Cancel();
                RestorePinkCameraLocked();
                _pinkScanOwnsCameraCursor = false;
                _lootState = LootMachineState.Idle;
                _consecutiveEmptySpacePresses = 0;
                _consecutivePinkCaptureFailures = 0;
                _scanCompleteUntil = DateTime.MinValue;
                _lastKillAt = DateTime.MinValue;
                _wasMobSelectedPrev = false;
                IsLootingActive = false;
                ReleaseLootScanCameraLocked($"all loot work cleared: {reason}");
            }
            if (hadWork)
                _log($"[Loot] Pink/loot work cleared: {reason}.");
        }

        private void CancelPinkScanWork(string reason)
        {
            lock (_cameraCursorOwnershipGate)
            {
                _pinkScanRequested = false;
                _pinkScanRequestedAt = DateTime.MinValue;
                _pinkRescanScheduler.Cancel();
                RestorePinkCameraLocked();
                _pinkScanOwnsCameraCursor = false;
                if (_lootState == LootMachineState.PinkScan)
                    _lootState = LootMachineState.Idle;
                IsLootingActive = false;
                _consecutiveEmptySpacePresses = 0;
                _consecutivePinkCaptureFailures = 0;
                _lastKillAt = DateTime.MinValue;
            }
            _log($"[Loot] SOD/SOP work cancelled: {reason}.");
        }

        private void RestorePinkCameraLocked()
        {
            if (_pinkScanRestoreDistance.HasValue &&
                (!_memoryService.TryGetCameraDistance(out short currentDistance) ||
                 currentDistance == BotConstants.Camera.SodSopLootScanDistance))
            {
                RestoreCameraDistance(_pinkScanRestoreDistance.Value);
            }
            _pinkScanRestoreDistance = null;
        }

        private void RestoreCameraDistance(short distance)
        {
            if (_memoryService.TrySetCameraDistance(distance))
                return;

            _memoryService.SetCameraDistance(distance);
            if (!_memoryService.TryGetCameraDistance(out short restored) || restored != distance)
                _log($"[LootCamera] Could not restore camera distance to {distance} after pink scan.");
        }

        private void SetPinkScanCameraDistance()
        {
            lock (_cameraCursorOwnershipGate)
            {
                if (!_pinkScanRestoreDistance.HasValue)
                {
                    if (_memoryService.TryGetCameraDistance(out short previousDistance))
                        _pinkScanRestoreDistance = previousDistance;
                    else
                    {
                        _pinkScanRestoreDistance = BotConstants.Camera.DefaultDistanceLock;
                        _log("[LootCamera] Previous camera distance unreadable; using the default lock for restoration.");
                    }
                }
                _pinkScanOwnsCameraCursor = true;
                _memoryService.SetCameraDistance(BotConstants.Camera.SodSopLootScanDistance);
            }
        }

        private void CompleteImmediatePinkScan(string outcome)
        {
            lock (_cameraCursorOwnershipGate)
            {
                RestorePinkCameraLocked();
                _pinkScanOwnsCameraCursor = false;
                _lootState = LootMachineState.ScanComplete;
                _scanCompleteUntil = DateTime.UtcNow.AddMilliseconds(BotConstants.Delays.LootScanCompleteHoldMs);
                _consecutiveEmptySpacePresses = 0;
                _consecutivePinkCaptureFailures = 0;
                IsLootingActive = false;
            }
            _log(outcome);
        }

        /// <summary>
        /// Phase A of a region pass: collects exact-white legacy pixels, an optional
        /// shaded near-white candidate mask, or the hot-pink family for SOD/SOP.
        /// The bitmap is bulk-read row by row so positive and negative GDI+ strides map
        /// to the same logical top-down client coordinates as GetPixel.
        /// </summary>
        private PixelCollectionStats CollectScanTargetPoints(
            int xStart, int xEnd, int yStart, int yEnd,
            bool applyExcludeZone, int exclXMin, int exclXMax, int exclYMin, int exclYMax,
            bool pinkOnly, List<Point> targetPoints,
            out PixelFormat sourcePixelFormat,
            out int sourceStride,
            bool shadedWhiteOnly = false)
            => CollectBitmapTargetPoints(
                _bitmap, xStart, xEnd, yStart, yEnd,
                applyExcludeZone, exclXMin, exclXMax, exclYMin, exclYMax,
                pinkOnly, targetPoints, ref _scanBuffer,
                out sourcePixelFormat, out sourceStride, shadedWhiteOnly);

        internal static PixelCollectionStats CollectTargetPointsFromBitmap(
            Bitmap bitmap, bool pinkOnly, List<Point> targetPoints,
            bool shadedWhiteOnly = false)
        {
            byte[] reusableBuffer = Array.Empty<byte>();
            return CollectBitmapTargetPoints(
                bitmap, 0, bitmap.Width, 0, bitmap.Height,
                applyExcludeZone: false, 0, 0, 0, 0,
                pinkOnly, targetPoints, ref reusableBuffer, out _, out _, shadedWhiteOnly);
        }

        private static PixelCollectionStats CollectBitmapTargetPoints(
            Bitmap bitmap,
            int xStart, int xEnd, int yStart, int yEnd,
            bool applyExcludeZone, int exclXMin, int exclXMax, int exclYMin, int exclYMax,
            bool pinkOnly, List<Point> targetPoints,
            ref byte[] reusableBuffer,
            out PixelFormat sourcePixelFormat,
            out int sourceStride,
            bool shadedWhiteOnly)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            ArgumentNullException.ThrowIfNull(targetPoints);
            sourcePixelFormat = bitmap.PixelFormat;
            sourceStride = 0;
            int initialCount = targetPoints.Count;

            Bitmap? converted = null;
            Bitmap scanBitmap = bitmap;
            BitmapData? data = null;
            bool useReferenceReader = false;
            try
            {
                if (bitmap.PixelFormat != PixelFormat.Format32bppArgb)
                {
                    converted = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);
                    using (Graphics graphics = Graphics.FromImage(converted))
                        graphics.DrawImageUnscaled(bitmap, 0, 0);
                    scanBitmap = converted;
                }

                data = scanBitmap.LockBits(
                    new Rectangle(0, 0, scanBitmap.Width, scanBitmap.Height),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppArgb);
                sourceStride = data.Stride;
                int rowBytes = checked(scanBitmap.Width * 4);
                int normalizedBytes = checked(rowBytes * scanBitmap.Height);
                if (reusableBuffer.Length < normalizedBytes)
                    reusableBuffer = new byte[normalizedBytes];

                // Normalize logical bitmap rows to a positive, tightly packed stride.
                // Copying Scan0 as one positive-length block is incorrect for negative
                // stride and can reverse rows or read the wrong memory range.
                for (int y = 0; y < scanBitmap.Height; y++)
                {
                    IntPtr sourceRow = IntPtr.Add(data.Scan0, checked(y * data.Stride));
                    Marshal.Copy(sourceRow, reusableBuffer, y * rowBytes, rowBytes);
                }

                return CollectTargetPointsFromBuffer(
                    reusableBuffer, rowBytes, scanBitmap.Height,
                    xStart, xEnd, yStart, yEnd,
                    applyExcludeZone, exclXMin, exclXMax, exclYMin, exclYMax,
                    pinkOnly, targetPoints, shadedWhiteOnly);
            }
            catch
            {
                targetPoints.RemoveRange(initialCount, targetPoints.Count - initialCount);
                useReferenceReader = true;
            }
            finally
            {
                if (data != null)
                {
                    try { scanBitmap.UnlockBits(data); }
                    catch { useReferenceReader = true; }
                }
                converted?.Dispose();
            }

            if (useReferenceReader)
            {
                sourceStride = 0;
                return CollectTargetPointsFromBitmapReference(
                    bitmap, xStart, xEnd, yStart, yEnd,
                    applyExcludeZone, exclXMin, exclXMax, exclYMin, exclYMax,
                    pinkOnly, targetPoints, shadedWhiteOnly);
            }

            return default;
        }

        /// <summary>
        /// Direct GetPixel reference reader and production fallback. It uses the same
        /// production classifier as the bulk BGRA reader.
        /// </summary>
        internal static PixelCollectionStats CollectTargetPointsFromBitmapReference(
            Bitmap bitmap,
            int xStart, int xEnd, int yStart, int yEnd,
            bool applyExcludeZone, int exclXMin, int exclXMax, int exclYMin, int exclYMax,
            bool pinkOnly, List<Point> targetPoints, bool shadedWhiteOnly = false)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            ArgumentNullException.ThrowIfNull(targetPoints);
            int initialCount = targetPoints.Count;
            int examined = 0;
            int rawPink = 0;
            int markerRejected = 0;
            for (int x = xStart; x < xEnd; x++)
            {
                for (int y = yStart; y < yEnd; y++)
                {
                    if (applyExcludeZone && x >= exclXMin && x <= exclXMax &&
                        y >= exclYMin && y <= exclYMax)
                        continue;

                    examined++;
                    Color color = bitmap.GetPixel(x, y);
                    bool isTarget;
                    if (pinkOnly)
                    {
                        bool raw = IsSodSopPinkPixel(color);
                        if (raw) rawPink++;
                        isTarget = IsPinkLootMaskPixel(color);
                        if (raw && !isTarget) markerRejected++;
                    }
                    else if (shadedWhiteOnly)
                    {
                        isTarget = IsShadedWhiteLootPixel(color);
                    }
                    else
                    {
                        isTarget = color.R == 255 && color.G == 255 && color.B == 255;
                    }

                    if (isTarget)
                        targetPoints.Add(new Point(x, y));
                }
            }

            return new PixelCollectionStats(examined, rawPink, markerRejected, targetPoints.Count - initialCount);
        }

        /// <summary>
        /// Testable core of Phase A: parses a raw 32bpp BGRA frame buffer and collects
        /// every target pixel. A negative stride means the supplied packed rows are
        /// bottom-up; production normalizes BitmapData rows before calling this helper.
        /// </summary>
        internal static PixelCollectionStats CollectTargetPointsFromBuffer(
            byte[] buffer, int stride, int bitmapHeight,
            int xStart, int xEnd, int yStart, int yEnd,
            bool applyExcludeZone, int exclXMin, int exclXMax, int exclYMin, int exclYMax,
            bool pinkOnly, List<Point> targetPoints, bool shadedWhiteOnly = false)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentNullException.ThrowIfNull(targetPoints);
            long absStrideLong = Math.Abs((long)stride);
            if (stride == 0 || bitmapHeight <= 0 || xStart < 0 || xEnd < xStart ||
                yStart < 0 || yEnd < yStart || yEnd > bitmapHeight ||
                (long)xEnd * 4 > absStrideLong ||
                absStrideLong * bitmapHeight > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(stride), "Invalid BGRA buffer geometry.");

            int absStride = checked((int)absStrideLong);
            bool bottomUp = stride < 0;
            int initialCount = targetPoints.Count;
            int examined = 0;
            int rawPink = 0;
            int markerRejected = 0;
            for (int x = xStart; x < xEnd; x++)
            {
                bool xOutsideExclude = !applyExcludeZone || x < exclXMin || x > exclXMax;
                for (int y = yStart; y < yEnd; y++)
                {
                    if (!xOutsideExclude && y >= exclYMin && y <= exclYMax)
                        continue;

                    int row = bottomUp ? bitmapHeight - 1 - y : y;
                    long offsetLong = (long)row * absStride + (long)x * 4;
                    if (offsetLong < 0 || offsetLong + 3 >= buffer.Length)
                        throw new ArgumentOutOfRangeException(nameof(buffer), "A requested pixel falls outside the BGRA buffer.");
                    int offset = (int)offsetLong;
                    int b = buffer[offset];
                    int g = buffer[offset + 1];
                    int r = buffer[offset + 2];
                    examined++;

                    bool isTarget;
                    if (pinkOnly)
                    {
                        bool raw = IsSodSopPinkPixel(r, g, b);
                        if (raw) rawPink++;
                        isTarget = IsPinkLootMaskPixel(r, g, b);
                        if (raw && !isTarget) markerRejected++;
                    }
                    else if (shadedWhiteOnly)
                    {
                        isTarget = IsShadedWhiteLootPixel(r, g, b);
                    }
                    else
                    {
                        isTarget = r == 255 && g == 255 && b == 255;
                    }

                    if (isTarget)
                        targetPoints.Add(new Point(x, y));
                }
            }

            return new PixelCollectionStats(examined, rawPink, markerRejected, targetPoints.Count - initialCount);
        }

        // ════════════════════════════════════════════════════════════════
        //  WHITE-PIXEL SHAPE FILTER (loot candidate detection)
        //  Keep the legacy compact white-sparkle candidates and also accept larger,
        //  concentrated shaded-white components like the SOD/SOP ground squares.
        //  The mouseover check remains authoritative for pickup in both paths.
        // ════════════════════════════════════════════════════════════════

        private const int MaxLootCandidateArea = 60;
        private const int MaxLootCandidateDimension = 24;
        private const int ThinLineMaxThickness = 2;
        private const int ThinLineMinLength = 12;
        private const int WideBandMinWidth = 18;
        private const int WideBandMinHeight = 12;

        private const int WhiteLootMinCandidateWidth = 10;
        private const int WhiteLootMinCandidateHeight = 10;
        private const int WhiteLootMinCandidatePixels = 30;
        private const float WhiteLootMinFillRatio = 0.20f;

        /// <summary>Tiny pink blobs (1–4 px) keep the old ±1 diagonal hover search around each pixel.</summary>
        private const int TinyProbeWithDiagonalsArea = 4;

        /// <summary>Hard cap on mouseover probes per region pass — replaces the old raw-white-count abort.</summary>
        private const int MaxLootProbesPerRegion = 400;

        /// <summary>
        /// Stage A spatial-coverage of a white component: the component's larger dimension
        /// is split into at most this many cells along each axis, so coverage grows with
        /// the component (up to 64 cells) instead of being a fixed sample count. Larger
        /// components use coarser cells; tiny components use at least two-pixel cells.
        /// </summary>
        private const int WhiteProbeCellsPerAxis = 8;
        private const int WhiteProbeCellMinPx = 2;

        /// <summary>Text-cluster detection: a horizontal run of at least this many glyph-like blobs is a mob name.</summary>
        private const int TextClusterMinGlyphs = 5;
        private const int TextClusterMaxGlyphArea = 40;
        private const int TextClusterMaxGlyphWidth = 14;
        private const int TextClusterMaxGlyphHeight = 16;
        private const int TextClusterMaxGapPx = 4;
        private const int TextClusterMaxBaselineDriftPx = 5;

        /// <summary>A connected group of loot-color pixels found during a region scan.</summary>
        internal sealed class WhiteComponent
        {
            public readonly List<Point> Points = new List<Point>();
            public int XMin = int.MaxValue, YMin = int.MaxValue;
            public int XMax = int.MinValue, YMax = int.MinValue;

            public int Area => Points.Count;
            public int Width => XMax - XMin + 1;
            public int Height => YMax - YMin + 1;

            public void Add(int x, int y)
            {
                Points.Add(new Point(x, y));
                if (x < XMin) XMin = x;
                if (x > XMax) XMax = x;
                if (y < YMin) YMin = y;
                if (y > YMax) YMax = y;
            }
        }

        /// <summary>Groups white pixels into 8-connected components.</summary>
        private static List<WhiteComponent> LabelWhiteComponents(List<Point> whitePoints)
        {
            var present = new HashSet<(int X, int Y)>(whitePoints.Count);
            foreach (Point p in whitePoints)
                present.Add((p.X, p.Y));

            var visited = new HashSet<(int X, int Y)>(whitePoints.Count);
            var components = new List<WhiteComponent>();

            foreach (Point start in whitePoints)
            {
                var startKey = (start.X, start.Y);
                if (!visited.Add(startKey))
                    continue;

                var component = new WhiteComponent();
                var queue = new Queue<(int X, int Y)>();
                queue.Enqueue(startKey);

                while (queue.Count > 0)
                {
                    var (x, y) = queue.Dequeue();
                    component.Add(x, y);

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0)
                                continue;

                            var neighbor = (x + dx, y + dy);
                            if (present.Contains(neighbor) && visited.Add(neighbor))
                                queue.Enqueue(neighbor);
                        }
                    }
                }

                components.Add(component);
            }

            return components;
        }

        /// <summary>
        /// True when a shaded-white component is a large, concentrated potential
        /// ground drop. There is no upper-size cap, so distance/zoom does not make a
        /// real drop disappear. This only selects probe locations: TryCollectAt still
        /// requires the normal lootable mouseover before the unchanged collection action.
        /// </summary>
        internal static bool IsShadedWhiteLootCandidate(WhiteComponent component)
        {
            if (component.Width < WhiteLootMinCandidateWidth ||
                component.Height < WhiteLootMinCandidateHeight ||
                component.Area < WhiteLootMinCandidatePixels)
                return false;

            float fillRatio = component.Area /
                (float)Math.Max(component.Width * component.Height, 1);
            return fillRatio >= WhiteLootMinFillRatio;
        }

        /// <summary>Original compact exact-white sparkle filter; retained for legacy loot.</summary>
        internal static bool IsLegacyWhiteSparkleCandidate(WhiteComponent component)
        {
            if (component.Area > MaxLootCandidateArea)
                return false;
            if (component.Width > MaxLootCandidateDimension ||
                component.Height > MaxLootCandidateDimension)
                return false;
            if (Math.Min(component.Width, component.Height) <= ThinLineMaxThickness &&
                Math.Max(component.Width, component.Height) >= ThinLineMinLength)
                return false;
            if (component.Width >= WideBandMinWidth && component.Height >= WideBandMinHeight)
                return false;
            return true;
        }

        /// <summary>
        /// True when a pink component could be a SOD/SOP ground drop. The pixel test
        /// classifies the whole hot-pink color family, so unrelated pink-ish terrain
        /// or effect pixels can also pass it; those still never get collected because
        /// every probe is confirmed against the game memory in TryCollectAt. There is
        /// NO upper size limit: the 0.6x0.6 world square can render far larger than a
        /// few pixels depending on camera distance, and an area/dimension cap would
        /// filter the real drop out. Only the minimum loot-square size is enforced
        /// (<c>BotConstants.Loot.PinkMinCandidate*</c>, 10x10 px) — smaller specks
        /// are skipped (treated as not-loot) so the probe budget is not burned on
        /// noise.
        /// </summary>
        internal static bool IsPinkLootCandidate(WhiteComponent component)
        {
            return component.Width >= BotConstants.Loot.PinkMinCandidateWidth &&
                   component.Height >= BotConstants.Loot.PinkMinCandidateHeight;
        }

        /// <summary>
        /// Identifies only sparse, approximately circular magenta ring components with the
        /// configured artificial mob-marker scale. Solid SOD/SOP squares keep their existing
        /// candidate behavior. This guards the pink scan's retry state from remaining armed on
        /// living mobs whose magenta marker can never pass the exact item mouseover check.
        /// </summary>
        internal static bool IsMobMarkerRingComponent(WhiteComponent component, Bitmap bitmap)
        {
            float expectedRadius = Math.Max(6f,
                BotConstants.MobGrouping.ExpectedMarkerRadiusPx * bitmap.Height /
                BotConstants.MobGrouping.ReferenceClientHeightPx);
            int minDimension = Math.Min(component.Width, component.Height);
            int maxDimension = Math.Max(component.Width, component.Height);
            if (minDimension < BotConstants.Loot.PinkMinCandidateWidth ||
                maxDimension > expectedRadius * 8f)
                return false;

            float aspectRatio = maxDimension / (float)Math.Max(minDimension, 1);
            if (aspectRatio > 3.5f)
                return false;

            float fillRatio = component.Area / (float)Math.Max(component.Width * component.Height, 1);
            return fillRatio <= 0.38f;
        }

        /// <summary>
        /// Finds glyph-like components arranged in horizontal runs (mob-name text) and
        /// returns them so they are excluded from probing. Loot sparkles are few and
        /// never arranged in long tight rows, so they survive this filter.
        /// </summary>
        private static HashSet<WhiteComponent> FindTextGlyphs(List<WhiteComponent> candidates)
        {
            var glyphs = new List<WhiteComponent>();
            foreach (WhiteComponent c in candidates)
            {
                if (c.Area <= TextClusterMaxGlyphArea &&
                    c.Width <= TextClusterMaxGlyphWidth &&
                    c.Height <= TextClusterMaxGlyphHeight)
                    glyphs.Add(c);
            }
            glyphs.Sort((a, b) => a.XMin != b.XMin ? a.XMin.CompareTo(b.XMin) : a.YMin.CompareTo(b.YMin));

            var textGlyphs = new HashSet<WhiteComponent>();
            var run = new List<WhiteComponent>();

            void FlushRun()
            {
                if (run.Count >= TextClusterMinGlyphs)
                {
                    foreach (WhiteComponent glyph in run)
                        textGlyphs.Add(glyph);
                }
                run.Clear();
            }

            foreach (WhiteComponent glyph in glyphs)
            {
                if (run.Count > 0)
                {
                    WhiteComponent prev = run[run.Count - 1];
                    int gap = glyph.XMin - prev.XMax - 1;
                    double prevCenterY = (prev.YMin + prev.YMax) / 2.0;
                    double centerY = (glyph.YMin + glyph.YMax) / 2.0;

                    if (gap > TextClusterMaxGapPx || Math.Abs(centerY - prevCenterY) > TextClusterMaxBaselineDriftPx)
                        FlushRun();
                }
                run.Add(glyph);
            }
            FlushRun();

            return textGlyphs;
        }

        /// <summary>
        /// Deterministic two-stage mouseover plan for one accepted white-loot component.
        /// Stage A (<see cref="Coverage"/>) probes one actual white pixel per occupied
        /// spatial cell, ranked by cell density and then by distance from the component
        /// centroid, so concentrated/interior regions are tested first and the whole
        /// component shape is covered before neighbours are repeated. Stage B
        /// (<see cref="Exhaustive"/>) contains every remaining white pixel of the same
        /// component, ordered centre-out, so an unsuccessful coverage sweep can still
        /// exhaust the candidate. Every point belongs to the component's detected white
        /// mask — normal white-loot probing never moves the cursor onto non-white pixels.
        /// </summary>
        internal sealed class WhiteProbePlan
        {
            public WhiteProbePlan(
                IReadOnlyList<Point> coverage,
                IReadOnlyList<Point> exhaustive,
                int cellSizePx,
                int occupiedCells)
            {
                Coverage = coverage;
                Exhaustive = exhaustive;
                CellSizePx = cellSizePx;
                OccupiedCells = occupiedCells;

                var all = new List<Point>(coverage.Count + exhaustive.Count);
                all.AddRange(coverage);
                all.AddRange(exhaustive);
                Points = all;
            }

            /// <summary>Stage A: spatially distributed representatives (one per occupied cell).</summary>
            public IReadOnlyList<Point> Coverage { get; }

            /// <summary>Stage B: every remaining component pixel, ordered centre-out.</summary>
            public IReadOnlyList<Point> Exhaustive { get; }

            /// <summary>Coverage followed by exhaustive fallback, with no duplicate coordinates.</summary>
            public IReadOnlyList<Point> Points { get; }

            public int CellSizePx { get; }
            public int OccupiedCells { get; }
        }

        private static int CompareRowMajor(Point a, Point b)
            => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X);

        /// <summary>
        /// Builds the deterministic two-stage probe plan for one white component. The
        /// plan is based only on the component's own detected pixels, so it is pure and
        /// testable without a live game window.
        /// </summary>
        internal static WhiteProbePlan BuildWhiteProbePlan(WhiteComponent component)
        {
            ArgumentNullException.ThrowIfNull(component);

            var pixels = new List<Point>(component.Points);
            pixels.Sort(CompareRowMajor);
            if (pixels.Count == 0)
            {
                return new WhiteProbePlan(
                    Array.Empty<Point>(), Array.Empty<Point>(),
                    WhiteProbeCellMinPx, 0);
            }

            int cellSize = Math.Max(
                WhiteProbeCellMinPx,
                (int)Math.Ceiling(Math.Max(component.Width, component.Height) / (double)WhiteProbeCellsPerAxis));

            var occupiedCells = new Dictionary<(int CellX, int CellY), List<Point>>();
            foreach (Point pixel in pixels)
            {
                var cellKey = (
                    (pixel.X - component.XMin) / cellSize,
                    (pixel.Y - component.YMin) / cellSize);
                if (!occupiedCells.TryGetValue(cellKey, out List<Point>? cellPixels))
                {
                    cellPixels = new List<Point>();
                    occupiedCells.Add(cellKey, cellPixels);
                }
                cellPixels.Add(pixel);
            }

            double centroidX = 0;
            double centroidY = 0;
            foreach (Point pixel in pixels)
            {
                centroidX += pixel.X;
                centroidY += pixel.Y;
            }
            centroidX /= pixels.Count;
            centroidY /= pixels.Count;

            // Prefer dense/interior cells first; ties fall back to distance from the
            // component centre and finally to a deterministic row-major cell order.
            var rankedCells = new List<KeyValuePair<(int CellX, int CellY), List<Point>>>(occupiedCells);
            rankedCells.Sort((left, right) =>
            {
                int density = right.Value.Count.CompareTo(left.Value.Count);
                if (density != 0)
                    return density;

                double leftDx = component.XMin + left.Key.CellX * cellSize + cellSize / 2.0 - centroidX;
                double leftDy = component.YMin + left.Key.CellY * cellSize + cellSize / 2.0 - centroidY;
                double rightDx = component.XMin + right.Key.CellX * cellSize + cellSize / 2.0 - centroidX;
                double rightDy = component.YMin + right.Key.CellY * cellSize + cellSize / 2.0 - centroidY;
                int distance = (leftDx * leftDx + leftDy * leftDy)
                    .CompareTo(rightDx * rightDx + rightDy * rightDy);
                if (distance != 0)
                    return distance;

                int cellY = left.Key.CellY.CompareTo(right.Key.CellY);
                return cellY != 0 ? cellY : left.Key.CellX.CompareTo(right.Key.CellX);
            });

            var coverage = new List<Point>(rankedCells.Count);
            var coverageSet = new HashSet<Point>();
            foreach (var cell in rankedCells)
            {
                Point representative = SelectCellRepresentative(cell.Value);
                if (coverageSet.Add(representative))
                    coverage.Add(representative);
            }

            // Every white pixel that was not used by Stage A remains eligible. Centre-out
            // ordering keeps the fallback visually similar to the coverage pass instead of
            // re-scanning one row from left to right.
            var exhaustive = new List<Point>(pixels.Count - coverage.Count);
            foreach (Point pixel in pixels)
            {
                if (coverageSet.Contains(pixel))
                    continue;
                exhaustive.Add(pixel);
            }
            exhaustive.Sort((left, right) =>
            {
                long leftDx = left.X - (long)centroidX;
                long leftDy = left.Y - (long)centroidY;
                long rightDx = right.X - (long)centroidX;
                long rightDy = right.Y - (long)centroidY;
                int distance = (leftDx * leftDx + leftDy * leftDy)
                    .CompareTo(rightDx * rightDx + rightDy * rightDy);
                return distance != 0 ? distance : CompareRowMajor(left, right);
            });

            return new WhiteProbePlan(coverage, exhaustive, cellSize, occupiedCells.Count);
        }

        /// <summary>Picks an actual detected-white pixel closest to the cell's own white centroid.</summary>
        private static Point SelectCellRepresentative(List<Point> cellPixels)
        {
            if (cellPixels.Count == 1)
                return cellPixels[0];

            double centroidX = 0;
            double centroidY = 0;
            foreach (Point pixel in cellPixels)
            {
                centroidX += pixel.X;
                centroidY += pixel.Y;
            }
            centroidX /= cellPixels.Count;
            centroidY /= cellPixels.Count;

            Point best = cellPixels[0];
            double bestDistance = double.MaxValue;
            foreach (Point pixel in cellPixels)
            {
                double dx = pixel.X - centroidX;
                double dy = pixel.Y - centroidY;
                double distance = dx * dx + dy * dy;
                if (distance < bestDistance ||
                    (distance == bestDistance && CompareRowMajor(pixel, best) < 0))
                {
                    best = pixel;
                    bestDistance = distance;
                }
            }
            return best;
        }

        /// <summary>
        /// Bounded, deterministic probe sweep over the accepted candidates. Normal white
        /// loot is interleaved round-robin between candidates so a large component cannot
        /// consume the whole budget before smaller components are touched. Pink SOD/SOP
        /// keeps its existing candidate-by-candidate order and probes every pixel.
        /// Already-examined white coordinates from a previous incomplete pass are skipped
        /// so a bounded budget continues instead of restarting the same prefix forever.
        /// </summary>
        internal sealed class LootProbePlanner
        {
            private readonly List<List<Point>> _points;
            private readonly List<int> _next;
            private readonly bool _interleave;
            private int _cursor;

            private LootProbePlanner(
                List<List<Point>> points,
                bool interleave,
                int previouslyProbedSkipped)
            {
                _points = points;
                _next = new List<int>(points.Count);
                for (int i = 0; i < points.Count; i++)
                    _next.Add(0);
                _interleave = interleave;
                PreviouslyProbedSkipped = previouslyProbedSkipped;

                int initialRemaining = 0;
                var initialPerCandidate = new int[points.Count];
                for (int i = 0; i < points.Count; i++)
                {
                    initialPerCandidate[i] = points[i].Count;
                    initialRemaining += points[i].Count;
                }
                InitialRemainingProbes = initialRemaining;
                InitialRemainingPerCandidate = initialPerCandidate;
            }

            public static LootProbePlanner CreateForWhite(
                IReadOnlyList<WhiteComponent> candidates,
                IReadOnlySet<Point>? alreadyProbed)
            {
                ArgumentNullException.ThrowIfNull(candidates);
                var points = new List<List<Point>>(candidates.Count);
                int skipped = 0;
                foreach (WhiteComponent candidate in candidates)
                {
                    WhiteProbePlan plan = BuildWhiteProbePlan(candidate);
                    var remaining = new List<Point>(plan.Points.Count);
                    foreach (Point point in plan.Points)
                    {
                        if (alreadyProbed != null && alreadyProbed.Contains(point))
                        {
                            skipped++;
                            continue;
                        }
                        remaining.Add(point);
                    }
                    points.Add(remaining);
                }
                return new LootProbePlanner(points, interleave: true, skipped);
            }

            public static LootProbePlanner CreateForPink(IReadOnlyList<WhiteComponent> candidates)
            {
                ArgumentNullException.ThrowIfNull(candidates);
                var points = new List<List<Point>>(candidates.Count);
                foreach (WhiteComponent candidate in candidates)
                    points.Add(new List<Point>(candidate.Points));
                return new LootProbePlanner(points, interleave: false, previouslyProbedSkipped: 0);
            }

            public int PreviouslyProbedSkipped { get; }
            public int InitialRemainingProbes { get; }
            private int[] InitialRemainingPerCandidate { get; }

            public int RemainingProbeCount
            {
                get
                {
                    int remaining = 0;
                    for (int i = 0; i < _points.Count; i++)
                        remaining += _points[i].Count - _next[i];
                    return remaining;
                }
            }

            public int CandidatesWithRemainingProbes
            {
                get
                {
                    int count = 0;
                    for (int i = 0; i < _points.Count; i++)
                    {
                        if (_next[i] < _points[i].Count)
                            count++;
                    }
                    return count;
                }
            }

            public int AttemptsFor(int candidateIndex)
                => candidateIndex >= 0 && candidateIndex < _next.Count ? _next[candidateIndex] : 0;

            public int InitialRemainingFor(int candidateIndex)
                => candidateIndex >= 0 && candidateIndex < InitialRemainingPerCandidate.Length
                    ? InitialRemainingPerCandidate[candidateIndex]
                    : 0;

            public bool TryNext(out int candidateIndex, out Point point)
            {
                if (_points.Count == 0)
                {
                    candidateIndex = -1;
                    point = default;
                    return false;
                }

                if (!_interleave)
                {
                    while (_cursor < _points.Count)
                    {
                        List<Point> candidatePoints = _points[_cursor];
                        if (_next[_cursor] < candidatePoints.Count)
                        {
                            point = candidatePoints[_next[_cursor]];
                            _next[_cursor]++;
                            candidateIndex = _cursor;
                            return true;
                        }
                        _cursor++;
                    }

                    candidateIndex = -1;
                    point = default;
                    return false;
                }

                for (int offset = 0; offset < _points.Count; offset++)
                {
                    int index = (_cursor + offset) % _points.Count;
                    List<Point> candidatePoints = _points[index];
                    if (_next[index] >= candidatePoints.Count)
                        continue;

                    point = candidatePoints[_next[index]];
                    _next[index]++;
                    _cursor = (index + 1) % _points.Count;
                    candidateIndex = index;
                    return true;
                }

                candidateIndex = -1;
                point = default;
                return false;
            }
        }

        /// <summary>
        /// A sweep that stops while candidate pixels remain unexamined is Incomplete —
        /// never an empty pass. Used by the scan loop after the bounded probe sequence.
        /// </summary>
        internal static LootScanPassResult ResolveProbeSweepResult(int remainingCandidateProbes)
            => remainingCandidateProbes > 0
                ? LootScanPassResult.Incomplete
                : LootScanPassResult.NoItem;

        /// <summary>
        /// Key that ties white-loot probe progress to the exact screen state and accepted
        /// candidate geometry. A different key means the character/camera/frame changed,
        /// so previously examined coordinates must not be trusted.
        /// </summary>
        internal static string BuildWhiteProbeProgressKey(
            string regionName,
            LootClientGeometry? geometry,
            IReadOnlyList<WhiteComponent> candidates)
        {
            string geometryKey = geometry.HasValue
                ? $"{geometry.Value.WindowHandle.ToInt64():X}:" +
                  $"{geometry.Value.ClientScreenOrigin.X},{geometry.Value.ClientScreenOrigin.Y}:" +
                  $"{geometry.Value.ClientSize.Width}x{geometry.Value.ClientSize.Height}"
                : "no-geometry";
            string candidateKey = string.Join(";", candidates.Select(c =>
                $"{c.XMin},{c.YMin},{c.XMax},{c.YMax},{c.Area}"));
            return $"{regionName}|{geometryKey}|{candidateKey}";
        }

        private void ClearWhiteProbeProgress()
        {
            _probedWhiteProbeProgress.Clear();
            _whiteProbeProgressKey = string.Empty;
        }

        private static string FormatWhiteComponent(WhiteComponent component)
            => $"bounds=({component.XMin},{component.YMin})-({component.XMax},{component.YMax}) " +
               $"{component.Area}px fill={component.Area / (float)Math.Max(component.Width * component.Height, 1):F2}";

        /// <summary>Compact candidate bounds/pixel summary; bounded so normal logs stay short.</summary>
        private static string DescribeWhiteCandidates(IReadOnlyList<WhiteComponent> candidates)
        {
            const int maxDetails = 8;
            string details = string.Join("; ", candidates
                .Take(maxDetails)
                .Select((candidate, index) => $"#{index + 1} {FormatWhiteComponent(candidate)}"));
            int hidden = candidates.Count - maxDetails;
            return hidden > 0 ? $"{details}; +{hidden} more" : details;
        }

        /// <summary>Per-candidate probe attempts, bounded to the first few candidates.</summary>
        private static string DescribeProbeAttempts(LootProbePlanner planner, int candidateCount)
        {
            const int maxDetails = 8;
            int shown = Math.Min(candidateCount, maxDetails);
            var parts = new List<string>(shown + 1);
            for (int i = 0; i < shown; i++)
                parts.Add($"#{i + 1}={planner.AttemptsFor(i)}/{planner.InitialRemainingFor(i)}");
            if (candidateCount > shown)
                parts.Add($"+{candidateCount - shown} more");
            return string.Join(", ", parts);
        }

        private LootProbeResult CheckProbeCaptureOwnership(bool pinkOnly)
        {
            if (_scanToken.IsCancellationRequested || ShouldAbortLoot() ||
                (pinkOnly && !PinkLootOnlyMode))
                return LootProbeResult.Cancelled;

            lock (_captureGate)
            {
                LootClientGeometry? current = RefreshClientOrigin();
                if (!current.HasValue || !current.Value.IsValid)
                {
                    _log("[LootCapture] Probe aborted: current client geometry became invalid.");
                    return LootProbeResult.CaptureInvalid;
                }
                if (GetForegroundWindow() != current.Value.WindowHandle)
                    return LootProbeResult.Cancelled;

                if (!_lastStableCaptureGeometry.HasValue ||
                    current.Value != _lastStableCaptureGeometry.Value)
                {
                    _log("[LootCapture] Probe aborted: client geometry changed since the analyzed frame.");
                    return LootProbeResult.CaptureInvalid;
                }
                return LootProbeResult.Miss;
            }
        }

        private bool CaptureScreen()
        {
            lock (_captureGate)
            {
                StableLootCaptureResult result = LootCaptureGeometry.CaptureStableFrame(
                    RefreshClientOrigin,
                    GetForegroundWindow,
                    geometry =>
                    {
                        if (!_hasClientOrigin || _bitmap == null || _graphics == null ||
                            _bitmap.Width != geometry.ClientSize.Width ||
                            _bitmap.Height != geometry.ClientSize.Height)
                        {
                            throw new InvalidOperationException("Capture bitmap does not match the resolved client geometry.");
                        }

                        _graphics.CopyFromScreen(
                            geometry.ClientScreenOrigin.X,
                            geometry.ClientScreenOrigin.Y,
                            0,
                            0,
                            geometry.ClientSize);
                    });

                _lastCaptureResult = result;
                if (!result.Succeeded)
                {
                    _lastStableCaptureGeometry = null;
                    string failureKey = $"{result.Status}:{result.Error}";
                    DateTime now = DateTime.UtcNow;
                    if (failureKey != _lastLoggedCaptureFailure ||
                        (now - _lastCaptureFailureLogAt).TotalSeconds >= 1)
                    {
                        _lastLoggedCaptureFailure = failureKey;
                        _lastCaptureFailureLogAt = now;
                        LootClientGeometry g = result.Geometry;
                        _log($"[LootCapture] CAPTURE FAILED status={result.Status} attempts={result.Attempts} " +
                             $"hwnd=0x{g.WindowHandle.ToInt64():X} window={FormatRectangle(g.WindowRectangle)} " +
                             $"clientOrigin=({g.ClientScreenOrigin.X},{g.ClientScreenOrigin.Y}) " +
                             $"client={g.ClientSize.Width}x{g.ClientSize.Height} error={result.Error}");
                    }
                    return false;
                }

                _lastLoggedCaptureFailure = string.Empty;
                _lastCaptureFailureLogAt = DateTime.MinValue;
                _lastStableCaptureGeometry = result.Geometry;
                if (result.Attempts > 1)
                {
                    _log($"[LootCapture] Geometry changed during capture; stable retry succeeded after {result.Attempts} attempts. " +
                         $"hwnd=0x{result.Geometry.WindowHandle.ToInt64():X} " +
                         $"window={FormatRectangle(result.Geometry.WindowRectangle)} " +
                         $"clientOrigin=({result.Geometry.ClientScreenOrigin.X},{result.Geometry.ClientScreenOrigin.Y}) " +
                         $"client={result.Geometry.ClientSize.Width}x{result.Geometry.ClientSize.Height}");
                }
                return true;
            }
        }

        private LootProbeResult TryCollectAt(int x, int y, bool pinkOnly = false)
        {
            LootProbeResult guard = CheckProbeCaptureOwnership(pinkOnly);
            if (guard != LootProbeResult.Miss)
                return guard;

            WaitMouseInPosition(x, y, pinkOnly);
            guard = CheckProbeCaptureOwnership(pinkOnly);
            if (guard != LootProbeResult.Miss)
                return guard;

            // Pink candidates are identified by the scan. Confirm lootability
            // with the calibrated mouseover, not the separate highlighted-type
            // field, which need not describe the ground item under this cursor.
            // The read always follows THIS cursor move (never a cached result).
            if (WaitForLootMouseOver(pinkOnly))
            {
                if (pinkOnly)
                {
                    _log("[Loot] Pink candidate mouseover matches calibration — collecting.");

                    // A SOD/SOP scroll weighs 1 lb: when the bag is at the weight limit
                    // the pickup would fail, so free weight BEFORE clicking the scroll.
                    MakeRoomForScrollIfFull();
                }

                guard = CheckProbeCaptureOwnership(pinkOnly);
                if (guard != LootProbeResult.Miss)
                    return guard;
                CollectionClick();
                return LootProbeResult.Collected;
            }
            return LootProbeResult.Miss;
        }

        private LootProbeResult TryCollectAtWithCount(
            int x, int y, bool pinkOnly, ref int cursorProbes)
        {
            cursorProbes++;
            return TryCollectAt(x, y, pinkOnly);
        }

        private static string ProbeOutcomeName(LootProbeResult result) => result switch
        {
            LootProbeResult.Collected => "collected",
            LootProbeResult.CaptureInvalid => "capture-geometry-changed-during-probes",
            _ => "probe-cancelled-or-focus-lost"
        };

        private static LootScanPassResult ProbeOutcomeToScanResult(LootProbeResult result) => result switch
        {
            LootProbeResult.Collected => LootScanPassResult.Collected,
            LootProbeResult.CaptureInvalid => LootScanPassResult.CaptureFailed,
            _ => LootScanPassResult.Cancelled
        };

        private static LootScanPassResult HandleProbeResult(
            LootProbeResult result, ref string diagnosticOutcome)
        {
            diagnosticOutcome = ProbeOutcomeName(result);
            return ProbeOutcomeToScanResult(result);
        }

        /// <summary>
        /// Which potion stack the bot drinks to free weight for a SOD/SOP scroll.
        /// </summary>
        internal enum WeightReliefPotion { None, Hp, Mana }

        /// <summary>
        /// True when the player's bag has no room for one SOD/SOP scroll
        /// (<see cref="BotConstants.Loot.ScrollWeightPounds"/> lb). An unreadable
        /// weight (max ≤ 0) never requests relief.
        /// </summary>
        internal static bool NeedsWeightReliefForScroll(int currentWeight, int maxWeight)
            => maxWeight > 0 &&
               currentWeight + BotConstants.Loot.ScrollWeightPounds > maxWeight;

        /// <summary>
        /// Picks the potion stack to drink for weight relief: the type the player has
        /// MORE of (ties go to HP). The last potion of each type is never drunk — it is
        /// the inventory-slot reserve (<see cref="BotConstants.Repot.PotionSlotReserve"/>).
        /// Returns <see cref="WeightReliefPotion.None"/> when both stacks are at the reserve.
        /// </summary>
        internal static WeightReliefPotion ChooseWeightReliefPotion(int hpPotions, int manaPotions)
        {
            int reserve = BotConstants.Repot.PotionSlotReserve;
            bool hpAvailable = hpPotions > reserve;
            bool manaAvailable = manaPotions > reserve;

            if (!hpAvailable && !manaAvailable)
                return WeightReliefPotion.None;
            if (hpAvailable && (!manaAvailable || hpPotions >= manaPotions))
                return WeightReliefPotion.Hp;
            return WeightReliefPotion.Mana;
        }

        /// <summary>
        /// Frees weight for one SOD/SOP scroll when the bag is full. The pink scan
        /// already confirmed a loot item under the cursor; without relief the click
        /// would walk to the scroll and fail with the game's "too heavy" message.
        /// The potion type the player has more of is drunk (key 1 = HP, key 2 = mana)
        /// — one potion is enough for one 1-lb scroll — and the weight is polled until
        /// the scroll fits or the timeout expires. The last potion of each type is
        /// always kept as the inventory-slot reserve; when both stacks are at the
        /// reserve nothing can be drunk and the pickup is attempted anyway.
        /// </summary>
        private void MakeRoomForScrollIfFull()
        {
            var (currentWeight, maxWeight) = _memoryService.GetWeight();
            if (!NeedsWeightReliefForScroll(currentWeight, maxWeight))
                return;

            _log($"[Loot] SOD/SOP found but weight is full ({currentWeight}/{maxWeight}) — drinking a potion to free {BotConstants.Loot.ScrollWeightPounds} lb.");

            int hpPotions = _memoryService.GetHpPotionCount();
            int manaPotions = _memoryService.GetManaPotionCount();
            WeightReliefPotion choice = ChooseWeightReliefPotion(hpPotions, manaPotions);
            if (_scanToken.IsCancellationRequested || ShouldAbortLoot() || !IsGameWindowFocused())
                return;
            if (choice == WeightReliefPotion.None)
            {
                _log($"[Loot] Weight full ({currentWeight}/{maxWeight}) but both potion stacks are at the slot reserve (HP {hpPotions}, mana {manaPotions}) — cannot free weight.");
                return;
            }

            if (choice == WeightReliefPotion.Hp)
                GameInput.PressKey(GameInput.VK_1, GameInput.SCAN_1);
            else
                GameInput.PressKey(GameInput.VK_2, GameInput.SCAN_2);

            _log($"[Loot] Drank {(choice == WeightReliefPotion.Hp ? "HP" : "mana")} potion (had HP {hpPotions}, mana {manaPotions}) — waiting for the weight to drop.");

            // The game applies the drink and updates the weight value asynchronously;
            // poll briefly until the scroll fits (or the timeout / abort hits).
            var deadline = DateTime.UtcNow.AddMilliseconds(BotConstants.Delays.WeightReliefTimeoutMs);
            do
            {
                Thread.Sleep(BotConstants.Delays.WeightReliefPollMs);
                if (_scanToken.IsCancellationRequested || ShouldAbortLoot())
                    return;
                (currentWeight, maxWeight) = _memoryService.GetWeight();
            }
            while (NeedsWeightReliefForScroll(currentWeight, maxWeight) && DateTime.UtcNow < deadline);

            if (NeedsWeightReliefForScroll(currentWeight, maxWeight))
                _log($"[Loot] Weight still full ({currentWeight}/{maxWeight}) after the potion — attempting the pickup anyway.");
            else
                _log($"[Loot] Weight now {currentWeight}/{maxWeight} — room for the SOD/SOP scroll.");
        }

        private void CollectionClick()
        {
            int positionBeforeClick = GetPositionX();
            bool leftButtonDown = false;
            _isCollecting = true;
            try
            {
            // Release any held right button, then left-click to pick up the item.
            MouseOperations.MouseEvent(MouseOperations.MouseEventFlags.RightUp);
            MouseOperations.MouseEvent(MouseOperations.MouseEventFlags.LeftDown);
            leftButtonDown = true;

            _log("Collecting item (SOD/SOP by pixel color + mouseover indicator).");
            Thread.Sleep(BotConstants.Delays.CollectClickHoldMs);

            MouseOperations.MouseEvent(MouseOperations.MouseEventFlags.LeftUp);
            leftButtonDown = false;

            // Spam spacebar while the character auto-walks to the clicked item.
            // There is NO hardcoded timeout — we track the player's X position
            // to detect when the character starts moving (click registered) and
            // when they stop (arrived at item).  Spacebar keeps pressing during
            // the entire walk so the character also area-loots any other items
            // it passes along the way.
            int checksumBefore = _memoryService.ComputeInventoryChecksum();
            int beforeX = positionBeforeClick;
            int lastX = beforeX;
            int stableChecks = 0;
            const int stableRequired = 3;
            int emptyRounds = 0;
            const int maxEmptyRounds = 10;

            while (true)
            {
                // If the player entered city, abort immediately. A mob being selected
                // only aborts the spacebar spam outside loot-priority mode — in
                // priority mode the walk to the clicked loot item must finish.
                if (_scanToken.IsCancellationRequested || ShouldAbortLoot() || !IsGameWindowFocused())
                {
                    _log("[Loot] Stop/city/focus change detected during collection — aborting spacebar spam.");
                    break;
                }

                GameInput.PressKey(GameInput.VK_SPACE, GameInput.SCAN_SPACE);
                Thread.Sleep(30);
                if (_scanToken.IsCancellationRequested || ShouldAbortLoot() || !IsGameWindowFocused())
                    break;
                GameInput.PressKey(GameInput.VK_SPACE, GameInput.SCAN_SPACE);
                Thread.Sleep(100);

                int checksumAfter = _memoryService.ComputeInventoryChecksum();
                if (checksumAfter != checksumBefore)
                {
                    // Item collected along the way — reset baseline, keep spamming.
                    checksumBefore = checksumAfter;
                    emptyRounds = 0;
                    continue;
                }

                emptyRounds++;

                int currentX = GetPositionX();
                bool hasMoved = currentX != beforeX;

                if (hasMoved)
                {
                    if (currentX == lastX)
                    {
                        stableChecks++;
                        if (stableChecks >= stableRequired)
                        {
                            // Character reached destination and stopped — done.
                            break;
                        }
                    }
                    else
                    {
                        stableChecks = 0; // still walking
                    }
                    lastX = currentX;
                }

                // Safety exit: no movement at all or walking too long with no pickups.
                if (emptyRounds >= maxEmptyRounds)
                {
                    break;
                }
            }

            Thread.Sleep(BotConstants.Delays.CollectAnimationMs); // Wait for collection animation/movement

            UnbugWhenCollecting(positionBeforeClick);

            // PixelScanUnderChar disabled — spam spacebar instead.
            // PixelScanUnderChar();

            // Force a fresh screen capture here so the next scan does NOT use the
            // stale bitmap from before the click (character has moved, item is gone,
            // screen content has changed).
            bool postClickCaptureSucceeded = CaptureScreen();
            if (postClickCaptureSucceeded)
            {
                _log("[Loot] Fresh screen capture forced after click.");

                // Loot diagnostics: also save the post-collection frame so the state the
                // character ended up in (item gone / still on the ground / walked away)
                // can be compared with the frame from the scan that clicked it.
                ScreenshotService.SaveLootScanFrame(
                    ScreenshotService.CloneLootScanFrame(_bitmap), "AfterClick", "post-click", _log);
            }
            else
            {
                _log("[LootCapture] Post-click frame capture failed; no frame was interpreted as empty.");
            }
            }
            finally
            {
                if (leftButtonDown)
                {
                    try { MouseOperations.MouseEvent(MouseOperations.MouseEventFlags.LeftUp); }
                    catch (Exception ex) { _log($"[Loot] Failed to release left mouse button after collection: {ex.Message}"); }
                }
                // Collection finished (item picked up / aborted) — the movement and
                // combat systems may resume attacking.
                _isCollecting = false;
            }
        }

        private void UnbugWhenCollecting(int beforeClickPosX)
        {
            int currentX = GetPositionX();
            if (beforeClickPosX == currentX)
            {
                // Hardcoded UnbugClickX/Y are screen-absolute → convert to bitmap-local.
                int refX = _referenceClientOriginX;
                int refY = _referenceClientOriginY;
                int bx = Math.Clamp(BotConstants.Loot.UnbugClickX - refX, 0, Math.Max(_clientWidth - 1, 0));
                int by = Math.Clamp(BotConstants.Loot.UnbugClickY - refY, 0, Math.Max(_clientHeight - 1, 0));
                var (screenX, screenY) = BitmapLocalToScreen(bx, by);
                MouseOperations.MoveAndLeftClickAbsolute(screenX, screenY, 100);
            }
        }

        // DISABLED — spam spacebar in CollectionClick instead.
        // private void PixelScanUnderChar()
        // {
        //     int refX = _referenceClientOriginX;
        //     int refY = _referenceClientOriginY;
        //
        //     // Convert hardcoded screen-absolute → bitmap-local, then clamp.
        //     int xStart = Math.Clamp(BotConstants.Loot.UnderCharScanStartX - refX, 0, Math.Max(_clientWidth - 1, 0));
        //     int xEnd   = Math.Clamp(BotConstants.Loot.UnderCharScanEndX   - refX, 0, Math.Max(_clientWidth, 0));
        //     int yStart = Math.Clamp(BotConstants.Loot.UnderCharScanStartY - refY, 0, Math.Max(_clientHeight - 1, 0));
        //     int yEnd   = Math.Clamp(BotConstants.Loot.UnderCharScanEndY   - refY, 0, Math.Max(_clientHeight, 0));
        //
        //     for (int x = xStart; x < xEnd; x += BotConstants.Loot.UnderCharScanStepX)
        //     {
        //         for (int y = yStart; y < yEnd; y += BotConstants.Loot.UnderCharScanStepY)
        //         {
        //             WaitMouseInPosition(x, y);
        //             if (_memoryService.IsLootMouseOver())
        //             {
        //                 CollectionClick();
        //                 return;
        //             }
        //         }
        //     }
        // }

        private void WaitMouseInPosition(int bitmapLocalX, int bitmapLocalY, bool pinkOnly = false)
        {
            // Clamp to actual client area as safety net.
            int clampedX = Math.Clamp(bitmapLocalX, 0, Math.Max(_clientWidth - 1, 0));
            int clampedY = Math.Clamp(bitmapLocalY, 0, Math.Max(_clientHeight - 1, 0));
            var (screenX, screenY) = BitmapLocalToScreen(clampedX, clampedY);
            MouseOperations.SetCursorPositionAbsolute(screenX, screenY);

            // The client updates its mouseover memory asynchronously. The previous
            // normal-loot value (1 ms) was too short — eight moves completed in ~22 ms
            // without ever observing the updated state. Pink keeps its existing 5 ms
            // settle; normal white loot uses the bounded configurable interval below.
            int settleMs = pinkOnly
                ? BotConstants.Delays.SodSopMouseOverSettleMs
                : BotConstants.Delays.LootWhiteMouseOverSettleMs;
            _scanToken.WaitHandle.WaitOne(settleMs);
        }

        /// <summary>
        /// Reads the calibrated "loot under cursor" indicator after the cursor has
        /// settled. Normal white loot re-reads a negative result a bounded number of
        /// times so a late asynchronous mouseover update is not recorded as a miss.
        /// A read from a previous cursor position is never reused: each probe waits for
        /// its own settle first and then reads the live memory value again.
        /// </summary>
        private bool WaitForLootMouseOver(bool pinkOnly)
        {
            if (_memoryService.IsLootMouseOver())
                return true;
            if (pinkOnly)
                return false;

            for (int poll = 0; poll < BotConstants.Delays.LootWhiteMouseOverExtraPolls; poll++)
            {
                if (_scanToken.WaitHandle.WaitOne(BotConstants.Delays.LootWhiteMouseOverExtraPollMs))
                    return false;
                if (_memoryService.IsLootMouseOver())
                    return true;
            }
            return false;
        }

        private int GetPositionX()
        {
             return _memoryService.GetLootPositionX();
        }
    }
}
