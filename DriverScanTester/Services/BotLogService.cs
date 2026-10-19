using System;
using System.Collections.Generic;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Process-wide bot log. Every bot line is stamped, kept in a bounded in-memory
    /// history and appended to the unlimited session file log — independent of whether
    /// the Bot Control Panel is open. The window subscribes to <see cref="LineAppended"/>
    /// and seeds itself from <see cref="SnapshotText"/>, so opening the panel later still
    /// shows what happened (including work done while detached).
    /// </summary>
    public sealed class BotLogService
    {
        /// <summary>Single shared instance used by the main view-model and the Bot window.</summary>
        public static BotLogService Instance { get; } = new();

        /// <summary>Number of lines kept for the UI (the file log is unlimited).</summary>
        public const int MaxLines = 500;

        private readonly Queue<string> _lines = new();
        private readonly object _lock = new();

        private BotLogService()
        {
        }

        /// <summary>Raised for every appended line (already timestamped). May fire on a worker thread.</summary>
        public event Action<string>? LineAppended;

        /// <summary>Appends one line: stamps it, stores it and writes it to the session file log.</summary>
        public void Append(string line)
        {
            if (string.IsNullOrEmpty(line))
                return;

            string stamped = $"[{DateTime.Now:HH:mm:ss.fff}] {line}";
            lock (_lock)
            {
                _lines.Enqueue(stamped);
                while (_lines.Count > MaxLines)
                    _lines.Dequeue();
            }

            // Unlimited file log (UI keeps only the last MaxLines lines).
            BotFileLogger.AppendLine(stamped);

            try
            {
                LineAppended?.Invoke(stamped);
            }
            catch
            {
                // A broken subscriber must never break the bot.
            }
        }

        /// <summary>Current UI history, oldest line first.</summary>
        public IReadOnlyList<string> Snapshot()
        {
            lock (_lock)
            {
                return new List<string>(_lines);
            }
        }

        /// <summary>Current UI history rendered as text (CRLF separated, trailing newline).</summary>
        public string SnapshotText()
        {
            lock (_lock)
            {
                return _lines.Count == 0 ? "" : string.Join("\r\n", _lines) + "\r\n";
            }
        }

        /// <summary>Clears the in-memory UI history (the file log keeps everything).</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _lines.Clear();
            }
        }
    }
}
