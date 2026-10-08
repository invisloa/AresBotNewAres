using System;
using System.Drawing;
using System.Drawing.Imaging;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class PlayerMarkerPixelDetectorTests
    {
        private static readonly Rectangle LootCharacterExclusionArea = new(430, 315, 161, 171);

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

            Assert.True(detector.ContainsOtherPlayerMarker(frame, new Rectangle(130, 130, 40, 40)));
        }

        [Fact]
        public void DetectsAllianceGreenTriangleAwayFromLocalPlayer()
        {
            using Bitmap frame = CreateFrame();
            FillTriangle(frame, x: 205, y: 80, size: 18, Color.FromArgb(0, 255, 0));

            var detector = new PlayerMarkerPixelDetector();

            Assert.True(detector.ContainsOtherPlayerMarker(frame, new Rectangle(130, 130, 40, 40)));
        }

        [Fact]
        public void RejectsYellowChatGlyphThatPreviouslyPassedTheDetector()
        {
            using Bitmap frame = CreateFrame();
            int pixels = FillCompactHChatGlyph(frame, x: 100, y: 100, width: 6, height: 9,
                Color.FromArgb(255, 255, 0));
            float fillRatio = pixels / (float)(6 * 9);
            float aspectRatio = 9f / 6f;

            // This compact, thick glyph passed the old 12-pixel / 4-pixel / 0.58 filters.
            Assert.InRange(pixels, 25, 45);
            Assert.True(fillRatio > 0.58f);
            Assert.True(aspectRatio <= 1.7f);
            Assert.False(new PlayerMarkerPixelDetector().ContainsOtherPlayerMarker(frame, Rectangle.Empty));
        }

        [Fact]
        public void RejectsCompactGreenChatGlyphAtSmallClientHeight()
        {
            using Bitmap frame = CreateFrame();
            FillCompactHChatGlyph(frame, x: 100, y: 100, width: 9, height: 7,
                Color.FromArgb(0, 255, 0));

            // The absolute size/pixel floors must not shrink at this 240px client height.
            Assert.False(new PlayerMarkerPixelDetector().ContainsOtherPlayerMarker(frame, Rectangle.Empty));
        }

        [Fact]
        public void RejectsExactGreenQButtonComponentsFromFalsePositiveScreenshot()
        {
            using Bitmap frame = CreateFrame(1024, 768);
            Color green = Color.FromArgb(0, 255, 0);
            FillPixelPattern(frame, 18, 610,
                new[] { "...#.", "..###", ".####", "###..", ".#...", "##...", "#....", "#...." }, green);
            FillPixelPattern(frame, 25, 629,
                new[] { "###....", ".####..", ".##..##", "......#" }, green);

            Rectangle[] hudAreas = PlayerMarkerPixelDetector.GetHudExclusionAreas(frame.Width, frame.Height);
            Assert.Contains(hudAreas, area => area.IntersectsWith(new Rectangle(18, 610, 5, 8)));
            Assert.Contains(hudAreas, area => area.IntersectsWith(new Rectangle(25, 629, 7, 4)));
            Assert.False(new PlayerMarkerPixelDetector().ContainsOtherPlayerMarker(frame, Rectangle.Empty));
        }

        [Fact]
        public void RejectsPlayerSizedTriangleInsideQButtonHudRegion()
        {
            using Bitmap frame = CreateFrame(1024, 768);
            FillTriangle(frame, x: 12, y: 600, size: 18, Color.FromArgb(0, 255, 0));

            Assert.False(new PlayerMarkerPixelDetector().ContainsOtherPlayerMarker(frame, Rectangle.Empty));
        }

        [Fact]
        public void RejectsGreenSquareOutsideHudBecauseAllianceMarkerMustBeTriangular()
        {
            using Bitmap frame = CreateFrame(1024, 768);
            FillRectangle(frame, new Rectangle(200, 300, 14, 14), Color.FromArgb(0, 255, 0));

            Assert.False(new PlayerMarkerPixelDetector().ContainsOtherPlayerMarker(frame, Rectangle.Empty));
        }

        [Fact]
        public void DetectsOccludedEmpireMarkerMatchingLiveRendering()
        {
            using Bitmap frame = CreateFrame(1024, 768);
            FillOccludedEmpireMarker(frame, x: 338, y: 154);

            bool found = new PlayerMarkerPixelDetector().TryFindOtherPlayerMarker(
                frame, LootCharacterExclusionArea, out PlayerMarkerCandidate candidate);

            Assert.True(found);
            Assert.Equal("Empire/yellow", candidate.Faction);
            Assert.Equal(new Rectangle(338, 154, 33, 30), candidate.Bounds);
            Assert.Equal(430, candidate.PixelCount);
            Assert.InRange(candidate.FillRatio, 0.43f, 0.44f);
            Assert.True(candidate.FillRatio < 0.58f);
        }

        [Fact]
        public void RealMarkerWinsWhileYellowChatGlyphsAreRejected()
        {
            using Bitmap frame = CreateFrame(1024, 768);
            FillOccludedEmpireMarker(frame, x: 338, y: 154);
            FillBottomChatGlyphs(frame);

            bool found = new PlayerMarkerPixelDetector().TryFindOtherPlayerMarker(
                frame, LootCharacterExclusionArea, out PlayerMarkerCandidate candidate);

            Assert.True(found);
            Assert.Equal("Empire/yellow", candidate.Faction);
            Assert.Equal(new Rectangle(338, 154, 33, 30), candidate.Bounds);
            Assert.True(candidate.Center.X is >= 338 and < 371);
            Assert.True(candidate.Center.Y is >= 154 and < 184);
            Assert.True(candidate.Bounds.Bottom < 300, "The accepted component must be the marker, not a bottom chat glyph.");
        }

        [Fact]
        public void ChatOnlyYellowGlyphsDoNotProduceAPlayerMarker()
        {
            using Bitmap frame = CreateFrame(1024, 768);
            FillBottomChatGlyphs(frame);

            Assert.False(new PlayerMarkerPixelDetector().ContainsOtherPlayerMarker(
                frame, LootCharacterExclusionArea));
        }

        [Fact]
        public void RealisticLocalEmpireMarkerIsAcceptedByShapeButExcludedByLootArea()
        {
            using Bitmap frame = CreateFrame(1024, 768);
            FillRealisticLocalEmpireMarker(frame, x: 501, y: 370);
            var detector = new PlayerMarkerPixelDetector();

            Assert.True(detector.TryFindOtherPlayerMarker(frame, Rectangle.Empty, out PlayerMarkerCandidate ownCandidate));
            Assert.Equal(new Rectangle(501, 370, 19, 21), ownCandidate.Bounds);
            Assert.Equal(164, ownCandidate.PixelCount);
            Assert.InRange(ownCandidate.FillRatio, 0.40f, 0.42f);
            Assert.False(detector.ContainsOtherPlayerMarker(frame, LootCharacterExclusionArea));
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

        private static Bitmap CreateFrame(int width = 320, int height = 240)
            => new(width, height, PixelFormat.Format32bppArgb);

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

        private static void FillPixelPattern(Bitmap bitmap, int x, int y, string[] rows, Color color)
        {
            for (int row = 0; row < rows.Length; row++)
            for (int column = 0; column < rows[row].Length; column++)
            {
                if (rows[row][column] == '#')
                    bitmap.SetPixel(x + column, y + row, color);
            }
        }

        private static int FillCompactHChatGlyph(Bitmap bitmap, int x, int y, int width, int height, Color color)
        {
            int pixelCount = 0;
            int middleRow = height / 2;
            for (int row = 0; row < height; row++)
            for (int column = 0; column < width; column++)
            {
                bool inVerticalStroke = column < 2 || column >= width - 2;
                bool inCrossbar = row == middleRow || row == middleRow + 1;
                if (!inVerticalStroke && !inCrossbar)
                    continue;

                bitmap.SetPixel(x + column, y + row, color);
                pixelCount++;
            }

            return pixelCount;
        }

        private static void FillBottomChatGlyphs(Bitmap bitmap)
        {
            Color yellow = Color.FromArgb(255, 255, 0);
            FillCompactHChatGlyph(bitmap, x: 740, y: 690, width: 6, height: 9, yellow);
            FillCompactHChatGlyph(bitmap, x: 760, y: 690, width: 6, height: 7, yellow);
            FillCompactHChatGlyph(bitmap, x: 780, y: 690, width: 7, height: 7, yellow);
            FillCompactHChatGlyph(bitmap, x: 800, y: 690, width: 9, height: 7, yellow);
        }

        private static void FillOccludedEmpireMarker(Bitmap bitmap, int x, int y)
        {
            FillConnectedRowProfile(bitmap, x, y, width: 33, height: 30,
                row => row is 13 or 14 or 15 ? 33 : row is 10 or 11 or 12 or 16 or 17 or 18 or 19 ? 13 : 12,
                Color.FromArgb(255, 255, 0));
        }

        private static void FillRealisticLocalEmpireMarker(Bitmap bitmap, int x, int y)
        {
            FillConnectedRowProfile(bitmap, x, y, width: 19, height: 21,
                row => row is 9 or 10 or 11 ? 19 : row == 0 ? 5 : 6,
                Color.FromArgb(255, 255, 0));
        }

        private static void FillConnectedRowProfile(
            Bitmap bitmap,
            int x,
            int y,
            int width,
            int height,
            Func<int, int> rowWidth,
            Color color)
        {
            for (int row = 0; row < height; row++)
            {
                int currentWidth = rowWidth(row);
                int left = x + (width - currentWidth) / 2;
                for (int column = 0; column < currentWidth; column++)
                    bitmap.SetPixel(left + column, y + row, color);
            }
        }
    }
}
