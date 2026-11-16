using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Coverage for the EtanaConditionalMove custom operation:
    ///   • registration / selectability (Unstuck / StuckRecovery + flow operations),
    ///   • Etana-only map guard and validated position reads,
    ///   • the strict coordinate condition and the single destination decision,
    ///   • rectangular ±1 arrival tolerance,
    ///   • navigation through the existing waypoint movement (temporary waypoint only),
    ///   • the standard stuck detection / unstuck staying active and non-recursive,
    ///   • the destination surviving recovery, cancellation and map changes.
    /// </summary>
    public sealed class EtanaConditionalMoveTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly MethodInfo HandleStuckMethod = typeof(MovementSystem).GetMethod(
            "HandleConfirmedNavigationStuckAsync", PrivateInstance)
            ?? throw new InvalidOperationException("HandleConfirmedNavigationStuckAsync not found.");

        private static readonly FieldInfo ReverseDiagonalField = typeof(MovementSystem).GetField(
            "_reverseDiagonalRecovery", PrivateInstance)
            ?? throw new InvalidOperationException("_reverseDiagonalRecovery not found.");

        private static readonly FieldInfo StuckCountField = typeof(MovementSystem).GetField(
            "_consecutiveStuckAttempts", PrivateInstance)
            ?? throw new InvalidOperationException("_consecutiveStuckAttempts not found.");

        private static readonly FieldInfo WaypointsField = typeof(MovementSystem).GetField(
            "_waypoints", PrivateInstance)
            ?? throw new InvalidOperationException("_waypoints not found.");

        private static readonly FieldInfo MovingForwardField = typeof(MovementSystem).GetField(
            "_isMovingForward", PrivateInstance)
            ?? throw new InvalidOperationException("_isMovingForward not found.");

        private static readonly FieldInfo RecoveryStageStartField = typeof(ReverseDiagonalRecovery).GetField(
            "_stageStartTime", PrivateInstance)
            ?? throw new InvalidOperationException("ReverseDiagonalRecovery._stageStartTime not found.");

        private static readonly FieldInfo PathRunnerSteeringField = typeof(PathRunnerService).GetField(
            "_steeringMode", PrivateInstance)
            ?? throw new InvalidOperationException("PathRunnerService._steeringMode not found.");

        // ───────────────────── registration / selectability ─────────────────────

        [Fact]
        public void ActionIsRegisteredAndSelectableEverywhereOperationsAreListed()
        {
            Assert.True(BotOperations.IsKnown("EtanaConditionalMove"));
            Assert.Contains("EtanaConditionalMove", BotOperations.KnownNames);

            // Case-insensitive lookup is the existing registry contract.
            Assert.True(BotOperations.IsKnown("etanaconditionalmove"));

            BotOperations.OperationDelegate registered = BotOperations.Operations["EtanaConditionalMove"];
            Assert.Equal(nameof(BotOperations.EtanaConditionalMove), registered.Method.Name);
        }

        [Fact]
        public void RecoveryConfigurationKeepsTheOperationNameAndDoesNotEnableItImplicitly()
        {
            // The Path Editor writes the selected Unstuck operation into the path point.
            var point = new PathPoint(
                1, 2,
                stuckRecoveryType: WaypointStuckRecoveryType.Operation,
                stuckRecoveryOperation: "EtanaConditionalMove");
            var waypoint = new Waypoint(
                point.X, point.Y, point.Precision, point.Mode,
                point.CameraDistanceLock, point.AttackDisengageDistance, point.ZoneRestriction,
                point.StuckRecoveryType, point.StuckRecoveryOperation,
                point.StuckRecoveryPath, point.StuckRecoveryMobCameraDistance);

            Assert.Equal(WaypointStuckRecoveryType.Operation, waypoint.StuckRecoveryType);
            Assert.Equal("EtanaConditionalMove", waypoint.StuckRecoveryOperation);

            // Existing configurations keep the standard recovery: nothing is enabled by default.
            var untouched = new PathPoint(3, 4);
            Assert.Equal(WaypointStuckRecoveryType.Default, untouched.StuckRecoveryType);
            Assert.Equal("", untouched.StuckRecoveryOperation);
        }

        [Fact]
        public void RecoveryConfigurationSurvivesThePathFileRoundTrip()
        {
            var segment = new PathSegment
            {
                Name = "etana-conditional",
                Points = new List<PathPoint>
                {
                    new(1, 2,
                        stuckRecoveryType: WaypointStuckRecoveryType.Operation,
                        stuckRecoveryOperation: "EtanaConditionalMove")
                }
            };

            PathSegment? restored = JsonSerializer.Deserialize<PathSegment>(
                JsonSerializer.Serialize(segment));

            Assert.NotNull(restored);
            PathPoint point = Assert.Single(restored!.Points);
            Assert.Equal(WaypointStuckRecoveryType.Operation, point.StuckRecoveryType);
            Assert.Equal("EtanaConditionalMove", point.StuckRecoveryOperation);
        }

        // ───────────────────── destination selection ─────────────────────

        [Theory]
        [InlineData(268f, 192f, 274f, 191f)]
        [InlineData(268f, 193f, 274f, 191f)]
        [InlineData(268f, 195f, 263f, 199f)]
        [InlineData(260f, 200f, 263f, 199f)]
        // Strict comparisons: the boundaries belong to the second destination.
        [InlineData(267f, 192f, 263f, 199f)]
        [InlineData(270f, 192f, 263f, 199f)]
        [InlineData(268f, 190f, 263f, 199f)]
        [InlineData(268f, 194f, 263f, 199f)]
        [InlineData(269.9f, 193.9f, 274f, 191f)]
        [InlineData(267.1f, 190.1f, 274f, 191f)]
        public void DestinationSelectionUsesTheStrictCondition(float x, float y, float expectedX, float expectedY)
        {
            (float actualX, float actualY) = BotOperations.SelectEtanaConditionalMoveDestination(x, y);

            Assert.Equal(expectedX, actualX);
            Assert.Equal(expectedY, actualY);
        }

        [Fact]
        public void EtanaIsMap35InTheRepositoryMapTable()
        {
            Assert.Equal(35, ItemSellerService.MapEtana);
            Assert.Equal(35, BotOperations.EtanaConditionalMoveMapId);
        }

        [Fact]
        public async Task DifferentMapDoesNotStartAnyMovement()
        {
            var lines = new List<string>();
            var simulation = new SimulatedMemory { Map = 36, X = 268, Y = 192 };
            OperationContext ctx = CreateContext(simulation, lines);
            int navigationCalls = 0;

            bool result = await BotOperations.EtanaConditionalMoveAsync(
                ctx, CancellationToken.None, (waypoints, token) =>
                {
                    navigationCalls++;
                    return Task.FromResult(true);
                });

            Assert.False(result);
            Assert.Equal(0, navigationCalls);
            Assert.Contains(lines, line => line.Contains("Skipped") && line.Contains("not Etana"));
            Assert.DoesNotContain(lines, line => line.Contains("Navigation started"));
        }

        [Fact]
        public async Task InvalidMapOrPositionReadingDoesNotStartAnyMovement()
        {
            var lines = new List<string>();
            var simulation = new SimulatedMemory { Map = 35, X = 268, Y = 192, ReadsFail = true };
            OperationContext ctx = CreateContext(simulation, lines);
            int navigationCalls = 0;

            bool result = await BotOperations.EtanaConditionalMoveAsync(
                ctx, CancellationToken.None, (waypoints, token) =>
                {
                    navigationCalls++;
                    return Task.FromResult(true);
                });

            Assert.False(result);
            Assert.Equal(0, navigationCalls);
            Assert.Contains(lines, line => line.Contains("Invalid map/position reading"));
        }

        // ───────────────────── arrival tolerance ─────────────────────

        [Theory]
        [InlineData(274f, 191f, true)]
        [InlineData(273f, 190f, true)] // -1/-1 corner
        [InlineData(275f, 192f, true)] // +1/+1 corner
        [InlineData(275f, 191f, true)] // +1 on X only
        [InlineData(274f, 192f, true)] // +1 on Y only
        [InlineData(276f, 191f, false)] // outside X tolerance
        [InlineData(272f, 191f, false)] // outside X tolerance
        [InlineData(274f, 193f, false)] // outside Y tolerance
        [InlineData(274f, 189f, false)] // outside Y tolerance
        [InlineData(275.1f, 191f, false)] // rectangular, not Euclidean: 1.1 > 1
        [InlineData(274f, 192.1f, false)]
        public void ArrivalToleranceIsRectangularPerAxis(float x, float y, bool expected)
        {
            Assert.Equal(
                expected,
                BotOperations.IsEtanaConditionalMoveDestinationReached(x, y, 274f, 191f));
        }

        [Fact]
        public async Task AlreadyWithinToleranceCompletesImmediatelyWithoutMovement()
        {
            var lines = new List<string>();
            // (263,199) is both outside the strict condition and already inside the
            // tolerance of the destination selected for that branch.
            var simulation = new SimulatedMemory { Map = 35, X = 263, Y = 199 };
            OperationContext ctx = CreateContext(simulation, lines);
            int navigationCalls = 0;

            bool result = await BotOperations.EtanaConditionalMoveAsync(
                ctx, CancellationToken.None, (waypoints, token) =>
                {
                    navigationCalls++;
                    return Task.FromResult(true);
                });

            Assert.True(result);
            Assert.Equal(0, navigationCalls);
            Assert.Contains(lines, line => line.Contains("no movement needed"));
        }

        // ───────────────────── navigation / recovery ─────────────────────

        [Fact]
        public async Task NavigationUsesTheTemporaryDirectCameraDefaultRecoveryWaypoint()
        {
            var lines = new List<string>();
            var simulation = new SimulatedMemory { Map = 35, X = 268, Y = 192 };
            OperationContext ctx = CreateContext(simulation, lines);
            List<Waypoint>? handedOver = null;

            bool result = await BotOperations.EtanaConditionalMoveAsync(
                ctx, CancellationToken.None, (waypoints, token) =>
                {
                    handedOver = waypoints;
                    // The navigation system walked the player onto the destination.
                    simulation.X = 274;
                    simulation.Y = 191;
                    return Task.FromResult(true);
                });

            Assert.True(result);
            Waypoint waypoint = Assert.Single(handedOver!);

            Assert.Equal(274f, waypoint.X);
            Assert.Equal(191f, waypoint.Y);

            // Temporary target only: no saved path, no standing method, no route replacement.
            Assert.False(waypoint.IsOperationStep);
            Assert.Equal("", waypoint.OnArrivalOperation);
            Assert.Equal("", waypoint.StuckRecoveryPath);

            // Existing movement configuration reused.
            Assert.Equal(MovementPrecision.Exact, waypoint.Precision);
            Assert.Equal(BotMode.OnlyMove, waypoint.Mode);
            Assert.Equal(ZoneRestriction.Both, waypoint.ZoneRestriction);
            Assert.Equal(MovementSteeringMode.DirectCamera, waypoint.SteeringMode);

            // Default recovery: a stuck inside this action uses the standard unstuck and
            // can never dispatch a configurable operation (recursion guard).
            Assert.Equal(WaypointStuckRecoveryType.Default, waypoint.StuckRecoveryType);
            Assert.Equal("", waypoint.StuckRecoveryOperation);
        }

        [Fact]
        public async Task RecoveryMovementAloneDoesNotSucceedAndTheOriginalTargetIsRetained()
        {
            var lines = new List<string>();
            var simulation = new SimulatedMemory { Map = 35, X = 268, Y = 192 };
            OperationContext ctx = CreateContext(simulation, lines);
            var requestedTargets = new List<(float X, float Y)>();

            bool result = await BotOperations.EtanaConditionalMoveAsync(
                ctx, CancellationToken.None, (waypoints, token) =>
                {
                    Waypoint waypoint = waypoints.Single();
                    requestedTargets.Add((waypoint.X, waypoint.Y));

                    if (requestedTargets.Count == 1)
                    {
                        // The existing unstuck moved the player, then the leg ended without
                        // reaching the selected destination.
                        simulation.X = 270;
                        simulation.Y = 193;
                        return Task.FromResult(true);
                    }

                    // Second leg: the same destination, now really reached.
                    Assert.Equal(274f, waypoint.X);
                    Assert.Equal(191f, waypoint.Y);
                    simulation.X = 273;
                    simulation.Y = 191;
                    return Task.FromResult(true);
                });

            Assert.True(result);
            Assert.Equal(2, requestedTargets.Count);
            Assert.All(requestedTargets, target => Assert.Equal((274f, 191f), target));
            Assert.Contains(lines, line => line.Contains("retained destination"));
            Assert.Contains(lines, line => line.Contains("Destination reached"));
        }

        [Fact]
        public async Task FailedNavigationFailsTheActionWithoutReportingSuccess()
        {
            var lines = new List<string>();
            var simulation = new SimulatedMemory { Map = 35, X = 268, Y = 192 };
            OperationContext ctx = CreateContext(simulation, lines);
            int navigationCalls = 0;

            bool result = await BotOperations.EtanaConditionalMoveAsync(
                ctx, CancellationToken.None, (waypoints, token) =>
                {
                    navigationCalls++;
                    return Task.FromResult(false);
                });

            Assert.False(result);
            Assert.Equal(1, navigationCalls);
            Assert.Contains(lines, line => line.Contains("without reaching the destination"));
            Assert.DoesNotContain(lines, line => line.Contains("Result=Success"));
        }

        [Fact]
        public async Task TeleportAwayFromEtanaTerminatesTheActionAndNeverNavigatesTheSecondLeg()
        {
            var lines = new List<string>();
            var simulation = new SimulatedMemory { Map = 35, X = 268, Y = 192 };
            OperationContext ctx = CreateContext(simulation, lines);
            int navigationCalls = 0;

            bool result = await BotOperations.EtanaConditionalMoveAsync(
                ctx, CancellationToken.None, (waypoints, token) =>
                {
                    navigationCalls++;
                    simulation.Map = 40; // town teleport / portal
                    simulation.X = 100;
                    simulation.Y = 100;
                    return Task.FromResult(false);
                });

            Assert.False(result);
            Assert.Equal(1, navigationCalls);
            Assert.Contains(lines, line => line.Contains("action terminated"));
            Assert.Contains(lines, line => line.Contains("Map changed from Etana"));
        }

        [Fact]
        public async Task MatchingCoordinatesOnAnotherMapAreNeverReportedAsSuccess()
        {
            var lines = new List<string>();
            var simulation = new SimulatedMemory { Map = 35, X = 268, Y = 192 };
            OperationContext ctx = CreateContext(simulation, lines);

            bool result = await BotOperations.EtanaConditionalMoveAsync(
                ctx, CancellationToken.None, (waypoints, token) =>
                {
                    simulation.Map = 44;
                    simulation.X = 274;
                    simulation.Y = 191;
                    return Task.FromResult(true);
                });

            Assert.False(result);
            Assert.Contains(lines, line => line.Contains("action terminated"));
            Assert.DoesNotContain(lines, line => line.Contains("Result=Success"));
        }

        [Fact]
        public async Task InvalidReadingAfterNavigationNeverReportsSuccess()
        {
            var lines = new List<string>();
            var simulation = new SimulatedMemory { Map = 35, X = 268, Y = 192 };
            OperationContext ctx = CreateContext(simulation, lines);

            bool result = await BotOperations.EtanaConditionalMoveAsync(
                ctx, CancellationToken.None, (waypoints, token) =>
                {
                    simulation.ReadsFail = true; // player object lost mid-leg
                    return Task.FromResult(true);
                });

            Assert.False(result);
            Assert.Contains(lines, line => line.Contains("could not be read after navigation"));
        }

        // ───────────────────── reentrancy / cancellation ─────────────────────

        [Fact]
        public async Task ReentrantInvocationIsRejectedWithoutStartingAnotherMovement()
        {
            var lines = new List<string>();
            var simulation = new SimulatedMemory { Map = 35, X = 268, Y = 192 };
            OperationContext ctx = CreateContext(simulation, lines);
            int outerCalls = 0;
            int nestedCalls = 0;
            bool nestedResult = true;

            bool result = await BotOperations.EtanaConditionalMoveAsync(
                ctx, CancellationToken.None, async (waypoints, token) =>
                {
                    outerCalls++;

                    // This is exactly what would happen if the configured Unstuck action were
                    // invoked again from inside the active action.
                    nestedResult = await BotOperations.EtanaConditionalMoveAsync(
                        ctx, token, (nestedWaypoints, nestedToken) =>
                        {
                            nestedCalls++;
                            return Task.FromResult(true);
                        });

                    simulation.X = 274;
                    simulation.Y = 191;
                    return true;
                });

            Assert.True(result);
            Assert.False(nestedResult);
            Assert.Equal(1, outerCalls);
            Assert.Equal(0, nestedCalls);
            Assert.Contains(lines, line => line.Contains("Re-entrant call rejected"));
        }

        [Fact]
        public async Task CancelDuringNavigationStopsSafelyAndReleasesTheReentrancyGuard()
        {
            var lines = new List<string>();
            var simulation = new SimulatedMemory { Map = 35, X = 268, Y = 192 };
            OperationContext ctx = CreateContext(simulation, lines);
            using var cts = new CancellationTokenSource();
            int navigationCalls = 0;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                BotOperations.EtanaConditionalMoveAsync(ctx, cts.Token, async (waypoints, token) =>
                {
                    navigationCalls++;
                    cts.Cancel(); // Stop All / action interruption while the leg is running
                    try
                    {
                        await Task.Delay(50, token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Same behaviour as PathRunnerService: the leg releases its inputs
                        // and returns without success.
                    }

                    return false;
                }));

            Assert.Equal(1, navigationCalls);

            // The guard is released in the finally block, so the next action run works.
            simulation.X = 263;
            simulation.Y = 199;
            bool afterCancel = await BotOperations.EtanaConditionalMoveAsync(
                ctx, CancellationToken.None, (waypoints, token) => Task.FromResult(true));

            Assert.True(afterCancel);
        }

        // ───────────────────── existing navigation integration ─────────────────────

        [Fact]
        public async Task ProductionNavigationLegUsesTheExistingRunnerWithTheEtanaMapGuard()
        {
            var lines = new List<string>();
            var simulation = new SimulatedMemory { Map = 35, X = 268, Y = 192 };
            OperationContext ctx = CreateContext(simulation, lines);
            var waypoint = new Waypoint(
                274, 191, MovementPrecision.Exact, BotMode.OnlyMove,
                zoneRestriction: ZoneRestriction.Both,
                stuckRecoveryType: WaypointStuckRecoveryType.Default,
                steeringMode: MovementSteeringMode.DirectCamera);

            // A pre-cancelled token keeps the leg from injecting input while the real
            // MovementSystem created by the runner can still be inspected.
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            bool completed = await BotOperations.RunEtanaConditionalMoveNavigationLegAsync(
                ctx, new List<Waypoint> { waypoint }, cts.Token);

            Assert.False(completed);
            Assert.Equal(PathRunStopReason.Cancelled, ctx.PathRunner.LastStopReason);
            // The leg released the runner, so a follow-up leg (same destination) can start.
            Assert.False(ctx.PathRunner.IsRunning);

            MovementSystem movement = Assert.IsType<MovementSystem>(ctx.PathRunner.CurrentMovement);
            Assert.Equal(35, movement.ExpectedMap);
            Assert.Equal("EtanaConditionalMove", movement.RouteLabel);
            Assert.Equal(BotMode.OnlyMove, movement.CurrentMode);
            Assert.Equal(MovementSteeringMode.DirectCamera, PathRunnerSteeringField.GetValue(ctx.PathRunner));

            Waypoint queued = GetQueuedWaypoint(movement);
            Assert.Equal(274f, queued.X);
            Assert.Equal(191f, queued.Y);
            Assert.Equal(WaypointStuckRecoveryType.Default, queued.StuckRecoveryType);
            Assert.Equal(MovementSteeringMode.DirectCamera, queued.SteeringMode);
        }

        [Fact]
        public async Task TemporaryWaypointDispatchesTheExistingStandardUnstuckAndKeepsTheDestination()
        {
            var lines = new List<string>();
            var simulation = new SimulatedMemory { Map = 35, X = 268, Y = 192 };
            var waypoint = new Waypoint(
                274, 191, MovementPrecision.Exact, BotMode.OnlyMove,
                zoneRestriction: ZoneRestriction.Both,
                stuckRecoveryType: WaypointStuckRecoveryType.Default,
                steeringMode: MovementSteeringMode.DirectCamera);

            var movement = new TestMovementSystem(
                simulation.CreateMemory(lines.Add), lines.Add, new List<Waypoint> { waypoint });

            await DispatchStuckAsync(movement, waypoint);

            // The existing standard unstuck runs — not a configurable recovery operation.
            Assert.Equal(1, GetStuckCount(movement));
            Assert.True(IsReverseDiagonalActive(movement));
            Assert.Contains(lines, line => line.Contains("[ReverseDiagonal] Started."));
            Assert.DoesNotContain(lines, line => line.Contains("[WaypointRecovery] Running operation"));
            Assert.DoesNotContain(lines, line => line.Contains("[WaypointRecovery] configured="));

            // The temporary destination survives the unstuck.
            Waypoint queuedDuringRecovery = GetQueuedWaypoint(movement);
            Assert.Equal(waypoint.X, queuedDuringRecovery.X);
            Assert.Equal(waypoint.Y, queuedDuringRecovery.Y);

            await DriveSuccessfulRecoveryAsync(movement, simulation, 271, 192);

            Assert.False(IsReverseDiagonalActive(movement));
            Assert.Contains(lines, line => line.Contains("PhysicalRecoverySucceeded=True"));

            Waypoint queuedAfterRecovery = GetQueuedWaypoint(movement);
            Assert.Equal(waypoint.X, queuedAfterRecovery.X);
            Assert.Equal(waypoint.Y, queuedAfterRecovery.Y);

            // Navigation toward the retained destination resumes on the next ticks (the
            // existing first-step heading alignment may turn in place before W goes down).
            for (int tick = 0; tick < 8 && !IsMovingForward(movement); tick++)
                await movement.Update(CancellationToken.None);
            Assert.True(IsMovingForward(movement), string.Join(" || ", lines.TakeLast(45)));

            // Recovery movement never completes the action by itself: the destination is
            // still not reached at the recovery position.
            Assert.False(BotOperations.IsEtanaConditionalMoveDestinationReached(271, 192, 274, 191));
            Assert.True(BotOperations.IsEtanaConditionalMoveDestinationReached(273, 191, 274, 191));
        }

        [Fact]
        public async Task RepeatedStuckEventsRespectTheExistingOutsideAttemptLimit()
        {
            var lines = new List<string>();
            var simulation = new SimulatedMemory { Map = 35, X = 268, Y = 192, InCity = false };
            var waypoint = new Waypoint(
                274, 191, MovementPrecision.Exact, BotMode.OnlyMove,
                zoneRestriction: ZoneRestriction.Both,
                stuckRecoveryType: WaypointStuckRecoveryType.Default,
                steeringMode: MovementSteeringMode.DirectCamera);

            var movement = new TestMovementSystem(
                simulation.CreateMemory(lines.Add), lines.Add, new List<Waypoint> { waypoint });

            await DispatchStuckAsync(movement, waypoint);
            await DispatchStuckAsync(movement, waypoint);
            Assert.Equal(2, GetStuckCount(movement));
            Assert.False(movement.IsNavigationFailureEscalationRequested);

            await DispatchStuckAsync(movement, waypoint);

            // Existing limits/escalation apply unchanged; the action is not reported as a
            // success and the route failure handling owns the next step.
            Assert.Equal(3, GetStuckCount(movement));
            Assert.True(movement.IsNavigationFailureEscalationRequested);
            Assert.Contains(lines, line => line.Contains("Attempt=3/3"));
            Assert.Contains(lines, line => line.Contains("navigation failure limit reached"));
        }

        [Fact]
        public async Task ProductionLegReceivesTheWorkflowPauseController()
        {
            var lines = new List<string>();
            var simulation = new SimulatedMemory { Map = 35, X = 268, Y = 192 };
            var pauseController = new BotPauseController();
            OperationContext ctx = CreateContext(simulation, lines, pauseController);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await BotOperations.RunEtanaConditionalMoveNavigationLegAsync(
                ctx,
                new List<Waypoint>
                {
                    new(274, 191, MovementPrecision.Exact, BotMode.OnlyMove,
                        zoneRestriction: ZoneRestriction.Both,
                        stuckRecoveryType: WaypointStuckRecoveryType.Default,
                        steeringMode: MovementSteeringMode.DirectCamera)
                },
                cts.Token);

            Assert.Same(pauseController, ctx.PathRunner.PauseController);
        }

        [Fact]
        public async Task OperationRunnerPublishesThePauseControllerToTheOperationContext()
        {
            var lines = new List<string>();
            var simulation = new SimulatedMemory { Map = 35, X = 268, Y = 192 };
            OperationContext ctx = CreateContext(simulation, lines);
            var pauseController = new BotPauseController();
            var runner = new OperationRunnerService(ctx, lines.Add)
            {
                PauseController = pauseController
            };
            BotPauseController? published = null;

            bool ok = await runner.RunOnceAsync(
                "EtanaConditionalMove",
                CancellationToken.None,
                1,
                (operationContext, token) =>
                {
                    published = operationContext.PauseController;
                    return Task.FromResult(true);
                });

            Assert.True(ok);
            Assert.Same(pauseController, published);
        }

        [Fact]
        public async Task NoSecondMovementControllerIsStartedOnARunnerThatIsAlreadyNavigating()
        {
            var lines = new List<string>();
            // Failing reads: the active route never receives a position and therefore never
            // injects input. Only the ownership guard is under test.
            GameMemoryService memory = CreateFailingMemory(lines.Add);
            OperationContext ctx = CreateContext(memory, lines);

            using var routeCts = new CancellationTokenSource();
            Task<bool> activeRoute = ctx.PathRunner.RunPathAsync(
                new List<Waypoint>
                {
                    new(268, 190, MovementPrecision.Accurate, BotMode.OnlyMove,
                        zoneRestriction: ZoneRestriction.Both)
                },
                loop: true,
                routeCts.Token);

            MovementSystem? activeMovement = null;
            for (int i = 0; i < 250 && activeMovement == null; i++)
            {
                activeMovement = ctx.PathRunner.CurrentMovement;
                if (activeMovement == null)
                    await Task.Delay(20);
            }
            Assert.NotNull(activeMovement);
            Assert.True(ctx.PathRunner.IsRunning);

            bool completed = await BotOperations.RunEtanaConditionalMoveNavigationLegAsync(
                ctx,
                new List<Waypoint>
                {
                    new(274, 191, MovementPrecision.Exact, BotMode.OnlyMove,
                        zoneRestriction: ZoneRestriction.Both,
                        stuckRecoveryType: WaypointStuckRecoveryType.Default,
                        steeringMode: MovementSteeringMode.DirectCamera)
                },
                CancellationToken.None);

            // The active route keeps its movement system and its ownership.
            Assert.False(completed);
            Assert.Same(activeMovement, ctx.PathRunner.CurrentMovement);
            Assert.True(ctx.PathRunner.IsRunning);
            Assert.Contains(lines, line => line.Contains("already navigating"));

            routeCts.Cancel();
            Task finished = await Task.WhenAny(activeRoute, Task.Delay(10_000));
            Assert.Same(activeRoute, finished);
            Assert.False(ctx.PathRunner.IsRunning);
        }

        // ───────────────────── helpers ─────────────────────

        private static GameMemoryService CreateFailingMemory(Action<string> log)
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

        private static OperationContext CreateContext(
            SimulatedMemory simulation,
            List<string> lines,
            BotPauseController? pauseController = null)
            => CreateContext(simulation.CreateMemory(lines.Add), lines, pauseController);

        private static OperationContext CreateContext(
            GameMemoryService memory,
            List<string> lines,
            BotPauseController? pauseController = null)
        {
            var pathRunner = new PathRunnerService(
                memory,
                lines.Add,
                enableWaypointSpecialRecoveries: false,
                steeringMode: MovementSteeringMode.DirectCamera);
            var context = new OperationContext(
                memory,
                pathRunner,
                new ItemSellerService(memory, lines.Add),
                new BotProfile(),
                lines.Add,
                () => { })
            {
                PauseController = pauseController
            };
            return context;
        }

        private static Waypoint GetQueuedWaypoint(MovementSystem movement)
        {
            var queue = (Queue<Waypoint>)(WaypointsField.GetValue(movement)
                ?? throw new InvalidOperationException("Waypoint queue missing."));
            Assert.NotEmpty(queue);
            return queue.Peek();
        }

        private static int GetStuckCount(MovementSystem movement)
            => (int)(StuckCountField.GetValue(movement) ?? 0);

        private static bool IsMovingForward(MovementSystem movement)
            => (bool)(MovingForwardField.GetValue(movement) ?? false);

        private static ReverseDiagonalRecovery GetRecovery(MovementSystem movement)
            => (ReverseDiagonalRecovery)(ReverseDiagonalField.GetValue(movement)
                ?? throw new InvalidOperationException("Recovery instance missing."));

        private static bool IsReverseDiagonalActive(MovementSystem movement)
            => GetRecovery(movement).IsActive;

        private static async Task DispatchStuckAsync(MovementSystem movement, Waypoint target)
        {
            var task = (Task?)HandleStuckMethod.Invoke(
                movement,
                new object[] { 268f, 192f, target, CancellationToken.None, "EtanaConditionalMoveTest" });
            Assert.NotNull(task);
            await task!;
        }

        /// <summary>
        /// Forces the current recovery stage to look old, then runs one real
        /// MovementSystem tick so the standard unstuck state machine runs deterministically.
        /// </summary>
        private static async Task TickRecoveryUpdateAsync(MovementSystem movement)
        {
            RecoveryStageStartField.SetValue(GetRecovery(movement), DateTime.Now.AddSeconds(-5));
            await movement.Update(CancellationToken.None);
        }

        private static async Task DriveSuccessfulRecoveryAsync(
            MovementSystem movement,
            SimulatedMemory simulation,
            float displacedX,
            float displacedY)
        {
            simulation.X = 268;
            simulation.Y = 192;
            await TickRecoveryUpdateAsync(movement); // camera settle -> W pressed, attempt baseline

            simulation.X = displacedX;
            simulation.Y = displacedY;
            await TickRecoveryUpdateAsync(movement); // physical displacement -> success cooldown

            await TickRecoveryUpdateAsync(movement); // cooldown -> Recovered
        }

        /// <summary>
        /// Movement system equivalent to the one the runner creates for the temporary leg:
        /// DirectCamera steering, standard recovery only, synthetic input suppressed.
        /// </summary>
        private sealed class TestMovementSystem : MovementSystem
        {
            public TestMovementSystem(
                GameMemoryService memory,
                Action<string> log,
                List<Waypoint> path)
                : base(
                    memory,
                    log,
                    path[^1].X,
                    path[^1].Y,
                    customPath: path,
                    loopPath: false,
                    enableWaypointSpecialRecoveries: false,
                    steeringMode: MovementSteeringMode.DirectCamera)
            {
                InternalRepotEnabled = false;
                AllowSpeedPotionBuff = false;
            }

            protected override void SendSyntheticForwardKeyEvent(bool keyUp) { }

            protected override void SendSyntheticBackwardKeyEvent(bool keyUp) { }

            protected override void SendTurnKeyEvent(bool leftA, bool keyUp) { }

            protected override void CaptureStuckScreenshot() { }
        }

        /// <summary>
        /// Minimal memory simulation: coherent map/X/Y reads (optionally failing) plus the
        /// camera and player action values the movement system reads during a tick.
        /// </summary>
        private sealed class SimulatedMemory
        {
            public const ulong ModuleBase = 0x100000;
            private const ulong PlayerBase = 0x200000;
            private const ulong CameraBase = 0x300000;
            private const ulong PlayerRange = 0x2000;
            private const ulong CameraRange = 0x400;

            public float X = 268f;
            public float Y = 192f;
            public int Map = ItemSellerService.MapEtana;
            public bool InCity;
            public bool ReadsFail;
            public byte Action = 25;
            public float CameraAngle;
            public short CameraDistance = BotConstants.Camera.DefaultDistanceLock;

            public GameMemoryService CreateMemory(Action<string> log)
                => new GameMemoryService(0, Read, Write, ModuleBase, 8, log);

            private bool Read(uint pid, ulong address, byte[] buffer, out uint bytesRead)
            {
                bytesRead = 0;
                if (ReadsFail || buffer.Length == 0)
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
