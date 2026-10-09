using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;
using DriverScanTester.Utils;

namespace DriverScanTester.Services
{
    public class LootSystem
    {
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
        //   Scan        → pixel-scan: label white blobs, filter ring/name/line shapes,
        //                  probe only loot-like blobs (small compact sparkles)
        //   PinkScan   → SOD/SOP-only mode (MoveAndAttack waypoints): after each kill,
        //                  sweep the WHOLE game window for PINK pixels in ONE continuous
        //                  mouse pass over every candidate pixel, collect each SOD/SOP
        //                  found, and repeat a fresh sweep after each successful pickup.
        //                  When a confirmed SOD/SOP pickup cannot fit (bag at the weight
        //                  limit) the player first drinks the potion type it has more of
        //                  (HP/mana) — one potion frees enough weight for one 1-lb scroll.
        //   ScanComplete→ a full scan pass found no items; hold briefly so the movement
        //                  system can walk away, then restart the cycle from Idle
        private enum LootMachineState { Idle, PostMobTab, AreaLoot, AreaLootWait, Scan, PinkScan, ScanComplete }
        private LootMachineState _lootState = LootMachineState.Idle;
        private DateTime _nextActionTime = DateTime.MinValue;
        private int _inventoryChecksumBefore;
        private int _consecutiveEmptySpacePresses;
        private const int MaxEmptySpacePressesBeforeScan = 3;

        /// <summary>Until this time the ScanComplete state holds (no area-loot/scan).</summary>
        private DateTime _scanCompleteUntil = DateTime.MinValue;

        /// <summary>Last time an item was actually collected (spacebar pickup or pixel-scan click).</summary>
        private DateTime _lastItemCollectedAt = DateTime.MinValue;

        // ── SOD/SOP pink-scan request (MoveAndAttack post-kill) ──
        // The movement system requests the pink scan the moment CombatHandler reports a
        // finished fight (TabAfterKill), so the scan is not missed when TAB re-selects a
        // mob before the loot task's own selected→not-selected transition is observed.
        private CancellationToken _scanToken;

        /// <summary>
        /// Reusable raw-pixel buffer for the bulk LockBits region scan (Phase A).
        /// Kept between scans so the captured frame is read in one copy instead of a
        /// <see cref="Bitmap.GetPixel"/> call per pixel.
        /// </summary>
        private byte[] _scanBuffer = Array.Empty<byte>();
        private readonly PlayerMarkerPixelDetector _playerMarkerPixelDetector = new();
        private bool _otherPlayerMarkerWasVisible;
        private bool _playerMarkerConfirmationPending;
        private DateTime _playerMarkerCandidateDetectedAt = DateTime.MinValue;
        private bool _playerMarkerScanEnabled;
        private volatile bool _pinkScanRequested;
        private DateTime _pinkScanRequestedAt = DateTime.MinValue;

        /// <summary>Time the pending background SOD/SOP rescan runs (MinValue = none). Armed at each
        /// kill; it runs while movement and combat continue (see <see cref="RunPinkRescan"/>).</summary>
        private DateTime _pinkRescanAt = DateTime.MinValue;

        /// <summary>True while a background rescan pass owns the camera and cursor.</summary>
        private volatile bool _pinkRescanActive;

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

        /// <summary>Game window handle — stored during ResolveClientOrigin().</summary>
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
        public bool PinkLootOnlyMode { get; set; } = false;

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

        /// <summary>Called once when another player marker first appears in a scan episode.</summary>
        public Action? OtherPlayerMarkerDetected { get; set; }

        /// <summary>
        /// Requests an immediate post-kill SOD/SOP pink scan. Called by the movement
        /// system the moment a fight ends (CombatHandler.TabAfterKill), so the scan is
        /// not missed if TAB re-selects a mob before the loot task observes the
        /// selected→not-selected transition. No-op unless <see cref="PinkLootOnlyMode"/>
        /// is enabled. The request is consumed by <see cref="Update"/>.
        /// </summary>
        public void RequestPinkScan()
        {
            if (!PinkLootOnlyMode)
                return;

            _pinkScanRequestedAt = DateTime.UtcNow;
            _pinkScanRequested = true;
            _log("[Loot] SOD/SOP pink scan requested (post-kill).");
        }

        /// <summary>
        /// True while a post-kill SOD/SOP pink scan is requested or running. The
        /// movement system holds combat/movement while this is true, so the drop check
        /// always finishes before the bot walks away or starts the next fight.
        /// </summary>
        public bool IsPinkScanPendingOrActive =>
            _pinkScanRequested || _lootState == LootMachineState.PinkScan;

        /// <summary>
        /// True while the background SOD/SOP rescan pass runs. Unlike
        /// <see cref="IsPinkScanPendingOrActive"/> this does NOT hold movement or combat:
        /// movement only yields the camera and the cursor to the pass.
        /// </summary>
        public bool IsPinkRescanActive => _pinkRescanActive;

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
        private bool _isCollecting = false;

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

