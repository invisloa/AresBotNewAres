using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DriverScanTester.Services
{
    public readonly record struct PlayerMarkerCandidate(
        string Faction,
        Rectangle Bounds,
        int PixelCount,
        float FillRatio,
        float AspectRatio,
        PointF Center);

    /// <summary>
    /// Detects Empire yellow and Alliance green player faction markers from their colored
    /// components. The silhouette checks tolerate rotation and perspective projection so
    /// ground markers remain detectable when the camera is tilted. Components overlapping
    /// the local-player exclusion or known HUD regions are ignored, and faction-specific
    /// shape checks reject compact UI glyphs. This exclusion applies only to marker
    /// detection; the loot pixel scan covers the whole scan region.
    ///
    /// Real client frames show that this game paints a LOT of objects in exactly the same
    /// emissive pure yellow as the Empire actor marker: dropped-item gems ("Ykshali",
    /// "Supakna"), monster bodies ("[PRIME] Thread Weaver", "Nanima"), spell effects and HUD
    /// icons. Color + size + "solid blob" tests therefore reported a false "other player"
    /// next to the character all the time (2026-11-04..16). Acceptance now requires the
    /// marker silhouettes actually drawn by the game: the Empire banner (wide band crossed by
    /// a narrow stem) or the Alliance triangle, on top of real-world size floors. Yellow solid
    /// blobs are never accepted, because a yellow drop item is silhouette-identical to them.
    /// </summary>
    internal sealed class PlayerMarkerPixelDetector
    {
        private const byte NoMarker = 0;
        private const byte EmpireMarker = 1;
        private const byte AllianceMarker = 2;

        // The installed markers use emissive pure RGB. Allow a little rasterization
        // variation while still rejecting most terrain and UI colors.
        private const int MarkerChannelMin = 205;
        private const int OtherChannelMax = 55;

        // Size floors. Every yellow item model, monster part, spell effect and HUD glyph in
        // this game is rendered with the same saturated pure yellow as the Empire marker, so
        // small fragments are never a reliable marker. Real markers measured on the client
        // are 19x21 px (164 px) up to 33x30 px (430 px) at the 840 px reference height; the
        // fragments that produced the 2026-11-16 false "other player" were 9x10..12x13 px
        // (54..78 px), i.e. clearly below these floors.
        private const int MinimumComponentDimensionAbsolutePx = 12;
        private const float MinimumComponentDimensionAtReferenceHeightPx = 14f;
        private const int MinimumComponentPixelsAbsolute = 80;
        private const float MinimumComponentPixelsAtReferenceHeight = 120f;
        private const float MaximumComponentDimensionAtReferenceHeight = 72f;
        private const float EmpireMaximumAspectRatio = 1.7f;
        private const float AllianceMaximumAspectRatio = 2.2f;
        private const float EmpireMinimumFillRatio = 0.30f;
        private const float AllianceMinimumFillRatio = 0.28f;

        // ── Empire/yellow "banner" silhouette ──
        // The verified Empire ground marker is a flat decal: a wide horizontal band crossed by
        // a much narrower vertical stem (33x30 px rows = 12,12,...,33,33,33,...,12 and
        // 19x21 px rows = 6,6,...,19,19,19,...,6 in real client frames).
        // A plain "dense blob" test accepted every solid yellow game object — dropped-item
        // gems ("Ykshali", "Supakna"), monster bodies, spell effects and HUD icons — which is
        // exactly what the false other-player reports of 2026-11-04..16 were. The silhouette
        // checks below require the stem/band contrast instead, so solid squares, slabs and
        // smooth diamonds are rejected no matter how yellow they are.
        private const float EmpireMinimumBandToStemWidthRatio = 2f;
        private const float EmpireMinimumBandToAdjacentWidthRatio = 2f;
        private const float EmpireMaximumBandRowFraction = 0.30f;
        private const float EmpireMinimumStemRowFraction = 0.50f;

        private byte[] _pixelKinds = Array.Empty<byte>();
        private int[] _queue = Array.Empty<int>();
        private byte[] _rowBytes = Array.Empty<byte>();

        /// <summary>
        /// Returns true if the client frame contains a shape-valid faction marker outside
        /// the local-character exclusion and known HUD regions.
        /// </summary>
        public bool ContainsOtherPlayerMarker(Bitmap bitmap, Rectangle lootCharacterExclusionArea)
            => TryFindOtherPlayerMarker(bitmap, lootCharacterExclusionArea, out _);

        public bool TryFindOtherPlayerMarker(
            Bitmap bitmap,
            Rectangle lootCharacterExclusionArea,
            out PlayerMarkerCandidate candidate)
            => TryFindOtherPlayerMarker(
                bitmap, lootCharacterExclusionArea,
                localCharacterAnchor: null, localCharacterExclusionRadius: 0f,
                allowClassEmblems: false,
                out candidate);

        /// <summary>
        /// Returns true when a shape-valid faction marker is present outside the configured
        /// loot exclusion rectangle, the known HUD regions and the LOCAL player's own marker
        /// radius.
        ///
        /// The extra anchor/radius parameters exist because the legacy loot exclusion rectangle
        /// is deliberately large (160x170 px): it hides the faction marker of another player
        /// standing next to the character, which is exactly how the bot attacked a player on
        /// 2026-10-29. Combat-time safety checks pass a compact radius around the character
        /// instead of the whole loot rectangle, so a nearby other player is still detected while
        /// the local player's own marker stays ignored.
        /// </summary>
        public bool TryFindOtherPlayerMarker(
            Bitmap bitmap,
            Rectangle lootCharacterExclusionArea,
            PointF? localCharacterAnchor,
            float localCharacterExclusionRadius,
            bool allowClassEmblems,
            out PlayerMarkerCandidate candidate)
        {
            candidate = default;
            if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
                return false;

            int pixelCount = checked(bitmap.Width * bitmap.Height);
            EnsureCapacity(pixelCount, bitmap.Width * 4);
            Array.Clear(_pixelKinds, 0, pixelCount);

            Bitmap? converted = null;
            Bitmap scanBitmap = bitmap;
            BitmapData? data = null;
            try
            {
                if (Image.GetPixelFormatSize(bitmap.PixelFormat) != 32)
                {
                    converted = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);
                    using (Graphics graphics = Graphics.FromImage(converted))
                        graphics.DrawImageUnscaled(bitmap, 0, 0);
                    scanBitmap = converted;
                }

                data = scanBitmap.LockBits(
                    new Rectangle(0, 0, scanBitmap.Width, scanBitmap.Height),
                    ImageLockMode.ReadOnly,
                    scanBitmap.PixelFormat);

                int width = scanBitmap.Width;
                int height = scanBitmap.Height;
                int stride = data.Stride;
                for (int y = 0; y < height; y++)
                {
                    IntPtr row = IntPtr.Add(data.Scan0, y * stride);
                    Marshal.Copy(row, _rowBytes, 0, width * 4);
                    int pixelIndex = y * width;
                    for (int x = 0, byteIndex = 0; x < width; x++, byteIndex += 4)
                    {
                        // Windows 32bpp ARGB memory is stored as BGRA.
                        byte b = _rowBytes[byteIndex];
                        byte g = _rowBytes[byteIndex + 1];
                        byte r = _rowBytes[byteIndex + 2];
                        _pixelKinds[pixelIndex + x] = ClassifyPlayerMarkerPixel(r, g, b);
                    }
                }

                return TryFindOtherMarkerComponent(
                    width, height, lootCharacterExclusionArea,
                    localCharacterAnchor, localCharacterExclusionRadius,
                    allowClassEmblems,
                    out candidate);
            }
            finally
            {
                if (data != null)
                {
                    try { scanBitmap.UnlockBits(data); }
                    catch { /* Detection cleanup must not affect the loot bot. */ }
                }
                converted?.Dispose();
            }
        }

        /// <summary>
        /// Local character screen anchor used by the combat-time marker scan: horizontal
        /// centre, slightly below the vertical centre (the same centre the legacy loot
        /// exclusion rectangle uses).
        /// </summary>
        internal static PointF GetCombatLocalCharacterAnchor(int clientWidth, int clientHeight)
            => new(
                clientWidth * BotConstants.CombatSafety.LocalCharacterAnchorRatioX,
                clientHeight * BotConstants.CombatSafety.LocalCharacterAnchorRatioY);

        /// <summary>
        /// Radius (px) around the local character that is treated as the local player's own
        /// faction marker, scaled from the 840 px reference client height.
        /// </summary>
        internal static float GetCombatLocalCharacterExclusionRadius(int clientHeight)
        {
            if (clientHeight <= 0)
                return 0f;
            float scale = clientHeight / (float)BotConstants.MobGrouping.ReferenceClientHeightPx;
            return BotConstants.CombatSafety.LocalCharacterMarkerExclusionRadiusPx * scale;
        }

        internal static byte ClassifyPlayerMarkerPixel(int r, int g, int b)
        {
            if (r >= MarkerChannelMin && g >= MarkerChannelMin && b <= OtherChannelMax)
                return EmpireMarker;
            if (g >= MarkerChannelMin && r <= OtherChannelMax && b <= OtherChannelMax)
                return AllianceMarker;
            return NoMarker;
        }

        internal static Rectangle GetLootCharacterExclusionArea(
            int clientWidth,
            int clientHeight,
            int referenceClientOriginX,
            int referenceClientOriginY)
        {
            if (clientWidth <= 0 || clientHeight <= 0)
                return Rectangle.Empty;

            Rectangle clientBounds = new(0, 0, clientWidth, clientHeight);
            Rectangle configuredArea = Rectangle.FromLTRB(
                BotConstants.Loot.ExcludeXMin - referenceClientOriginX,
                BotConstants.Loot.ExcludeYMin - referenceClientOriginY,
                BotConstants.Loot.ExcludeXMax - referenceClientOriginX + 1,
                BotConstants.Loot.ExcludeYMax - referenceClientOriginY + 1);
            return Rectangle.Intersect(clientBounds, configuredArea);
        }

        /// <summary>
        /// Known fixed HUD panels are not part of the world-marker search. These are
        /// scaled from the game's 840px reference client height and clipped to the
        /// actual client. The separate lower-left region covers the bright-green Q
        /// button that caused the 2026-10-08 false positive.
        /// </summary>
        internal static Rectangle[] GetHudExclusionAreas(int clientWidth, int clientHeight)
        {
            if (clientWidth <= 0 || clientHeight <= 0)
                return Array.Empty<Rectangle>();

            float scale = clientHeight / BotConstants.MobGrouping.ReferenceClientHeightPx;
            int Scale(float value) => Math.Max(1, (int)Math.Round(value * scale));
            Rectangle clientBounds = new(0, 0, clientWidth, clientHeight);
            int leftWidth = Scale(340);
            int leftHeight = Scale(175);
            int rightWidth = Scale(155);
            int rightHeight = Scale(280);
            int topCenterWidth = Scale(170);
            int topCenterHeight = Scale(48);
            int bottomHeight = Scale(112);
            int qButtonWidth = Scale(60);
            int qButtonTopOffset = Scale(210);
            int qButtonBottomOffset = Scale(115);

            Rectangle[] configuredAreas =
            {
                new(0, 0, leftWidth, leftHeight),
                new(clientWidth - rightWidth, 0, rightWidth, rightHeight),
                new((clientWidth - topCenterWidth) / 2, 0, topCenterWidth, topCenterHeight),
                new(0, clientHeight - bottomHeight, clientWidth, bottomHeight),
                new(0, clientHeight - qButtonTopOffset, qButtonWidth,
                    qButtonTopOffset - qButtonBottomOffset)
            };

            for (int i = 0; i < configuredAreas.Length; i++)
                configuredAreas[i] = Rectangle.Intersect(clientBounds, configuredAreas[i]);
            return configuredAreas;
        }

        private bool TryFindOtherMarkerComponent(
            int width,
            int height,
            Rectangle lootCharacterExclusionArea,
            PointF? localCharacterAnchor,
            float localCharacterExclusionRadius,
            bool allowClassEmblems,
            out PlayerMarkerCandidate candidate)
        {
            candidate = default;
            float scale = height / BotConstants.MobGrouping.ReferenceClientHeightPx;
            float minDimension = Math.Max(
                MinimumComponentDimensionAbsolutePx,
                MinimumComponentDimensionAtReferenceHeightPx * scale);
            int minPixels = Math.Max(
                MinimumComponentPixelsAbsolute,
                (int)Math.Round(MinimumComponentPixelsAtReferenceHeight * scale * scale));
            float maxDimension = Math.Max(
                MinimumComponentDimensionAbsolutePx,
                MaximumComponentDimensionAtReferenceHeight * scale);
            Rectangle clientBounds = new(0, 0, width, height);
            Rectangle exclusionArea = Rectangle.Intersect(clientBounds, lootCharacterExclusionArea);
            Rectangle[] hudExclusionAreas = GetHudExclusionAreas(width, height);
            int pixelCount = width * height;

            for (int start = 0; start < pixelCount; start++)
            {
                byte markerKind = _pixelKinds[start];
                if (markerKind == NoMarker)
                    continue;

                int head = 0;
                int tail = 0;
                _queue[tail++] = start;
                _pixelKinds[start] = NoMarker;

                int count = 0;
                long sumX = 0;
                long sumY = 0;
                int minX = width;
                int maxX = -1;
                int minY = height;
                int maxY = -1;

                while (head < tail)
                {
                    int index = _queue[head++];
                    int x = index % width;
                    int y = index / width;
                    count++;
                    sumX += x;
                    sumY += y;
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int nextY = y + dy;
                        if ((uint)nextY >= (uint)height)
                            continue;

                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0)
                                continue;
                            int nextX = x + dx;
                            if ((uint)nextX >= (uint)width)
                                continue;

                            int next = nextY * width + nextX;
                            if (_pixelKinds[next] != markerKind)
                                continue;

                            _pixelKinds[next] = NoMarker;
                            _queue[tail++] = next;
                        }
                    }
                }

                Rectangle componentBounds = Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
                if (componentBounds.IntersectsWith(exclusionArea) ||
                    IntersectsAny(componentBounds, hudExclusionAreas))
                    continue;

                float centerX = sumX / (float)count;
                float centerY = sumY / (float)count;

                // The compact combat-time radius ignores the local player's own marker while
                // keeping another player's marker visible right next to the character.
                if (localCharacterAnchor.HasValue && localCharacterExclusionRadius > 0f)
                {
                    PointF anchor = localCharacterAnchor.Value;
                    float dx = centerX - anchor.X;
                    float dy = centerY - anchor.Y;
                    if ((dx * dx) + (dy * dy) <= localCharacterExclusionRadius * localCharacterExclusionRadius)
                        continue;
                }

                int componentWidth = componentBounds.Width;
                int componentHeight = componentBounds.Height;
                int largestDimension = Math.Max(componentWidth, componentHeight);
                int smallestDimension = Math.Min(componentWidth, componentHeight);
                if (count < minPixels || smallestDimension < minDimension || largestDimension > maxDimension)
                    continue;

                float aspectRatio = largestDimension / (float)Math.Max(smallestDimension, 1);
                float fillRatio = count / (float)(componentWidth * componentHeight);
                float maxAspectRatio = markerKind == EmpireMarker
                    ? EmpireMaximumAspectRatio
                    : AllianceMaximumAspectRatio;
                float minimumFillRatio = markerKind == EmpireMarker
                    ? EmpireMinimumFillRatio
                    : AllianceMinimumFillRatio;
                if (aspectRatio > maxAspectRatio || fillRatio < minimumFillRatio)
                    continue;

                // The Empire marker must show the banner silhouette (wide band + narrow stem);
                // Alliance markers must have a triangular, widening silhouette. Together with
                // the size floors this rejects solid yellow game objects (dropped items, monster
                // models, spell effects, HUD glyphs) that only pass the color test.
                //
                // Combat-time safety (allowClassEmblems) additionally accepts the verified
                // player CLASS EMBLEM: a large, solid faction-colored banner/flag silhouette
                // rendered above another player's character. The 2026-10-29 incident player was
                // rendered exactly like that and the plain ground-marker shape checks rejected
                // it. The loot scan does not enable this, so its behavior is unchanged.
                //
                // The emblem path is limited to ALLIANCE/green on purpose: every real false
                // "other player" report so far (2026-11-04..16) was a yellow game object —
                // dropped-item gems ("Ykshali", "Supakna"), monster bodies and HUD glyphs — and a
                // yellow dome/slab is silhouette-identical to a green banner. Yellow therefore
                // only counts when the verified banner silhouette is present.
                bool passesStrictShape =
                    (markerKind == EmpireMarker && HasBannerStemProfile(_queue, tail, width, componentBounds)) ||
                    (markerKind == AllianceMarker && HasTriangleProfile(_queue, tail, width, componentBounds));
                if (!passesStrictShape &&
                    !(allowClassEmblems &&
                      markerKind == AllianceMarker &&
                      IsClassEmblemProfile(
                          _queue, tail, width,
                          componentBounds, count, fillRatio, aspectRatio, height)))
                    continue;

                candidate = new PlayerMarkerCandidate(
                    markerKind == EmpireMarker ? "Empire/yellow" : "Alliance/green",
                    componentBounds,
                    count,
                    fillRatio,
                    aspectRatio,
                    new PointF(centerX, centerY));
                return true;
            }

            return false;
        }

        /// <summary>
        /// Verified player class-emblem silhouette check used ONLY by the combat-time safety
        /// scan. Accepts a large, solid, faction-colored emblem (wide upper body with a solid
        /// tapering lower half) while still rejecting hollow glyphs and thin UI noise.
        /// </summary>
        private static bool IsClassEmblemProfile(
            int[] componentPixels,
            int componentPixelCount,
            int frameWidth,
            Rectangle bounds,
            int pixelCount,
            float fillRatio,
            float aspectRatio,
            int frameHeight)
        {
            float scale = frameHeight / BotConstants.MobGrouping.ReferenceClientHeightPx;
            float minimumHeight = BotConstants.CombatSafety.EmblemMinimumHeightPx * scale;
            float minimumWidth = BotConstants.CombatSafety.EmblemMinimumWidthPx * scale;
            float minimumPixels = BotConstants.CombatSafety.EmblemMinimumPixels * scale * scale;

            if (bounds.Height < minimumHeight || bounds.Width < minimumWidth)
                return false;
            if (pixelCount < minimumPixels)
                return false;
            if (fillRatio < BotConstants.CombatSafety.EmblemMinimumFillRatio)
                return false;
            if (aspectRatio > BotConstants.CombatSafety.EmblemMaximumAspectRatio)
                return false;

            int lowerHalfTop = bounds.Top + (bounds.Height / 2);
            int lowerHalfHeight = bounds.Bottom - lowerHalfTop;
            if (lowerHalfHeight <= 0)
                return false;

            int lowerHalfPixels = 0;
            for (int i = 0; i < componentPixelCount; i++)
            {
                int y = (componentPixels[i] / frameWidth) - lowerHalfTop;
                if (y >= 0)
                    lowerHalfPixels++;
            }

            float lowerHalfFill = lowerHalfPixels / (float)(bounds.Width * lowerHalfHeight);
            if (lowerHalfFill < BotConstants.CombatSafety.EmblemMinimumLowerHalfFillRatio)
                return false;

            // The verified class emblem is a banner/flag carried above the player: the wide
            // cloth sits in the upper half and tapers into the pole at the bottom. Ground items
            // ("Ykshali", "Supakna"), monster bodies and HUD glyphs are vertically symmetric
            // diamonds/slabs or bottom-heavy, so they fail this top-heavy profile even though
            // they are large, solid and faction-colored.
            int[] rowPixels = new int[bounds.Height];
            for (int i = 0; i < componentPixelCount; i++)
                rowPixels[(componentPixels[i] / frameWidth) - bounds.Top]++;

            int third = Math.Max(1, bounds.Height / 3);
            int upperThirdPixels = 0;
            int lowerThirdPixels = 0;
            for (int row = 0; row < third; row++)
            {
                upperThirdPixels += rowPixels[row];
                lowerThirdPixels += rowPixels[rowPixels.Length - 1 - row];
            }

            return upperThirdPixels > 0 &&
                   upperThirdPixels >=
                   BotConstants.CombatSafety.EmblemMinimumUpperThirdToLowerThirdRatio * lowerThirdPixels;
        }

        /// <summary>
        /// Empire faction-marker silhouette: a wide horizontal band crossed by a much narrower
        /// vertical stem. Both the row and the column profile are tested so a marker rendered
        /// with a rotated/oblique camera still validates. Solid squares, slabs, diamonds and
        /// arbitrary monster/item blobs have no narrow stem and are rejected.
        /// </summary>
        private static bool HasBannerStemProfile(
            int[] componentPixels,
            int componentPixelCount,
            int frameWidth,
            Rectangle bounds)
        {
            return HasBannerProfileAlongAxis(
                       componentPixels, componentPixelCount, frameWidth, bounds, horizontalAxis: false) ||
                   HasBannerProfileAlongAxis(
                       componentPixels, componentPixelCount, frameWidth, bounds, horizontalAxis: true);
        }

        private static bool HasBannerProfileAlongAxis(
            int[] componentPixels,
            int componentPixelCount,
            int frameWidth,
            Rectangle bounds,
            bool horizontalAxis)
        {
            int profileLength = horizontalAxis ? bounds.Width : bounds.Height;
            if (profileLength < 8)
                return false;

            int[] minimumCrossCoordinate = new int[profileLength];
            int[] maximumCrossCoordinate = new int[profileLength];
            Array.Fill(minimumCrossCoordinate, int.MaxValue);
            Array.Fill(maximumCrossCoordinate, -1);

            for (int i = 0; i < componentPixelCount; i++)
            {
                int index = componentPixels[i];
                int x = index % frameWidth;
                int y = index / frameWidth;
                int profileIndex = horizontalAxis ? x - bounds.Left : y - bounds.Top;
                int crossCoordinate = horizontalAxis ? y - bounds.Top : x - bounds.Left;
                minimumCrossCoordinate[profileIndex] = Math.Min(
                    minimumCrossCoordinate[profileIndex], crossCoordinate);
                maximumCrossCoordinate[profileIndex] = Math.Max(
                    maximumCrossCoordinate[profileIndex], crossCoordinate);
            }

            int occupied = 0;
            int maximumWidth = 0;
            int firstOccupied = -1;
            int lastOccupied = -1;
            for (int i = 0; i < profileLength; i++)
            {
                if (maximumCrossCoordinate[i] < 0)
                    continue;
                int width = maximumCrossCoordinate[i] - minimumCrossCoordinate[i] + 1;
                maximumWidth = Math.Max(maximumWidth, width);
                if (firstOccupied < 0)
                    firstOccupied = i;
                lastOccupied = i;
                occupied++;
            }

            if (occupied < 8 || maximumWidth < 8)
                return false;

            int[] widths = new int[lastOccupied - firstOccupied + 1];
            for (int i = firstOccupied; i <= lastOccupied; i++)
            {
                widths[i - firstOccupied] = maximumCrossCoordinate[i] < 0
                    ? 0
                    : maximumCrossCoordinate[i] - minimumCrossCoordinate[i] + 1;
            }

            int[] sortedWidths = (int[])widths.Clone();
            Array.Sort(sortedWidths);
            float medianWidth = sortedWidths[sortedWidths.Length / 2];
            if (maximumWidth < EmpireMinimumBandToStemWidthRatio * medianWidth)
                return false;
            float bandFloor = maximumWidth * 0.80f;
            float stemCeiling = maximumWidth * 0.55f;
            int bandRows = 0;
            int stemRows = 0;
            foreach (int width in widths)
            {
                if (width >= bandFloor)
                    bandRows++;
                if (width > 0 && width <= stemCeiling)
                    stemRows++;
            }

            if (bandRows == 0 || bandRows > occupied * EmpireMaximumBandRowFraction)
                return false;
            if (stemRows < occupied * EmpireMinimumStemRowFraction)
                return false;

            // The band must appear abruptly: the rows touching the band have to be far narrower
            // than the band itself. A smoothly widening dome (dropped-item gem, monster body) has
            // wide neighbours and is rejected even when its overall width statistics look
            // banner-like.
            int firstBandRow = -1;
            int lastBandRow = -1;
            for (int i = 0; i < widths.Length; i++)
            {
                if (widths[i] < bandFloor)
                    continue;
                if (firstBandRow < 0)
                    firstBandRow = i;
                lastBandRow = i;
            }

            int widestNeighbour = 0;
            if (firstBandRow > 0)
                widestNeighbour = Math.Max(widestNeighbour, widths[firstBandRow - 1]);
            if (lastBandRow >= 0 && lastBandRow + 1 < widths.Length)
                widestNeighbour = Math.Max(widestNeighbour, widths[lastBandRow + 1]);
            if (maximumWidth < EmpireMinimumBandToAdjacentWidthRatio * widestNeighbour)
                return false;

            // The stem must reach both ends of the component, i.e. the wide band is interior
            // and not just a slab edge.
            return widths[0] > 0 && widths[0] <= stemCeiling &&
                   widths[widths.Length - 1] > 0 && widths[widths.Length - 1] <= stemCeiling;
        }

        private static bool IntersectsAny(Rectangle bounds, Rectangle[] exclusionAreas)
        {
            foreach (Rectangle area in exclusionAreas)
            {
                if (bounds.IntersectsWith(area))
                    return true;
            }
            return false;
        }

        private static bool HasTriangleProfile(
            int[] componentPixels,
            int componentPixelCount,
            int frameWidth,
            Rectangle bounds)
        {
            return HasTriangleProfileAlongAxis(
                       componentPixels, componentPixelCount, frameWidth, bounds, horizontalAxis: false) ||
                   HasTriangleProfileAlongAxis(
                       componentPixels, componentPixelCount, frameWidth, bounds, horizontalAxis: true) ||
                   HasTriangleProfileAlongPrincipalAxis(componentPixels, componentPixelCount, frameWidth, bounds);
        }

        /// <summary>
        /// Tests the taper along the component's principal axis. The old row/column-only
        /// test rejects a valid faction triangle as soon as an oblique camera projects it
        /// diagonally across the screen. PCA gives a rotation-independent axis; coarse
        /// slices avoid requiring a pixel-perfect edge after rasterization/occlusion.
        /// </summary>
        private static bool HasTriangleProfileAlongPrincipalAxis(
            int[] componentPixels,
            int componentPixelCount,
            int frameWidth,
            Rectangle bounds)
        {
            double meanX = 0;
            double meanY = 0;
            for (int i = 0; i < componentPixelCount; i++)
            {
                int index = componentPixels[i];
                meanX += index % frameWidth;
                meanY += index / frameWidth;
            }
            meanX /= componentPixelCount;
            meanY /= componentPixelCount;

            double varianceX = 0;
            double varianceY = 0;
            double covariance = 0;
            for (int i = 0; i < componentPixelCount; i++)
            {
                int index = componentPixels[i];
                double dx = index % frameWidth - meanX;
                double dy = index / frameWidth - meanY;
                varianceX += dx * dx;
                varianceY += dy * dy;
                covariance += dx * dy;
            }

            double angle = 0.5 * Math.Atan2(2 * covariance, varianceX - varianceY);
            double axisX = Math.Cos(angle);
            double axisY = Math.Sin(angle);
            double crossX = -axisY;
            double crossY = axisX;
            double minProjection = double.PositiveInfinity;
            double maxProjection = double.NegativeInfinity;
            for (int i = 0; i < componentPixelCount; i++)
            {
                int index = componentPixels[i];
                double dx = index % frameWidth - meanX;
                double dy = index / frameWidth - meanY;
                double projection = dx * axisX + dy * axisY;
                minProjection = Math.Min(minProjection, projection);
                maxProjection = Math.Max(maxProjection, projection);
            }

            double span = maxProjection - minProjection;
            if (span < 6)
                return false;

            const int binCount = 12;
            var minimumCross = new double[binCount];
            var maximumCross = new double[binCount];
            Array.Fill(minimumCross, double.PositiveInfinity);
            Array.Fill(maximumCross, double.NegativeInfinity);
            for (int i = 0; i < componentPixelCount; i++)
            {
                int index = componentPixels[i];
                double dx = index % frameWidth - meanX;
                double dy = index / frameWidth - meanY;
                double projection = dx * axisX + dy * axisY;
                int bin = Math.Clamp((int)((projection - minProjection) / span * binCount), 0, binCount - 1);
                double crossProjection = dx * crossX + dy * crossY;
                minimumCross[bin] = Math.Min(minimumCross[bin], crossProjection);
                maximumCross[bin] = Math.Max(maximumCross[bin], crossProjection);
            }

            var widths = new double[binCount];
            int occupiedBins = 0;
            double maximumWidth = 0;
            for (int i = 0; i < binCount; i++)
            {
                if (double.IsPositiveInfinity(minimumCross[i]))
                    continue;
                widths[i] = maximumCross[i] - minimumCross[i] + 1;
                maximumWidth = Math.Max(maximumWidth, widths[i]);
                occupiedBins++;
            }

            if (occupiedBins < 9 || maximumWidth <= 1)
                return false;

            int endBinCount = binCount / 4;
            double firstEndWidth = 0;
            double lastEndWidth = 0;
            int firstOccupied = 0;
            int lastOccupied = 0;
            for (int i = 0; i < endBinCount; i++)
            {
                if (widths[i] > 0)
                {
                    firstEndWidth += widths[i];
                    firstOccupied++;
                }
                int reverseIndex = binCount - 1 - i;
                if (widths[reverseIndex] > 0)
                {
                    lastEndWidth += widths[reverseIndex];
                    lastOccupied++;
                }
            }

            if (firstOccupied == 0 || lastOccupied == 0)
                return false;

            firstEndWidth /= firstOccupied;
            lastEndWidth /= lastOccupied;
            return (firstEndWidth <= maximumWidth * 0.70 && lastEndWidth >= maximumWidth * 0.70) ||
                   (lastEndWidth <= maximumWidth * 0.70 && firstEndWidth >= maximumWidth * 0.70);
        }

        private static bool HasTriangleProfileAlongAxis(
            int[] componentPixels,
            int componentPixelCount,
            int frameWidth,
            Rectangle bounds,
            bool horizontalAxis)
        {
            int profileLength = horizontalAxis ? bounds.Width : bounds.Height;
            int[] minimumCrossCoordinate = new int[profileLength];
            int[] maximumCrossCoordinate = new int[profileLength];
            Array.Fill(minimumCrossCoordinate, int.MaxValue);
            Array.Fill(maximumCrossCoordinate, -1);

            for (int i = 0; i < componentPixelCount; i++)
            {
                int index = componentPixels[i];
                int x = index % frameWidth;
                int y = index / frameWidth;
                int profileIndex = horizontalAxis ? x - bounds.Left : y - bounds.Top;
                int crossCoordinate = horizontalAxis ? y - bounds.Top : x - bounds.Left;
                minimumCrossCoordinate[profileIndex] = Math.Min(
                    minimumCrossCoordinate[profileIndex], crossCoordinate);
                maximumCrossCoordinate[profileIndex] = Math.Max(
                    maximumCrossCoordinate[profileIndex], crossCoordinate);
            }

            int[] profileWidths = new int[profileLength];
            int occupiedProfiles = 0;
            int maximumWidth = 0;
            for (int i = 0; i < profileLength; i++)
            {
                if (maximumCrossCoordinate[i] < 0)
                    continue;
                profileWidths[i] = maximumCrossCoordinate[i] - minimumCrossCoordinate[i] + 1;
                maximumWidth = Math.Max(maximumWidth, profileWidths[i]);
                occupiedProfiles++;
            }

            if (occupiedProfiles < Math.Ceiling(profileLength * 0.75) || maximumWidth <= 1)
                return false;

            return WidensTowardEnd(profileWidths, maximumWidth, reverse: false) ||
                   WidensTowardEnd(profileWidths, maximumWidth, reverse: true);
        }

        private static bool WidensTowardEnd(int[] profileWidths, int maximumWidth, bool reverse)
        {
            int first = reverse ? profileWidths[^1] : profileWidths[0];
            int last = reverse ? profileWidths[0] : profileWidths[^1];
            if (first > maximumWidth * 0.65f || last < maximumWidth * 0.70f)
                return false;

            int widening = 0;
            int narrowing = 0;
            for (int i = 1; i < profileWidths.Length; i++)
            {
                int previous = reverse ? profileWidths[profileWidths.Length - i] : profileWidths[i - 1];
                int current = reverse ? profileWidths[profileWidths.Length - i - 1] : profileWidths[i];
                int delta = current - previous;
                if (delta > 1)
                    widening += delta;
                else if (delta < -1)
                    narrowing -= delta;
            }

            return widening >= Math.Max(2, maximumWidth / 3) &&
                   narrowing <= Math.Max(2, (int)Math.Round(widening * 0.40));
        }

        private void EnsureCapacity(int pixelCount, int rowByteCount)
        {
            if (_pixelKinds.Length < pixelCount)
                _pixelKinds = new byte[pixelCount];
            if (_queue.Length < pixelCount)
                _queue = new int[pixelCount];
            if (_rowBytes.Length < rowByteCount)
                _rowBytes = new byte[rowByteCount];
        }
    }
}
