using System;
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
