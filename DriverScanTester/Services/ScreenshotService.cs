using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Shared screenshot helper. Captures the full virtual screen (all monitors) and
    /// stores it under Screenshots/&lt;subfolder&gt; outside the build output, so a rebuild
    /// (which wipes bin/Debug) never deletes the images:
    ///   • dev run (bin/Debug/...)  → &lt;project&gt;\Screenshots\&lt;subfolder&gt;
    ///   • published/single-file run → &lt;exe&gt;\Screenshots\&lt;subfolder&gt;
    /// Never throws — a failed capture must not break the bot.
    /// </summary>
    public static class ScreenshotService
    {
        /// <summary>Folder for town-teleport screenshots (Screenshots/TownPortals).</summary>
        public const string TownPortalsFolder = "TownPortals";

        /// <summary>Project-root folder for loot pixel-scan diagnostics (LootSSFolder).</summary>
        public const string LootScanFolder = "LootSSFolder";

        /// <summary>
        /// Master switch for loot-scan diagnostics. When false,
        /// <see cref="CloneLootScanFrame"/> returns null and nothing is written to
        /// LootSSFolder (no per-scan clone, no annotation, no PNG). Default: false —
        /// diagnostics off; flip to true to debug loot detection again.
        /// </summary>
        public static bool LootScanCaptureEnabled { get; set; } = false;

        /// <summary>
        /// Clones the current loot-scan frame. The loot scanner reuses one bitmap for
        /// every capture, so a clone must be taken at capture time to preserve the
        /// exact pixels that were analyzed (the next capture — e.g. the post-click
        /// refresh — overwrites the same buffer). Returns null when diagnostics are
        /// disabled or the clone fails. Never throws.
        /// </summary>
        public static Bitmap? CloneLootScanFrame(Bitmap? source)
        {
            if (!LootScanCaptureEnabled || source == null)
                return null;

            try
            {
                return source.Clone(new Rectangle(0, 0, source.Width, source.Height), source.PixelFormat);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Annotates and saves one loot-scan frame as a PNG in LootSSFolder
        /// (dev: project root, next to Screenshots; published: next to the exe).
        /// The annotation draws the scanned region (green), the character exclude zone
        /// (red), every white pixel candidate (yellow) and a caption with the outcome,
        /// so loot detection problems can be diagnosed from what the bot actually saw.
        /// The frame is disposed afterwards. Never throws — diagnostics must not break
        /// the bot.
        /// </summary>
        /// <param name="label">Scan label, e.g. "SmallScan", "BigScan", "AfterClick".</param>
        /// <param name="outcome">Scan result, e.g. "collected", "no-item".</param>
        public static void SaveLootScanFrame(
            Bitmap? frame,
            string label,
            string outcome,
            Action<string> log,
            int whiteCount = 0,
            Rectangle scanRegion = default,
            Rectangle excludeZone = default,
            IReadOnlyList<Point>? whiteHits = null)
        {
            if (frame == null)
                return;

            try
            {
                try
                {
                    using Graphics graphics = Graphics.FromImage(frame);

                    if (scanRegion.Width > 0 && scanRegion.Height > 0)
                    {
                        using var scanPen = new Pen(Color.FromArgb(255, 0, 220, 0), 2);
                        graphics.DrawRectangle(scanPen, scanRegion);
                    }

                    if (excludeZone.Width > 0 && excludeZone.Height > 0)
                    {
                        using var excludePen = new Pen(Color.FromArgb(255, 230, 0, 0), 2);
                        graphics.DrawRectangle(excludePen, excludeZone);
                    }

                    if (whiteHits != null)
                    {
                        using var hitBrush = new SolidBrush(Color.FromArgb(255, 255, 230, 0));
                        foreach (Point hit in whiteHits)
                            graphics.FillRectangle(hitBrush, hit.X - 1, hit.Y - 1, 3, 3);
                    }

                    string caption = $"{label} | {outcome} | white px: {whiteCount}";
                    using var font = new Font(FontFamily.GenericSansSerif, 16f, FontStyle.Bold);
                    SizeF captionSize = graphics.MeasureString(caption, font);
                    using var captionBg = new SolidBrush(Color.FromArgb(190, 0, 0, 0));
                    graphics.FillRectangle(captionBg, 4, 4, captionSize.Width + 12, captionSize.Height + 8);
                    using var captionBrush = new SolidBrush(Color.White);
                    graphics.DrawString(caption, font, captionBrush, 10, 8);
                }
                catch
                {
                    // Annotation is best-effort — still save the raw frame below.
                }

                string folder = ResolveLootScanDir();
                Directory.CreateDirectory(folder);

                string fileName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}_{label}_{outcome}.png";
                string filePath = Path.Combine(folder, fileName);

                frame.Save(filePath, ImageFormat.Png);
                log($"[LootSS] {label} {outcome} (white={whiteCount}) saved: {filePath}");
            }
            catch (Exception ex)
            {
                log($"[LootSS] Failed to save loot scan screenshot: {ex.Message}");
            }
            finally
            {
                frame.Dispose();
            }
        }

        /// <summary>
        /// Dev run → project-root LootSSFolder (next to Screenshots); published run →
        /// LootSSFolder next to the exe. Same layout convention as the Logs folder.
        /// </summary>
        private static string ResolveLootScanDir()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                string devDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", LootScanFolder));
                string projectDir = Path.GetDirectoryName(devDir) ?? "";
                if (Directory.Exists(Path.Combine(projectDir, "SavedPaths")) ||
                    File.Exists(Path.Combine(projectDir, "DriverScanTester.csproj")))
                    return devDir;
            }
            catch
            {
                // Fall through to exe-local LootSSFolder.
            }
            return Path.Combine(baseDir, LootScanFolder);
        }

        /// <summary>
        /// Captures the full virtual screen into Screenshots/&lt;subfolder&gt;/&lt;timestamp&gt;.png.
        /// The filename includes milliseconds so two captures in the same second
        /// (e.g. quick teleport retries) never overwrite each other.
        /// </summary>
        /// <param name="subfolder">Folder inside Screenshots, e.g. <see cref="TownPortalsFolder"/>.</param>
        /// <param name="log">Log sink shown in the bot UI.</param>
        /// <param name="logPrefix">Prefix for the log lines, e.g. "[Teleport]".</param>
        public static void CaptureFullScreen(string subfolder, Action<string> log, string logPrefix)
        {
            try
            {
                var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;

                using (Bitmap bitmap = new Bitmap(virtualScreen.Width, virtualScreen.Height))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(virtualScreen.X, virtualScreen.Y, 0, 0, bitmap.Size);

                    string screenshotsDir = ResolveScreenshotsDir(subfolder);
                    Directory.CreateDirectory(screenshotsDir);

                    string fileName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.png";
                    string filePath = Path.Combine(screenshotsDir, fileName);

                    bitmap.Save(filePath, ImageFormat.Png);
                    log($"{logPrefix} Screenshot saved: {filePath}");
                }
            }
            catch (Exception ex)
            {
                log($"{logPrefix} Failed to capture screenshot: {ex.Message}");
            }
        }

        /// <summary>
        /// Dev run → project-root Screenshots (next to SavedPaths); published run →
        /// Screenshots next to the exe. Same convention as BotFileLogger's Logs folder.
        /// </summary>
        private static string ResolveScreenshotsDir(string subfolder)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                string devDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "Screenshots", subfolder));
                string projectDir = Path.GetDirectoryName(Path.GetDirectoryName(devDir) ?? "") ?? "";
                if (Directory.Exists(Path.Combine(projectDir, "SavedPaths")) ||
                    File.Exists(Path.Combine(projectDir, "DriverScanTester.csproj")))
                    return devDir;
            }
            catch
            {
                // Fall through to exe-local Screenshots.
            }
            return Path.Combine(baseDir, "Screenshots", subfolder);
        }
    }
}
