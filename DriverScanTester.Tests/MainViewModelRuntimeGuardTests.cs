using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Services;
using DriverScanTester.ViewModels;
using Xunit;

namespace DriverScanTester.Tests
{
    internal static class MainViewModelTestHarness
    {
        internal const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        internal static MainViewModel CreateAttached()
        {
            var vm = new MainViewModel(startHotkeyListener: false);
            SetField(vm, "_isAttached", true);
            return vm;
        }

        internal static T GetField<T>(object target, string name)
        {
            FieldInfo field = FindField(target.GetType(), name)
                ?? throw new InvalidOperationException($"{name} was not found.");
            object? value = field.GetValue(target);
            if (value == null)
                throw new InvalidOperationException($"{name} was null.");
            return (T)value;
        }

        internal static T? GetFieldOrNull<T>(object target, string name) where T : class
        {
            FieldInfo field = FindField(target.GetType(), name)
                ?? throw new InvalidOperationException($"{name} was not found.");
            return field.GetValue(target) as T;
        }

        internal static void SetField(object target, string name, object? value)
        {
            FieldInfo field = FindField(target.GetType(), name)
                ?? throw new InvalidOperationException($"{name} was not found.");
            field.SetValue(target, value);
        }

        internal static object? InvokePrivate(object target, string name, params object?[] args)
        {
            MethodInfo method = target.GetType().GetMethod(name, PrivateInstance)
                ?? throw new InvalidOperationException($"{name} was not found.");
            return method.Invoke(target, args);
        }

        internal static BotWorkflowCoordinator CreateRunningWorkflowStub()
        {
            // Only IsRunning is read before the standalone-loot rejection is decided.
            // No production coordinator method is invoked on this uninitialized instance.
            var coordinator = (BotWorkflowCoordinator)RuntimeHelpers.GetUninitializedObject(
                typeof(BotWorkflowCoordinator));
            SetField(coordinator, "_isRunning", true);
            return coordinator;
        }

        private static FieldInfo? FindField(Type type, string name)
        {
            while (type != null)
            {
                FieldInfo? field = type.GetField(name, PrivateInstance);
                if (field != null)
                    return field;
                type = type.BaseType!;
            }
            return null;
        }
    }

    public sealed class StandaloneLootWorkflowIsolationTests
    {
        private const uint Pid = 991;
        private const ulong ModuleBase = 0x620000;
        private const ulong CameraBase = 0x2500000;

        [Fact]
        public void StandaloneLootStartIsRejectedWhileProfileWorkflowRunsWithoutCameraWrites()
        {
            MainViewModel vm = MainViewModelTestHarness.CreateAttached();
            var memory = new CameraMemoryFixture();
            using var workflowController = new WKeyCameraController(memory.CreateService(), _ => { });
            WKeyCameraOwner workflowOwner = workflowController.CreateOwner("workflow W", _ => { });
            Assert.True(workflowController.TryAcquireW(workflowOwner));
            MainViewModelTestHarness.SetField(vm, "_wKeyCameraController", workflowController);
            MainViewModelTestHarness.SetField(vm, "_workflowCoordinator",
                MainViewModelTestHarness.CreateRunningWorkflowStub());
            memory.VerticalWrites.Clear();
            memory.Events.Clear();

            try
            {
                MainViewModelTestHarness.InvokePrivate(vm, "ToggleLootBot", true);

                Assert.Null(MainViewModelTestHarness.GetFieldOrNull<LootSystem>(vm, "_lootSystem"));
                Assert.Null(MainViewModelTestHarness.GetFieldOrNull<Task>(vm, "_lootBotTask"));
                Assert.False(MainViewModelTestHarness.GetField<bool>(vm, "_isLootBotRunning"));
                Assert.Same(workflowController,
                    MainViewModelTestHarness.GetFieldOrNull<WKeyCameraController>(vm, "_wKeyCameraController"));
                Assert.Equal(1, workflowController.ActiveWOwnerCount);
                Assert.Empty(memory.VerticalWrites);
                Assert.Empty(memory.Events);
                Assert.Contains("active profile workflow", vm.LogText, StringComparison.Ordinal);
            }
            finally
            {
                MainViewModelTestHarness.SetField(vm, "_workflowCoordinator", null);
                workflowController.ReleaseW(workflowOwner);
            }
        }

