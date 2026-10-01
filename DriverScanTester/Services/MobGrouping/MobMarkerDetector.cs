using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;

namespace DriverScanTester.Services
{
    public sealed class MobMarkerDetectionResult
    {
        public bool Success { get; init; }
        public int ClientWidth { get; init; }
        public int ClientHeight { get; init; }
        public Rectangle RoiClient { get; init; }
        public MobPoint PlayerAnchor { get; init; }
        public IReadOnlyList<MobMarker> Markers { get; init; } = Array.Empty<MobMarker>();
    }

    /// <summary>
    /// Captures only the game-client combat ROI and detects the artificial cyan centers and
    /// magenta rings. This component has no combat, route or movement decisions.
    /// </summary>
    public sealed class MobMarkerDetector : IDisposable
    {
        private const int CenterRingAngleSamples = BotConstants.MobGrouping.RingAngularSamples;
        private readonly Action<string> _log;
        private readonly object _gate = new();

        private Bitmap? _frame;
        private Graphics? _graphics;
        private bool[] _magentaMask = Array.Empty<bool>();
        private bool[] _cyanMask = Array.Empty<bool>();
        private bool[] _visited = Array.Empty<bool>();
        private int[] _votes = Array.Empty<int>();
        private int[] _queue = Array.Empty<int>();
        private byte[] _rowBytes = Array.Empty<byte>();
        private readonly List<int> _magentaPixels = new();
        private readonly List<(int X, int Y, int Votes)> _peaks = new();
        private readonly List<(int X, int Y, int Votes, float Coverage)> _ringCandidates = new();
        private bool _captureErrorLogged;
        private bool _disposed;

        public MobMarkerDetector(Action<string>? log = null)
        {
            _log = log ?? (_ => { });
        }

        public bool IsGameWindowValid() => TryGetGameClient(out _, out _, out _, out _, requireForeground: true);

        /// <summary>Captures/analyzes one ROI. Failed or unfocused capture is reported, never thrown.</summary>
        public MobMarkerDetectionResult CaptureAndDetect()
        {
            lock (_gate)
            {
                if (_disposed || !TryGetGameClient(out nint hwnd, out int screenX, out int screenY,
                        out Size clientSize, requireForeground: true))
                    return new MobMarkerDetectionResult();

                Rectangle roi = GetCombatRoi(clientSize.Width, clientSize.Height);
                if (roi.Width <= 0 || roi.Height <= 0)
                    return new MobMarkerDetectionResult();

                EnsureFrame(roi.Width, roi.Height);
                try
                {
                    _graphics!.CopyFromScreen(
                        screenX + roi.Left,
                        screenY + roi.Top,
                        0,
                        0,
                        new Size(roi.Width, roi.Height));

                    float markerRadius = GetExpectedRadius(clientSize.Height);
                    MobMarker[] markers = DetectBitmapLocked(_frame!, roi.Left, roi.Top, markerRadius);
                    _captureErrorLogged = false;
                    return new MobMarkerDetectionResult
                    {
                        Success = true,
                        ClientWidth = clientSize.Width,
                        ClientHeight = clientSize.Height,
                        RoiClient = roi,
                        PlayerAnchor = GetPlayerAnchor(clientSize.Width, clientSize.Height),
                        Markers = markers
                    };
                }
                catch (Exception ex)
                {
                    if (!_captureErrorLogged)
                    {
                        _captureErrorLogged = true;
                        _log($"[MobGrouping] client ROI capture/detection failed: {ex.Message}");
                    }
                    return new MobMarkerDetectionResult();
                }
            }
        }

