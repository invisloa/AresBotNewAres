using System;
using System.Drawing;
using System.Drawing.Imaging;
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
        private static readonly int[] smallX = { 850, 1170 };
        private static readonly int[] smallY = { 410, 730 };
        private static readonly int[] bigX = { 550, 1360 };
        private static readonly int[] bigY = { 290, 835 };

        // Character exclude zone
        private const int ExcludeXMin = 934;
        private const int ExcludeXMax = 979;
        private const int ExcludeYMin = 500;
        private const int ExcludeYMax = 538;

        private Bitmap _bitmap;
        private Graphics _graphics;
        private bool _wasSodDetected = false;

        public LootSystem(GameMemoryService memoryService, Action<string> log)
        {
            _memoryService = memoryService;
            _log = log;

            _bitmap = new Bitmap(1370, 840);
            _graphics = Graphics.FromImage(_bitmap);
        }

        public async Task Update(CancellationToken token)
        {
             PixelScan();
             await Task.Delay(10, token);
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

                for (int x = xRange[0]; x < xRange[1]; x++)
                {
                    for (int y = yRange[0]; y < yRange[1]; y++)
                    {
                        Color pixelColor = _bitmap.GetPixel(x, y);

                        bool inExcludeZone = (x >= ExcludeXMin && x <= ExcludeXMax && y >= ExcludeYMin && y <= ExcludeYMax);
                        bool isWhite = (pixelColor.R == 255 && pixelColor.G == 255 && pixelColor.B == 255);

                        if (!inExcludeZone && isWhite)
                        {
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
            _graphics.CopyFromScreen(0, 0, 0, 0, _bitmap.Size);
        }

        private bool TryCollectAt(int x, int y)
        {
            WaitMouseInPosition(x, y);
            CheckIfSodSelected();
            return ClickAndCollectWhatItem();
        }

        private void CheckIfSodSelected()
        {
            int highlighted = GetCurrentItemHighlightedType();
            if (highlighted == GameMemoryService.SOD)
            {
                _wasSodDetected = true;
            }
        }

        private bool ClickAndCollectWhatItem()
        {
            int highlighted = GetCurrentItemHighlightedType();

            if (highlighted == GameMemoryService.SOD || highlighted == GameMemoryService.SOP)
            {
                CollectionClick(highlighted);
                return true;
            }
            return false;
        }

        private void CollectionClick(int itemType)
        {
            int positionBeforeClick = GetPositionX();

            MouseOperations.MouseEvent(MouseOperations.MouseEventFlags.RightUp);
            MouseOperations.MouseEvent(MouseOperations.MouseEventFlags.LeftDown);

            _log($"Collecting Item Type: {itemType}");
            Thread.Sleep(50);

            MouseOperations.MouseEvent(MouseOperations.MouseEventFlags.LeftUp);

            Thread.Sleep(500); // Wait for collection animation/movement

            UnbugWhenCollecting(positionBeforeClick);

            PixelScanUnderChar();
        }

        private void UnbugWhenCollecting(int beforeClickPosX)
        {
            int currentX = GetPositionX();
            if (beforeClickPosX == currentX)
            {
                MouseOperations.MoveAndLeftClick(900, 600, 100);
            }
        }

        private void PixelScanUnderChar()
        {
            for (int x = 930; x < 980; x+=5)
            {
                for (int y = 500; y < 545; y+=5)
                {
                    WaitMouseInPosition(x, y);
                    if (ClickAndCollectWhatItem())
                    {
                        return;
                    }
                }
            }
        }

        private void WaitMouseInPosition(int x, int y)
        {
            MouseOperations.SetCursorPosition(x, y);
            Thread.Sleep(1);
        }

        private int GetCurrentItemHighlightedType()
        {
            return _memoryService.GetCurrentItemHighlightedType();
        }

        private int GetPositionX()
        {
             return _memoryService.GetLootPositionX();
        }
    }
}
