using System.Collections.Generic;
using System.Linq;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// The fast NPC scan (stage 1) and the slow discovery scan (stage 2) use the SAME
    /// center-outward scan pattern. These tests pin that pattern down without the game.
    /// </summary>
    public sealed class NpcScanPatternTests
    {
        private const int ClientWidth = 1024;
        private const int ClientHeight = 768;

        /// <summary>Grid step used by both scan stages (production fast scan + slow discovery).</summary>
        private const int Step = ItemSellerService.SellerScanStepPx;

        /// <summary>The dense step used before the "1/10 of the work" change.</summary>
        private const int FormerDenseStep = 17;

        [Fact]
        public void ScanGridPerformsAboutOneTenthOfTheFormerDenseGridPoints()
        {
            // Requested: the NPC scan should do ~1/10 of what it did with the 17 px grid.
            // Point count grows with 1 / step², so the step is 17 px × √10 ≈ 54 px.
            Assert.Equal(54, ItemSellerService.SellerScanStepPx);

            int formerPoints = ItemSellerService
                .EnumerateSellerScanPoints(ClientWidth, ClientHeight, FormerDenseStep)
                .Count();
            int currentPoints = ItemSellerService
                .EnumerateSellerScanPoints(ClientWidth, ClientHeight)
                .Count();

            Assert.True(formerPoints > currentPoints);
            Assert.InRange((double)formerPoints / currentPoints, 9.0, 11.0);
        }

        [Fact]
        public void ScanPatternStartsAtTheClientCenterAndExpandsOutwards()
        {
            var points = ItemSellerService
                .EnumerateSellerScanPoints(ClientWidth, ClientHeight)
                .Take(5)
                .ToArray();

            Assert.Equal((ClientWidth / 2, ClientHeight / 2), points[0]);

            // First ring: top edge from -step to +step at y = center - step.
            Assert.Equal((ClientWidth / 2 - Step, ClientHeight / 2 - Step), points[1]);
            Assert.Equal((ClientWidth / 2, ClientHeight / 2 - Step), points[2]);
            Assert.Equal((ClientWidth / 2 + Step, ClientHeight / 2 - Step), points[3]);
        }

        [Fact]
        public void ScanPatternNeverLeavesTheCameraSafeClientArea()
        {
            var points = ItemSellerService.EnumerateSellerScanPoints(ClientWidth, ClientHeight).ToList();

            Assert.NotEmpty(points);
            Assert.All(points, point => Assert.True(
                ItemSellerService.IsSellerScanPointWithinSafeArea(
                    point.X, point.Y, ClientWidth, ClientHeight),
                $"Scan point ({point.X},{point.Y}) is outside the camera-safe client area."));
        }

        [Fact]
        public void ScanPatternVisitsEverySafeGridPointExactlyOnce()
        {
            int centerX = ClientWidth / 2;
            int centerY = ClientHeight / 2;

            var expected = new HashSet<(int X, int Y)>();
            for (int y = 0; y < ClientHeight; y++)
            {
                for (int x = 0; x < ClientWidth; x++)
                {
                    if ((x - centerX) % Step != 0 || (y - centerY) % Step != 0)
                        continue;

                    if (!ItemSellerService.IsSellerScanPointWithinSafeArea(x, y, ClientWidth, ClientHeight))
                        continue;

                    expected.Add((x, y));
                }
            }

            var actual = ItemSellerService.EnumerateSellerScanPoints(ClientWidth, ClientHeight).ToList();

            Assert.NotEmpty(expected);
            Assert.Equal(expected.Count, actual.Count);
            Assert.Equal(expected.Count, actual.Distinct().Count());
            Assert.All(actual, point => Assert.Contains(point, expected));
        }

        [Fact]
        public void ScanPatternIsDeterministic()
        {
            var first = ItemSellerService.EnumerateSellerScanPoints(ClientWidth, ClientHeight).ToList();
            var second = ItemSellerService.EnumerateSellerScanPoints(ClientWidth, ClientHeight).ToList();

            Assert.Equal(first, second);
        }

        [Fact]
        public void ScanPatternRequiresAValidClientSize()
        {
            Assert.Empty(ItemSellerService.EnumerateSellerScanPoints(0, ClientHeight));
            Assert.Empty(ItemSellerService.EnumerateSellerScanPoints(ClientWidth, 0));
            Assert.Empty(ItemSellerService.EnumerateSellerScanPoints(-10, -10));
        }
    }
}