            // Resolve client area once at construction; if the window is moved
            // the resolution will be re-tried on each capture failure.
            ResolveClientOrigin();
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
        /// Looks up the game window, stores its client-area top-left corner and dimensions,
        /// and computes the coordinate offset from the reference window position (447,77)
        /// that the hardcoded scan coordinates were designed for.
        /// Falls back to (0,0) if the window cannot be found.
        /// </summary>
        private void ResolveClientOrigin()
        {
            _hwnd = FindWindow(null, "Legend of Ares");
            if (_hwnd == nint.Zero) _hwnd = FindWindow(null, "Ares");
            if (_hwnd == nint.Zero) _hwnd = FindWindow(null, "Nostalgia");
            if (_hwnd == nint.Zero) _hwnd = FindWindow(null, "Epic Of Ares Client");

            if (_hwnd == nint.Zero)
            {
                _log("[LootSystem] Game window not found — falling back to screen origin (0,0).");
                _clientOriginX = 0;
                _clientOriginY = 0;
                _clientWidth = 0;
                _clientHeight = 0;
                _referenceClientOriginX = 0;
                _referenceClientOriginY = 0;
                _coordOffsetX = 0;
                _coordOffsetY = 0;
                _hasClientOrigin = false;
                return;
            }

            if (!GetClientRect(_hwnd, out RECT clientRect))
            {
                _log("[LootSystem] GetClientRect failed — falling back to screen origin (0,0).");
                _clientOriginX = 0;
                _clientOriginY = 0;
                _clientWidth = 0;
                _clientHeight = 0;
                _referenceClientOriginX = 0;
                _referenceClientOriginY = 0;
                _coordOffsetX = 0;
                _coordOffsetY = 0;
                _hasClientOrigin = false;
                return;
            }

            POINT topLeft = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(_hwnd, ref topLeft))
            {
                _log("[LootSystem] ClientToScreen failed — falling back to screen origin (0,0).");
                _clientOriginX = 0;
                _clientOriginY = 0;
                _clientWidth = 0;
                _clientHeight = 0;
                _referenceClientOriginX = 0;
                _referenceClientOriginY = 0;
                _coordOffsetX = 0;
                _coordOffsetY = 0;
                _hasClientOrigin = false;
                return;
            }

            _clientOriginX = topLeft.X;
            _clientOriginY = topLeft.Y;
            int newWidth = clientRect.Right - clientRect.Left;
            int newHeight = clientRect.Bottom - clientRect.Top;
            _hasClientOrigin = true;

            // Recreate bitmap if actual client dimensions changed or bitmap not yet created.
            if (_bitmap == null || _bitmap.Width != newWidth || _bitmap.Height != newHeight)
            {
                _graphics?.Dispose();
                _bitmap?.Dispose();
                _clientWidth = Math.Max(newWidth, 1);
                _clientHeight = Math.Max(newHeight, 1);
                _bitmap = new Bitmap(_clientWidth, _clientHeight);
                _graphics = Graphics.FromImage(_bitmap);
                _log($"[LootSystem] Bitmap resized to actual client area: {_clientWidth} x {_clientHeight}");
            }
            else
            {
                _clientWidth = newWidth;
                _clientHeight = newHeight;
            }

            // ── Compute coordinate offset from reference window position ──
            // Hardcoded coordinates in BotConstants.Loot are screen-absolute values
            // designed for when the window was at (ExpectedWindowX=447, ExpectedWindowY=77).
            //
            // The reference client origin is what ClientToScreen returned at that position.
            // It equals ExpectedWindow + border/title-bar sizes.
            //
            // borderX = clientOriginX - windowRect.Left  (constant for this window style)
            // titleY  = clientOriginY - windowRect.Top   (constant for this window style)
            //
            // referenceClientOriginX = ExpectedWindowX + borderX
            //                        = ExpectedWindowX + (clientOriginX - windowRect.Left)
            //                        = clientOriginX - (windowRect.Left - ExpectedWindowX)
            //                        = clientOriginX - coordOffsetX
            //
            // coordOffsetX = windowRect.Left - ExpectedWindowX
            // coordOffsetY = windowRect.Top  - ExpectedWindowY

            if (!GetWindowRect(_hwnd, out RECT windowRect))
            {
                _log("[LootSystem] GetWindowRect failed — cannot compute reference offset. Falling back to raw client-relative.");
                _referenceClientOriginX = 0;
                _referenceClientOriginY = 0;
                _coordOffsetX = 0;
                _coordOffsetY = 0;
            }
            else
            {
                const int expectedWindowX = 447;
                const int expectedWindowY = 77;

                _coordOffsetX = windowRect.Left - expectedWindowX;
                _coordOffsetY = windowRect.Top - expectedWindowY;

                _referenceClientOriginX = _clientOriginX - _coordOffsetX;
                _referenceClientOriginY = _clientOriginY - _coordOffsetY;
            }

