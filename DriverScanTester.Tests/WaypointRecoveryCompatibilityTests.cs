using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class WaypointRecoveryCompatibilityTests
    {
        private static GameMemoryService CreateStubMemory(Action<string> log)
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
            return new GameMemoryService(0, read, write, 0, 8, log);
        }

        private static MovementSystem CreateMovement(
            Action<string> log,
            bool enableSpecialRecoveries = true,
            WaypointRecoveryExecutor? executor = null)
        {
            var memory = CreateStubMemory(log);
            var path = new List<Waypoint>
            {
                new Waypoint(5000, 5000, MovementPrecision.Medium, BotMode.OnlyMove)
            };
            return new MovementSystem(
                memory,
                log,
                5000,
                5000,
                customPath: path,
                loopPath: true,
                enableWaypointSpecialRecoveries: enableSpecialRecoveries,
                waypointRecoveryExecutor: executor)
            {
                InternalRepotEnabled = false
            };
        }

        private static async Task DispatchAsync(
            MovementSystem movement,
            Waypoint target,
            CancellationToken token = default)
        {
            var method = typeof(MovementSystem).GetMethod(
                "HandleConfirmedNavigationStuckAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var task = (Task?)method!.Invoke(
                movement,
                new object[] { 100f, 100f, target, token, "Test" });
            Assert.NotNull(task);
            await task!;
        }

        [Fact]
        public void LegacyPathPointJsonUsesDefaultRecoveryValues()
        {
            var segment = JsonSerializer.Deserialize<PathSegment>(
                "{\"Name\":\"legacy\",\"Points\":[{\"X\":1,\"Y\":2}]}" );

            Assert.NotNull(segment);
            var point = Assert.Single(segment!.Points);
            Assert.Equal(WaypointStuckRecoveryType.Default, point.StuckRecoveryType);
            Assert.Equal("", point.StuckRecoveryOperation);
            Assert.Equal("", point.StuckRecoveryPath);
            Assert.Equal(PathPoint.DefaultCameraDistanceLock, point.StuckRecoveryMobCameraDistance);
        }

        [Fact]
        public void LegacyWaypointConstructorDefaultsToStandardRecovery()
        {
            var waypoint = new Waypoint(1, 2, MovementPrecision.Medium, BotMode.OnlyMove);

            Assert.Equal(WaypointStuckRecoveryType.Default, waypoint.StuckRecoveryType);
            Assert.Equal("", waypoint.StuckRecoveryOperation);
            Assert.Equal("", waypoint.StuckRecoveryPath);
            Assert.Equal(Waypoint.DefaultCameraDistanceLock, waypoint.StuckRecoveryMobCameraDistance);
        }

        [Fact]
        public void ExplicitRecoveryConfigurationSurvivesPointToWaypointValues()
        {
            var point = new PathPoint(
                1,
                2,
                MovementPrecision.Accurate,
                BotMode.MoveAndAttack,
                16900,
                70,
                ZoneRestriction.Both,
                WaypointStuckRecoveryType.Operation,
                "Enter_COT",
                "Escape.json",
                16880);
            var waypoint = new Waypoint(
                point.X,
                point.Y,
                point.Precision,
                point.Mode,
                point.CameraDistanceLock,
                point.AttackDisengageDistance,
                point.ZoneRestriction,
                point.StuckRecoveryType,
                point.StuckRecoveryOperation,
                point.StuckRecoveryPath,
                point.StuckRecoveryMobCameraDistance);

            Assert.Equal(WaypointStuckRecoveryType.Operation, waypoint.StuckRecoveryType);
            Assert.Equal("Enter_COT", waypoint.StuckRecoveryOperation);
            Assert.Equal("Escape.json", waypoint.StuckRecoveryPath);
            Assert.Equal((short)16880, waypoint.StuckRecoveryMobCameraDistance);
        }

        [Fact]
        public async Task DefaultDispatchStartsExistingReverseDiagonalRecovery()
        {
            var lines = new List<string>();
            var movement = CreateMovement(lines.Add);
            var target = new Waypoint(5000, 5000, MovementPrecision.Medium, BotMode.OnlyMove);

            await DispatchAsync(movement, target);

            Assert.Contains(lines, line => line.Contains("[ReverseDiagonal] Started."));
            Assert.DoesNotContain(lines, line => line.Contains("configured=Default"));
        }

        [Fact]
        public async Task DisabledSpecialRecoveryUsesExistingDefaultPath()
        {
            var lines = new List<string>();
            var movement = CreateMovement(lines.Add, enableSpecialRecoveries: false);
            var target = new Waypoint(
                5000,
                5000,
                MovementPrecision.Medium,
                BotMode.OnlyMove,
                stuckRecoveryType: WaypointStuckRecoveryType.Operation,
                stuckRecoveryOperation: "Enter_COT");

            await DispatchAsync(movement, target);

            Assert.Contains(lines, line => line.Contains("[ReverseDiagonal] Started."));
            Assert.DoesNotContain(lines, line => line.Contains("[WaypointRecovery] Stuck"));
        }

        [Fact]
        public async Task SpecialRecoveryIsNotRepeatedWithoutRealProgress()
        {
            var lines = new List<string>();
            var movement = CreateMovement(lines.Add);
            var target = new Waypoint(
                5000,
                5000,
                MovementPrecision.Medium,
                BotMode.OnlyMove,
                stuckRecoveryType: WaypointStuckRecoveryType.Operation,
                stuckRecoveryOperation: "Enter_COT");

            await DispatchAsync(movement, target);
            await DispatchAsync(movement, target);

            Assert.Contains(lines, line => line.Contains("Special recovery failed"));
            Assert.Contains(lines, line => line.Contains("already attempted at this stuck location"));
        }

        [Fact]
        public async Task CancelledSpecialRecoveryCleansActiveStateAndInputOwnership()
        {
            var lines = new List<string>();
            var memory = CreateStubMemory(lines.Add);
            var loader = new SavedPathLoader(lines.Add);
            var executor = new WaypointRecoveryExecutor(
                memory,
                loader,
                new ItemSellerService(memory, lines.Add),
                new BotProfile(),
                lines.Add,
                () => { });
            var movement = CreateMovement(lines.Add, executor: executor);
            var target = new Waypoint(
                5000,
                5000,
                MovementPrecision.Medium,
                BotMode.OnlyMove,
                stuckRecoveryType: WaypointStuckRecoveryType.Operation,
                stuckRecoveryOperation: "Wait");

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => DispatchAsync(movement, target, cts.Token));

            Assert.False(movement.IsWaypointSpecialRecoveryActive);
            Assert.False(movement.IsAttackKeyHeld);
        }

        [Fact]
        public async Task AttackMobDispatchActivatesTickDrivenRecoveryWithoutW()
        {
            var lines = new List<string>();
            var movement = CreateMovement(lines.Add);
            var target = new Waypoint(
                5000,
                5000,
                MovementPrecision.Medium,
                BotMode.OnlyMove,
                stuckRecoveryType: WaypointStuckRecoveryType.AttackMob,
                stuckRecoveryMobCameraDistance: 16880);

            await DispatchAsync(movement, target);

            Assert.True(movement.IsWaypointSpecialRecoveryActive);
            Assert.Contains(lines, line => line.Contains("[WaypointRecovery] AttackMob started"));
            Assert.DoesNotContain(lines, line => line.Contains("[ReverseDiagonal] Started."));

            movement.CancelWaypointSpecialRecovery();

            Assert.False(movement.IsWaypointSpecialRecoveryActive);
            Assert.False(movement.IsAttackKeyHeld);
        }

        [Fact]
        public async Task AttackMobOwnedSkillThreeIsReleasedByExistingCleanup()
        {
            var lines = new List<string>();
            var movement = CreateMovement(lines.Add);
            var target = new Waypoint(
                5000,
                5000,
                MovementPrecision.Medium,
                BotMode.OnlyMove,
                stuckRecoveryType: WaypointStuckRecoveryType.AttackMob,
                stuckRecoveryMobCameraDistance: 16880);

            await DispatchAsync(movement, target);
            Assert.True(movement.IsWaypointSpecialRecoveryActive);

            // Simulate the exact ownership state right after the recovery logs
            // "[Key] 3 hold (attack skill)" — same flag, no parallel tracking.
            var field = typeof(MovementSystem).GetField(
                "_isSkillThreeHeld", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(movement, true);
            Assert.True(movement.IsAttackKeyHeld);

            movement.CancelWaypointSpecialRecovery();

            Assert.False(movement.IsWaypointSpecialRecoveryActive);
            Assert.False(movement.IsAttackKeyHeld);
            Assert.Contains(lines, line => line.Contains("[Key] 3 up"));
        }

        [Fact]
        public async Task WaypointAdvanceResetsGuardSoSpecialBecomesEligibleAgain()
        {
            var lines = new List<string>();
            var memory = CreateStubMemory(lines.Add);
            var path = new List<Waypoint>
            {
                new Waypoint(5000, 5000, MovementPrecision.Medium, BotMode.OnlyMove,
                    stuckRecoveryType: WaypointStuckRecoveryType.Operation,
                    stuckRecoveryOperation: "Enter_COT"),
                new Waypoint(5100, 5100, MovementPrecision.Medium, BotMode.OnlyMove,
                    stuckRecoveryType: WaypointStuckRecoveryType.Operation,
                    stuckRecoveryOperation: "Enter_COT")
            };
            var movement = new MovementSystem(
                memory,
                lines.Add,
                5100,
                5100,
                customPath: path,
                loopPath: true,
                enableWaypointSpecialRecoveries: true)
            {
                InternalRepotEnabled = false
            };

            var firstTarget = path[0];
            var secondTarget = path[1];

            // First stuck: the special attempt is allowed (executor missing → failure
            // fallback) and the one-attempt guard is armed.
            await DispatchAsync(movement, firstTarget);
            Assert.Contains(lines, line => line.Contains("configured=Operation"));
            Assert.Contains(lines, line => line.Contains("Special recovery failed"));

            // A second stuck at the same unchanged position must NOT repeat it.
            await DispatchAsync(movement, firstTarget);
            Assert.Contains(lines, line => line.Contains("already attempted at this stuck location"));
            int configuredLogsAfterFirstEpisode = lines.Count(
                line => line.Contains("configured=Operation"));
            Assert.Equal(1, configuredLogsAfterFirstEpisode);

            // The waypoint is actually advanced/dequeued (player stands on it) —
            // AdvanceReachedWaypoints resets the special-attempt guard and the queue
            // keeps the next real waypoint of the loop path.
            var advance = typeof(MovementSystem).GetMethod(
                "AdvanceReachedWaypoints",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(advance);
            var advancedObj = advance!.Invoke(movement, new object[] { 5000f, 5000f });
            Assert.NotNull(advancedObj);
            Assert.True((bool)advancedObj!);

            // After the waypoint change the special recovery must be eligible again.
            await DispatchAsync(movement, secondTarget);
            Assert.Contains(lines, line => line.Contains("configured=Operation"));
            Assert.True(lines.Count(line => line.Contains("configured=Operation")) > configuredLogsAfterFirstEpisode);
        }

        [Fact]
        public async Task WorkflowRepotRequestAbortsRouteWithExplicitReason()
        {
            var lines = new List<string>();
            var movement = CreateMovement(lines.Add);
            Assert.False(movement.InternalRepotEnabled);
            var target = new Waypoint(
                5000,
                5000,
                MovementPrecision.Medium,
                BotMode.OnlyMove,
                stuckRecoveryType: WaypointStuckRecoveryType.Repot);

            await DispatchAsync(movement, target);

            Assert.True(movement.IsWaypointRepotRequested);
            Assert.False(movement.IsWaypointSpecialRecoveryActive);
            Assert.DoesNotContain(lines, line => line.Contains("[ReverseDiagonal] Started."));
            Assert.Contains(lines, line => line.Contains("[WaypointRecovery] Repot requested by waypoint"));
        }
    }
}
