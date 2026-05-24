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

        public LootSystem(GameMemoryService memoryService, Action<string> log)
        {
            _memoryService = memoryService;
            _log = log;

            _bitmap = new Bitmap(BotConstants.Loot.BitmapWidth, BotConstants.Loot.BitmapHeight);
            _graphics = Graphics.FromImage(_bitmap);
        }

        public async Task Update(CancellationToken token)
        {
             PixelScan();
             await Task.Delay(BotConstants.Delays.LootUpdateMs, token);
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
            if (highlighted == BotConstants.GameMagicValues.Sod)
            {
                _wasSodDetected = true;
            }
        }

        private bool ClickAndCollectWhatItem()
        {
            int highlighted = GetCurrentItemHighlightedType();

            if (highlighted == BotConstants.GameMagicValues.Sod || highlighted == BotConstants.GameMagicValues.Sop)
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
                MouseOperations.MoveAndLeftClick(BotConstants.Loot.UnbugClickX, BotConstants.Loot.UnbugClickY, 100);
            }
        }

        private void PixelScanUnderChar()
        {
            for (int x = BotConstants.Loot.UnderCharScanStartX; x < BotConstants.Loot.UnderCharScanEndX; x += BotConstants.Loot.UnderCharScanStepX)
            {
                for (int y = BotConstants.Loot.UnderCharScanStartY; y < BotConstants.Loot.UnderCharScanEndY; y += BotConstants.Loot.UnderCharScanStepY)
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
