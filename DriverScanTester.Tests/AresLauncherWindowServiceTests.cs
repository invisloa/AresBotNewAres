using System.Drawing;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class AresLauncherWindowServiceTests
    {
        [Fact]
        public void CalculatesPlayCenterFromReferenceClientBounds()
        {
            var clientBounds = new Rectangle(352, 167, 1216, 699);

            bool calculated = AresLauncherWindowService.TryCalculatePlayButtonPosition(
                clientBounds,
                out Point screenPosition,
                out string reason);

            Assert.True(calculated, reason);
            Assert.Equal(new Point(1433, 785), screenPosition);
        }

        [Theory]
        [InlineData(305, 138, 1386, 756)]
        [InlineData(-1920, 100, -839, 718)]
        public void CalculatesPlayCenterAfterWindowMoves(
            int clientX,
            int clientY,
            int expectedX,
            int expectedY)
        {
            var clientBounds = new Rectangle(clientX, clientY, 1216, 699);

            bool calculated = AresLauncherWindowService.TryCalculatePlayButtonPosition(
                clientBounds,
                out Point screenPosition,
                out string reason);

            Assert.True(calculated, reason);
            Assert.Equal(new Point(expectedX, expectedY), screenPosition);
        }

        [Fact]
        public void ScalesClientOffsetsForDpiScaledWindow()
        {
            var clientBounds = new Rectangle(-1000, 50, 2432, 1398);

            bool calculated = AresLauncherWindowService.TryCalculatePlayButtonPosition(
                clientBounds,
                out Point screenPosition,
                out string reason);

            Assert.True(calculated, reason);
            Assert.Equal(new Point(1162, 1286), screenPosition);
            Assert.True(clientBounds.Contains(screenPosition));
        }

        [Fact]
        public void RejectsEmptyClientBounds()
        {
            bool calculated = AresLauncherWindowService.TryCalculatePlayButtonPosition(
                Rectangle.Empty,
                out Point screenPosition,
                out string reason);

            Assert.False(calculated);
            Assert.Equal(Point.Empty, screenPosition);
            Assert.Contains("empty", reason);
        }

        [Fact]
        public void RejectsUnmeasuredClientAspectRatio()
        {
            bool calculated = AresLauncherWindowService.TryCalculatePlayButtonPosition(
                new Rectangle(0, 0, 1024, 768),
                out _,
                out string reason);

            Assert.False(calculated);
            Assert.Contains("16:9", reason);
        }
    }
}
