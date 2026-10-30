using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class WKeyCameraControllerTests
    {
        private const uint Pid = 873;
        private const ulong ModuleBase = 0x510000;
        private const ulong CameraBase = 0x2400000;

        [Fact]
        public void SessionStartEstablishesStationaryViewWithoutRedundantWritesOrYawChange()
        {
            var memory = new MemoryFixture(BotConstants.Camera.MovementVerticalLock);
            float yawBefore = memory.ReadCameraYaw();

            using var controller = new WKeyCameraController(memory.CreateService(), _ => { });

            Assert.Equal(new[] { $"Vertical:{BotConstants.Camera.StationaryVerticalLock}" }, memory.VerticalWrites);
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());
            Assert.Equal(yawBefore, memory.ReadCameraYaw());

            Assert.True(controller.TryInitializeStationaryView());
            Assert.Single(memory.VerticalWrites);
        }

        [Fact]
        public void FirstWDownAppliesMovementLockBeforeKeyAndWUpRestoresStationaryLock()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            List<string> events = memory.Events;
            float yawBefore = memory.ReadCameraYaw();
            using var controller = new WKeyCameraController(memory.CreateService(), _ => { });
            var movement = new RecordingMovementSystem(memory.CreateService(), controller, events);

            Assert.True(InvokeBool(movement, "TryStartMoving"));
            Assert.Equal(new[] { $"Vertical:{BotConstants.Camera.MovementVerticalLock}", "W-down" }, events);

            movement.StopMoving();

            Assert.Equal($"Vertical:{BotConstants.Camera.StationaryVerticalLock}", events[3]);
            Assert.Equal("W-up", events[2]);
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());
            Assert.Equal(yawBefore, memory.ReadCameraYaw());
            Assert.Equal(0, controller.ActiveWOwnerCount);
        }

        [Fact]
        public void RepeatedWRequestsDoNotRepeatVerticalWrites()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            using var controller = new WKeyCameraController(memory.CreateService(), _ => { });
            var movement = new RecordingMovementSystem(memory.CreateService(), controller, memory.Events);

            for (int i = 0; i < 100; i++)
                Assert.True(InvokeBool(movement, "TryStartMoving"));

            Assert.Single(memory.VerticalWrites);
            Assert.Equal(new[] { $"Vertical:{BotConstants.Camera.MovementVerticalLock}" }, memory.VerticalWrites);
            movement.ReleaseCameraOwnership();
        }

        [Fact]
        public void ForceStartMovingKeepsMovementViewAcrossItsInternalKeyGap()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            using var controller = new WKeyCameraController(memory.CreateService(), _ => { });
            var movement = new RecordingMovementSystem(memory.CreateService(), controller, memory.Events);
            Assert.True(InvokeBool(movement, "TryStartMoving"));
            memory.Events.Clear();
            memory.VerticalWrites.Clear();

            InvokeVoid(movement, "ForceStartMoving");

            Assert.Equal(new[] { "W-up", "W-down" }, memory.Events);
            Assert.Empty(memory.VerticalWrites);
            Assert.Equal(BotConstants.Camera.MovementVerticalLock, memory.ReadCameraVerticalLock());
            movement.StopMoving();
        }

        [Fact]
        public void RepeatedStopMovingDoesNotRepeatStationaryCameraWrite()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            using var controller = new WKeyCameraController(memory.CreateService(), _ => { });
            var movement = new RecordingMovementSystem(memory.CreateService(), controller, memory.Events);
            Assert.True(InvokeBool(movement, "TryStartMoving"));

            movement.StopMoving();
            int writesAfterFirstStop = memory.VerticalWrites.Count;
            movement.StopMoving();

            Assert.Equal(2, writesAfterFirstStop);
            Assert.Equal(writesAfterFirstStop, memory.VerticalWrites.Count);
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());
        }

        [Fact]
        public void MultipleWOwnersAreIdempotentAndOnlyLastReleaseRestoresStationaryView()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            using var controller = new WKeyCameraController(memory.CreateService(), _ => { });
            WKeyCameraOwner first = controller.CreateOwner("waypoint movement", keyUp =>
                memory.Events.Add(keyUp ? "first-W-up" : "first-W-down"));
            WKeyCameraOwner second = controller.CreateOwner("position refresh", keyUp =>
                memory.Events.Add(keyUp ? "second-W-up" : "second-W-down"));

            Assert.True(controller.TryAcquireW(first));
            Assert.True(controller.TryAcquireW(first));
            Assert.True(controller.TryAcquireW(second));
            Assert.Single(memory.VerticalWrites);
            Assert.Equal(1, memory.Events.Count(e => e == "first-W-down"));
            Assert.DoesNotContain("second-W-down", memory.Events);

            controller.ReleaseW(first);
            controller.ReleaseW(first);
            Assert.Equal(BotConstants.Camera.MovementVerticalLock, memory.ReadCameraVerticalLock());
            Assert.Single(memory.VerticalWrites);
            Assert.DoesNotContain("first-W-up", memory.Events);

            controller.ReleaseW(second);
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());
            Assert.Equal(2, memory.VerticalWrites.Count);
            Assert.Equal(1, memory.Events.Count(e => e == "second-W-up"));
            Assert.Equal(0, controller.ActiveWOwnerCount);
        }

        [Fact]
        public void FailedMovementLockWritePreventsWDown()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock) { IgnoreVerticalWrites = true };
            using var controller = new WKeyCameraController(memory.CreateService(), _ => { });
            var movement = new RecordingMovementSystem(memory.CreateService(), controller, memory.Events);

            Assert.False(InvokeBool(movement, "TryStartMoving"));
            Assert.DoesNotContain("W-down", memory.Events);
            Assert.Equal(0, controller.ActiveWOwnerCount);
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());
        }

        [Fact]
        public void FailedWDownReleasesCameraOwnershipAndRestoresStationaryView()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            using var controller = new WKeyCameraController(memory.CreateService(), _ => { });
            var movement = new RecordingMovementSystem(memory.CreateService(), controller, memory.Events)
            {
                ThrowOnWDown = true
            };

            Assert.False(InvokeBool(movement, "TryStartMoving"));
            Assert.Contains("W-up", memory.Events);
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());
            Assert.Equal(0, controller.ActiveWOwnerCount);
        }

        [Fact]
        public void KeyboardTurningWithoutWDoesNotChangeVerticalLock()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            using var controller = new WKeyCameraController(memory.CreateService(), _ => { });
            var movement = new RecordingMovementSystem(memory.CreateService(), controller, memory.Events);

            InvokeVoid(movement, "ApplyKeyboardSteeringBearing", 30f, false);

            Assert.Contains(memory.Events, e => e.StartsWith("A-or-D:", StringComparison.Ordinal));
            Assert.Empty(memory.VerticalWrites);
            Assert.Equal(0, controller.ActiveWOwnerCount);
        }

        [Fact]
        public void AttackSkillThreeAloneDoesNotRequestMovementView()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            using var controller = new WKeyCameraController(memory.CreateService(), _ => { });
            var movement = new RecordingMovementSystem(memory.CreateService(), controller, memory.Events);

            InvokeVoid(movement, "HoldSkillThree");
            movement.ReleaseCombatKeys();

            Assert.Equal(new[] { "Skill3-down", "Skill3-up" }, memory.Events);
            Assert.Empty(memory.VerticalWrites);
            Assert.Equal(0, controller.ActiveWOwnerCount);
        }

        [Fact]
        public void CameraDistanceChangeDoesNotOverwriteMovementLockOrYaw()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            float yawBefore = memory.ReadCameraYaw();
            using var controller = new WKeyCameraController(memory.CreateService(), _ => { });
            var movement = new RecordingMovementSystem(memory.CreateService(), controller, memory.Events);
            Assert.True(InvokeBool(movement, "TryStartMoving"));
            memory.VerticalWrites.Clear();

            Assert.True(InvokeBool(movement, "TryApplyMovementCameraDistance", (short)16950));

            Assert.Equal(BotConstants.Camera.MovementVerticalLock, memory.ReadCameraVerticalLock());
            Assert.Empty(memory.VerticalWrites);
            Assert.Equal(yawBefore, memory.ReadCameraYaw());
            movement.StopMoving();
        }

        [Fact]
        public void LootCameraOwnerAndMandatoryBarrierBlockUnauthorizedWRequests()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            using var controller = new WKeyCameraController(memory.CreateService(), _ => { });
            WKeyCameraOwner movementOwner = controller.CreateOwner("main movement", _ => { });
            WKeyCameraOwner auxiliaryOwner = controller.CreateOwner("auxiliary recovery path", _ => { });
            object lootOwner = new();
            object sellerOwner = new();

            Assert.True(controller.TryAcquireExternalCameraOwner(lootOwner));
            Assert.False(controller.TryAcquireExternalCameraOwner(sellerOwner));
            Assert.False(controller.TryAcquireW(movementOwner));
            controller.ReleaseExternalCameraOwner(lootOwner, restoreStationary: true);
            Assert.True(controller.TryAcquireExternalCameraOwner(sellerOwner));
            controller.ReleaseExternalCameraOwner(sellerOwner, restoreStationary: true);

            controller.SetMandatoryLootNavigationBlock(movementOwner, blocked: true);
            Assert.False(controller.TryAcquireW(auxiliaryOwner));
            Assert.False(controller.TryAcquireW(movementOwner));
            Assert.True(controller.TryAcquireW(movementOwner, authorizedDuringLootBarrier: true));
            controller.ReleaseW(movementOwner);
            controller.SetMandatoryLootNavigationBlock(movementOwner, blocked: false);

            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());
        }

        [Fact]
        public void ActiveSessionRejectsASecondControllerUntilOwnershipIsReleased()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            using var first = new WKeyCameraController(memory.CreateService(), _ => { });
            WKeyCameraOwner firstOwner = first.CreateOwner("workflow W", _ => { });
            Assert.True(first.TryAcquireW(firstOwner));

            Assert.Throws<InvalidOperationException>(
                () => new WKeyCameraController(memory.CreateService(), _ => { }));

            first.EndSession();
            Assert.False(first.IsEnded);
            first.ReleaseW(firstOwner);
            Assert.True(first.IsEnded);
            Assert.False(WKeyCameraController.HasActiveSession(Pid, ModuleBase));

            using var second = new WKeyCameraController(memory.CreateService(), _ => { });
            WKeyCameraOwner secondOwner = second.CreateOwner("new workflow W", _ => { });
            Assert.True(second.TryAcquireW(secondOwner));
            memory.VerticalWrites.Clear();

            // Releasing the old, already-ended owner must not touch the new session.
            first.ReleaseW(firstOwner);

            Assert.Empty(memory.VerticalWrites);
            Assert.Equal(BotConstants.Camera.MovementVerticalLock, memory.ReadCameraVerticalLock());
            Assert.Equal(1, second.ActiveWOwnerCount);
            second.ReleaseW(secondOwner);
        }

        [Fact]
        public void ExternalCameraLeaseBlocksCameraYawWritesUntilReleased()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            using var controller = new WKeyCameraController(memory.CreateService(), _ => { });
            object scanOwner = new();
            int initialAngleBits = BitConverter.SingleToInt32Bits(1.25f);
            int attemptedAngleBits = BitConverter.SingleToInt32Bits(2.5f);

            Assert.True(controller.TrySetCameraAngleBits(initialAngleBits));
            Assert.True(controller.TryAcquireExternalCameraOwner(scanOwner));

            Assert.False(controller.TrySetCameraAngleBits(attemptedAngleBits));
            Assert.False(controller.TrySetCameraAngle(2.5f));
            Assert.Equal(initialAngleBits,
                BitConverter.SingleToInt32Bits(memory.ReadCameraYaw()));

            controller.ReleaseExternalCameraOwner(scanOwner, restoreStationary: true);

            Assert.True(controller.TrySetCameraAngle(2.5f));
            Assert.Equal(attemptedAngleBits,
                BitConverter.SingleToInt32Bits(memory.ReadCameraYaw()));
        }

        [Fact]
        public void ExternalCameraReleaseWithoutRestoreDoesNotForceStationaryView()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            using var controller = new WKeyCameraController(memory.CreateService(), _ => { });
            object diagnosticOwner = new();

            Assert.True(controller.TryAcquireExternalCameraOwner(diagnosticOwner));
            short capturedValue = 16400;
            memory.Set(CameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset,
                BitConverter.GetBytes(capturedValue));

            controller.ReleaseExternalCameraOwner(diagnosticOwner, restoreStationary: false);

            Assert.Equal(capturedValue, memory.ReadCameraVerticalLock());

            object nextOwner = new();
            Assert.True(controller.TryAcquireExternalCameraOwner(nextOwner));
            controller.ReleaseExternalCameraOwner(nextOwner, restoreStationary: true);
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());
        }

        [Fact]
        public void FailedFinalWUpKeepsReleaseUnresolvedAndDoesNotRestoreStationaryView()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            var logs = new List<string>();
            using var controller = new WKeyCameraController(memory.CreateService(), logs.Add);
            bool failWUp = true;
            int wUpAttempts = 0;
            WKeyCameraOwner owner = controller.CreateOwner("W-up failure", keyUp =>
            {
                if (keyUp)
                {
                    wUpAttempts++;
                    if (failWUp)
                        throw new InvalidOperationException("test W-up failure");
                }
                memory.Events.Add(keyUp ? "W-up" : "W-down");
            });

            try
            {
                Assert.True(controller.TryAcquireW(owner));
                Assert.Equal(BotConstants.Camera.MovementVerticalLock, memory.ReadCameraVerticalLock());

                controller.ReleaseW(owner);

                Assert.Equal(0, controller.ActiveWOwnerCount);
                Assert.True(controller.HasUnresolvedPhysicalWRelease);
                Assert.False(controller.IsAcceptingOperations);
                Assert.False(controller.IsEnded);
                Assert.Equal(BotConstants.Camera.MovementVerticalLock, memory.ReadCameraVerticalLock());
                Assert.Contains(logs, line => line.Contains("Physical W-up failed", StringComparison.Ordinal));
                Assert.Contains(logs, line => line.Contains("remains unresolved", StringComparison.Ordinal));

                controller.EndSession();

                Assert.False(controller.IsEnded);
                Assert.True(controller.HasUnresolvedPhysicalWRelease);
                Assert.True(WKeyCameraController.HasActiveSession(Pid, ModuleBase));
                Assert.Throws<InvalidOperationException>(
                    () => new WKeyCameraController(memory.CreateService(), _ => { }));
            }
            finally
            {
                failWUp = false;
                controller.ReleaseW(owner);
            }

            Assert.False(controller.HasUnresolvedPhysicalWRelease);
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());
            Assert.True(controller.IsEnded);
            Assert.False(WKeyCameraController.HasActiveSession(Pid, ModuleBase));

            using var replacement = new WKeyCameraController(memory.CreateService(), _ => { });
            Assert.True(replacement.IsAcceptingOperations);
            Assert.True(wUpAttempts >= 2);
        }

        [Fact]
        public void RepeatedCleanupRetriesUnresolvedWUpAndCompletesOnlyAfterSuccess()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            using var controller = new WKeyCameraController(memory.CreateService(), _ => { });
            bool failWUp = true;
            int wUpAttempts = 0;
            WKeyCameraOwner owner = controller.CreateOwner("retry W-up", keyUp =>
            {
                if (keyUp)
                {
                    wUpAttempts++;
                    if (failWUp)
                        throw new InvalidOperationException("test W-up failure");
                }
            });

            try
            {
                Assert.True(controller.TryAcquireW(owner));
                controller.ReleaseW(owner);
                Assert.Equal(1, wUpAttempts);
                Assert.True(controller.HasUnresolvedPhysicalWRelease);

                controller.EndSession();
                Assert.Equal(2, wUpAttempts);
                Assert.False(controller.IsEnded);

                controller.ReleaseW(owner);
                Assert.Equal(3, wUpAttempts);
                Assert.True(controller.HasUnresolvedPhysicalWRelease);
                Assert.Equal(BotConstants.Camera.MovementVerticalLock, memory.ReadCameraVerticalLock());

                failWUp = false;
                controller.EndSession();
            }
            finally
            {
                failWUp = false;
                if (!controller.IsEnded)
                    controller.ReleaseW(owner);
            }

            Assert.Equal(4, wUpAttempts);
            Assert.False(controller.HasUnresolvedPhysicalWRelease);
            Assert.True(controller.IsEnded);
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());
            Assert.False(WKeyCameraController.HasActiveSession(Pid, ModuleBase));
        }

        [Fact]
        public void MultipleWOwnersSendOnlyOnePhysicalWDownAndOneWUp()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            using var controller = new WKeyCameraController(memory.CreateService(), _ => { });
            int downCount = 0;
            int upCount = 0;
            WKeyCameraOwner first = controller.CreateOwner("first owner", keyUp =>
            {
                if (keyUp) upCount++; else downCount++;
            });
            WKeyCameraOwner second = controller.CreateOwner("second owner", keyUp =>
            {
                if (keyUp) upCount++; else downCount++;
            });

            Assert.True(controller.TryAcquireW(first));
            Assert.True(controller.TryAcquireW(second));
            Assert.Equal(1, downCount);

            controller.ReleaseW(first);
            Assert.Equal(0, upCount);
            Assert.Equal(BotConstants.Camera.MovementVerticalLock, memory.ReadCameraVerticalLock());

            controller.ReleaseW(second);
            Assert.Equal(1, downCount);
            Assert.Equal(1, upCount);
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());
            Assert.False(controller.HasUnresolvedPhysicalWRelease);
        }

        [Fact]
        public void ForceStartMovingCannotInterruptAnotherActiveWOwner()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            var logs = new List<string>();
            using var controller = new WKeyCameraController(memory.CreateService(), logs.Add);
            var movement = new RecordingMovementSystem(memory.CreateService(), controller, memory.Events);
            Assert.True(InvokeBool(movement, "TryStartMoving"));

            var second = controller.CreateOwner("position refresh", keyUp =>
                memory.Events.Add(keyUp ? "second-W-up" : "second-W-down"));
            Assert.True(controller.TryAcquireW(second));
            memory.Events.Clear();
            int countBefore = GetField<int>(movement, "_startMoveCount");
            DateTime graceBefore = GetField<DateTime>(movement, "_ignoreStuckUntil");
            DateTime lastRestartBefore = GetField<DateTime>(movement, "_lastForceStartMovingAt");

            InvokeVoid(movement, "ForceStartMoving");

            Assert.Empty(memory.Events);
            Assert.Equal(2, controller.ActiveWOwnerCount);
            Assert.Equal(BotConstants.Camera.MovementVerticalLock, memory.ReadCameraVerticalLock());
            Assert.Contains(logs, line => line.Contains("restart suppressed", StringComparison.Ordinal));
            Assert.Equal(countBefore, GetField<int>(movement, "_startMoveCount"));
            Assert.Equal(graceBefore, GetField<DateTime>(movement, "_ignoreStuckUntil"));
            Assert.Equal(lastRestartBefore, GetField<DateTime>(movement, "_lastForceStartMovingAt"));

            movement.StopMoving();
            Assert.Equal(1, controller.ActiveWOwnerCount);
            controller.ReleaseW(second);
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());
        }

        [Fact]
        public void SuccessfulForceStartMovingUpdatesCountersAndStuckGrace()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            using var controller = new WKeyCameraController(memory.CreateService(), _ => { });
            var movement = new RecordingMovementSystem(memory.CreateService(), controller, memory.Events);
            Assert.True(InvokeBool(movement, "TryStartMoving"));
            int countBefore = GetField<int>(movement, "_startMoveCount");
            DateTime graceBefore = GetField<DateTime>(movement, "_ignoreStuckUntil");
            DateTime lastRestartBefore = GetField<DateTime>(movement, "_lastForceStartMovingAt");
            memory.Events.Clear();

            InvokeVoid(movement, "ForceStartMoving");

            Assert.Equal(new[] { "W-up", "W-down" }, memory.Events);
            Assert.Equal(countBefore + 1, GetField<int>(movement, "_startMoveCount"));
            Assert.True(GetField<DateTime>(movement, "_ignoreStuckUntil") > graceBefore);
            Assert.True(GetField<DateTime>(movement, "_lastForceStartMovingAt") > lastRestartBefore);
            Assert.True(GetField<bool>(movement, "_isMovingForward"));
            Assert.Equal(BotConstants.Camera.MovementVerticalLock, memory.ReadCameraVerticalLock());
            movement.StopMoving();
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());
        }

        [Fact]
        public void ForceStartMovingWUpFailureReturnsFailedWithoutBookkeeping()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            using var controller = new WKeyCameraController(memory.CreateService(), _ => { });
            var movement = new RecordingMovementSystem(memory.CreateService(), controller, memory.Events)
            {
                FailWUp = false
            };
            Assert.True(InvokeBool(movement, "TryStartMoving"));
            int countBefore = GetField<int>(movement, "_startMoveCount");
            DateTime graceBefore = GetField<DateTime>(movement, "_ignoreStuckUntil");
            movement.FailWUp = true;
            memory.Events.Clear();

            try
            {
                InvokeVoid(movement, "ForceStartMoving");

                Assert.Equal(countBefore, GetField<int>(movement, "_startMoveCount"));
                Assert.Equal(graceBefore, GetField<DateTime>(movement, "_ignoreStuckUntil"));
                Assert.False(GetField<bool>(movement, "_isMovingForward"));
                Assert.Equal(0, controller.ActiveWOwnerCount);
                Assert.True(controller.HasUnresolvedPhysicalWRelease);
                Assert.False(controller.IsAcceptingOperations);
                Assert.Equal(BotConstants.Camera.MovementVerticalLock, memory.ReadCameraVerticalLock());
            }
            finally
            {
                movement.FailWUp = false;
                movement.StopMoving();
            }

            Assert.Equal(0, controller.ActiveWOwnerCount);
            Assert.False(controller.HasUnresolvedPhysicalWRelease);
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());
        }

        [Fact]
        public void ForceStartMovingWDownFailureReturnsFailedAndPerformsSafeCleanup()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            using var controller = new WKeyCameraController(memory.CreateService(), _ => { });
            var movement = new RecordingMovementSystem(memory.CreateService(), controller, memory.Events)
            {
                FailWDownAfterFirst = true
            };
            Assert.True(InvokeBool(movement, "TryStartMoving"));
            int countBefore = GetField<int>(movement, "_startMoveCount");
            DateTime graceBefore = GetField<DateTime>(movement, "_ignoreStuckUntil");
            memory.Events.Clear();

            InvokeVoid(movement, "ForceStartMoving");

            Assert.Equal(
                new[] { "W-up", "W-down", "W-up", $"Vertical:{BotConstants.Camera.StationaryVerticalLock}" },
                memory.Events);
            Assert.Equal(countBefore, GetField<int>(movement, "_startMoveCount"));
            Assert.Equal(graceBefore, GetField<DateTime>(movement, "_ignoreStuckUntil"));
            Assert.False(GetField<bool>(movement, "_isMovingForward"));
            Assert.Equal(0, controller.ActiveWOwnerCount);
            Assert.False(controller.HasUnresolvedPhysicalWRelease);
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());

            // Subsequent movement and Stop remain functional after the failed restart.
            var nextMovement = new RecordingMovementSystem(memory.CreateService(), controller, memory.Events);
            Assert.True(InvokeBool(nextMovement, "TryStartMoving"));
            nextMovement.StopMoving();
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());
        }

        [Fact]
        public void TerminatingMovementInstanceClearsOnlyItsOwnMandatoryBarrierRegistration()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            using var controller = new WKeyCameraController(memory.CreateService(), _ => { });
            var oldMovement = new RecordingMovementSystem(memory.CreateService(), controller, memory.Events);
            var newMovement = new RecordingMovementSystem(memory.CreateService(), controller, memory.Events);

            InvokeVoid(oldMovement, "SetLootNavigationBarrierHoldsInput", true);
            Assert.False(InvokeBool(oldMovement, "TryStartMoving"));

            oldMovement.ReleaseCameraOwnership();

            Assert.True(InvokeBool(newMovement, "TryStartMoving"));
            newMovement.ReleaseCameraOwnership();
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());
        }

        [Fact]
        public void StandaloneLootCameraWorksBeforeAndAfterAMovementSession()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            Assert.False(WKeyCameraController.HasActiveSession(Pid, ModuleBase));
            AssertNormalLootCameraVerifies(memory);

            using (var movementController = new WKeyCameraController(memory.CreateService(), _ => { }))
            {
                WKeyCameraOwner owner = movementController.CreateOwner("temporary movement", _ => { });
                Assert.True(movementController.TryAcquireW(owner));
                movementController.ReleaseW(owner);
            }

            Assert.False(WKeyCameraController.HasActiveSession(Pid, ModuleBase));
            AssertNormalLootCameraVerifies(memory);
        }

        [Fact]
        public void SuccessfulFinalReleaseRestoresStationaryBeforeUnregisteringSession()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            using var controller = new WKeyCameraController(memory.CreateService(), _ => { });
            WKeyCameraOwner owner = controller.CreateOwner("final release", keyUp =>
                memory.Events.Add(keyUp ? "W-up" : "W-down"));
            Assert.True(controller.TryAcquireW(owner));

            // Closing with an active W owner keeps the session registered until the
            // final release completes.
            controller.EndSession();
            Assert.False(controller.IsEnded);
            Assert.True(WKeyCameraController.HasActiveSession(Pid, ModuleBase));

            bool? sessionRegisteredDuringFinalRestore = null;
            memory.BeforeVerticalWrite = value =>
            {
                if (value == BotConstants.Camera.StationaryVerticalLock)
                    sessionRegisteredDuringFinalRestore = WKeyCameraController.HasActiveSession(Pid, ModuleBase);
            };
            int writesBefore = memory.VerticalWrites.Count;

            controller.ReleaseW(owner);

            Assert.Equal(writesBefore + 1, memory.VerticalWrites.Count);
            Assert.True(sessionRegisteredDuringFinalRestore,
                "the final stationary restore must happen before the session is unregistered");
            Assert.True(controller.IsEnded);
            Assert.False(WKeyCameraController.HasActiveSession(Pid, ModuleBase));

            using var replacement = new WKeyCameraController(memory.CreateService(), _ => { });
            Assert.True(replacement.IsAcceptingOperations);
        }

        [Fact]
        public void FailedReleaseThenEndSessionRetryRestoresExactlyOnceBeforeUnregistering()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            using var controller = new WKeyCameraController(memory.CreateService(), _ => { });
            bool failWUp = true;
            int stationaryRestoresDuringEndSession = 0;
            bool? sessionRegisteredDuringFinalRestore = null;
            WKeyCameraOwner owner = controller.CreateOwner("failed final release", keyUp =>
            {
                if (keyUp && failWUp)
                    throw new InvalidOperationException("test W-up failure");
                memory.Events.Add(keyUp ? "W-up" : "W-down");
            });

            Assert.True(controller.TryAcquireW(owner));
            controller.ReleaseW(owner);
            Assert.True(controller.HasUnresolvedPhysicalWRelease);
            Assert.True(WKeyCameraController.HasActiveSession(Pid, ModuleBase));
            Assert.Equal(BotConstants.Camera.MovementVerticalLock, memory.ReadCameraVerticalLock());

            failWUp = false;
            memory.BeforeVerticalWrite = value =>
            {
                if (value != BotConstants.Camera.StationaryVerticalLock)
                    return;
                stationaryRestoresDuringEndSession++;
                sessionRegisteredDuringFinalRestore = WKeyCameraController.HasActiveSession(Pid, ModuleBase);
            };
            int writesBeforeEndSession = memory.VerticalWrites.Count;

            controller.EndSession();

            Assert.Equal(writesBeforeEndSession + 1, memory.VerticalWrites.Count);
            Assert.Equal(1, stationaryRestoresDuringEndSession);
            Assert.True(sessionRegisteredDuringFinalRestore,
                "session-end retry must restore the camera before unregistering");
            Assert.False(controller.HasUnresolvedPhysicalWRelease);
            Assert.True(controller.IsEnded);
            Assert.False(WKeyCameraController.HasActiveSession(Pid, ModuleBase));
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());

            // The ended generation can no longer write to the game camera.
            memory.VerticalWrites.Clear();
            controller.EndSession();
            controller.Dispose();
            controller.ReleaseW(owner);
            Assert.Empty(memory.VerticalWrites);

            using var replacement = new WKeyCameraController(memory.CreateService(), _ => { });
            Assert.True(replacement.IsAcceptingOperations);
        }

        [Fact]
        public async Task NewControllerCannotRegisterDuringFinalRestorationAndOldSessionNeverOverwritesIt()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            using var oldController = new WKeyCameraController(memory.CreateService(), _ => { });
            object externalOwner = new();
            Assert.True(oldController.TryAcquireExternalCameraOwner(externalOwner));
            short operationView = 16400;
            memory.Set(CameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset,
                BitConverter.GetBytes(operationView));

            // A closing session waits for its external camera owner before the final write.
            oldController.EndSession();
            Assert.False(oldController.IsEnded);
            Assert.True(WKeyCameraController.HasActiveSession(Pid, ModuleBase));

            using var restoreStarted = new ManualResetEventSlim(false);
            using var allowRestoreToFinish = new ManualResetEventSlim(false);
            memory.BeforeVerticalWrite = value =>
            {
                if (value != BotConstants.Camera.StationaryVerticalLock)
                    return;
                restoreStarted.Set();
                Assert.True(allowRestoreToFinish.Wait(TimeSpan.FromSeconds(10)),
                    "test did not release the blocked final restoration");
            };

            Task releaseTask = Task.Run(() =>
                oldController.ReleaseExternalCameraOwner(externalOwner, restoreStationary: true));
            Assert.True(restoreStarted.Wait(TimeSpan.FromSeconds(10)),
                "the old controller never entered its final restoration");

            // The session is still registered while the final camera write is in flight.
            Assert.Throws<InvalidOperationException>(
                () => new WKeyCameraController(memory.CreateService(), _ => { }));

            allowRestoreToFinish.Set();
            Task finishedRelease = await Task.WhenAny(releaseTask, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(releaseTask, finishedRelease);
            await releaseTask;
            Assert.True(oldController.IsEnded);
            Assert.False(WKeyCameraController.HasActiveSession(Pid, ModuleBase));
            memory.BeforeVerticalWrite = null;

            using var replacement = new WKeyCameraController(memory.CreateService(), _ => { });
            WKeyCameraOwner newOwner = replacement.CreateOwner("new session W", keyUp =>
                memory.Events.Add(keyUp ? "new-W-up" : "new-W-down"));
            Assert.True(replacement.TryAcquireW(newOwner));
            Assert.Equal(BotConstants.Camera.MovementVerticalLock, memory.ReadCameraVerticalLock());
            memory.VerticalWrites.Clear();
            memory.Events.Clear();

            // Every late call from the old generation must be a no-op.
            oldController.ReleaseExternalCameraOwner(externalOwner);
            oldController.ReleaseW(newOwner);
            oldController.EndSession();
            oldController.Dispose();

            Assert.Empty(memory.VerticalWrites);
            Assert.Empty(memory.Events);
            Assert.Equal(BotConstants.Camera.MovementVerticalLock, memory.ReadCameraVerticalLock());
            Assert.Equal(1, replacement.ActiveWOwnerCount);
            replacement.ReleaseW(newOwner);
        }

        [Fact]
        public void RepeatedCleanupAfterSuccessfulReleaseIsIdempotentAndDoesNotAffectReplacement()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            using var controller = new WKeyCameraController(memory.CreateService(), _ => { });
            int wUpCount = 0;
            WKeyCameraOwner owner = controller.CreateOwner("repeated cleanup", keyUp =>
            {
                if (keyUp) wUpCount++;
                memory.Events.Add(keyUp ? "W-up" : "W-down");
            });

            Assert.True(controller.TryAcquireW(owner));
            controller.EndSession();
            controller.ReleaseW(owner);

            Assert.True(controller.IsEnded);
            Assert.Equal(1, wUpCount);
            int writesAfterSuccessfulRelease = memory.VerticalWrites.Count;

            for (int i = 0; i < 5; i++)
            {
                controller.ReleaseW(owner);
                controller.EndSession();
                controller.Dispose();
            }

            Assert.Equal(writesAfterSuccessfulRelease, memory.VerticalWrites.Count);
            Assert.Equal(1, wUpCount);
            Assert.False(WKeyCameraController.HasActiveSession(Pid, ModuleBase));

            using var replacement = new WKeyCameraController(memory.CreateService(), _ => { });
            WKeyCameraOwner newOwner = replacement.CreateOwner("replacement W", keyUp =>
                memory.Events.Add(keyUp ? "new-W-up" : "new-W-down"));
            Assert.True(replacement.TryAcquireW(newOwner));
            Assert.Equal(BotConstants.Camera.MovementVerticalLock, memory.ReadCameraVerticalLock());
            memory.VerticalWrites.Clear();
            memory.Events.Clear();

            for (int i = 0; i < 5; i++)
            {
                controller.ReleaseW(owner);
                controller.EndSession();
                controller.Dispose();
            }

            Assert.Empty(memory.VerticalWrites);
            Assert.Empty(memory.Events);
            Assert.Equal(BotConstants.Camera.MovementVerticalLock, memory.ReadCameraVerticalLock());
            Assert.Equal(1, replacement.ActiveWOwnerCount);
            replacement.ReleaseW(newOwner);
        }

        [Fact]
        public void ExternalCameraOwnershipDelaysSessionCompletionUntilItsRelease()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            using var controller = new WKeyCameraController(memory.CreateService(), _ => { });
            object externalOwner = new();
            Assert.True(controller.TryAcquireExternalCameraOwner(externalOwner));
            short operationView = 16400;
            memory.Set(CameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset,
                BitConverter.GetBytes(operationView));

            controller.EndSession();

            Assert.False(controller.IsEnded);
            Assert.True(WKeyCameraController.HasActiveSession(Pid, ModuleBase));
            Assert.Equal(operationView, memory.ReadCameraVerticalLock());
            Assert.Throws<InvalidOperationException>(
                () => new WKeyCameraController(memory.CreateService(), _ => { }));

            controller.ReleaseExternalCameraOwner(externalOwner, restoreStationary: true);

            Assert.True(controller.IsEnded);
            Assert.False(WKeyCameraController.HasActiveSession(Pid, ModuleBase));
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());

            using var replacement = new WKeyCameraController(memory.CreateService(), _ => { });
            Assert.True(replacement.IsAcceptingOperations);
        }

        private static void AssertNormalLootCameraVerifies(MemoryFixture memory)
        {
            using var loot = new LootSystem(memory.CreateService(), _ => { }, wKeyCameraController: null);
            MethodInfo ensure = typeof(LootSystem).GetMethod("EnsureLootScanCamera", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("EnsureLootScanCamera was not found.");
            object?[] arguments = { null };
            Assert.True((bool)(ensure.Invoke(loot, arguments) ?? false));
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());
            MethodInfo release = typeof(LootSystem).GetMethod("ReleaseLootScanCameraLocked", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("ReleaseLootScanCameraLocked was not found.");
            release.Invoke(loot, new object[] { "test complete" });
        }

        private static T GetField<T>(object target, string name)
        {
            FieldInfo field = typeof(MovementSystem).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"{name} was not found.");
            return (T)(field.GetValue(target) ?? throw new InvalidOperationException($"{name} was null."));
        }

        private static bool InvokeBool(object target, string name, params object[] args)
        {
            Type type = target is MovementSystem ? typeof(MovementSystem) : target.GetType();
            MethodInfo method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"{name} was not found.");
            return (bool)(method.Invoke(target, args) ?? false);
        }

        private static void InvokeVoid(object target, string name, params object[] args)
        {
            Type type = target is MovementSystem ? typeof(MovementSystem) : target.GetType();
            MethodInfo method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"{name} was not found.");
            method.Invoke(target, args);
        }

        private sealed class RecordingMovementSystem : MovementSystem
        {
            private readonly List<string> _events;
            private int _wDownCount;
            public bool ThrowOnWDown { get; init; }
            public bool FailWDownAfterFirst { get; init; }
            public bool FailWUp { get; set; }

            public RecordingMovementSystem(
                GameMemoryService memory,
                WKeyCameraController cameraController,
                List<string> events)
                : base(
                    memory,
                    _ => { },
                    targetX: 10,
                    targetY: 10,
                    customPath: new[] { new Waypoint(10, 10, MovementPrecision.Medium, BotMode.OnlyMove) },
                    wKeyCameraController: cameraController,
                    targetSafety: CombatSafetyFixtures.Monster(5000))
            {
                _events = events;
            }

            protected override void SendSyntheticForwardKeyEvent(bool keyUp)
            {
                _events.Add(keyUp ? "W-up" : "W-down");
                if (!keyUp)
                {
                    _wDownCount++;
                    if (ThrowOnWDown || (FailWDownAfterFirst && _wDownCount > 1))
                        throw new InvalidOperationException("test W-down failure");
                }
                else if (FailWUp)
                {
                    throw new InvalidOperationException("test W-up failure");
                }
            }

            protected override void SendTurnKeyEvent(bool leftA, bool keyUp)
                => _events.Add($"A-or-D:{(leftA ? "A" : "D")}:{(keyUp ? "up" : "down")}");

            protected override void SendSkillThreeKeyEvent(bool keyUp)
                => _events.Add(keyUp ? "Skill3-up" : "Skill3-down");
        }

        private sealed class MemoryFixture
        {
            private readonly Dictionary<ulong, byte[]> _memory = new();

            public List<string> Events { get; } = new();
            public List<string> VerticalWrites { get; } = new();
            public bool IgnoreVerticalWrites { get; init; }

            /// <summary>Optional hook used by deterministic shutdown-race tests.</summary>
            public Action<short>? BeforeVerticalWrite { get; set; }

            public MemoryFixture(short verticalLock)
            {
                Set(ModuleBase + BotConstants.MemoryOffsets.CameraPtr, BitConverter.GetBytes(CameraBase));
                Set(CameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset, BitConverter.GetBytes(verticalLock));
                Set(CameraBase + BotConstants.MemoryOffsets.CameraAngle, BitConverter.GetBytes(0f));
                Set(CameraBase + BotConstants.MemoryOffsets.CameraDistance, BitConverter.GetBytes((short)17000));
            }

            public void Set(ulong address, byte[] value) => _memory[address] = value;

            public short ReadCameraVerticalLock()
                => BitConverter.ToInt16(_memory[CameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset], 0);

            public float ReadCameraYaw()
                => BitConverter.ToSingle(_memory[CameraBase + BotConstants.MemoryOffsets.CameraAngle], 0);

            public GameMemoryService CreateService()
            {
                GameMemoryService.ReadMemoryDelegate read =
                    (uint pid, ulong address, byte[] buffer, out uint bytesRead) =>
                    {
                        bytesRead = 0;
                        if (!_memory.TryGetValue(address, out byte[]? data) || data.Length < buffer.Length)
                            return false;
                        Buffer.BlockCopy(data, 0, buffer, 0, buffer.Length);
                        bytesRead = (uint)buffer.Length;
                        return true;
                    };

                GameMemoryService.WriteMemoryDelegate write =
                    (uint pid, ulong address, byte[] buffer, out uint bytesWritten) =>
                    {
                        bytesWritten = 0;
                        if (address == CameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset && IgnoreVerticalWrites)
                            return false;
                        if (address == CameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset)
                            BeforeVerticalWrite?.Invoke(BitConverter.ToInt16(buffer, 0));
                        _memory[address] = (byte[])buffer.Clone();
                        bytesWritten = (uint)buffer.Length;
                        if (address == CameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset)
                        {
                            string entry = $"Vertical:{BitConverter.ToInt16(buffer, 0)}";
                            Events.Add(entry);
                            VerticalWrites.Add(entry);
                        }
                        return true;
                    };

                return new GameMemoryService(Pid, read, write, ModuleBase, 8, _ => { });
            }
        }
    }
}
