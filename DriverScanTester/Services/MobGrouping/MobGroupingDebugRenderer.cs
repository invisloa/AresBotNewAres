using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace DriverScanTester.Services
{
    /// <summary>One-shot annotated ROI writer; it does not participate in detection or movement.</summary>
    internal static class MobGroupingDebugRenderer
    {
        internal static bool Save(
            Bitmap roiFrame,
            MobMarkerDetectionResult detection,
            MobGroupAnalysis analysis,
            string state,
            Action<string> log)
        {
            try
            {
                using Bitmap annotated = (Bitmap)roiFrame.Clone();
                using (Graphics graphics = Graphics.FromImage(annotated))
                using (var mobPen = new Pen(Color.Lime, 2f))
                using (var centerBrush = new SolidBrush(Color.Yellow))
                using (var playerPen = new Pen(Color.DeepSkyBlue, 2f))
                using (var centroidPen = new Pen(Color.Gold, 2f))
                using (var vectorPen = new Pen(Color.Cyan, 2f))
                using (var font = new Font(FontFamily.GenericSansSerif, 11f, FontStyle.Bold))
                using (var textBrush = new SolidBrush(Color.White))
                using (var textBack = new SolidBrush(Color.FromArgb(190, 0, 0, 0)))
                {
                    foreach (MobMarker marker in detection.Markers)
                    {
                        float x = marker.Center.X - detection.RoiClient.Left;
                        float y = marker.Center.Y - detection.RoiClient.Top;
                        float radius = Math.Max(4f, marker.Radius);
                        graphics.DrawEllipse(mobPen, x - radius, y - radius, radius * 2, radius * 2);
                        graphics.FillEllipse(centerBrush, x - 2, y - 2, 4, 4);
                    }

                    float anchorX = detection.PlayerAnchor.X - detection.RoiClient.Left;
                    float anchorY = detection.PlayerAnchor.Y - detection.RoiClient.Top;
                    graphics.DrawEllipse(playerPen, anchorX - 7, anchorY - 7, 14, 14);
                    if (analysis.DetectedMobCount > 0)
                    {
                        float centroidX = analysis.Centroid.X - detection.RoiClient.Left;
                        float centroidY = analysis.Centroid.Y - detection.RoiClient.Top;
                        graphics.DrawEllipse(centroidPen, centroidX - 5, centroidY - 5, 10, 10);
                        var escape = MobGroupingDirectionMath.ToScreenVector(analysis.RecommendedEscapeDirection);
                        graphics.DrawLine(vectorPen, anchorX, anchorY,
                            anchorX + escape.X * detection.ClientHeight * 0.15f,
                            anchorY + escape.Y * detection.ClientHeight * 0.15f);
                    }

                    string caption = $"{state} | mobs={analysis.DetectedMobCount} | avg={analysis.AverageSpread:F1}px | " +
                                     $"max={analysis.MaximumSpread:F1}px | escape={analysis.RecommendedEscapeDirection}";
                    SizeF size = graphics.MeasureString(caption, font);
                    graphics.FillRectangle(textBack, 5, 5, size.Width + 10, size.Height + 6);
                    graphics.DrawString(caption, font, textBrush, 10, 8);
                }

                string directory = ResolveDebugDirectory();
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}_mob-grouping.png");
                annotated.Save(path, ImageFormat.Png);
                log($"[MobGrouping] one-shot debug frame saved: {path}");
                return true;
            }
            catch (Exception ex)
            {
                log($"[MobGrouping] debug snapshot failed: {ex.Message}");
                return false;
            }
        }

        private static string ResolveDebugDirectory()
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string relative = "";
            for (int depth = 1; depth <= 6; depth++)
            {
                relative = Path.Combine(relative, "..");
                string candidate = Path.GetFullPath(Path.Combine(baseDirectory, relative, "MobGroupingDebug"));
                string parent = Path.GetDirectoryName(candidate) ?? "";
                if (Directory.Exists(Path.Combine(parent, "SavedPaths")) ||
                    File.Exists(Path.Combine(parent, "DriverScanTester.csproj")))
                    return candidate;
            }
            return Path.Combine(baseDirectory, "MobGroupingDebug");
        }
    }
}
