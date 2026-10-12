using System;
using System.Drawing;

namespace DriverScanTester.Services
{
    /// <summary>Resolved geometry for one game-client capture attempt.</summary>
    internal readonly record struct LootClientGeometry(
        nint WindowHandle,
        Rectangle WindowRectangle,
        Point ClientScreenOrigin,
        Size ClientSize)
    {
        private const int ReferenceWindowX = 447;
        private const int ReferenceWindowY = 77;

        public bool IsValid => WindowHandle != nint.Zero &&
                               WindowRectangle.Width > 0 && WindowRectangle.Height > 0 &&
                               ClientSize.Width > 0 && ClientSize.Height > 0;

        public Point CoordinateOffset => new(
            WindowRectangle.Left - ReferenceWindowX,
            WindowRectangle.Top - ReferenceWindowY);

        public Point ReferenceClientOrigin => new(
            ClientScreenOrigin.X - CoordinateOffset.X,
            ClientScreenOrigin.Y - CoordinateOffset.Y);

        public Point LegacyScreenToBitmapLocal(Point legacyScreenPoint)
        {
            Point reference = ReferenceClientOrigin;
            return new Point(legacyScreenPoint.X - reference.X, legacyScreenPoint.Y - reference.Y);
        }

        public Point BitmapLocalToScreen(Point bitmapLocalPoint)
            => new(ClientScreenOrigin.X + bitmapLocalPoint.X,
                   ClientScreenOrigin.Y + bitmapLocalPoint.Y);
    }

    internal enum StableLootCaptureStatus
    {
        Success,
        WindowUnavailable,
        InvalidGeometry,
        NotForeground,
        CaptureFailed,
        GeometryUnstable
    }

    internal readonly record struct StableLootCaptureResult(
        StableLootCaptureStatus Status,
        LootClientGeometry Geometry,
        int Attempts,
        string Error)
    {
        public bool Succeeded => Status == StableLootCaptureStatus.Success;
    }

    /// <summary>
    /// Retries a capture if the window moves or resizes while the pixels are being copied.
    /// The delegates keep the consistency policy testable without a live game window.
    /// </summary>
    internal static class LootCaptureGeometry
    {
        internal const int DefaultMaximumAttempts = 3;

        internal static StableLootCaptureResult CaptureStableFrame(
            Func<LootClientGeometry?> resolveGeometry,
            Func<nint> getForegroundWindow,
            Action<LootClientGeometry> capture,
            int maximumAttempts = DefaultMaximumAttempts)
        {
            ArgumentNullException.ThrowIfNull(resolveGeometry);
            ArgumentNullException.ThrowIfNull(getForegroundWindow);
            ArgumentNullException.ThrowIfNull(capture);
            if (maximumAttempts <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumAttempts));

            LootClientGeometry lastGeometry = default;
            for (int attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                LootClientGeometry? before;
                try
                {
                    before = resolveGeometry();
                }
                catch (Exception ex)
                {
                    return new(StableLootCaptureStatus.CaptureFailed, lastGeometry, attempt,
                        $"Resolving game client geometry failed: {ex.Message}");
                }
                if (!before.HasValue)
                    return new(StableLootCaptureStatus.WindowUnavailable, lastGeometry, attempt, "Game window/client geometry could not be resolved.");
                if (!before.Value.IsValid)
                    return new(StableLootCaptureStatus.InvalidGeometry, before.Value, attempt, "Game window or client area has invalid dimensions.");
                try
                {
                    if (getForegroundWindow() != before.Value.WindowHandle)
                        return new(StableLootCaptureStatus.NotForeground, before.Value, attempt, "Resolved game window is not foreground.");
                }
                catch (Exception ex)
                {
                    return new(StableLootCaptureStatus.CaptureFailed, before.Value, attempt,
                        $"Foreground-window check failed: {ex.Message}");
                }

                lastGeometry = before.Value;
                try
                {
                    capture(before.Value);
                }
                catch (Exception ex)
                {
                    return new(StableLootCaptureStatus.CaptureFailed, before.Value, attempt, ex.Message);
                }

                LootClientGeometry? after;
                try
                {
                    after = resolveGeometry();
                }
                catch (Exception ex)
                {
                    return new(StableLootCaptureStatus.CaptureFailed, before.Value, attempt,
                        $"Revalidating game client geometry failed: {ex.Message}");
                }
                if (!after.HasValue)
                    return new(StableLootCaptureStatus.WindowUnavailable, before.Value, attempt, "Game window disappeared during capture.");
                if (!after.Value.IsValid)
                    return new(StableLootCaptureStatus.InvalidGeometry, after.Value, attempt, "Game client geometry became invalid during capture.");
                try
                {
                    if (getForegroundWindow() != after.Value.WindowHandle)
                        return new(StableLootCaptureStatus.NotForeground, after.Value, attempt, "Foreground changed during capture.");
                }
                catch (Exception ex)
                {
                    return new(StableLootCaptureStatus.CaptureFailed, after.Value, attempt,
                        $"Foreground recheck failed: {ex.Message}");
                }

                if (before.Value == after.Value)
                    return new(StableLootCaptureStatus.Success, after.Value, attempt, string.Empty);

                lastGeometry = after.Value;
            }

            return new(StableLootCaptureStatus.GeometryUnstable, lastGeometry, maximumAttempts,
                $"Game window geometry changed during all {maximumAttempts} capture attempts.");
        }
    }
}