            _log($"[LootSystem] Game window at screen ({windowRect.Left}, {windowRect.Top}), " +
                 $"client area ({_clientOriginX}, {_clientOriginY}) size {_clientWidth}x{_clientHeight}");
            _log($"[LootSystem] Coord offset from reference: ({_coordOffsetX}, {_coordOffsetY}), " +
                 $"reference client origin: ({_referenceClientOriginX}, {_referenceClientOriginY})");
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
            return (bitmapLocalX + _referenceClientOriginX + _coordOffsetX,
                    bitmapLocalY + _referenceClientOriginY + _coordOffsetY);
        }

        /// <summary>
        /// Checks whether the game window is the foreground (focused) window.
        /// If it is not, the loot bot must NOT scan or click to avoid acting on other windows.
        /// </summary>
        private bool IsGameWindowFocused()
        {
            return GetForegroundWindow() == _hwnd;
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

        public async Task Update(CancellationToken token)
        {
            _scanToken = token;
            // ── Focus guard: if the game window is NOT the foreground window,
            //    do NOT scan, click, or send any input.  Without this check the
            //    loot bot would capture/click whatever window is on top (desktop,
            //    folders, browser, etc.) and potentially cause damage. ──
            if (!IsGameWindowFocused())
            {
                ResetPlayerMarkerConfirmation();
                StopScanSpacebarSpam();
                if (_lootState != LootMachineState.Idle)
                {
                    _lootState = LootMachineState.Idle;
                    _consecutiveEmptySpacePresses = 0;
                }
                IsLootingActive = false;
                await Task.Delay(BotConstants.Delays.LootUpdateMs, token);
                return;
            }

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
                _pinkRescanAt = DateTime.MinValue;
                StopScanSpacebarSpam();
                if (_lootState != LootMachineState.Idle)
                {
                    _lootState = LootMachineState.Idle;
                    _consecutiveEmptySpacePresses = 0;
                }
                IsLootingActive = false;
                await Task.Delay(BotConstants.Delays.LootUpdateMs, token);
                return;
            }

            if (ConfirmPlayerMarkerIfDue())
            {
                if (!token.IsCancellationRequested)
                    await Task.Delay(BotConstants.Delays.LootUpdateMs, token);
                return;
            }

            // ── Track mob selection to detect mob death ──
            bool isMobSelected = _memoryService.IsMobSelected();

            // ── Post-kill SOD/SOP pink-scan request (MoveAndAttack) ──
            // The movement system requests this the moment a fight ends. Consume the
            // flag on every tick (even when it is dropped) so a stale request can never
            // hold the movement system forever.
            bool pinkScanRequested = _pinkScanRequested;
            if (pinkScanRequested)
            {
                _pinkScanRequested = false;
                bool fresh = (DateTime.UtcNow - _pinkScanRequestedAt).TotalMilliseconds <=
                             BotConstants.Loot.PinkScanRequestTtlMs;
                if (!PinkLootOnlyMode)
                {
                    pinkScanRequested = false;
                }
                else if (!fresh)
                {
                    _log("[Loot] Stale pink-scan request dropped (expired before it could start).");
                    pinkScanRequested = false;
                }
            }

            // Mob just died (was selected → no longer selected) → normally TAB first to
            // check if there are more mobs to kill before starting loot. In loot-priority
            // mode the TAB check is skipped — looting comes first: wait ~200ms so the
            // drops appear, then loot everything before the next target is even selected.
            // In SOD/SOP pink-only mode the request above (or this transition) starts a
            // pink big-region scan instead of the normal loot cycle.
            if (_wasMobSelectedPrev && !isMobSelected)
            {
                _wasMobSelectedPrev = false;
                _lastKillAt = DateTime.UtcNow;
                if (PinkLootOnlyMode)
                {
                    pinkScanRequested = true;
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
            // The scan is NOT cancelled by a re-selected target (the check belongs to
            // the mob that just died), so the state machine below keeps running even
            // while a mob is selected — the movement system holds combat during the scan.
            if (pinkScanRequested)
            {
                _log($"[Loot] Mob killed — SOD/SOP pink scan starting (big region only, pink pixels only; waiting {BotConstants.Delays.LootPostKillDelayMs}ms for drops).");
                IsLootingActive = true;
                _lootState = LootMachineState.PinkScan;
                _nextActionTime = DateTime.UtcNow.AddMilliseconds(BotConstants.Delays.LootPostKillDelayMs);
                _consecutiveEmptySpacePresses = 0;

                // Arm the background rescan for this kill. A kill that arrives while an
                // earlier rescan is still pending re-arms it, so the rescan always sweeps the
                // latest drops instead of firing before them.
                _pinkRescanAt = DateTime.UtcNow.AddMilliseconds(BotConstants.Loot.PinkRescanDelayMs);
            }

            // ── Background SOD/SOP rescan (MoveAndAttack) ──
            // Runs PinkRescanDelayMs after the kill while movement and combat keep going:
            // no hold is set here, the pass only takes the camera and the cursor
            // (IsPinkRescanActive). It is placed before the selected-mob pause below, so a
            // fight that starts during the wait never cancels it.
            PinkRescanAction rescan = DecidePinkRescan(
                PinkLootOnlyMode,
                _lootState == LootMachineState.PinkScan,
                _pinkRescanAt,
                DateTime.UtcNow,
                TimeSpan.FromMilliseconds(BotConstants.Loot.PinkRescanExpireMs));
            if (rescan == PinkRescanAction.Expire)
            {
                _log("[Loot] SOD/SOP rescan dropped (expired or mode changed).");
                _pinkRescanAt = DateTime.MinValue;
            }
            else if (rescan == PinkRescanAction.Run)
            {
                RunPinkRescan();
                await Task.Delay(BotConstants.Delays.LootUpdateMs, token);
                return;
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
                    // ONE region (big), PINK pixels only. Pink drops found are
                    // collected and the pass repeats until a full pass finds none left.
                    // No small-region pass, no white-blob scan, no spacebar area-loot.
                    if (DateTime.UtcNow < _nextActionTime)
                    {
                        break; // post-kill delay — let the drops appear first
                    }

                    // Apply the scan zoom only when the post-kill wait is over, then
                    // allow the game to render at that distance before capturing pixels.
                    _memoryService.SetCameraDistance(BotConstants.Camera.SodSopLootScanDistance);
                    await Task.Delay(BotConstants.Delays.SodSopCameraSettleMs, token);

                    if (PinkPixelScan())
                    {
                        // Pink SOD/SOP was found and collected — rescan next tick
                        // to drain any remaining pink drops.
                        _lastItemCollectedAt = DateTime.UtcNow;
                        _nextActionTime = DateTime.UtcNow.AddMilliseconds(50);
                    }
                    else
                    {
                        // The full sweep probed EVERY pink candidate pixel in one
                        // continuous pass and collected nothing — the remaining pink
                        // pixels are false positives (AoE/terrain), not loot. Done.
                        _log($"[Loot] Pink scan complete — no SOD/SOP left to loot{KillElapsed()}.");
                        IsLootingActive = false;
                        _lastKillAt = DateTime.MinValue;
                        _lootState = LootMachineState.ScanComplete;
                        _scanCompleteUntil = DateTime.UtcNow.AddMilliseconds(
                            BotConstants.Delays.LootScanCompleteHoldMs);
                        _consecutiveEmptySpacePresses = 0;
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

            if (ScanRegion(smallX, smallY, "SmallScan"))
            {
                return true;
            }
            if (ScanRegion(bigX, bigY, "BigScan"))
            {
                return true;
            }
            return false;
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
        private bool PinkPixelScan()
        {
            return ScanRegion(bigX, bigY, "BigScan-Pink", pinkOnly: true);
        }

        /// <summary>What the loot loop should do with a pending background rescan this tick.</summary>
        internal enum PinkRescanAction { None, Wait, Run, Expire }

        /// <summary>
        /// Pure decision for the background SOD/SOP rescan. <c>None</c>: nothing pending.
        /// <c>Wait</c>: pending but not due yet, or a post-kill pass is still running (it owns
        /// the loop). <c>Run</c>: due and inside its validity window. <c>Expire</c>: outside
        /// PinkLootOnly mode, or more than <paramref name="expireAfter"/> past due.
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
            if (now - dueAt > expireAfter)
                return PinkRescanAction.Expire;
            return PinkRescanAction.Run;
        }

        /// <summary>
        /// One background SOD/SOP rescan pass, <see cref="BotConstants.Loot.PinkRescanDelayMs"/>
        /// after a kill. Unlike the post-kill check it does not hold movement or combat: it
        /// only takes the camera and the cursor (<see cref="IsPinkRescanActive"/> makes movement
        /// yield those). A pass that collects something re-arms itself shortly, so remaining
        /// drops are drained; an empty pass ends the rescan.
        /// </summary>
        private void RunPinkRescan()
        {
            _log("[Loot] SOD/SOP background rescan — bot keeps moving and fighting.");
            _pinkRescanActive = true;
            try
            {
                _memoryService.SetCameraDistance(BotConstants.Camera.SodSopLootScanDistance);
                Thread.Sleep(BotConstants.Delays.SodSopCameraSettleMs);

                if (PinkPixelScan())
                {
                    _lastItemCollectedAt = DateTime.UtcNow;
                    _pinkRescanAt = DateTime.UtcNow.AddMilliseconds(50);
                }
                else
                {
                    _log($"[Loot] SOD/SOP background rescan finished — nothing more collected{KillElapsed()}.");
                    _pinkRescanAt = DateTime.MinValue;
                }
            }
            finally
            {
                _pinkRescanActive = false;
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

        /// <summary>
        /// Analyzes a captured frame with the same pink pixel mask, connected-component
        /// labeling, minimum blob size and artificial mob-marker rejection used by the
        /// live SOD/SOP scan. It performs no mouse movement, memory mouseover checks or loot.
        /// </summary>
        internal static (int PinkPixels, int Components, int LootCandidates, int MobMarkerRings, int TinyComponents)
            AnalyzePinkFrame(Bitmap bitmap)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            if (bitmap.Width <= 0 || bitmap.Height <= 0)
                return (0, 0, 0, 0, 0);

            Bitmap? converted = null;
            Bitmap scanBitmap = bitmap;
            BitmapData? data = null;
            var targetPoints = new List<Point>();
            try
            {
                if (Image.GetPixelFormatSize(bitmap.PixelFormat) != 32)
                {
                    converted = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);
                    using (Graphics graphics = Graphics.FromImage(converted))
                        graphics.DrawImageUnscaled(bitmap, 0, 0);
                    scanBitmap = converted;
                }

                data = scanBitmap.LockBits(
                    new Rectangle(0, 0, scanBitmap.Width, scanBitmap.Height),
                    ImageLockMode.ReadOnly,
                    scanBitmap.PixelFormat);
                int stride = data.Stride;
                int bytes = Math.Abs(stride) * data.Height;
                byte[] buffer = new byte[bytes];
                Marshal.Copy(data.Scan0, buffer, 0, bytes);
                CollectTargetPointsFromBuffer(
                    buffer, stride, data.Height,
                    0, data.Width, 0, data.Height,
                    applyExcludeZone: false,
                    exclXMin: 0, exclXMax: 0, exclYMin: 0, exclYMax: 0,
                    pinkOnly: true,
                    targetPoints: targetPoints);
            }
            finally
            {
                if (data != null)
                {
                    try { scanBitmap.UnlockBits(data); }
                    catch { /* Keep the test scanner safe if GDI+ rejects an unlock. */ }
                }
                converted?.Dispose();
            }

            List<WhiteComponent> components = LabelWhiteComponents(targetPoints);
            int lootCandidates = 0;
            int mobMarkerRings = 0;
            int tinyComponents = 0;
            foreach (WhiteComponent component in components)
            {
                if (!IsPinkLootCandidate(component))
                {
                    if (component.Width < BotConstants.Loot.PinkMinCandidateWidth ||
                        component.Height < BotConstants.Loot.PinkMinCandidateHeight)
                        tinyComponents++;
                    continue;
                }

                if (IsMobMarkerRingComponent(component, bitmap))
                    mobMarkerRings++;
                else
                    lootCandidates++;
            }

            return (targetPoints.Count, components.Count, lootCandidates, mobMarkerRings, tinyComponents);
        }

        private bool ScanRegion(int[] xRange, int[] yRange, string regionName, bool pinkOnly = false)
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

            try
            {
                CaptureScreen();

                // If bitmap capture failed (window not found, etc.), abort scan.
                if (_bitmap == null || _clientWidth <= 0 || _clientHeight <= 0)
                {
                    diagnosticOutcome = "capture-failed";
                    return false;
                }

                // Check the full client frame for another player's faction marker on
                // every loot pixel-scan pass, regardless of the loot scan rectangle.
                ScanForOtherPlayerPixels();
                if (_scanToken.IsCancellationRequested)
                {
                    diagnosticOutcome = "cancelled-after-player-detection";
                    return false;
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
                    return false;
                }

                // Snapshot the frame under analysis (see diagnostics comment above).
                diagnosticFrame = ScreenshotService.CloneLootScanFrame(_bitmap);
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
                    return false;
                }

                // ── Phase A: collect every target pixel in the region — no input.
                //    Normal loot = pure-white pixels; SOD/SOP pink mode = saturated
                //    pink pixels, with the narrow artificial mob-marker magenta removed
                //    BEFORE connected components are built (IsPinkLootMaskPixel). The old
                //    scan probed each pixel immediately, so mob names, target rings and
                //    sprite highlights produced hundreds of useless mouse probes and even
                //    aborted whole region passes. Now the candidates are gathered first
                //    and only filtered survivors are probed. The frame is read in one
                //    bulk LockBits copy (CollectScanTargetPoints), not GetPixel per px. ──
                var targetPoints = new List<Point>();
                CollectScanTargetPoints(
                    xStart, xEnd, yStart, yEnd,
                    applyExcludeZone, exclXMin, exclXMax, exclYMin, exclYMax,
                    pinkOnly, targetPoints);
                diagnosticWhiteCount = targetPoints.Count;

                // ── Phase B: label 8-connected components and keep only loot-shaped
                //    blobs. Target rings (long thin arcs), mob-name text (glyph rows),
                //    weapon lines and oversized sprite highlights are dropped instead
                //    of being probed; a dialog is one huge blob and is dropped too. ──
                List<WhiteComponent> components = LabelWhiteComponents(targetPoints);
                var candidates = new List<WhiteComponent>();
                int filteredComponents = 0;
                int tinyPinkComponents = 0;
                int mobMarkerComponents = 0;
                foreach (WhiteComponent component in components)
                {
                    bool isCandidate = pinkOnly
                        ? IsPinkLootCandidate(component)
                        : IsLootCandidate(component);
                    if (isCandidate && pinkOnly && IsMobMarkerRingComponent(component, _bitmap))
                    {
                        // The artificial Hyena markers deliberately use saturated magenta and
                        // overlap the legacy SOD/SOP color family. Exclude sparse marker-ring
                        // geometry before probing; exact loot
                        // mouseover remains authoritative for all actual SOD/SOP components.
                        mobMarkerComponents++;
                        isCandidate = false;
                    }

                    if (isCandidate)
                    {
                        candidates.Add(component);
                    }
                    else
                    {
                        filteredComponents++;
                        if (pinkOnly &&
                            (component.Width < BotConstants.Loot.PinkMinCandidateWidth ||
                             component.Height < BotConstants.Loot.PinkMinCandidateHeight))
                        {
                            tinyPinkComponents++;
                        }
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
                    if (ShouldAbortLoot())
                    {
                        diagnosticOutcome = "city-abort";
                        return false;
                    }

                    bool addDiagonals = component.Area <= TinyProbeWithDiagonalsArea;
                    List<Point> points = pinkOnly ? component.Points : SelectProbePoints(component);
                    foreach (Point probe in points)
                    {
                        if (_scanToken.IsCancellationRequested || ShouldAbortLoot())
                            return false;
                        if (TryCollectAt(probe.X, probe.Y, pinkOnly)) { diagnosticOutcome = "collected"; return true; }
                        probesUsed++;

                        if (addDiagonals)
                        {
                            if (TryCollectAt(probe.X + 1, probe.Y + 1, pinkOnly)) { diagnosticOutcome = "collected"; return true; }
                            if (TryCollectAt(probe.X - 1, probe.Y - 1, pinkOnly)) { diagnosticOutcome = "collected"; return true; }
                            if (TryCollectAt(probe.X + 1, probe.Y - 1, pinkOnly)) { diagnosticOutcome = "collected"; return true; }
                            if (TryCollectAt(probe.X - 1, probe.Y + 1, pinkOnly)) { diagnosticOutcome = "collected"; return true; }
                            probesUsed += 4;
                        }

                        diagnosticWhiteHits.Add(probe);

                        // Only the normal white-loot scan keeps the probe budget.
                        if (!pinkOnly && probesUsed >= MaxLootProbesPerRegion)
                        {
                            _log($"[Loot] {regionName}: probe budget ({MaxLootProbesPerRegion}) exhausted — " +
                                 $"{diagnosticWhiteHits.Count} probed px of {targetPoints.Count} {pixelNoun} px in {components.Count} components; " +
                                 $"aborting region (likely UI/effect clutter).");
                            diagnosticOutcome = "too-many-white";
                            return false;
                        }
                    }
                }

                if (targetPoints.Count > 0)
                {
                    string tinyPinkNote = pinkOnly
                        ? $", {tinyPinkComponents} below {BotConstants.Loot.PinkMinCandidateWidth}x{BotConstants.Loot.PinkMinCandidateHeight} px skipped"
                        : "";
                    _log($"[Loot] {regionName}: scanned {scanPixels} px, {targetPoints.Count} {pixelNoun} px in {components.Count} components — " +
                         $"{filteredComponents} filtered (shape/size; mob-marker rings={mobMarkerComponents}){tinyPinkNote}, {textGlyphs.Count} text glyphs skipped, " +
                         $"{candidates.Count} loot candidates probed ({probesUsed} probes), no item collected{KillElapsed()}.");
                }
                else
                {
                    _log($"[Loot] {regionName}: scanned {scanPixels} px, no {pixelNoun} pixels.");
                }
                diagnosticOutcome = "no-item";
            }
            catch (Exception ex)
            {
                _log($"PixelScan error: {ex.Message}");
            }
            finally
            {
                // Save whatever frame was captured (if any) with the scan overlay,
                // regardless of how the region pass ended.
                ScreenshotService.SaveLootScanFrame(
                    diagnosticFrame,
                    regionName,
                    diagnosticOutcome,
                    _log,
                    diagnosticWhiteCount,
                    diagnosticScanRegion,
                    diagnosticExcludeZone,
                    diagnosticWhiteHits,
                    pixelNoun);
            }
            return false;
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
                _playerMarkerCandidateDetectedAt = DateTime.UtcNow;
                _log($"[PlayerPixels] Candidate marker detected: {DescribePlayerMarkerCandidate(candidate)}; " +
                     $"character-exclusion={FormatRectangle(characterExclusionArea)}; " +
                     $"HUD exclusions={FormatRectangles(PlayerMarkerPixelDetector.GetHudExclusionAreas(_bitmap.Width, _bitmap.Height))}. " +
                     $"Will recheck in {BotConstants.Delays.OtherPlayerMarkerConfirmMs} ms.");
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

            if ((DateTime.UtcNow - _playerMarkerCandidateDetectedAt).TotalMilliseconds <
                BotConstants.Delays.OtherPlayerMarkerConfirmMs)
                return false;

            try
            {
                CaptureScreen();
                if (_bitmap == null || _clientWidth <= 0 || _clientHeight <= 0)
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
                _playerMarkerConfirmationPending = false;
                _playerMarkerCandidateDetectedAt = DateTime.MinValue;
                _otherPlayerMarkerWasVisible = stillVisible;

                if (!stillVisible)
                {
                    _log("[PlayerPixels] Candidate disappeared during the 5-second confirmation scan; continuing normally.");
                    return false;
                }

                _log($"[PlayerPixels] Other-player marker confirmed after the delay: " +
                     $"{DescribePlayerMarkerCandidate(confirmedCandidate)}; " +
                     $"character-exclusion={FormatRectangle(characterExclusionArea)}.");
                ScreenshotService.SavePlayerMarkerScreenshot(
                    _bitmap,
                    confirmedCandidate,
                    characterExclusionArea,
                    PlayerMarkerPixelDetector.GetHudExclusionAreas(_bitmap.Width, _bitmap.Height),
                    _log);
                OtherPlayerMarkerDetected?.Invoke();
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

        private static string FormatRectangles(Rectangle[] rectangles)
            => string.Join(";", Array.ConvertAll(rectangles, FormatRectangle));

        private void ResetPlayerMarkerConfirmation()
        {
            _otherPlayerMarkerWasVisible = false;
            _playerMarkerConfirmationPending = false;
            _playerMarkerCandidateDetectedAt = DateTime.MinValue;
        }

        /// <summary>
        /// Phase A of a region pass: collects every target pixel of the clamped scan
        /// region — pure white for normal loot, the hot-pink family for SOD/SOP.
        ///
        /// The frame is read through one LockBits bulk copy instead of a
        /// <see cref="Bitmap.GetPixel"/> call per pixel. The pink pass sweeps the
        /// WHOLE client area (~1M px) and used to spend hundreds of milliseconds per
        /// pass in GDI+ GetPixel overhead; that pause is what made the pink sweep look
        /// like "move x times, stop, move x times". With the bulk read the pixel pass
        /// costs a few milliseconds instead.
        /// </summary>
        private void CollectScanTargetPoints(
            int xStart, int xEnd, int yStart, int yEnd,
            bool applyExcludeZone, int exclXMin, int exclXMax, int exclYMin, int exclYMax,
            bool pinkOnly, List<Point> targetPoints)
        {
            Bitmap bitmap = _bitmap;
            PixelFormat format = bitmap.PixelFormat;

            // Only 32bpp formats have the 4-byte BGRA layout this reader assumes.
            // Anything else (should not happen — the bitmap is created 32bppArgb)
            // falls back to the slow GetPixel path below.
            if (Image.GetPixelFormatSize(format) == 32)
            {
                BitmapData? data = null;
                try
                {
                    data = bitmap.LockBits(
                        new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                        ImageLockMode.ReadOnly,
                        format);

                    int stride = data.Stride;
                    int bytes = Math.Abs(stride) * data.Height;
                    if (_scanBuffer.Length < bytes)
                        _scanBuffer = new byte[bytes];
                    Marshal.Copy(data.Scan0, _scanBuffer, 0, bytes);

                    CollectTargetPointsFromBuffer(
                        _scanBuffer, stride, data.Height,
                        xStart, xEnd, yStart, yEnd,
                        applyExcludeZone, exclXMin, exclXMax, exclYMin, exclYMax,
                        pinkOnly, targetPoints);
                    return;
                }
                catch
                {
                    // LockBits/Marshal failed for an unexpected reason — drop any
                    // partially collected points and use the safe fallback below.
                    targetPoints.Clear();
                }
                finally
                {
                    if (data != null)
                        bitmap.UnlockBits(data);
                }
            }

            // Fallback: per-pixel GDI+ read (slow, but always works).
            for (int x = xStart; x < xEnd; x++)
            {
                for (int y = yStart; y < yEnd; y++)
                {
                    if (applyExcludeZone &&
                        x >= exclXMin && x <= exclXMax && y >= exclYMin && y <= exclYMax)
                        continue; // character exclude zone

                    Color pixelColor = bitmap.GetPixel(x, y);
                    bool isTarget = pinkOnly
                        ? IsPinkLootMaskPixel(pixelColor)
                        : (pixelColor.R == 255 && pixelColor.G == 255 && pixelColor.B == 255);
                    if (isTarget)
                        targetPoints.Add(new Point(x, y));
                }
            }
        }

        /// <summary>
        /// Testable core of Phase A: parses a raw 32bpp BGRA frame buffer and collects
        /// every target pixel. Byte order in memory is B, G, R, A; <paramref name="stride"/> may
        /// be negative for bottom-up frames. Normal loot = pure white; pink mode = the
        /// SOD/SOP hot-pink family with artificial mob-marker magenta removed.
        /// </summary>
        internal static void CollectTargetPointsFromBuffer(
            byte[] buffer, int stride, int bitmapHeight,
            int xStart, int xEnd, int yStart, int yEnd,
            bool applyExcludeZone, int exclXMin, int exclXMax, int exclYMin, int exclYMax,
            bool pinkOnly, List<Point> targetPoints)
        {
            bool bottomUp = stride < 0;
            int absStride = Math.Abs(stride);
            for (int x = xStart; x < xEnd; x++)
            {
                bool xOutsideExclude = !applyExcludeZone || x < exclXMin || x > exclXMax;
                for (int y = yStart; y < yEnd; y++)
                {
                    if (!xOutsideExclude && y >= exclYMin && y <= exclYMax)
                        continue; // character exclude zone

                    int row = bottomUp ? bitmapHeight - 1 - y : y;
                    int offset = row * absStride + x * 4;
                    int b = buffer[offset];
                    int g = buffer[offset + 1];
                    int r = buffer[offset + 2];
                    bool isTarget = pinkOnly
                        ? IsPinkLootMaskPixel(r, g, b)
                        : (r == 255 && g == 255 && b == 255);
                    if (isTarget)
                        targetPoints.Add(new Point(x, y));
                }
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  WHITE-PIXEL SHAPE FILTER (loot candidate detection)
        //  Mob names, target rings, weapon lines and sprite highlights are
        //  white too (see LootSSFolder diagnostics). They are removed by
        //  connected-component shape so only small compact blobs — the actual
        //  loot sparkles — are probed with the IsLootMouseOver() check.
        // ════════════════════════════════════════════════════════════════

        /// <summary>Largest area (px) a white blob may have to be a loot candidate.</summary>
        private const int MaxLootCandidateArea = 60;

        /// <summary>Largest width/height (px) a white blob may have to be a loot candidate.</summary>
        private const int MaxLootCandidateDimension = 24;

        /// <summary>Blobs at most this thick but at least <see cref="ThinLineMinLength"/> long are weapon/UI lines, not loot.</summary>
        private const int ThinLineMaxThickness = 2;
        private const int ThinLineMinLength = 12;

        /// <summary>Components at least this wide AND tall are ring arcs / name bands, never loot.</summary>
        private const int WideBandMinWidth = 18;
        private const int WideBandMinHeight = 12;

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

        /// <summary>A connected group of pure-white pixels found during a region scan.</summary>
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
        /// True when a white component looks like a loot drop (small compact blob).
        /// Large blobs (ring arcs, name bands, dialogs), thin long lines (weapon glow,
        /// UI separators) and wide low-fill bands are rejected.
        /// </summary>
        private static bool IsLootCandidate(WhiteComponent component)
        {
            if (component.Area > MaxLootCandidateArea)
                return false;
            if (component.Width > MaxLootCandidateDimension || component.Height > MaxLootCandidateDimension)
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

        private void CaptureScreen()
        {
            // If we lost track of the client origin (e.g. window was moved),
            // re-resolve it so the capture still targets the correct area.
            if (!_hasClientOrigin)
            {
                ResolveClientOrigin();
            }

            // If the window was not found or bitmap not created, skip capture.
            if (_bitmap == null || _graphics == null || _clientWidth <= 0 || _clientHeight <= 0)
            {
                return;
            }

            _graphics.CopyFromScreen(_clientOriginX, _clientOriginY, 0, 0, _bitmap.Size);
        }

        private bool TryCollectAt(int x, int y, bool pinkOnly = false)
        {
            WaitMouseInPosition(x, y, pinkOnly);

            // If the player entered city, abort immediately. A mob being selected
            // only aborts outside loot-priority mode.
            if (_scanToken.IsCancellationRequested || ShouldAbortLoot())
            {
                return false;
            }

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
                CollectionClick();
                return true;
            }
            return false;
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
            _isCollecting = true;
            try
            {
            // Release any held right button, then left-click to pick up the item.
            MouseOperations.MouseEvent(MouseOperations.MouseEventFlags.RightUp);
            MouseOperations.MouseEvent(MouseOperations.MouseEventFlags.LeftDown);

            _log("Collecting item (SOD/SOP by pixel color + mouseover indicator).");
            Thread.Sleep(BotConstants.Delays.CollectClickHoldMs);

            MouseOperations.MouseEvent(MouseOperations.MouseEventFlags.LeftUp);

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
                if (_scanToken.IsCancellationRequested || ShouldAbortLoot())
                {
                    _log("[Loot] Stop/city detected during collection — aborting spacebar spam.");
                    break;
                }

                GameInput.PressKey(GameInput.VK_SPACE, GameInput.SCAN_SPACE);
                Thread.Sleep(30);
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
            CaptureScreen();
            _log("[Loot] Fresh screen capture forced after click.");

            // Loot diagnostics: also save the post-collection frame so the state the
            // character ended up in (item gone / still on the ground / walked away)
            // can be compared with the annotated frame from the scan that clicked it.
            ScreenshotService.SaveLootScanFrame(
                ScreenshotService.CloneLootScanFrame(_bitmap), "AfterClick", "post-click", _log);
            }
            finally
            {
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
