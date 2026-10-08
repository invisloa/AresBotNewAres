using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DriverScanTester.Services
{
    internal readonly record struct PlayerMarkerCandidate(
        string Faction,
        Rectangle Bounds,
        int PixelCount,
        float FillRatio,
        float AspectRatio,
        PointF Center);

    /// <summary>
    /// Detects the installed top-down player faction markers: Empire yellow squares
    /// (RGB #FFFF00) and Alliance green triangles (RGB #00FF00). Components overlapping
    /// the local-character exclusion or known HUD regions are ignored, and faction-specific
    /// shape checks reject compact UI glyphs.
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
        private const int MinimumComponentDimensionAbsolutePx = 10;
        private const float MinimumComponentDimensionAtReferenceHeightPx = 12f;
        private const int MinimumComponentPixelsAbsolute = 60;
        private const float MinimumComponentPixelsAtReferenceHeight = 80f;
        private const float MaximumComponentDimensionAtReferenceHeight = 72f;
        private const float EmpireMaximumAspectRatio = 1.7f;
        private const float AllianceMaximumAspectRatio = 2.2f;
        private const float EmpireMinimumFillRatio = 0.30f;
        private const float AllianceMinimumFillRatio = 0.28f;

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
                    width, height, lootCharacterExclusionArea, out candidate);
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

                // Keep square-like Empire components spatially supported in both axes;
                // Alliance markers must have a triangular, widening silhouette. This
                // rejects compact colored UI glyphs that pass color/size tests alone.
                if (markerKind == EmpireMarker &&
                    !HasDenseProjection(_queue, tail, width, componentBounds))
                    continue;
                if (markerKind == AllianceMarker &&
                    !HasTriangleProfile(_queue, tail, width, componentBounds))
                    continue;

                float centerX = sumX / (float)count;
                float centerY = sumY / (float)count;

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

        private static bool IntersectsAny(Rectangle bounds, Rectangle[] exclusionAreas)
        {
            foreach (Rectangle area in exclusionAreas)
            {
                if (bounds.IntersectsWith(area))
                    return true;
            }
            return false;
        }

        private static bool HasDenseProjection(
            int[] componentPixels,
            int componentPixelCount,
            int frameWidth,
            Rectangle bounds)
        {
            int[] rowCounts = new int[bounds.Height];
            int[] columnCounts = new int[bounds.Width];
            for (int i = 0; i < componentPixelCount; i++)
            {
                int index = componentPixels[i];
                int x = index % frameWidth - bounds.Left;
                int y = index / frameWidth - bounds.Top;
                rowCounts[y]++;
                columnCounts[x]++;
            }

            int occupiedRows = 0;
            foreach (int count in rowCounts)
            {
                if (count > 0)
                    occupiedRows++;
            }
            int occupiedColumns = 0;
            foreach (int count in columnCounts)
            {
                if (count > 0)
                    occupiedColumns++;
            }

            return occupiedRows >= Math.Ceiling(bounds.Height * 0.75) &&
                   occupiedColumns >= Math.Ceiling(bounds.Width * 0.75);
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
                       componentPixels, componentPixelCount, frameWidth, bounds, horizontalAxis: true);
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
