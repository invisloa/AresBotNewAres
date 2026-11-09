using System;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Unit tests for the automatic MouseOver ITEM value discovery rule (no game required):
    /// a value is accepted only when the SAME raw hovered value is published by several
    /// DIFFERENT item candidates found by the loot pixel scan.
    /// </summary>
    public sealed class ItemMouseOverCalibrationTests
    {
        [Fact]
        public void ValueIsConfirmedOnlyAfterTheRequiredNumberOfDifferentItems()
        {
            var calibration = new ItemMouseOverCalibration(requiredDistinctItems: 3);

            Assert.False(calibration.RecordObservation(100, 100, 121498128));
            Assert.False(calibration.IsComplete);
            Assert.Equal(1, calibration.DistinctItemCountFor(121498128));

            Assert.False(calibration.RecordObservation(300, 100, 121498128));
            Assert.False(calibration.IsComplete);

            Assert.True(calibration.RecordObservation(500, 400, 121498128));
            Assert.True(calibration.IsComplete);
            Assert.Equal(121498128, calibration.ConfirmedValue);
        }

        [Fact]
        public void AnotherPixelOfTheSameItemDoesNotCountTwice()
        {
            var calibration = new ItemMouseOverCalibration(requiredDistinctItems: 3, minItemSeparationPx: 12);

            Assert.False(calibration.RecordObservation(100, 100, 121498128));
            // 8 px away — another probe pixel of the same blob.
            Assert.False(calibration.RecordObservation(108, 104, 121498128));
            Assert.False(calibration.RecordObservation(92, 95, 121498128));

            Assert.Equal(1, calibration.DistinctItemCountFor(121498128));
            Assert.Equal(3, calibration.Observations);
            Assert.False(calibration.IsComplete);

            // A genuinely different item is the 2nd distinct candidate; one more is needed.
            Assert.False(calibration.RecordObservation(400, 300, 121498128));
            Assert.Equal(2, calibration.DistinctItemCountFor(121498128));
            Assert.False(calibration.IsComplete);

            Assert.True(calibration.RecordObservation(700, 500, 121498128));
            Assert.True(calibration.IsComplete);
        }

        [Fact]
        public void NoHoveredObjectNeverConfirmsAValue()
        {
            var calibration = new ItemMouseOverCalibration(requiredDistinctItems: 2);

            Assert.False(calibration.RecordObservation(100, 100, 0));
            Assert.False(calibration.RecordObservation(300, 300, 0));
            Assert.False(calibration.RecordObservation(500, 500, 0));

            Assert.False(calibration.IsComplete);
            Assert.Equal(0, calibration.TrackedValueCount);
            Assert.Equal(3, calibration.Observations);
        }

        [Fact]
        public void DifferentValuesAreTrackedSeparately()
        {
            var calibration = new ItemMouseOverCalibration(requiredDistinctItems: 3);

            calibration.RecordObservation(100, 100, 111);
            calibration.RecordObservation(300, 100, 111);
            calibration.RecordObservation(500, 300, 222);
            calibration.RecordObservation(700, 300, 222);

            Assert.False(calibration.IsComplete);
            Assert.Equal(2, calibration.DistinctItemCountFor(111));
            Assert.Equal(2, calibration.DistinctItemCountFor(222));

            Assert.True(calibration.RecordObservation(900, 500, 111));
            Assert.Equal(111, calibration.ConfirmedValue);
        }

        [Fact]
        public void CandidateThatAppearsOnOnlyOneItemIsNeverAccepted()
        {
            var calibration = new ItemMouseOverCalibration(requiredDistinctItems: 3);

            Assert.False(calibration.RecordObservation(100, 100, 999));
            Assert.False(calibration.RecordObservation(104, 100, 999));
            Assert.False(calibration.RecordObservation(100, 106, 999));

            Assert.False(calibration.IsComplete);
            Assert.Null(calibration.ConfirmedValue);
        }

        [Fact]
        public void ProbeBudgetBoundsTheSlowScan()
        {
            var calibration = new ItemMouseOverCalibration(requiredDistinctItems: 3, maxObservations: 5);

            for (int i = 0; i < 5; i++)
                calibration.RecordObservation(100 + (i * 100), 100, 0);

            Assert.True(calibration.IsExhausted);
            Assert.False(calibration.IsActive);

            // An exhausted calibration ignores further probes (never confirms later by accident).
            Assert.False(calibration.RecordObservation(1000, 1000, 121498128));
            Assert.False(calibration.RecordObservation(2000, 2000, 121498128));
            Assert.False(calibration.RecordObservation(3000, 3000, 121498128));
            Assert.False(calibration.IsComplete);
        }

        [Fact]
        public void StatusSummaryDescribesTheAttempt()
        {
            var calibration = new ItemMouseOverCalibration(requiredDistinctItems: 3);

            calibration.RecordObservation(100, 100, 121498128);
            calibration.RecordObservation(300, 300, 0);

            string summary = calibration.DescribeObservations();

            Assert.Contains("probes=2/", summary, StringComparison.Ordinal);
            Assert.Contains("distinctValues=1", summary, StringComparison.Ordinal);
            Assert.Contains("confirmed=none", summary, StringComparison.Ordinal);

            calibration.RecordObservation(500, 500, 121498128);
            calibration.RecordObservation(800, 800, 121498128);

            Assert.Contains("confirmed=121498128", calibration.DescribeObservations(), StringComparison.Ordinal);
        }

        [Fact]
        public void InvalidSettingsAreRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ItemMouseOverCalibration(requiredDistinctItems: 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ItemMouseOverCalibration(requiredDistinctItems: 3, maxObservations: 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ItemMouseOverCalibration(minItemSeparationPx: -1));
        }
    }
}
