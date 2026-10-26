using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using DriverScanTester;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class LootPinkCaptureRegressionTests
    {
        private const int ClientWidth = 1024;
        private const int ClientHeight = 768;
        private static readonly Color ScrollPink = Color.FromArgb(255, 20, 147);

        [Fact]
        public void ReportedCandidateIsDetectedByNormalCombinedAndPinkOnlyButNotAquamarine()
        {
            using Bitmap frame = CreateReportedCandidateFrame();

            LootSystem.PinkFrameAnalysisResult pinkOnly = LootSystem.AnalyzePinkFrame(frame);
            LootSystem.LootPreScanResult combined = LootSystem.AnalyzeNormalLootCandidates(
                frame, new Rectangle(0, 0, frame.Width, frame.Height),
                BotConstants.Loot.PassivePreScanMaxCandidateComponents);
            var aquamarinePoints = new List<Point>();
            var pinkPoints = new List<Point>();
            LootSystem.PixelCollectionStats pixelStats = LootSystem.CollectTargetPointsFromBitmap(
                frame, pinkOnly: false, aquamarinePoints, pinkPoints);
            var pinkStats = new LootSystem.PixelCollectionStats(
                pixelStats.PixelsExamined, pixelStats.RawPinkPixels,
                pixelStats.MarkerPixelsRejected, pinkPoints.Count);
            List<LootSystem.TypedLootCandidate> typedCandidates = LootSystem.AnalyzeLootCandidateMasks(
                frame, aquamarinePoints, pinkPoints, pinkStats, LootSystem.LootScanMode.NormalCombined,
                out _, out _, out _, out _);

            LootSystem.LootComponent candidate = Assert.Single(pinkOnly.CandidateComponents);
            Assert.Equal(new Rectangle(539, 315, 12, 11),
                Rectangle.FromLTRB(candidate.XMin, candidate.YMin, candidate.XMax + 1, candidate.YMax + 1));
            Assert.Equal(74, candidate.Area);
            Assert.InRange(candidate.Area / (float)(candidate.Width * candidate.Height), 0.56f, 0.562f);
            Assert.Equal(LootSystem.LootPreScanOutcome.PotentialCandidate, combined.Outcome);
            Assert.Equal(1, combined.CandidateComponents);
            Assert.Equal(new Rectangle(539, 315, 12, 11), combined.CandidateBounds);
            Assert.Equal(74, Assert.Single(combined.Candidates!).Component.Area);
            Assert.Equal(LootSystem.LootCandidateKind.SodSopPink, Assert.Single(typedCandidates).Kind);
            Assert.Empty(aquamarinePoints);
        }

        [Fact]
        public void ValidPinkCandidateSurvivesArtificialMagentaMarkerPixels()
        {
            using Bitmap frame = CreateReportedCandidateFrame();
            using (Graphics graphics = Graphics.FromImage(frame))
            using (var markerBrush = new SolidBrush(Color.FromArgb(255, 0, 255)))
                graphics.FillRectangle(markerBrush, 700, 600, 12, 12);

            LootSystem.PinkFrameAnalysisResult result = LootSystem.AnalyzePinkFrame(frame);

            Assert.Equal(1, result.LootCandidates);
            Assert.True(result.MarkerPixelsRejected >= 144);
            Assert.Equal(74, Assert.Single(result.CandidateComponents).Area);
        }

        [Fact]
        public void PassiveCombinedPreScanFindsPinkOutsideItsAquamarineRegion()
        {
            using Bitmap frame = CreateReportedCandidateFrame();

            LootSystem.LootPreScanResult result = LootSystem.AnalyzeNormalLootCandidates(
                frame, new Rectangle(0, 0, 100, 100),
                BotConstants.Loot.PassivePreScanMaxCandidateComponents);

            Assert.Equal(LootSystem.LootPreScanOutcome.PotentialCandidate, result.Outcome);
            Assert.Equal(1, result.CandidateComponents);
            Assert.Equal(new Rectangle(539, 315, 12, 11), result.CandidateBounds);
        }

        [Fact]
        public void ScreenshotLikeScroll_IsOneCandidateAndSuppliesInClientProbePoints()
        {
            using Bitmap frame = CreateScreenshotLikeFrame();

            LootSystem.PinkFrameAnalysisResult result = LootSystem.AnalyzePinkFrame(frame);

            Assert.Equal(ClientWidth * ClientHeight, result.TotalPixelsExamined);
            Assert.Equal(432, result.RawPinkPixels);
            Assert.Equal(0, result.MarkerPixelsRejected);
            Assert.Equal(432, result.PinkPixels);
            Assert.Equal(2, result.Components);
            Assert.Equal(1, result.LootCandidates);
            Assert.Equal(1, result.SizeOrShapeRejectedComponents);
            Assert.Equal(1, result.TinyComponents);
            Assert.Equal(0, result.MobMarkerRings);
            Assert.Equal(430, result.FinalProbeCount);

            LootSystem.LootComponent scroll = Assert.Single(result.CandidateComponents);
            Assert.Equal(new Rectangle(463, 298, 26, 29),
                Rectangle.FromLTRB(scroll.XMin, scroll.YMin, scroll.XMax + 1, scroll.YMax + 1));
            Assert.Equal(430, scroll.Area);
            Assert.InRange(scroll.Area / (float)(scroll.Width * scroll.Height), 0.55f, 0.59f);
            Assert.All(scroll.Points, point =>
            {
                Assert.InRange(point.X, 0, ClientWidth - 1);
                Assert.InRange(point.Y, 0, ClientHeight - 1);
            });
            Assert.Contains(result.CandidateDetails, component =>
                component.Bounds == new Rectangle(463, 298, 26, 29) &&
                component.PixelCount == 430 && component.IsLootCandidate);
        }

        [Fact]
        public void ScreenshotPinkShadeIsNotRemovedByArtificialMarkerMask()
        {
            using Bitmap frame = new(ClientWidth, ClientHeight, PixelFormat.Format32bppArgb);
            FillDiamond(frame, new Rectangle(463, 298, 26, 29), ScrollPink, 1.08f);
            // A nearby artificial marker remains excluded from the loot mask.
            DrawMagentaRing(frame, centerX: 505, centerY: 312, radius: 16);

            LootSystem.PinkFrameAnalysisResult result = LootSystem.AnalyzePinkFrame(frame);

            Assert.True(LootSystem.IsSodSopPinkPixel(255, 20, 147));
            Assert.False(LootSystem.IsPinkLootMaskPixel(Color.FromArgb(255, 0, 255)));
            Assert.Equal(1, result.LootCandidates);
            Assert.Equal(0, result.MobMarkerRings);
            Assert.Contains(result.CandidateComponents, component => component.Area >= 400);
        }

        [Fact]
        public void PartiallyOccludedScrollRemainsCandidateWhenItsVisibleBoundsAreLargeEnough()
        {
            using Bitmap frame = new(ClientWidth, ClientHeight, PixelFormat.Format32bppArgb);
            FillDiamond(frame, new Rectangle(463, 298, 26, 29), ScrollPink, 1.08f);
            // Occlude an interior patch without removing the qualifying outer extent.
            using (Graphics graphics = Graphics.FromImage(frame))
                graphics.FillRectangle(Brushes.Black, 472, 306, 7, 10);

            LootSystem.PinkFrameAnalysisResult result = LootSystem.AnalyzePinkFrame(frame);

            Assert.Single(result.CandidateComponents);
            Assert.True(result.CandidateComponents[0].Area >= 350);
        }

        [Theory]
        [InlineData(255, 20, 147)]
        [InlineData(255, 34, 175)]
        [InlineData(239, 21, 148)]
        public void RequiredScrollShadesPassTheProductionMask(byte r, byte g, byte b)
        {
            Assert.True(LootSystem.IsSodSopPinkPixel(r, g, b));
            Assert.True(LootSystem.IsPinkLootMaskPixel(r, g, b));
        }

        [Fact]
        public void OrdinaryTerrainDoesNotProducePinkCandidates()
        {
            using Bitmap frame = new(ClientWidth, ClientHeight, PixelFormat.Format32bppArgb);
            FillDiamond(frame, new Rectangle(463, 298, 26, 29), Color.FromArgb(82, 116, 65), 1.08f);

            LootSystem.PinkFrameAnalysisResult result = LootSystem.AnalyzePinkFrame(frame);

            Assert.Equal(0, result.RawPinkPixels);
            Assert.Empty(result.CandidateComponents);
        }

        [Fact]
        public void BulkReaderMatchesGetPixelReferenceForPositiveStrideFrame()
        {
            using Bitmap frame = new(91, 57, PixelFormat.Format32bppArgb);
            FillDiamond(frame, new Rectangle(11, 8, 26, 29), ScrollPink, 1.08f);
            frame.SetPixel(78, 49, Color.FromArgb(255, 0, 255));

            AssertBulkMatchesReference(frame);
        }

        [Fact]
        public void BulkReaderMatchesGetPixelReferenceForNegativeStrideFrame()
        {
            const int width = 64;
            const int height = 48;
            int rowBytes = width * 4;
            IntPtr storage = Marshal.AllocHGlobal(rowBytes * height);
            try
            {
                byte[] zeros = new byte[rowBytes * height];
                Marshal.Copy(zeros, 0, storage, zeros.Length);
                IntPtr scan0 = IntPtr.Add(storage, rowBytes * (height - 1));
                for (int y = 9; y < 38; y++)
                for (int x = 12; x < 38; x++)
                {
                    if (Math.Abs(x - 24.5) / 13 + Math.Abs(y - 23) / 14.5 > 1.08)
                        continue;
                    IntPtr pixel = IntPtr.Add(scan0, -y * rowBytes + x * 4);
                    Marshal.WriteByte(pixel, 0, ScrollPink.B);
                    Marshal.WriteByte(pixel, 1, ScrollPink.G);
                    Marshal.WriteByte(pixel, 2, ScrollPink.R);
                    Marshal.WriteByte(pixel, 3, 255);
                }

                using var frame = new Bitmap(
                    width, height, -rowBytes, PixelFormat.Format32bppArgb, scan0);
                AssertBulkMatchesReference(frame);
            }
            finally
            {
                Marshal.FreeHGlobal(storage);
            }
        }

        [Fact]
        public void PendingImmediatePinkScanBlocksMovementCameraCursorAndTargetWrites()
        {
            GameMemoryService.ReadMemoryDelegate read =
                (uint pid, ulong address, byte[] buffer, out uint bytesRead) =>
                {
                    bytesRead = 0;
                    return false;
                };
            GameMemoryService.WriteMemoryDelegate write =
                (uint pid, ulong address, byte[] buffer, out uint bytesWritten) =>
                {
                    bytesWritten = 0;
                    return false;
                };
            var service = new GameMemoryService(0, read, write, 0, 8, _ => { });
            var loot = new LootSystem(service, _ => { }) { PinkLootOnlyMode = true };
            loot.RequestPinkScan();

            bool cameraWrite = false;
            bool cursorWrite = false;
            bool tabWrite = false;
            Assert.False(loot.TryRunMovementCameraAction(() => cameraWrite = true));
            Assert.False(loot.TryRunMovementCursorAction(() => cursorWrite = true));
            Assert.False(loot.TryRunMovementTargetAction(() => tabWrite = true));
            Assert.False(cameraWrite || cursorWrite || tabWrite);

            loot.PinkLootOnlyMode = false;
            Assert.True(loot.TryRunMovementCameraAction(() => cameraWrite = true));
            Assert.True(loot.TryRunMovementCursorAction(() => cursorWrite = true));
            Assert.True(loot.TryRunMovementTargetAction(() => tabWrite = true));
            Assert.True(cameraWrite && cursorWrite && tabWrite);
            loot.Stop();
        }

        [Fact]
        public void InvalidCaptureIsFailureAndCannotBecomeAnEmptyPinkPass()
        {
            int foregroundChecks = 0;
            StableLootCaptureResult capture = LootCaptureGeometry.CaptureStableFrame(
                resolveGeometry: () => null,
                getForegroundWindow: () => { foregroundChecks++; return nint.Zero; },
                capture: _ => throw new InvalidOperationException("must not capture without geometry"));

            Assert.Equal(StableLootCaptureStatus.WindowUnavailable, capture.Status);
            Assert.False(capture.Succeeded);
            Assert.Equal(0, foregroundChecks);
            Assert.NotEqual(StableLootCaptureStatus.Success, capture.Status);

            LootClientGeometry valid = MakeGeometry(
                new Rectangle(447, 77, 1040, 810), new Point(450, 103), new Size(1024, 768));
            StableLootCaptureResult copyFailure = LootCaptureGeometry.CaptureStableFrame(
                () => valid, () => valid.WindowHandle,
                _ => throw new InvalidOperationException("GDI copy failed"));
            Assert.Equal(StableLootCaptureStatus.CaptureFailed, copyFailure.Status);
            Assert.False(copyFailure.Succeeded);
        }

        [Fact]
        public void StableCaptureUsesOriginalPositionAndMapsLegacyProbeCoordinates()
        {
            LootClientGeometry geometry = MakeGeometry(
                new Rectangle(447, 77, 1040, 810), new Point(450, 103), new Size(1024, 768));
            int copies = 0;
            StableLootCaptureResult result = LootCaptureGeometry.CaptureStableFrame(
                () => geometry,
                () => geometry.WindowHandle,
                captured => { Assert.Equal(geometry, captured); copies++; });

            Assert.True(result.Succeeded);
            Assert.Equal(1, copies);
            Assert.Equal(new Point(926, 415), geometry.BitmapLocalToScreen(new Point(476, 312)));
            Assert.Equal(new Point(476, 312), geometry.LegacyScreenToBitmapLocal(new Point(926, 415)));
        }

        [Theory]
        [InlineData(180, 0, 0, 0)]
        [InlineData(-145, 0, 0, 0)]
        [InlineData(0, -130, 0, 0)]
        [InlineData(0, 205, 0, 0)]
        [InlineData(0, 0, 128, 64)]
        public void CaptureRetriesAgainstMovedOrResizedWindowAndUsesNewOrigin(
            int dx, int dy, int widthDelta, int heightDelta)
        {
            LootClientGeometry original = MakeGeometry(
                new Rectangle(447, 77, 1040, 810), new Point(450, 103), new Size(1024, 768));
            LootClientGeometry moved = MakeGeometry(
                new Rectangle(447 + dx, 77 + dy, 1040 + widthDelta, 810 + heightDelta),
                new Point(450 + dx, 103 + dy),
                new Size(1024 + widthDelta, 768 + heightDelta));
            LootClientGeometry current = original;
            var copiedGeometries = new List<LootClientGeometry>();
            StableLootCaptureResult result = LootCaptureGeometry.CaptureStableFrame(
                () => current,
                () => current.WindowHandle,
                captured =>
                {
                    copiedGeometries.Add(captured);
                    if (copiedGeometries.Count == 1)
                        current = moved;
                });

            Assert.True(result.Succeeded);
            Assert.Equal(2, result.Attempts);
            Assert.Equal(original, copiedGeometries[0]);
            Assert.Equal(moved, copiedGeometries[1]);
            Assert.Equal(moved, result.Geometry);
            Point local = new(476, 312);
            Point screen = moved.BitmapLocalToScreen(local);
            Assert.Equal(new Point(moved.ClientScreenOrigin.X + local.X,
                moved.ClientScreenOrigin.Y + local.Y), screen);
            Assert.Equal(local, moved.LegacyScreenToBitmapLocal(new Point(926, 415)));
        }

        [Fact]
        public void InvalidClientGeometryAndForegroundMismatchAreReported()
        {
            LootClientGeometry invalid = MakeGeometry(
                new Rectangle(10, 10, 0, 0), new Point(10, 10), Size.Empty);
            StableLootCaptureResult invalidResult = LootCaptureGeometry.CaptureStableFrame(
                () => invalid, () => invalid.WindowHandle, _ => { });
            Assert.Equal(StableLootCaptureStatus.InvalidGeometry, invalidResult.Status);

            LootClientGeometry valid = MakeGeometry(
                new Rectangle(447, 77, 1040, 810), new Point(450, 103), new Size(1024, 768));
            StableLootCaptureResult focusResult = LootCaptureGeometry.CaptureStableFrame(
                () => valid, () => (nint)123, _ => { });
            Assert.Equal(StableLootCaptureStatus.NotForeground, focusResult.Status);
        }

        [Fact]
        public void UnstableGeometryFailsAfterBoundedRetries()
        {
            int position = 0;
            StableLootCaptureResult result = LootCaptureGeometry.CaptureStableFrame(
                () => MakeGeometry(
                    new Rectangle(447 + position, 77, 1040, 810),
                    new Point(450 + position, 103), new Size(1024, 768)),
                () => (nint)1,
                _ => position++);

            Assert.Equal(StableLootCaptureStatus.GeometryUnstable, result.Status);
            Assert.Equal(LootCaptureGeometry.DefaultMaximumAttempts, result.Attempts);
            Assert.Equal(LootCaptureGeometry.DefaultMaximumAttempts, position);
        }

        private static void AssertBulkMatchesReference(Bitmap frame)
        {
            var bulk = new List<Point>();
            var reference = new List<Point>();
            LootSystem.PixelCollectionStats bulkStats =
                LootSystem.CollectTargetPointsFromBitmap(frame, pinkOnly: true, bulk);
            LootSystem.PixelCollectionStats referenceStats =
                LootSystem.CollectTargetPointsFromBitmapReference(
                    frame, 0, frame.Width, 0, frame.Height,
                    pinkOnly: true, reference);

            Assert.Equal(reference, bulk);
            Assert.Equal(referenceStats, bulkStats);
        }

        private static Bitmap CreateReportedCandidateFrame()
        {
            var frame = new Bitmap(ClientWidth, ClientHeight, PixelFormat.Format32bppArgb);
            int[] rowWidths = { 2, 4, 6, 8, 10, 12, 12, 8, 6, 4, 2 };
            for (int row = 0; row < rowWidths.Length; row++)
            {
                int width = rowWidths[row];
                int startX = 539 + (12 - width) / 2;
                for (int offset = 0; offset < width; offset++)
                    frame.SetPixel(startX + offset, 315 + row, Color.FromArgb(255, 34, 175));
            }
            return frame;
        }

        private static Bitmap CreateScreenshotLikeFrame()
        {
            var frame = new Bitmap(ClientWidth, ClientHeight, PixelFormat.Format32bppArgb);
            FillDiamond(frame, new Rectangle(463, 298, 26, 29), ScrollPink, 1.08f);
            frame.SetPixel(777, 703, ScrollPink);
            frame.SetPixel(778, 703, ScrollPink);
            return frame;
        }

        private static void FillDiamond(Bitmap bitmap, Rectangle bounds, Color color, float extent)
        {
            double centerX = bounds.Left + (bounds.Width - 1) / 2.0;
            double centerY = bounds.Top + (bounds.Height - 1) / 2.0;
            double radiusX = bounds.Width / 2.0;
            double radiusY = bounds.Height / 2.0;
            for (int y = bounds.Top; y < bounds.Bottom; y++)
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                double normalized = Math.Abs(x - centerX) / radiusX + Math.Abs(y - centerY) / radiusY;
                if (normalized <= extent)
                    bitmap.SetPixel(x, y, color);
            }
        }

        private static void DrawMagentaRing(Bitmap bitmap, int centerX, int centerY, int radius)
        {
            for (int angle = 0; angle < 360; angle += 2)
            {
                double radians = angle * Math.PI / 180.0;
                int x = centerX + (int)Math.Round(radius * Math.Cos(radians));
                int y = centerY + (int)Math.Round(radius * Math.Sin(radians));
                if (x >= 0 && x < bitmap.Width && y >= 0 && y < bitmap.Height)
                    bitmap.SetPixel(x, y, Color.FromArgb(255, 0, 255));
            }
        }

        private static LootClientGeometry MakeGeometry(Rectangle window, Point clientOrigin, Size clientSize)
            => new((nint)1, window, clientOrigin, clientSize);
    }
}