        /// <summary>Returns a detached one-shot ROI frame for the optional diagnostics renderer.</summary>
        internal Bitmap? CaptureDebugFrame(out MobMarkerDetectionResult? result)
        {
            lock (_gate)
            {
                result = null;
                if (_disposed || !TryGetGameClient(out _, out int screenX, out int screenY,
                        out Size clientSize, requireForeground: true))
                    return null;

                Rectangle roi = GetCombatRoi(clientSize.Width, clientSize.Height);
                if (roi.Width <= 0 || roi.Height <= 0)
                    return null;

                EnsureFrame(roi.Width, roi.Height);
                try
                {
                    _graphics!.CopyFromScreen(screenX + roi.Left, screenY + roi.Top, 0, 0,
                        new Size(roi.Width, roi.Height));
                    MobPoint anchor = GetPlayerAnchor(clientSize.Width, clientSize.Height);
                    MobMarker[] markers = DetectBitmapLocked(
                        _frame!, roi.Left, roi.Top, GetExpectedRadius(clientSize.Height));
                    result = new MobMarkerDetectionResult
                    {
                        Success = true,
                        ClientWidth = clientSize.Width,
                        ClientHeight = clientSize.Height,
                        RoiClient = roi,
                        PlayerAnchor = anchor,
                        Markers = markers
                    };
                    _captureErrorLogged = false;
                    return (Bitmap)_frame!.Clone();
                }
                catch (Exception ex)
                {
                    if (!_captureErrorLogged)
                    {
                        _captureErrorLogged = true;
                        _log($"[MobGrouping] debug frame capture failed: {ex.Message}");
                    }
                    return null;
                }
            }
        }

