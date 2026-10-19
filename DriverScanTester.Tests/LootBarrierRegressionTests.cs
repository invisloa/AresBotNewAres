using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Services;
using static DriverScanTester.Services.LootSystem;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Regression coverage for the mandatory MoveAndAttackAndLoot navigation barrier:
    /// ordinary waypoint movement may be released only by a camera-verified Completed
    /// request for the exact armed generation. Capture failures, cancellations,
    /// deferred combat and stopped loot never count as an empty scan.
    /// </summary>
    public sealed class LootBarrierRegressionTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

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

        private static LootSystem CreateLoot(Action<string> log)
            => new LootSystem(CreateStubMemory(log), log);

        private static MovementSystem CreateMovement(
            Action<string> log,
            BotMode mode = BotMode.MoveAndAttackAndLoot,
            LootSystem? loot = null)
        {
            var path = new List<Waypoint>
            {
                new Waypoint(5000, 5000, MovementPrecision.Medium, mode)
            };
            var movement = new MovementSystem(
                CreateStubMemory(log),
                log,
                5000,
                5000,
                customPath: path,
                loopPath: true)
            {
                InternalRepotEnabled = false,
                LootSystemRef = loot
            };
            return movement;
        }

        private static LootRequestStatus Snapshot(
            long requestId,
            LootRequestState state,
            bool cameraVerified = true,
            int itemsCollected = 0,
            string detail = "test")
            => new(requestId, LootBarrierReason.PostKill, state, cameraVerified,
                itemsCollected, detail, DateTime.UtcNow);

        private static int QueueCount(MovementSystem movement)
        {
            var field = typeof(MovementSystem).GetField("_waypoints", PrivateInstance);
            Assert.NotNull(field);
            var queue = field!.GetValue(movement) as Queue<Waypoint>;
            Assert.NotNull(queue);
            return queue!.Count;
        }

        private static bool IsMovingForward(MovementSystem movement)
        {
            var field = typeof(MovementSystem).GetField("_isMovingForward", PrivateInstance);
            Assert.NotNull(field);
            return (bool)field!.GetValue(movement)!;
        }

        private static void InvokePrivate(MovementSystem movement, string method, params object[] args)
        {
            var info = typeof(MovementSystem).GetMethod(method, PrivateInstance);
            Assert.NotNull(info);
            info!.Invoke(movement, args);
        }

        private static async Task<bool> InvokeAdvanceAsync(
            MovementSystem movement,
            float x,
            float y,
            CancellationToken token = default)
        {
            var info = typeof(MovementSystem).GetMethod("AdvanceReachedWaypoints", PrivateInstance);
            Assert.NotNull(info);
            var task = (Task<bool>?)info!.Invoke(movement, new object[] { x, y, token });
            Assert.NotNull(task);
            return await task!;
        }

        private static bool InvokeEnsureWaypointBarrier(
            MovementSystem movement,
            Waypoint target,
            float x,
            float y)
        {
            var info = typeof(MovementSystem).GetMethod(
                "EnsureLootBarrierBeforeWaypointAdvance", PrivateInstance);
            Assert.NotNull(info);
            return (bool)info!.Invoke(movement, new object[] { target, x, y })!;
        }

        // ── Test 1 — movement after kill ────────────────────────────────────────

        [Fact]
        public void PostKillPendingRunningAndDeferred_AllBlockNavigation()
        {
            var barrier = new LootNavigationBarrier();
            barrier.Arm(42, LootBarrierReason.PostKill);

            Assert.True(barrier.IsBlockingNavigation);

            foreach (LootRequestState state in new[]
                     {
                         LootRequestState.Pending,
                         LootRequestState.Running,
                         LootRequestState.Deferred
                     })
            {
                barrier.ApplyStatus(Snapshot(42, state), 100f, 100f);
                Assert.True(barrier.IsBlockingNavigation);
                Assert.True(barrier.IsActive);
            }

            // Only an explicitly camera-verified Completed status releases navigation.
            barrier.ApplyStatus(Snapshot(42, LootRequestState.Completed, cameraVerified: true), 100f, 100f);
            Assert.False(barrier.IsBlockingNavigation);
            Assert.True(barrier.CanReuseCompletedScan(100f, 100f, out long completedId));
            Assert.Equal(42, completedId);
        }

        [Fact]
        public void StartMoving_IsSuppressedWhileMandatoryLootBarrierIsArmed()
        {
            var lines = new List<string>();
            using var loot = CreateLoot(lines.Add);
            var movement = CreateMovement(lines.Add, loot: loot);

            InvokePrivate(movement, "ArmMandatoryLootBarrier", LootBarrierReason.PostKill);
            Assert.True(movement.IsLootNavigationBlocked);

            InvokePrivate(movement, "StartMoving");

            Assert.False(IsMovingForward(movement));
            Assert.Contains(lines, line => line.Contains("StartMoving suppressed", StringComparison.Ordinal));
        }

        // ── Test 2 — waypoint transition ───────────────────────────────────────

        [Fact]
        public async Task ReachingLootWaypoint_DoesNotAdvanceUntilBarrierCompletes()
        {
            var lines = new List<string>();
            using var loot = CreateLoot(lines.Add);
            var movement = CreateMovement(lines.Add, loot: loot);
            var target = new Waypoint(5000, 5000, MovementPrecision.Medium, BotMode.MoveAndAttackAndLoot);

            // Arrival check arms exactly one WaypointTransition obligation and the queue
            // entry stays in place.
            bool advanced = await InvokeAdvanceAsync(movement, 5000, 5000);
            Assert.False(advanced);
            Assert.Equal(1, QueueCount(movement));
            Assert.True(movement.IsLootNavigationBlocked);
            Assert.Equal(LootBarrierReason.WaypointTransition, movement.LootBarrier.Reason);

            // Another tick must not create a new obligation.
            long requestId = movement.LootBarrier.RequestId;
            bool advancedAgain = await InvokeAdvanceAsync(movement, 5000, 5000);
            Assert.False(advancedAgain);
            Assert.Equal(requestId, movement.LootBarrier.RequestId);
            Assert.Equal(1, QueueCount(movement));

            // A verified completion for the exact generation releases the transition.
            movement.LootBarrier.ApplyStatus(
                new LootRequestStatus(
                    requestId, LootBarrierReason.WaypointTransition,
                    LootRequestState.Completed, CameraVerified: true,
                    ItemsCollected: 0, Detail: "verified empty pass", DateTime.UtcNow),
                5000f, 5000f);

            bool advancedAfterCompletion = await InvokeAdvanceAsync(movement, 5000, 5000);
            Assert.True(advancedAfterCompletion);
            Assert.Equal(0, QueueCount(movement));
            Assert.False(movement.IsLootNavigationBlocked);
        }

        [Fact]
        public void ValidPostKillScanCanSatisfyWaypointBoundaryOnlyWithoutMovement()
        {
            var lines = new List<string>();
            using var loot = CreateLoot(lines.Add);
            var movement = CreateMovement(lines.Add, loot: loot);
            var target = new Waypoint(5000, 5000, MovementPrecision.Medium, BotMode.MoveAndAttackAndLoot);

            InvokePrivate(movement, "ArmMandatoryLootBarrier", LootBarrierReason.PostKill);
            long id = movement.LootBarrier.RequestId;
            movement.LootBarrier.ApplyStatus(
                Snapshot(id, LootRequestState.Completed, cameraVerified: true), 5000f, 5000f);

            Assert.True(InvokeEnsureWaypointBarrier(movement, target, 5000f, 5000f));

            // Real navigation invalidates the completed scan; the boundary must check again.
            movement.LootBarrier.NotifyNavigationStarted();
            Assert.False(InvokeEnsureWaypointBarrier(movement, target, 5000f, 5000f));
            Assert.True(movement.IsLootNavigationBlocked);
        }

        // ── Test 3 — camera verification ───────────────────────────────────────

        [Fact]
        public void LootCaptureRequiresExactDistanceLockSettlementAndOwnership()
        {
            Assert.Equal((short)17000, BotConstants.Camera.LootScanDistance);
            Assert.Equal(BotConstants.Camera.LootScanDistance, LootSystem.NormalLootScanDistance);

            short distance = LootSystem.NormalLootScanDistance;
            short lockValue = BotConstants.Camera.DefaultVerticalLock;

            Assert.True(LootSystem.IsLootCaptureAuthorized(
                distance, distance, lockValue, lockValue,
                stabilizationElapsed: true, ownershipHeld: true));

            Assert.False(LootSystem.IsLootCaptureAuthorized(
                distance, (short)(distance - 1), lockValue, lockValue,
                stabilizationElapsed: true, ownershipHeld: true));
            Assert.False(LootSystem.IsLootCaptureAuthorized(
                distance, distance, lockValue, (short)(lockValue - 1),
                stabilizationElapsed: true, ownershipHeld: true));
            Assert.False(LootSystem.IsLootCaptureAuthorized(
                distance, distance, lockValue, lockValue,
                stabilizationElapsed: false, ownershipHeld: true));
            Assert.False(LootSystem.IsLootCaptureAuthorized(
                distance, distance, lockValue, lockValue,
                stabilizationElapsed: true, ownershipHeld: false));
        }

        // ── Test 4 — camera write conflict ─────────────────────────────────────

        [Fact]
        public void MovementCameraWriteCannotOverrideLootScanCameraOwnership()
        {
            using var loot = CreateLoot(_ => { });
            var ownedField = typeof(LootSystem).GetField("_lootScanCameraOwned", PrivateInstance);
            Assert.NotNull(ownedField);

            int writes = 0;
            ownedField!.SetValue(loot, true);
            Assert.False(loot.TryRunMovementCameraAction(() => writes++));
            Assert.Equal(0, writes);

            ownedField.SetValue(loot, false);
            Assert.True(loot.TryRunMovementCameraAction(() => writes++));
            Assert.Equal(1, writes);
        }

        // ── Test 5 — failed capture ─────────────────────────────────────────────

        [Fact]
        public void CaptureFailureIsNeverAValidEmptyScanOrSuccessfulBarrier()
        {
            LootScanPassResult combined = LootSystem.CombineNormalLootScanResults(
                LootScanPassResult.NoItem, LootScanPassResult.CaptureFailed, cameraVerified: true);
            Assert.Equal(LootScanPassResult.CaptureFailed, combined);
            Assert.NotEqual(LootScanPassResult.NoItem, combined);

            var barrier = new LootNavigationBarrier();
            barrier.Arm(5, LootBarrierReason.PostKill);
            barrier.ApplyStatus(Snapshot(5, LootRequestState.Failed, cameraVerified: false), 10f, 10f);
            Assert.True(barrier.IsBlockingNavigation);
            Assert.False(barrier.CanReuseCompletedScan(10f, 10f, out _));
        }

        // ── Test 6 — scan cancellation ─────────────────────────────────────────

        [Fact]
        public void CancelledLootRequestNeverReleasesNavigation()
        {
            var lines = new List<string>();
            using var loot = CreateLoot(lines.Add);
            long id = loot.BeginMandatoryLootRequest(LootBarrierReason.WaypointTransition);
            loot.Stop();

            Assert.True(loot.TryGetMandatoryLootRequestStatus(id, out LootRequestStatus status));
            Assert.Equal(LootRequestState.Cancelled, status.State);

            var barrier = new LootNavigationBarrier();
            barrier.Arm(id, LootBarrierReason.WaypointTransition);
            barrier.ApplyStatus(status, 25f, 25f);

            Assert.True(barrier.IsBlockingNavigation);
            Assert.True(barrier.HasTerminalFailure);
            Assert.False(barrier.CanReuseCompletedScan(25f, 25f, out _));
        }

        // ── Test 7 — item collected ────────────────────────────────────────────

        [Fact]
        public void CollectedItemKeepsTheMandatoryObligationOpen()
        {
            Assert.Equal(
                LootScanPassResult.Collected,
                LootSystem.CombineNormalLootScanResults(
                    LootScanPassResult.Collected, null, cameraVerified: true));

            var barrier = new LootNavigationBarrier();
            barrier.Arm(7, LootBarrierReason.PostKill);
            barrier.ApplyStatus(
                Snapshot(7, LootRequestState.Running, cameraVerified: false, itemsCollected: 1),
                0f, 0f);

            Assert.True(barrier.IsBlockingNavigation);
            Assert.False(barrier.CanReuseCompletedScan(0f, 0f, out _));
        }

        // ── Test 8 — consecutive combat ────────────────────────────────────────

        [Fact]
        public void ConsecutiveKillsPreserveTheObligationAndKeepTheSameGeneration()
        {
            using var loot = CreateLoot(_ => { });
            long first = loot.BeginMandatoryLootRequest(LootBarrierReason.PostKill);
            long second = loot.BeginMandatoryLootRequest(LootBarrierReason.PostKill);

            Assert.Equal(first, second);
            Assert.True(loot.TryGetMandatoryLootRequestStatus(first, out LootRequestStatus status));
            Assert.Equal(LootRequestState.Pending, status.State);

            var barrier = new LootNavigationBarrier();
            barrier.Arm(first, LootBarrierReason.PostKill);
            barrier.ApplyStatus(Snapshot(first, LootRequestState.Deferred, cameraVerified: false), 0f, 0f);
            Assert.True(barrier.IsBlockingNavigation);

            barrier.ApplyStatus(Snapshot(first, LootRequestState.Completed, cameraVerified: true), 0f, 0f);
            Assert.False(barrier.IsBlockingNavigation);

            // Resolve the LootSystem generation explicitly; a resolved obligation must
            // produce a new request id for a later kill.
            loot.CancelMandatoryLootRequest(first, "test generation resolved");
            long third = loot.BeginMandatoryLootRequest(LootBarrierReason.PostKill);
            Assert.True(third > second, "A resolved obligation must produce a new generation.");
        }

        // ── Test 9 — route resync ──────────────────────────────────────────────

        [Fact]
        public void RouteResyncCannotRunWhileLootBarrierBlocksNavigation()
        {
            Assert.False(MovementSystem.CanRunRouteResyncAfterCombat(pending: true, lootBarrierBlocked: true));
            Assert.True(MovementSystem.CanRunRouteResyncAfterCombat(pending: false, lootBarrierBlocked: false));
            Assert.True(MovementSystem.CanRunRouteResyncAfterCombat(pending: true, lootBarrierBlocked: false));
        }

        // ── Test 10 — mode compatibility ───────────────────────────────────────

        [Fact]
        public void OnlyMoveAndMoveAndAttackDoNotUseTheMandatoryBarrier()
        {
            Assert.False(MovementSystem.RequiresMandatoryLootBarrier(BotMode.OnlyMove));
            Assert.False(MovementSystem.RequiresMandatoryLootBarrier(BotMode.MoveAndAttack));
            Assert.True(MovementSystem.RequiresMandatoryLootBarrier(BotMode.MoveAndAttackAndLoot));

            // SOD/SOP-only mode keeps its existing flag; the mandatory white-loot
            // barrier is never armed for it by the MovementSystem.
            using var loot = CreateLoot(_ => { });
            loot.PinkLootOnlyMode = true;
            Assert.True(loot.PinkLootOnlyMode);
        }

        // ── Test 11 — repeated ticks ───────────────────────────────────────────

        [Fact]
        public void RepeatedArmingDoesNotCreateDuplicateRequestsOrResetRunningState()
        {
            using var loot = CreateLoot(_ => { });
            long first = loot.BeginMandatoryLootRequest(LootBarrierReason.WaypointTransition);
            long second = loot.BeginMandatoryLootRequest(LootBarrierReason.WaypointTransition);
            Assert.Equal(first, second);

            var barrier = new LootNavigationBarrier();
            barrier.Arm(first, LootBarrierReason.WaypointTransition);
            barrier.ApplyStatus(Snapshot(first, LootRequestState.Running, cameraVerified: false), 0f, 0f);
            barrier.Arm(first, LootBarrierReason.WaypointTransition);

            Assert.Equal(LootBarrierPhase.Running, barrier.Phase);
            Assert.Equal(first, barrier.RequestId);
        }

        // ── Test 12 — failure and shutdown ─────────────────────────────────────

        [Fact]
        public void FocusLossDefersButNeverCompletesAStoppedLootTask()
        {
            using var loot = CreateLoot(_ => { });
            long id = loot.BeginMandatoryLootRequest(LootBarrierReason.PostKill);

            var cancelAll = typeof(LootSystem).GetMethod("CancelAllLootWork", PrivateInstance);
            Assert.NotNull(cancelAll);
            cancelAll!.Invoke(loot, new object[] { "game window is not foreground", true });

            Assert.True(loot.TryGetMandatoryLootRequestStatus(id, out LootRequestStatus deferred));
            Assert.Equal(LootRequestState.Deferred, deferred.State);
            Assert.False(deferred.CameraVerified);

            var barrier = new LootNavigationBarrier();
            barrier.Arm(id, LootBarrierReason.PostKill);
            barrier.ApplyStatus(deferred, 0f, 0f);
            Assert.True(barrier.IsBlockingNavigation);

            // A stopped loot task is a cancellation, never an empty result.
            loot.Stop();
            Assert.True(loot.TryGetMandatoryLootRequestStatus(id, out LootRequestStatus cancelled));
            Assert.Equal(LootRequestState.Cancelled, cancelled.State);
            barrier.ApplyStatus(cancelled, 0f, 0f);
            Assert.True(barrier.IsBlockingNavigation);
        }

        // ── Scan-region completeness / result preservation ─────────────────────

        [Theory]
        [InlineData((int)LootScanPassResult.NoItem, (int)LootScanPassResult.NoItem, (int)LootScanPassResult.NoItem)]
        [InlineData((int)LootScanPassResult.NoItem, (int)LootScanPassResult.CaptureFailed, (int)LootScanPassResult.CaptureFailed)]
        [InlineData((int)LootScanPassResult.NoItem, (int)LootScanPassResult.CameraVerificationFailed, (int)LootScanPassResult.CameraVerificationFailed)]
        [InlineData((int)LootScanPassResult.NoItem, (int)LootScanPassResult.InvalidGeometry, (int)LootScanPassResult.InvalidGeometry)]
        [InlineData((int)LootScanPassResult.NoItem, (int)LootScanPassResult.Incomplete, (int)LootScanPassResult.Incomplete)]
        [InlineData((int)LootScanPassResult.CaptureFailed, null, (int)LootScanPassResult.CaptureFailed)]
        [InlineData((int)LootScanPassResult.Cancelled, null, (int)LootScanPassResult.Cancelled)]
        public void NormalLootPassRequiresBothValidNoItemRegions(
            int small,
            int? big,
            int expected)
        {
            Assert.Equal(
                (LootScanPassResult)expected,
                LootSystem.CombineNormalLootScanResults(
                    (LootScanPassResult)small,
                    big.HasValue ? (LootScanPassResult)big.Value : null,
                    cameraVerified: true));
        }

        [Fact]
        public void EmptySmallScanAloneIsNotACompletePass()
        {
            Assert.Equal(
                LootScanPassResult.Incomplete,
                LootSystem.CombineNormalLootScanResults(
                    LootScanPassResult.NoItem, null, cameraVerified: true));
        }

        [Fact]
        public void CompletionWithoutCameraVerificationFailsTheBarrier()
        {
            var barrier = new LootNavigationBarrier();
            barrier.Arm(99, LootBarrierReason.PostKill);
            barrier.ApplyStatus(Snapshot(99, LootRequestState.Completed, cameraVerified: false), 0f, 0f);

            Assert.True(barrier.IsBlockingNavigation);
            Assert.True(barrier.HasTerminalFailure);
            Assert.Contains("without verified loot camera", barrier.Detail, StringComparison.Ordinal);
        }

        [Fact]
        public void StaleCompletionForAnotherGenerationCannotReleaseTheArmedRequest()
        {
            var barrier = new LootNavigationBarrier();
            barrier.Arm(100, LootBarrierReason.PostKill);
            barrier.ApplyStatus(Snapshot(101, LootRequestState.Completed, cameraVerified: true), 0f, 0f);

            Assert.True(barrier.IsBlockingNavigation);
            Assert.False(barrier.CanReuseCompletedScan(0f, 0f, out _));
        }
    }
}
