using System.Drawing;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class LootNearestFirstTests
    {
        [Theory]
        [InlineData(1000, 700, false)]
        [InlineData(1400, 900, false)]
        [InlineData(1000, 700, true)]
        public void ProductionCandidates_ArePinkFirstThenNearestToFullClientCenter(
            int width, int height, bool pinkOnly)
        {
            var mode = pinkOnly ? LootSystem.LootScanMode.PinkOnly : LootSystem.LootScanMode.NormalCombined;
            using var bitmap = new Bitmap(width, height);
            int centerX = width / 2, centerY = height / 2;
            // Deliberately not in distance or left-to-right order.
            var normal = Blob(centerX - 200, centerY).Concat(Blob(centerX, centerY))
                .Concat(Blob(width - 50, 20)).ToList();
            var pink = Blob(20, 20).Concat(Blob(centerX + 100, centerY + 100)).ToList();
            var stats = new LootSystem.PixelCollectionStats(width * height, pink.Count, 0, pink.Count);
            var candidates = LootSystem.AnalyzeLootCandidateMasks(bitmap, normal, pink, stats, mode,
                out _, out _, out _, out _);

            Assert.Equal(mode == LootSystem.LootScanMode.PinkOnly ? 2 : 5, candidates.Count);
            Assert.Equal(LootSystem.LootCandidateKind.SodSopPink, candidates[0].Kind);
            Assert.Equal(centerX + 100, candidates[0].Component.XMin);
            Assert.Equal(LootSystem.LootCandidateKind.SodSopPink, candidates[1].Kind);
            Assert.Equal(20, candidates[1].Component.XMin);
            if (mode == LootSystem.LootScanMode.NormalCombined)
            {
                Assert.Equal(centerX, candidates[2].Component.XMin);
                Assert.Equal(centerX - 200, candidates[3].Component.XMin);
                Assert.Equal(width - 50, candidates[4].Component.XMin);
                Assert.All(candidates.Skip(2), c => Assert.Equal(LootSystem.LootCandidateKind.NormalAquamarine, c.Kind));
            }

            var planner = mode == LootSystem.LootScanMode.PinkOnly
                ? LootSystem.LootProbePlanner.CreateForPink(candidates.Select(c => c.Component).ToList())
                : LootSystem.LootProbePlanner.CreateForCombined(candidates, alreadyProbed: null);
            for (int expectedIndex = 0; expectedIndex < candidates.Count; expectedIndex++)
            {
                for (int probe = 0; probe < candidates[expectedIndex].Component.Area; probe++)
                {
                    Assert.True(planner.TryNext(out int index, out _));
                    Assert.Equal(expectedIndex, index);
                }
            }
            Assert.False(planner.TryNext(out _, out _));
        }

        [Fact]
        public void DistanceRanking_UsesBothAxesAndDeterministicTieBreaks()
        {
            var near = Component(494, 344);
            var farBelow = Component(494, 600);
            var left = Component(394, 344);
            var right = Component(594, 344);
            Assert.True(LootSystem.CompareLootDistanceToPlayer(near, farBelow, 1000, 700) < 0);
            Assert.True(LootSystem.CompareLootDistanceToPlayer(left, right, 1000, 700) < 0);
            Assert.Equal(0, LootSystem.CompareLootDistanceToPlayer(near, near, 1000, 700));
        }

        [Fact]
        public void BudgetContinuation_FinishesNearestCandidateBeforeTryingFartherCandidate()
        {
            var nearest = Component(494, 344);
            var farther = Component(100, 100);
            var candidates = new[]
            {
                new LootSystem.TypedLootCandidate(nearest, LootSystem.LootCandidateKind.NormalAquamarine),
                new LootSystem.TypedLootCandidate(farther, LootSystem.LootCandidateKind.NormalAquamarine)
            };
            var examined = new HashSet<Point>();
            var first = LootSystem.LootProbePlanner.CreateForCombined(candidates, examined);
            for (int i = 0; i < 50; i++)
            {
                Assert.True(first.TryNext(out int index, out Point point));
                Assert.Equal(0, index);
                examined.Add(point);
            }
            Assert.Equal(LootSystem.LootScanPassResult.Incomplete,
                LootSystem.ResolveProbeSweepResult(first.RemainingProbeCount));
            var continuation = LootSystem.LootProbePlanner.CreateForCombined(candidates, examined);
            Assert.Equal(50, continuation.PreviouslyProbedSkipped);
            for (int i = 0; i < nearest.Area - 50; i++)
            {
                Assert.True(continuation.TryNext(out int index, out Point point));
                Assert.Equal(0, index);
                Assert.DoesNotContain(point, examined);
            }
            Assert.True(continuation.TryNext(out int fartherIndex, out _));
            Assert.Equal(1, fartherIndex);
        }

        private static List<Point> Blob(int x, int y) => Enumerable.Range(y, 12)
            .SelectMany(row => Enumerable.Range(x, 12).Select(column => new Point(column, row))).ToList();

        private static LootSystem.LootComponent Component(int x, int y)
        {
            var component = new LootSystem.LootComponent();
            foreach (Point point in Blob(x, y))
                component.Add(point.X, point.Y);
            return component;
        }
    }
}
