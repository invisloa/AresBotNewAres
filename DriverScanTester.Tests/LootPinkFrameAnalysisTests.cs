using System.Drawing;
using System.Drawing.Imaging;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class LootPinkFrameAnalysisTests
    {
        [Fact]
        public void AnalyzePinkFrameReportsLootSizedPinkBlobWithoutMouseoverOrInput()
        {
            using var frame = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
            Fill(frame, new Rectangle(10, 12, 12, 12), Color.FromArgb(255, 34, 175));

            var result = LootSystem.AnalyzePinkFrame(frame);

            Assert.Equal(144, result.PinkPixels);
            Assert.Equal(1, result.Components);
            Assert.Equal(1, result.LootCandidates);
            Assert.Equal(0, result.MobMarkerRings);
            Assert.Equal(0, result.TinyComponents);
        }

        [Fact]
        public void AnalyzePinkFrameCountsButRejectsSubMinimumBlob()
        {
            using var frame = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
            Fill(frame, new Rectangle(10, 12, 8, 8), Color.FromArgb(255, 34, 175));

            var result = LootSystem.AnalyzePinkFrame(frame);

            Assert.Equal(64, result.PinkPixels);
            Assert.Equal(1, result.Components);
            Assert.Equal(0, result.LootCandidates);
            Assert.Equal(1, result.TinyComponents);
        }

        [Fact]
        public void AnalyzePinkFrameExcludesArtificialMagentaMobMarkerPixels()
        {
            using var frame = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
            Fill(frame, new Rectangle(10, 12, 12, 12), Color.FromArgb(255, 0, 255));

            var result = LootSystem.AnalyzePinkFrame(frame);

            Assert.Equal(0, result.PinkPixels);
            Assert.Equal(0, result.Components);
            Assert.Equal(0, result.LootCandidates);
        }

        private static void Fill(Bitmap bitmap, Rectangle rectangle, Color color)
        {
            for (int y = rectangle.Top; y < rectangle.Bottom; y++)
            for (int x = rectangle.Left; x < rectangle.Right; x++)
                bitmap.SetPixel(x, y, color);
        }
    }
}
