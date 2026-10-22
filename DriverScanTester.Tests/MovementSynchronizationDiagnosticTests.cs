using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Unit tests for the movement-synchronization diagnostic. Every input backend is a
    /// fake: these tests never inject real keyboard or mouse input and never touch a game.
    /// </summary>
    public sealed class MovementSynchronizationDiagnosticTests : IDisposable
    {
        private const uint TestPid = 4242;
        private readonly string _reportRoot;
        private readonly List<FakeKeyboardBackend> _backends = new();
        private readonly List<string> _mouseCalls = new();
        private int _writeCalls;
        private bool _failBackendDown;

        public MovementSynchronizationDiagnosticTests()
        {
            _reportRoot = Path.Combine(
                Path.GetTempPath(),
                "dstest-movementdiag-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_reportRoot);
        }

        public void Dispose()
        {
            try { Directory.Delete(_reportRoot, recursive: true); } catch { }
        }

        private MovementDiagnosticSettings FastSettings(
            MovementDiagnosticScenario scenario = MovementDiagnosticScenario.KeybdEventContinuousW,
            bool advanced = false,
            bool autoObservation = false,
            int[]? checkpoints = null)
        {
            return new MovementDiagnosticSettings
            {
                DurationMs = scenario == MovementDiagnosticScenario.ManualKeyboardBaseline ? 150 : 200,
                CountdownMs = 0,
                RepeatIntervalMs = 40,
                SampleIntervalMs = 20,
                PulseDownMs = 30,
                PulseUpMs = 20,
                FocusValidationTimeoutMs = 0,
                AdvancedModeEnabled = advanced,
                AutoStartRecoveryObservation = autoObservation,
                RecoveryCheckpointsSeconds = checkpoints ?? Array.Empty<int>(),
                MaxObservationMinutes = 1,
                SafeWaypointX = scenario == MovementDiagnosticScenario.ExistingMovementSystemOnlyMove ? 105f : null,
                SafeWaypointY = scenario == MovementDiagnosticScenario.ExistingMovementSystemOnlyMove ? 100f : null,
                MaxSafeWaypointDistance = 30f
            };
        }

        private MovementSynchronizationDiagnosticService CreateService(
            string? conflict = null,
            Func<(float x, float y, bool success)>? readPlayerPosition = null,
            Func<MovementDiagnosticOnlyMoveContext, CancellationToken, Task<MovementDiagnosticMovementRunResult>>? onlyMoveRunner = null,
            Func<uint, Action<string>, IMovementDiagnosticMouseTurnAssist>? mouseAssistFactory = null)
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
                    _writeCalls++;
                    bytesWritten = 0;
                    return false;
                };
            var memory = new GameMemoryService(TestPid, read, write, 0, 8, _ => { });

            return new MovementSynchronizationDiagnosticService(
                log: _ => { },
                isAttached: () => true,
                getPid: () => TestPid,
                createMemoryService: () => memory,
                focusGame: () => { },
                getConflictReason: () => conflict,
                readPlayerPosition: readPlayerPosition,
                getForegroundProcessId: () => TestPid,
                backendFactory: mode =>
                {
                    var backend = new FakeKeyboardBackend(mode) { FailDown = _failBackendDown };
                    _backends.Add(backend);
                    return backend;
                },
                mouseAssistFactory: mouseAssistFactory ??
                    ((pid, log) => new FakeMouseAssist(_mouseCalls)),
                onlyMoveRunner: onlyMoveRunner,
                reportWriter: new MovementDiagnosticReportWriter(_ => { }, _reportRoot));
        }

        private static async Task WaitForTestAsync(MovementSynchronizationDiagnosticService service)
        {
            Assert.True(await service.WaitForTestCompletionAsync(TimeSpan.FromSeconds(8)),
                "Diagnostic test did not finish within the test timeout.");
        }

        private static async Task<MovementDiagnosticSession> StartAndWaitAsync(
            MovementSynchronizationDiagnosticService service,
            MovementDiagnosticScenario scenario,
            MovementDiagnosticSettings settings)
        {
            MovementDiagnosticStartResult start = await service.StartTestAsync(scenario, settings);
            Assert.True(start.Accepted, start.Message);
            await WaitForTestAsync(service);
            MovementDiagnosticSession? session = service.GetCurrentSessionSnapshotForTesting();
            Assert.NotNull(session);
            return session!;
        }

        [Fact]
        public async Task ManualBaselineSendsNoSyntheticInputAndRecordsSamples()
        {
            var service = CreateService();
            MovementDiagnosticSession session = await StartAndWaitAsync(
                service,
                MovementDiagnosticScenario.ManualKeyboardBaseline,
                FastSettings(MovementDiagnosticScenario.ManualKeyboardBaseline));

            Assert.Empty(_backends);
            Assert.Equal(0, session.TotalKeyDownEvents);
            Assert.Equal(0, session.TotalKeyUpEvents);
            Assert.Equal(0, session.InputFailureCount);
            Assert.Equal(0, _writeCalls);
            Assert.Contains(session.Events, e => e.EventType == "INPUT_INJECTION_STOPPED");
            Assert.True(session.Samples.Count > 0, "The baseline must record samples without injecting input.");
        }

        [Fact]
        public async Task ContinuousKeybdEventWProducesExactlyOneDownAndOneUp()
        {
            var service = CreateService();
            MovementDiagnosticSession session = await StartAndWaitAsync(
                service,
                MovementDiagnosticScenario.KeybdEventContinuousW,
                FastSettings());

            FakeKeyboardBackend backend = Assert.Single(_backends);
            Assert.Equal(MovementDiagnosticInputMode.KeybdEvent, backend.Mode);
            Assert.Equal(new[] { "W:down", "W:up" }, backend.Calls);
            Assert.Equal(1, session.TotalKeyDownEvents);
            Assert.Equal(1, session.TotalKeyUpEvents);
            Assert.Equal(0, session.TotalRepeatEvents);
            Assert.True(session.InputCleanupComplete);
        }

        [Fact]
        public async Task RepeatedKeybdEventWUsesConfiguredRepeatsAndOneRelease()
        {
            var service = CreateService();
            MovementDiagnosticSettings settings = FastSettings();
            settings.DurationMs = 300;
            settings.RepeatIntervalMs = 40;
            MovementDiagnosticSession session = await StartAndWaitAsync(
                service,
                MovementDiagnosticScenario.KeybdEventRepeatedW,
                settings);

            FakeKeyboardBackend backend = Assert.Single(_backends);
            Assert.Equal(1, session.TotalKeyDownEvents);
            Assert.Equal(1, session.TotalKeyUpEvents);
            Assert.True(session.TotalRepeatEvents >= 2, $"Expected repeats, got {session.TotalRepeatEvents}.");
            Assert.Equal(backend.Calls.Count(c => c == "W:down"), session.TotalRepeatEvents + 1);
            Assert.Equal(1, backend.Calls.Count(c => c == "W:up"));
            Assert.All(
                session.Events.Where(e => e.EventType == "KEY_REPEAT"),
                e => Assert.True(e.ActualIntervalMs.HasValue));
        }

        [Theory]
        [InlineData(MovementDiagnosticScenario.SendInputVirtualKey, MovementDiagnosticInputMode.SendInputVirtualKey)]
        [InlineData(MovementDiagnosticScenario.SendInputScanCode, MovementDiagnosticInputMode.SendInputScanCode)]
        public async Task SendInputScenariosUseTheirExpectedBackendAndOneDownOneUp(
            MovementDiagnosticScenario scenario,
            MovementDiagnosticInputMode expectedMode)
        {
            var service = CreateService();
            MovementDiagnosticSession session = await StartAndWaitAsync(service, scenario, FastSettings(scenario));

            FakeKeyboardBackend backend = Assert.Single(_backends);
            Assert.Equal(expectedMode, backend.Mode);
            Assert.Equal(new[] { "W:down", "W:up" }, backend.Calls);
            Assert.Equal(expectedMode, session.PrimaryInputMode);
        }

        [Theory]
        [InlineData(MovementDiagnosticInputMode.SendInputVirtualKey, false, 0u, 0x57, 0)]
        [InlineData(MovementDiagnosticInputMode.SendInputVirtualKey, true, 0x0002u, 0x57, 0)]
        [InlineData(MovementDiagnosticInputMode.SendInputScanCode, false, 0x0008u, 0, 0x11)]
        [InlineData(MovementDiagnosticInputMode.SendInputScanCode, true, 0x000Au, 0, 0x11)]
        public void SendInputUsesTheCorrectVirtualKeyOrScanCodeFlags(
            MovementDiagnosticInputMode mode,
            bool keyUp,
            uint expectedFlags,
            ushort expectedVk,
            ushort expectedScan)
        {
            var input = SendInputDiagnosticKeyboardBackend.CreateInputForTesting(
                mode,
                GameInput.VK_W,
                GameInput.SCAN_W,
                keyUp);

            Assert.Equal(1u, input.type);
            Assert.Equal(expectedFlags, input.U.ki.dwFlags);
            Assert.Equal(expectedVk, input.U.ki.wVk);
            Assert.Equal(expectedScan, input.U.ki.wScan);
        }

        [Fact]
        public async Task PulsedWAlternatesDownAndUpAndReleasesAtTheEnd()
        {
            var service = CreateService();
            MovementDiagnosticSettings settings = FastSettings(MovementDiagnosticScenario.KeybdEventPulsedW);
            settings.DurationMs = 220;
            settings.PulseDownMs = 30;
            settings.PulseUpMs = 20;
            MovementDiagnosticSession session = await StartAndWaitAsync(
                service,
                MovementDiagnosticScenario.KeybdEventPulsedW,
                settings);

            FakeKeyboardBackend backend = Assert.Single(_backends);
            Assert.True(session.PulseTransitions >= 4, $"Expected several pulse transitions, got {session.PulseTransitions}.");
            Assert.Contains("W:down", backend.Calls);
            Assert.Contains("W:up", backend.Calls);
            Assert.Equal("W:up", backend.Calls[^1]);
            Assert.True(session.InputCleanupComplete);
        }

        [Fact]
        public async Task KeyboardSteeringRunsIndependentWAThenWDStagesAndReleasesWAD()
        {
            var service = CreateService();
            MovementDiagnosticSettings settings = FastSettings(MovementDiagnosticScenario.KeybdEventKeyboardSteering);
            settings.DurationMs = 400;
            MovementDiagnosticSession session = await StartAndWaitAsync(
                service,
                MovementDiagnosticScenario.KeybdEventKeyboardSteering,
                settings);

            FakeKeyboardBackend backend = Assert.Single(_backends);
            int wDown = backend.Calls.IndexOf("W:down");
            int aDown = backend.Calls.IndexOf("A:down");
            int aUp = backend.Calls.IndexOf("A:up");
            int wUpFirst = backend.Calls.IndexOf("W:up");
            int dDown = backend.Calls.IndexOf("D:down");
            int dUp = backend.Calls.IndexOf("D:up");
            int wUpLast = backend.Calls.LastIndexOf("W:up");

            Assert.True(wDown >= 0 && aDown > wDown);
            Assert.True(aUp > aDown && wUpFirst > aUp);
            Assert.True(dDown > wUpFirst && dUp > dDown && wUpLast > dUp);
            Assert.DoesNotContain(backend.Calls, c => c.Contains("B:"));
            Assert.Equal(2, session.StageCount);
            Assert.True(session.InputCleanupComplete);
        }

        [Fact]
        public async Task MouseAssistedTurningAcquiresAndReleasesBothSidesAndReleasesKeys()
        {
            var service = CreateService();
            MovementDiagnosticSettings settings = FastSettings(MovementDiagnosticScenario.KeyboardAndMouseFastTurning);
            settings.DurationMs = 400;
            MovementDiagnosticSession session = await StartAndWaitAsync(
                service,
                MovementDiagnosticScenario.KeyboardAndMouseFastTurning,
                settings);

            FakeKeyboardBackend backend = Assert.Single(_backends);
            Assert.Contains("W:down", backend.Calls);
            Assert.Contains("A:down", backend.Calls);
            Assert.Contains("D:down", backend.Calls);
            Assert.Equal("D:up", backend.Calls[^2]);
            Assert.Equal("W:up", backend.Calls[^1]);
            Assert.Contains(_mouseCalls, c => c == "acquire-left");
            Assert.Contains(_mouseCalls, c => c == "release-left");
            Assert.Contains(_mouseCalls, c => c == "acquire-right");
            Assert.Contains(_mouseCalls, c => c == "release-right");
            Assert.True(session.InputCleanupComplete);
        }

        [Fact]
        public async Task DirectCameraIsRejectedUnlessAdvancedModeIsExplicitlyEnabled()
        {
            var service = CreateService();
            MovementDiagnosticStartResult result = await service.StartTestAsync(
                MovementDiagnosticScenario.DirectCameraMemorySteering,
                FastSettings(MovementDiagnosticScenario.DirectCameraMemorySteering));

            Assert.False(result.Accepted);
            Assert.Contains("advanced", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(_backends);
        }

        [Fact]
        public async Task DirectCameraPerformsOneBoundedWriteReadbackAndShortW()
        {
            var service = CreateService();
            MovementDiagnosticSettings settings = FastSettings(
                MovementDiagnosticScenario.DirectCameraMemorySteering,
                advanced: true);
            settings.DurationMs = 300;
            MovementDiagnosticSession session = await StartAndWaitAsync(
                service,
                MovementDiagnosticScenario.DirectCameraMemorySteering,
                settings);

            FakeKeyboardBackend backend = Assert.Single(_backends);
            Assert.Equal(new[] { "W:down", "W:up" }, backend.Calls);
            Assert.Equal(1, session.CameraWriteCount);
            Assert.Contains(session.Events, e => e.EventType == "CAMERA_WRITE_ISSUED");
            Assert.Contains(session.Events, e =>
                e.EventType == "CAMERA_WRITE_READBACK_VERIFIED" ||
                e.EventType == "CAMERA_WRITE_READBACK_MISMATCH");
        }

        [Fact]
        public async Task ExistingMovementSystemUsesOnlyMoveKeyboardTurnAndConfiguredWaypoint()
        {
            MovementDiagnosticOnlyMoveContext? captured = null;
            var service = CreateService(
                readPlayerPosition: () => (100f, 100f, true),
                onlyMoveRunner: (context, token) =>
                {
                    captured = context;
                    return Task.FromResult(new MovementDiagnosticMovementRunResult
                    {
                        GoalReached = false,
                        SteeringMode = MovementSteeringMode.KeyboardTurn.ToString(),
                        DirectCameraRecoveryInvoked = false,
                        UpdateTicks = 3,
                        Detail = "fake only-move runner"
                    });
                });

            MovementDiagnosticSession session = await StartAndWaitAsync(
                service,
                MovementDiagnosticScenario.ExistingMovementSystemOnlyMove,
                FastSettings(MovementDiagnosticScenario.ExistingMovementSystemOnlyMove));

            Assert.NotNull(captured);
            Assert.Equal(BotMode.OnlyMove, captured!.Mode);
            Assert.Equal(MovementSteeringMode.KeyboardTurn, captured.SteeringMode);
            Assert.Equal(105f, captured.TargetX);
            Assert.Equal(100f, captured.TargetY);
            Assert.Empty(_backends); // the production controller owns its own input path
            Assert.Equal(MovementSteeringMode.KeyboardTurn.ToString(), session.SteeringModeUsed);
            Assert.False(session.DirectCameraRecoveryInvoked);
        }

        [Fact]
        public async Task ExistingMovementSystemRefusesWithoutAnExplicitSafeWaypoint()
        {
            var service = CreateService(readPlayerPosition: () => (100f, 100f, true));
            MovementDiagnosticSettings settings = FastSettings(MovementDiagnosticScenario.ExistingMovementSystemOnlyMove);
            settings.SafeWaypointX = null;
            settings.SafeWaypointY = null;

            MovementDiagnosticStartResult result = await service.StartTestAsync(
                MovementDiagnosticScenario.ExistingMovementSystemOnlyMove,
                settings);

            Assert.False(result.Accepted);
            Assert.Contains("waypoint", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task CancellationDuringHoldReleasesEveryOwnedKey()
        {
            var service = CreateService();
            MovementDiagnosticSettings settings = FastSettings();
            settings.DurationMs = 5000;

            MovementDiagnosticStartResult start = await service.StartTestAsync(
                MovementDiagnosticScenario.KeybdEventContinuousW,
                settings);
            Assert.True(start.Accepted, start.Message);
            await Task.Delay(120);

            MovementDiagnosticStartResult stop = await service.StopTestAsync("unit-test cancellation");
            Assert.True(stop.Accepted, stop.Message);

            MovementDiagnosticSession session = service.GetCurrentSessionSnapshotForTesting()!;
            FakeKeyboardBackend backend = Assert.Single(_backends);
            Assert.Contains("W:down", backend.Calls);
            Assert.Equal("W:up", backend.Calls[^1]);
            Assert.True(session.InputCleanupComplete);
            Assert.Equal(MovementDiagnosticOutcome.Stopped, session.Outcome);
            Assert.False(service.GetStatus().HasUnreleasedKeys);
        }

        [Fact]
        public async Task ScenarioExceptionStillPerformsKeyCleanup()
        {
            var throwingMouse = new FakeMouseAssist(new List<string>(), throwOnAcquire: true);
            var service = CreateService(mouseAssistFactory: (pid, log) => throwingMouse);
            MovementDiagnosticSettings settings = FastSettings(
                MovementDiagnosticScenario.KeyboardAndMouseFastTurning);
            settings.DurationMs = 500;

            MovementDiagnosticSession session = await StartAndWaitAsync(
                service,
                MovementDiagnosticScenario.KeyboardAndMouseFastTurning,
                settings);

            FakeKeyboardBackend backend = Assert.Single(_backends);
            Assert.Contains("W:down", backend.Calls);
            Assert.Contains("A:down", backend.Calls);
            Assert.Contains("W:up", backend.Calls);
            Assert.Contains("A:up", backend.Calls);
            Assert.True(session.InputCleanupComplete);
            Assert.Equal(MovementDiagnosticOutcome.Failed, session.Outcome);
        }

        [Fact]
        public async Task SecondDiagnosticCannotStartWhileTheFirstIsActive()
        {
            var service = CreateService();
            MovementDiagnosticSettings settings = FastSettings();
            settings.DurationMs = 2000;

            MovementDiagnosticStartResult first = await service.StartTestAsync(
                MovementDiagnosticScenario.KeybdEventContinuousW,
                settings);
            Assert.True(first.Accepted, first.Message);

            MovementDiagnosticStartResult second = await service.StartTestAsync(
                MovementDiagnosticScenario.SendInputVirtualKey,
                FastSettings());
            Assert.False(second.Accepted);
            Assert.Contains("already running", second.Message, StringComparison.OrdinalIgnoreCase);

            await service.StopTestAsync("unit-test cleanup");
        }

        [Fact]
        public async Task ConflictingProductionActivityBlocksDiagnosticStart()
        {
            var service = CreateService(conflict: "Movement bot is running.");
            MovementDiagnosticStartResult result = await service.StartTestAsync(
                MovementDiagnosticScenario.KeybdEventContinuousW,
                FastSettings());

            Assert.False(result.Accepted);
            Assert.Contains("Movement bot", result.Message);
            Assert.Empty(_backends);
        }

        [Fact]
        public async Task PostTestObservationSendsNoInputAndPerformsNoCameraWrites()
        {
            var service = CreateService();
            MovementDiagnosticSession session = await StartAndWaitAsync(
                service,
                MovementDiagnosticScenario.KeybdEventContinuousW,
                FastSettings());
            int callsAfterTest = _backends.Single().Calls.Count;
            int writesAfterTest = _writeCalls;
            int samplesAfterTest = session.Samples.Count;

            MovementDiagnosticStartResult obsStart = await service.StartPostTestObservationAsync();
            Assert.True(obsStart.Accepted, obsStart.Message);
            await Task.Delay(250);
            Assert.True(service.IsObservationActive);
            MovementDiagnosticStartResult obsStop = await service.StopObservationAsync("unit-test observation");
            Assert.True(obsStop.Accepted, obsStop.Message);

            MovementDiagnosticSession after = service.GetCurrentSessionSnapshotForTesting()!;
            Assert.Equal(callsAfterTest, _backends.Single().Calls.Count);
            Assert.Equal(writesAfterTest, _writeCalls);
            Assert.True(after.Samples.Count >= samplesAfterTest);

            long stopSequence = after.Events.First(e => e.EventType == "INPUT_INJECTION_STOPPED").Sequence;
            Assert.DoesNotContain(
                after.Events.Where(e => e.Sequence > stopSequence),
                e => e.EventType.StartsWith("KEY_", StringComparison.Ordinal) ||
                     e.EventType.StartsWith("CAMERA_WRITE", StringComparison.Ordinal));
        }

        [Fact]
        public async Task RecoveryDurationUsesActualInputStopAndManualMarkTimestamps()
        {
            var service = CreateService();
            MovementDiagnosticSession session = await StartAndWaitAsync(
                service,
                MovementDiagnosticScenario.KeybdEventContinuousW,
                FastSettings());
            Assert.NotNull(session.InputInjectionStoppedUtc);

            await Task.Delay(80);
            MovementDiagnosticMarkResult mark = service.MarkRecoveryObserved();
            Assert.True(mark.Accepted, mark.Message);
            Assert.True(mark.RecoveryDurationMs >= 50,
                $"Expected >=50ms recovery, got {mark.RecoveryDurationMs:F0}ms.");

            MovementDiagnosticSession after = service.GetCurrentSessionSnapshotForTesting()!;
            Assert.True(after.RecoveryObservedUtc.HasValue);
            Assert.True(after.RecoveryDurationMs.HasValue);
            double expected = (after.RecoveryObservedUtc!.Value - after.InputInjectionStoppedUtc!.Value).TotalMilliseconds;
            Assert.InRange(after.RecoveryDurationMs!.Value, expected - 1, expected + 1);
            Assert.Contains(after.Events, e => e.EventType == "RECOVERY_OBSERVED_MANUAL");
        }

        [Fact]
        public async Task RecoveryCheckpointsAreAutomaticAndDistinctFromManualRecoveryMark()
        {
            var service = CreateService();
            MovementDiagnosticSettings settings = FastSettings(
                autoObservation: true,
                checkpoints: new[] { 1 });
            MovementDiagnosticSession session = await StartAndWaitAsync(
                service,
                MovementDiagnosticScenario.KeybdEventContinuousW,
                settings);

            await Task.Delay(1300);
            MovementDiagnosticStartResult obsStop = await service.StopObservationAsync("unit-test checkpoint");
            Assert.True(obsStop.Accepted, obsStop.Message);

            MovementDiagnosticSession after = service.GetCurrentSessionSnapshotForTesting()!;
            MovementDiagnosticEventEntry checkpoint = Assert.Single(
                after.Events.Where(e => e.EventType == "RECOVERY_CHECKPOINT" && e.Details?.Contains("checkpoint=1s") == true));
            Assert.Contains("automatic-checkpoint", checkpoint.Details);
            Assert.Null(after.RecoveryObservedUtc);
        }

        [Fact]
        public async Task FailedInputApiCallIsRecordedRatherThanIgnored()
        {
            _failBackendDown = true;
            var service = CreateService();
            MovementDiagnosticStartResult start = await service.StartTestAsync(
                MovementDiagnosticScenario.KeybdEventContinuousW,
                FastSettings());
            Assert.True(start.Accepted, start.Message);
            await WaitForTestAsync(service);

            MovementDiagnosticSession session = service.GetCurrentSessionSnapshotForTesting()!;
            Assert.Contains(session.Events, e => e.EventType == "INPUT_FAILURE");
            Assert.True(session.InputFailureCount > 0);
            Assert.All(
                session.Events.Where(e => e.EventType == "INPUT_FAILURE"),
                e => Assert.True(e.ApiSucceeded == false));
        }

        [Fact]
        public async Task GlobalStopAllCancelsTestAndReleasesDiagnosticInput()
        {
            var service = CreateService();
            MovementDiagnosticSettings settings = FastSettings();
            settings.DurationMs = 5000;
            MovementDiagnosticStartResult start = await service.StartTestAsync(
                MovementDiagnosticScenario.KeybdEventContinuousW,
                settings);
            Assert.True(start.Accepted, start.Message);
            await Task.Delay(100);

            service.StopForGlobalStop("unit-test Stop All");
            Assert.False(service.IsTestActive);
            Assert.False(service.IsObservationActive);
            Assert.False(service.GetStatus().HasUnreleasedKeys);

            FakeKeyboardBackend backend = Assert.Single(_backends);
            Assert.Equal("W:up", backend.Calls[^1]);
            MovementDiagnosticSession session = service.GetCurrentSessionSnapshotForTesting()!;
            Assert.True(session.InputCleanupComplete);
        }

        [Fact]
        public async Task CancelledSessionStillProducesReportArtefacts()
        {
            var service = CreateService();
            MovementDiagnosticSettings settings = FastSettings();
            settings.DurationMs = 5000;
            MovementDiagnosticStartResult start = await service.StartTestAsync(
                MovementDiagnosticScenario.KeybdEventContinuousW,
                settings);
            Assert.True(start.Accepted, start.Message);
            await Task.Delay(80);
            await service.StopTestAsync("cancel and keep report");

            string? directory = await service.ExportReportAsync();
            Assert.NotNull(directory);
            Assert.True(File.Exists(Path.Combine(directory!, "events.csv")));
            Assert.True(File.Exists(Path.Combine(directory!, "samples.csv")));
            Assert.True(File.Exists(Path.Combine(directory!, "session.json")));
            Assert.True(File.Exists(Path.Combine(directory!, "report.txt")));
        }

        [Fact]
        public async Task ReportIncludesObserverRowAndComparisonTable()
        {
            var service = CreateService();
            await StartAndWaitAsync(
                service,
                MovementDiagnosticScenario.KeybdEventContinuousW,
                FastSettings());

            MovementDiagnosticStartResult saved = service.RecordObserverFeedback(
                MovementRemoteObservationRating.AnimationStutteringOnly,
                persistedAfterStop: true,
                observedDuringManualMovement: true,
                notes: "second client showed stutter but no position jump");
            Assert.True(saved.Accepted, saved.Message);
            service.MarkRecoveryObserved();

            string? directory = await service.ExportReportAsync();
            Assert.NotNull(directory);
            string report = File.ReadAllText(Path.Combine(directory!, "report.txt"));
            string sessionJson = File.ReadAllText(Path.Combine(directory!, "session.json"));

            Assert.Contains("SCENARIO COMPARISON", report);
            Assert.Contains("Stuttering animation only", report);
            Assert.Contains("Yes (reported)", report);
            Assert.Contains("second client showed stutter but no position jump", sessionJson);
        }

        [Fact]
        public void MovementSystemSpeedPotionRebuffRemainsEnabledByDefault()
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
            var memory = new GameMemoryService(TestPid, read, write, 0, 8, _ => { });
            var movement = new MovementSystem(memory, _ => { }, targetX: 1, targetY: 1);

            Assert.True(movement.AllowSpeedPotionBuff);
        }

        [Fact]
        public async Task RerunningAScenarioCreatesAnIndependentIdentifiableSession()
        {
            var service = CreateService();
            MovementDiagnosticSession first = await StartAndWaitAsync(
                service,
                MovementDiagnosticScenario.KeybdEventContinuousW,
                FastSettings());
            MovementDiagnosticSession second = await StartAndWaitAsync(
                service,
                MovementDiagnosticScenario.KeybdEventContinuousW,
                FastSettings());

            Assert.NotEqual(first.SessionId, second.SessionId);
            Assert.NotEqual(first.OutputDirectory, second.OutputDirectory);
            Assert.True(File.Exists(Path.Combine(first.OutputDirectory!, "events.csv")));
            Assert.True(File.Exists(Path.Combine(second.OutputDirectory!, "events.csv")));
        }

        [Fact]
        public async Task ObservationWithoutAnExperimentIsRejected()
        {
            var service = CreateService();
            MovementDiagnosticStartResult result = await service.StartPostTestObservationAsync();
            Assert.False(result.Accepted);
            Assert.Contains("Run an experiment first", result.Message);
        }

        [Fact]
        public void ProductionInputOwnershipIsNotBlockedWhenNoDiagnosticIsActive()
        {
            var service = CreateService();
            Assert.False(service.BlocksOtherRuntimes);
            Assert.False(service.IsTestActive);
            Assert.False(service.IsObservationActive);
            Assert.True(service.LastCleanupComplete);
        }

        private sealed class FakeKeyboardBackend : IMovementDiagnosticKeyboardBackend
        {
            public FakeKeyboardBackend(MovementDiagnosticInputMode mode)
            {
                Mode = mode;
            }

            public MovementDiagnosticInputMode Mode { get; }
            public string Name => Mode.ToString();
            public List<string> Calls { get; } = new();
            public bool FailDown { get; set; }

            public MovementDiagnosticInputCallResult KeyDown(byte virtualKey, byte scanCode)
            {
                Calls.Add($"{KeyName(virtualKey)}:down");
                return FailDown
                    ? new MovementDiagnosticInputCallResult(false, 0, 5, "fake SendInput failure")
                    : new MovementDiagnosticInputCallResult(true, 1, 0, "fake success");
            }

            public MovementDiagnosticInputCallResult KeyUp(byte virtualKey, byte scanCode)
            {
                Calls.Add($"{KeyName(virtualKey)}:up");
                return new MovementDiagnosticInputCallResult(true, 1, 0, "fake success");
            }

            public void Dispose()
            {
            }

            private static string KeyName(byte vk) => vk switch
            {
                GameInput.VK_W => "W",
                GameInput.VK_A => "A",
                GameInput.VK_D => "D",
                _ => $"0x{vk:X2}"
            };
        }

        private sealed class FakeMouseAssist : IMovementDiagnosticMouseTurnAssist
        {
            private readonly List<string> _calls;
            private readonly bool _throwOnAcquire;
            private bool _leftSide;
            public bool IsActive { get; private set; }

            public FakeMouseAssist(List<string> calls, bool throwOnAcquire = false)
            {
                _calls = calls;
                _throwOnAcquire = throwOnAcquire;
            }

            public bool IsMovingCursor => IsActive;

            public MovementDiagnosticMouseAssistResult Acquire(bool leftSide, string stageLabel)
            {
                _calls.Add(leftSide ? "acquire-left" : "acquire-right");
                if (_throwOnAcquire)
                    throw new InvalidOperationException("fake mouse-assist failure");
                IsActive = true;
                _leftSide = leftSide;
                return new MovementDiagnosticMouseAssistResult(true, true, true, 10, 10, 1, 10, "fake acquired");
            }

            public MovementDiagnosticMouseAssistResult Release(string reason)
            {
                if (IsActive)
                    _calls.Add(_leftSide ? "release-left" : "release-right");
                IsActive = false;
                return new MovementDiagnosticMouseAssistResult(true, true, true, 1, 10, 500, 10, "fake released");
            }

            public MovementDiagnosticMouseAssistResult Cleanup(string reason) => Release(reason);

            public void Dispose()
            {
                IsActive = false;
            }
        }
    }
}
