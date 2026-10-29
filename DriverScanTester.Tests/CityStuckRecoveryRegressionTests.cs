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
    /// STUCK 1/2 -> reverse diagonal physically succeeds -> counter was reset -> repeat.
    /// Physical recovery success must never reset the navigation failure count for the
    /// same unresolved waypoint, and the city route must stop after 2 attempts so the
    /// coordinator can wait 10 minutes, reset and start again from the Repot step.
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

        private static readonly FieldInfo NavigationFailureEscalationField = typeof(MovementSystem).GetField(
            "_navigationFailureEscalationRequested",
            PrivateInstance) ?? throw new InvalidOperationException("_navigationFailureEscalationRequested not found.");

        private static readonly FieldInfo MovingForwardField = typeof(MovementSystem).GetField(
            "_isMovingForward",
            PrivateInstance) ?? throw new InvalidOperationException("_isMovingForward not found.");

        private static readonly FieldInfo IgnoreStuckUntilField = typeof(MovementSystem).GetField(
            "_ignoreStuckUntil",
            PrivateInstance) ?? throw new InvalidOperationException("_ignoreStuckUntil not found.");

        private static readonly FieldInfo InitialHeadingAlignmentField = typeof(MovementSystem).GetField(
            "_initialHeadingAlignmentPending",
            PrivateInstance) ?? throw new InvalidOperationException("_initialHeadingAlignmentPending not found.");

        private static readonly FieldInfo InitialResyncDoneField = typeof(MovementSystem).GetField(
            "_initialResyncDone",
            PrivateInstance) ?? throw new InvalidOperationException("_initialResyncDone not found.");

        private static readonly FieldInfo LastMoveProgressTimeField = typeof(MovementSystem).GetField(
            "_lastMoveProgressTime",
            PrivateInstance) ?? throw new InvalidOperationException("_lastMoveProgressTime not found.");

        private static readonly FieldInfo BestWaypointDistanceField = typeof(MovementSystem).GetField(
            "_bestWaypointDistance",
            PrivateInstance) ?? throw new InvalidOperationException("_bestWaypointDistance not found.");

        private static readonly FieldInfo LastNavigationProgressTimeField = typeof(MovementSystem).GetField(
            "_lastNavigationProgressTime",
            PrivateInstance) ?? throw new InvalidOperationException("_lastNavigationProgressTime not found.");

        private static readonly MethodInfo UpdateNavigationProgressMethod = typeof(MovementSystem).GetMethod(
            "UpdateNavigationProgress",
            PrivateInstance) ?? throw new InvalidOperationException("UpdateNavigationProgress not found.");

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

        private static async Task<bool> UpdateNavigationProgressAsync(
            MovementSystem movement,
            Waypoint target,
            float x,
            float y)
        {
            var task = (Task<bool>?)UpdateNavigationProgressMethod.Invoke(
                movement,
                new object[] { target, x, y, CancellationToken.None });
            Assert.NotNull(task);
            return await task!;
        }

        private static DateTime GetNavigationProgressTime(MovementSystem movement)
            => (DateTime)(LastNavigationProgressTimeField.GetValue(movement) ?? DateTime.MinValue);

        private static List<Waypoint> CreateReportedRoute() => new()
        {
            new Waypoint(325, 166, MovementPrecision.Accurate, BotMode.OnlyMove,
                zoneRestriction: ZoneRestriction.Both),
            new Waypoint(335, 143, MovementPrecision.Accurate, BotMode.OnlyMove,
                zoneRestriction: ZoneRestriction.Both)
        };

        private static SyntheticInputPathMovementSystem CreateReportedMovement(
            SimulatedGameMemory simulation,
            Action<string> log,
            float x = 326f,
            float y = 164f,
            byte action = 75)
        {
            simulation.X = x;
            simulation.Y = y;
            simulation.Action = action;
            simulation.InCity = false;
            var movement = new SyntheticInputPathMovementSystem(
                simulation.CreateMemory(log),
                log,
                CreateReportedRoute());
            // The reported run was already navigating WP5; the initial route-resync
            // optimisation (unrelated to this regression) is skipped so the active
            // waypoint stays the exact reproduction target.
            InitialResyncDoneField.SetValue(movement, true);
            return movement;
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
        public async Task SuccessfulPhysicalRecoveryPreservesNavigationFailuresAndSecondCityStuckStopsTheRoute()
        {
            var lines = new List<string>();
            var simulation = new SimulatedGameMemory();
            var target = new Waypoint(265, 196, MovementPrecision.Accurate, BotMode.OnlyMove,
                zoneRestriction: ZoneRestriction.Both);
            var movement = CreateMovement(simulation, lines.Add, new List<Waypoint> { target });

            // ── Stuck event 1: physical recovery succeeds, navigation failure stays 1/2 ──
            await DispatchStuckAsync(movement, target);
            Assert.Equal(1, GetStuckCount(movement));

            await DriveSuccessfulRecoveryAsync(movement, simulation, 262, 188, 256, 188);

            Assert.Equal(1, GetStuckCount(movement));
            Assert.False(IsRecoveryActive(movement));
            Assert.Contains(lines, line => line.Contains("PhysicalRecoverySucceeded=True"));
            Assert.Contains(lines, line => line.Contains("navigation failure count preserved: 1/2"));

            // ── Stuck event 2: the city limit (2) is reached — the route stops instead of
            //    hammering a third recovery in the same spot. ──
            await DispatchStuckAsync(movement, target);

            Assert.True(movement.IsNavigationFailureEscalationRequested);
            Assert.Contains(lines, line => line.Contains("Attempt=2/2"));
            Assert.Contains(lines, line => line.Contains("navigation failure limit reached"));
            Assert.Contains(lines, line => line.Contains("the coordinator waits 10 min"));
            Assert.False(IsRecoveryActive(movement));

            int startedBeforeThirdDispatch = lines.Count(line => line.Contains("[ReverseDiagonal] Started."));
            await DispatchStuckAsync(movement, target);

            Assert.True(movement.IsNavigationFailureEscalationRequested);
            Assert.False(IsRecoveryActive(movement));
            Assert.DoesNotContain(lines, line => line.Contains("Attempt=3/2"));
            Assert.Equal(startedBeforeThirdDispatch, lines.Count(line => line.Contains("[ReverseDiagonal] Started.")));
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

            // The next waypoint starts with a clean history: a new stuck is 1/2, not 2/2.
            await DispatchStuckAsync(movement, second);
            Assert.Equal(1, GetStuckCount(movement));
        }

        [Fact]
        public async Task OutsideNavigationEscalatesAfterThreeFailuresForTheSameWaypoint()
        {
            var lines = new List<string>();
            var simulation = new SimulatedGameMemory();
            var target = new Waypoint(265, 196, MovementPrecision.Accurate, BotMode.OnlyMove);
            var movement = CreateMovement(simulation, lines.Add, new List<Waypoint> { target }, inCity: false);

            await DispatchStuckAsync(movement, target);
            await DispatchStuckAsync(movement, target);
            Assert.Equal(2, GetStuckCount(movement));
            Assert.False(movement.IsNavigationFailureEscalationRequested);

            await DispatchStuckAsync(movement, target);

            Assert.True(movement.IsNavigationFailureEscalationRequested);
            Assert.Equal(3, GetStuckCount(movement));
            Assert.Contains(lines, line => line.Contains("Attempt=3/3"));
            Assert.Contains(lines, line => line.Contains("[NavigationFailure] Escalating"));
            Assert.Equal(2, (int)CityAttemptLimitField.GetRawConstantValue()!);
            Assert.Equal(3, (int)OutsideAttemptLimitField.GetRawConstantValue()!);
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
            Assert.Contains(lines, line => line.Contains("PhysicalRecoverySucceeded=False") && line.Contains("preserved: 1/2"));

            // A second stuck event increments; the failed recovery did not wipe history.
            await DispatchStuckAsync(movement, target);
            Assert.Equal(2, GetStuckCount(movement));
        }

        [Fact]
        public async Task OneSuccessfulPhysicalRecoveryResumesRouteButDoesNotClearFailureHistory()
        {
            var lines = new List<string>();
            var simulation = new SimulatedGameMemory { InCity = false };
            var target = new Waypoint(265, 196, MovementPrecision.Accurate,
                BotMode.MoveAndAttackAndLoot, zoneRestriction: ZoneRestriction.Both);
            var movement = CreateMovement(simulation, lines.Add, new List<Waypoint> { target }, inCity: false);

            await DispatchStuckAsync(movement, target);
            await DriveSuccessfulRecoveryAsync(movement, simulation, 262, 188, 256, 188);

            Assert.Equal(1, GetStuckCount(movement));
            Assert.Contains(lines, line => line.Contains("PhysicalRecoverySucceeded=True"));
            Assert.Contains(lines, line => line.Contains("NavigationRecovered=False"));
            Assert.False(movement.IsNavigationFailureEscalationRequested);

            // Only ordinary route progress that beats the pre-collision best proves
            // navigation resumed; it still does not erase the same-waypoint history.
            Assert.False(await UpdateNavigationProgressAsync(movement, target, 264f, 190f));
            Assert.Equal(1, GetStuckCount(movement));
            Assert.Contains(lines, line => line.Contains("NavigationRecovered=True"));
        }

        [Fact]
        public async Task BacktrackingAndReapproachingTheSameCollisionDoesNotResetNavigationClockOrBestDistance()
        {
            var lines = new List<string>();
            var simulation = new SimulatedGameMemory { InCity = false };
            var target = new Waypoint(265, 196, MovementPrecision.Accurate, BotMode.OnlyMove,
                zoneRestriction: ZoneRestriction.Both);
            var movement = CreateMovement(simulation, lines.Add, new List<Waypoint> { target }, inCity: false);

            await DispatchStuckAsync(movement, target);
            float bestAtCollision = (float)BestWaypointDistanceField.GetValue(movement)!;
            DateTime timerAtCollision = (DateTime)LastNavigationProgressTimeField.GetValue(movement)!;

            // Simulate ReverseDiagonal backing away, then returning toward the same wall.
            Assert.False(await UpdateNavigationProgressAsync(movement, target, 256f, 188f));
            Assert.False(await UpdateNavigationProgressAsync(movement, target, 262.5f, 188f));

            float bestAfterRecovery = (float)BestWaypointDistanceField.GetValue(movement)!;
            Assert.True(bestAfterRecovery < bestAtCollision); // the physical best is retained
            Assert.Equal(timerAtCollision, (DateTime)LastNavigationProgressTimeField.GetValue(movement)!);

            // Returning to the old collision position is worse than the recovery best,
            // so it cannot refresh the navigation timer.
            Assert.False(await UpdateNavigationProgressAsync(movement, target, 262f, 188f));
            Assert.Equal(bestAfterRecovery, (float)BestWaypointDistanceField.GetValue(movement)!);
            Assert.Equal(timerAtCollision, (DateTime)LastNavigationProgressTimeField.GetValue(movement)!);
            Assert.Equal(1, GetStuckCount(movement));

            await DispatchStuckAsync(movement, target);
            Assert.Equal(2, GetStuckCount(movement));
            Assert.False(movement.IsNavigationFailureEscalationRequested);
        }

        [Fact]
        public async Task FifteenSecondsWithoutNavigationProgressEscalatesToRepotWorkflow()
        {
            var lines = new List<string>();
            var simulation = new SimulatedGameMemory { InCity = false };
            var target = new Waypoint(265, 196, MovementPrecision.Accurate, BotMode.MoveAndAttackAndLoot,
                zoneRestriction: ZoneRestriction.Both);
            var movement = CreateMovement(simulation, lines.Add, new List<Waypoint> { target }, inCity: false);
            await DispatchStuckAsync(movement, target);
            LastNavigationProgressTimeField.SetValue(movement, DateTime.UtcNow.AddSeconds(-16));

            bool escalated = await UpdateNavigationProgressAsync(movement, target, 262f, 188f);

            Assert.True(escalated);
            Assert.True(movement.IsNavigationFailureEscalationRequested);
            Assert.Contains(lines, line => line.Contains("no genuine waypoint progress for"));
        }

        [Fact]
        public async Task StationaryNearWaypointStartsNavigationClockAndInvokesExistingUnstuck()
        {
            var lines = new List<string>();
            var simulation = new SimulatedGameMemory();
            MovementSystem movement = CreateReportedMovement(simulation, lines.Add);

            IgnoreStuckUntilField.SetValue(movement, DateTime.Now.AddSeconds(-1));
            InitialHeadingAlignmentField.SetValue(movement, false);

            // Tick 1 requests forward movement (W down), tick 2 opens the navigation
            // window. This is the exact reported state: Fwd=True, Act=75, distance 2.24.
            await movement.Update(CancellationToken.None);
            await movement.Update(CancellationToken.None);

            Assert.False(IsRecoveryActive(movement));
            Assert.Equal(0, GetStuckCount(movement));
            DateTime windowStart = GetNavigationProgressTime(movement);
            Assert.NotEqual(DateTime.MinValue, windowStart);

            // The player stays at (326,164) for longer than the existing navigation
            // no-progress timeout.
            LastNavigationProgressTimeField.SetValue(movement, DateTime.UtcNow.AddSeconds(-16));
            await movement.Update(CancellationToken.None);

            Assert.Equal(1, GetStuckCount(movement));
            Assert.True(IsRecoveryActive(movement));
            Assert.False(movement.IsNavigationFailureEscalationRequested);
            Assert.Contains(lines, line => line.Contains("[NavigationStall]") &&
                                           line.Contains("325") &&
                                           line.Contains("166") &&
                                           line.Contains("noProgress=") &&
                                           line.Contains("invoking existing unstuck"));
            Assert.Contains(lines, line => line.Contains("[Unstuck]") && line.Contains("Attempt=1/3"));
            Assert.Contains(lines, line => line.Contains("[ReverseDiagonal] Started."));
            Assert.DoesNotContain(lines, line => line.Contains("[ConnectionWatchdog]"));
        }

        [Fact]
        public async Task GenuineMovementTowardWaypointDoesNotTriggerUnstuck()
        {
            var lines = new List<string>();
            var simulation = new SimulatedGameMemory();
            MovementSystem movement = CreateReportedMovement(simulation, lines.Add, x: 340f, y: 166f);
            Waypoint wp5 = CreateReportedRoute()[0];
            MovingForwardField.SetValue(movement, true);

            Assert.False(await UpdateNavigationProgressAsync(movement, wp5, 340f, 166f));
            LastNavigationProgressTimeField.SetValue(movement, DateTime.UtcNow.AddSeconds(-16));

            // 2.0 units closer is meaningful progress and refreshes the window.
            Assert.False(await UpdateNavigationProgressAsync(movement, wp5, 338f, 166f));
            DateTime refreshed = GetNavigationProgressTime(movement);
            Assert.True(refreshed > DateTime.UtcNow.AddSeconds(-2));
            Assert.False(IsRecoveryActive(movement));
            Assert.Equal(0, GetStuckCount(movement));
            Assert.DoesNotContain(lines, line => line.Contains("[NavigationStall]"));

            // Further normal approach also stays quiet.
            LastNavigationProgressTimeField.SetValue(movement, DateTime.UtcNow.AddSeconds(-10));
            Assert.False(await UpdateNavigationProgressAsync(movement, wp5, 335f, 166f));
            Assert.Equal(0, GetStuckCount(movement));
        }

        [Fact]
        public async Task SmallCoordinateFluctuationsCannotSuppressTheNavigationStall()
        {
            var lines = new List<string>();
            var simulation = new SimulatedGameMemory();
            MovementSystem movement = CreateReportedMovement(simulation, lines.Add);
            Waypoint wp5 = CreateReportedRoute()[0];
            MovingForwardField.SetValue(movement, true);

            Assert.False(await UpdateNavigationProgressAsync(movement, wp5, 326f, 164f));
            LastNavigationProgressTimeField.SetValue(movement, DateTime.UtcNow.AddSeconds(-10));
            DateTime before = GetNavigationProgressTime(movement);

            // Fluctuations that do not improve waypoint distance by 1.0 must not reset
            // the no-progress clock.
            Assert.False(await UpdateNavigationProgressAsync(movement, wp5, 327f, 165f));
            Assert.False(await UpdateNavigationProgressAsync(movement, wp5, 327f, 164f));
            Assert.False(await UpdateNavigationProgressAsync(movement, wp5, 326f, 164f));
            Assert.Equal(before, GetNavigationProgressTime(movement));
            Assert.Equal(0, GetStuckCount(movement));

            LastNavigationProgressTimeField.SetValue(movement, DateTime.UtcNow.AddSeconds(-16));
            Assert.True(await UpdateNavigationProgressAsync(movement, wp5, 326f, 164f));
            Assert.Equal(1, GetStuckCount(movement));
            Assert.True(IsRecoveryActive(movement));
            Assert.Contains(lines, line => line.Contains("[NavigationStall]"));
        }

        [Fact]
        public async Task VerifiedRecoveryResumesNavigationAndAdvancesTheWaypoint()
        {
            var lines = new List<string>();
            var simulation = new SimulatedGameMemory();
            MovementSystem movement = CreateReportedMovement(simulation, lines.Add);

            IgnoreStuckUntilField.SetValue(movement, DateTime.Now.AddSeconds(-1));
            InitialHeadingAlignmentField.SetValue(movement, false);
            await movement.Update(CancellationToken.None);
            await movement.Update(CancellationToken.None);
            LastNavigationProgressTimeField.SetValue(movement, DateTime.UtcNow.AddSeconds(-16));
            await movement.Update(CancellationToken.None);
            Assert.True(IsRecoveryActive(movement));

            // ReverseDiagonal verifies real displacement before reporting success.
            await DriveSuccessfulRecoveryAsync(movement, simulation, 326f, 164f, 330f, 164f);
            Assert.False(IsRecoveryActive(movement));
            Assert.Contains(lines, line => line.Contains("PhysicalRecoverySucceeded=True"));
            Assert.Equal(1, GetStuckCount(movement));

            // Route movement resumes: reaching WP5 within its acceptance radius advances
            // to WP6, which is the only proof that navigation actually resumed.
            simulation.X = 324f;
            simulation.Y = 165f;
            await movement.Update(CancellationToken.None);

            Assert.Contains(lines, line => line.Contains("325") && line.Contains("166") && line.Contains("reached"));
            Assert.Contains(lines, line => line.Contains("Tracking waypoint=WP2") &&
                                           line.Contains("335") && line.Contains("143"));
            Assert.Equal(0, GetStuckCount(movement));
            Assert.False(movement.IsNavigationFailureEscalationRequested);
        }

        [Fact]
        public async Task FailedRecoveryFallsBackToTheExistingBoundedEscalation()
        {
            var lines = new List<string>();
            var simulation = new SimulatedGameMemory();
            MovementSystem movement = CreateReportedMovement(simulation, lines.Add);
            Waypoint wp5 = CreateReportedRoute()[0];

            IgnoreStuckUntilField.SetValue(movement, DateTime.Now.AddSeconds(-1));
            InitialHeadingAlignmentField.SetValue(movement, false);
            await movement.Update(CancellationToken.None);
            await movement.Update(CancellationToken.None);
            LastNavigationProgressTimeField.SetValue(movement, DateTime.UtcNow.AddSeconds(-16));
            await movement.Update(CancellationToken.None);
            Assert.Equal(1, GetStuckCount(movement));

            // The maneuver produces no meaningful movement: the existing recovery
            // exhausts its own bounded attempts and preserves the failure history.
            await DriveFailedRecoveryAsync(movement, simulation, 326f, 164f);
            Assert.False(IsRecoveryActive(movement));
            Assert.Equal(1, GetStuckCount(movement));
            Assert.Contains(lines, line => line.Contains("PhysicalRecoverySucceeded=False"));
            Assert.False(movement.IsNavigationFailureEscalationRequested);

            // Navigation resumes with W held but still cannot progress: the existing
            // navigation-failure fallback runs instead of looping the failed maneuver.
            MovingForwardField.SetValue(movement, true);
            LastNavigationProgressTimeField.SetValue(movement, DateTime.UtcNow.AddSeconds(-16));
            Assert.True(await UpdateNavigationProgressAsync(movement, wp5, 326f, 164f),
                string.Join(Environment.NewLine, lines));

            Assert.True(movement.IsNavigationFailureEscalationRequested);
            Assert.Contains(lines, line => line.Contains("no genuine waypoint progress for"));
            Assert.DoesNotContain(lines, line => line.Contains("Attempt=2/3"));
        }

        [Fact]
        public async Task PlayerInsideAcceptanceRadiusAdvancesWithoutRecovery()
        {
            var lines = new List<string>();
            var simulation = new SimulatedGameMemory();
            MovementSystem movement = CreateReportedMovement(simulation, lines.Add, x: 325f, y: 167f);

            IgnoreStuckUntilField.SetValue(movement, DateTime.Now.AddSeconds(-1));
            InitialHeadingAlignmentField.SetValue(movement, false);
            await movement.Update(CancellationToken.None);

            Assert.Contains(lines, line => line.Contains("325") && line.Contains("166") && line.Contains("reached"));
            Assert.Contains(lines, line => line.Contains("Tracking waypoint=WP") &&
                                           line.Contains("335") && line.Contains("143"));
            Assert.Equal(0, GetStuckCount(movement));
            Assert.False(IsRecoveryActive(movement));
            Assert.DoesNotContain(lines, line => line.Contains("[NavigationStall]"));
        }

        [Fact]
        public async Task IntentionalStationaryNavigationDoesNotCountAsAStall()
        {
            var lines = new List<string>();
            var simulation = new SimulatedGameMemory();
            MovementSystem movement = CreateReportedMovement(simulation, lines.Add);
            Waypoint wp5 = CreateReportedRoute()[0];
            MovingForwardField.SetValue(movement, false);

            // A stale window from an earlier movement phase must be discarded while
            // navigation intentionally holds input (loot/combat hold, first-step turn).
            LastNavigationProgressTimeField.SetValue(movement, DateTime.UtcNow.AddSeconds(-30));
            Assert.False(await UpdateNavigationProgressAsync(movement, wp5, 326f, 164f));
            Assert.Equal(DateTime.MinValue, GetNavigationProgressTime(movement));
            Assert.Equal(0, GetStuckCount(movement));
            Assert.False(IsRecoveryActive(movement));

            // When forward movement resumes the window restarts from that moment.
            MovingForwardField.SetValue(movement, true);
            Assert.False(await UpdateNavigationProgressAsync(movement, wp5, 326f, 164f));
            DateTime resumed = GetNavigationProgressTime(movement);
            Assert.NotEqual(DateTime.MinValue, resumed);
            Assert.True(resumed > DateTime.UtcNow.AddSeconds(-2));
            Assert.Equal(0, GetStuckCount(movement));

            // The restarted window still detects a stationary player after the timeout.
            LastNavigationProgressTimeField.SetValue(movement, DateTime.UtcNow.AddSeconds(-16));
            Assert.True(await UpdateNavigationProgressAsync(movement, wp5, 326f, 164f));
            Assert.Equal(1, GetStuckCount(movement));
        }

        [Fact]
        public void NavigationRecoveryRunsBeforeTheConnectionWatchdogAndDoesNotHideRealDisconnects()
        {
            var watchdog = new GameConnectionWatchdog();
            DateTime start = new(2026, 10, 28, 2, 58, 1, DateTimeKind.Utc);

            // 02:58:01 -> player stationary at (326,164) with W requested.
            Assert.False(watchdog.Observe(true, 326f, 164f, start));
            Assert.False(watchdog.Observe(true, 326f, 164f, start.AddSeconds(14.9)));

            // 02:58:16 -> the navigation recovery (which runs at 15s, before this 30s
            // watchdog) verifies real displacement; the watchdog restarts its interval.
            Assert.False(watchdog.Observe(true, 324f, 163f, start.AddSeconds(16)));
            Assert.False(watchdog.Observe(true, 324f, 163f, start.AddSeconds(45.9)));

            // A genuine 30-second unresponsive interval still stops the bot.
            Assert.True(watchdog.Observe(true, 324f, 163f, start.AddSeconds(46)));
        }

        [Fact]
        public async Task ManualStopDuringReverseDiagonalReleasesInputsAndPreventsAnotherWDown()
        {
            var lines = new List<string>();
            var simulation = new SimulatedGameMemory { InCity = false };
            var target = new Waypoint(265, 196, MovementPrecision.Accurate, BotMode.OnlyMove,
                zoneRestriction: ZoneRestriction.Both);
            var movement = new InputRecordingMovementSystem(
                simulation.CreateMemory(lines.Add), lines.Add, target);

            await DispatchStuckAsync(movement, target);
            await TickRecoveryUpdateAsync(movement);
            Assert.Contains("W-down", movement.InputEvents);

            movement.Stop();
            int wDownsAtStop = movement.InputEvents.Count(input => input == "W-down");
            await TickRecoveryUpdateAsync(movement);

            Assert.False(IsRecoveryActive(movement));
            Assert.False(movement.IsNavigationFailureEscalationRequested);
            Assert.False((bool)(typeof(MovementSystem).GetField("_isUnstuckRoutineActive", PrivateInstance)!.GetValue(movement) ?? true));
            Assert.Equal(wDownsAtStop, movement.InputEvents.Count(input => input == "W-down"));
            Assert.Contains("W-up", movement.InputEvents);
            Assert.Contains("A-up", movement.InputEvents);
            Assert.Contains("D-up", movement.InputEvents);
            Assert.Contains("S-up", movement.InputEvents);
        }

        [Fact]
        public async Task CoordinatorStopsWaitsAndRestartsFromRepotAfterCityStuck()
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
                    () => { })
                {
                    // Keep the regression suite fast: the production default is 10 minutes.
                    InCityStuckResetDelayMs = 5
                };

                var repotStep = new BotFlowStep
                {
                    Type = BotFlowStepType.Repot,
                    RepotPaths = new List<BotRouteStep>
                    {
                        new BotRouteStep { PathFile = pathFile }
                    }
                };
                profile.FlowSteps = new List<BotFlowStep> { repotStep };

                // City-stuck escalation (2 unstuck attempts in the city) must not burn more
                // route retries: the workflow stops, waits, resets and restarts at Repot.
                Assert.False(await RunRepotStepWithInjectedCityStuckAsync(coordinator, pathRunner, repotStep));
                Assert.Equal(PathRunStopReason.CityStuck, pathRunner.LastStopReason);
                Assert.Equal(0, GetMoveToRepotRetryCount(coordinator));
                Assert.NotEqual(BotPhase.Failed, coordinator.CurrentPhase);
                Assert.Contains(lines, line => line.Contains("in-city unstuck limit reached"));
                Assert.Contains(lines, line => line.Contains("Going to the Repot step and starting again"));
                Assert.Contains(lines, line => line.Contains("Redirecting workflow to Repot step 1/1"));
                Assert.DoesNotContain(lines, line => line.Contains("Could not reach the repot point"));
            }
            finally
            {
                try { File.Delete(fullPath); } catch { }
            }
        }

        [Fact]
        public async Task RepotPathThatCannotBeLoadedStillFailsAfterBoundedRetries()
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
            var profile = new BotProfile { Name = "missing-repot-path" };
            var operationContext = new OperationContext(memory, pathRunner, itemSeller, profile, lines.Add, () => { });
            var operationRunner = new OperationRunnerService(operationContext, lines.Add);
            var coordinator = new BotWorkflowCoordinator(
                memory, repotSystem, repotDetector, pathLoader, pathRunner, operationRunner,
                profile, lines.Add, () => { })
            {
                InCityStuckResetDelayMs = 5
            };

            var repotStep = new BotFlowStep
            {
                Type = BotFlowStepType.Repot,
                RepotPaths = new List<BotRouteStep>
                {
                    new BotRouteStep { PathFile = $"MissingRepotPath_{Guid.NewGuid():N}.json" }
                }
            };
            profile.FlowSteps = new List<BotFlowStep> { repotStep };

            for (int attempt = 1; attempt <= BotConstants.Repot.MaxMoveToRepotRetries; attempt++)
            {
                var task = (Task<bool>?)ExecuteRepotStepMethod.Invoke(
                    coordinator,
                    new object[] { repotStep, CancellationToken.None });
                Assert.NotNull(task);
                Assert.False(await task!);
            }

            Assert.Equal(0, GetMoveToRepotRetryCount(coordinator));
            Assert.Equal(BotPhase.Failed, coordinator.CurrentPhase);
            Assert.Contains(lines, line =>
                line.Contains($"Could not reach the repot point after {BotConstants.Repot.MaxMoveToRepotRetries} tries"));
        }

        private static readonly MethodInfo ExecuteRepotStepMethod = typeof(BotWorkflowCoordinator).GetMethod(
            "ExecuteRepotStepAsync",
            PrivateInstance) ?? throw new InvalidOperationException("ExecuteRepotStepAsync not found.");

        private static readonly FieldInfo MoveToRepotRetryField = typeof(BotWorkflowCoordinator).GetField(
            "_moveToRepotRetryCount",
            PrivateInstance) ?? throw new InvalidOperationException("_moveToRepotRetryCount not found.");

        private static int GetMoveToRepotRetryCount(BotWorkflowCoordinator coordinator)
            => (int)(MoveToRepotRetryField.GetValue(coordinator) ?? 0);

        [Fact]
        public async Task NavigationFailureInsideTheCityWaitsResetsAndRestartsFromRepot()
        {
            string saveDirectory = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "SavedPaths"));
            Directory.CreateDirectory(saveDirectory);
            string pathFile = $"NavigationFailureRepot_{Guid.NewGuid():N}.json";
            string fullPath = Path.Combine(saveDirectory, pathFile);
            File.WriteAllText(fullPath,
                "{\"Name\":\"navigation-failure-repot\",\"Points\":[{\"X\":265,\"Y\":196,\"Precision\":2,\"Mode\":0}]}" );

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
                var profile = new BotProfile { Name = "navigation-failure-recovery" };
                var operationContext = new OperationContext(memory, pathRunner, itemSeller, profile, lines.Add, () => { });
                var operationRunner = new OperationRunnerService(operationContext, lines.Add);
                var coordinator = new BotWorkflowCoordinator(
                    memory, repotSystem, repotDetector, pathLoader, pathRunner, operationRunner,
                    profile, lines.Add, () => { })
                {
                    InCityStuckResetDelayMs = 5
                };
                var repotStep = new BotFlowStep
                {
                    Type = BotFlowStepType.Repot,
                    RepotPaths = new List<BotRouteStep> { new() { PathFile = pathFile } }
                };
                profile.FlowSteps = new List<BotFlowStep> { repotStep };

                // A navigation failure inside the city is the in-city unstuck limit: no more
                // back-to-back repot retries — stop, wait 10 min, reset and start again.
                Assert.False(await RunRepotStepWithInjectedNavigationFailureAsync(coordinator, pathRunner, repotStep));
                Assert.Equal(PathRunStopReason.NavigationFailure, pathRunner.LastStopReason);
                Assert.Equal(0, GetMoveToRepotRetryCount(coordinator));
                Assert.NotEqual(BotPhase.Failed, coordinator.CurrentPhase);
                Assert.Contains(lines, line => line.Contains("in-city unstuck limit reached"));
                Assert.Contains(lines, line => line.Contains("Going to the Repot step and starting again"));
                Assert.DoesNotContain(lines, line => line.Contains("Could not reach the repot point"));
            }
            finally
            {
                try { File.Delete(fullPath); } catch { }
            }
        }

        /// <summary>
        /// Runs the real coordinator Repot step (real PathRunner + real MovementSystem) and
        /// injects the workflow city-stuck fatal flag into the newly created movement system.
        /// This covers the boundary: MovementSystem fatal -> PathRunner.LastStopReason.CityStuck
        /// -> coordinator stop/wait/reset/restart-from-Repot.
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

        private static async Task<bool> RunRepotStepWithInjectedNavigationFailureAsync(
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

            NavigationFailureEscalationField.SetValue(pathRunner.CurrentMovement!, true);
            return await task!;
        }

        /// <summary>
        /// Test-only MovementSystem that suppresses the real full-screen stuck screenshot.
        /// Everything else runs through the production implementation unchanged.
        /// </summary>
        private sealed class InputRecordingMovementSystem : MovementSystem
        {
            public List<string> InputEvents { get; } = new();

            public InputRecordingMovementSystem(
                GameMemoryService memory,
                Action<string> log,
                Waypoint target)
                : base(
                    memory,
                    log,
                    target.X,
                    target.Y,
                    customPath: new List<Waypoint> { target },
                    loopPath: false,
                    enableWaypointSpecialRecoveries: true)
            {
                InternalRepotEnabled = false;
            }

            protected override void SendSyntheticForwardKeyEvent(bool keyUp)
                => InputEvents.Add(keyUp ? "W-up" : "W-down");

            protected override void SendSyntheticBackwardKeyEvent(bool keyUp)
                => InputEvents.Add(keyUp ? "S-up" : "S-down");

            protected override void SendTurnKeyEvent(bool leftA, bool keyUp)
                => InputEvents.Add($"{(leftA ? "A" : "D")}-{(keyUp ? "up" : "down")}");

            protected override void CaptureStuckScreenshot()
            {
                // Regression tests must not capture the desktop.
            }
        }

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
        /// Multi-waypoint movement used by the navigation-progress regression tests.
        /// Synthetic key events are suppressed so tests never touch the real keyboard.
        /// </summary>
        private sealed class SyntheticInputPathMovementSystem : MovementSystem
        {
            public SyntheticInputPathMovementSystem(
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
                    enableWaypointSpecialRecoveries: true)
            {
                InternalRepotEnabled = false;
            }

            protected override void SendSyntheticForwardKeyEvent(bool keyUp) { }

            protected override void SendSyntheticBackwardKeyEvent(bool keyUp) { }

            protected override void SendTurnKeyEvent(bool leftA, bool keyUp) { }

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
