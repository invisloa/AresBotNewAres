using System;
using System.Collections.Generic;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Bookkeeping for the automatic MouseOver ITEM value re-discovery (the item twin of
    /// the NPC slow discovery).
    ///
    /// The loot scan cannot prove "this is an item" with a dialogue like the NPC scan can,
    /// and not every hovered pixel is an item. The accepted proof used here is
    /// CONSISTENCY: the raw value published while the cursor hovers item-like candidates
    /// (the normal loot pixel classifiers) must be the SAME value on several DIFFERENT
    /// items. A value seen on <see cref="RequiredDistinctItems"/> distinct item candidates
    /// is therefore treated as the item MouseOver value; a single hover (or a value that
    /// only ever appears on one blob) is never accepted.
    ///
    /// Probes whose anchor is closer than <see cref="MinItemSeparationPx"/> to an anchor
    /// that already counted for the same value are the same item (many probe pixels per
    /// blob) and count once.
    ///
    /// Pure logic (no memory/mouse IO), so the acceptance rule is unit-testable.
    /// </summary>
    public sealed class ItemMouseOverCalibration
    {
        /// <summary>How many different item candidates must publish the same value.</summary>
        public const int DefaultRequiredDistinctItems = 3;

        /// <summary>Upper bound of calibration probes per scan attempt (bounds the slow scan).</summary>
        public const int DefaultMaxObservations = 120;

        /// <summary>Probes at most this close (px, per axis) belong to the same item.</summary>
        public const int DefaultMinItemSeparationPx = 12;

        private readonly Dictionary<int, List<(int X, int Y)>> _anchorsByValue = new();
        private readonly int _requiredDistinctItems;
        private readonly int _maxObservations;
        private readonly int _minItemSeparationPx;

        public ItemMouseOverCalibration(
            int requiredDistinctItems = DefaultRequiredDistinctItems,
            int maxObservations = DefaultMaxObservations,
            int minItemSeparationPx = DefaultMinItemSeparationPx)
        {
            if (requiredDistinctItems < 2)
                throw new ArgumentOutOfRangeException(nameof(requiredDistinctItems));
            if (maxObservations < requiredDistinctItems)
                throw new ArgumentOutOfRangeException(nameof(maxObservations));
            if (minItemSeparationPx < 0)
                throw new ArgumentOutOfRangeException(nameof(minItemSeparationPx));

            _requiredDistinctItems = requiredDistinctItems;
            _maxObservations = maxObservations;
            _minItemSeparationPx = minItemSeparationPx;
        }

        /// <summary>The value confirmed by the required number of distinct items, else null.</summary>
        public int? ConfirmedValue { get; private set; }

        /// <summary>True once a value was confirmed by enough distinct item candidates.</summary>
        public bool IsComplete => ConfirmedValue.HasValue;

        /// <summary>Number of probes recorded so far (including probes that read 0 / an NPC).</summary>
        public int Observations { get; private set; }

        /// <summary>True when the probe budget is used up without a confirmation.</summary>
        public bool IsExhausted => !IsComplete && Observations >= _maxObservations;

        /// <summary>True while the scan should keep collecting calibration observations.</summary>
        public bool IsActive => !IsComplete && !IsExhausted;

        /// <summary>How many distinct item candidates must agree on one value.</summary>
        public int RequiredDistinctItems => _requiredDistinctItems;

        /// <summary>How many distinct hovered values were observed.</summary>
        public int TrackedValueCount => _anchorsByValue.Count;

        /// <summary>How many distinct item candidates published this value so far.</summary>
        public int DistinctItemCountFor(int value)
            => _anchorsByValue.TryGetValue(value, out List<(int X, int Y)>? anchors) ? anchors.Count : 0;

        /// <summary>
        /// Records one probe: <paramref name="hoveredValue"/> is the raw mouseover value read
        /// while the cursor hovered the item candidate anchored at
        /// (<paramref name="anchorX"/>, <paramref name="anchorY"/>). Passing 0 records an
        /// attempt without a hovered object (it only consumes the probe budget).
        /// Returns true when THIS observation confirmed the value.
        /// </summary>
        public bool RecordObservation(int anchorX, int anchorY, int hoveredValue)
        {
            if (!IsActive)
                return false;

            Observations++;

            if (hoveredValue == 0)
                return false;

            if (!_anchorsByValue.TryGetValue(hoveredValue, out List<(int X, int Y)>? anchors))
            {
                anchors = new List<(int X, int Y)>(_requiredDistinctItems);
                _anchorsByValue[hoveredValue] = anchors;
            }

            foreach ((int X, int Y) anchor in anchors)
            {
                if (Math.Abs(anchor.X - anchorX) <= _minItemSeparationPx &&
                    Math.Abs(anchor.Y - anchorY) <= _minItemSeparationPx)
                {
                    // Same item (another pixel of the same blob) — do not count it twice.
                    return false;
                }
            }

            anchors.Add((anchorX, anchorY));
            if (anchors.Count < _requiredDistinctItems)
                return false;

            ConfirmedValue = hoveredValue;
            return true;
        }

        /// <summary>Short diagnostic summary for the bot log.</summary>
        public string DescribeObservations()
            => $"probes={Observations}/{_maxObservations}, distinctValues={TrackedValueCount}, " +
               $"confirmed={(IsComplete ? MouseOverValueDiscovery.Describe(ConfirmedValue!.Value) : "none")}";
    }
}