        internal MobMarker[] DetectBitmapForTesting(Bitmap bitmap, int clientOffsetX = 0, int clientOffsetY = 0,
            float expectedRadius = BotConstants.MobGrouping.ExpectedMarkerRadiusPx)
        {
            lock (_gate)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(MobMarkerDetector));
                return DetectBitmapLocked(bitmap, clientOffsetX, clientOffsetY, expectedRadius);
            }
        }

        internal static Rectangle GetCombatRoi(int clientWidth, int clientHeight)
        {
            if (clientWidth <= 0 || clientHeight <= 0)
                return Rectangle.Empty;

            MobPoint anchor = GetPlayerAnchor(clientWidth, clientHeight);
            int halfWidth = (int)Math.Round(clientWidth * BotConstants.MobGrouping.RoiHalfWidthRatio);
            int halfHeight = (int)Math.Round(clientHeight * BotConstants.MobGrouping.RoiHalfHeightRatio);
            int left = Math.Clamp((int)Math.Floor(anchor.X - halfWidth), 0, clientWidth - 1);
            int top = Math.Clamp((int)Math.Floor(anchor.Y - halfHeight), 0, clientHeight - 1);
            int right = Math.Clamp((int)Math.Ceiling(anchor.X + halfWidth), left + 1, clientWidth);
            int bottom = Math.Clamp((int)Math.Ceiling(anchor.Y + halfHeight), top + 1, clientHeight);
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        internal static MobPoint GetPlayerAnchor(int clientWidth, int clientHeight) => new(
            clientWidth * BotConstants.MobGrouping.PlayerAnchorRatioX,
            clientHeight * BotConstants.MobGrouping.PlayerAnchorRatioY);

        internal static float GetExpectedRadius(int clientHeight)
        {
            float scale = clientHeight > 0
                ? clientHeight / BotConstants.MobGrouping.ReferenceClientHeightPx
                : 1f;
            return Math.Max(6f, BotConstants.MobGrouping.ExpectedMarkerRadiusPx * scale);
        }

        internal static bool IsMagentaMarkerPixel(Color color)
        {
            int tolerance = BotConstants.MobGrouping.MarkerColorTolerance;
            return color.R >= 255 - tolerance &&
                   color.G <= tolerance &&
                   color.B >= 255 - tolerance &&
                   Math.Abs(color.R - color.B) <= tolerance;
        }

        internal static bool IsCyanCenterPixel(Color color)
        {
            int tolerance = BotConstants.MobGrouping.MarkerColorTolerance;
            return color.R <= tolerance &&
                   color.G >= 255 - tolerance &&
                   color.B >= 255 - tolerance &&
                   Math.Abs(color.G - color.B) <= tolerance;
        }

        internal static IReadOnlyList<MobMarker> DeduplicateCandidates(
            IEnumerable<MobMarker> candidates,
            float expectedRadius)
        {
            var ordered = candidates
                .OrderByDescending(marker => marker.Source == MobMarkerSource.CyanCenter)
                .ThenByDescending(marker => marker.Confidence)
                .ToArray();
            var result = new List<MobMarker>(ordered.Length);

            foreach (MobMarker candidate in ordered)
            {
                bool duplicate = false;
                for (int i = 0; i < result.Count; i++)
                {
                    MobMarker accepted = result[i];
                    float radius = Math.Min(
                        accepted.Radius > 0 ? accepted.Radius : expectedRadius,
                        candidate.Radius > 0 ? candidate.Radius : expectedRadius);
                    float acceptedRadius = accepted.Radius > 0 ? accepted.Radius : expectedRadius;
                    float candidateRadius = candidate.Radius > 0 ? candidate.Radius : expectedRadius;
                    float radiusDifference = Math.Abs(acceptedRadius - candidateRadius);
                    float radiusTolerance = Math.Max(
                        BotConstants.MobGrouping.MarkerRadiusTolerancePx * 2,
                        expectedRadius * 0.45f);
                    if (radiusDifference > radiusTolerance)
                        continue;

                    float duplicateFactor = accepted.Source == candidate.Source
                        ? BotConstants.MobGrouping.CandidateDeduplicationRadiusFactor * 0.85f
                        : BotConstants.MobGrouping.CandidateDeduplicationRadiusFactor;
                    float tolerance = Math.Max(2.5f, radius * duplicateFactor);
                    float dx = accepted.Center.X - candidate.Center.X;
                    float dy = accepted.Center.Y - candidate.Center.Y;
                    if (dx * dx + dy * dy <= tolerance * tolerance)
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                    result.Add(candidate);
            }

            return result;
        }

        private MobMarker[] DetectBitmapLocked(Bitmap bitmap, int clientOffsetX, int clientOffsetY, float expectedRadius)
        {
            if (bitmap.Width <= 0 || bitmap.Height <= 0)
                return Array.Empty<MobMarker>();

            int width = bitmap.Width;
            int height = bitmap.Height;
            int pixelCount = checked(width * height);
            EnsureBuffers(width, height);
            Array.Clear(_magentaMask, 0, pixelCount);
            Array.Clear(_cyanMask, 0, pixelCount);
            Array.Clear(_visited, 0, pixelCount);
            _magentaPixels.Clear();

            BitmapData? data = null;
            Bitmap? converted = null;
            Bitmap scanBitmap = bitmap;
            try
            {
                if (bitmap.PixelFormat != PixelFormat.Format32bppArgb)
                {
                    converted = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                    using (Graphics graphics = Graphics.FromImage(converted))
                        graphics.DrawImageUnscaled(bitmap, 0, 0);
                    scanBitmap = converted;
                }

                data = scanBitmap.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppArgb);
                for (int y = 0; y < height; y++)
                {
                    IntPtr row = IntPtr.Add(data.Scan0, y * data.Stride);
                    Marshal.Copy(row, _rowBytes, 0, width * 4);
                    int pixelIndex = y * width;
                    for (int x = 0, byteIndex = 0; x < width; x++, byteIndex += 4)
                    {
                        // 32bpp ARGB is laid out as BGRA bytes on Windows.
                        byte b = _rowBytes[byteIndex];
                        byte g = _rowBytes[byteIndex + 1];
                        byte r = _rowBytes[byteIndex + 2];
                        int index = pixelIndex + x;
                        if (r >= 255 - BotConstants.MobGrouping.MarkerColorTolerance &&
                            g <= BotConstants.MobGrouping.MarkerColorTolerance &&
                            b >= 255 - BotConstants.MobGrouping.MarkerColorTolerance &&
                            Math.Abs(r - b) <= BotConstants.MobGrouping.MarkerColorTolerance)
                        {
                            _magentaMask[index] = true;
                            _magentaPixels.Add(index);
                        }
                        else if (r <= BotConstants.MobGrouping.MarkerColorTolerance &&
                                 g >= 255 - BotConstants.MobGrouping.MarkerColorTolerance &&
                                 b >= 255 - BotConstants.MobGrouping.MarkerColorTolerance &&
                                 Math.Abs(g - b) <= BotConstants.MobGrouping.MarkerColorTolerance)
                        {
                            _cyanMask[index] = true;
                        }
                    }
                }
            }
            finally
            {
                if (data != null)
                {
                    try { scanBitmap.UnlockBits(data); }
                    catch { /* Capture cleanup must remain safe if GDI+ rejects an unlock. */ }
                }
                converted?.Dispose();
            }

            var candidates = new List<MobMarker>();
            FindCyanCenters(candidates, width, height, clientOffsetX, clientOffsetY, expectedRadius);
            FindMagentaRingCenters(candidates, width, height, clientOffsetX, clientOffsetY, expectedRadius);
            return DeduplicateCandidates(candidates, expectedRadius).ToArray();
        }

        private void FindCyanCenters(List<MobMarker> candidates, int width, int height,
            int clientOffsetX, int clientOffsetY, float expectedRadius)
        {
            for (int start = 0; start < width * height; start++)
            {
                if (!_cyanMask[start] || _visited[start])
                    continue;

                int head = 0;
                int tail = 0;
                _queue[tail++] = start;
                _visited[start] = true;
                int count = 0;
                long sumX = 0;
                long sumY = 0;

                while (head < tail)
                {
                    int index = _queue[head++];
                    int x = index % width;
                    int y = index / width;
                    count++;
                    sumX += x;
                    sumY += y;

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = y + dy;
                        if ((uint)ny >= (uint)height) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = x + dx;
                            if ((uint)nx >= (uint)width) continue;
                            int next = ny * width + nx;
                            if (_cyanMask[next] && !_visited[next])
                            {
                                _visited[next] = true;
                                _queue[tail++] = next;
                            }
                        }
                    }
                }

                if (count < BotConstants.MobGrouping.CyanMinimumComponentPixels ||
                    count > BotConstants.MobGrouping.CyanMaximumComponentPixels)
                    continue;

                candidates.Add(new MobMarker(
                    new MobPoint(clientOffsetX + sumX / (float)count, clientOffsetY + sumY / (float)count),
                    expectedRadius,
                    1f,
                    MobMarkerSource.CyanCenter));
            }
        }

        private void FindMagentaRingCenters(List<MobMarker> candidates, int width, int height,
            int clientOffsetX, int clientOffsetY, float expectedRadius)
        {
            if (_magentaPixels.Count == 0)
                return;

            int pixelCount = width * height;
            Array.Clear(_votes, 0, pixelCount);
            float innerRadius = expectedRadius * BotConstants.MobGrouping.InnerRingRadiusRatio;
            int tolerance = Math.Max(1, BotConstants.MobGrouping.MarkerRadiusTolerancePx);
            int[] radii =
            {
                Math.Max(3, (int)Math.Round(expectedRadius)),
                Math.Max(3, (int)Math.Round(innerRadius))
            };
            int[] radialOffsets = { -tolerance, 0, tolerance };

            foreach (int pointIndex in _magentaPixels)
            {
                int px = pointIndex % width;
                int py = pointIndex / width;
                foreach (int ringRadius in radii)
                {
                    foreach (int radialOffset in radialOffsets)
                    {
                        int radius = Math.Max(2, ringRadius + radialOffset);
                        for (int angleIndex = 0; angleIndex < CenterRingAngleSamples; angleIndex++)
                        {
                            double angle = angleIndex * 2.0 * Math.PI / CenterRingAngleSamples;
                            int cx = (int)Math.Round(px - radius * Math.Cos(angle));
                            int cy = (int)Math.Round(py - radius * Math.Sin(angle));
                            if ((uint)cx < (uint)width && (uint)cy < (uint)height)
                                _votes[cy * width + cx]++;
                        }
                    }
                }
            }

            _peaks.Clear();
            int minimumVotes = BotConstants.MobGrouping.MinimumRingHoughVotes;
            for (int y = 1; y < height - 1; y++)
            {
                int row = y * width;
                for (int x = 1; x < width - 1; x++)
                {
                    int index = row + x;
                    int value = _votes[index];
                    if (value < minimumVotes)
                        continue;

                    bool isMaximum = true;
                    for (int dy = -1; dy <= 1 && isMaximum; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int neighborIndex = (y + dy) * width + x + dx;
                            int neighborValue = _votes[neighborIndex];
                            if (neighborValue > value || (neighborValue == value && neighborIndex < index))
                            {
                                isMaximum = false;
                                break;
                            }
                        }
                    }
                    if (isMaximum)
                        _peaks.Add((x, y, value));
                }
            }

            _ringCandidates.Clear();
            foreach (var peak in _peaks)
            {
                float coverage = ScoreCircularRing(
                    peak.X, peak.Y, width, height, expectedRadius, innerRadius,
                    BotConstants.MobGrouping.RingValidationTolerancePx);
                if (coverage < BotConstants.MobGrouping.MinimumRingCoverage ||
                    HasFilledMagentaCenter(peak.X, peak.Y, width, height, expectedRadius))
                    continue;
                _ringCandidates.Add((peak.X, peak.Y, peak.Votes, coverage));
            }

            // Rank geometry before raw Hough votes: an off-center hypothesis can receive many
            // votes from one arc, while the actual center has the strongest complete/partial ring.
            _ringCandidates.Sort((left, right) =>
            {
                int coverageOrder = right.Coverage.CompareTo(left.Coverage);
                return coverageOrder != 0 ? coverageOrder : right.Votes.CompareTo(left.Votes);
            });
            var acceptedRingCenters = new List<(int X, int Y)>();
            float nonMaximumDistance = Math.Max(3f, expectedRadius * 0.50f);
            float nonMaximumDistanceSquared = nonMaximumDistance * nonMaximumDistance;

            foreach (var candidate in _ringCandidates)
            {
                bool nearAccepted = false;
                foreach (var accepted in acceptedRingCenters)
                {
                    float dx = accepted.X - candidate.X;
                    float dy = accepted.Y - candidate.Y;
                    if (dx * dx + dy * dy <= nonMaximumDistanceSquared)
                    {
                        nearAccepted = true;
                        break;
                    }
                }
                if (nearAccepted)
                    continue;

                acceptedRingCenters.Add((candidate.X, candidate.Y));
                candidates.Add(new MobMarker(
                    new MobPoint(clientOffsetX + candidate.X, clientOffsetY + candidate.Y),
                    expectedRadius,
                    candidate.Coverage,
                    MobMarkerSource.MagentaRing));
            }
        }

        private float ScoreCircularRing(int centerX, int centerY, int width, int height,
            float outerRadius, float innerRadius, int tolerance)
        {
            int outerHits = 0;
            int innerHits = 0;
            for (int i = 0; i < CenterRingAngleSamples; i++)
            {
                double angle = i * 2.0 * Math.PI / CenterRingAngleSamples;
                if (HasMagentaNearCircle(centerX, centerY, width, height, outerRadius, tolerance, angle))
                    outerHits++;
                if (HasMagentaNearCircle(centerX, centerY, width, height, innerRadius, tolerance, angle))
                    innerHits++;
            }

            return Math.Max(outerHits, innerHits) / (float)CenterRingAngleSamples;
        }

        private bool HasMagentaNearCircle(int centerX, int centerY, int width, int height,
            float radius, int tolerance, double angle)
        {
            float dx = (float)Math.Cos(angle);
            float dy = (float)Math.Sin(angle);
            for (int radialOffset = -tolerance; radialOffset <= tolerance; radialOffset++)
            {
                int x = (int)Math.Round(centerX + (radius + radialOffset) * dx);
                int y = (int)Math.Round(centerY + (radius + radialOffset) * dy);
                if ((uint)x < (uint)width && (uint)y < (uint)height && _magentaMask[y * width + x])
                    return true;
            }
            return false;
        }

        private bool HasFilledMagentaCenter(int centerX, int centerY, int width, int height, float radius)
        {
            float clearRadius = radius * BotConstants.MobGrouping.RingCenterClearRadiusRatio;
            int samples = CenterRingAngleSamples / 2;
            int hitCount = 0;
            int tested = 0;
            for (int radialStep = 1; radialStep <= 2; radialStep++)
            {
                float sampleRadius = clearRadius * radialStep / 2f;
                for (int i = 0; i < samples; i++)
                {
                    double angle = i * 2.0 * Math.PI / samples;
                    int x = (int)Math.Round(centerX + sampleRadius * Math.Cos(angle));
                    int y = (int)Math.Round(centerY + sampleRadius * Math.Sin(angle));
                    if ((uint)x >= (uint)width || (uint)y >= (uint)height)
                        continue;
                    tested++;
                    if (_magentaMask[y * width + x])
                        hitCount++;
                }
            }

            int centerIndex = centerY * width + centerX;
            bool centerIsMagenta = (uint)centerX < (uint)width && (uint)centerY < (uint)height && _magentaMask[centerIndex];
            float coverage = tested == 0 ? 0 : hitCount / (float)tested;
            return centerIsMagenta || coverage > BotConstants.MobGrouping.MaximumCenterMagentaCoverage;
        }

        private void EnsureFrame(int width, int height)
        {
            if (_frame != null && _frame.Width == width && _frame.Height == height)
                return;

            _graphics?.Dispose();
            _frame?.Dispose();
            _frame = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            _graphics = Graphics.FromImage(_frame);
        }

        private void EnsureBuffers(int width, int height)
        {
            int size = checked(width * height);
            if (_magentaMask.Length >= size)
            {
                if (_rowBytes.Length < checked(width * 4))
                    _rowBytes = new byte[checked(width * 4)];
                return;
            }

            _magentaMask = new bool[size];
            _cyanMask = new bool[size];
            _visited = new bool[size];
            _votes = new int[size];
            _queue = new int[size];
            if (_rowBytes.Length < checked(width * 4))
                _rowBytes = new byte[checked(width * 4)];
        }

        private static bool TryGetGameClient(out nint hwnd, out int screenX, out int screenY,
            out Size clientSize, bool requireForeground)
        {
            hwnd = FindGameWindow();
            screenX = 0;
            screenY = 0;
            clientSize = Size.Empty;
            if (hwnd == nint.Zero || (requireForeground && GetForegroundWindow() != hwnd) ||
                !GetClientRect(hwnd, out RECT rect))
                return false;

            POINT topLeft = new() { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref topLeft))
                return false;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0)
                return false;

            screenX = topLeft.X;
            screenY = topLeft.Y;
            clientSize = new Size(width, height);
            return true;
        }

        private static nint FindGameWindow()
        {
            nint hwnd = FindWindow(null, "Legend of Ares");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Ares");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Nostalgia");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Epic Of Ares Client");
            return hwnd;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _graphics?.Dispose();
                _graphics = null;
                _frame?.Dispose();
                _frame = null;
            }
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern nint FindWindow(string? className, string windowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(nint hwnd, out RECT rect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ClientToScreen(nint hwnd, ref POINT point);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint GetForegroundWindow();

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }
    }
}
