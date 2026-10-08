using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

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
        [StructLayout(LayoutKind.Sequential)]
        private struct ClientRect { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ClientPoint { public int X; public int Y; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint FindWindow(string? className, string windowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(nint hwnd, out ClientRect rect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(nint hwnd, out ClientRect rect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ClientToScreen(nint hwnd, ref ClientPoint point);

        /// <summary>Folder for town-teleport screenshots (Screenshots/TownPortals).</summary>
        public const string TownPortalsFolder = "TownPortals";

        /// <summary>Project-root folder for loot pixel-scan diagnostics (LootSSFolder).</summary>
        public const string LootScanFolder = "LootSSFolder";

        /// <summary>Folder for screenshots captured when another player's marker is detected.</summary>
        public const string PlayerPixelsFolder = "PlayerPixels";

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
        /// <param name="pixelLabel">Name of the detected pixel class in the caption ("white" / "pink").</param>
        public static void SaveLootScanFrame(
            Bitmap? frame,
            string label,
            string outcome,
            Action<string> log,
            int whiteCount = 0,
            Rectangle scanRegion = default,
            Rectangle excludeZone = default,
            IReadOnlyList<Point>? whiteHits = null,
            string pixelLabel = "white")
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

                    string caption = $"{label} | {outcome} | {pixelLabel} px: {whiteCount}";
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
        /// Captures the game window's client area for a manual pixel-detection test.
        /// The caller owns and must dispose the returned bitmap. Returns null on failure.
        /// </summary>
        public static Bitmap? CaptureGameClientFrame(Action<string>? log = null)
            => CaptureGameClientFrame(out _, log);

        /// <summary>
        /// Captures the game client and returns the reference client origin used to map
        /// the loot scanner's legacy screen coordinates into this frame.
        /// </summary>
        public static Bitmap? CaptureGameClientFrame(
            out Point referenceClientOrigin,
            Action<string>? log = null)
        {
            referenceClientOrigin = Point.Empty;
            nint hwnd = FindWindow(null, "Legend of Ares");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Ares");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Nostalgia");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Epic Of Ares Client");
            if (hwnd == nint.Zero)
            {
                log?.Invoke("[Detection Test] Could not find the game window.");
                return null;
            }

            if (!GetClientRect(hwnd, out ClientRect rect))
            {
                log?.Invoke("[Detection Test] Could not read the game client rectangle.");
                return null;
            }

            ClientPoint topLeft = new() { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref topLeft))
            {
                log?.Invoke("[Detection Test] Could not resolve the game client screen position.");
                return null;
            }

            if (GetWindowRect(hwnd, out ClientRect windowRect))
            {
                const int expectedWindowX = 447;
                const int expectedWindowY = 77;
                referenceClientOrigin = new Point(
                    topLeft.X - (windowRect.Left - expectedWindowX),
                    topLeft.Y - (windowRect.Top - expectedWindowY));
            }
            else
            {
                log?.Invoke("[Detection Test] Could not read the game window position; using the loot scanner's zero-origin fallback.");
            }

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0)
            {
                log?.Invoke("[Detection Test] Game client area is empty.");
                return null;
            }

            Bitmap? frame = null;
            try
            {
                frame = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (Graphics graphics = Graphics.FromImage(frame))
                    graphics.CopyFromScreen(topLeft.X, topLeft.Y, 0, 0, frame.Size);
                return frame;
            }
            catch (Exception ex)
            {
                frame?.Dispose();
                log?.Invoke($"[Detection Test] Game client capture failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Saves an annotated game-client frame when another player's faction marker is
        /// confirmed. The candidate, local-character exclusion, and ignored HUD regions
        /// are outlined so false positives can be diagnosed from the saved image.
        /// Never throws.
        /// </summary>
        internal static void SavePlayerMarkerScreenshot(
            Bitmap? frame,
            PlayerMarkerCandidate candidate,
            Rectangle characterExclusionArea,
            IReadOnlyList<Rectangle> hudExclusionAreas,
            Action<string> log)
        {
            if (frame == null)
                return;

            try
            {
                using Bitmap annotated = frame.Clone(
                    new Rectangle(0, 0, frame.Width, frame.Height), frame.PixelFormat);
                using (Graphics graphics = Graphics.FromImage(annotated))
                using (var candidatePen = new Pen(Color.Red, 3f))
                using (var characterPen = new Pen(Color.Cyan, 2f))
                using (var hudPen = new Pen(Color.Orange, 2f))
                using (var captionFont = new Font(FontFamily.GenericSansSerif, 10f, FontStyle.Bold))
                {
                    DrawRectangleOutline(graphics, candidatePen, candidate.Bounds);
                    DrawRectangleOutline(graphics, characterPen, characterExclusionArea);
                    foreach (Rectangle hudArea in hudExclusionAreas)
                        DrawRectangleOutline(graphics, hudPen, hudArea);

                    string caption = $"{candidate.Faction} | px={candidate.PixelCount} | " +
                                     $"fill={candidate.FillRatio:P0} | aspect={candidate.AspectRatio:F2}";
                    SizeF captionSize = graphics.MeasureString(caption, captionFont);
                    using var captionBackground = new SolidBrush(Color.FromArgb(210, 0, 0, 0));
                    using var captionBrush = new SolidBrush(Color.White);
                    graphics.FillRectangle(captionBackground, 4, 4, captionSize.Width + 12, captionSize.Height + 8);
                    graphics.DrawString(caption, captionFont, captionBrush, 10, 8);
                }

                string screenshotsDir = ResolveScreenshotsDir(PlayerPixelsFolder);
                Directory.CreateDirectory(screenshotsDir);
                string fileName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.png";
                string filePath = Path.Combine(screenshotsDir, fileName);
                annotated.Save(filePath, ImageFormat.Png);
                log($"[PlayerPixels] Annotated confirmed-marker screenshot saved: {filePath}");
            }
            catch (Exception ex)
            {
                log($"[PlayerPixels] Failed to save player-marker screenshot: {ex.Message}");
            }
        }

        private static void DrawRectangleOutline(Graphics graphics, Pen pen, Rectangle bounds)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;

            graphics.DrawRectangle(
                pen,
                bounds.X,
                bounds.Y,
                Math.Max(1, bounds.Width - 1),
                Math.Max(1, bounds.Height - 1));
        }

        /// <summary>
        /// Saves the manual player-marker test frame with the accepted pixel component
        /// and the loot-scanner character-exclusion area outlined. Returns null on failure.
        /// </summary>
        internal static string? SavePlayerMarkerDiagnosticScreenshot(
            Bitmap frame,
            PlayerMarkerCandidate candidate,
            Rectangle characterExclusionArea)
        {
            try
            {
                using Bitmap annotated = frame.Clone(
                    new Rectangle(0, 0, frame.Width, frame.Height), frame.PixelFormat);
                using (Graphics graphics = Graphics.FromImage(annotated))
                using (var candidatePen = new Pen(Color.Red, 3f))
                using (var exclusionPen = new Pen(Color.Cyan, 2f))
                using (var hudPen = new Pen(Color.Orange, 2f))
                {
                    graphics.DrawRectangle(
                        candidatePen,
                        candidate.Bounds.X,
                        candidate.Bounds.Y,
                        Math.Max(1, candidate.Bounds.Width - 1),
                        Math.Max(1, candidate.Bounds.Height - 1));
                    if (characterExclusionArea.Width > 0 && characterExclusionArea.Height > 0)
                    {
                        graphics.DrawRectangle(
                            exclusionPen,
                            characterExclusionArea.X,
                            characterExclusionArea.Y,
                            Math.Max(1, characterExclusionArea.Width - 1),
                            Math.Max(1, characterExclusionArea.Height - 1));
                    }
                    foreach (Rectangle hudArea in PlayerMarkerPixelDetector.GetHudExclusionAreas(
                                 frame.Width, frame.Height))
                    {
                        if (hudArea.Width <= 0 || hudArea.Height <= 0)
                            continue;
                        graphics.DrawRectangle(
                            hudPen,
                            hudArea.X,
                            hudArea.Y,
                            Math.Max(1, hudArea.Width - 1),
                            Math.Max(1, hudArea.Height - 1));
                    }
                }

                string folder = ResolveScreenshotsDir(PlayerPixelsFolder);
                Directory.CreateDirectory(folder);
                string filePath = Path.Combine(
                    folder,
                    $"Test-{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.png");
                annotated.Save(filePath, ImageFormat.Png);
                return filePath;
            }
            catch
            {
                return null;
            }
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
