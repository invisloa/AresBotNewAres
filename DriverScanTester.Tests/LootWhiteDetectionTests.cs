using System.Drawing;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class LootWhiteDetectionTests
    {
        [Theory]
        [InlineData(255, 255, 255, true)]
        [InlineData(220, 215, 205, true)]
        [InlineData(160, 150, 145, true)]
        [InlineData(145, 145, 145, true)]
        [InlineData(144, 144, 144, false)]
        [InlineData(180, 130, 120, false)]
        [InlineData(150, 185, 220, false)]
        [InlineData(255, 34, 175, false)] // Pink remains exclusive to the SOD/SOP scan.
        public void ShadedWhiteClassifier_AcceptsBrightNeutralShadesOnly(
            int r, int g, int b, bool expected)
        {
            Assert.Equal(expected, LootSystem.IsShadedWhiteLootPixel(r, g, b));
        }

        private static LootSystem.WhiteComponent MakeFilledBlob(int width, int height)
            => MakeBlobAt(0, 0, width, height);

        private static LootSystem.WhiteComponent MakeBlobAt(int originX, int originY, int width, int height)
        {
            var component = new LootSystem.WhiteComponent();
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                    component.Add(originX + x, originY + y);
            }
            return component;
        }

        [Fact]
        public void ReferenceReaderUsesShadedWhiteClassifier()
        {
            using var bitmap = new Bitmap(2, 1);
            bitmap.SetPixel(0, 0, Color.FromArgb(180, 174, 169));
            bitmap.SetPixel(1, 0, Color.FromArgb(128, 128, 128));
            var points = new List<Point>();

            LootSystem.CollectTargetPointsFromBitmapReference(
                bitmap, 0, bitmap.Width, 0, bitmap.Height,
                pinkOnly: false, targetPoints: points, shadedWhiteOnly: true);

            Assert.Equal(new[] { new Point(0, 0) }, points);
        }

        [Theory]
        [InlineData(9, 20, false)]
        [InlineData(20, 9, false)]
        [InlineData(10, 10, true)]
        [InlineData(64, 64, true)]
        public void WhiteLootCandidatesRequireLargeFootprintWithoutUpperCap(
            int width, int height, bool expected)
        {
            Assert.Equal(expected, LootSystem.IsShadedWhiteLootCandidate(MakeFilledBlob(width, height)));
        }

        [Fact]
        public void LegacyCompactWhiteSparkleDetectionIsPreserved()
        {
            Assert.True(LootSystem.IsLegacyWhiteSparkleCandidate(MakeFilledBlob(4, 4)));
        }

        [Fact]
        public void WhiteLootCandidateRejectsSparseDiagonalNoise()
        {
            var diagonal = new LootSystem.WhiteComponent();
            for (int i = 0; i < 10; i++)
                diagonal.Add(i, i);

            Assert.False(LootSystem.IsShadedWhiteLootCandidate(diagonal));
        }

        [Fact]
        public void LargeWhiteComponentProbePlanCoversEveryMaskPixelWithSpatialSpread()
        {
            LootSystem.WhiteComponent component = MakeFilledBlob(20, 20);
            LootSystem.WhiteProbePlan plan = LootSystem.BuildWhiteProbePlan(component);
            var mask = new HashSet<Point>(component.Points);

            Assert.Equal(400, component.Area);
            Assert.Equal(400, plan.Points.Count);
            Assert.Equal(400, plan.Points.Distinct().Count());
            Assert.True(plan.Coverage.Count > 8,
                $"Stage A must not be limited to eight probes, was {plan.Coverage.Count}.");

            // Every generated mouseover point is an actual detected-white pixel.
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
        public void ProbePlannerContinuesPastEarlyMissesToALaterWhitePixel()
        {
            LootSystem.WhiteComponent component = MakeFilledBlob(20, 20);
            LootSystem.WhiteProbePlan plan = LootSystem.BuildWhiteProbePlan(component);
            Point expected = plan.Exhaustive[plan.Exhaustive.Count / 2];
            Assert.DoesNotContain(expected, plan.Coverage);

            var planner = LootSystem.LootProbePlanner.CreateForWhite(new[] { component }, alreadyProbed: null);
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
        public void MultipleWhiteComponentsAreProbedFairlyAndSurviveFreshCapture()
        {
            LootSystem.WhiteComponent large = MakeBlobAt(0, 0, 30, 30);
            LootSystem.WhiteComponent small = MakeBlobAt(80, 0, 10, 10);
            var candidates = new[] { large, small };

            var planner = LootSystem.LootProbePlanner.CreateForWhite(candidates, alreadyProbed: null);
            Assert.True(planner.TryNext(out int firstIndex, out _));
            Assert.True(planner.TryNext(out int secondIndex, out _));
            Assert.NotEqual(firstIndex, secondIndex);
            Assert.Equal(2, planner.CandidatesWithRemainingProbes);

            // The 900-pixel component cannot starve the 100-pixel component: the small
            // candidate's whole plan is consumed after at most two round-robin turns.
            var fairness = LootSystem.LootProbePlanner.CreateForWhite(candidates, alreadyProbed: null);
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
            var freshScan = LootSystem.LootProbePlanner.CreateForWhite(new[] { large }, alreadyProbed: null);
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
            LootSystem.WhiteComponent component = MakeFilledBlob(30, 30);
            const int budget = 50;
            var examined = new HashSet<Point>();

            var planner = LootSystem.LootProbePlanner.CreateForWhite(new[] { component }, examined);
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
            var continuation = LootSystem.LootProbePlanner.CreateForWhite(new[] { component }, examined);
            Assert.Equal(budget, continuation.PreviouslyProbedSkipped);
            Assert.Equal(component.Area - budget, continuation.InitialRemainingProbes);

            int continued = 0;
            while (continuation.TryNext(out _, out _))
                continued++;
            Assert.Equal(component.Area - budget, continued);
            Assert.Equal(LootSystem.LootScanPassResult.NoItem,
                LootSystem.ResolveProbeSweepResult(continuation.RemainingProbeCount));

            // Candidate geometry changes reset the continuation key.
            LootSystem.WhiteComponent shifted = MakeBlobAt(1, 0, 30, 30);
            Assert.NotEqual(
                LootSystem.BuildWhiteProbeProgressKey("BigScan", null, new[] { component }),
                LootSystem.BuildWhiteProbeProgressKey("BigScan", null, new[] { shifted }));
        }

        [Fact]
        public void DiagnosticCaptionSeparatesDetectedMaskFromProbePositions()
        {
            string caption = ScreenshotService.BuildLootScanCaption(
                "BigScan", "no-item",
                whiteCount: 400,
                exactWhiteCount: 300,
                shadedWhiteCount: 100,
                candidateComponents: 1,
                candidatePixelCount: 400,
                probeCount: 8,
                confirmedProbe: null,
                pixelLabel: "white");

            Assert.Contains("detected=400px", caption, StringComparison.Ordinal);
            Assert.Contains("exact=300", caption, StringComparison.Ordinal);
            Assert.Contains("shaded=100", caption, StringComparison.Ordinal);
            Assert.Contains("candidates=1 comps/400px", caption, StringComparison.Ordinal);
            Assert.Contains("probes=8", caption, StringComparison.Ordinal);
            Assert.DoesNotContain("detected=8px", caption, StringComparison.Ordinal);
        }
    }
}
