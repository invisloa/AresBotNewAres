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
        private DateTime _lastSpacePressTime = DateTime.MinValue;

        // ── Client-area tracking (window-position independent) ──
        private int _clientOriginX;
        private int _clientOriginY;
        private bool _hasClientOrigin;

        /// <summary>Maximum white pixels before aborting scan — dialogs/UI have tons of white.</summary>
        private const int MAX_WHITE_PIXELS = 1000;

        /// <summary>Startup time — skip scans for the first 5s so loot doesn't run before movement.</summary>
        private readonly DateTime _createdAt = DateTime.UtcNow;

        public LootSystem(GameMemoryService memoryService, Action<string> log)
        {
            _memoryService = memoryService;
            _log = log;

            _bitmap = new Bitmap(BotConstants.Loot.BitmapWidth, BotConstants.Loot.BitmapHeight);
            _graphics = Graphics.FromImage(_bitmap);

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
        private static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern nint FindWindow(string lpClassName, string lpWindowName);

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
        /// Looks up the game window and stores its client-area top-left corner
        /// in screen coordinates. Falls back to (0,0) if the window cannot be found.
        /// </summary>
        private void ResolveClientOrigin()
        {
            nint hwnd = FindWindow(null, "Legend of Ares");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Ares");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Nostalgia");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Epic Of Ares Client");

            if (hwnd == nint.Zero)
            {
                _log("[LootSystem] Game window not found — falling back to screen origin (0,0).");
                _clientOriginX = 0;
                _clientOriginY = 0;
                _hasClientOrigin = false;
                return;
            }

            if (!GetClientRect(hwnd, out RECT clientRect))
            {
                _log("[LootSystem] GetClientRect failed — falling back to screen origin (0,0).");
                _clientOriginX = 0;
                _clientOriginY = 0;
                _hasClientOrigin = false;
                return;
            }

            POINT topLeft = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref topLeft))
            {
                _log("[LootSystem] ClientToScreen failed — falling back to screen origin (0,0).");
                _clientOriginX = 0;
                _clientOriginY = 0;
                _hasClientOrigin = false;
                return;
            }

            _clientOriginX = topLeft.X;
            _clientOriginY = topLeft.Y;
            _hasClientOrigin = true;

            _log($"[LootSystem] Game client area at screen ({_clientOriginX}, {_clientOriginY}) — scan coordinates are client-relative.");
        }

        /// <summary>
        /// Converts client-relative coordinates to absolute screen coordinates
        /// using the stored client origin.
        /// </summary>
        private (int ScreenX, int ScreenY) ClientToScreenAbsolute(int clientX, int clientY)
        {
            return (_clientOriginX + clientX, _clientOriginY + clientY);
        }

        /// <summary>
        /// Runs a single pixel scan pass (for testing via the "Test Loot" button).
        /// Skips the startup delay and spacebar press, just does the scan.
        /// </summary>
        public void PerformSingleScan()
        {
            PixelScan();
        }

        public async Task Update(CancellationToken token)
        {
            // ── Startup guard: skip scans for the first 5 seconds so loot
            //    doesn't start before the movement bot begins moving. ──
            if ((DateTime.UtcNow - _createdAt).TotalSeconds < 5.0)
            {
                await Task.Delay(BotConstants.Delays.LootUpdateMs, token);
                return;
            }

            // ── Periodically press spacebar so the game picks up nearby items
            //    while moving. ──
            TryPressSpaceForAreaLoot();

            // ── Pixel scan for SOD/SOP items on the ground. Runs even during
            //    combat — the ClickAndCollectWhatItem check ensures only SOD/SOP
            //    items are clicked. ──
            PixelScan();

            await Task.Delay(BotConstants.Delays.LootUpdateMs, token);
        }

        /// <summary>
        /// Presses the spacebar if enough time has elapsed since the last press.
        /// The game loots all surrounding items near the player when space is hit,
        /// and it also picks up items along the path while moving toward a target.
        /// </summary>
        private void TryPressSpaceForAreaLoot()
        {
            if ((DateTime.UtcNow - _lastSpacePressTime).TotalMilliseconds >= BotConstants.Delays.LootSpacePressMs)
            {
                GameInput.PressKey(GameInput.VK_SPACE, GameInput.SCAN_SPACE);
                _lastSpacePressTime = DateTime.UtcNow;
            }
        }

        private void PixelScan()
        {
            if (ScanRegion(smallX, smallY))
            {
                return;
            }
            if (ScanRegion(bigX, bigY))
            {
                return;
            }
        }

        private bool ScanRegion(int[] xRange, int[] yRange)
        {
            try
            {
                CaptureScreen();
                _wasSodDetected = false;
                int whiteCount = 0;

                for (int x = xRange[0]; x < xRange[1]; x++)
                {
                    for (int y = yRange[0]; y < yRange[1]; y++)
                    {
                        Color pixelColor = _bitmap.GetPixel(x, y);

                        bool inExcludeZone = (x >= ExcludeXMin && x <= ExcludeXMax && y >= ExcludeYMin && y <= ExcludeYMax);
                        bool isWhite = (pixelColor.R == 255 && pixelColor.G == 255 && pixelColor.B == 255);

                        if (!inExcludeZone && isWhite)
                        {
                            whiteCount++;
                            if (whiteCount > MAX_WHITE_PIXELS)
                            {
                                // Exceeded threshold — this is likely a dialog/UI window,
                                // not the game world. Abort the entire scan pass.
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

            _graphics.CopyFromScreen(_clientOriginX, _clientOriginY, 0, 0, _bitmap.Size);
        }

        private bool TryCollectAt(int x, int y)
        {
            WaitMouseInPosition(x, y);

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

            MouseOperations.MouseEvent(MouseOperations.MouseEventFlags.RightUp);
            MouseOperations.MouseEvent(MouseOperations.MouseEventFlags.LeftDown);

            _log("Collecting item (SOD/SOP by pixel color + mouseover indicator).");
            Thread.Sleep(BotConstants.Delays.CollectClickHoldMs);

            MouseOperations.MouseEvent(MouseOperations.MouseEventFlags.LeftUp);

            Thread.Sleep(BotConstants.Delays.CollectAnimationMs); // Wait for collection animation/movement

            UnbugWhenCollecting(positionBeforeClick);

            PixelScanUnderChar();
        }

        private void UnbugWhenCollecting(int beforeClickPosX)
        {
            int currentX = GetPositionX();
            if (beforeClickPosX == currentX)
            {
                var (screenX, screenY) = ClientToScreenAbsolute(BotConstants.Loot.UnbugClickX, BotConstants.Loot.UnbugClickY);
                MouseOperations.MoveAndLeftClickAbsolute(screenX, screenY, 100);
            }
        }

        private void PixelScanUnderChar()
        {
            for (int x = BotConstants.Loot.UnderCharScanStartX; x < BotConstants.Loot.UnderCharScanEndX; x += BotConstants.Loot.UnderCharScanStepX)
            {
                for (int y = BotConstants.Loot.UnderCharScanStartY; y < BotConstants.Loot.UnderCharScanEndY; y += BotConstants.Loot.UnderCharScanStepY)
                {
                    WaitMouseInPosition(x, y);
                    if (_memoryService.IsLootMouseOver())
                    {
                        CollectionClick();
                        return;
                    }
                }
            }
        }

        private void WaitMouseInPosition(int clientX, int clientY)
        {
            var (screenX, screenY) = ClientToScreenAbsolute(clientX, clientY);
            MouseOperations.SetCursorPositionAbsolute(screenX, screenY);
            Thread.Sleep(1);
        }

        private int GetPositionX()
        {
             return _memoryService.GetLootPositionX();
        }
    }
}
