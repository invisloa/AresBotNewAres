using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Services;
using static DriverScanTester.Services.LootSystem;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Regression coverage for the corrected MoveAndAttackAndLoot behavior:
    /// ordinary waypoint movement never arms a stationary loot scan; the passive
    /// moving pre-scan only observes the current view; a confirmed post-kill request
    /// is the only mandatory full loot cycle.
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
            LootSystem? loot = null,
            int waypointCount = 1,
            bool loopPath = false)
        {
            var path = new List<Waypoint>();
            for (int i = 0; i < waypointCount; i++)
                path.Add(new Waypoint(1000 * (i + 1), 1000 * (i + 1), MovementPrecision.Medium, mode));

            var movement = new MovementSystem(
                CreateStubMemory(log),
                log,
                1000,
                1000,
                customPath: path,
                loopPath: loopPath)
            {
                InternalRepotEnabled = false,
                LootSystemRef = loot
            };
            return movement;
        }

        private static LootRequestStatus Snapshot(
            long requestId,
            LootBarrierReason reason,
            LootRequestState state,
            bool cameraVerified = true,
            int itemsCollected = 0,
            string detail = "test")
            => new(requestId, reason, state, cameraVerified,
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

        private static void SetMovingForward(MovementSystem movement, bool value)
        {
            var field = typeof(MovementSystem).GetField("_isMovingForward", PrivateInstance);
            Assert.NotNull(field);
            field!.SetValue(movement, value);
        }

        private static bool BarrierHoldsInput(MovementSystem movement)
        {
            var field = typeof(MovementSystem).GetField("_lootNavigationBarrierHoldsInput", PrivateInstance);
            Assert.NotNull(field);
            return (bool)field!.GetValue(movement)!;
        }

        private static void InvokePrivate(object target, string method, params object[] args)
        {
            var info = target.GetType().GetMethod(method, PrivateInstance);
            Assert.NotNull(info);
            info!.Invoke(target, args);
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

        private static LootScanPassOutcome ValidEmptyOutcome()
            => new(LootScanPassResult.NoItem, LootScanPassResult.NoItem,
                LootScanPassResult.NoItem, CameraVerified: true, "test empty pass");

        private static bool IsLootStateIdle(LootSystem loot)
        {
            var field = typeof(LootSystem).GetField("_lootState", PrivateInstance);
            Assert.NotNull(field);
            return field!.GetValue(loot)!.ToString() == "Idle";
        }

        private static void RunMandatoryService(LootSystem loot, bool mobSelected = false)
            => InvokePrivate(loot, "ServiceMandatoryLootRequest", mobSelected);

        private static void CompleteMandatory(
            LootSystem loot, long requestId, LootScanPassOutcome outcome)
            => InvokePrivate(loot, "CompleteMandatoryLootRequest", requestId, outcome);

        // ── Test 1 — continuous walking without loot ───────────────────────────

        [Fact]
        public async Task MultipleLootWaypointsAdvanceWithoutAnyLootScanOrStop()
        {
            var lines = new List<string>();
            using var loot = CreateLoot(lines.Add);
            var movement = CreateMovement(lines.Add, loot: loot, waypointCount: 3);
            SetMovingForward(movement, true);

            int index = 0;
            foreach (int coord in new[] { 1000, 2000, 3000 })
            {
                bool advanced = await InvokeAdvanceAsync(movement, coord, coord);
                Assert.True(advanced, $"Waypoint {coord} should advance normally.");
                Assert.False(movement.IsLootNavigationBlocked);
                // The final waypoint legitimately enters standby (existing behavior),
                // but no intermediate transition may stop navigation for loot.
                if (index++ < 2)
                    Assert.True(IsMovingForward(movement), "Ordinary advancement must not release W.");
            }

            Assert.Equal(0, QueueCount(movement));
            Assert.DoesNotContain(lines, line => line.Contains("LootBarrier] Armed", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains("LootVerify]", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains("LootCamera]", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains("StartMoving", StringComparison.Ordinal));
        }

        // ── Test 2 — long journey without loot ─────────────────────────────────

        [Fact]
        public void RepeatedTicksInLootModeNeverStartACycleWithoutARequest()
        {
            var lines = new List<string>();
            using var loot = CreateLoot(lines.Add);
            loot.MoveAndAttackAndLootMode = true;

            for (int i = 0; i < 100; i++)
            {
                RunMandatoryService(loot);
                Assert.False(loot.IsLootCycleActive,
                    "Idle MoveAndAttackAndLoot must not start the legacy timer-driven collector.");
                Assert.False(loot.HasActiveMandatoryLootRequest);
            }

            Assert.DoesNotContain(lines, line => line.Contains("Spacebar", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains("PixelScan started", StringComparison.Ordinal));
        }

        // ── Test 3 — passive candidate scan ────────────────────────────────────

        [Fact]
        public void SpacebarLootIsDisabledByDefault()
        {
            Assert.False(BotConstants.Loot.SpacebarLootEnabledDefault);
        }

        [Fact]
        public void DisabledSpacebarLootNeverStartsTheScanSpamTask()
        {
            bool original = LootRuntimeSettings.SpacebarLootEnabled;
            try
            {
                LootRuntimeSettings.SpacebarLootEnabled = false;
                using var loot = CreateLoot(_ => { });

                InvokePrivate(loot, "StartScanSpacebarSpam");

                var field = typeof(LootSystem).GetField("_scanSpacebarCts", PrivateInstance);
                Assert.NotNull(field);
                Assert.Null(field!.GetValue(loot));
            }
            finally
            {
                LootRuntimeSettings.SpacebarLootEnabled = original;
            }
        }

        [Fact]
        public void PassiveAnalysisFindsPlausibleWhiteLootWithoutMovingAnything()
        {
            using var bitmap = new Bitmap(200, 200);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
                graphics.FillRectangle(Brushes.White, 50, 50, 14, 14);
            }

            LootPreScanResult result = LootSystem.AnalyzeWhiteLootCandidates(
                bitmap, new Rectangle(0, 0, 200, 200),
                BotConstants.Loot.PassivePreScanMaxCandidateComponents);

            Assert.Equal(LootPreScanOutcome.PotentialCandidate, result.Outcome);
            Assert.True(result.CandidateComponents >= 1);
            Assert.True(result.WhitePixels > 0);
            Assert.False(result.CandidateBounds.IsEmpty);
            Assert.Equal((short)17000, BotConstants.Camera.LootScanDistance);
            Assert.Equal(BotConstants.Camera.LootScanDistance, LootSystem.NormalLootScanDistance);
        }

        [Fact]
        public void TravelCandidateRequestStopsNavigationAndStartsVerificationOnce()
        {
            var lines = new List<string>();
            using var loot = CreateLoot(lines.Add);
            var movement = CreateMovement(lines.Add, loot: loot);
            SetMovingForward(movement, true);

            long requestId = loot.BeginMandatoryLootRequest(LootBarrierReason.TravelCandidateVerification);
            Assert.True(requestId > 0);

            InvokePrivate(movement, "RefreshLootBarrier");
            Assert.True(movement.IsLootNavigationBlocked);
            Assert.Equal(LootBarrierReason.TravelCandidateVerification, movement.LootBarrier.Reason);
            Assert.True(BarrierHoldsInput(movement));

            InvokePrivate(movement, "StartMoving");
            Assert.False(IsMovingForward(movement));

            loot.NotifyNavigationHeld(requestId);
            RunMandatoryService(loot);
            Assert.True(loot.IsLootCycleActive);
            Assert.True(loot.TryGetMandatoryLootRequestStatus(requestId, out LootRequestStatus status));
            Assert.Equal(LootRequestState.Running, status.State);
        }

        // ── Test 4 — false-positive candidate ──────────────────────────────────

        [Fact]
        public void FailedCandidateSuppressionIsRegionBoundedAndExpires()
        {
            var tracker = new LootSystem.CandidateSuppressionTracker();
            var bounds = new Rectangle(100, 100, 20, 20);
            DateTime now = DateTime.UtcNow;

            Assert.False(tracker.IsSuppressed(bounds, now));
            (int firstCount, int firstMs) = tracker.RecordFailure(bounds, now);
            Assert.Equal(1, firstCount);
            Assert.True(tracker.IsSuppressed(bounds, now.AddMilliseconds(10)));

            // Same artifact, same region: bounded backoff grows.
            (int secondCount, int secondMs) = tracker.RecordFailure(bounds, now.AddMilliseconds(10));
            Assert.Equal(2, secondCount);
            Assert.True(secondMs > firstMs);

            // Meaningful visual change (region moved) is new evidence.
            var moved = new Rectangle(180, 100, 20, 20);
            Assert.False(tracker.IsSuppressed(moved, now.AddMilliseconds(20)));

            // Expiry is bounded too: suppression never blacklists the location.
            tracker.RecordFailure(bounds, now);
            Assert.False(tracker.IsSuppressed(
                bounds, now.AddMilliseconds(BotConstants.Loot.PassivePreScanSuppressionMaxMs + 1)));
        }

        [Fact]
        public void UnavailableTravelVerificationReleasesNavigationWithoutClaimingEmpty()
        {
            var lines = new List<string>();
            using var loot = CreateLoot(lines.Add);
            var movement = CreateMovement(lines.Add, loot: loot);

            long requestId = loot.BeginMandatoryLootRequest(LootBarrierReason.TravelCandidateVerification);
            loot.NotifyNavigationHeld(requestId);
            InvokePrivate(movement, "RefreshLootBarrier");
            RunMandatoryService(loot);
            Assert.True(movement.IsLootNavigationBlocked);

            InvokePrivate(loot, "MarkTravelVerificationUnavailable", requestId, "capture failed");
            Assert.True(loot.TryGetMandatoryLootRequestStatus(requestId, out LootRequestStatus status));
            Assert.Equal(LootRequestState.Unavailable, status.State);
            Assert.False(status.CameraVerified);

            InvokePrivate(movement, "RefreshLootBarrier");
            Assert.False(movement.IsLootNavigationBlocked);
            Assert.False(BarrierHoldsInput(movement));
            Assert.Contains(lines, line => line.Contains("not treated as an empty result", StringComparison.Ordinal));
        }

        // ── Test 5 — confirmed item ─────────────────────────────────────────────

        [Fact]
        public void ConfirmedItemCompletesTheVerificationAndOnlyThenReleasesNavigation()
        {
            var lines = new List<string>();
            using var loot = CreateLoot(lines.Add);
            var movement = CreateMovement(lines.Add, loot: loot);

            long requestId = loot.BeginMandatoryLootRequest(LootBarrierReason.TravelCandidateVerification);
            loot.NotifyNavigationHeld(requestId);
            InvokePrivate(movement, "RefreshLootBarrier");
            RunMandatoryService(loot);

            InvokePrivate(loot, "RecordMandatoryItemCollected", requestId);
            Assert.True(movement.IsLootNavigationBlocked);

            CompleteMandatory(loot, requestId, ValidEmptyOutcome());
            Assert.True(loot.TryGetMandatoryLootRequestStatus(requestId, out LootRequestStatus status));
            Assert.Equal(LootRequestState.Completed, status.State);
            Assert.True(status.CameraVerified);
            Assert.Equal(1, status.ItemsCollected);
            Assert.True(IsLootStateIdle(loot));

            InvokePrivate(movement, "RefreshLootBarrier");
            Assert.False(movement.IsLootNavigationBlocked);
            Assert.False(BarrierHoldsInput(movement));
            Assert.False(loot.IsLootCycleActive);
        }

        // ── Test 6 — last mob killed ────────────────────────────────────────────

        [Fact]
        public void PostKillSignalArmsMandatoryScanAndBlocksNavigationUntilVerified()
        {
            var lines = new List<string>();
            using var loot = CreateLoot(lines.Add);
            var movement = CreateMovement(lines.Add, loot: loot);
            SetMovingForward(movement, true);

            InvokePrivate(movement, "ArmMandatoryLootBarrier", LootBarrierReason.PostKill);
            Assert.True(movement.IsLootNavigationBlocked);
            Assert.True(BarrierHoldsInput(movement));
            Assert.Equal(LootBarrierReason.PostKill, movement.LootBarrier.Reason);
            long requestId = movement.LootBarrier.RequestId;
            Assert.True(requestId > 0);

            InvokePrivate(movement, "StartMoving");
            Assert.False(IsMovingForward(movement), "No waypoint movement may resume before the post-kill scan completes.");

            loot.NotifyNavigationHeld(requestId);
            RunMandatoryService(loot);
            Assert.True(loot.IsLootCycleActive);

            CompleteMandatory(loot, requestId, ValidEmptyOutcome());
            InvokePrivate(movement, "RefreshLootBarrier");
            Assert.False(movement.IsLootNavigationBlocked);
            Assert.False(BarrierHoldsInput(movement));
            Assert.Contains(lines, line => line.Contains("PostKill", StringComparison.Ordinal));
        }

        [Fact]
        public void PostKillUnavailableIsNeverAValidEmptyScan()
        {
            var barrier = new LootNavigationBarrier();
            barrier.Arm(77, LootBarrierReason.PostKill);

            barrier.ApplyStatus(Snapshot(
                77, LootBarrierReason.PostKill, LootRequestState.Unavailable,
                cameraVerified: false, detail: "capture failed"));

            Assert.True(barrier.IsBlockingNavigation);
            Assert.True(barrier.HasTerminalFailure);
            Assert.NotEqual(LootBarrierPhase.Unavailable, barrier.Phase);
            Assert.Contains("unavailable", barrier.Detail, StringComparison.Ordinal);
        }

        // ── Test 7 — waypoint reached ───────────────────────────────────────────

        [Fact]
        public async Task ReachingLootWaypointDoesNotArmAnyMandatoryScan()
        {
            var lines = new List<string>();
            using var loot = CreateLoot(lines.Add);
            var movement = CreateMovement(lines.Add, loot: loot);
            SetMovingForward(movement, true);

            bool advanced = await InvokeAdvanceAsync(movement, 1000, 1000);

            Assert.True(advanced);
            Assert.Equal(0, QueueCount(movement));
            Assert.False(movement.IsLootNavigationBlocked);
            Assert.False(BarrierHoldsInput(movement));
            Assert.False(loot.HasActiveMandatoryLootRequest);
            Assert.DoesNotContain(lines, line => line.Contains("LootBarrier] Armed", StringComparison.Ordinal));
        }

        // ── Test 8 — route resync ───────────────────────────────────────────────

        [Fact]
        public void RouteResyncWaitsForPostKillButIsNotAscanTrigger()
        {
            var barrier = new LootNavigationBarrier();
            barrier.Arm(88, LootBarrierReason.PostKill);

            Assert.False(MovementSystem.CanRunRouteResyncAfterCombat(
                pending: true, lootBarrierBlocked: barrier.IsBlockingNavigation));

            barrier.ApplyStatus(Snapshot(
                88, LootBarrierReason.PostKill, LootRequestState.Completed,
                cameraVerified: true));

            Assert.True(MovementSystem.CanRunRouteResyncAfterCombat(
                pending: true, lootBarrierBlocked: barrier.IsBlockingNavigation));

            // Resync bookkeeping by itself never creates a request; only the real kill
            // signal (TabAfterKill) or a pre-scan candidate can.
            using var loot = CreateLoot(_ => { });
            Assert.False(loot.HasActiveMandatoryLootRequest);
        }

        // ── Test 9 — camera separation ──────────────────────────────────────────

        [Fact]
        public void LootCaptureRequiresExactDistanceLockSettlementAndOwnership()
        {
            Assert.Equal((short)17000, BotConstants.Camera.LootScanDistance);
            Assert.Equal(BotConstants.Camera.LootScanDistance, LootSystem.NormalLootScanDistance);

            short distance = LootSystem.NormalLootScanDistance;
            short lockValue = BotConstants.Camera.StationaryVerticalLock;
            Assert.Equal((short)16310, lockValue);
            Assert.Equal((short)16160, BotConstants.Camera.MovementVerticalLock);

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

        [Fact]
        public void VerifiedLootScanUsesStationaryLockAndBlocksWCameraWritesUntilRelease()
        {
            const uint pid = 909;
            const ulong moduleBase = 0x710000;
            const ulong cameraBase = 0x3700000;
            var memoryBytes = new Dictionary<ulong, byte[]>
            {
                [moduleBase + BotConstants.MemoryOffsets.CameraPtr] = BitConverter.GetBytes(cameraBase),
                [cameraBase + BotConstants.MemoryOffsets.CameraDistance] = BitConverter.GetBytes((short)17000),
                [cameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset] =
                    BitConverter.GetBytes(BotConstants.Camera.StationaryVerticalLock)
            };

            GameMemoryService.ReadMemoryDelegate read =
                (uint readPid, ulong address, byte[] buffer, out uint bytesRead) =>
                {
                    bytesRead = 0;
                    if (!memoryBytes.TryGetValue(address, out byte[]? data) || data.Length < buffer.Length)
                        return false;
                    Buffer.BlockCopy(data, 0, buffer, 0, buffer.Length);
                    bytesRead = (uint)buffer.Length;
                    return true;
                };
            GameMemoryService.WriteMemoryDelegate write =
                (uint writePid, ulong address, byte[] buffer, out uint bytesWritten) =>
                {
                    memoryBytes[address] = (byte[])buffer.Clone();
                    bytesWritten = (uint)buffer.Length;
                    return true;
                };

            var memory = new GameMemoryService(pid, read, write, moduleBase, 8, _ => { });
            using var controller = new WKeyCameraController(memory, _ => { });
            using var loot = new LootSystem(memory, _ => { }, controller);
            MethodInfo ensure = typeof(LootSystem).GetMethod("EnsureLootScanCamera", PrivateInstance)
                ?? throw new InvalidOperationException("EnsureLootScanCamera was not found.");
            object?[] arguments = { null };

            Assert.True((bool)(ensure.Invoke(loot, arguments) ?? false));
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock,
                BitConverter.ToInt16(memoryBytes[cameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset], 0));

            WKeyCameraOwner movementOwner = controller.CreateOwner("loot scan race regression", _ => { });
            Assert.False(controller.TryAcquireW(movementOwner));

            MethodInfo release = typeof(LootSystem).GetMethod("ReleaseLootScanCameraLocked", PrivateInstance)
                ?? throw new InvalidOperationException("ReleaseLootScanCameraLocked was not found.");
            release.Invoke(loot, new object[] { "test scan complete" });

            Assert.True(controller.TryAcquireW(movementOwner));
            controller.ReleaseW(movementOwner);
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock,
                BitConverter.ToInt16(memoryBytes[cameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset], 0));
        }

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

        // ── Test 10 — capture failure ───────────────────────────────────────────

        [Fact]
        public void InvalidPreScanGeometryIsCaptureUnavailableNotACandidate()
        {
            using var bitmap = new Bitmap(80, 80);
            LootPreScanResult result = LootSystem.AnalyzeWhiteLootCandidates(
                bitmap, new Rectangle(-10, -10, 5, 5),
                BotConstants.Loot.PassivePreScanMaxCandidateComponents);

            Assert.Equal(LootPreScanOutcome.CaptureUnavailable, result.Outcome);
            Assert.Equal(Rectangle.Empty, result.CandidateBounds);
        }

        [Fact]
        public void CaptureFailureIsNeverAValidEmptyScanOrSuccessfulBarrier()
        {
            LootScanPassResult combined = LootSystem.CombineNormalLootScanResults(
                LootScanPassResult.NoItem, LootScanPassResult.CaptureFailed, cameraVerified: true);
            Assert.Equal(LootScanPassResult.CaptureFailed, combined);
            Assert.NotEqual(LootScanPassResult.NoItem, combined);

            var barrier = new LootNavigationBarrier();
            barrier.Arm(5, LootBarrierReason.PostKill);
            barrier.ApplyStatus(Snapshot(5, LootBarrierReason.PostKill, LootRequestState.Failed, cameraVerified: false));
            Assert.True(barrier.IsBlockingNavigation);
        }

        // ── Test 11 — other movement modes ──────────────────────────────────────

        [Fact]
        public void OnlyMoveAndMoveAndAttackDoNotUseTheMandatoryBarrier()
        {
            Assert.False(MovementSystem.RequiresMandatoryLootBarrier(BotMode.OnlyMove));
            Assert.False(MovementSystem.RequiresMandatoryLootBarrier(BotMode.MoveAndAttack));
            Assert.True(MovementSystem.RequiresMandatoryLootBarrier(BotMode.MoveAndAttackAndLoot));

            using var loot = CreateLoot(_ => { });
            loot.PinkLootOnlyMode = true;
            Assert.True(loot.PinkLootOnlyMode);
            Assert.False(loot.MoveAndAttackAndLootMode);
        }

        // ── Test 12 — no duplicate operations ───────────────────────────────────

        [Fact]
        public void RepeatedRequestsAndServicesDoNotCreateDuplicateObligations()
        {
            using var loot = CreateLoot(_ => { });
            long first = loot.BeginMandatoryLootRequest(LootBarrierReason.TravelCandidateVerification);
            long second = loot.BeginMandatoryLootRequest(LootBarrierReason.TravelCandidateVerification);
            Assert.Equal(first, second);

            loot.NotifyNavigationHeld(first);
            RunMandatoryService(loot);
            RunMandatoryService(loot);

            Assert.True(loot.TryGetMandatoryLootRequestStatus(first, out LootRequestStatus status));
            Assert.Equal(LootRequestState.Running, status.State);

            var barrier = new LootNavigationBarrier();
            barrier.Arm(first, LootBarrierReason.TravelCandidateVerification);
            barrier.Arm(first, LootBarrierReason.TravelCandidateVerification);
            Assert.Equal(first, barrier.RequestId);
            Assert.True(barrier.IsActive);
        }

        // ── Scan-pass result preservation ───────────────────────────────────────

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
            barrier.ApplyStatus(Snapshot(99, LootBarrierReason.PostKill, LootRequestState.Completed, cameraVerified: false));

            Assert.True(barrier.IsBlockingNavigation);
            Assert.True(barrier.HasTerminalFailure);
            Assert.Contains("without verified loot camera", barrier.Detail, StringComparison.Ordinal);
        }

        [Fact]
        public void StaleCompletionForAnotherGenerationCannotReleaseTheArmedRequest()
        {
            var barrier = new LootNavigationBarrier();
            barrier.Arm(100, LootBarrierReason.PostKill);
            barrier.ApplyStatus(Snapshot(101, LootBarrierReason.PostKill, LootRequestState.Completed, cameraVerified: true));

            Assert.True(barrier.IsBlockingNavigation);
        }

        // ── Candidate-shape / text-glyph filtering ──────────────────────────────

        [Fact]
        public void TextGlyphRowsAreNotPlausibleLootCandidates()
        {
            using var bitmap = new Bitmap(200, 100);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
                for (int i = 0; i < 7; i++)
                    graphics.FillRectangle(Brushes.White, 20 + i * 6, 30, 4, 4);
            }

            LootPreScanResult result = LootSystem.AnalyzeWhiteLootCandidates(
                bitmap, new Rectangle(0, 0, 200, 100),
                BotConstants.Loot.PassivePreScanMaxCandidateComponents);

            Assert.Equal(LootPreScanOutcome.NoCandidate, result.Outcome);
            Assert.Equal(0, result.CandidateComponents);
        }

        [Fact]
        public void CharacterCenterAreaIsScannedByThePreScan()
        {
            using var bitmap = new Bitmap(200, 200);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
                graphics.FillRectangle(Brushes.White, 90, 90, 14, 14);
            }

            LootPreScanResult result = LootSystem.AnalyzeWhiteLootCandidates(
                bitmap,
                new Rectangle(0, 0, 200, 200),
                BotConstants.Loot.PassivePreScanMaxCandidateComponents);

            Assert.Equal(LootPreScanOutcome.PotentialCandidate, result.Outcome);
            Assert.True(result.CandidateComponents >= 1);
        }

        [Fact]
        public void TooManyCandidateComponentsAreTreatedAsUiClutter()
        {
            using var bitmap = new Bitmap(300, 300);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
                for (int i = 0; i < 6; i++)
                {
                    for (int j = 0; j < 6; j++)
                    {
                        if (i == j) continue;
                        graphics.FillRectangle(Brushes.White, 20 + i * 40, 20 + j * 40, 12, 12);
                    }
                }
            }

            LootPreScanResult result = LootSystem.AnalyzeWhiteLootCandidates(
                bitmap, new Rectangle(0, 0, 300, 300),
                maxCandidateComponents: 8);

            Assert.Equal(LootPreScanOutcome.NoCandidate, result.Outcome);
            Assert.True(result.CandidateComponents > 8);
        }

        // ── Cancellation / focus loss ───────────────────────────────────────────

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
            barrier.ApplyStatus(deferred);
            Assert.True(barrier.IsBlockingNavigation);

            loot.Stop();
            Assert.True(loot.TryGetMandatoryLootRequestStatus(id, out LootRequestStatus cancelled));
            Assert.Equal(LootRequestState.Cancelled, cancelled.State);
            barrier.ApplyStatus(cancelled);
            Assert.True(barrier.IsBlockingNavigation);
        }
    }
}
