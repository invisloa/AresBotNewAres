using System;
using System.Collections.Generic;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Unit tests for the stage-2 candidate rules used by the automatic MouseOver NPC
    /// calibration (no game required): which samples may become a candidate, which ones are
    /// rejected, how rejections/budgets are tracked and how the active value changes.
    /// </summary>
    public sealed class MouseOverValueDiscoveryTests
    {
        private const int ActiveValue = 138599240;
        private const int CandidateValue = 121498128;

        private static MouseOverValueDiscovery CreateDiscovery(
            List<string>? log = null,
            int maxCandidateTests = MouseOverValueDiscovery.DefaultMaxCandidateTests,
            int maxRejectedValues = MouseOverValueDiscovery.DefaultMaxRejectedValues,
            int activeValue = ActiveValue)
        {
            // Mirrors ItemSellerService: the active value lives in ONE place (the closure
            // over SellerPointedValue) and the discovery only reads/writes it.
            int currentActiveValue = activeValue;
            return new MouseOverValueDiscovery(
                () => currentActiveValue,
                value => currentActiveValue = value,
                message => log?.Add(message),
                maxCandidateTests,
                maxRejectedValues);
        }

        private static MouseOverValueSample Stable(int value, bool npcHighlighted = false)
            => MouseOverValueSample.FromReads(true, value, true, value, npcHighlighted);

        [Fact]
        public void SampleIsStableOnlyWhenBothReadsSucceedAndAgree()
        {
            MouseOverValueSample agreeing = MouseOverValueSample.FromReads(true, 7, true, 7, false);
            MouseOverValueSample disagreeing = MouseOverValueSample.FromReads(true, 7, true, 8, false);
            MouseOverValueSample firstReadFailed = MouseOverValueSample.FromReads(false, 0, true, 7, false);
            MouseOverValueSample confirmReadFailed = MouseOverValueSample.FromReads(true, 7, false, 0, false);

            Assert.True(agreeing.ReadSucceeded);
            Assert.True(agreeing.Stable);
            Assert.Equal(7, agreeing.Value);

            Assert.True(disagreeing.ReadSucceeded);
            Assert.False(disagreeing.Stable);

            // A failed read must never be reported as a readable value (not even 0).
            Assert.False(firstReadFailed.ReadSucceeded);
            Assert.False(firstReadFailed.Stable);
            Assert.False(confirmReadFailed.ReadSucceeded);
            Assert.False(confirmReadFailed.Stable);
        }

        [Fact]
        public void UnreadableSampleIsRejectedBeforeTheHoverProbeRuns()
        {
            MouseOverValueDiscovery discovery = CreateDiscovery();
            bool probeInvoked = false;

            MouseOverCandidateDecision decision = discovery.Evaluate(
                MouseOverValueSample.FromReads(false, 0, false, 0, false),
                () => { probeInvoked = true; return true; });

            Assert.Equal(MouseOverCandidateDecision.ReadFailed, decision);
            Assert.False(probeInvoked);
        }

        [Fact]
        public void UnstableSampleIsNotACandidate()
        {
            MouseOverValueDiscovery discovery = CreateDiscovery();
            bool probeInvoked = false;

            MouseOverCandidateDecision decision = discovery.Evaluate(
                MouseOverValueSample.FromReads(true, CandidateValue, true, CandidateValue + 1, false),
                () => { probeInvoked = true; return true; });

            Assert.Equal(MouseOverCandidateDecision.Unstable, decision);
            Assert.False(probeInvoked);
        }

        [Fact]
        public void ZeroValueMeansNothingIsHoveredAndIsNotACandidate()
        {
            MouseOverValueDiscovery discovery = CreateDiscovery();

            Assert.Equal(
                MouseOverCandidateDecision.NothingHovered,
                discovery.Evaluate(Stable(0), () => true));
        }

        [Fact]
        public void StableNovelNonZeroValueBecomesACandidate()
        {
            MouseOverValueDiscovery discovery = CreateDiscovery();

            Assert.Equal(
                MouseOverCandidateDecision.Candidate,
                discovery.Evaluate(Stable(CandidateValue, npcHighlighted: true), () => true));
        }

        [Fact]
        public void ValueMatchingTheActiveValueIsNotACandidate()
        {
            MouseOverValueDiscovery discovery = CreateDiscovery(activeValue: ActiveValue);

            Assert.Equal(
                MouseOverCandidateDecision.MatchesActiveValue,
                discovery.Evaluate(Stable(ActiveValue), () => true));
        }

        [Fact]
        public void HoverProbeOnlyRunsForValuesThatPassedEveryOtherCheck()
        {
            MouseOverValueDiscovery discovery = CreateDiscovery();
            int probeCalls = 0;

            discovery.Evaluate(Stable(0), () => { probeCalls++; return true; });
            discovery.Evaluate(Stable(ActiveValue), () => { probeCalls++; return true; });
            Assert.Equal(0, probeCalls);

            Assert.Equal(
                MouseOverCandidateDecision.Candidate,
                discovery.Evaluate(Stable(CandidateValue), () => { probeCalls++; return true; }));
            Assert.Equal(1, probeCalls);
        }

        [Fact]
        public void ValueThatDoesNotFollowTheCursorIsRejected()
        {
            MouseOverValueDiscovery discovery = CreateDiscovery();

            Assert.Equal(
                MouseOverCandidateDecision.HoverChangeNotObserved,
                discovery.Evaluate(Stable(CandidateValue), () => false));
        }

        [Fact]
        public void RejectedValueIsNeverAcceptedAgainInTheSameRun()
        {
            MouseOverValueDiscovery discovery = CreateDiscovery();

            Assert.Equal(MouseOverCandidateDecision.Candidate,
                discovery.Evaluate(Stable(CandidateValue), () => true));

            discovery.Reject(CandidateValue, "NPC dialogue did not open");

            Assert.True(discovery.IsRejected(CandidateValue));
            Assert.Equal(1, discovery.RejectedValueCount);
            Assert.Equal(MouseOverCandidateDecision.AlreadyRejected,
                discovery.Evaluate(Stable(CandidateValue), () => true));

            // Rejecting the same value twice must not grow the bookkeeping.
            discovery.Reject(CandidateValue, "again");
            Assert.Equal(1, discovery.RejectedValueCount);
        }

        [Fact]
        public void ZeroIsNeverRememberedAsARejectedValue()
        {
            MouseOverValueDiscovery discovery = CreateDiscovery();

            discovery.Reject(0, "nothing");

            Assert.False(discovery.IsRejected(0));
            Assert.Equal(0, discovery.RejectedValueCount);
            Assert.Equal(MouseOverCandidateDecision.NothingHovered,
                discovery.Evaluate(Stable(0), () => true));
        }

        [Fact]
        public void CandidateValidationBudgetIsBounded()
        {
            MouseOverValueDiscovery discovery = CreateDiscovery(maxCandidateTests: 2);

            Assert.Equal(1, discovery.StartCandidateTest(100));
            Assert.False(discovery.IsTestBudgetExhausted);
            Assert.Equal(MouseOverCandidateDecision.Candidate,
                discovery.Evaluate(Stable(200), () => true));

            Assert.Equal(2, discovery.StartCandidateTest(200));
            Assert.True(discovery.IsTestBudgetExhausted);

            Assert.Equal(MouseOverCandidateDecision.TestBudgetExhausted,
                discovery.Evaluate(Stable(300), () => true));
            Assert.Equal(2, discovery.CandidateTestCount);
        }

        [Fact]
        public void RejectedValueBookkeepingIsBounded()
        {
            MouseOverValueDiscovery discovery = CreateDiscovery(maxRejectedValues: 2);

            discovery.Reject(101, "one");
            discovery.Reject(102, "two");
            discovery.Reject(103, "three");

            Assert.Equal(2, discovery.RejectedValueCount);
            Assert.True(discovery.IsRejected(101));
            Assert.True(discovery.IsRejected(102));
            /* The third value could not be remembered; it must still not be treated as
               successfully validated anywhere — it simply may be observed again. */
            Assert.False(discovery.IsRejected(103));
        }

        [Fact]
        public void ConfirmMakesTheValidatedValueTheOnlyActiveValue()
        {
            var log = new List<string>();
            MouseOverValueDiscovery discovery = CreateDiscovery(log, activeValue: ActiveValue);

            int previous = discovery.Confirm(CandidateValue);

            Assert.Equal(ActiveValue, previous);
            Assert.Equal(CandidateValue, discovery.ActiveValue);

            // The old value is now just another hovered-object value, the new one is active.
            Assert.Equal(MouseOverCandidateDecision.MatchesActiveValue,
                discovery.Evaluate(Stable(CandidateValue), () => true));
            Assert.Equal(MouseOverCandidateDecision.Candidate,
                discovery.Evaluate(Stable(ActiveValue), () => true));

            Assert.Contains(log, line =>
                line.Contains("Active NPC detection value updated", StringComparison.Ordinal) &&
                line.Contains(ActiveValue.ToString(), StringComparison.Ordinal) &&
                line.Contains(CandidateValue.ToString(), StringComparison.Ordinal));
        }

        [Fact]
        public void ObservationCountTracksStableObservationsOfAValue()
        {
            MouseOverValueDiscovery discovery = CreateDiscovery();

            discovery.Evaluate(Stable(CandidateValue), () => true);
            discovery.Evaluate(Stable(CandidateValue), () => true);
            discovery.Evaluate(Stable(CandidateValue + 1), () => true);

            Assert.Equal(2, discovery.ObservationCount(CandidateValue));
            Assert.Equal(1, discovery.ObservationCount(CandidateValue + 1));
            Assert.Equal(0, discovery.ObservationCount(0));
        }

        [Fact]
        public void DescribeMarksTheUnsetValueExplicitly()
        {
            Assert.Equal("0 (unset)", MouseOverValueDiscovery.Describe(0));
            Assert.Equal("138599240 (0x0842DB48)", MouseOverValueDiscovery.Describe(138599240));
        }

        [Fact]
        public void InvalidLimitsAreRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new MouseOverValueDiscovery(() => 1, _ => { }, _ => { }, maxCandidateTests: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new MouseOverValueDiscovery(() => 1, _ => { }, _ => { }, maxRejectedValues: 0));
        }
    }
}
