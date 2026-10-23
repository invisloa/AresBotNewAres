using System;
using System.Collections.Generic;
using System.Reflection;
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
        public void FailedWUpIsReportedAndDoesNotLeaveLogicalOwnership()
        {
            var memory = new MemoryFixture(BotConstants.Camera.StationaryVerticalLock);
            var logs = new List<string>();
            using var controller = new WKeyCameraController(memory.CreateService(), logs.Add);
            WKeyCameraOwner owner = controller.CreateOwner("W-up failure", keyUp =>
            {
                if (keyUp)
                    throw new InvalidOperationException("test W-up failure");
            });

            Assert.True(controller.TryAcquireW(owner));
            controller.ReleaseW(owner);

            Assert.Equal(0, controller.ActiveWOwnerCount);
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());
            Assert.Contains(logs, line => line.Contains("Physical W-up failed", StringComparison.Ordinal));
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

            InvokeVoid(movement, "ForceStartMoving");

            Assert.Empty(memory.Events);
            Assert.Equal(2, controller.ActiveWOwnerCount);
            Assert.Equal(BotConstants.Camera.MovementVerticalLock, memory.ReadCameraVerticalLock());
            Assert.Contains(logs, line => line.Contains("restart suppressed", StringComparison.Ordinal));

            movement.StopMoving();
            Assert.Equal(1, controller.ActiveWOwnerCount);
            controller.ReleaseW(second);
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
            public bool ThrowOnWDown { get; init; }

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
                    wKeyCameraController: cameraController)
            {
                _events = events;
            }

            protected override void SendSyntheticForwardKeyEvent(bool keyUp)
            {
                _events.Add(keyUp ? "W-up" : "W-down");
                if (!keyUp && ThrowOnWDown)
                    throw new InvalidOperationException("test W-down failure");
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
