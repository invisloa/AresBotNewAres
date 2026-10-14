using System;
using System.Drawing;
using System.IO;
using System.Linq;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class GameDisconnectDialogDetectorTests
    {
        [Fact]
        public void DetectsTheCapturedDisconnectPromptAtItsClientAreaLocation()
        {
            using Bitmap frame = CreateFrame(withPrompt: true, offsetX: 2, offsetY: 1);
            var detector = new GameDisconnectDialogDetector();

            Assert.True(detector.IsAvailable);
            Assert.True(detector.IsDisconnected(frame));
        }

        [Fact]
        public void DoesNotDetectAnOrdinaryClientFrame()
        {
            using Bitmap frame = CreateFrame(withPrompt: false);
            var detector = new GameDisconnectDialogDetector();

            Assert.False(detector.IsDisconnected(frame));
        }

        private static Bitmap CreateFrame(bool withPrompt, int offsetX = 0, int offsetY = 0)
        {
            const int clientWidth = 1024;
            const int clientHeight = 768;
            var frame = new Bitmap(clientWidth, clientHeight);
            using (Graphics graphics = Graphics.FromImage(frame))
                graphics.Clear(Color.Black);

            if (!withPrompt)
                return frame;

            string resourceName = typeof(GameDisconnectDialogDetector).Assembly
                .GetManifestResourceNames()
                .Single(name => name.EndsWith("DisconnectedDialogTextMask.png", StringComparison.OrdinalIgnoreCase));
            using Stream stream = typeof(GameDisconnectDialogDetector).Assembly
                .GetManifestResourceStream(resourceName)!;
            using var mask = new Bitmap(stream);

            for (int y = 0; y < mask.Height; y++)
            {
                for (int x = 0; x < mask.Width; x++)
                {
                    if (mask.GetPixel(x, y).R > 120)
                        frame.SetPixel(120 + offsetX + x, 362 + offsetY + y, Color.White);
                }
            }

            return frame;
        }
    }
}
