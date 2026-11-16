using System;
using System.Drawing;
using System.Drawing.Imaging;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class PlayerMarkerPixelDetectorTests
    {
        private static readonly Rectangle LootCharacterExclusionArea = new(430, 315, 161, 171);

        [Theory]
        [InlineData(255, 255, 0, true)]
        [InlineData(0, 255, 0, true)]
        [InlineData(255, 20, 147, false)]
        [InlineData(255, 255, 255, false)]
        [InlineData(80, 180, 60, false)]
        public void ClassifiesConfiguredFactionMarkerColors(int r, int g, int b, bool expected)
        {
            Assert.Equal(expected,
                PlayerMarkerPixelDetector.ClassifyPlayerMarkerPixel(r, g, b) != 0);
        }

        /// <summary>
        /// The verified Empire ground marker is a banner decal: a wide horizontal band crossed
        /// by a much narrower vertical stem (real client frames measure 19x21 px and 33x30 px).
        /// Solid square/diamond "markers" are NOT accepted any more: every yellow game object
        /// (dropped-item gems such as "Ykshali"/"Supakna", monster bodies, spell effects, HUD
        /// glyphs) is rendered in exactly the same saturated yellow, so a solid blob is not
        /// evidence of another player.
        /// </summary>
        [Fact]
        public void DetectsEmpireBannerMarkerAwayFromLocalPlayer()
        {
            using Bitmap frame = CreateFrame(1024, 768);
            FillBannerMarker(frame, x: 400, y: 220, width: 19, height: 21,
                Color.FromArgb(255, 255, 0));

            var detector = new PlayerMarkerPixelDetector();

            Assert.True(detector.ContainsOtherPlayerMarker(frame, new Rectangle(430, 315, 161, 171)));
        }

        [Fact]
        public void RejectsSolidYellowSquareBecauseYellowGameObjectsLookLikeThis()
        {
            using Bitmap frame = CreateFrame();
            FillRectangle(frame, new Rectangle(210, 90, 14, 14), Color.FromArgb(255, 255, 0));

            // 2026-11-04 false positive: a pure-yellow square inside a spell effect.
            Assert.False(new PlayerMarkerPixelDetector().ContainsOtherPlayerMarker(
                frame, new Rectangle(130, 130, 40, 40)));
        }

        [Fact]
        public void DetectsAllianceGreenTriangleAwayFromLocalPlayer()
        {
            using Bitmap frame = CreateFrame();
            FillTriangle(frame, x: 205, y: 80, size: 18, Color.FromArgb(0, 255, 0));

            var detector = new PlayerMarkerPixelDetector();

            Assert.True(detector.ContainsOtherPlayerMarker(frame, new Rectangle(130, 130, 40, 40)));
        }

        [Fact]
        public void DetectsAllianceTriangleProjectedDiagonallyByTiltedCamera()
        {
            using Bitmap frame = CreateFrame();
            FillProjectedTriangle(frame, centerX: 210, centerY: 90, length: 18, halfBase: 8,
                angleDegrees: 45, Color.FromArgb(0, 255, 0));

            Assert.True(new PlayerMarkerPixelDetector().ContainsOtherPlayerMarker(
                frame, new Rectangle(130, 130, 40, 40)));
        }

        /// <summary>
        /// 2026-11-15 false positive: the dropped item "Ykshali" is a large solid yellow diamond.
        /// It is big, solid, faction-colored and completely indistinguishable from a "diamond"
        /// marker by color/size/shape — which is why the loosened dense-blob test was wrong.
        /// </summary>
        [Fact]
        public void RejectsSolidYellowDiamondGroundItem()
        {
            using Bitmap frame = CreateFrame();
            FillDiamond(frame, centerX: 210, centerY: 90, radius: 9, Color.FromArgb(255, 255, 0));

            Assert.False(new PlayerMarkerPixelDetector().ContainsOtherPlayerMarker(
                frame, new Rectangle(130, 130, 40, 40)));
        }

        [Fact]
        public void RejectsSolidYellowDropItemDomeNearTheCharacter()
        {
            // Silhouette of the real dropped-item models (57x66 "Ykshali", 42x51 "Supakna"):
            // a smooth vertical dome with no narrow stem.
            using Bitmap frame = CreateFrame(1024, 768);
            PointF anchor = PlayerMarkerPixelDetector.GetCombatLocalCharacterAnchor(frame.Width, frame.Height);
            float radius = PlayerMarkerPixelDetector.GetCombatLocalCharacterExclusionRadius(frame.Height);

            int centerX = (int)Math.Round(anchor.X) - 110;
            int centerY = (int)Math.Round(anchor.Y) - 70;
            FillDome(frame, centerX, centerY, width: 42, height: 51, Color.FromArgb(255, 255, 0));

            Assert.False(new PlayerMarkerPixelDetector().TryFindOtherPlayerMarker(
                frame, Rectangle.Empty, anchor, radius, allowClassEmblems: true, out _));
        }

        [Fact]
        public void RejectsYellowChatGlyphThatPreviouslyPassedTheDetector()
        {
            using Bitmap frame = CreateFrame();
            int pixels = FillCompactHChatGlyph(frame, x: 100, y: 100, width: 6, height: 9,
                Color.FromArgb(255, 255, 0));
            float fillRatio = pixels / (float)(6 * 9);
            float aspectRatio = 9f / 6f;

            // This compact, thick glyph passed the old 12-pixel / 4-pixel / 0.58 filters.
            Assert.InRange(pixels, 25, 45);
            Assert.True(fillRatio > 0.58f);
            Assert.True(aspectRatio <= 1.7f);
            Assert.False(new PlayerMarkerPixelDetector().ContainsOtherPlayerMarker(frame, Rectangle.Empty));
        }

        [Fact]
        public void RejectsCompactGreenChatGlyphAtSmallClientHeight()
        {
            using Bitmap frame = CreateFrame();
            FillCompactHChatGlyph(frame, x: 100, y: 100, width: 9, height: 7,
                Color.FromArgb(0, 255, 0));

            // The absolute size/pixel floors must not shrink at this 240px client height.
            Assert.False(new PlayerMarkerPixelDetector().ContainsOtherPlayerMarker(frame, Rectangle.Empty));
        }

        [Fact]
        public void RejectsExactGreenQButtonComponentsFromFalsePositiveScreenshot()
        {
            using Bitmap frame = CreateFrame(1024, 768);
            Color green = Color.FromArgb(0, 255, 0);
            FillPixelPattern(frame, 18, 610,
                new[] { "...#.", "..###", ".####", "###..", ".#...", "##...", "#....", "#...." }, green);
            FillPixelPattern(frame, 25, 629,
                new[] { "###....", ".####..", ".##..##", "......#" }, green);

            Rectangle[] hudAreas = PlayerMarkerPixelDetector.GetHudExclusionAreas(frame.Width, frame.Height);
            Assert.Contains(hudAreas, area => area.IntersectsWith(new Rectangle(18, 610, 5, 8)));
            Assert.Contains(hudAreas, area => area.IntersectsWith(new Rectangle(25, 629, 7, 4)));
            Assert.False(new PlayerMarkerPixelDetector().ContainsOtherPlayerMarker(frame, Rectangle.Empty));
        }

        [Fact]
        public void RejectsPlayerSizedTriangleInsideQButtonHudRegion()
        {
            using Bitmap frame = CreateFrame(1024, 768);
            FillTriangle(frame, x: 12, y: 600, size: 18, Color.FromArgb(0, 255, 0));

            Assert.False(new PlayerMarkerPixelDetector().ContainsOtherPlayerMarker(frame, Rectangle.Empty));
        }

        [Fact]
        public void RejectsGreenSquareOutsideHudBecauseAllianceMarkerMustBeTriangular()
        {
            using Bitmap frame = CreateFrame(1024, 768);
            FillRectangle(frame, new Rectangle(200, 300, 14, 14), Color.FromArgb(0, 255, 0));

            Assert.False(new PlayerMarkerPixelDetector().ContainsOtherPlayerMarker(frame, Rectangle.Empty));
        }

        /// <summary>
        /// 2026-10-29 incident fixture: the other player's Alliance/green marker stood ~89 px
        /// from the local character anchor, with its nearest pixel only ~57 px away. The legacy
        /// loot exclusion rectangle (160x170 px) hid it; the compact combat radius must detect it.
        /// </summary>
        /// <summary>
        /// The REAL 2026-10-29 death frame: the adjacent other player ("Gissel", Alliance) was
        /// rendered with a large green CLASS EMBLEM (shield silhouette) above the character, not
        /// with the plain triangular ground marker. Combat-time detection must find it; the
        /// legacy loot exclusion rectangle hides it (root cause) and the strict loot shape
        /// validation rejects it.
        /// </summary>
        [Fact]
        public void RealIncidentFrame_CombatScanFindsTheOtherPlayerClassEmblem()
        {
            string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "incident-2026-10-29_death.png");
            Assert.True(File.Exists(fixturePath),
                $"incident fixture missing: {fixturePath} (check the test project's fixture item)");

            using Bitmap frame = new Bitmap(fixturePath);
            PointF anchor = PlayerMarkerPixelDetector.GetCombatLocalCharacterAnchor(frame.Width, frame.Height);
            float radius = PlayerMarkerPixelDetector.GetCombatLocalCharacterExclusionRadius(frame.Height);

            bool combatFound = new PlayerMarkerPixelDetector().TryFindOtherPlayerMarker(
                frame, Rectangle.Empty, anchor, radius, allowClassEmblems: true,
                out PlayerMarkerCandidate candidate);

            Assert.True(combatFound, "the combat-time scan must detect the other player's class emblem");
            Assert.Equal("Alliance/green", candidate.Faction);
            Assert.Equal(new Rectangle(429, 296, 41, 51), candidate.Bounds);
            Assert.True(candidate.Center.X < anchor.X && candidate.Center.Y < anchor.Y,
                "the emblem belongs to the other player up-left of the character anchor");

            // Root cause evidence: the legacy loot exclusion rectangle (431,315,161,171) covers
            // this marker, so the pre-fix attack-start check could not see the player.
            Assert.False(new PlayerMarkerPixelDetector().TryFindOtherPlayerMarker(
                frame, new Rectangle(431, 315, 161, 171), out _));

            // The strict loot shape validation still rejects the emblem (loot behavior unchanged).
            Assert.False(new PlayerMarkerPixelDetector().TryFindOtherPlayerMarker(
                frame, Rectangle.Empty, anchor, radius, allowClassEmblems: false, out _));

            // The yellow dropped item ("Ykshali", 57x66 solid diamond) in the same frame must
            // never be mistaken for an Empire player.
            using Bitmap yellowOnly = new(fixturePath);
            using (Graphics graphics = Graphics.FromImage(yellowOnly))
            using (var brush = new SolidBrush(Color.Black))
                graphics.FillRectangle(brush, 429, 296, 41, 51);
            Assert.False(new PlayerMarkerPixelDetector().TryFindOtherPlayerMarker(
                yellowOnly, Rectangle.Empty, anchor, radius, allowClassEmblems: true, out _));
        }

        /// <summary>
        /// Regression for the 2026-11-04..16 false "other player detected" reports. Every saved
        /// screenshot of that period shows a yellow GAME OBJECT (dropped-item gems such as
        /// "Supakna"/"Ykshali"/"Pewter", monster bodies such as "[PRIME] Thread Weaver" and
        /// "Nanima", spell effects and even HUD glyphs) sitting next to the character — no other
        /// player at all. The old color+size+dense-blob test accepted them, which stopped the bot
        /// and blocked attacks. None of these frames may produce a player marker again.
        /// </summary>
        [Theory]
        [InlineData("Test-2026-11-16_13-05-57-247.png")]
        [InlineData("Test-2026-11-15_19-40-47-337.png")]
        [InlineData("Test-2026-11-15_15-39-34-244.png")]
        [InlineData("Test-2026-11-15_10-58-49-934.png")]
        [InlineData("Test-2026-11-10_17-05-12-004.png")]
        [InlineData("Test-2026-11-10_16-51-50-739.png")]
        [InlineData("Test-2026-11-10_16-54-21-171.png")]
        [InlineData("Test-2026-11-09_02-53-38-252.png")]
        [InlineData("Test-2026-11-04_01-46-57-441.png")]
        public void RealFalsePositiveFrame_ProducesNoPlayerMarker(string fixtureName)
        {
            string fixturePath = Path.Combine(
                AppContext.BaseDirectory, "Fixtures", "PlayerPixels", fixtureName);
            Assert.True(File.Exists(fixturePath),
                $"false-positive fixture missing: {fixturePath} (check the test project's fixture items)");

            using Bitmap frame = new(fixturePath);
            PointF anchor = PlayerMarkerPixelDetector.GetCombatLocalCharacterAnchor(frame.Width, frame.Height);
            float radius = PlayerMarkerPixelDetector.GetCombatLocalCharacterExclusionRadius(frame.Height);
            var detector = new PlayerMarkerPixelDetector();

            Assert.False(detector.TryFindOtherPlayerMarker(
                    frame, Rectangle.Empty, anchor, radius, allowClassEmblems: true,
                    out PlayerMarkerCandidate combatCandidate),
                $"combat scan must not report a player: {combatCandidate.Faction} {combatCandidate.Bounds}");
            Assert.False(detector.TryFindOtherPlayerMarker(
                    frame, new Rectangle(430, 315, 161, 171), out PlayerMarkerCandidate lootCandidate),
                $"loot scan must not report a player: {lootCandidate.Faction} {lootCandidate.Bounds}");
        }

        [Fact]
        public void CombatScanAcceptsAVerifiedClassEmblemShape()
        {
            using Bitmap frame = CreateFrame(1024, 768);
            PointF anchor = PlayerMarkerPixelDetector.GetCombatLocalCharacterAnchor(1024, 768);
            float radius = PlayerMarkerPixelDetector.GetCombatLocalCharacterExclusionRadius(768);

            int centerX = (int)Math.Round(anchor.X) - 63;
            int centerY = (int)Math.Round(anchor.Y) - 63;
            FillClassEmblem(frame, centerX - 20, centerY - 25, Color.FromArgb(0, 255, 0));

            bool combatFound = new PlayerMarkerPixelDetector().TryFindOtherPlayerMarker(
                frame, Rectangle.Empty, anchor, radius, allowClassEmblems: true, out _);

            Assert.True(combatFound);
        }

        [Fact]
        public void CombatScanRejectsATinyYellowMonsterFragmentFromThe20261116Frame()
        {
            // 2026-11-16 false positive: a 9x10 px yellow sliver of the "[PRIME] Thread Weaver"
            // monster model was accepted as an Empire marker because the size floors were tiny
            // (60 px / 10 px). Monster bodies and drop items keep producing such slivers.
            using Bitmap frame = CreateFrame(1024, 768);
            PointF anchor = PlayerMarkerPixelDetector.GetCombatLocalCharacterAnchor(frame.Width, frame.Height);
            float radius = PlayerMarkerPixelDetector.GetCombatLocalCharacterExclusionRadius(frame.Height);

            int x = (int)Math.Round(anchor.X) - 116;
            int y = (int)Math.Round(anchor.Y) + 95;
            FillDiamond(frame, centerX: x, centerY: y, radius: 6, Color.FromArgb(255, 255, 0));

            Assert.False(new PlayerMarkerPixelDetector().TryFindOtherPlayerMarker(
                frame, Rectangle.Empty, anchor, radius, allowClassEmblems: true, out _));
        }

        [Fact]
        public void CombatScanRejectsAHollowColoredRingAsAnEmblem()
        {
            using Bitmap frame = CreateFrame(1024, 768);
            PointF anchor = PlayerMarkerPixelDetector.GetCombatLocalCharacterAnchor(1024, 768);
            float radius = PlayerMarkerPixelDetector.GetCombatLocalCharacterExclusionRadius(768);

            int centerX = (int)Math.Round(anchor.X) - 63;
            int centerY = (int)Math.Round(anchor.Y) - 63;
            using (Graphics graphics = Graphics.FromImage(frame))
            using (var pen = new Pen(Color.FromArgb(0, 255, 0), 2f))
            {
                graphics.DrawEllipse(pen, centerX - 20, centerY - 25, 40, 50);
            }

            bool found = new PlayerMarkerPixelDetector().TryFindOtherPlayerMarker(
                frame, Rectangle.Empty, anchor, radius, allowClassEmblems: true, out _);

            Assert.False(found);
        }

        [Fact]
        public void CombatExclusionDetectsTheAdjacentOtherPlayerMarkerFromTheIncident()
        {
            using Bitmap frame = CreateFrame(1024, 768);
            PointF anchor = PlayerMarkerPixelDetector.GetCombatLocalCharacterAnchor(1024, 768);
            float radius = PlayerMarkerPixelDetector.GetCombatLocalCharacterExclusionRadius(768);

            int centerX = (int)Math.Round(anchor.X) - 63;
            int centerY = (int)Math.Round(anchor.Y) - 63;
            FillTriangle(frame, x: centerX - 20, y: centerY - 20, size: 41, Color.FromArgb(0, 255, 0));

            var detector = new PlayerMarkerPixelDetector();
            bool found = detector.TryFindOtherPlayerMarker(
                frame, Rectangle.Empty, anchor, radius, allowClassEmblems: true,
                out PlayerMarkerCandidate candidate);

            Assert.True(found);
            float distance = MathF.Sqrt(
                MathF.Pow(candidate.Center.X - anchor.X, 2) +
                MathF.Pow(candidate.Center.Y - anchor.Y, 2));
            Assert.True(distance > radius,
                $"candidate must be outside the compact own-marker radius ({distance:F1} vs {radius:F1})");
        }

        [Fact]
        public void CombatExclusionStillIgnoresTheLocalPlayersOwnMarker()
        {
            using Bitmap frame = CreateFrame(1024, 768);
            PointF anchor = PlayerMarkerPixelDetector.GetCombatLocalCharacterAnchor(1024, 768);
            float radius = PlayerMarkerPixelDetector.GetCombatLocalCharacterExclusionRadius(768);

            FillTriangle(frame,
                x: (int)Math.Round(anchor.X) - 12,
                y: (int)Math.Round(anchor.Y) - 12,
                size: 24,
                Color.FromArgb(0, 255, 0));

            var detector = new PlayerMarkerPixelDetector();
            bool found = detector.TryFindOtherPlayerMarker(
                frame, Rectangle.Empty, anchor, radius, allowClassEmblems: true, out _);

            Assert.False(found);
        }

        [Fact]
        public void LootExclusionRectangleStillHidesTheAdjacentMarker()
        {
            // Documents the root cause: the legacy loot rectangle (translated into this frame)
            // covers the adjacent player's marker, which is why combat safety needs the compact
            // radius instead of the loot exclusion.
            using Bitmap frame = CreateFrame(1024, 768);
            PointF anchor = PlayerMarkerPixelDetector.GetCombatLocalCharacterAnchor(1024, 768);
            int centerX = (int)Math.Round(anchor.X) - 63;
            int centerY = (int)Math.Round(anchor.Y) - 63;
            FillTriangle(frame, x: centerX - 20, y: centerY - 20, size: 41, Color.FromArgb(0, 255, 0));

            Rectangle legacyExclusion = new(431, 315, 161, 171);
            Assert.False(new PlayerMarkerPixelDetector().TryFindOtherPlayerMarker(
                frame, legacyExclusion, out _));
        }

        [Fact]
        public void DetectsOccludedEmpireMarkerMatchingLiveRendering()
        {
            using Bitmap frame = CreateFrame(1024, 768);
            FillOccludedEmpireMarker(frame, x: 338, y: 154);

            bool found = new PlayerMarkerPixelDetector().TryFindOtherPlayerMarker(
                frame, LootCharacterExclusionArea, out PlayerMarkerCandidate candidate);

            Assert.True(found);
            Assert.Equal("Empire/yellow", candidate.Faction);
            Assert.Equal(new Rectangle(338, 154, 33, 30), candidate.Bounds);
            Assert.Equal(430, candidate.PixelCount);
            Assert.InRange(candidate.FillRatio, 0.43f, 0.44f);
            Assert.True(candidate.FillRatio < 0.58f);
        }

        [Fact]
        public void RealMarkerWinsWhileYellowChatGlyphsAreRejected()
        {
            using Bitmap frame = CreateFrame(1024, 768);
            FillOccludedEmpireMarker(frame, x: 338, y: 154);
            FillBottomChatGlyphs(frame);

            bool found = new PlayerMarkerPixelDetector().TryFindOtherPlayerMarker(
                frame, LootCharacterExclusionArea, out PlayerMarkerCandidate candidate);

            Assert.True(found);
            Assert.Equal("Empire/yellow", candidate.Faction);
            Assert.Equal(new Rectangle(338, 154, 33, 30), candidate.Bounds);
            Assert.True(candidate.Center.X is >= 338 and < 371);
            Assert.True(candidate.Center.Y is >= 154 and < 184);
            Assert.True(candidate.Bounds.Bottom < 300, "The accepted component must be the marker, not a bottom chat glyph.");
        }

        [Fact]
        public void ChatOnlyYellowGlyphsDoNotProduceAPlayerMarker()
        {
            using Bitmap frame = CreateFrame(1024, 768);
            FillBottomChatGlyphs(frame);

            Assert.False(new PlayerMarkerPixelDetector().ContainsOtherPlayerMarker(
                frame, LootCharacterExclusionArea));
        }

        [Fact]
        public void RealisticLocalEmpireMarkerIsAcceptedByShapeButExcludedByLootArea()
        {
            using Bitmap frame = CreateFrame(1024, 768);
            FillRealisticLocalEmpireMarker(frame, x: 501, y: 370);
            var detector = new PlayerMarkerPixelDetector();

            Assert.True(detector.TryFindOtherPlayerMarker(frame, Rectangle.Empty, out PlayerMarkerCandidate ownCandidate));
            Assert.Equal(new Rectangle(501, 370, 19, 21), ownCandidate.Bounds);
            Assert.Equal(164, ownCandidate.PixelCount);
            Assert.InRange(ownCandidate.FillRatio, 0.40f, 0.42f);
            Assert.False(detector.ContainsOtherPlayerMarker(frame, LootCharacterExclusionArea));
        }

        [Fact]
        public void LootCharacterExclusionAreaMatchesLootScannerReferenceCoordinates()
        {
            Rectangle area = PlayerMarkerPixelDetector.GetLootCharacterExclusionArea(
                1024, 768, referenceClientOriginX: 450, referenceClientOriginY: 103);

            Assert.Equal(new Rectangle(430, 315, 161, 171), area);
        }

        [Fact]
        public void RejectsThinGreenTextLikeComponent()
        {
            using Bitmap frame = CreateFrame();
            FillRectangle(frame, new Rectangle(180, 100, 24, 3), Color.FromArgb(0, 255, 0));

            var detector = new PlayerMarkerPixelDetector();

            Assert.False(detector.ContainsOtherPlayerMarker(frame, new Rectangle(80, 100, 40, 40)));
        }

        private static Bitmap CreateFrame(int width = 320, int height = 240)
            => new(width, height, PixelFormat.Format32bppArgb);

        private static void FillRectangle(Bitmap bitmap, Rectangle rectangle, Color color)
        {
            for (int y = rectangle.Top; y < rectangle.Bottom; y++)
            for (int x = rectangle.Left; x < rectangle.Right; x++)
                bitmap.SetPixel(x, y, color);
        }

        /// <summary>
        /// Draws the verified class-emblem silhouette: a wide upper body with a solid,
        /// tapering lower half (the 2026-10-29 other player's marker).
        /// </summary>
        private static void FillClassEmblem(Bitmap bitmap, int x, int y, Color color)
        {
            // Upper flag/body: 40 px wide, 20 px tall.
            for (int row = 0; row < 20; row++)
            for (int px = x; px < x + 40; px++)
                bitmap.SetPixel(px, y + row, color);

            // Solid tapering lower body: 31 rows narrowing from 34 px to 4 px.
            for (int row = 0; row < 31; row++)
            {
                int width = 34 - (row * 30 / 30);
                int left = x + (40 - width) / 2;
                for (int px = left; px < left + width; px++)
                    bitmap.SetPixel(px, y + 20 + row, color);
            }
        }

        private static void FillTriangle(Bitmap bitmap, int x, int y, int size, Color color)
        {
            int centerX = x + size / 2;
            for (int row = 0; row < size; row++)
            {
                int halfWidth = (int)Math.Round(row * (size / 2.0) / (size - 1));
                for (int px = centerX - halfWidth; px <= centerX + halfWidth; px++)
                    bitmap.SetPixel(px, y + row, color);
            }
        }

        private static void FillProjectedTriangle(
            Bitmap bitmap,
            int centerX,
            int centerY,
            int length,
            int halfBase,
            double angleDegrees,
            Color color)
        {
            double angle = angleDegrees * Math.PI / 180.0;
            double axisX = Math.Cos(angle);
            double axisY = Math.Sin(angle);
            double crossX = -axisY;
            double crossY = axisX;
            int extent = length + halfBase;
            for (int y = centerY - extent; y <= centerY + extent; y++)
            for (int x = centerX - extent; x <= centerX + extent; x++)
            {
                double dx = x - centerX;
                double dy = y - centerY;
                double along = dx * axisX + dy * axisY;
                if (along < -length / 2.0 || along > length / 2.0)
                    continue;
                double across = Math.Abs(dx * crossX + dy * crossY);
                double allowedHalfWidth = halfBase * (along + length / 2.0) / length;
                if (across <= allowedHalfWidth)
                    bitmap.SetPixel(x, y, color);
            }
        }

        private static void FillDiamond(Bitmap bitmap, int centerX, int centerY, int radius, Color color)
        {
            for (int y = centerY - radius; y <= centerY + radius; y++)
            for (int x = centerX - radius; x <= centerX + radius; x++)
            {
                if (Math.Abs(x - centerX) + Math.Abs(y - centerY) <= radius)
                    bitmap.SetPixel(x, y, color);
            }
        }

        /// <summary>
        /// Draws the verified Empire ground marker: a wide horizontal band crossed by a much
        /// narrower vertical stem (the silhouette measured in real client frames).
        /// </summary>
        private static void FillBannerMarker(Bitmap bitmap, int x, int y, int width, int height, Color color)
        {
            int bandHeight = Math.Max(2, height / 7);
            int bandTop = y + ((height - bandHeight) / 2);
            int stemWidth = Math.Max(2, width / 3);
            int stemLeft = x + ((width - stemWidth) / 2);

            for (int row = 0; row < height; row++)
            {
                bool inBand = row >= bandTop - y && row < bandTop - y + bandHeight;
                int left = inBand ? x : stemLeft;
                int right = inBand ? x + width : stemLeft + stemWidth;
                for (int column = left; column < right; column++)
                    bitmap.SetPixel(column, y + row, color);
            }
        }

        /// <summary>
        /// Draws the shipped dropped-item silhouette: a smooth vertically symmetric dome with
        /// its widest row in the middle and no narrow stem.
        /// </summary>
        private static void FillDome(Bitmap bitmap, int x, int y, int width, int height, Color color)
        {
            int half = height / 2;
            for (int row = 0; row < height; row++)
            {
                int distance = Math.Abs(row - half);
                int currentWidth = Math.Max(1, width - (distance * width / Math.Max(half, 1)));
                int left = x + ((width - currentWidth) / 2);
                for (int column = left; column < left + currentWidth; column++)
                    bitmap.SetPixel(column, y + row, color);
            }
        }

        private static void FillPixelPattern(Bitmap bitmap, int x, int y, string[] rows, Color color)
        {
            for (int row = 0; row < rows.Length; row++)
            for (int column = 0; column < rows[row].Length; column++)
            {
                if (rows[row][column] == '#')
                    bitmap.SetPixel(x + column, y + row, color);
            }
        }

        private static int FillCompactHChatGlyph(Bitmap bitmap, int x, int y, int width, int height, Color color)
        {
            int pixelCount = 0;
            int middleRow = height / 2;
            for (int row = 0; row < height; row++)
            for (int column = 0; column < width; column++)
            {
                bool inVerticalStroke = column < 2 || column >= width - 2;
                bool inCrossbar = row == middleRow || row == middleRow + 1;
                if (!inVerticalStroke && !inCrossbar)
                    continue;

                bitmap.SetPixel(x + column, y + row, color);
                pixelCount++;
            }

            return pixelCount;
        }

        private static void FillBottomChatGlyphs(Bitmap bitmap)
        {
            Color yellow = Color.FromArgb(255, 255, 0);
            FillCompactHChatGlyph(bitmap, x: 740, y: 690, width: 6, height: 9, yellow);
            FillCompactHChatGlyph(bitmap, x: 760, y: 690, width: 6, height: 7, yellow);
            FillCompactHChatGlyph(bitmap, x: 780, y: 690, width: 7, height: 7, yellow);
            FillCompactHChatGlyph(bitmap, x: 800, y: 690, width: 9, height: 7, yellow);
        }

        private static void FillOccludedEmpireMarker(Bitmap bitmap, int x, int y)
        {
            FillConnectedRowProfile(bitmap, x, y, width: 33, height: 30,
                row => row is 13 or 14 or 15 ? 33 : row is 10 or 11 or 12 or 16 or 17 or 18 or 19 ? 13 : 12,
                Color.FromArgb(255, 255, 0));
        }

        private static void FillRealisticLocalEmpireMarker(Bitmap bitmap, int x, int y)
        {
            FillConnectedRowProfile(bitmap, x, y, width: 19, height: 21,
                row => row is 9 or 10 or 11 ? 19 : row == 0 ? 5 : 6,
                Color.FromArgb(255, 255, 0));
        }

        private static void FillConnectedRowProfile(
            Bitmap bitmap,
            int x,
            int y,
            int width,
            int height,
            Func<int, int> rowWidth,
            Color color)
        {
            for (int row = 0; row < height; row++)
            {
                int currentWidth = rowWidth(row);
                int left = x + (width - currentWidth) / 2;
                for (int column = 0; column < currentWidth; column++)
                    bitmap.SetPixel(left + column, y + row, color);
            }
        }
    }
}
