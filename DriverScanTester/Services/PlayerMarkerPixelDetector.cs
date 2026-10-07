using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Detects the installed top-down player faction markers: Empire yellow squares
    /// (RGB #FFFF00) and Alliance green triangles (RGB #00FF00). The local player's
    /// marker is ignored around the stable player anchor; only other on-screen markers
    /// are reported.
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
        private const int MinimumComponentPixels = 12;
        private const int MinimumComponentDimension = 4;
        private const float MaximumComponentDimensionAtReferenceHeight = 72f;
        private const float LocalPlayerExclusionRadiusFactor = 1.75f;

        private byte[] _pixelKinds = Array.Empty<byte>();
        private int[] _queue = Array.Empty<int>();
        private byte[] _rowBytes = Array.Empty<byte>();

        /// <summary>
        /// Returns true if the client frame contains a compact faction marker outside
        /// the local-player exclusion area. The marker components are read in bulk from
        /// the captured frame; no additional screen capture is required.
        /// </summary>
        public bool ContainsOtherPlayerMarker(Bitmap bitmap, float localPlayerAnchorX, float localPlayerAnchorY)
        {
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

                return HasOtherMarkerComponent(width, height, localPlayerAnchorX, localPlayerAnchorY);
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

        private bool HasOtherMarkerComponent(int width, int height, float localPlayerAnchorX, float localPlayerAnchorY)
        {
            float scale = height / BotConstants.MobGrouping.ReferenceClientHeightPx;
            float maxDimension = Math.Max(
                MinimumComponentDimension,
                MaximumComponentDimensionAtReferenceHeight * scale);
            float localPlayerExclusionRadius = Math.Max(
                12f,
                BotConstants.MobGrouping.ExpectedMarkerRadiusPx * scale * LocalPlayerExclusionRadiusFactor);
            float localPlayerExclusionRadiusSquared = localPlayerExclusionRadius * localPlayerExclusionRadius;
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

                int componentWidth = maxX - minX + 1;
                int componentHeight = maxY - minY + 1;
                int largestDimension = Math.Max(componentWidth, componentHeight);
                int smallestDimension = Math.Min(componentWidth, componentHeight);
                if (count < MinimumComponentPixels || smallestDimension < MinimumComponentDimension ||
                    largestDimension > maxDimension)
                    continue;

                float aspectRatio = largestDimension / (float)Math.Max(smallestDimension, 1);
                float fillRatio = count / (float)(componentWidth * componentHeight);
                float maxAspectRatio = markerKind == EmpireMarker ? 1.7f : 2.2f;
                float minimumFillRatio = markerKind == EmpireMarker ? 0.58f : 0.28f;
                if (aspectRatio > maxAspectRatio || fillRatio < minimumFillRatio)
                    continue;

                float centerX = sumX / (float)count;
                float centerY = sumY / (float)count;
                float dxFromPlayer = centerX - localPlayerAnchorX;
                float dyFromPlayer = centerY - localPlayerAnchorY;
                if (dxFromPlayer * dxFromPlayer + dyFromPlayer * dyFromPlayer <=
                    localPlayerExclusionRadiusSquared)
                    continue;

                return true;
            }

            return false;
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
