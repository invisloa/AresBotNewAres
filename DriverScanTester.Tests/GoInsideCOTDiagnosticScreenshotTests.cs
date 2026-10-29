using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Regression coverage for the missing GoInsideCOT failure diagnostics: every failed
    /// attempt must capture exactly one screenshot at the failure point, before the runner
    /// retries, and screenshot errors must never change the operation outcome.
    /// </summary>
    public sealed class GoInsideCOTDiagnosticScreenshotTests
    {
        private const ulong ModuleBase = 0x100000;
        private const ulong PlayerBase = 0x200000;
        private const ulong UiWindowBase = 0x300000;
        private const ulong DialogBase = 0x400000;

        [Fact]
        public async Task TestA_DialogDidNotOpenCapturesScreenshotBeforeTheAttemptReturns()
        {
            var memory = new CotMemory { DialogOpen = false };
            var log = new List<string>();
            var screenshots = new ScreenshotRecorder();
            OperationContext context = CreateContext(memory, log, screenshots.Capture);
            context.ItemSeller.LastNpcRightClickScreenPosition = (892, 413);

            bool succeeded = await RunAttemptAsync(context, scanNpc: () => true);

            Assert.False(succeeded);
            CaptureCall capture = Assert.Single(screenshots.Calls);
            Assert.Equal("GoInsideCOT", capture.Operation);
            Assert.Equal("NpcDialogNotOpened", capture.Reason);
            Assert.Equal(1, capture.Attempt);

            Assert.Contains(log, line =>
                line.Contains("[GoInsideCOT] Attempt 1/3 FAILED. Reason=NpcDialogNotOpened"));
            Assert.Contains(log, line =>
                line.Contains("Map=35") &&
                line.Contains("Position=(334,143)") &&
                line.Contains("NpcDetected=True") &&
                line.Contains("ClickPosition=(892,413)") &&
                line.Contains("IsDialogOpen=False"));
            Assert.Contains(log, line => line.Contains("Diagnostic screenshot saved:"));
            Assert.DoesNotContain(log, line => line.Contains("Retrying in"));
        }

        [Fact]
        public async Task TestB_ThreeConsecutiveFailuresCaptureThreeUniqueScreenshots()
        {
            var memory = new CotMemory();
            var log = new List<string>();
            var screenshots = new ScreenshotRecorder();
            OperationContext context = CreateContext(memory, log, screenshots.Capture);
            var runner = new OperationRunnerService(context, log.Add);

            // The real runner retry loop drives three GoInsideCOT attempts. The NPC scan
            // fails on every attempt, so no dialog/delay/click state is needed.
            BotOperations.OperationDelegate operation = (opContext, token) =>
                BotOperations.GoInsideCOTCoreAsync(
                    opContext,
                    scanAndRightClickNpc: () => false,
                    delayAsync: (_, _) => Task.CompletedTask,
                    clickAtCalibrated: (_, _, _) => { },
                    token);

            bool succeeded = await runner.RunWithRetryAsync(
                "GoInsideCOT", CancellationToken.None, operation);

            Assert.False(succeeded);
            Assert.Equal(3, screenshots.Calls.Count);
            Assert.Equal(new[] { 1, 2, 3 }, screenshots.Calls.Select(call => call.Attempt).ToArray());
            Assert.All(screenshots.Calls, call => Assert.Equal("NpcNotFound", call.Reason));
            Assert.Equal(3, screenshots.Paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());

            Assert.Contains("Attempt1", screenshots.Paths[0]);
            Assert.Contains("Attempt2", screenshots.Paths[1]);
            Assert.Contains("Attempt3", screenshots.Paths[2]);
            Assert.All(screenshots.Paths, path =>
                Assert.Contains(Path.Combine("Screenshots", "Operations", "GoInsideCOT"), path));

            Assert.Equal(2, log.Count(line => line.Contains("failed. Retrying in")));
            Assert.Contains(log, line => line.Contains("failed after 3 attempts"));

            int firstRetryIndex = log.FindIndex(line => line.Contains("failed. Retrying in"));
            int firstScreenshotIndex = log.FindIndex(line =>
                line.Contains("[GoInsideCOT] Diagnostic screenshot saved:"));
            Assert.True(firstScreenshotIndex >= 0 && firstScreenshotIndex < firstRetryIndex,
                "The first failure screenshot must be captured before the first retry.");
        }

        [Fact]
        public async Task TestC_FirstAttemptFailsAndSecondSucceedsProducesOnlyOneFailureScreenshot()
        {
            var memory = new CotMemory { DialogOpen = false };
            var log = new List<string>();
            var screenshots = new ScreenshotRecorder();
            OperationContext context = CreateContext(memory, log, screenshots.Capture);
            var runner = new OperationRunnerService(context, log.Add);

            int operationAttempt = 0;
            BotOperations.OperationDelegate operation = (opContext, token) =>
            {
                operationAttempt++;
                opContext.ItemSeller.LastNpcRightClickScreenPosition = (892, 413);

                if (operationAttempt == 1)
                {
                    // Attempt 1: NPC clicked, dialog stays closed -> failure.
                    return BotOperations.GoInsideCOTCoreAsync(
                        opContext,
                        scanAndRightClickNpc: () => true,
                        delayAsync: (_, _) => Task.CompletedTask,
                        clickAtCalibrated: (_, _, _) => { },
                        token);
                }

                // Attempt 2: NPC clicked, dialog opens, the enter click changes the map.
                memory.DialogOpen = true;
                return BotOperations.GoInsideCOTCoreAsync(
                    opContext,
                    scanAndRightClickNpc: () => true,
                    delayAsync: (_, _) => Task.CompletedTask,
                    clickAtCalibrated: (_, _, _) => memory.Map = 36,
                    token);
            };

            bool succeeded = await runner.RunWithRetryAsync(
                "GoInsideCOT", CancellationToken.None, operation);

            Assert.True(succeeded);
            CaptureCall capture = Assert.Single(screenshots.Calls);
            Assert.Equal(1, capture.Attempt);
            Assert.Equal("NpcDialogNotOpened", capture.Reason);
            Assert.Contains(log, line => line.Contains("GoInsideCOT: map changed — success."));
        }

        [Fact]
        public async Task TestD_ScreenshotFailureIsLoggedAndOriginalFailureHandlingContinues()
        {
            var memory = new CotMemory { DialogOpen = false };
            var log = new List<string>();
            OperationContext context = CreateContext(
                memory,
                log,
                capture: (_, _, _) => throw new InvalidOperationException("simulated disk failure"));

            bool succeeded = await RunAttemptAsync(context, scanNpc: () => true);

            Assert.False(succeeded);
            Assert.Contains(log, line => line.Contains("GoInsideCOT: NPC popup did not open. Aborting."));
            Assert.Contains(log, line =>
                line.Contains("[GoInsideCOT] Diagnostic screenshot failed:") &&
                line.Contains("InvalidOperationException") &&
                line.Contains("simulated disk failure"));
        }

        [Fact]
        public async Task TestE_AllGoInsideCOTFailureBranchesCaptureExactlyOneScreenshot()
        {
            // 1. NPC was not found by the scan.
            await AssertSingleFailureCaptureAsync(
                configure: _ => { },
                scanNpc: () => false,
                clickAtCalibrated: (_, _, _) => { },
                expectedReason: "NpcNotFound",
                expectedNpcDetected: false,
                expectedClicks: 0);

            // 2. NPC clicked, but the required dialog did not open.
            await AssertSingleFailureCaptureAsync(
                configure: memory => memory.DialogOpen = false,
                scanNpc: () => true,
                clickAtCalibrated: (_, _, _) => { },
                expectedReason: "NpcDialogNotOpened",
                expectedNpcDetected: true,
                expectedClicks: 0);

            // 3. Dialog opened and the option was clicked, but the map never changed.
            await AssertSingleFailureCaptureAsync(
                configure: memory => memory.DialogOpen = true,
                scanNpc: () => true,
                clickAtCalibrated: (_, _, _) => { },
                expectedReason: "MapDidNotChange",
                expectedNpcDetected: true,
                expectedClicks: 2);
        }

        [Fact]
        public async Task TestF_OneFailedAttemptNeverProducesDuplicateScreenshotsFromNestedHandlers()
        {
            var memory = new CotMemory { DialogOpen = true };
            var log = new List<string>();
            var screenshots = new ScreenshotRecorder();
            OperationContext context = CreateContext(memory, log, screenshots.Capture);
            context.ItemSeller.LastNpcRightClickScreenPosition = (900, 420);
            int screenshotsSeenAtFirstClick = -1;
            int clickCount = 0;

            bool succeeded = await RunAttemptAsync(
                context,
                scanNpc: () => true,
                clickAtCalibrated: (_, _, _) =>
                {
                    if (clickCount++ == 0)
                        screenshotsSeenAtFirstClick = screenshots.Calls.Count;
                });

            Assert.False(succeeded);
            Assert.Equal(2, clickCount);
            Assert.Equal(0, screenshotsSeenAtFirstClick);
            Assert.Single(screenshots.Calls);
            Assert.Equal("MapDidNotChange", screenshots.Calls[0].Reason);
        }

        [Fact]
        public void FailureScreenshotNamesIncludeOperationAttemptAndReason()
        {
            string fileName = ScreenshotService.BuildOperationFailureScreenshotFileName(
                "GoInsideCOT",
                "NpcDialogNotOpened",
                attemptNumber: 2,
                new DateTime(2026, 10, 29, 0, 29, 5, 123));

            Assert.Equal(
                "2026-10-29_00-29-05-123_GoInsideCOT_Attempt2_NpcDialogNotOpened.png",
                fileName);

            Assert.EndsWith(
                Path.Combine("Screenshots", "Operations", "GoInsideCOT"),
                ScreenshotService.ResolveOperationScreenshotsDir("GoInsideCOT").TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        private static async Task AssertSingleFailureCaptureAsync(
            Action<CotMemory> configure,
            Func<bool> scanNpc,
            Action<int, int, string> clickAtCalibrated,
            string expectedReason,
            bool expectedNpcDetected,
            int expectedClicks)
        {
            var memory = new CotMemory();
            configure(memory);
            var log = new List<string>();
            var screenshots = new ScreenshotRecorder();
            OperationContext context = CreateContext(memory, log, screenshots.Capture);
            context.ItemSeller.LastNpcRightClickScreenPosition = (892, 413);
            int clicks = 0;

            bool succeeded = await RunAttemptAsync(
                context,
                scanNpc,
                clickAtCalibrated: (x, y, prefix) =>
                {
                    clicks++;
                    clickAtCalibrated(x, y, prefix);
                });

            Assert.False(succeeded);
            CaptureCall capture = Assert.Single(screenshots.Calls);
            Assert.Equal(expectedReason, capture.Reason);
            Assert.Equal(1, capture.Attempt);
            Assert.Equal(expectedClicks, clicks);
            Assert.Contains(log, line =>
                line.Contains($"[GoInsideCOT] Attempt 1/3 FAILED. Reason={expectedReason}"));
            Assert.Contains(log, line =>
                line.Contains($"NpcDetected={expectedNpcDetected}"));
            Assert.Contains(log, line => line.Contains("Diagnostic screenshot saved:"));
        }

        private static Task<bool> RunAttemptAsync(
            OperationContext context,
            Func<bool> scanNpc,
            Action<int, int, string>? clickAtCalibrated = null)
        {
            return BotOperations.GoInsideCOTCoreAsync(
                context,
                scanAndRightClickNpc: scanNpc,
                delayAsync: (_, _) => Task.CompletedTask,
                clickAtCalibrated: clickAtCalibrated ?? ((_, _, _) => { }),
                CancellationToken.None);
        }

        private static OperationContext CreateContext(
            CotMemory memory,
            List<string> log,
            Func<string, string, int, string?> capture)
        {
            GameMemoryService memoryService = memory.CreateService(log.Add);
            var pathRunner = new PathRunnerService(memoryService, log.Add);
            var itemSeller = new ItemSellerService(memoryService, log.Add);
            return new OperationContext(
                memoryService,
                pathRunner,
                itemSeller,
                new BotProfile(),
                log.Add,
                () => { },
                diagnosticScreenshotCapture: capture);
        }

        private sealed record CaptureCall(
            string Operation,
            string Reason,
            int Attempt,
            string Path);

        private sealed class ScreenshotRecorder
        {
            public List<CaptureCall> Calls { get; } = new();

            public IReadOnlyList<string> Paths => Calls.Select(call => call.Path).ToList();

            public string Capture(string operationName, string failureReason, int attemptNumber)
            {
                string fileName = ScreenshotService.BuildOperationFailureScreenshotFileName(
                    operationName, failureReason, attemptNumber, DateTime.Now);
                string path = Path.Combine(
                    ScreenshotService.ResolveOperationScreenshotsDir(operationName),
                    fileName);

                Calls.Add(new CaptureCall(operationName, failureReason, attemptNumber, path));
                return path;
            }
        }

        /// <summary>
        /// Minimal live-memory replacement for the exact reads GoInsideCOT and its
        /// diagnostics use: map, X/Y, player pointer and the S_IsDialogOpen1 chain.
        /// </summary>
        private sealed class CotMemory
        {
            private readonly Dictionary<ulong, byte[]> _memory = new();

            public int Map { get; set; } = 35;
            public short X { get; set; } = 334;
            public short Y { get; set; } = 143;
            public bool DialogOpen { get; set; }

            public GameMemoryService CreateService(Action<string> log)
            {
                SetPointer(ModuleBase + BotConstants.MemoryOffsets.PlayerPtr, PlayerBase);
                SetPointer(ModuleBase + BotConstants.MemoryOffsets.UiWindowM, UiWindowBase);
                SetPointer(UiWindowBase + (ulong)BotConstants.MemoryOffsets.DialogOpenPtr, DialogBase);

                GameMemoryService.ReadMemoryDelegate read =
                    (uint pid, ulong address, byte[] buffer, out uint bytesRead) =>
                    {
                        bytesRead = 0;
                        RefreshDynamicValues();

                        if (!_memory.TryGetValue(address, out byte[]? stored) ||
                            stored.Length < buffer.Length)
                        {
                            return false;
                        }

                        Buffer.BlockCopy(stored, 0, buffer, 0, buffer.Length);
                        bytesRead = (uint)buffer.Length;
                        return true;
                    };

                GameMemoryService.WriteMemoryDelegate write =
                    (uint pid, ulong address, byte[] buffer, out uint bytesWritten) =>
                    {
                        bytesWritten = 0;
                        return false;
                    };

                return new GameMemoryService(0, read, write, ModuleBase, 8, log);
            }

            private void RefreshDynamicValues()
            {
                SetInt(PlayerBase + BotConstants.MemoryOffsets.CurrentMap, Map);
                SetShort(PlayerBase + BotConstants.MemoryOffsets.X, X);
                SetShort(PlayerBase + BotConstants.MemoryOffsets.Y, Y);
                _memory[DialogBase + (ulong)BotConstants.MemoryOffsets.DialogOpen1] =
                    new[] { DialogOpen ? (byte)1 : (byte)0 };
            }

            private void SetPointer(ulong address, ulong value)
                => _memory[address] = BitConverter.GetBytes(value);

            private void SetInt(ulong address, int value)
                => _memory[address] = BitConverter.GetBytes(value);

            private void SetShort(ulong address, short value)
                => _memory[address] = BitConverter.GetBytes(value);
        }
    }
}
