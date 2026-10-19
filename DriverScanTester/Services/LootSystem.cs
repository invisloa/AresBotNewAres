using System;
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
        internal enum LootScanPassResult { Collected, NoItem, CaptureFailed, Cancelled }
        private enum LootProbeResult { Miss, Collected, CaptureInvalid, Cancelled }

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
               _isCollecting || IsLootCycleActive;

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
            short previousCamera = BotConstants.Camera.DefaultDistanceLock;
            if (_memoryService.TryGetCameraDistance(out short readDistance))
                previousCamera = readDistance;
            else
                _log("[LootTest] Previous camera distance unreadable; the default lock will be restored after the test.");

            short scanCamera = pinkOnly
                ? BotConstants.Camera.SodSopLootScanDistance
                : BotConstants.Camera.LootScanDistance;
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
                     $"(client {_clientWidth}x{_clientHeight}, camera {scanCamera}, mode={(pinkOnly ? "pink-only" : "white-loot")}, " +
                     $"item mouseover calibration={mouseOverCalibration} (0x{(uint)mouseOverCalibration:X8})" +
                     (mouseOverCalibration == 0
                         ? " — CAPTURE IT with 'Capture Item Mouseover' or no probe can ever confirm a pickup)."
                         : ")."));

                _memoryService.SetCameraDistance(scanCamera);
                cameraSet = true;
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
                    if (cursorSaved)
                        MouseOperations.SetCursorPositionAbsolute(cursorX, cursorY);
                }
                catch (Exception ex)
                {
                    _log($"[LootTest] Cleanup failed (camera/cursor restore): {ex.Message}");
                }
                _pinkLootOnlyMode = previousPinkMode;
                ScreenshotService.LootScanCaptureEnabled = previousCaptureEnabled;
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
                LootScanPassResult.Cancelled => BuildDiagnosticResult(
                    mode, LootDiagnosticOutcome.Cancelled, items, passes,
                    "Scan cancelled (timeout, focus loss, or city entry)."),
                _ => BuildDiagnosticResult(
                    mode, LootDiagnosticOutcome.NoItem, items, passes,
                    $"Scan completed ({passes} pass(es)).")
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
                CancelAllLootWork("game window is not foreground");
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
                    // over and the next kill re-arms the phase.
                    IsLootingActive = false;

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
                    // ── Zoom camera in so ground items appear larger and their
                    //    white highlights / name text become more detectable. ──
                    _memoryService.SetCameraDistance(BotConstants.Camera.LootScanDistance);

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

                    // ── Pixel scan with spacebar spam on a background thread.
                    //    Each successful scan loots one (or more) items, then we
                    //    rescan to catch any other items that may be visible.
                    //    Items under the character (exclude zone) get collected
                    //    via the background spacebar spam simultaneously. ──
                    StartScanSpacebarSpam();
                    try
                    {
                        if (PixelScan())
                        {
                            // Item was found and collected — rescan next tick to drain remaining items.
                            _lastItemCollectedAt = DateTime.UtcNow;
                            _nextActionTime = DateTime.UtcNow.AddMilliseconds(50);
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
                        }
                    }
                    finally
                    {
                        StopScanSpacebarSpam();
                    }
                    break;

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

        private bool PixelScan()
        {
            // Zoom camera in during the pixel scan so ground items appear larger
            // and their white highlights / name text become more detectable.
            // The movement/combat system restores its own distance on the next tick.
            _memoryService.SetCameraDistance(BotConstants.Camera.LootScanDistance);

            if (ScanRegion(smallX, smallY, "SmallScan") == LootScanPassResult.Collected)
                return true;
            return ScanRegion(bigX, bigY, "BigScan") == LootScanPassResult.Collected;
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

        private LootScanPassResult ScanRegion(
            int[] xRange, int[] yRange, string regionName,
            bool pinkOnly = false, string scanReason = "white-loot")
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
            Rectangle diagnosticScanRegion = Rectangle.Empty;
            Rectangle diagnosticExcludeZone = Rectangle.Empty;
            int diagnosticWhiteCount = 0;
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
                if (!CaptureScreen())
                {
                    diagnosticOutcome = "capture-failed";
                    if (pinkOnly && ScreenshotService.LootScanCaptureEnabled)
                        LogPinkCaptureFailureDiagnostics(scanStartedUtc, scanReason, _lastCaptureResult);
                    return LootScanPassResult.CaptureFailed;
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
                    _log($"[Loot] {regionName}: clamped range is empty (out of client area).");
                    diagnosticOutcome = "empty-range";
                    return LootScanPassResult.NoItem;
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
                diagnosticWhiteCount = targetPoints.Count;
                if (!pinkOnly)
                {
                    CollectScanTargetPoints(
                        xStart, xEnd, yStart, yEnd,
                        applyExcludeZone, exclXMin, exclXMax, exclYMin, exclYMax,
                        false, shadedWhitePoints!,
                        out _, out _, shadedWhiteOnly: true);
                    diagnosticWhiteCount = shadedWhitePoints!.Count;
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

                // ── Phase C: probe only the surviving candidate blobs. The hover check
                //    is unchanged: small blobs get every pixel, larger ones a handful of
                //    sampled points. Tiny blobs keep the old ±1 diagonal search.
                //
                //    The normal white-loot scan stays bounded by MaxLootProbesPerRegion
                //    so UI clutter can never stall it. The SOD/SOP pink pass has NO
                //    probe budget: it probes EVERY candidate pixel in this single pass,
                //    as one continuous mouse sweep. The old per-pass budget (16 probes)
                //    aborted the pass after a few samples and relied on the state machine
                //    to retry with a rotating offset — each retry re-captured and
                //    re-scanned the whole window first, which is exactly what produced
                //    the visible "move x times → pause → move x times" rhythm. ──
                int probesUsed = 0;
                foreach (WhiteComponent component in candidates)
                {
                    if (ShouldAbortLoot() || _scanToken.IsCancellationRequested ||
                        (pinkOnly && !PinkLootOnlyMode))
                    {
                        diagnosticOutcome = "cancelled-before-probe";
                        return LootScanPassResult.Cancelled;
                    }

                    bool addDiagonals = component.Area <= TinyProbeWithDiagonalsArea;
                    List<Point> points = pinkOnly ? component.Points : SelectProbePoints(component);
                    foreach (Point probe in points)
                    {
                        if (_scanToken.IsCancellationRequested || ShouldAbortLoot() ||
                            (pinkOnly && !PinkLootOnlyMode))
                        {
                            diagnosticOutcome = "cancelled-during-probe";
                            return LootScanPassResult.Cancelled;
                        }
                        actualCursorProbes++;
                        LootProbeResult probeResult = TryCollectAt(probe.X, probe.Y, pinkOnly);
                        if (probeResult == LootProbeResult.Collected)
                        {
                            diagnosticOutcome = "collected";
                            return LootScanPassResult.Collected;
                        }
                        if (probeResult == LootProbeResult.CaptureInvalid)
                        {
                            diagnosticOutcome = "capture-geometry-changed-during-probes";
                            return LootScanPassResult.CaptureFailed;
                        }
                        if (probeResult == LootProbeResult.Cancelled)
                        {
                            diagnosticOutcome = "probe-cancelled-or-focus-lost";
                            return LootScanPassResult.Cancelled;
                        }
                        probesUsed++;

                        if (addDiagonals)
                        {
                            LootProbeResult diagonalResult = TryCollectAtWithCount(
                                probe.X + 1, probe.Y + 1, pinkOnly, ref actualCursorProbes);
                            if (diagonalResult != LootProbeResult.Miss)
                                return HandleProbeResult(diagonalResult, ref diagnosticOutcome);
                            diagonalResult = TryCollectAtWithCount(
                                probe.X - 1, probe.Y - 1, pinkOnly, ref actualCursorProbes);
                            if (diagonalResult != LootProbeResult.Miss)
                                return HandleProbeResult(diagonalResult, ref diagnosticOutcome);
                            diagonalResult = TryCollectAtWithCount(
                                probe.X + 1, probe.Y - 1, pinkOnly, ref actualCursorProbes);
                            if (diagonalResult != LootProbeResult.Miss)
                                return HandleProbeResult(diagonalResult, ref diagnosticOutcome);
                            diagonalResult = TryCollectAtWithCount(
                                probe.X - 1, probe.Y + 1, pinkOnly, ref actualCursorProbes);
                            if (diagonalResult != LootProbeResult.Miss)
                                return HandleProbeResult(diagonalResult, ref diagnosticOutcome);
                            probesUsed += 4;
                        }

                        diagnosticWhiteHits.Add(probe);

                        // Only the normal white-loot scan keeps the probe budget.
                        if (!pinkOnly && probesUsed >= MaxLootProbesPerRegion)
                        {
                            _log($"[Loot] {regionName}: probe budget ({MaxLootProbesPerRegion}) exhausted — " +
                                 $"{diagnosticWhiteHits.Count} probed px of {diagnosticWhiteCount} {pixelNoun} px in {components.Count} components; " +
                                 $"aborting region (likely UI/effect clutter).");
                            diagnosticOutcome = "too-many-white";
                            return LootScanPassResult.NoItem;
                        }
                    }
                }

                if (diagnosticWhiteCount > 0)
                {
                    string tinyPinkNote = pinkOnly
                        ? $", {tinyPinkComponents} below {BotConstants.Loot.PinkMinCandidateWidth}x{BotConstants.Loot.PinkMinCandidateHeight} px skipped"
                        : "";
                    _log($"[Loot] {regionName}: scanned {scanPixels} px, {diagnosticWhiteCount} {pixelNoun} px in {components.Count} components — " +
                         $"{filteredComponents} filtered (shape/size; mob-marker rings={mobMarkerComponents}){tinyPinkNote}, {textGlyphs.Count} text glyphs skipped, " +
                         $"{candidates.Count} loot candidates probed ({probesUsed} probes), no item collected{KillElapsed()}.");
                }
                else
                {
                    _log($"[Loot] {regionName}: scanned {scanPixels} px, no {pixelNoun} pixels.");
                }
                diagnosticOutcome = "no-item";
                return LootScanPassResult.NoItem;
            }
            catch (Exception ex)
            {
                _log($"PixelScan error: {ex.Message}");
                diagnosticOutcome = "scan-error";
                return pinkOnly ? LootScanPassResult.CaptureFailed : LootScanPassResult.NoItem;
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

                // White-loot retains the existing annotated frame. Pink diagnostics save
                // an exact unmodified clone separately, before it can be annotated.
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
                        ScreenshotService.SaveLootScanFrame(
                            diagnosticFrame, regionName, diagnosticOutcome, _log,
                            diagnosticWhiteCount, diagnosticScanRegion, diagnosticExcludeZone,
                            diagnosticWhiteHits, pixelNoun);
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

        private void CancelAllLootWork(string reason)
        {
            bool hadWork;
            lock (_cameraCursorOwnershipGate)
            {
                PinkRescanSnapshot pendingRescan = _pinkRescanScheduler.Snapshot;
                hadWork = _pinkScanRequested || _pinkScanOwnsCameraCursor || _pinkRescanActive ||
                          _lootState != LootMachineState.Idle || IsLootingActive ||
                          pendingRescan.Pending || pendingRescan.Active;
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

        /// <summary>Components up to this area are probed at every pixel.</summary>
        private const int DenseProbeMaxArea = 12;

        /// <summary>Tiny blobs (1–4 px) additionally get the old ±1 diagonal hover search around each pixel.</summary>
        private const int TinyProbeWithDiagonalsArea = 4;

        /// <summary>Max probe points sampled from a non-dense component.</summary>
        private const int MaxSampleProbePoints = 8;

        /// <summary>Hard cap on mouseover probes per region pass — replaces the old raw-white-count abort.</summary>
        private const int MaxLootProbesPerRegion = 400;

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
        /// Probe points for a candidate component: every pixel for dense (small)
        /// components, or evenly sampled points for larger ones. Pixels are ordered
        /// like the old column-major scan (x, then y).
        /// </summary>
        private static List<Point> SelectProbePoints(WhiteComponent component)
        {
            var ordered = new List<Point>(component.Points);
            ordered.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));

            if (component.Area <= DenseProbeMaxArea || ordered.Count <= MaxSampleProbePoints)
                return ordered;

            var probes = new List<Point>(MaxSampleProbePoints);
            for (int i = 0; i < MaxSampleProbePoints; i++)
            {
                int index = (int)Math.Round(i * (ordered.Count - 1) / (double)(MaxSampleProbePoints - 1));
                probes.Add(ordered[index]);
            }
            return probes;
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
            if (_memoryService.IsLootMouseOver())
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
            // Give the game time to update the mouseover memory value before IsLootMouseOver checks it.
            // Allow 5 ms for SOD/SOP mouseover updates; normal scans keep 1 ms.
            if (pinkOnly)
                _scanToken.WaitHandle.WaitOne(5);
            else
                Thread.Sleep(1);
        }

        private int GetPositionX()
        {
             return _memoryService.GetLootPositionX();
        }
    }
}
