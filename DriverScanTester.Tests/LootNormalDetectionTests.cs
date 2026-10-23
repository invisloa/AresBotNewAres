using System.Drawing;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class LootNormalDetectionTests
    {
        private static LootSystem.LootComponent MakeFilledBlob(int width, int height)
            => MakeBlobAt(0, 0, width, height);

        private static LootSystem.LootComponent MakeBlobAt(int originX, int originY, int width, int height)
        {
            var component = new LootSystem.LootComponent();
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                    component.Add(originX + x, originY + y);
            }
            return component;
        }

        [Theory]
        [InlineData(9, 20, false)]
        [InlineData(20, 9, false)]
        [InlineData(10, 10, true)]
        [InlineData(64, 64, true)]
        public void NormalLootCandidatesRequireLargeFootprintWithoutUpperCap(
            int width, int height, bool expected)
        {
            Assert.Equal(expected, LootSystem.IsNormalLootCandidate(MakeFilledBlob(width, height)));
        }

        [Fact]
        public void NormalLootCandidateRejectsSparseDiagonalNoise()
        {
            var diagonal = new LootSystem.LootComponent();
            for (int i = 0; i < 10; i++)
                diagonal.Add(i, i);

            Assert.False(LootSystem.IsNormalLootCandidate(diagonal));
        }

        [Fact]
        public void LargeLootComponentProbePlanCoversEveryMaskPixelWithSpatialSpread()
        {
            LootSystem.LootComponent component = MakeFilledBlob(20, 20);
            LootSystem.LootProbePlan plan = LootSystem.BuildLootProbePlan(component);
            var mask = new HashSet<Point>(component.Points);

            Assert.Equal(400, component.Area);
            Assert.Equal(400, plan.Points.Count);
            Assert.Equal(400, plan.Points.Distinct().Count());
            Assert.True(plan.Coverage.Count > 8,
                $"Stage A must not be limited to eight probes, was {plan.Coverage.Count}.");

            // Every generated mouseover point is an actual detected loot pixel.
            Assert.All(plan.Points, probe => Assert.Contains(probe, mask));
            Assert.All(plan.Coverage, probe => Assert.Contains(probe, mask));

            // One representative per occupied stage-A cell: no repeated near-neighbour first.
            var coveredCells = plan.Coverage
                .Select(p => ((p.X - component.XMin) / plan.CellSizePx,
                              (p.Y - component.YMin) / plan.CellSizePx))
                .ToHashSet();
            Assert.Equal(plan.Coverage.Count, coveredCells.Count);

            // Distinct portions of the blob are covered before the exhaustive fallback.
            Assert.Contains(plan.Coverage, p => p.X < 10 && p.Y < 10);
            Assert.Contains(plan.Coverage, p => p.X >= 10 && p.Y < 10);
            Assert.Contains(plan.Coverage, p => p.X < 10 && p.Y >= 10);
            Assert.Contains(plan.Coverage, p => p.X >= 10 && p.Y >= 10);

            Assert.Equal(400 - plan.Coverage.Count, plan.Exhaustive.Count);
            Assert.Empty(plan.Coverage.Intersect(plan.Exhaustive));
            Assert.Equal(plan.Coverage, plan.Points.Take(plan.Coverage.Count));
        }

        [Fact]
        public void ProbePlannerContinuesPastEarlyMissesToALaterLootPixel()
        {
            LootSystem.LootComponent component = MakeFilledBlob(20, 20);
            LootSystem.LootProbePlan plan = LootSystem.BuildLootProbePlan(component);
            Point expected = plan.Exhaustive[plan.Exhaustive.Count / 2];
            Assert.DoesNotContain(expected, plan.Coverage);

            var planner = LootSystem.LootProbePlanner.CreateForNormal(new[] { component }, alreadyProbed: null);
            int attempts = 0;
            Point? confirmed = null;
            while (planner.TryNext(out int candidateIndex, out Point probe))
            {
                attempts++;
                Assert.Equal(0, candidateIndex);
                Assert.Contains(probe, component.Points);
                if (probe == expected)
                {
                    confirmed = probe;
                    break;
                }
            }

            Assert.Equal(expected, confirmed);
            Assert.True(attempts > 8,
                $"The scanner must continue past the first eight misses, stopped after {attempts}.");
        }

        [Fact]
        public void MultipleLootComponentsAreProbedFairlyAndSurviveFreshCapture()
        {
            LootSystem.LootComponent large = MakeBlobAt(0, 0, 30, 30);
            LootSystem.LootComponent small = MakeBlobAt(80, 0, 10, 10);
            var candidates = new[] { large, small };

            var planner = LootSystem.LootProbePlanner.CreateForNormal(candidates, alreadyProbed: null);
            Assert.True(planner.TryNext(out int firstIndex, out _));
            Assert.True(planner.TryNext(out int secondIndex, out _));
            Assert.NotEqual(firstIndex, secondIndex);
            Assert.Equal(2, planner.CandidatesWithRemainingProbes);

            // The 900-pixel component cannot starve the 100-pixel component: the small
            // candidate's whole plan is consumed after at most two round-robin turns.
            var fairness = LootSystem.LootProbePlanner.CreateForNormal(candidates, alreadyProbed: null);
            int smallPlanCount = fairness.InitialRemainingFor(1);
            int probes = 0;
            while (fairness.TryNext(out _, out _))
            {
                probes++;
                if (fairness.AttemptsFor(1) >= smallPlanCount)
                    break;
            }
            Assert.Equal(smallPlanCount, fairness.AttemptsFor(1));
            Assert.True(probes <= 2 * smallPlanCount,
                $"Small candidate took {probes} probes for {smallPlanCount} points.");
            Assert.True(fairness.AttemptsFor(0) > 0 &&
                        fairness.AttemptsFor(0) < fairness.InitialRemainingFor(0));

            // Simulate picking up the small item, then a FRESH capture that only
            // contains the large candidate: the remaining loot stays eligible.
            var freshScan = LootSystem.LootProbePlanner.CreateForNormal(new[] { large }, alreadyProbed: null);
            Point? freshConfirmed = null;
            while (freshScan.TryNext(out _, out Point probe))
            {
                if (large.Points.Contains(probe))
                {
                    freshConfirmed = probe;
                    break;
                }
            }
            Assert.NotNull(freshConfirmed);
        }

        [Fact]
        public void ProbeBudgetExhaustionIsIncompleteAndContinuationSkipsExaminedPixels()
        {
            LootSystem.LootComponent component = MakeFilledBlob(30, 30);
            const int budget = 50;
            var examined = new HashSet<Point>();

            var planner = LootSystem.LootProbePlanner.CreateForNormal(new[] { component }, examined);
            int used = 0;
            while (used < budget && planner.TryNext(out _, out Point probe))
            {
                used++;
                examined.Add(probe);
            }

            Assert.Equal(budget, used);
            Assert.True(planner.RemainingProbeCount > 0);
            Assert.Equal(LootSystem.LootScanPassResult.Incomplete,
                LootSystem.ResolveProbeSweepResult(planner.RemainingProbeCount));

            // Bounded continuation: the next pass skips examined pixels instead of
            // restarting the same prefix, then reports NoItem only once truly exhausted.
            var continuation = LootSystem.LootProbePlanner.CreateForNormal(new[] { component }, examined);
            Assert.Equal(budget, continuation.PreviouslyProbedSkipped);
            Assert.Equal(component.Area - budget, continuation.InitialRemainingProbes);

            int continued = 0;
            while (continuation.TryNext(out _, out _))
                continued++;
            Assert.Equal(component.Area - budget, continued);
            Assert.Equal(LootSystem.LootScanPassResult.NoItem,
                LootSystem.ResolveProbeSweepResult(continuation.RemainingProbeCount));

            // Candidate geometry changes reset the continuation key.
            LootSystem.LootComponent shifted = MakeBlobAt(1, 0, 30, 30);
            Assert.NotEqual(
                LootSystem.BuildLootProbeProgressKey("BigScan", null, new[] { component }),
                LootSystem.BuildLootProbeProgressKey("BigScan", null, new[] { shifted }));
        }

        [Fact]
        public void DiagnosticCaptionSeparatesDetectedMaskFromProbePositions()
        {
            string caption = ScreenshotService.BuildLootScanCaption(
                "BigScan", "no-item",
                lootCount: 400,
                candidateComponents: 1,
                candidatePixelCount: 400,
                probeCount: 8,
                confirmedProbe: null,
                pixelLabel: "normal",
                aquamarineCount: 400);

            Assert.Contains("detected=400px", caption, StringComparison.Ordinal);
            Assert.Contains("aquamarine=400", caption, StringComparison.Ordinal);
            Assert.Contains("candidates=1 comps/400px", caption, StringComparison.Ordinal);
            Assert.Contains("probes=8", caption, StringComparison.Ordinal);
            Assert.DoesNotContain("detected=8px", caption, StringComparison.Ordinal);
        }
    }
}
