using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;

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
            Action<string> log,
            string? decisionReason = null,
            MobFormationTrendResult? trend = null,
            string? decisionSummary = null)
        {
            try
            {
                using Bitmap annotated = (Bitmap)roiFrame.Clone();
                using (Graphics graphics = Graphics.FromImage(annotated))
                using (var mobPen = new Pen(Color.Lime, 2f))
                using (var centerBrush = new SolidBrush(Color.Yellow))
                using (var playerPen = new Pen(Color.DeepSkyBlue, 2f))
                using (var centroidPen = new Pen(Color.Gold, 2f))
                using (var clusterPen = new Pen(Color.OrangeRed, 2f))
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
                    }

                    // Best local cluster (the movement-suppression signal), drawn as the
                    // normalized compact neighborhood around the most compact pair.
                    if (analysis.DetectedMobCount >= 2 && analysis.EffectiveMarkerRadius > 0)
                    {
                        float clusterX = analysis.BestLocalClusterCenter.X - detection.RoiClient.Left;
                        float clusterY = analysis.BestLocalClusterCenter.Y - detection.RoiClient.Top;
                        float clusterRadius = Math.Max(4f,
                            BotConstants.MobGrouping.CompactClusterNeighborhoodFactor *
                            analysis.EffectiveMarkerRadius);
                        graphics.DrawEllipse(clusterPen, clusterX - clusterRadius,
                            clusterY - clusterRadius, clusterRadius * 2, clusterRadius * 2);
                    }

                    // Movement diagnostics show the analysis state only. There is deliberately
                    // no escape-direction arrow: gather movement follows the live route waypoint
                    // (resolved by MovementSystem), never RecommendedEscapeDirection.
                    string caption = $"{state} | mobs={analysis.DetectedMobCount} | " +
                                     $"localCluster={analysis.BestLocalClusterSize} | " +
                                     $"pair={analysis.BestPairDistanceNormalized:F2}r | " +
                                     $"quality={analysis.FormationQuality:F3} | " +
                                     $"compact={analysis.HasCompactLocalGroup} | " +
                                     $"reliable={analysis.IsMovementSampleReliable}";
                    string second = (trend.HasValue
                                        ? $"trend={trend.Value.Trend} ({trend.Value.RelativeChange:P0}) | "
                                        : "") +
                                    $"wholeClustered={analysis.IsSufficientlyClustered} | " +
                                    $"attackReadyCluster={analysis.HasAttackReadyCluster} | " +
                                    $"reason={decisionReason ?? "-"}";

                    // The decision summary can be longer than the ROI frame, so every caption line
                    // is word-wrapped to the image width instead of being clipped at the edge.
                    float available = Math.Max(40f, annotated.Width - 20f);
                    var lines = new List<(string Text, SizeF Size)>();
                    foreach (string? line in new[] { caption, second, decisionSummary })
                    {
                        if (string.IsNullOrEmpty(line))
                            continue;
                        foreach (string wrapped in WrapToWidth(graphics, font, line, available))
                            lines.Add((wrapped, graphics.MeasureString(wrapped, font)));
                    }

                    float width = 0f;
                    float height = 6f;
                    foreach ((string _, SizeF size) in lines)
                    {
                        width = Math.Max(width, size.Width);
                        height += size.Height;
                    }

                    graphics.FillRectangle(textBack, 5, 5, width + 10, height);
                    float lineY = 8f;
                    foreach ((string text, SizeF size) in lines)
                    {
                        graphics.DrawString(text, font, textBrush, 10, lineY);
                        lineY += size.Height;
                    }
                }

                string directory = ResolveDebugDirectory();
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}_mob-grouping.png");
                annotated.Save(path, ImageFormat.Png);
                log($"[MobGrouping] one-shot debug frame saved: {path}");

                // A fight produces several snapshots per second; keep the folder bounded
                // (only files written by this renderer are ever removed).
                PruneOldSnapshots(directory);
                return true;
            }
            catch (Exception ex)
            {
                log($"[MobGrouping] debug snapshot failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Maximum number of diagnostic frames kept on disk.</summary>
        private const int MaxRetainedSnapshots = 60;

        /// <summary>Deletes the oldest snapshots above <see cref="MaxRetainedSnapshots"/>.</summary>
        private static void PruneOldSnapshots(string directory)
        {
            try
            {
                var files = new DirectoryInfo(directory)
                    .GetFiles("*_mob-grouping.png", SearchOption.TopDirectoryOnly);
                if (files.Length <= MaxRetainedSnapshots)
                    return;

                foreach (var file in files
                    .OrderByDescending(file => file.Name, StringComparer.Ordinal)
                    .Skip(MaxRetainedSnapshots))
                {
                    try { file.Delete(); }
                    catch { /* retention must never break a diagnostic write */ }
                }
            }
            catch
            {
                // Diagnostics only: never surface retention failures.
            }
        }

        /// <summary>Greedy word wrap to the available caption width (never clips a decision).</summary>
        private static IEnumerable<string> WrapToWidth(
            Graphics graphics, Font font, string text, float maxWidth)
        {
            string[] words = text.Split(' ');
            var current = new StringBuilder();
            foreach (string word in words)
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && graphics.MeasureString(candidate, font).Width > maxWidth)
                {
                    yield return current.ToString();
                    current.Clear().Append(word);
                }
                else
                {
                    current.Clear().Append(candidate);
                }
            }

            if (current.Length > 0)
                yield return current.ToString();
        }

        private static string ResolveDebugDirectory()        {
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
