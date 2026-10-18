using System.Drawing;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class LootWhiteDetectionTests
    {
        [Theory]
        [InlineData(255, 255, 255, true)]
        [InlineData(220, 215, 205, true)]
        [InlineData(160, 150, 145, true)]
        [InlineData(145, 145, 145, true)]
        [InlineData(144, 144, 144, false)]
        [InlineData(180, 130, 120, false)]
        [InlineData(150, 185, 220, false)]
        [InlineData(255, 34, 175, false)] // Pink remains exclusive to the SOD/SOP scan.
        public void ShadedWhiteClassifier_AcceptsBrightNeutralShadesOnly(
            int r, int g, int b, bool expected)
        {
            Assert.Equal(expected, LootSystem.IsShadedWhiteLootPixel(r, g, b));
        }

        private static LootSystem.WhiteComponent MakeFilledBlob(int width, int height)
        {
            var component = new LootSystem.WhiteComponent();
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                    component.Add(x, y);
            }
            return component;
        }

        [Fact]
        public void ReferenceReaderUsesShadedWhiteClassifier()
        {
            using var bitmap = new Bitmap(2, 1);
            bitmap.SetPixel(0, 0, Color.FromArgb(180, 174, 169));
            bitmap.SetPixel(1, 0, Color.FromArgb(128, 128, 128));
            var points = new List<Point>();

            LootSystem.CollectTargetPointsFromBitmapReference(
                bitmap, 0, bitmap.Width, 0, bitmap.Height,
                applyExcludeZone: false, 0, 0, 0, 0,
                pinkOnly: false, targetPoints: points, shadedWhiteOnly: true);

            Assert.Equal(new[] { new Point(0, 0) }, points);
        }

        [Theory]
        [InlineData(9, 20, false)]
        [InlineData(20, 9, false)]
        [InlineData(10, 10, true)]
        [InlineData(64, 64, true)]
        public void WhiteLootCandidatesRequireLargeFootprintWithoutUpperCap(
            int width, int height, bool expected)
        {
            Assert.Equal(expected, LootSystem.IsShadedWhiteLootCandidate(MakeFilledBlob(width, height)));
        }

        [Fact]
        public void LegacyCompactWhiteSparkleDetectionIsPreserved()
        {
            Assert.True(LootSystem.IsLegacyWhiteSparkleCandidate(MakeFilledBlob(4, 4)));
        }

        [Fact]
        public void WhiteLootCandidateRejectsSparseDiagonalNoise()
        {
            var diagonal = new LootSystem.WhiteComponent();
            for (int i = 0; i < 10; i++)
                diagonal.Add(i, i);

            Assert.False(LootSystem.IsShadedWhiteLootCandidate(diagonal));
        }
    }
}
