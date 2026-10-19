using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Regression coverage for the confirmed city stuck loop:
    /// STUCK 1/3 -> reverse diagonal physically succeeds -> counter was reset -> repeat.
    /// Physical recovery success must never reset the navigation failure count for the
    /// same unresolved waypoint.
    /// </summary>
    public sealed class CityStuckRecoveryRegressionTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly MethodInfo HandleStuckMethod = typeof(MovementSystem).GetMethod(
            "HandleConfirmedNavigationStuckAsync",
            PrivateInstance) ?? throw new InvalidOperationException("HandleConfirmedNavigationStuckAsync not found.");

        private static readonly MethodInfo AdvanceMethod = typeof(MovementSystem).GetMethod(
            "AdvanceReachedWaypoints",
            PrivateInstance) ?? throw new InvalidOperationException("AdvanceReachedWaypoints not found.");

        private static readonly MethodInfo RebuildQueueMethod = typeof(MovementSystem).GetMethod(
            "RebuildWaypointQueueFromIndex",
            PrivateInstance) ?? throw new InvalidOperationException("RebuildWaypointQueueFromIndex not found.");

        private static readonly FieldInfo StuckCountField = typeof(MovementSystem).GetField(
            "_consecutiveStuckAttempts",
            PrivateInstance) ?? throw new InvalidOperationException("_consecutiveStuckAttempts not found.");

        private static readonly FieldInfo RecoveryField = typeof(MovementSystem).GetField(
            "_reverseDiagonalRecovery",
            PrivateInstance) ?? throw new InvalidOperationException("_reverseDiagonalRecovery not found.");

        private static readonly FieldInfo CityFatalField = typeof(MovementSystem).GetField(
            "_isCityStuckFatal",
            PrivateInstance) ?? throw new InvalidOperationException("_isCityStuckFatal not found.");

        private static readonly FieldInfo MovingForwardField = typeof(MovementSystem).GetField(
            "_isMovingForward",
            PrivateInstance) ?? throw new InvalidOperationException("_isMovingForward not found.");

        private static readonly FieldInfo IgnoreStuckUntilField = typeof(MovementSystem).GetField(
            "_ignoreStuckUntil",
            PrivateInstance) ?? throw new InvalidOperationException("_ignoreStuckUntil not found.");

        private static readonly FieldInfo InitialHeadingAlignmentField = typeof(MovementSystem).GetField(
            "_initialHeadingAlignmentPending",
            PrivateInstance) ?? throw new InvalidOperationException("_initialHeadingAlignmentPending not found.");

        private static readonly FieldInfo LastMoveProgressTimeField = typeof(MovementSystem).GetField(
            "_lastMoveProgressTime",
            PrivateInstance) ?? throw new InvalidOperationException("_lastMoveProgressTime not found.");

        private static readonly FieldInfo RecoveryStageStartField = typeof(ReverseDiagonalRecovery).GetField(
            "_stageStartTime",
            PrivateInstance) ?? throw new InvalidOperationException("_stageStartTime not found.");

        private static readonly FieldInfo CityAttemptLimitField = typeof(MovementSystem).GetField(
            "STUCK_MAX_ATTEMPTS_IN_CITY",
            BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException("STUCK_MAX_ATTEMPTS_IN_CITY not found.");

        private static readonly FieldInfo OutsideAttemptLimitField = typeof(MovementSystem).GetField(
            "STUCK_MAX_ATTEMPTS_OUTSIDE",
            BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException("STUCK_MAX_ATTEMPTS_OUTSIDE not found.");

        private static MovementSystem CreateMovement(
            SimulatedGameMemory simulation,
            Action<string> log,
            List<Waypoint>? path = null,
            bool inCity = true)
        {
            simulation.InCity = inCity;
            path ??= new List<Waypoint>
            {
                new Waypoint(265, 196, MovementPrecision.Accurate, BotMode.OnlyMove,
                    zoneRestriction: ZoneRestriction.Both)
            };

            return new TestMovementSystem(
                simulation.CreateMemory(log),
                log,
                path[^1].X,
                path[^1].Y,
                path);
        }

        private static int GetStuckCount(MovementSystem movement)
            => (int)(StuckCountField.GetValue(movement) ?? 0);

        private static ReverseDiagonalRecovery GetRecovery(MovementSystem movement)
            => (ReverseDiagonalRecovery)(RecoveryField.GetValue(movement)
                ?? throw new InvalidOperationException("Recovery instance missing."));

        private static bool IsRecoveryActive(MovementSystem movement)
            => GetRecovery(movement).IsActive;

        private static async Task DispatchStuckAsync(MovementSystem movement, Waypoint target)
        {
            var task = (Task?)HandleStuckMethod.Invoke(
                movement,
                new object[] { 262f, 188f, target, CancellationToken.None, "RegressionTest" });
            Assert.NotNull(task);
            await task!;
        }

        /// <summary>
        /// Forces the current recovery stage to look old, then runs one real
        /// MovementSystem.Update tick. This drives the actual recovery state machine
        /// (camera settle -> W -> movement window -> success cooldown) deterministically.
        /// </summary>
        private static async Task TickRecoveryUpdateAsync(MovementSystem movement)
        {
            ReverseDiagonalRecovery recovery = GetRecovery(movement);
            RecoveryStageStartField.SetValue(recovery, DateTime.Now.AddSeconds(-5));
            await movement.Update(CancellationToken.None);
        }

        private static async Task DriveSuccessfulRecoveryAsync(
            MovementSystem movement,
            SimulatedGameMemory simulation,
            float recoveryX,
            float recoveryY,
            float displacedX,
            float displacedY)
        {
            simulation.X = recoveryX;
            simulation.Y = recoveryY;
            await TickRecoveryUpdateAsync(movement); // camera settle -> W pressed, attempt baseline

            simulation.X = displacedX;
            simulation.Y = displacedY;
            await TickRecoveryUpdateAsync(movement); // physical displacement -> success cooldown

            await TickRecoveryUpdateAsync(movement); // cooldown -> Recovered
        }

        private static async Task DriveFailedRecoveryAsync(
            MovementSystem movement,
            SimulatedGameMemory simulation,
            float x,
            float y)
        {
            simulation.X = x;
            simulation.Y = y;

            for (int attempt = 0; attempt < BotConstants.ReverseDiagonal.MaxAttempts; attempt++)
            {
                await TickRecoveryUpdateAsync(movement); // camera settle -> W pressed
                await TickRecoveryUpdateAsync(movement); // no displacement -> next/failed
            }
        }

        [Fact]
        public async Task SuccessfulPhysicalRecoveriesPreserveNavigationFailuresAndThirdCityStuckEscalates()
        {
            var lines = new List<string>();
            var simulation = new SimulatedGameMemory();
            var target = new Waypoint(265, 196, MovementPrecision.Accurate, BotMode.OnlyMove,
                zoneRestriction: ZoneRestriction.Both);
            var movement = CreateMovement(simulation, lines.Add, new List<Waypoint> { target });

            // ── Stuck event 1: physical recovery succeeds, navigation failure stays 1/3 ──
            await DispatchStuckAsync(movement, target);
            Assert.Equal(1, GetStuckCount(movement));

            await DriveSuccessfulRecoveryAsync(movement, simulation, 262, 188, 256, 188);

            Assert.Equal(1, GetStuckCount(movement));
            Assert.False(IsRecoveryActive(movement));
            Assert.Contains(lines, line => line.Contains("Physical recovery succeeded") && line.Contains("preserved: 1/3"));

            // ── Stuck event 2: another successful physical recovery, counter stays 2/3 ──
            await DispatchStuckAsync(movement, target);
            Assert.Equal(2, GetStuckCount(movement));

            await DriveSuccessfulRecoveryAsync(movement, simulation, 258, 193, 256, 188);

            Assert.Equal(2, GetStuckCount(movement));
            Assert.False(IsRecoveryActive(movement));
            Assert.Contains(lines, line => line.Contains("Physical recovery succeeded") && line.Contains("preserved: 2/3"));

            // ── Stuck event 3: city limit reached, route aborted, no fourth recovery ──
            await DispatchStuckAsync(movement, target);

            Assert.True(movement.IsCityStuckFatal);
            Assert.Contains(lines, line => line.Contains("Attempt=3/3"));
            Assert.Contains(lines, line => line.Contains("CITY STUCK LIMIT REACHED"));
            Assert.False(IsRecoveryActive(movement));

            int startedBeforeFourthDispatch = lines.Count(line => line.Contains("[ReverseDiagonal] Started."));
            await DispatchStuckAsync(movement, target);

            Assert.True(movement.IsCityStuckFatal);
            Assert.False(IsRecoveryActive(movement));
            Assert.DoesNotContain(lines, line => line.Contains("Attempt=4/3"));
            Assert.Equal(startedBeforeFourthDispatch, lines.Count(line => line.Contains("[ReverseDiagonal] Started.")));
        }

        [Fact]
        public async Task ReachingTheBlockedWaypointResetsNavigationFailuresForTheNextWaypoint()
        {
            var lines = new List<string>();
            var simulation = new SimulatedGameMemory();
            var first = new Waypoint(265, 196, MovementPrecision.Accurate, BotMode.OnlyMove,
                zoneRestriction: ZoneRestriction.Both);
            var second = new Waypoint(300, 300, MovementPrecision.Accurate, BotMode.OnlyMove,
                zoneRestriction: ZoneRestriction.Both);
            var movement = CreateMovement(simulation, lines.Add, new List<Waypoint> { first, second });

            await DispatchStuckAsync(movement, first);
            Assert.Equal(1, GetStuckCount(movement));

            // Same-target route resynchronization must not wipe the failure history.
            object? resyncResult = RebuildQueueMethod.Invoke(
                movement,
                new object[] { 0, first, false });
            Assert.NotNull(resyncResult);
            Assert.Equal("SameTarget", resyncResult!.ToString());
            Assert.Equal(1, GetStuckCount(movement));
            Assert.Contains(lines, line => line.Contains("Rebuild skipped: same target"));

            var advancedTask = (Task<bool>)AdvanceMethod.Invoke(
                movement,
                new object[] { 265f, 196f, CancellationToken.None })!;
            Assert.True(await advancedTask);

            Assert.Equal(0, GetStuckCount(movement));
            Assert.Contains(lines, line => line.Contains("Navigation failure count reset") &&
                                           line.Contains("reached"));

            // The next waypoint starts with a clean history: a new stuck is 1/3, not 2/3.
            await DispatchStuckAsync(movement, second);
            Assert.Equal(1, GetStuckCount(movement));
        }

        [Fact]
        public async Task OutsideCityKeepsFifteenAttemptThresholdAndDoesNotUseCityEscalation()
        {
            var lines = new List<string>();
            var simulation = new SimulatedGameMemory();
            var target = new Waypoint(265, 196, MovementPrecision.Accurate, BotMode.OnlyMove);
            var movement = CreateMovement(simulation, lines.Add, new List<Waypoint> { target }, inCity: false);

            // 14 consecutive unresolved navigation failures must not escalate in the field.
            for (int i = 0; i < 14; i++)
            {
                await DispatchStuckAsync(movement, target);
                Assert.Equal(i + 1, GetStuckCount(movement));
            }

            Assert.False(movement.IsCityStuckFatal);
            Assert.False(movement.IsReportAndGoBackRequested);

            // The city-specific limit (3) must not affect outside navigation. The
            // existing outside limit remains 15 (ReportAndGoBack escalation is hit only
            // on the 15th event; the unit test intentionally does not send the real town
            // teleport key press).
            Assert.Equal(3, (int)CityAttemptLimitField.GetRawConstantValue()!);
            Assert.Equal(15, (int)OutsideAttemptLimitField.GetRawConstantValue()!);
            Assert.False(movement.IsReportAndGoBackRequested);
        }

        [Fact]
        public async Task ActionStuckAndNoProgressDetectionUseTheSameFailureAccounting()
        {
            // ── Action-based detector: 2 idle samples while W is held ──
            var actionLines = new List<string>();
            var actionSimulation = new SimulatedGameMemory { Action = 25 };
            var actionTarget = new Waypoint(
                265, 196, MovementPrecision.Accurate, BotMode.OnlyMove,
                zoneRestriction: ZoneRestriction.Both);
            var actionMovement = CreateMovement(
                actionSimulation, actionLines.Add, new List<Waypoint> { actionTarget });

            MovingForwardField.SetValue(actionMovement, true);
            IgnoreStuckUntilField.SetValue(actionMovement, DateTime.Now.AddSeconds(-1));
            InitialHeadingAlignmentField.SetValue(actionMovement, false);

            await actionMovement.Update(CancellationToken.None); // first idle sample
            await actionMovement.Update(CancellationToken.None); // confirmed ActionStuck

            Assert.Equal(1, GetStuckCount(actionMovement));
            Assert.True(IsRecoveryActive(actionMovement));
            Assert.Contains(actionLines, line => line.Contains("[ActionStuck]"));

            // ── Position-based NoProgress detector: action claims running, position frozen ──
            var noProgressLines = new List<string>();
            var noProgressSimulation = new SimulatedGameMemory { Action = 27 };
            var noProgressTarget = new Waypoint(
                265, 196, MovementPrecision.Accurate, BotMode.OnlyMove,
                zoneRestriction: ZoneRestriction.Both);
            var noProgressMovement = CreateMovement(
                noProgressSimulation, noProgressLines.Add, new List<Waypoint> { noProgressTarget });

            MovingForwardField.SetValue(noProgressMovement, true);
            IgnoreStuckUntilField.SetValue(noProgressMovement, DateTime.Now.AddSeconds(-1));
            InitialHeadingAlignmentField.SetValue(noProgressMovement, false);

            await noProgressMovement.Update(CancellationToken.None); // establish progress baseline
            LastMoveProgressTimeField.SetValue(
                noProgressMovement,
                DateTime.Now.AddMilliseconds(-(BotConstants.Movement.NoProgressTimeoutMs + 500)));
            await noProgressMovement.Update(CancellationToken.None); // confirmed NoProgress

            Assert.Equal(1, GetStuckCount(noProgressMovement));
            Assert.True(IsRecoveryActive(noProgressMovement));
            Assert.Contains(noProgressLines, line => line.Contains("[NoProgress]"));
        }

        [Fact]
        public async Task FailedReverseDiagonalRecoveryCannotResetNavigationFailureHistory()
        {
            var lines = new List<string>();
            var simulation = new SimulatedGameMemory();
            var target = new Waypoint(265, 196, MovementPrecision.Accurate, BotMode.OnlyMove,
                zoneRestriction: ZoneRestriction.Both);
            var movement = CreateMovement(simulation, lines.Add, new List<Waypoint> { target });

            await DispatchStuckAsync(movement, target);
            Assert.Equal(1, GetStuckCount(movement));

            await DriveFailedRecoveryAsync(movement, simulation, 262, 188);

            Assert.False(IsRecoveryActive(movement));
            Assert.Equal(1, GetStuckCount(movement));
            Assert.Contains(lines, line => line.Contains("Physical recovery failed") && line.Contains("preserved: 1/3"));

            // A second stuck event increments; the failed recovery did not wipe history.
            await DispatchStuckAsync(movement, target);
            Assert.Equal(2, GetStuckCount(movement));
        }

        [Fact]
        public async Task CoordinatorRetriesCityStuckRepotRouteBoundedAndThenFails()
        {
            string saveDirectory = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "SavedPaths"));
            Directory.CreateDirectory(saveDirectory);

            string pathFile = $"CityStuckRegression_{Guid.NewGuid():N}.json";
            string fullPath = Path.Combine(saveDirectory, pathFile);
            File.WriteAllText(fullPath,
                "{\"Name\":\"city-stuck-regression\",\"Points\":[" +
                "{\"X\":265,\"Y\":196,\"Precision\":2,\"Mode\":0}," +
                "{\"X\":300,\"Y\":300,\"Precision\":2,\"Mode\":0}]}");

            try
            {
                var lines = new List<string>();
                var simulation = new SimulatedGameMemory { InCity = true, X = 100, Y = 100 };
                var memory = simulation.CreateMemory(lines.Add);
                var pathLoader = new SavedPathLoader(lines.Add);
                var pathRunner = new PathRunnerService(
                    memory,
                    lines.Add,
                    enableWaypointSpecialRecoveries: false,
                    steeringMode: MovementSteeringMode.DirectCamera);
                var repotSystem = new RepotSystem(memory, lines.Add);
                var repotDetector = new RepotDetectorService(lines.Add);
                var itemSeller = new ItemSellerService(memory, lines.Add);
                var profile = new BotProfile { Name = "city-stuck-regression" };
                var operationContext = new OperationContext(
                    memory,
                    pathRunner,
                    itemSeller,
                    profile,
                    lines.Add,
                    () => { });
                var operationRunner = new OperationRunnerService(operationContext, lines.Add);
                var coordinator = new BotWorkflowCoordinator(
                    memory,
                    repotSystem,
                    repotDetector,
                    pathLoader,
                    pathRunner,
                    operationRunner,
                    profile,
                    lines.Add,
                    () => { });

                var repotStep = new BotFlowStep
                {
                    Type = BotFlowStepType.Repot,
                    RepotPaths = new List<BotRouteStep>
                    {
                        new BotRouteStep { PathFile = pathFile }
                    }
                };

                Assert.False(await RunRepotStepWithInjectedCityStuckAsync(coordinator, pathRunner, repotStep));
                Assert.Equal(1, GetMoveToRepotRetryCount(coordinator));
                Assert.Equal(PathRunStopReason.CityStuck, pathRunner.LastStopReason);

                Assert.False(await RunRepotStepWithInjectedCityStuckAsync(coordinator, pathRunner, repotStep));
                Assert.Equal(2, GetMoveToRepotRetryCount(coordinator));
                Assert.Equal(PathRunStopReason.CityStuck, pathRunner.LastStopReason);

                Assert.False(await RunRepotStepWithInjectedCityStuckAsync(coordinator, pathRunner, repotStep));
                Assert.Equal(0, GetMoveToRepotRetryCount(coordinator));
                Assert.Equal(BotPhase.Failed, coordinator.CurrentPhase);
                Assert.Equal(PathRunStopReason.CityStuck, pathRunner.LastStopReason);

                Assert.Contains(lines, line => line.Contains("Could not reach the repot point after 3 tries"));
            }
            finally
            {
                try { File.Delete(fullPath); } catch { }
            }
        }

        private static readonly MethodInfo ExecuteRepotStepMethod = typeof(BotWorkflowCoordinator).GetMethod(
            "ExecuteRepotStepAsync",
            PrivateInstance) ?? throw new InvalidOperationException("ExecuteRepotStepAsync not found.");

        private static readonly FieldInfo MoveToRepotRetryField = typeof(BotWorkflowCoordinator).GetField(
            "_moveToRepotRetryCount",
            PrivateInstance) ?? throw new InvalidOperationException("_moveToRepotRetryCount not found.");

        private static int GetMoveToRepotRetryCount(BotWorkflowCoordinator coordinator)
            => (int)(MoveToRepotRetryField.GetValue(coordinator) ?? 0);

        /// <summary>
        /// Runs the real coordinator Repot step (real PathRunner + real MovementSystem) and
        /// injects the workflow city-stuck fatal flag into the newly created movement system,
        /// exactly as the third stuck escalation would. This covers the boundary:
        /// MovementSystem fatal -> PathRunner.LastStopReason.CityStuck -> coordinator retry.
        /// </summary>
        private static async Task<bool> RunRepotStepWithInjectedCityStuckAsync(
            BotWorkflowCoordinator coordinator,
            PathRunnerService pathRunner,
            BotFlowStep step)
        {
            MovementSystem? previousMovement = pathRunner.CurrentMovement;
            var task = (Task<bool>?)ExecuteRepotStepMethod.Invoke(
                coordinator,
                new object[] { step, CancellationToken.None });
            Assert.NotNull(task);

            bool started = SpinWait.SpinUntil(
                () => pathRunner.CurrentMovement != null &&
                      !ReferenceEquals(pathRunner.CurrentMovement, previousMovement),
                5000);
            Assert.True(started, "Coordinator did not start a new MovementSystem for the repot path.");

            CityFatalField.SetValue(pathRunner.CurrentMovement!, true);
            return await task!;
        }

        /// <summary>
        /// Test-only MovementSystem that suppresses the real full-screen stuck screenshot.
        /// Everything else runs through the production implementation unchanged.
        /// </summary>
        private sealed class TestMovementSystem : MovementSystem
        {
            public TestMovementSystem(
                GameMemoryService memory,
                Action<string> log,
                float targetX,
                float targetY,
                List<Waypoint> path)
                : base(
                    memory,
                    log,
                    targetX,
                    targetY,
                    customPath: path,
                    loopPath: false,
                    enableWaypointSpecialRecoveries: true)
            {
                InternalRepotEnabled = false;
            }

            protected override void CaptureStuckScreenshot()
            {
                // Regression tests must not capture the desktop.
            }
        }

        /// <summary>
        /// Minimal memory simulation for movement tests: a stable map, configurable player
        /// position/city flag, current action and camera. Unknown player/camera offsets read
        /// as zero so the full MovementSystem.Update tick can run without a live game.
        /// </summary>
        private sealed class SimulatedGameMemory
        {
            public const ulong ModuleBase = 0x100000;
            private const ulong PlayerBase = 0x200000;
            private const ulong CameraBase = 0x300000;
            private const ulong PlayerRange = 0x2000;
            private const ulong CameraRange = 0x400;

            public float X = 262f;
            public float Y = 188f;
            public int Map = 1;
            public bool InCity = true;
            public byte Action = 25;
            public float CameraAngle;
            public short CameraDistance = BotConstants.Camera.DefaultDistanceLock;

            public GameMemoryService CreateMemory(Action<string> log)
                => new GameMemoryService(0, Read, Write, ModuleBase, 8, log);

            private bool Read(uint pid, ulong address, byte[] buffer, out uint bytesRead)
            {
                bytesRead = 0;
                if (buffer.Length == 0)
                    return false;

                if (address == ModuleBase + BotConstants.MemoryOffsets.PlayerPtr)
                    return Copy(buffer, BitConverter.GetBytes(PlayerBase), out bytesRead);

                if (address == ModuleBase + BotConstants.MemoryOffsets.CameraPtr)
                    return Copy(buffer, BitConverter.GetBytes(CameraBase), out bytesRead);

                if (address >= PlayerBase && address < PlayerBase + PlayerRange)
                {
                    ulong offset = address - PlayerBase;
                    byte[] value = offset switch
                    {
                        BotConstants.MemoryOffsets.X => BitConverter.GetBytes((short)MathF.Round(X)),
                        BotConstants.MemoryOffsets.Y => BitConverter.GetBytes((short)MathF.Round(Y)),
                        BotConstants.MemoryOffsets.IsInCity => BitConverter.GetBytes(InCity ? 1 : 0),
                        BotConstants.MemoryOffsets.CurrentMap => BitConverter.GetBytes(Map),
                        BotConstants.MemoryOffsets.CurrentAction => new[] { Action },
                        BotConstants.MemoryOffsets.TargetSelected => BitConverter.GetBytes(0),
                        BotConstants.MemoryOffsets.Animation1 => BitConverter.GetBytes(0),
                        BotConstants.MemoryOffsets.Animation2 => BitConverter.GetBytes(0),
                        BotConstants.MemoryOffsets.Hp => BitConverter.GetBytes((short)1000),
                        BotConstants.MemoryOffsets.Mana => BitConverter.GetBytes((short)1000),
                        BotConstants.MemoryOffsets.MaxHp => BitConverter.GetBytes((short)1000),
                        BotConstants.MemoryOffsets.MaxMp => BitConverter.GetBytes((short)1000),
                        BotConstants.MemoryOffsets.AttackSpeed1 => BitConverter.GetBytes((short)0),
                        BotConstants.MemoryOffsets.AttackSpeed2 => BitConverter.GetBytes((short)0),
                        _ => Array.Empty<byte>()
                    };
                    return Copy(buffer, value, out bytesRead);
                }

                if (address >= CameraBase && address < CameraBase + CameraRange)
                {
                    ulong offset = address - CameraBase;
                    byte[] value = offset switch
                    {
                        BotConstants.MemoryOffsets.CameraAngle => BitConverter.GetBytes(CameraAngle),
                        BotConstants.MemoryOffsets.CameraDistance => BitConverter.GetBytes(CameraDistance),
                        _ => Array.Empty<byte>()
                    };
                    return Copy(buffer, value, out bytesRead);
                }

                return false;
            }

            private bool Write(uint pid, ulong address, byte[] buffer, out uint bytesWritten)
            {
                bytesWritten = (uint)buffer.Length;
                if (address >= CameraBase && address < CameraBase + CameraRange)
                {
                    ulong offset = address - CameraBase;
                    if (offset == BotConstants.MemoryOffsets.CameraAngle && buffer.Length == 4)
                        CameraAngle = BitConverter.ToSingle(buffer, 0);
                    else if (offset == BotConstants.MemoryOffsets.CameraDistance && buffer.Length == 2)
                        CameraDistance = BitConverter.ToInt16(buffer, 0);
                    return true;
                }

                return true;
            }

            private static bool Copy(byte[] buffer, byte[] value, out uint bytesRead)
            {
                Array.Clear(buffer, 0, buffer.Length);
                int count = Math.Min(buffer.Length, value.Length);
                if (count > 0)
                    Array.Copy(value, buffer, count);
                bytesRead = (uint)buffer.Length;
                return true;
            }
        }
    }
}
