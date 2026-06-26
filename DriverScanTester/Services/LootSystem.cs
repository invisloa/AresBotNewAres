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
        private bool _wasSodDetected = false;

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
        //   Scan        → pixel-scan for SOD/SOP white pixels and collect them
        private enum LootMachineState { Idle, PostMobTab, AreaLoot, AreaLootWait, Scan }
        private LootMachineState _lootState = LootMachineState.Idle;
        private DateTime _nextActionTime = DateTime.MinValue;
        private int _inventoryChecksumBefore;
        private int _consecutiveEmptySpacePresses;
        private const int MaxEmptySpacePressesBeforeScan = 3;

        /// <summary>Game window handle — stored during ResolveClientOrigin().</summary>
        private nint _hwnd;

        /// <summary>Previous tick's IsMobSelected value — used to detect mob death.</summary>
        private bool _wasMobSelectedPrev = false;

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

        /// <summary>Maximum white pixels before aborting scan — dialogs/UI have tons of white.</summary>
        private const int MAX_WHITE_PIXELS = 200;

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
        /// Runs the pixel scan pass (for testing via the "Test Loot" button).
        /// Skips the startup delay and spacebar press, but keeps rescanning
        /// until a scan returns with no items, so multiple visible items get collected.
        /// </summary>
        public void PerformSingleScan()
        {
            if (!IsGameWindowFocused())
            {
                _log("[Loot] Test scan aborted — game window is not in focus.");
                return;
            }

            LogScanArea();

            // Keep rescanning until a scan returns no items, so multiple
            // visible items get collected in sequence.
            // While scanning, spam spacebar on a background thread to collect
            // items under the character (the pixel-scan exclude zone).
            int collectedCount = 0;
            int pass = 0;
            while (true)
            {
                pass++;
                _log($"[Loot] === Scan pass {pass} ===");

                StartScanSpacebarSpam();
                try
                {
                    if (!PixelScan())
                    {
                        break;
                    }
                }
                finally
                {
                    StopScanSpacebarSpam();
                }

                collectedCount++;
                _log($"[Loot] Pass {pass} collected an item, rescanning…");
            }
            _log($"[LootSystem] Test scan complete — {pass} pass(es), {collectedCount} item(s) collected from visible field.");
        }

        /// <summary>
        /// Visualises the scan area by moving the mouse along the perimeter of both
        /// scan regions (SmallScan and BigScan). 3 passes for testing.
        /// Moves in steps of 5 pixels, 1ms dwell at each step.
        /// </summary>
        public void TestScanAreaVisualization()
        {
            LogScanArea();

            for (int pass = 1; pass <= 3; pass++)
            {
                _log($"[Loot] === Perimeter trace pass {pass}/3 ===");
                TracePerimeter(smallX[0], smallY[0], smallX[1], smallY[1], "SmallScan");
                TracePerimeter(bigX[0], bigY[0], bigX[1], bigY[1], "BigScan");
            }
            _log("[Loot] Perimeter trace complete.");
        }

        /// <summary>
        /// Moves the mouse along the 4 edges of a rectangle defined by (xMin,yMin)-(xMax,yMax).
        /// Coordinates are in old-screen-absolute (same as the hardcoded scan ranges).
        /// Steps of 5 pixels, 1ms dwell at each step.
        /// </summary>
        private void TracePerimeter(int xMin, int yMin, int xMax, int yMax, string name)
        {
            _log($"[Loot] {name} perimeter: ({xMin},{yMin}) → ({xMax},{yMin}) → ({xMax},{yMax}) → ({xMin},{yMax}) → ({xMin},{yMin})");

            // Top edge: left → right, X +5
            for (int x = xMin; x <= xMax; x += 5)
                TraceStep(x, yMin, name);

            // Right edge: top → bottom, Y +5
            for (int y = yMin; y <= yMax; y += 5)
                TraceStep(xMax, y, name);

            // Bottom edge: right → left, X -5
            for (int x = xMax; x >= xMin; x -= 5)
                TraceStep(x, yMax, name);

            // Left edge: bottom → top, Y -5
            for (int y = yMax; y >= yMin; y -= 5)
                TraceStep(xMin, y, name);
        }

        private void TraceStep(int oldScreenAbsX, int oldScreenAbsY, string name)
        {
            // Convert old screen-absolute → current screen using coord offset.
            int screenX = oldScreenAbsX + _coordOffsetX;
            int screenY = oldScreenAbsY + _coordOffsetY;
            MouseOperations.SetCursorPositionAbsolute(screenX, screenY);
            Thread.Sleep(1);
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
            // ── Focus guard: if the game window is NOT the foreground window,
            //    do NOT scan, click, or send any input.  Without this check the
            //    loot bot would capture/click whatever window is on top (desktop,
            //    folders, browser, etc.) and potentially cause damage. ──
            if (!IsGameWindowFocused())
            {
                StopScanSpacebarSpam();
                if (_lootState != LootMachineState.Idle)
                {
                    _lootState = LootMachineState.Idle;
                    _consecutiveEmptySpacePresses = 0;
                }
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
                StopScanSpacebarSpam();
                if (_lootState != LootMachineState.Idle)
                {
                    _lootState = LootMachineState.Idle;
                    _consecutiveEmptySpacePresses = 0;
                }
                await Task.Delay(BotConstants.Delays.LootUpdateMs, token);
                return;
            }

            // ── Track mob selection to detect mob death ──
            bool isMobSelected = _memoryService.IsMobSelected();

            // Mob just died (was selected → no longer selected) → TAB first to
            // check if there are more mobs to kill before starting loot.
            if (_wasMobSelectedPrev && !isMobSelected)
            {
                _log("[Loot] Mob killed — pressing TAB to check for more mobs.");
                _lootState = LootMachineState.PostMobTab;
                _nextActionTime = DateTime.UtcNow;
                _consecutiveEmptySpacePresses = 0;
                _wasMobSelectedPrev = false;
            }

            // ── If a mob is selected, TOTALLY CANCEL any loot in progress. ──
            if (isMobSelected)
            {
                _wasMobSelectedPrev = true;

                // Kill any running spacebar-spam background task from the Scan phase.
                StopScanSpacebarSpam();

                if (_lootState != LootMachineState.Idle)
                {
                    _log("[Loot] Mob selected — cancelling loot, returning to Idle.");
                    _lootState = LootMachineState.Idle;
                    _consecutiveEmptySpacePresses = 0;
                }
                await Task.Delay(BotConstants.Delays.LootUpdateMs, token);
                return;
            }

            // ── Loot state machine ──
            // PostMobTab→ press TAB (best-effort), then immediately start loot
            //   (combat bot's own targeting interrupts loot via IsMobSelected check)
            // AreaLoot → press spacebar x3, snapshot inventory → AreaLootWait → compare →
            //   (items collected) → AreaLoot again (keep spacebar-looting)
            //   (no items after N tries) → Scan (with 200ms delay)
            // Scan → pixel-scan for items (meanwhile spam spacebar), collect them → back to AreaLoot
            // ===================================================================

            switch (_lootState)
            {
                case LootMachineState.Idle:
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
                        // if a mob gets selected during the presses.
                        GameInput.PressKey(GameInput.VK_SPACE, GameInput.SCAN_SPACE);
                        if (_memoryService.IsMobSelected() || _memoryService.GetIsInCity()) { _lootState = LootMachineState.Idle; break; }
                        Thread.Sleep(30);
                        GameInput.PressKey(GameInput.VK_SPACE, GameInput.SCAN_SPACE);
                        if (_memoryService.IsMobSelected() || _memoryService.GetIsInCity()) { _lootState = LootMachineState.Idle; break; }
                        Thread.Sleep(30);
                        GameInput.PressKey(GameInput.VK_SPACE, GameInput.SCAN_SPACE);
                        _log("[Loot] Spacebar pressed x3 (area loot).");

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
                            _log("[Loot] Items collected — repeating spacebar.");
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
                                _log("[Loot] No more items via spacebar — switching to pixel scan in 100ms.");
                                _consecutiveEmptySpacePresses = 0;
                                _lootState = LootMachineState.Scan;
                                _nextActionTime = DateTime.UtcNow.AddMilliseconds(100);
                            }
                            else
                            {
                                // Try spacebar again after a brief pause.
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
                            _nextActionTime = DateTime.UtcNow.AddMilliseconds(50);
                        }
                        else
                        {
                            // No items found by scan — end the loot phase, restart with area-loot.
                            _log("[Loot] Scan complete — no more visible items.");
                            _lootState = LootMachineState.Idle;
                            _nextActionTime = DateTime.UtcNow.AddMilliseconds(
                                BotConstants.Delays.LootSpacePressMs);
                            _consecutiveEmptySpacePresses = 0;
                        }
                    }
                    finally
                    {
                        StopScanSpacebarSpam();
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

        private bool ScanRegion(int[] xRange, int[] yRange, string regionName)
        {
            try
            {
                CaptureScreen();

                // If bitmap capture failed (window not found, etc.), abort scan.
                if (_bitmap == null || _clientWidth <= 0 || _clientHeight <= 0)
                {
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

                // Also adjust exclude zone to bitmap-local coords.
                int exclXMin = ExcludeXMin - refX;
                int exclXMax = ExcludeXMax - refX;
                int exclYMin = ExcludeYMin - refY;
                int exclYMax = ExcludeYMax - refY;

                if (xStart >= xEnd || yStart >= yEnd)
                {
                    _log($"[Loot] {regionName}: clamped range is empty (out of client area).");
                    return false;
                }

                _wasSodDetected = false;
                int whiteCount = 0;
                int scanPixels = (xEnd - xStart) * (yEnd - yStart);

                // Check once before starting the scan — if a mob is selected or we're in
                // city, abort immediately.  During the scan the top-level Update() will
                // cancel on the next tick if a new mob gets selected, so we don't need
                // to check on every pixel.
                if (_memoryService.IsMobSelected() || _memoryService.GetIsInCity())
                {
                    _log($"[Loot] {regionName}: combat/city detected before scan — aborting.");
                    return false;
                }

                for (int x = xStart; x < xEnd; x++)
                {
                    for (int y = yStart; y < yEnd; y++)
                    {
                        Color pixelColor = _bitmap.GetPixel(x, y);

                        bool inExcludeZone = (x >= exclXMin && x <= exclXMax && y >= exclYMin && y <= exclYMax);
                        bool isWhite = (pixelColor.R == 255 && pixelColor.G == 255 && pixelColor.B == 255);

                        if (!inExcludeZone && isWhite)
                        {
                            whiteCount++;
                            if (whiteCount > MAX_WHITE_PIXELS)
                            {
                                // Exceeded threshold — this is likely a dialog/UI window,
                                // not the game world. Abort the entire scan pass.
                                _log($"[Loot] {regionName}: aborted — >{MAX_WHITE_PIXELS} white pixels (likely dialog/UI).");
                                return false;
                            }

                            if (TryCollectAt(x, y)) return true;
                            if (TryCollectAt(x + 1, y + 1)) return true;
                            if (TryCollectAt(x - 1, y - 1)) return true;
                            if (TryCollectAt(x + 1, y - 1)) return true;
                            if (TryCollectAt(x - 1, y + 1)) return true;

                            if (_wasSodDetected)
                            {
                                return true;
                            }
                        }
                    }
                }
                _log($"[Loot] {regionName}: scanned {scanPixels} pixels, {whiteCount} white candidates, no item collected.");
            }
            catch (Exception ex)
            {
                _log($"PixelScan error: {ex.Message}");
            }
            return false;
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

        private bool TryCollectAt(int x, int y)
        {
            WaitMouseInPosition(x, y);

            // If a mob got selected or player entered city, abort immediately.
            if (_memoryService.IsMobSelected() || _memoryService.GetIsInCity())
            {
                return false;
            }

            // If the mouseover indicator shows an item is selected (value 10312),
            // treat it as SOD/SOP and collect regardless of the specific item type.
            if (_memoryService.IsLootMouseOver())
            {
                _wasSodDetected = true;
                CollectionClick();
                return true;
            }
            return false;
        }

        private void CollectionClick()
        {
            int positionBeforeClick = GetPositionX();

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
                // If a mob was selected or player entered city, abort immediately.
                if (_memoryService.IsMobSelected() || _memoryService.GetIsInCity())
                {
                    _log("[Loot] Mob/city detected during collection — aborting spacebar spam.");
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

        private void WaitMouseInPosition(int bitmapLocalX, int bitmapLocalY)
        {
            // Clamp to actual client area as safety net.
            int clampedX = Math.Clamp(bitmapLocalX, 0, Math.Max(_clientWidth - 1, 0));
            int clampedY = Math.Clamp(bitmapLocalY, 0, Math.Max(_clientHeight - 1, 0));
            var (screenX, screenY) = BitmapLocalToScreen(clampedX, clampedY);
            MouseOperations.SetCursorPositionAbsolute(screenX, screenY);
            // Give the game time to update the mouseover memory value before IsLootMouseOver checks it.
            Thread.Sleep(1);
        }

        private int GetPositionX()
        {
             return _memoryService.GetLootPositionX();
        }
    }
}