        [Fact]
        public void StandaloneLootStartIsRejectedWhileWorkflowTaskIsCleaningUp()
        {
            MainViewModel vm = MainViewModelTestHarness.CreateAttached();
            var workflowGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            MainViewModelTestHarness.SetField(vm, "_workflowTask", workflowGate.Task);

            try
            {
                MainViewModelTestHarness.InvokePrivate(vm, "ToggleLootBot", true);

                Assert.Null(MainViewModelTestHarness.GetFieldOrNull<LootSystem>(vm, "_lootSystem"));
                Assert.Null(MainViewModelTestHarness.GetFieldOrNull<Task>(vm, "_lootBotTask"));
                Assert.Null(MainViewModelTestHarness.GetFieldOrNull<WKeyCameraController>(vm, "_wKeyCameraController"));
                Assert.False(MainViewModelTestHarness.GetField<bool>(vm, "_isLootBotRunning"));
                Assert.Contains("still cleaning up", vm.LogText, StringComparison.Ordinal);
            }
            finally
            {
                workflowGate.SetResult(true);
            }
        }

        [Fact]
        public async Task StandaloneLootStartsWhenIdleWithoutAWorkflowOrSharedCamera()
        {
            MainViewModel vm = MainViewModelTestHarness.CreateAttached();

            MainViewModelTestHarness.InvokePrivate(vm, "ToggleLootBot", true);

            LootSystem? loot = MainViewModelTestHarness.GetFieldOrNull<LootSystem>(vm, "_lootSystem");
            Task? lootTask = MainViewModelTestHarness.GetFieldOrNull<Task>(vm, "_lootBotTask");
            Assert.NotNull(loot);
            Assert.NotNull(lootTask);
            Assert.True(MainViewModelTestHarness.GetField<bool>(vm, "_isLootBotRunning"));
            Assert.Null(MainViewModelTestHarness.GetFieldOrNull<WKeyCameraController>(vm, "_wKeyCameraController"));

            MainViewModelTestHarness.InvokePrivate(vm, "ToggleLootBot", false);
            Assert.False(MainViewModelTestHarness.GetField<bool>(vm, "_isLootBotRunning"));
            Task completed = await Task.WhenAny(lootTask!, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(lootTask, completed);
        }

        [Fact]
        public async Task StandaloneLootUsesTheRunningMovementsSharedCameraController()
        {
            MainViewModel vm = MainViewModelTestHarness.CreateAttached();
            var memory = new CameraMemoryFixture();
            using var movementController = new WKeyCameraController(memory.CreateService(), _ => { });
            MainViewModelTestHarness.SetField(vm, "_isMovementBotRunning", true);
            MainViewModelTestHarness.SetField(vm, "_wKeyCameraController", movementController);

            try
            {
                MainViewModelTestHarness.InvokePrivate(vm, "ToggleLootBot", true);

                LootSystem loot = MainViewModelTestHarness.GetField<LootSystem>(vm, "_lootSystem");
                Task lootTask = MainViewModelTestHarness.GetField<Task>(vm, "_lootBotTask");
                Assert.Same(movementController,
                    MainViewModelTestHarness.GetFieldOrNull<WKeyCameraController>(loot, "_wKeyCameraController"));
                Assert.True(MainViewModelTestHarness.GetField<bool>(vm, "_isLootBotRunning"));

                MainViewModelTestHarness.InvokePrivate(vm, "ToggleLootBot", false);
                Task completed = await Task.WhenAny(lootTask, Task.Delay(TimeSpan.FromSeconds(5)));
                Assert.Same(lootTask, completed);
            }
            finally
            {
                MainViewModelTestHarness.SetField(vm, "_isMovementBotRunning", false);
            }
        }

        [Fact]
        public async Task EndedControllerReferenceDoesNotBlockStandaloneLoot()
        {
            MainViewModel vm = MainViewModelTestHarness.CreateAttached();
            var memory = new CameraMemoryFixture();
            using var endedController = new WKeyCameraController(memory.CreateService(), _ => { });
            endedController.EndSession();
            Assert.True(endedController.IsEnded);
            MainViewModelTestHarness.SetField(vm, "_wKeyCameraController", endedController);

            MainViewModelTestHarness.InvokePrivate(vm, "ToggleLootBot", true);

            Assert.NotNull(MainViewModelTestHarness.GetFieldOrNull<LootSystem>(vm, "_lootSystem"));
            Task lootTask = MainViewModelTestHarness.GetField<Task>(vm, "_lootBotTask");
            Assert.True(MainViewModelTestHarness.GetField<bool>(vm, "_isLootBotRunning"));

            MainViewModelTestHarness.InvokePrivate(vm, "ToggleLootBot", false);
            Task completed = await Task.WhenAny(lootTask, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(lootTask, completed);
        }

        [Fact]
        public void StandaloneLootIsRejectedWhileAnIndependentOperationTestOwnsTheCamera()
        {
            MainViewModel vm = MainViewModelTestHarness.CreateAttached();
            MainViewModelTestHarness.SetField(vm, "_attachedPid", Pid);
            var memory = new CameraMemoryFixture(pid: Pid, moduleBase: 0);
            using var operationController = new WKeyCameraController(memory.CreateService(), _ => { });
            WKeyCameraOwner operationOwner = operationController.CreateOwner("operation test", _ => { });
            Assert.True(operationController.TryAcquireW(operationOwner));
            memory.VerticalWrites.Clear();
            memory.Events.Clear();

            try
            {
                MainViewModelTestHarness.InvokePrivate(vm, "ToggleLootBot", true);

                Assert.Null(MainViewModelTestHarness.GetFieldOrNull<LootSystem>(vm, "_lootSystem"));
                Assert.Null(MainViewModelTestHarness.GetFieldOrNull<Task>(vm, "_lootBotTask"));
                Assert.False(MainViewModelTestHarness.GetField<bool>(vm, "_isLootBotRunning"));
                Assert.Equal(1, operationController.ActiveWOwnerCount);
                Assert.Equal(BotConstants.Camera.MovementVerticalLock, memory.ReadCameraVerticalLock());
                Assert.Empty(memory.VerticalWrites);
                Assert.Empty(memory.Events);
                Assert.Contains("camera-owning operation", vm.LogText, StringComparison.Ordinal);
                Assert.True(WKeyCameraController.HasActiveSession(Pid, 0));
            }
            finally
            {
                operationController.ReleaseW(operationOwner);
            }

            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());
        }

        [Fact]
        public async Task PreviousOperationTestCleanupBlocksStandaloneLootUntilTheSessionEnds()
        {
            MainViewModel vm = MainViewModelTestHarness.CreateAttached();
            MainViewModelTestHarness.SetField(vm, "_attachedPid", Pid);
            var memory = new CameraMemoryFixture(pid: Pid, moduleBase: 0);
            using var operationController = new WKeyCameraController(memory.CreateService(), _ => { });
            bool failWUp = true;
            WKeyCameraOwner operationOwner = operationController.CreateOwner("operation test", keyUp =>
            {
                if (keyUp && failWUp)
                    throw new InvalidOperationException("test W-up failure");
            });

            Assert.True(operationController.TryAcquireW(operationOwner));
            operationController.ReleaseW(operationOwner);
            Assert.True(operationController.HasUnresolvedPhysicalWRelease);
            operationController.EndSession();
            Assert.False(operationController.IsEnded);
            Assert.True(WKeyCameraController.HasActiveSession(Pid, 0));

            MainViewModelTestHarness.InvokePrivate(vm, "ToggleLootBot", true);

            Assert.Null(MainViewModelTestHarness.GetFieldOrNull<LootSystem>(vm, "_lootSystem"));
            Assert.Null(MainViewModelTestHarness.GetFieldOrNull<Task>(vm, "_lootBotTask"));
            Assert.False(MainViewModelTestHarness.GetField<bool>(vm, "_isLootBotRunning"));
            Assert.Contains("camera-owning operation", vm.LogText, StringComparison.Ordinal);
            Assert.Equal(BotConstants.Camera.MovementVerticalLock, memory.ReadCameraVerticalLock());

            failWUp = false;
            operationController.EndSession();
            Assert.True(operationController.IsEnded);
            Assert.False(WKeyCameraController.HasActiveSession(Pid, 0));

            MainViewModelTestHarness.InvokePrivate(vm, "ToggleLootBot", true);

            LootSystem loot = MainViewModelTestHarness.GetField<LootSystem>(vm, "_lootSystem");
            Task lootTask = MainViewModelTestHarness.GetField<Task>(vm, "_lootBotTask");
            Assert.True(MainViewModelTestHarness.GetField<bool>(vm, "_isLootBotRunning"));
            Assert.Null(MainViewModelTestHarness.GetFieldOrNull<WKeyCameraController>(loot, "_wKeyCameraController"));

            MainViewModelTestHarness.InvokePrivate(vm, "ToggleLootBot", false);
            Task completed = await Task.WhenAny(lootTask, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(lootTask, completed);
        }

        [Fact]
        public async Task IncompletePreviousLootTaskBlocksRestartUntilItsCleanupCompletes()
        {
            MainViewModel vm = MainViewModelTestHarness.CreateAttached();
            var previousTaskGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            MainViewModelTestHarness.SetField(vm, "_lootBotTask", previousTaskGate.Task);
            MainViewModelTestHarness.SetField(vm, "_isLootBotRunning", false);

            MainViewModelTestHarness.InvokePrivate(vm, "ToggleLootBot", true);

            Assert.Null(MainViewModelTestHarness.GetFieldOrNull<LootSystem>(vm, "_lootSystem"));
            Assert.Same(previousTaskGate.Task,
                MainViewModelTestHarness.GetFieldOrNull<Task>(vm, "_lootBotTask"));
            Assert.False(MainViewModelTestHarness.GetField<bool>(vm, "_isLootBotRunning"));
            Assert.Contains("previous standalone loot task", vm.LogText, StringComparison.Ordinal);

            previousTaskGate.SetResult(true);

            MainViewModelTestHarness.InvokePrivate(vm, "ToggleLootBot", true);

            Assert.NotNull(MainViewModelTestHarness.GetFieldOrNull<LootSystem>(vm, "_lootSystem"));
            Task lootTask = MainViewModelTestHarness.GetField<Task>(vm, "_lootBotTask");
            Assert.True(MainViewModelTestHarness.GetField<bool>(vm, "_isLootBotRunning"));

            MainViewModelTestHarness.InvokePrivate(vm, "ToggleLootBot", false);
            Task completed = await Task.WhenAny(lootTask, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(lootTask, completed);
        }

        [Fact]
        public async Task CompletedPreviousLootTaskDoesNotBlockRestart()
        {
            MainViewModel vm = MainViewModelTestHarness.CreateAttached();
            MainViewModelTestHarness.SetField(vm, "_lootBotTask", Task.CompletedTask);
            MainViewModelTestHarness.SetField(vm, "_isLootBotRunning", false);

            MainViewModelTestHarness.InvokePrivate(vm, "ToggleLootBot", true);

            Assert.NotNull(MainViewModelTestHarness.GetFieldOrNull<LootSystem>(vm, "_lootSystem"));
            Task lootTask = MainViewModelTestHarness.GetField<Task>(vm, "_lootBotTask");
            Assert.True(MainViewModelTestHarness.GetField<bool>(vm, "_isLootBotRunning"));
            Assert.DoesNotContain("previous standalone loot task", vm.LogText, StringComparison.Ordinal);

            MainViewModelTestHarness.InvokePrivate(vm, "ToggleLootBot", false);
            Task completed = await Task.WhenAny(lootTask, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(lootTask, completed);
        }

        [Fact]
        public async Task CameraSessionCreationCannotOvertakeAnInProgressLootStartup()
        {
            MainViewModel vm = MainViewModelTestHarness.CreateAttached();
            using var startupEntered = new ManualResetEventSlim(false);
            using var allowStartupToFinish = new ManualResetEventSlim(false);
            vm.CameraRuntimeStartupGateHeldForTests = () =>
            {
                startupEntered.Set();
                Assert.True(allowStartupToFinish.Wait(TimeSpan.FromSeconds(10)),
                    "test did not release the standalone-loot startup");
            };

            Task lootStart = Task.Run(() =>
                MainViewModelTestHarness.InvokePrivate(vm, "ToggleLootBot", true));
            Assert.True(startupEntered.Wait(TimeSpan.FromSeconds(10)),
                "standalone loot never reached its registered startup state");

            // The loot runtime is registered and the startup gate is still held. A
            // concurrent camera-owning start must not create a controller/session.
            Task movementStart = Task.Run(() =>
                MainViewModelTestHarness.InvokePrivate(vm, "ToggleMovementBot", true));

            Assert.Null(MainViewModelTestHarness.GetFieldOrNull<WKeyCameraController>(vm, "_wKeyCameraController"));

            allowStartupToFinish.Set();
            Task startupCompletion = Task.WhenAll(lootStart, movementStart);
            Task finishedStartups = await Task.WhenAny(startupCompletion, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(startupCompletion, finishedStartups);
            await startupCompletion;

            try
            {
                Assert.Null(MainViewModelTestHarness.GetFieldOrNull<WKeyCameraController>(vm, "_wKeyCameraController"));
                Assert.False(MainViewModelTestHarness.GetField<bool>(vm, "_isMovementBotRunning"));
                Assert.NotNull(MainViewModelTestHarness.GetFieldOrNull<LootSystem>(vm, "_lootSystem"));
                Assert.True(MainViewModelTestHarness.GetField<bool>(vm, "_isLootBotRunning"));
                Assert.Contains("standalone loot bot is running", vm.LogText, StringComparison.Ordinal);
            }
            finally
            {
                vm.CameraRuntimeStartupGateHeldForTests = null;
                MainViewModelTestHarness.InvokePrivate(vm, "ToggleLootBot", false);
            }

            Task lootTask = MainViewModelTestHarness.GetField<Task>(vm, "_lootBotTask");
            Task completed = await Task.WhenAny(lootTask, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(lootTask, completed);
        }

        private sealed class CameraMemoryFixture
        {
            private readonly Dictionary<ulong, byte[]> _memory = new();
            private readonly uint _pid;
            private readonly ulong _moduleBase;
            private readonly ulong _cameraBase;

            public List<string> Events { get; } = new();
            public List<string> VerticalWrites { get; } = new();

            public CameraMemoryFixture(
                uint pid = Pid,
                ulong moduleBase = ModuleBase,
                ulong cameraBase = CameraBase)
            {
                _pid = pid;
                _moduleBase = moduleBase;
                _cameraBase = cameraBase;
                Set(moduleBase + BotConstants.MemoryOffsets.CameraPtr, BitConverter.GetBytes(cameraBase));
                Set(cameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset,
                    BitConverter.GetBytes(BotConstants.Camera.StationaryVerticalLock));
                Set(cameraBase + BotConstants.MemoryOffsets.CameraAngle, BitConverter.GetBytes(0f));
                Set(cameraBase + BotConstants.MemoryOffsets.CameraDistance, BitConverter.GetBytes((short)17000));
            }

            private void Set(ulong address, byte[] value) => _memory[address] = value;

            public short ReadCameraVerticalLock()
                => BitConverter.ToInt16(
                    _memory[_cameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset], 0);

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
                        _memory[address] = (byte[])buffer.Clone();
                        bytesWritten = (uint)buffer.Length;
                        if (address == _cameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset)
                        {
                            string entry = $"Vertical:{BitConverter.ToInt16(buffer, 0)}";
                            Events.Add(entry);
                            VerticalWrites.Add(entry);
                        }
                        return true;
                    };

                return new GameMemoryService(_pid, read, write, _moduleBase, 8, _ => { });
            }
        }
    }

    public sealed class PathReplacementCleanupTests
    {
        private const uint Pid = 992;
        private const ulong ModuleBase = 0x630000;
        private const ulong CameraBase = 0x2600000;

        [Fact]
        public void ReplacementWhileMovementTaskIsRunningIsRejectedWithoutCreatingRuntime()
        {
            MainViewModel vm = MainViewModelTestHarness.CreateAttached();
            var movementGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var movementCts = new CancellationTokenSource();
            MainViewModelTestHarness.SetField(vm, "_movementBotTask", movementGate.Task);
            MainViewModelTestHarness.SetField(vm, "_movementBotCts", movementCts);
            MainViewModelTestHarness.SetField(vm, "_isMovementBotRunning", true);

            StartPath(vm);

            Assert.True(movementCts.IsCancellationRequested);
            Assert.Same(movementGate.Task, MainViewModelTestHarness.GetField<Task>(vm, "_movementBotTask"));
            Assert.True(MainViewModelTestHarness.GetField<bool>(vm, "_isMovementBotRunning"));
            Assert.Null(MainViewModelTestHarness.GetFieldOrNull<WKeyCameraController>(vm, "_wKeyCameraController"));
            AssertNoReplacementRuntimesCreated(vm);
            Assert.Contains("still stopping", vm.LogText, StringComparison.Ordinal);

            movementGate.SetResult(true);
        }

        [Fact]
        public void ReplacementWhileMovementCancellationIsPendingIsRejected()
        {
            MainViewModel vm = MainViewModelTestHarness.CreateAttached();
            var movementGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var movementCts = new CancellationTokenSource();
            MainViewModelTestHarness.SetField(vm, "_movementBotTask", movementGate.Task);
            MainViewModelTestHarness.SetField(vm, "_movementBotCts", movementCts);
            // The loop already cleared its running flag but has not completed its finally block.
            MainViewModelTestHarness.SetField(vm, "_isMovementBotRunning", false);

            StartPath(vm);

            Assert.True(movementCts.IsCancellationRequested);
            Assert.Null(MainViewModelTestHarness.GetFieldOrNull<WKeyCameraController>(vm, "_wKeyCameraController"));
            AssertNoReplacementRuntimesCreated(vm);
            Assert.Contains("still stopping", vm.LogText, StringComparison.Ordinal);

            movementGate.SetResult(true);
        }

        [Fact]
        public void ReplacementWhileLootCleanupIsPendingIsRejected()
        {
            MainViewModel vm = MainViewModelTestHarness.CreateAttached();
            var lootGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var lootCts = new CancellationTokenSource();
            MainViewModelTestHarness.SetField(vm, "_movementBotTask", Task.CompletedTask);
            MainViewModelTestHarness.SetField(vm, "_lootBotTask", lootGate.Task);
            MainViewModelTestHarness.SetField(vm, "_lootBotCts", lootCts);
            MainViewModelTestHarness.SetField(vm, "_isLootBotRunning", true);

            StartPath(vm);

            Assert.True(lootCts.IsCancellationRequested);
            Assert.Same(lootGate.Task, MainViewModelTestHarness.GetField<Task>(vm, "_lootBotTask"));
            Assert.Null(MainViewModelTestHarness.GetFieldOrNull<WKeyCameraController>(vm, "_wKeyCameraController"));
            AssertNoReplacementRuntimesCreated(vm);
            Assert.Contains("still stopping", vm.LogText, StringComparison.Ordinal);

            lootGate.SetResult(true);
        }

        [Fact]
        public void ReplacementIsPermittedAfterBothTasksCompletedAndNoOldSessionRemains()
        {
            MainViewModel vm = MainViewModelTestHarness.CreateAttached();
            MainViewModelTestHarness.SetField(vm, "_movementBotTask", Task.CompletedTask);
            MainViewModelTestHarness.SetField(vm, "_lootBotTask", Task.CompletedTask);
            MainViewModelTestHarness.SetField(vm, "_healManaBotTask", Task.CompletedTask);

            bool prepared = (bool)(MainViewModelTestHarness.InvokePrivate(
                vm, "TryPrepareReplacementCameraSession", "Run This Path") ?? false);

            Assert.True(prepared);
            Assert.False(WKeyCameraController.HasActiveSession(Pid, ModuleBase));
            Assert.Null(MainViewModelTestHarness.GetFieldOrNull<WKeyCameraController>(vm, "_wKeyCameraController"));
            AssertNoReplacementRuntimesCreated(vm);
        }

        [Fact]
        public void ReplacementIsRejectedWhilePhysicalWUpRemainsUnresolved()
        {
            MainViewModel vm = MainViewModelTestHarness.CreateAttached();
            var memory = new CameraMemoryFixture();
            var controller = new WKeyCameraController(memory.CreateService(), _ => { });
            bool failWUp = true;
            WKeyCameraOwner owner = controller.CreateOwner("unresolved W-up", keyUp =>
            {
                if (keyUp && failWUp)
                    throw new InvalidOperationException("test W-up failure");
            });
            Assert.True(controller.TryAcquireW(owner));
            controller.ReleaseW(owner);
            Assert.True(controller.HasUnresolvedPhysicalWRelease);
            MainViewModelTestHarness.SetField(vm, "_wKeyCameraController", controller);
            MainViewModelTestHarness.SetField(vm, "_movementBotTask", Task.CompletedTask);
            MainViewModelTestHarness.SetField(vm, "_lootBotTask", Task.CompletedTask);

            bool firstAttempt;
            try
            {
                firstAttempt = (bool)(MainViewModelTestHarness.InvokePrivate(
                    vm, "TryPrepareReplacementCameraSession", "Run This Path") ?? false);
            }
            finally
            {
                failWUp = false;
            }

            Assert.False(firstAttempt);
            Assert.True(controller.HasUnresolvedPhysicalWRelease);
            Assert.False(controller.IsEnded);
            Assert.Equal(BotConstants.Camera.MovementVerticalLock, memory.ReadCameraVerticalLock());
            AssertNoReplacementRuntimesCreated(vm);

            bool retry = (bool)(MainViewModelTestHarness.InvokePrivate(
                vm, "TryPrepareReplacementCameraSession", "Run This Path") ?? false);
            Assert.True(retry);
            Assert.True(controller.IsEnded);
            Assert.False(controller.HasUnresolvedPhysicalWRelease);
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());
            controller.Dispose();
        }

        [Fact]
        public void EndedPreviousControllerStillBlocksReplacementWhileOldTaskIsActive()
        {
            MainViewModel vm = MainViewModelTestHarness.CreateAttached();
            var memory = new CameraMemoryFixture();
            var controller = new WKeyCameraController(memory.CreateService(), _ => { });
            controller.EndSession();
            Assert.True(controller.IsEnded);
            var movementGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            MainViewModelTestHarness.SetField(vm, "_wKeyCameraController", controller);
            MainViewModelTestHarness.SetField(vm, "_movementBotTask", movementGate.Task);

            bool firstAttempt;
            try
            {
                firstAttempt = (bool)(MainViewModelTestHarness.InvokePrivate(
                    vm, "TryPrepareReplacementCameraSession", "Run This Path") ?? false);
            }
            finally
            {
                movementGate.SetResult(true);
            }

            Assert.False(firstAttempt);
            Assert.Contains("still stopping", vm.LogText, StringComparison.Ordinal);
            Assert.Same(controller,
                MainViewModelTestHarness.GetFieldOrNull<WKeyCameraController>(vm, "_wKeyCameraController"));
            AssertNoReplacementRuntimesCreated(vm);

            bool retry = (bool)(MainViewModelTestHarness.InvokePrivate(
                vm, "TryPrepareReplacementCameraSession", "Run This Path") ?? false);
            Assert.True(retry);
            controller.Dispose();
        }

        [Fact]
        public async Task PreviousTaskCleanupCannotAffectTheReplacementRunsController()
        {
            MainViewModel vm = MainViewModelTestHarness.CreateAttached();
            var memory = new CameraMemoryFixture();

            using var oldController = new WKeyCameraController(memory.CreateService(), _ => { });
            var oldMovement = new TestMovementSystem(memory.CreateService(), oldController, memory.Events);
            Assert.True(InvokeBool(oldMovement, "TryStartMoving"));

            oldController.EndSession();
            oldMovement.ReleaseCameraOwnership();
            Assert.True(oldController.IsEnded);

            using var newController = new WKeyCameraController(memory.CreateService(), _ => { });
            var newMovement = new TestMovementSystem(memory.CreateService(), newController, memory.Events);
            Assert.True(InvokeBool(newMovement, "TryStartMoving"));
            memory.Events.Clear();

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var loop = (Task)MainViewModelTestHarness.InvokePrivate(
                vm, "MovementBotLoop", cancelled.Token, oldMovement, oldController)!;
            Task completed = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(loop, completed);
            await loop;

            Assert.False(newController.IsEnded);
            Assert.True(newController.IsAcceptingOperations);
            Assert.Equal(1, newController.ActiveWOwnerCount);
            Assert.Equal(BotConstants.Camera.MovementVerticalLock, memory.ReadCameraVerticalLock());
            Assert.Empty(memory.Events);

            newMovement.StopMoving();
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());
        }

        [Fact]
        public void RapidStopRunStopSequenceRejectsReplacementWhileOldTaskIsStillActive()
        {
            MainViewModel vm = MainViewModelTestHarness.CreateAttached();
            var movementGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var movementCts = new CancellationTokenSource();
            MainViewModelTestHarness.SetField(vm, "_movementBotTask", movementGate.Task);
            MainViewModelTestHarness.SetField(vm, "_movementBotCts", movementCts);
            MainViewModelTestHarness.SetField(vm, "_isMovementBotRunning", true);

            vm.StopAllBotsInternal();
            StartPath(vm);
            Assert.Null(MainViewModelTestHarness.GetFieldOrNull<WKeyCameraController>(vm, "_wKeyCameraController"));
            AssertNoReplacementRuntimesCreated(vm);
            vm.StopAllBotsInternal();

            Assert.True(movementCts.IsCancellationRequested);
            Assert.Same(movementGate.Task, MainViewModelTestHarness.GetField<Task>(vm, "_movementBotTask"));
            AssertNoReplacementRuntimesCreated(vm);

            movementGate.SetResult(true);
        }

        [Fact]
        public void IdleReplacementPreparationIsAllowed()
        {
            MainViewModel vm = MainViewModelTestHarness.CreateAttached();

            bool prepared = (bool)(MainViewModelTestHarness.InvokePrivate(
                vm, "TryPrepareReplacementCameraSession", "Run This Path") ?? false);

            Assert.True(prepared);
            Assert.Null(MainViewModelTestHarness.GetFieldOrNull<WKeyCameraController>(vm, "_wKeyCameraController"));
            AssertNoReplacementRuntimesCreated(vm);
            Assert.False(WKeyCameraController.HasActiveSession(Pid, ModuleBase));
        }

        private static void StartPath(MainViewModel vm)
        {
            var path = new List<Waypoint>
            {
                new(10, 10, MovementPrecision.Medium, BotMode.OnlyMove)
            };
            MainViewModelTestHarness.InvokePrivate(vm, "StartBotWithPath", path, false);
        }

        private static void AssertNoReplacementRuntimesCreated(MainViewModel vm)
        {
            Assert.Null(MainViewModelTestHarness.GetFieldOrNull<MovementSystem>(vm, "_movementSystem"));
            Assert.Null(MainViewModelTestHarness.GetFieldOrNull<HealManaSystem>(vm, "_healManaSystem"));
            Assert.Null(MainViewModelTestHarness.GetFieldOrNull<LootSystem>(vm, "_lootSystem"));
            Task? healTask = MainViewModelTestHarness.GetFieldOrNull<Task>(vm, "_healManaBotTask");
            Assert.True(healTask == null || healTask.IsCompleted);
        }

        private static bool InvokeBool(object target, string name, params object[] args)
        {
            MethodInfo method = typeof(MovementSystem).GetMethod(
                name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"{name} was not found.");
            return (bool)(method.Invoke(target, args) ?? false);
        }

        private sealed class TestMovementSystem : MovementSystem
        {
            private readonly List<string> _events;

            public TestMovementSystem(
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
                => _events.Add(keyUp ? "W-up" : "W-down");

            protected override void SendTurnKeyEvent(bool leftA, bool keyUp) { }

            protected override void SendSkillThreeKeyEvent(bool keyUp) { }
        }

        private sealed class CameraMemoryFixture
        {
            private readonly Dictionary<ulong, byte[]> _memory = new();

            public List<string> Events { get; } = new();

            public CameraMemoryFixture()
            {
                Set(ModuleBase + BotConstants.MemoryOffsets.CameraPtr, BitConverter.GetBytes(CameraBase));
                Set(CameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset,
                    BitConverter.GetBytes(BotConstants.Camera.StationaryVerticalLock));
                Set(CameraBase + BotConstants.MemoryOffsets.CameraAngle, BitConverter.GetBytes(0f));
                Set(CameraBase + BotConstants.MemoryOffsets.CameraDistance, BitConverter.GetBytes((short)17000));
            }

            private void Set(ulong address, byte[] value) => _memory[address] = value;

            public short ReadCameraVerticalLock()
                => BitConverter.ToInt16(
                    _memory[CameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset], 0);

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
                        _memory[address] = (byte[])buffer.Clone();
                        bytesWritten = (uint)buffer.Length;
                        if (address == CameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset)
                            Events.Add($"Vertical:{BitConverter.ToInt16(buffer, 0)}");
                        return true;
                    };

                return new GameMemoryService(Pid, read, write, ModuleBase, 8, _ => { });
            }
        }
    }
}
