using System;
using System.Drawing;
using System.Reflection;
using DriverScanTester;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Regression tests for the SOD/SOP pink-pixel detection used by the
    /// MoveAndAttack loot mode.
    ///
    /// Normal loot is detected by pure-white pixels; SOD/SOP ground drops use a
    /// custom hot-pink scroll texture whose shading/anti-aliasing produces MANY
    /// pink shades (observed roughly R 239..255, G 21..109, B 148..255), so
    /// <see cref="LootSystem"/> classifies the hot-pink COLOR FAMILY — strong
    /// red, significant blue, substantially weaker green and clear magenta
    /// dominance — rather than one literal RGB value. Both the darker red/pink
    /// and the brighter magenta areas therefore qualify, while white sparkles,
    /// gray/stone floor, green terrain and red/yellow/blue/cyan effects are
    /// rejected. Blobs must still be at least 10x10 px (the rendered loot
    /// square) while smaller pink specks are skipped; the mouseover memory check
    /// remains authoritative for actually collecting a candidate. There is no
    /// upper size limit: the rendered square can be much larger than a few pixels.
    /// </summary>
    public sealed class LootPinkDetectionTests
    {
        private static bool IsPink(byte r, byte g, byte b)
            => LootSystem.IsSodSopPinkPixel(Color.FromArgb(255, r, g, b));

        [Theory]
        // Shades observed on the actual in-game custom scroll texture — the shaded
        // darker red/pink through the bright magenta highlights must ALL qualify.
        [InlineData(255, 24, 154, true)]
        [InlineData(255, 27, 163, true)]
        [InlineData(255, 34, 175, true)]   // typical central value
        [InlineData(255, 45, 192, true)]
        [InlineData(255, 61, 224, true)]
        [InlineData(255, 109, 255, true)]
        [InlineData(239, 21, 148, true)]   // dark shaded edge of the texture
        [InlineData(255, 20, 147, true)]   // old painted DeepPink — still in the family
        [InlineData(255, 0, 255, true)]    // pure magenta — same hot-pink family
        [InlineData(255, 105, 180, true)]  // hot pink
        [InlineData(200, 20, 180, true)]   // R/sum lower bounds exactly satisfied
        // Boundary values just outside the family — must be rejected.
        [InlineData(199, 20, 180, false)]  // R below 200
        [InlineData(255, 151, 255, false)] // G above 150
        [InlineData(255, 120, 119, false)] // B below 120
        [InlineData(229, 150, 255, false)] // R - G below 80
        [InlineData(255, 150, 189, false)] // B - G below 40
        [InlineData(200, 120, 179, false)] // R + B below 380
        [InlineData(255, 192, 203, false)] // classic pink (green too strong)
        [InlineData(140, 10, 80, false)]   // DeepPink at ~55% ground-light brightness
        // Not pink / wrong hue — must be rejected.
        [InlineData(255, 255, 255, false)] // white loot sparkle
        [InlineData(128, 128, 128, false)] // gray floor
        [InlineData(0, 255, 0, false)]     // pure green — old paint / grass
        [InlineData(60, 200, 60, false)]   // saturated grass green
        [InlineData(120, 220, 100, false)] // bright green with a warm tint
        [InlineData(110, 130, 130, false)] // teal — green barely above red
        [InlineData(255, 0, 0, false)]     // red
        [InlineData(0, 0, 255, false)]     // blue
        [InlineData(200, 180, 60, false)]  // yellow
        [InlineData(40, 150, 180, false)]  // cyan/blue
        [InlineData(150, 120, 150, false)] // muted purple-gray
        public void IsSodSopPinkPixel_MatchesHotPinkColorFamily(byte r, byte g, byte b, bool expected)
        {
            Assert.Equal(expected, IsPink(r, g, b));
        }

        [Fact]
        public void MutedTerrainColor_IsRejected()
        {
            Assert.False(IsPink(130, 110, 90));
        }

        private static LootSystem.WhiteComponent MakeBlob(int width, int height)
        {
            var component = new LootSystem.WhiteComponent();
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    component.Add(x, y);
                }
            }
            return component;
        }

        [Fact]
        public void PinkBlobAtMinimumSize_IsSodSopCandidate()
        {
            // 10x10 — exactly the minimum loot-square size.
            Assert.True(LootSystem.IsPinkLootCandidate(MakeBlob(10, 10)));
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(4, 4)]
        [InlineData(5, 5)]
        [InlineData(9, 9)]
        [InlineData(9, 10)]
        [InlineData(10, 9)]
        [InlineData(1, 10)]
        [InlineData(10, 1)]
        public void PinkBlobSmallerThanLootSquare_IsRejected(int width, int height)
        {
            // The SOD/SOP loot square is at least 10 px wide AND 10 px tall;
            // anything smaller is pink noise/effect speckle and must never be probed.
            Assert.False(LootSystem.IsPinkLootCandidate(MakeBlob(width, height)));
        }

        [Theory]
        [InlineData(10, 10)]
        [InlineData(10, 11)]
        [InlineData(11, 10)]
        public void PinkBlobAtLeastLootSquare_IsAccepted(int width, int height)
        {
            Assert.True(LootSystem.IsPinkLootCandidate(MakeBlob(width, height)));
        }

        [Fact]
        public void LargePinkSquare_IsAccepted()
        {
            // Regression: the 0.6x0.6 world square can render far larger than the
            // old 240 px / 48 px caps. There is no upper size limit anymore — the
            // pink-family pixel test plus the minimum-size filter keep background
            // noise out, and an area cap would silently filter out the real SOD/SOP
            // drop.
            Assert.True(LootSystem.IsPinkLootCandidate(MakeBlob(20, 20)));
            Assert.True(LootSystem.IsPinkLootCandidate(MakeBlob(64, 64)));
        }

        [Fact]
        public void LongPinkLine_IsRejected()
        {
            // 60 px wide but only 1 px tall: below the 10 px minimum height.
            Assert.False(LootSystem.IsPinkLootCandidate(MakeBlob(60, 1)));
        }

        [Fact]
        public void WideButFlatPinkShape_IsRejected()
        {
            Assert.False(LootSystem.IsPinkLootCandidate(MakeBlob(40, 4)));
        }

        [Fact]
        public void WhiteComponent_NestedType_IsInternalForTesting()
        {
            // Guards the InternalsVisibleTo test hook: if this type stops being
            // accessible the pink candidate tests above no longer compile/run.
            Type? nested = typeof(LootSystem).GetNestedType("WhiteComponent", BindingFlags.NonPublic);
            Assert.NotNull(nested);
        }

        // ── Artificial mob-marker magenta exclusion (pink-loot mask) ──

        /// <summary>
        /// The production connected-component labeler, fed through the pink-loot mask.
        /// Mirrors exactly what ScanRegion does in Phase A for pinkOnly=true.
        /// </summary>
        private static List<LootSystem.WhiteComponent> LabelPinkLootComponents(
            IEnumerable<(Point Point, Color Color)> pixels)
        {
            List<Point> maskPoints = pixels
                .Where(pixel => LootSystem.IsPinkLootMaskPixel(pixel.Color))
                .Select(pixel => pixel.Point)
                .ToList();
            return LabelRawComponents(maskPoints);
        }

        private static List<LootSystem.WhiteComponent> LabelRawComponents(IEnumerable<Point> pixels)
        {
            MethodInfo method = typeof(LootSystem).GetMethod(
                "LabelWhiteComponents", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("LootSystem.LabelWhiteComponents was not found.");
            return (List<LootSystem.WhiteComponent>)method.Invoke(
                null, new object[] { pixels.ToList() })!;
        }

        private static IEnumerable<(Point Point, Color Color)> RingPixels(int centerX, int centerY)
        {
            float radius = BotConstants.MobGrouping.ExpectedMarkerRadiusPx;
            var markerMagenta = Color.FromArgb(255, 0, 255);
            for (int angle = 0; angle < 360; angle += 2)
            {
                double radians = angle * Math.PI / 180.0;
                for (int thickness = -1; thickness <= 1; thickness++)
                {
                    int x = centerX + (int)Math.Round((radius + thickness) * Math.Cos(radians));
                    int y = centerY + (int)Math.Round((radius + thickness) * Math.Sin(radians));
                    yield return (new Point(x, y), markerMagenta);
                }
            }
        }

        [Fact]
        public void PinkLootMaskExcludesArtificialMarkerMagentaButKeepsTheSodSopFamily()
        {
            // Pure magenta belongs to the broad SOD/SOP pixel family ...
            Assert.True(IsPink(255, 0, 255));
            // ... but the narrow artificial marker predicate removes it from the
            // pink-loot connected-component mask before labeling.
            Assert.False(LootSystem.IsPinkLootMaskPixel(Color.FromArgb(255, 0, 255)));
            Assert.False(LootSystem.IsPinkLootMaskPixel(Color.FromArgb(230, 20, 232)));

            // Ordinary SOD/SOP texture shades stay detectable.
            Assert.True(LootSystem.IsPinkLootMaskPixel(Color.FromArgb(255, 34, 175)));
            Assert.True(LootSystem.IsPinkLootMaskPixel(Color.FromArgb(239, 21, 148)));
            Assert.True(LootSystem.IsPinkLootMaskPixel(Color.FromArgb(255, 109, 255)));
            Assert.True(LootSystem.IsPinkLootMaskPixel(Color.FromArgb(255, 45, 192)));
        }

        [Fact]
        public void SingleAndTouchingArtificialMarkerRingsProduceNoPinkLootComponent()
        {
            var ringLayouts = new[]
            {
                new[] { (X: 50, Y: 50) },
                new[] { (X: 50, Y: 50), (X: 84, Y: 50) }
            };

            foreach (var layout in ringLayouts)
            {
                var pixels = layout
                    .SelectMany(center => RingPixels(center.X, center.Y))
                    .ToList();
                Assert.NotEmpty(pixels);
                Assert.Empty(LabelPinkLootComponents(pixels));
            }
        }

        [Fact]
        public void WideOverlappingArtificialMarkerRingStructureProducesNoPinkLootComponent()
        {
            var centers = new[]
            {
                (X: 50, Y: 50), (X: 76, Y: 50), (X: 63, Y: 73),
                (X: 89, Y: 73), (X: 63, Y: 27), (X: 89, Y: 27)
            };
            var pixels = centers
                .SelectMany(center => RingPixels(center.X, center.Y))
                .ToList();

            // The raw magenta structure merges into one wide connected component —
            // the exact shape that previously could slip through IsMobMarkerRingComponent.
            List<LootSystem.WhiteComponent> raw = LabelRawComponents(pixels.Select(pixel => pixel.Point));
            Assert.Contains(raw, component => component.Width > 40);

            // Filtering through the production pink-loot mask removes every marker pixel,
            // so no component (wide or not) can ever become a pink-loot candidate.
            Assert.Empty(LabelPinkLootComponents(pixels));
        }

        [Fact]
        public void SodSopPinkSquareTouchingArtificialMarkerStillLeavesPinkCandidates()
        {
            var pixels = new List<(Point Point, Color Color)>();
            for (int x = 40; x < 64; x++)
            {
                for (int y = 40; y < 64; y++)
                    pixels.Add((new Point(x, y), Color.FromArgb(255, 34, 175)));
            }

            // A marker ring overlaps the pink square; only the ring pixels are removed.
            pixels.AddRange(RingPixels(52, 52));

            List<LootSystem.WhiteComponent> components = LabelPinkLootComponents(pixels);
            Assert.Contains(components, LootSystem.IsPinkLootCandidate);
        }

        [Fact]
        public void SodSopPinkSquareWithoutMarkerRemainsAFullPinkLootCandidate()
        {
            var pixels = new List<(Point Point, Color Color)>();
            for (int x = 0; x < 12; x++)
            {
                for (int y = 0; y < 12; y++)
                    pixels.Add((new Point(x, y), Color.FromArgb(255, 34, 175)));
            }

            List<LootSystem.WhiteComponent> components = LabelPinkLootComponents(pixels);
            LootSystem.WhiteComponent component = Assert.Single(components);
            Assert.True(LootSystem.IsPinkLootCandidate(component));
        }
    }
}
