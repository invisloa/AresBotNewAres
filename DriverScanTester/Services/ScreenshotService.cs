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

        /// <summary>
        /// Project folder for automatic and manual loot diagnostics. Unlike LootSSFolder
        /// this folder is explicitly un-ignored in .gitignore so captured frames are
        /// visible in the IDE and Git Changes.
        /// </summary>
        public const string DiagnosticLootScreenshotsFolder = "LootTest";

        /// <summary>Folder for screenshots captured when another player's marker is detected.</summary>
        public const string PlayerPixelsFolder = "PlayerPixels";

        /// <summary>Folder for screenshots captured by the game-connection watchdog.</summary>
        public const string ConnectionStatesFolder = "ConnectionStates";

        /// <summary>
        /// Master switch for loot-scan diagnostics. Enabled by default so every loot
        /// frame is saved with its validated candidate pixels annotated.
        /// </summary>
        public static bool LootScanCaptureEnabled { get; set; } = true;

        /// <summary>
        /// Loot screenshots go to the project's visible Screenshots/LootTest folder
        /// by default; setting this false keeps the legacy ignored LootSSFolder path.
        /// </summary>
        public static bool LootScanCaptureToProjectScreenshots { get; set; } = true;

        /// <summary>
        /// Clones the current loot-scan frame. The loot scanner reuses one bitmap for
        /// every capture, so a clone must be taken at capture time to preserve the
        /// exact pixels that were analyzed (the next capture — e.g. the post-click
        /// refresh — overwrites the same buffer). Every enabled loot capture is retained;
        /// cloning is not capped. Returns null when diagnostics are disabled or cloning
        /// fails. Never throws.
        /// </summary>
        public static Bitmap? CloneLootScanFrame(Bitmap? source)
            => CloneLootScanFrameCore(source);

        /// <summary>Clones a pink Phase-A frame; pink scans have the same uncapped retention.</summary>
        public static Bitmap? ClonePinkLootScanFrame(Bitmap? source)
            => CloneLootScanFrameCore(source);

        private static Bitmap? CloneLootScanFrameCore(Bitmap? source)
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
        /// Builds the annotated-frame caption. Detection counts and mouseover-probe
        /// counts come from separate arguments, so a large detected mask can never be
        /// presented as the handful of positions that were actually probed.
        /// </summary>
        internal static string BuildLootScanCaption(
            string label,
            string outcome,
            int lootCount,
            int candidateComponents,
            int candidatePixelCount,
            int probeCount,
            Point? confirmedProbe,
            string pixelLabel,
            int aquamarineCount = 0)
        {
            string detectedDetail = pixelLabel == "normal"
                ? $" ({pixelLabel}: aquamarine={aquamarineCount})"
                : $" ({pixelLabel})";
            return $"{label} | {outcome} | detected={lootCount}px{detectedDetail} | " +
                   $"candidates={candidateComponents} comps/{candidatePixelCount}px | probes={probeCount} | " +
                   $"confirmed={(confirmedProbe.HasValue ? $"({confirmedProbe.Value.X},{confirmedProbe.Value.Y})" : "none")}";
        }

        /// <summary>
        /// Saves one loot-scan frame unmodified as a PNG in the configured loot-diagnostics
        /// folder, so the exact frame consumed by the pixel scan can be inspected without
        /// annotations. The frame is disposed afterwards. Never throws — diagnostics must
        /// not break the bot.
        /// </summary>
        /// <param name="label">Scan label, e.g. "SmallScan", "BigScan", "AfterClick".</param>
        /// <param name="metadata">Diagnostic metadata appended to the log line.</param>
        public static void SaveLootScanRawFrame(
            Bitmap? frame,
            string label,
            string metadata,
            Action<string> log)
        {
            if (frame == null)
                return;

            try
            {
                if (!LootScanCaptureEnabled)
                    return;

                string folder = ResolveLootScanDir();
                Directory.CreateDirectory(folder);
                string fileName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}_{label}_raw.png";
                string filePath = Path.Combine(folder, fileName);
                // Save the detached clone directly. Do not annotate it: this is the
                // exact unmodified bitmap consumed by Phase A.
                frame.Save(filePath, ImageFormat.Png);
                log($"[LootSS] Raw {label} frame saved: {filePath}; {metadata}");
            }
            catch (Exception ex)
            {
                log($"[LootSS] Failed to save raw loot frame: {ex.Message}; {metadata}");
            }
            finally
            {
                frame.Dispose();
            }
        }

        /// <summary>
        /// Annotates and saves one loot-scan frame. The annotation keeps the separate
        /// datasets visually distinct:
        ///    green rectangle  — scanned region;
        ///    yellow 1px dots  — pixels classified by the active loot color masks;
        ///    orange 1px dots  — pixels of accepted loot candidate components;
        ///    magenta outline  — accepted candidate component bounds;
        ///    cyan 3px dots    — actual mouseover probe positions;
        ///    bright-green mark— the probe whose mouseover confirmed the item.
        /// When <paramref name="saveRawFrame"/> is set the untouched clone is written
        /// first, so the original frame can be inspected without annotations.
        /// Annotation is best-effort and never modifies the bitmap used for detection.
        /// </summary>
        public static void SaveLootScanFrame(
            Bitmap? frame,
            string label,
            string outcome,
            Action<string> log,
            int lootCount = 0,
            Rectangle scanRegion = default,
            IReadOnlyList<Point>? probeHits = null,
            string pixelLabel = "normal",
            IReadOnlyList<Point>? detectedPixels = null,
            IReadOnlyList<Point>? candidatePixels = null,
            IReadOnlyList<Rectangle>? candidateBounds = null,
            Point? confirmedProbe = null,
            int aquamarineCount = 0,
            int candidatePixelCount = 0,
            bool saveRawFrame = false,
            string rawMetadata = "")
        {
            if (frame == null)
                return;

            try
            {
                string folder = ResolveLootScanDir();
                Directory.CreateDirectory(folder);
                string timestamp = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}";

                if (saveRawFrame && LootScanCaptureEnabled)
                {
                    string rawPath = Path.Combine(folder, $"{timestamp}_{label}_raw.png");
                    frame.Save(rawPath, ImageFormat.Png);
                    log($"[LootSS] Raw {label} frame saved: {rawPath}; {rawMetadata}");
                }

                try
                {
                    using Graphics graphics = Graphics.FromImage(frame);

                    // Detection mask first, so candidate/cursor markers stay readable.
                    if (detectedPixels != null && detectedPixels.Count > 0)
                    {
                        using var maskBrush = new SolidBrush(Color.FromArgb(255, 255, 230, 0));
                        foreach (Point pixel in detectedPixels)
                            graphics.FillRectangle(maskBrush, pixel.X, pixel.Y, 1, 1);
                    }

                    if (candidatePixels != null && candidatePixels.Count > 0)
                    {
                        using var candidateBrush = new SolidBrush(Color.FromArgb(255, 255, 130, 0));
                        foreach (Point pixel in candidatePixels)
                            graphics.FillRectangle(candidateBrush, pixel.X, pixel.Y, 1, 1);
                    }

                    if (candidateBounds != null)
                    {
                        using var candidatePen = new Pen(Color.FromArgb(255, 220, 0, 220), 1);
                        foreach (Rectangle bounds in candidateBounds)
                            graphics.DrawRectangle(candidatePen, bounds);
                    }

                    if (scanRegion.Width > 0 && scanRegion.Height > 0)
                    {
                        using var scanPen = new Pen(Color.FromArgb(255, 0, 220, 0), 2);
                        graphics.DrawRectangle(scanPen, scanRegion);
                    }

                    if (probeHits != null)
                    {
                        using var hitBrush = new SolidBrush(Color.FromArgb(255, 0, 220, 255));
                        foreach (Point hit in probeHits)
                            graphics.FillRectangle(hitBrush, hit.X - 1, hit.Y - 1, 3, 3);
                    }

                    if (confirmedProbe.HasValue)
                    {
                        Point confirmed = confirmedProbe.Value;
                        using var confirmedPen = new Pen(Color.FromArgb(255, 0, 255, 80), 2);
                        graphics.DrawRectangle(confirmedPen, confirmed.X - 5, confirmed.Y - 5, 11, 11);
                        graphics.DrawLine(confirmedPen, confirmed.X - 7, confirmed.Y, confirmed.X + 7, confirmed.Y);
                        graphics.DrawLine(confirmedPen, confirmed.X, confirmed.Y - 7, confirmed.X, confirmed.Y + 7);
                    }

                    int probeCount = probeHits?.Count ?? 0;
                    int candidateCount = candidateBounds?.Count ?? 0;
                    string caption = BuildLootScanCaption(
                        label, outcome, lootCount,
                        candidateCount, candidatePixelCount, probeCount, confirmedProbe, pixelLabel,
                        aquamarineCount);
                    using var font = new Font(FontFamily.GenericSansSerif, 16f, FontStyle.Bold);
                    SizeF captionSize = graphics.MeasureString(caption, font);
                    using var captionBg = new SolidBrush(Color.FromArgb(190, 0, 0, 0));
                    graphics.FillRectangle(captionBg, 4, 4, captionSize.Width + 12, captionSize.Height + 8);
                    using var captionBrush = new SolidBrush(Color.White);
                    graphics.DrawString(caption, font, captionBrush, 10, 8);
                }
                catch
                {
                    // Annotation is best-effort — still save the frame below.
                }

                string fileName = $"{timestamp}_{label}_{outcome}.png";
                string filePath = Path.Combine(folder, fileName);

                frame.Save(filePath, ImageFormat.Png);
                log($"[LootSS] {label} {outcome} ({pixelLabel}={lootCount}, candidates={candidatePixelCount}px, probes={probeHits?.Count ?? 0}) saved: {filePath}");
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
        /// LootSSFolder next to the exe. The project folder is located by walking up from
        /// the EXE/current directory and looking for DriverScanTester.csproj, so launching
        /// from bin/, BuildTestOutput/ or any other nested build folder still writes the
        /// manual-loot diagnostic screenshots into the real project folder.
        /// </summary>
        private static string ResolveLootScanDir()
        {
            if (LootScanCaptureToProjectScreenshots)
                return GetDiagnosticLootScanOutputDirectory();

            string? projectDir = TryFindProjectDirectory(AppDomain.CurrentDomain.BaseDirectory)
                ?? TryFindProjectDirectory(Directory.GetCurrentDirectory());
            if (projectDir != null)
                return Path.Combine(projectDir, LootScanFolder);

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                string devDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", LootScanFolder));
                string projectCandidate = Path.GetDirectoryName(devDir) ?? "";
                if (Directory.Exists(Path.Combine(projectCandidate, "SavedPaths")) ||
                    File.Exists(Path.Combine(projectCandidate, "DriverScanTester.csproj")))
                    return devDir;
            }
            catch
            {
                // Fall through to exe-local LootSSFolder.
            }
            return Path.Combine(baseDir, LootScanFolder);
        }

        /// <summary>
        /// Returns the folder where manual diagnostic loot screenshots are written.
        /// Exposed so the Tools tab can log the exact path before/after a test run.
        /// </summary>
        internal static string GetLootScanOutputDirectory() => ResolveLootScanDir();

        /// <summary>
        /// Visible project folder for automatic and manual loot screenshots:
        /// Screenshots/LootTest. This path is un-ignored so generated PNGs are visible in Git Changes.
        /// </summary>
        internal static string GetDiagnosticLootScanOutputDirectory()
        {
            string? projectDir = TryFindProjectDirectory(AppDomain.CurrentDomain.BaseDirectory)
                ?? TryFindProjectDirectory(Directory.GetCurrentDirectory());
            if (projectDir != null)
                return Path.Combine(projectDir, "Screenshots", DiagnosticLootScreenshotsFolder);

            return ResolveScreenshotsDir(DiagnosticLootScreenshotsFolder);
        }

        /// <summary>
        /// Walks up from <paramref name="startDirectory"/> looking for the project file.
        /// Supports both the project-folder layout (.../DriverScanTester/DriverScanTester.csproj)
        /// and the repository-root layout (.../repo/DriverScanTester/DriverScanTester.csproj).
        /// Returns null when no source project can be found (published build).
        /// </summary>
        internal static string? TryFindProjectDirectory(string startDirectory)
        {
            try
            {
                DirectoryInfo? directory = new DirectoryInfo(startDirectory);
                for (int depth = 0; depth < 8 && directory != null; depth++, directory = directory.Parent)
                {
                    if (File.Exists(Path.Combine(directory.FullName, "DriverScanTester.csproj")))
                        return directory.FullName;

                    string nestedProject = Path.Combine(directory.FullName, "DriverScanTester");
                    if (File.Exists(Path.Combine(nestedProject, "DriverScanTester.csproj")))
                        return nestedProject;
                }
            }
            catch
            {
                // Invalid start path / permissions — treat as a published run.
            }

            return null;
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
        /// and the local-player marker exclusion area outlined. Returns null on failure.
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
