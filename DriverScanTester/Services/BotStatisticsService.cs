using System;
using System.Diagnostics;
using System.Threading;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Session-wide bot statistics shown in the read-only Statistics section of the
    /// Bot Control Panel: total active work time, completed workflow cycles and how
    /// many items the bot sold. Counters are cumulative for the whole application
    /// session (they are not reset by an individual Start/Stop).
    ///
    /// Work time is sampled once per second from the activity/pause predicates
    /// configured by <see cref="ViewModels.MainViewModel"/>, so it keeps counting
    /// while the Bot Control Panel is closed and stops counting while the bot is
    /// paused (paused time is not work time). Sampling is monotonic (Stopwatch),
    /// therefore wall-clock changes/DST never move it backwards.
    /// </summary>
    public sealed class BotStatisticsService
    {
        /// <summary>Single shared instance used by the view-models and the sell path.</summary>
        public static BotStatisticsService Instance { get; } = new BotStatisticsService();

        private const int SampleIntervalMs = 1000;

        /// <summary>
        /// Upper bound for a single sample. A suspended machine (sleep/hibernate) or a
        /// stalled sampler must not turn into hours of counted bot work.
        /// </summary>
        private static readonly TimeSpan MaxSampleDelta = TimeSpan.FromSeconds(10);

        private readonly object _gate = new object();
        private readonly System.Threading.Timer _sampler;
        private readonly bool _samplerEnabled;
        private Func<bool>? _isActivityActive;
        private Func<bool>? _isPaused;
        private long _lastSampleTimestamp;
        private TimeSpan _workTime;
        private int _itemsSold;
        private int _completedCycles;

        /// <summary>
        /// Creates the statistics holder. Internal so tests can create an isolated
        /// instance with the background sampler disabled; production code uses the
        /// <see cref="Instance"/> singleton.
        /// </summary>
        internal BotStatisticsService(bool samplerEnabled = true)
        {
            _samplerEnabled = samplerEnabled;
            _lastSampleTimestamp = Stopwatch.GetTimestamp();
            _sampler = new System.Threading.Timer(_ => Sample(Stopwatch.GetTimestamp()), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// Wires the statistics sampler to the live runtime state. Called once by the
        /// main view-model: <paramref name="isActivityActive"/> is true while the
        /// workflow or any standalone bot (movement/loot/heal) is running.
        /// </summary>
        public void Configure(Func<bool> isActivityActive, Func<bool> isPaused)
        {
            if (isActivityActive == null) throw new ArgumentNullException(nameof(isActivityActive));
            if (isPaused == null) throw new ArgumentNullException(nameof(isPaused));

            lock (_gate)
            {
                _isActivityActive = isActivityActive;
                _isPaused = isPaused;
                _lastSampleTimestamp = Stopwatch.GetTimestamp();
            }

            if (_samplerEnabled)
                _sampler.Change(SampleIntervalMs, SampleIntervalMs);
        }

        /// <summary>Total time the bot has been running (paused time excluded).</summary>
        public TimeSpan WorkTime
        {
            get
            {
                lock (_gate)
                    return _workTime;
            }
        }

        /// <summary><see cref="WorkTime"/> in a compact "hh:mm:ss" / "d.hh:mm:ss" form.</summary>
        public string WorkTimeText => FormatWorkTime(WorkTime);

        /// <summary>Total number of items sold since the application was started.</summary>
        public int ItemsSold => Volatile.Read(ref _itemsSold);

        /// <summary>Total completed workflow cycles since the application was started.</summary>
        public int CompletedCycles => Volatile.Read(ref _completedCycles);

        /// <summary>Adds sold items and returns the new session total.</summary>
        public int AddItemsSold(int count)
        {
            if (count <= 0)
                return ItemsSold;

            return Interlocked.Add(ref _itemsSold, count);
        }

        /// <summary>Adds one completed workflow cycle and returns the new session total.</summary>
        public int IncrementCompletedCycles() => Interlocked.Increment(ref _completedCycles);

        /// <summary>Clears every session statistic (work time, sold items, cycles).</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _workTime = TimeSpan.Zero;
                _lastSampleTimestamp = Stopwatch.GetTimestamp();
            }
            Interlocked.Exchange(ref _itemsSold, 0);
            Interlocked.Exchange(ref _completedCycles, 0);
        }

        internal void SampleForTests(long nowTimestamp) => Sample(nowTimestamp);

        /// <summary>Test hook: replaces the sample anchor so deltas are deterministic.</summary>
        internal void SetLastSampleTimestampForTests(long timestamp)
        {
            lock (_gate)
                _lastSampleTimestamp = timestamp;
        }

        private void Sample(long nowTimestamp)
        {
            lock (_gate)
            {
                TimeSpan delta = Stopwatch.GetElapsedTime(_lastSampleTimestamp, nowTimestamp);
                _lastSampleTimestamp = nowTimestamp;

                if (delta <= TimeSpan.Zero)
                    return;
                if (delta > MaxSampleDelta)
                    delta = MaxSampleDelta;

                bool active;
                try
                {
                    active = _isActivityActive?.Invoke() == true && _isPaused?.Invoke() != true;
                }
                catch
                {
                    // A faulty predicate must never break the sampler; treat it as idle.
                    active = false;
                }

                if (active)
                    _workTime += delta;
            }
        }

        /// <summary>Formats work time as "hh:mm:ss" (or "d.hh:mm:ss" after a full day).</summary>
        public static string FormatWorkTime(TimeSpan value)
        {
            if (value < TimeSpan.Zero)
                value = TimeSpan.Zero;

            return value.TotalDays >= 1
                ? $"{(int)value.TotalDays}d {value.Hours:D2}:{value.Minutes:D2}:{value.Seconds:D2}"
                : $"{(int)value.TotalHours:D2}:{value.Minutes:D2}:{value.Seconds:D2}";
        }
    }
}
