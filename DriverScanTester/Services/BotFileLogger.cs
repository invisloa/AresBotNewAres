using System;
using System.IO;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Unlimited file sink for the bot log. The UI keeps only the last 500 lines,
    /// but every line is appended to a per-session text file with no size limit.
    /// Thread-safe, never throws (logging must not break the bot).
    /// </summary>
    public static class BotFileLogger
    {
        private static readonly object _fileLock = new();
        private static string? _filePath;

        /// <summary>Full path of the current session log file.</summary>
        public static string CurrentFilePath
        {
            get
            {
                lock (_fileLock)
                {
                    return EnsureInitializedLocked();
                }
            }
        }

        /// <summary>Appends one already-stamped line to the session log file.</summary>
        public static void AppendLine(string stampedLine)
        {
            try
            {
                string path;
                lock (_fileLock)
                {
                    path = EnsureInitializedLocked();
                }
                File.AppendAllText(path, stampedLine + "\r\n");
            }
            catch
            {
                // Logging must never crash the bot (disk full, path locked, ...).
            }
        }

        /// <summary>
        /// Dedicated Logs folder outside the build output, so a rebuild (which wipes
        /// bin/Debug) never deletes the logs. Dev run (bin/Debug/...) → project-root
        /// Logs (next to Screenshots); published/single-file run → Logs next to the exe.
        /// </summary>
        private static string ResolveLogsDir()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                string devLogs = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "Logs"));
                string projectDir = Path.GetDirectoryName(devLogs) ?? "";
                // Dev layout check: ...\DriverScanTester\bin\Debug\net8.0-windows →
                // project dir holds the .csproj (same convention as Screenshots).
                if (Directory.Exists(Path.Combine(projectDir, "SavedPaths")) ||
                    File.Exists(Path.Combine(projectDir, "DriverScanTester.csproj")))
                    return devLogs;
            }
            catch
            {
                // Fall through to exe-local Logs.
            }
            return Path.Combine(baseDir, "Logs");
        }

        private static string EnsureInitializedLocked()
        {
            if (_filePath != null)
                return _filePath;

            string logsDir = ResolveLogsDir();
            Directory.CreateDirectory(logsDir);

            string fileName = $"bot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt";
            _filePath = Path.Combine(logsDir, fileName);

            try
            {
                File.AppendAllText(_filePath,
                    $"=== Bot log started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\r\n");
            }
            catch
            {
                // Ignore header write failures; per-line writes will retry.
            }

            return _filePath;
        }
    }
}
