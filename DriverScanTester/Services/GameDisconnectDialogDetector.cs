using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Recognizes the game's modal "The connection to server has been disconnected."
    /// message using a small text-pixel template captured from the game client. The
    /// search is restricted to the message's known client-area location, so unrelated
    /// scene text does not trigger the fail-safe.
    /// </summary>
    public sealed class GameDisconnectDialogDetector
    {
        private const int ReferenceClientWidth = 1024;
        private const int ReferenceClientHeight = 768;
        private const int ReferenceTextLeft = 120;
        private const int ReferenceTextTop = 362;
        private const int SearchRadiusX = 4;
        private const int SearchRadiusY = 2;
        private const byte BrightPixelThreshold = 120;
        private const double RequiredMatchRatio = 0.90;
        private const double MaximumUnexpectedPixelRatio = 0.08;

        private static readonly PromptTemplate? Template = LoadTemplate();

        public bool IsAvailable => Template?.ForegroundPixels.Length > 0;

        public bool IsDisconnected(Bitmap? clientFrame)
        {
            PromptTemplate? template = Template;
            if (clientFrame == null || template == null || template.ForegroundPixels.Length == 0)
                return false;

            double scaleX = clientFrame.Width / (double)ReferenceClientWidth;
            double scaleY = clientFrame.Height / (double)ReferenceClientHeight;
            int expectedLeft = (int)Math.Round(ReferenceTextLeft * scaleX);
            int expectedTop = (int)Math.Round(ReferenceTextTop * scaleY);
            int scaledWidth = Math.Max(1, (int)Math.Round(template.Width * scaleX));
            int scaledHeight = Math.Max(1, (int)Math.Round(template.Height * scaleY));
            int requiredMatches = (int)Math.Ceiling(template.ForegroundPixels.Length * RequiredMatchRatio);
            int maximumUnexpectedPixels = Math.Max(
                24,
                (int)Math.Ceiling(template.ForegroundPixels.Length * MaximumUnexpectedPixelRatio));

            int radiusX = Math.Max(1, (int)Math.Round(SearchRadiusX * scaleX));
            int radiusY = Math.Max(1, (int)Math.Round(SearchRadiusY * scaleY));
            for (int offsetY = -radiusY; offsetY <= radiusY; offsetY++)
            {
                for (int offsetX = -radiusX; offsetX <= radiusX; offsetX++)
                {
                    int left = expectedLeft + offsetX;
                    int top = expectedTop + offsetY;
                    if (left < 0 || top < 0 || left + scaledWidth > clientFrame.Width ||
                        top + scaledHeight > clientFrame.Height)
                        continue;

                    int matches = 0;
                    foreach (Point pixel in template.ForegroundPixels)
                    {
                        int x = left + (int)Math.Round(pixel.X * scaleX);
                        int y = top + (int)Math.Round(pixel.Y * scaleY);
                        if (IsBright(clientFrame.GetPixel(x, y)))
                            matches++;
                    }

                    if (matches < requiredMatches)
                        continue;

                    int unexpectedPixels = 0;
                    for (int y = top; y < top + scaledHeight; y++)
                    {
                        for (int x = left; x < left + scaledWidth; x++)
                        {
                            if (!IsBright(clientFrame.GetPixel(x, y)))
                                continue;

                            int templateX = Math.Clamp((int)Math.Round((x - left) / scaleX), 0, template.Width - 1);
                            int templateY = Math.Clamp((int)Math.Round((y - top) / scaleY), 0, template.Height - 1);
                            if (!template.ForegroundMask[templateY, templateX] && ++unexpectedPixels > maximumUnexpectedPixels)
                                break;
                        }
                        if (unexpectedPixels > maximumUnexpectedPixels)
                            break;
                    }

                    if (unexpectedPixels <= maximumUnexpectedPixels)
                        return true;
                }
            }

            return false;
        }

        private static bool IsBright(Color color)
            => Math.Max(color.R, Math.Max(color.G, color.B)) >= BrightPixelThreshold;

        private static PromptTemplate? LoadTemplate()
        {
            try
            {
                Assembly assembly = typeof(GameDisconnectDialogDetector).Assembly;
                string? resourceName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(name => name.EndsWith(
                        "DisconnectedDialogTextMask.png",
                        StringComparison.OrdinalIgnoreCase));
                if (resourceName == null)
                    return null;

                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                    return null;

                using var bitmap = new Bitmap(stream);
                var foreground = new List<Point>();
                var mask = new bool[bitmap.Height, bitmap.Width];
                for (int y = 0; y < bitmap.Height; y++)
                {
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        if (!IsBright(bitmap.GetPixel(x, y)))
                            continue;

                        mask[y, x] = true;
                        foreground.Add(new Point(x, y));
                    }
                }

                return new PromptTemplate(bitmap.Width, bitmap.Height, mask, foreground.ToArray());
            }
            catch
            {
                // If the optional visual template cannot be loaded, the memory/movement
                // watchdogs remain active and continue to fail safe.
                return null;
            }
        }

        private sealed record PromptTemplate(
            int Width,
            int Height,
            bool[,] ForegroundMask,
            Point[] ForegroundPixels);
    }
}
