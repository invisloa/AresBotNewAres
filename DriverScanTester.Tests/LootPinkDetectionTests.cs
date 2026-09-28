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
    /// Normal loot is detected by pure-white pixels; SOD/SOP ground drops are
    /// painted with a solid opaque DeepPink texture by the client mod
    /// (tools/scroll_pink_square.py: PINK = BGRA(147, 20, 255, 255)).
    /// <see cref="LootSystem"/> must match that color EXACTLY — no ranges — so
    /// white sparkles, gray/stone floor, green terrain and red/yellow/blue/cyan
    /// effects never match, and it must keep blobs that are at least 10x10 px
    /// (the painted loot square) while skipping smaller pink specks. There is no
    /// upper size limit: the rendered square can be much larger than a few pixels.
    /// </summary>
    public sealed class LootPinkDetectionTests
    {
        private static bool IsPink(byte r, byte g, byte b)
            => LootSystem.IsSodSopPinkPixel(Color.FromArgb(255, r, g, b));

        [Theory]
        // Only the exact painted texture color is a SOD/SOP candidate.
        [InlineData(255, 20, 147, true)]   // DeepPink (RGB 255,20,147) — exact painted color
        // Off-by-one shades are NOT the painted color and must be rejected.
        [InlineData(254, 20, 147, false)]
        [InlineData(255, 21, 147, false)]
        [InlineData(255, 20, 146, false)]
        [InlineData(255, 19, 148, false)]
        [InlineData(255, 0, 255, false)]   // pure magenta — not the SOD/SOP color
        [InlineData(255, 105, 180, false)] // hot pink
        [InlineData(255, 192, 203, false)] // classic pink
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
        public void IsSodSopPinkPixel_MatchesOnlyExactPaintedColor(byte r, byte g, byte b, bool expected)
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
            // exact-color pixel test already rules out terrain and AoE effects, and
            // an area cap would silently filter out the real SOD/SOP drop.
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
    }
}
