using System;
using System.Drawing;
using System.Drawing.Imaging;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class PlayerMarkerPixelDetectorTests
    {
        [Theory]
        [InlineData(255, 255, 0, true)]
        [InlineData(0, 255, 0, true)]
        [InlineData(255, 20, 147, false)]
        [InlineData(255, 255, 255, false)]
        [InlineData(80, 180, 60, false)]
        public void ClassifiesConfiguredFactionMarkerColors(int r, int g, int b, bool expected)
        {
            Assert.Equal(expected,
                PlayerMarkerPixelDetector.ClassifyPlayerMarkerPixel(r, g, b) != 0);
        }

        [Fact]
        public void DetectsEmpireYellowSquareAwayFromLocalPlayer()
        {
            using Bitmap frame = CreateFrame();
            FillRectangle(frame, new Rectangle(210, 90, 14, 14), Color.FromArgb(255, 255, 0));

            var detector = new PlayerMarkerPixelDetector();

            Assert.True(detector.ContainsOtherPlayerMarker(frame, new Rectangle(130, 90, 40, 40)));
        }

        [Fact]
        public void DetectsAllianceGreenTriangleAwayFromLocalPlayer()
        {
            using Bitmap frame = CreateFrame();
            FillTriangle(frame, x: 205, y: 80, size: 18, Color.FromArgb(0, 255, 0));

            var detector = new PlayerMarkerPixelDetector();

            Assert.True(detector.ContainsOtherPlayerMarker(frame, new Rectangle(130, 90, 40, 40)));
        }

        [Fact]
        public void IgnoresMarkerInsideLootCharacterExclusionArea()
        {
            using Bitmap frame = CreateFrame();
            FillRectangle(frame, new Rectangle(94, 114, 14, 14), Color.FromArgb(0, 255, 0));

            var detector = new PlayerMarkerPixelDetector();

            Assert.False(detector.ContainsOtherPlayerMarker(frame, new Rectangle(80, 100, 40, 40)));
        }

        [Fact]
        public void LootCharacterExclusionAreaMatchesLootScannerReferenceCoordinates()
        {
            Rectangle area = PlayerMarkerPixelDetector.GetLootCharacterExclusionArea(
                1024, 768, referenceClientOriginX: 450, referenceClientOriginY: 103);

            Assert.Equal(new Rectangle(430, 315, 161, 171), area);
        }

        [Fact]
        public void RejectsThinGreenTextLikeComponent()
        {
            using Bitmap frame = CreateFrame();
            FillRectangle(frame, new Rectangle(180, 100, 24, 3), Color.FromArgb(0, 255, 0));

            var detector = new PlayerMarkerPixelDetector();

            Assert.False(detector.ContainsOtherPlayerMarker(frame, new Rectangle(80, 100, 40, 40)));
        }

        private static Bitmap CreateFrame() => new(320, 240, PixelFormat.Format32bppArgb);

        private static void FillRectangle(Bitmap bitmap, Rectangle rectangle, Color color)
        {
            for (int y = rectangle.Top; y < rectangle.Bottom; y++)
            for (int x = rectangle.Left; x < rectangle.Right; x++)
                bitmap.SetPixel(x, y, color);
        }

        private static void FillTriangle(Bitmap bitmap, int x, int y, int size, Color color)
        {
            int centerX = x + size / 2;
            for (int row = 0; row < size; row++)
            {
                int halfWidth = (int)Math.Round(row * (size / 2.0) / (size - 1));
                for (int px = centerX - halfWidth; px <= centerX + halfWidth; px++)
                    bitmap.SetPixel(px, y + row, color);
            }
        }
    }
}
