using System;
using System.Collections.Generic;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Cross-scan agreement tracker for the automatic MouseOver ITEM value discovery.
    ///
    /// A normal loot pass usually sees only ONE item drop, so requiring three DIFFERENT
    /// items in the SAME scan can never confirm a value. The accepted proof is instead the
    /// SAME raw hovered value observed in several SEPARATE loot scans (for example one scan
    /// after each kill). The scans do not have to belong to the same bot run: the counts are
    /// persisted by <see cref="MouseCalibrationStore"/> and restored for the same game
    /// session, so a stopped/started bot continues exactly where it left off.
    ///
    /// Values are tracked independently: a value that only appears once (or twice) is never
    /// accepted, and 0 ("nothing hovered") never counts as an observation.
    ///
    /// Pure logic (no memory/mouse/file IO), so the acceptance rule is unit-testable.
    /// </summary>
    public sealed class ItemMouseOverScanAgreement
    {
        /// <summary>How many separate loot scans must publish the same value.</summary>
        public const int DefaultRequiredScans = 3;

        private readonly Dictionary<int, int> _scanCountsByValue = new();
        private readonly int _requiredScans;

        /// <param name="requiredScans">Number of separate loot scans that must agree.</param>
        /// <param name="persistedScanCounts">
        /// Counts restored from a previous run for the same game session/value, or null.
        /// </param>
        public ItemMouseOverScanAgreement(
            int requiredScans = DefaultRequiredScans,
            IReadOnlyDictionary<int, int>? persistedScanCounts = null)
        {
            if (requiredScans < 2)
                throw new ArgumentOutOfRangeException(nameof(requiredScans));

            _requiredScans = requiredScans;

            if (persistedScanCounts == null)
                return;

            foreach (KeyValuePair<int, int> pair in persistedScanCounts)
            {
                if (pair.Key == 0 || pair.Value <= 0)
                    continue;

                _scanCountsByValue[pair.Key] = pair.Value;
                if (pair.Value >= _requiredScans)
                    ConfirmedValue = pair.Key;
            }
        }

        /// <summary>How many separate loot scans must publish the same value.</summary>
        public int RequiredScans => _requiredScans;

        /// <summary>The value confirmed by enough separate loot scans, else null.</summary>
        public int? ConfirmedValue { get; private set; }

        /// <summary>True once a value was confirmed by enough separate loot scans.</summary>
        public bool IsComplete => ConfirmedValue.HasValue;

        /// <summary>True when at least one scan observation is stored.</summary>
        public bool HasPendingObservations => !IsComplete && _scanCountsByValue.Count > 0;

        /// <summary>How many different candidate values were recorded.</summary>
        public int TrackedValueCount => _scanCountsByValue.Count;

        /// <summary>How many separate loot scans published this value so far.</summary>
        public int ScanCountFor(int value)
            => _scanCountsByValue.TryGetValue(value, out int count) ? count : 0;

        /// <summary>
        /// Records the value observed in ONE loot scan. Call this at most once per scan.
        /// Returns true when THIS observation completed the required agreement.
        /// </summary>
        public bool RecordScan(int value)
        {
            if (IsComplete || value == 0)
                return false;

            int count = ScanCountFor(value) + 1;
            _scanCountsByValue[value] = count;

            if (count < _requiredScans)
                return false;

            ConfirmedValue = value;
            return true;
        }

        /// <summary>Copy of the accumulated counts, suitable for persistence.</summary>
        public Dictionary<int, int> Snapshot() => new(_scanCountsByValue);

        /// <summary>Drops everything (used when the game session or active value changes).</summary>
        public void Reset()
        {
            _scanCountsByValue.Clear();
            ConfirmedValue = null;
        }

        /// <summary>Short diagnostic summary for the bot log.</summary>
        public string DescribeProgress()
        {
            if (IsComplete)
                return $"value {MouseOverValueDiscovery.Describe(ConfirmedValue!.Value)} confirmed by {RequiredScans} loot scans";

            if (_scanCountsByValue.Count == 0)
                return $"0/{RequiredScans} loot scans recorded";

            var parts = new List<string>(_scanCountsByValue.Count);
            foreach (KeyValuePair<int, int> pair in _scanCountsByValue)
                parts.Add($"{MouseOverValueDiscovery.Describe(pair.Key)}: {pair.Value}/{RequiredScans}");

            return string.Join(", ", parts);
        }
    }
}
