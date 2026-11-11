using System;
using System.Collections.Generic;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Bookkeeping for the automatic MouseOver ITEM value re-discovery (the item twin of
    /// the NPC slow discovery).
    ///
    /// The loot scan cannot prove "this is an item" with a dialogue like the NPC scan can,
    /// and not every hovered pixel is an item. This class collects the probes of ONE loot
    /// scan and offers two kinds of evidence:
    ///  - the SAME value on several DIFFERENT items within this scan (fast path), and
    ///  - the dominant value of the whole scan, which the caller feeds into
    ///    <see cref="ItemMouseOverScanAgreement"/> so the same value may also be confirmed
    ///    across SEVERAL scans (one per kill / bot run). A single hover (or a value that
    ///    only ever appears on one blob) is never accepted on its own.
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
        private readonly Dictionary<int, int> _observationCountsByValue = new();
        private readonly List<int> _valueOrder = new();
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
                _valueOrder.Add(hoveredValue);
            }

            _observationCountsByValue[hoveredValue] =
                (_observationCountsByValue.TryGetValue(hoveredValue, out int observations) ? observations : 0) + 1;

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

        /// <summary>How many probes published this value during this scan.</summary>
        public int ObservationCountFor(int value)
            => _observationCountsByValue.TryGetValue(value, out int count) ? count : 0;

        /// <summary>
        /// The single value that best represents THIS scan: the value read by the most probes
        /// (ties broken by the number of distinct item candidates). Returns false when no
        /// non-zero value was observed or when two values are equally strong — an ambiguous
        /// scan must not cast a cross-scan vote.
        ///
        /// Frequency is the primary evidence because the mouseover buffer can briefly return
        /// the previous object's value while the cursor settles on a candidate: the real item
        /// value is then still the one seen by most probes of the scan.
        ///
        /// This is what allows one-item scans to participate in the cross-scan agreement
        /// (<see cref="ItemMouseOverScanAgreement"/>) instead of requiring three different
        /// items in the same scan.
        /// </summary>
        public bool TryGetDominantValue(out int value)
        {
            value = 0;
            int bestObservationCount = 0;
            int bestItemCount = 0;
            bool ambiguous = false;

            foreach (int candidate in _valueOrder)
            {
                int observationCount = ObservationCountFor(candidate);
                int itemCount = DistinctItemCountFor(candidate);

                if (observationCount > bestObservationCount ||
                    (observationCount == bestObservationCount && itemCount > bestItemCount))
                {
                    value = candidate;
                    bestObservationCount = observationCount;
                    bestItemCount = itemCount;
                    ambiguous = false;
                }
                else if (observationCount == bestObservationCount && itemCount == bestItemCount)
                {
                    ambiguous = true;
                }
            }

            if (ambiguous || value == 0)
            {
                value = 0;
                return false;
            }

            return true;
        }

        /// <summary>Short diagnostic summary for the bot log.</summary>
        public string DescribeObservations()
            => $"probes={Observations}/{_maxObservations}, distinctValues={TrackedValueCount}, " +
               $"confirmed={(IsComplete ? MouseOverValueDiscovery.Describe(ConfirmedValue!.Value) : "none")}";
    }
}
