using System;
using System.Collections.Generic;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Unit tests for the cross-scan item MouseOver agreement rule (no game required):
    /// the SAME raw hovered value must be observed in several SEPARATE loot scans, not on
    /// three different items inside one scan. The counts can be persisted and resumed by a
    /// later bot run.
    /// </summary>
    public sealed class ItemMouseOverScanAgreementTests
    {
        private const int ItemValue = 121498128;

        [Fact]
        public void SameValueOnThreeSeparateScansConfirmsIt()
        {
            var agreement = new ItemMouseOverScanAgreement(requiredScans: 3);

            Assert.False(agreement.RecordScan(ItemValue));
            Assert.False(agreement.IsComplete);
            Assert.Equal(1, agreement.ScanCountFor(ItemValue));

            Assert.False(agreement.RecordScan(ItemValue));
            Assert.False(agreement.IsComplete);
            Assert.Equal(2, agreement.ScanCountFor(ItemValue));
            Assert.True(agreement.HasPendingObservations);

            Assert.True(agreement.RecordScan(ItemValue));
            Assert.True(agreement.IsComplete);
            Assert.Equal(ItemValue, agreement.ConfirmedValue);
        }

        [Fact]
        public void OneItemPerScanIsEnoughBecauseScansAccumulate()
        {
            // This is the user-facing regression: a scan with a single drop must still
            // contribute evidence; the three observations come from three different kills.
            var agreement = new ItemMouseOverScanAgreement(requiredScans: 3);

            agreement.RecordScan(ItemValue); // kill 1 -> one item
            agreement.RecordScan(ItemValue); // kill 2 -> one item
            Assert.False(agreement.IsComplete);

            Assert.True(agreement.RecordScan(ItemValue)); // kill 3 -> one item
            Assert.Equal(ItemValue, agreement.ConfirmedValue);
        }

        [Fact]
        public void ZeroMeansNothingHoveredAndNeverCounts()
        {
            var agreement = new ItemMouseOverScanAgreement(requiredScans: 3);

            for (int i = 0; i < 5; i++)
                Assert.False(agreement.RecordScan(0));

            Assert.False(agreement.IsComplete);
            Assert.False(agreement.HasPendingObservations);
            Assert.Equal(0, agreement.TrackedValueCount);
        }

        [Fact]
        public void DifferentValuesAreCountedSeparately()
        {
            var agreement = new ItemMouseOverScanAgreement(requiredScans: 3);

            agreement.RecordScan(111);
            agreement.RecordScan(222);
            agreement.RecordScan(111);
            agreement.RecordScan(333);
            agreement.RecordScan(111);

            Assert.True(agreement.IsComplete);
            Assert.Equal(111, agreement.ConfirmedValue);
            Assert.Equal(3, agreement.ScanCountFor(111));
            Assert.Equal(1, agreement.ScanCountFor(222));
            Assert.Equal(1, agreement.ScanCountFor(333));
        }

        [Fact]
        public void PersistedCountsResumeAcrossBotRuns()
        {
            var firstRun = new ItemMouseOverScanAgreement(requiredScans: 3);
            firstRun.RecordScan(ItemValue);
            firstRun.RecordScan(ItemValue);

            // A stopped/restarted bot restores the snapshot instead of starting at zero.
            var secondRun = new ItemMouseOverScanAgreement(
                requiredScans: 3,
                persistedScanCounts: firstRun.Snapshot());

            Assert.Equal(2, secondRun.ScanCountFor(ItemValue));
            Assert.True(secondRun.HasPendingObservations);

            Assert.True(secondRun.RecordScan(ItemValue));
            Assert.Equal(ItemValue, secondRun.ConfirmedValue);
        }

        [Fact]
        public void AlreadyConfirmedPersistedValueIsRecognized()
        {
            var agreement = new ItemMouseOverScanAgreement(
                requiredScans: 3,
                persistedScanCounts: new Dictionary<int, int> { [ItemValue] = 3 });

            Assert.True(agreement.IsComplete);
            Assert.Equal(ItemValue, agreement.ConfirmedValue);
            Assert.False(agreement.HasPendingObservations);
            Assert.False(agreement.RecordScan(999));
        }

        [Fact]
        public void ResetForgetsEverything()
        {
            var agreement = new ItemMouseOverScanAgreement(requiredScans: 3);
            agreement.RecordScan(ItemValue);
            agreement.RecordScan(ItemValue);

            agreement.Reset();

            Assert.False(agreement.IsComplete);
            Assert.False(agreement.HasPendingObservations);
            Assert.Equal(0, agreement.ScanCountFor(ItemValue));
        }

        [Fact]
        public void ProgressSummaryDescribesTheAgreement()
        {
            var agreement = new ItemMouseOverScanAgreement(requiredScans: 3);

            Assert.Contains("0/3", agreement.DescribeProgress(), StringComparison.Ordinal);

            agreement.RecordScan(ItemValue);
            string pending = agreement.DescribeProgress();
            Assert.Contains("1/3", pending, StringComparison.Ordinal);
            Assert.Contains(MouseOverValueDiscovery.Describe(ItemValue), pending, StringComparison.Ordinal);

            agreement.RecordScan(ItemValue);
            agreement.RecordScan(ItemValue);
            Assert.Contains("confirmed", agreement.DescribeProgress(), StringComparison.Ordinal);
        }

        [Fact]
        public void RequiresAtLeastTwoScans()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new ItemMouseOverScanAgreement(requiredScans: 1));
        }
    }
}
