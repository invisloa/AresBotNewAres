using System.Drawing;
using DriverScanTester;
using DriverScanTester.Services;
using static DriverScanTester.Services.LootSystem;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class LootAquamarineDetectionTests
    {
        [Theory]
        [InlineData(0, 255, 170, true)]   // exact client-mod aquamarine #00FFAA
        [InlineData(40, 200, 140, true)]  // shaded by scene lighting
        [InlineData(80, 160, 120, true)]  // darker/fogged shade
        [InlineData(255, 255, 255, false)] // white is NOT normal loot
        [InlineData(255, 20, 147, false)]  // SOD/SOP DeepPink
        [InlineData(0, 255, 0, false)]     // pure green terrain/effects
        [InlineData(0, 255, 255, false)]   // pure cyan effects
        [InlineData(80, 160, 80, false)]   // grass green
        [InlineData(128, 128, 128, false)] // neutral gray
        public void AquamarineClassifier_AcceptsShadedCyanGreenOnly(
            int r, int g, int b, bool expected)
        {
            Assert.Equal(expected, LootSystem.IsAquamarineLootPixel(r, g, b));
        }

        [Fact]
        public void ReferenceReaderUsesAquamarineClassifier()
        {
            using var bitmap = new Bitmap(3, 1);
            bitmap.SetPixel(0, 0, Color.FromArgb(0, 255, 170));
            bitmap.SetPixel(1, 0, Color.White);
            bitmap.SetPixel(2, 0, Color.FromArgb(255, 20, 147));
            var points = new List<Point>();

            LootSystem.CollectTargetPointsFromBitmapReference(
                bitmap, 0, bitmap.Width, 0, bitmap.Height,
                pinkOnly: false, targetPoints: points);

            Assert.Equal(new[] { new Point(0, 0) }, points);
        }

        [Fact]
        public void NormalCombinedBitmapReaderCollectsBothMasksFromOneFrameAndRejectsMarkerMagenta()
        {
            using var bitmap = new Bitmap(3, 1);
            bitmap.SetPixel(0, 0, Color.FromArgb(0, 255, 170));
            bitmap.SetPixel(1, 0, Color.FromArgb(255, 34, 175));
            bitmap.SetPixel(2, 0, Color.FromArgb(255, 0, 255));
            var aquamarinePoints = new List<Point>();
            var pinkPoints = new List<Point>();

            LootSystem.PixelCollectionStats stats = LootSystem.CollectTargetPointsFromBitmap(
                bitmap, pinkOnly: false, aquamarinePoints, pinkPoints);

            Assert.Equal(new[] { new Point(0, 0) }, aquamarinePoints);
            Assert.Equal(new[] { new Point(1, 0) }, pinkPoints);
            Assert.Equal(1, stats.MaskPixels);
            Assert.Equal(2, stats.RawPinkPixels);
            Assert.Equal(1, stats.MarkerPixelsRejected);
        }

        [Fact]
        public void BulkBufferReaderCollectsAquamarineFromBgra()
        {
            const int width = 2;
            const int height = 1;
            const int stride = width * 4;
            byte[] buffer = new byte[stride * height];
            // Pixel 0: aquamarine BGRA(170, 255, 0, 255).
            buffer[0] = 170;
            buffer[1] = 255;
            buffer[2] = 0;
            buffer[3] = 255;
            // Pixel 1: white, which must not match the aquamarine-only mask.
            buffer[4] = 255;
            buffer[5] = 255;
            buffer[6] = 255;
            buffer[7] = 255;

            var points = new List<Point>();
            LootSystem.CollectTargetPointsFromBuffer(
                buffer, stride, height, 0, width, 0, height,
                pinkOnly: false, targetPoints: points);

            Assert.Equal(new[] { new Point(0, 0) }, points);
        }

        [Fact]
        public void CombinedBufferReaderKeepsBothMasksAlignedForBottomUpFrames()
        {
            const int width = 2;
            const int height = 2;
            const int stride = -width * 4;
            byte[] buffer = new byte[Math.Abs(stride) * height];

            void SetPixel(int x, int y, byte r, byte g, byte b)
            {
                int row = height - 1 - y;
                int offset = row * Math.Abs(stride) + x * 4;
                buffer[offset] = b;
                buffer[offset + 1] = g;
                buffer[offset + 2] = r;
                buffer[offset + 3] = 255;
            }

            SetPixel(0, 0, 0, 255, 170);
            SetPixel(1, 1, 255, 34, 175);
            var aquamarinePoints = new List<Point>();
            var pinkPoints = new List<Point>();

            LootSystem.CollectTargetPointsFromBuffer(
                buffer, stride, height, 0, width, 0, height,
                pinkOnly: false, aquamarinePoints, pinkPoints);

            Assert.Equal(new[] { new Point(0, 0) }, aquamarinePoints);
            Assert.Equal(new[] { new Point(1, 1) }, pinkPoints);
        }

        [Fact]
        public void CombinedBigRegionMasksClipAquamarineButKeepPinkAcrossTheClient()
        {
            using var bitmap = new Bitmap(1500, 800);
            Color aqua = Color.FromArgb(0, 255, 170);
            Color pinkColor = Color.FromArgb(255, 34, 175);
            bitmap.SetPixel(600, 300, pinkColor); // Outside SmallScan, inside BigScan.
            bitmap.SetPixel(900, 400, aqua);       // Inside BigScan's aquamarine rectangle.
            bitmap.SetPixel(1400, 760, aqua);      // Outside BigScan's aquamarine rectangle.
            bitmap.SetPixel(1400, 780, pinkColor);// Pink remains whole-client in BigScan.
            var aquamarinePoints = new List<Point>();
            var pinkPoints = new List<Point>();

            LootSystem.CollectTargetPointsFromBitmap(
                bitmap, pinkOnly: false, aquamarinePoints, pinkPoints,
                aquamarineBounds: new Rectangle(550, 290, 810, 470));

            Assert.Equal(new[] { new Point(900, 400) }, aquamarinePoints);
            Assert.Equal(new[] { new Point(600, 300), new Point(1400, 780) }, pinkPoints);
        }

        [Fact]
        public void PassiveAnalysisKeepsTouchingAquamarineAndPinkComponentsIndependent()
        {
            using var bitmap = new Bitmap(100, 60);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
                using var aquamarine = new SolidBrush(Color.FromArgb(0, 255, 170));
                using var pink = new SolidBrush(Color.FromArgb(255, 34, 175));
                graphics.FillRectangle(aquamarine, 20, 20, 12, 12);
                graphics.FillRectangle(pink, 32, 20, 12, 12);
            }

            LootPreScanResult result = LootSystem.AnalyzeNormalLootCandidates(
                bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                BotConstants.Loot.PassivePreScanMaxCandidateComponents);

            Assert.Equal(LootPreScanOutcome.PotentialCandidate, result.Outcome);
            Assert.Equal(2, result.CandidateComponents);
            Assert.Equal(new Rectangle(20, 20, 24, 12), result.CandidateBounds);
        }

        [Fact]
        public void PassiveAnalysisFindsAquamarineLootCube()
        {
            using var bitmap = new Bitmap(200, 200);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (var brush = new SolidBrush(Color.FromArgb(0, 255, 170)))
            {
                graphics.Clear(Color.Black);
                graphics.FillRectangle(brush, 50, 50, 14, 14);
            }

            LootPreScanResult result = LootSystem.AnalyzeNormalLootCandidates(
                bitmap, new Rectangle(0, 0, 200, 200),
                BotConstants.Loot.PassivePreScanMaxCandidateComponents);

            Assert.Equal(LootPreScanOutcome.PotentialCandidate, result.Outcome);
            Assert.True(result.CandidateComponents >= 1);
            Assert.True(result.LootPixels > 0);
            Assert.False(result.CandidateBounds.IsEmpty);
        }

        [Fact]
        public void WhiteBlobIsNotANormalLootCandidate()
        {
            using var bitmap = new Bitmap(200, 200);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
                graphics.FillRectangle(Brushes.White, 50, 50, 14, 14);
            }

            LootPreScanResult result = LootSystem.AnalyzeNormalLootCandidates(
                bitmap, new Rectangle(0, 0, 200, 200),
                BotConstants.Loot.PassivePreScanMaxCandidateComponents);

            Assert.Equal(LootPreScanOutcome.NoCandidate, result.Outcome);
            Assert.Equal(0, result.LootPixels);
        }

        [Fact]
        public void AquamarineCubeUsesTheNormalLootFootprintFilter()
        {
            var component = new LootSystem.LootComponent();
            for (int x = 0; x < 10; x++)
            for (int y = 0; y < 10; y++)
                component.Add(x, y);

            Assert.True(LootSystem.IsNormalLootCandidate(component));
        }
    }
}
