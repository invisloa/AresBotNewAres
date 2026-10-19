using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Regression coverage for the confirmed COT failure: map 36 was visible while
    /// X/Y still belonged to the previous location, StartProtection treated the stale
    /// coordinates as a wrong route start and teleported to town ~337 ms after entry.
    /// These tests exercise the real position synchronization state machine.
    /// </summary>
    public sealed class PositionSynchronizationTests
    {
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly MethodInfo StartPositionMatchesMethod = typeof(BotWorkflowCoordinator)
            .GetMethod("StartPositionMatches", PrivateStatic)
            ?? throw new InvalidOperationException("StartPositionMatches not found.");

        private static readonly MethodInfo RunStartProtectionCoreMethod = typeof(BotWorkflowCoordinator)
            .GetMethod("RunStartProtectionCoreAsync", PrivateInstance)
            ?? throw new InvalidOperationException("RunStartProtectionCoreAsync not found.");

        private static PositionSyncOptions FastOptions() => new()
        {
            MapConfirmationTimeoutMs = 500,
            MapStableReadCount = 2,
            PollMs = 10,
            SettleTimeoutMs = 150,
            WRefreshSettleTimeoutMs = 150,
            WRefreshPollMs = 10,
            MaxWRefreshAttempts = 2,
            WRefreshRetryDelayMs = 10,
            MaxTransitionRestarts = 3,
            NudgeKeyDownMs = 1
        };

        [Fact]
        public async Task DelayedCoordinateRefreshAfterMapChangeDoesNotPrematurelyFail()
        {
            var memory = new SyncMemory { Map = 35, X = 336, Y = 146 };
            int nudges = 0;
            var service = CreateService(memory, FastOptions(), _ =>
            {
                nudges++;
                return Task.CompletedTask;
            }, out _);

            service.MarkTransitionImminent("GoInsideCOT");

            memory.Map = 36;
            memory.FreshX = 128;
            memory.FreshY = 145;
            memory.FreshAt = DateTime.UtcNow.AddMilliseconds(60);

            PositionSyncResult result = await service.SynchronizeAsync(36, CancellationToken.None);

            Assert.True(result.IsReady);
            Assert.Equal(36, result.State.Map);
            Assert.Equal(128, result.State.X);
            Assert.Equal(145, result.State.Y);
            Assert.Equal(0, nudges);
            Assert.Equal(PositionSyncState.PositionReady, service.State);
            Assert.False(service.HasPendingTransition);
        }

        [Fact]
        public async Task MiniWRefreshUsesNewCoordinatesInsteadOfStaleOnes()
        {
            var memory = new SyncMemory { Map = 35, X = 336, Y = 146 };
            int nudges = 0;
            var service = CreateService(memory, FastOptions(), _ =>
            {
                nudges++;
                memory.X = 128;
                memory.Y = 145;
                return Task.CompletedTask;
            }, out _);

            service.MarkTransitionImminent("GoInsideCOT");
            memory.Map = 36; // X/Y intentionally remain stale

            PositionSyncResult result = await service.SynchronizeAsync(36, CancellationToken.None);

            Assert.True(result.IsReady);
            Assert.Equal(128, result.State.X);
            Assert.Equal(145, result.State.Y);
            Assert.Equal(36, result.State.Map);
            Assert.Equal(1, nudges);
            Assert.NotEqual((short)336, result.State.X);
        }

        [Fact]
        public async Task WRefreshThatDoesNotMovePlayerNeverFabricatesReady()
        {
            var memory = new SyncMemory { Map = 35, X = 336, Y = 146 };
            int nudges = 0;
            var service = CreateService(memory, FastOptions(), _ =>
            {
                nudges++;
                return Task.CompletedTask;
            }, out List<string> log);

            service.MarkTransitionImminent("GoInsideCOT");
            memory.Map = 36;

            PositionSyncResult result = await service.SynchronizeAsync(36, CancellationToken.None);

            Assert.False(result.IsReady);
            Assert.Equal(PositionSyncResultKind.Unconfirmed, result.Kind);
            Assert.Equal(336, result.State.X);
            Assert.Equal(146, result.State.Y);
            Assert.Equal(2, nudges);
            Assert.Equal(PositionSyncState.PositionSynchronizationFailed, service.State);
            Assert.True(service.HasPendingTransition);
            Assert.Contains(log, line => line.Contains("Position could not be proven fresh"));
        }

        [Fact]
        public async Task GenuineFarStartPositionIsTrustworthyAndNotFabricatedAsExpected()
        {
            var memory = new SyncMemory { Map = 35, X = 214, Y = 211 };
            var service = CreateService(memory, FastOptions(), _ => Task.CompletedTask, out _);

            service.MarkTransitionImminent("GoInsideCOT");
            memory.Map = 36;
            memory.X = 180;
            memory.Y = 210;

            PositionSyncResult result = await service.SynchronizeAsync(36, CancellationToken.None);

            Assert.True(result.IsReady);
            Assert.Equal(180, result.State.X);
            Assert.Equal(210, result.State.Y);
            Assert.Equal(36, result.State.Map);

            // The comparison must not accept the configured route start merely because
            // synchronization succeeded, and the map/position checks stay independent.
            object?[] arguments = { result.State, 128, 145, 36, 5, false, false };
            bool matches = (bool)(StartPositionMatchesMethod.Invoke(null, arguments) ?? false);
            Assert.False(matches);
            Assert.True((bool)arguments[5]!);  // map matches
            Assert.False((bool)arguments[6]!); // position is genuinely far
        }

        [Fact]
        public async Task MapNotReachedIsDistinctFromPositionMismatchAndDoesNotNudge()
        {
            var memory = new SyncMemory { Map = 35, X = 214, Y = 211 };
            int nudges = 0;
            var options = FastOptions();
            options.MapConfirmationTimeoutMs = 120;
            var service = CreateService(memory, options, _ =>
            {
                nudges++;
                return Task.CompletedTask;
            }, out _);

            service.MarkTransitionImminent("GoInsideCOT");
            memory.Map = 36;

            PositionSyncResult result = await service.SynchronizeAsync(37, CancellationToken.None);

            Assert.False(result.IsReady);
            Assert.Equal(PositionSyncResultKind.MapNotReached, result.Kind);
            Assert.Equal(0, nudges);
        }

        [Fact]
        public async Task CancellationDuringWRefreshStopsTheAttemptWithoutRetrying()
        {
            var memory = new SyncMemory { Map = 35, X = 336, Y = 146 };
            int nudges = 0;
            using var cts = new CancellationTokenSource();
            var service = CreateService(memory, FastOptions(), token =>
            {
                nudges++;
                cts.Cancel();
                throw new OperationCanceledException(token);
            }, out _);

            service.MarkTransitionImminent("GoInsideCOT");
            memory.Map = 36;

            PositionSyncResult result = await service.SynchronizeAsync(36, cts.Token);

            Assert.False(result.IsReady);
            Assert.Equal(PositionSyncResultKind.Cancelled, result.Kind);
            Assert.Equal(1, nudges);
            Assert.Equal(PositionSyncState.PositionSynchronizationFailed, service.State);
        }

        [Fact]
        public async Task SecondTransitionSupersedesFirstAndCannotBeValidatedByIt()
        {
            var memory = new SyncMemory { Map = 35, X = 100, Y = 100 };
            PositionSyncService? service = null;
            service = CreateService(memory, FastOptions(), _ =>
            {
                // First transition's refresh callback: a newer transition starts while
                // the old synchronization is still in flight.
                memory.Map = 37;
                memory.X = 200;
                memory.Y = 200;
                service!.BeginTransition(
                    "second transition",
                    new PlayerStateSnapshot(true, SyncMemory.PlayerBase, true, 36, true, 100, 100, DateTime.UtcNow));
                return Task.CompletedTask;
            }, out List<string> log);

            service.MarkTransitionImminent("first transition");
            memory.Map = 36;
            memory.X = 100;
            memory.Y = 100;

            PositionSyncResult first = await service.SynchronizeAsync(36, CancellationToken.None);

            Assert.False(first.IsReady);
            Assert.Contains(log, line => line.Contains("TransitionId=2") || line.Contains("MapNotReached"));

            PositionSyncResult second = await service.SynchronizeAsync(37, CancellationToken.None);

            Assert.True(second.IsReady);
            Assert.Equal(37, second.State.Map);
            Assert.Equal(200, second.State.X);
            Assert.Equal(200, second.State.Y);
            Assert.Equal(2, service.TransitionId);
            Assert.Equal(37, service.LastValidatedState!.Value.Map);
        }

        [Fact]
        public async Task OrdinaryMovementWithoutTransitionDoesNotNudge()
        {
            var memory = new SyncMemory { Map = 1, X = 10, Y = 10 };
            int nudges = 0;
            var service = CreateService(memory, FastOptions(), _ =>
            {
                nudges++;
                return Task.CompletedTask;
            }, out _);

            PositionSyncResult result = await service.EnsurePositionReadyAsync(
                "ordinary waypoint movement", 0, CancellationToken.None);

            Assert.True(result.IsReady);
            Assert.Equal(10, result.State.X);
            Assert.Equal(10, result.State.Y);
            Assert.Equal(0, nudges);
            Assert.Equal(PositionSyncState.PositionReady, service.State);
        }

        [Fact]
        public async Task TownTeleportWithUnchangedMapButChangedPositionIsFresh()
        {
            var memory = new SyncMemory { Map = 35, X = 214, Y = 211 };
            var service = CreateService(memory, FastOptions(), _ => Task.CompletedTask, out _);

            service.MarkTransitionImminent("TeleportToCity");
            memory.Map = 35;
            memory.X = 300;
            memory.Y = 300;

            PositionSyncResult result = await service.SynchronizeAsync(0, CancellationToken.None);

            Assert.True(result.IsReady);
            Assert.Equal(35, result.State.Map);
            Assert.Equal(300, result.State.X);
            Assert.Equal(300, result.State.Y);
        }

        [Fact]
        public async Task InvalidPlayerPointerIsNotReportedAsZeroPosition()
        {
            var memory = new SyncMemory { Map = 35, X = 336, Y = 146 };
            var service = CreateService(memory, FastOptions(), _ => Task.CompletedTask, out _);

            service.MarkTransitionImminent("GoInsideCOT");
            memory.Map = 36;
            memory.FailPointer = true;

            PositionSyncResult result = await service.SynchronizeAsync(36, CancellationToken.None);

            Assert.False(result.IsReady);
            Assert.False(result.State.PlayerPointerReadSucceeded);
            Assert.False(result.State.HasCoherentState);
            Assert.True(result.Kind == PositionSyncResultKind.Timeout ||
                        result.Kind == PositionSyncResultKind.Unconfirmed);
        }

        [Fact]
        public async Task FailedCoordinateReadIsNotTreatedAsZeroPosition()
        {
            var memory = new SyncMemory { Map = 35, X = 336, Y = 146 };
            var options = FastOptions();
            options.MapConfirmationTimeoutMs = 100;
            var service = CreateService(memory, options, _ => Task.CompletedTask, out _);

            service.MarkTransitionImminent("GoInsideCOT");
            memory.Map = 36;
            memory.FailPosition = true;

            PositionSyncResult result = await service.SynchronizeAsync(36, CancellationToken.None);

            Assert.False(result.IsReady);
            Assert.False(result.State.PositionReadSucceeded);
            Assert.False(result.State.HasCoherentState);
        }

        [Fact]
        public async Task FailedMapReadIsNotTreatedAsAConfirmedMap()
        {
            var memory = new SyncMemory { Map = 35, X = 336, Y = 146 };
            var options = FastOptions();
            options.MapConfirmationTimeoutMs = 100;
            var service = CreateService(memory, options, _ => Task.CompletedTask, out _);

            service.MarkTransitionImminent("GoInsideCOT");
            memory.FailMap = true;

            PositionSyncResult result = await service.SynchronizeAsync(36, CancellationToken.None);

            Assert.False(result.IsReady);
            Assert.False(result.State.MapReadSucceeded);
            Assert.False(result.State.HasCoherentState);
        }

        [Fact]
        public async Task CancelledWPressAlwaysSendsKeyUp()
        {
            var memory = new SyncMemory();
            var options = FastOptions();
            options.NudgeKeyDownMs = 5000;
            var events = new List<(byte vk, byte scan, uint flags)>();
            var service = new PositionSyncService(
                memory.CreateMemory(_ => { }),
                _ => { },
                () => { },
                wRefreshAsync: null,
                options,
                (vk, scan, flags) => events.Add((vk, scan, flags)));

            using var cts = new CancellationTokenSource(50);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.PressWRefreshAsync(cts.Token));

            Assert.Equal(2, events.Count);
            Assert.Equal(BotConstants.Keyboard.VkW, events[0].vk);
            Assert.Equal(0u, events[0].flags);
            Assert.Equal(BotConstants.Keyboard.VkW, events[1].vk);
            Assert.Equal((uint)BotConstants.Keyboard.KeyEventKeyUp, events[1].flags);
        }

        [Fact]
        public void CoherentSnapshotReadsThePointerOnce()
        {
            const ulong moduleBase = 0x100000;
            const ulong playerBaseA = 0x200000;
            const ulong playerBaseB = 0x300000;
            int pointerReads = 0;

            GameMemoryService.ReadMemoryDelegate read = (uint pid, ulong address, byte[] buffer, out uint bytesRead) =>
            {
                bytesRead = 0;
                if (address == moduleBase + BotConstants.MemoryOffsets.PlayerPtr)
                {
                    pointerReads++;
                    return Copy(buffer, BitConverter.GetBytes(pointerReads == 1 ? playerBaseA : playerBaseB), out bytesRead);
                }

                ulong offset = address - playerBaseA;
                if (address >= playerBaseA && address < playerBaseA + 0x2000)
                {
                    return offset switch
                    {
                        BotConstants.MemoryOffsets.CurrentMap => Copy(buffer, BitConverter.GetBytes(36), out bytesRead),
                        BotConstants.MemoryOffsets.X => Copy(buffer, BitConverter.GetBytes((short)128), out bytesRead),
                        BotConstants.MemoryOffsets.Y => Copy(buffer, BitConverter.GetBytes((short)145), out bytesRead),
                        _ => false
                    };
                }

                offset = address - playerBaseB;
                if (address >= playerBaseB && address < playerBaseB + 0x2000)
                {
                    return offset switch
                    {
                        BotConstants.MemoryOffsets.CurrentMap => Copy(buffer, BitConverter.GetBytes(37), out bytesRead),
                        BotConstants.MemoryOffsets.X => Copy(buffer, BitConverter.GetBytes((short)900), out bytesRead),
                        BotConstants.MemoryOffsets.Y => Copy(buffer, BitConverter.GetBytes((short)900), out bytesRead),
                        _ => false
                    };
                }

                return false;
            };

            GameMemoryService.WriteMemoryDelegate write =
                (uint pid, ulong address, byte[] buffer, out uint bytesWritten) =>
                {
                    bytesWritten = 0;
                    return false;
                };

            var memory = new GameMemoryService(0, read, write, moduleBase, 8, _ => { });
            PlayerStateSnapshot snapshot = memory.GetPlayerStateSnapshot();

            Assert.Equal(1, pointerReads);
            Assert.Equal(playerBaseA, snapshot.PlayerBase);
            Assert.Equal(36, snapshot.Map);
            Assert.Equal(128, snapshot.X);
            Assert.Equal(145, snapshot.Y);
            Assert.True(snapshot.HasCoherentState);
        }

        [Fact]
        public async Task StartProtectionBlocksWhenPositionCannotBeValidated()
        {
            var memory = new SyncMemory { Map = 35, X = 336, Y = 146 };
            var options = FastOptions();
            options.SettleTimeoutMs = 20;
            options.WRefreshSettleTimeoutMs = 20;
            options.MaxWRefreshAttempts = 1;

            int nudges = 0;
            var service = CreateService(memory, options, _ =>
            {
                nudges++;
                return Task.CompletedTask;
            }, out List<string> log);

            service.MarkTransitionImminent("GoInsideCOT");
            memory.Map = 36;

            var coordinator = CreateCoordinator(memory, service, log);
            object?[] arguments = { 128, 145, 36, 5, CancellationToken.None };
            var task = (Task<bool>?)RunStartProtectionCoreMethod.Invoke(coordinator, arguments);
            Assert.NotNull(task);
            bool passed = await task!;

            Assert.False(passed);
            Assert.Equal(1, nudges);
            Assert.Contains(log, line => line.Contains("Town teleport BLOCKED"));
            Assert.Contains(log, line => line.Contains("Start check postponed"));
        }

        [Fact]
        public async Task StartProtectionPassesWhenSynchronizedPositionMatchesRouteStart()
        {
            var memory = new SyncMemory { Map = 36, X = 128, Y = 145 };
            int nudges = 0;
            var service = CreateService(memory, FastOptions(), _ =>
            {
                nudges++;
                return Task.CompletedTask;
            }, out List<string> log);

            var coordinator = CreateCoordinator(memory, service, log);
            object?[] arguments = { 128, 145, 36, 5, CancellationToken.None };
            var task = (Task<bool>?)RunStartProtectionCoreMethod.Invoke(coordinator, arguments);
            Assert.NotNull(task);
            bool passed = await task!;

            Assert.True(passed);
            Assert.Equal(0, nudges);
            Assert.Contains(log, line => line.Contains("[StartProtection] PASS"));
        }

        private static PositionSyncService CreateService(
            SyncMemory memory,
            PositionSyncOptions options,
            Func<CancellationToken, Task> nudge,
            out List<string> log)
        {
            log = new List<string>();
            return new PositionSyncService(memory.CreateMemory(log.Add), log.Add, () => { }, nudge, options);
        }

        private static BotWorkflowCoordinator CreateCoordinator(
            SyncMemory memory,
            PositionSyncService positionSync,
            List<string> log)
        {
            GameMemoryService memoryService = memory.CreateMemory(log.Add);
            var pathLoader = new SavedPathLoader(log.Add);
            var pathRunner = new PathRunnerService(memoryService, log.Add);
            var itemSeller = new ItemSellerService(memoryService, log.Add);
            var profile = new BotProfile();
            var context = new OperationContext(
                memoryService,
                pathRunner,
                itemSeller,
                profile,
                log.Add,
                () => { },
                positionSync);
            var operationRunner = new OperationRunnerService(context, log.Add);
            return new BotWorkflowCoordinator(
                memoryService,
                new RepotSystem(memoryService, log.Add),
                new RepotDetectorService(log.Add),
                pathLoader,
                pathRunner,
                operationRunner,
                profile,
                log.Add,
                () => { },
                positionSync);
        }

        private static bool Copy(byte[] destination, byte[] value, out uint bytesRead)
        {
            bytesRead = 0;
            if (value.Length < destination.Length)
                return false;

            Buffer.BlockCopy(value, 0, destination, 0, destination.Length);
            bytesRead = (uint)destination.Length;
            return true;
        }

        /// <summary>
        /// Minimal memory simulation for the synchronization tests. Unknown player
        /// offsets fail the read, matching an unreadable game object.
        /// </summary>
        private sealed class SyncMemory
        {
            public const ulong ModuleBase = 0x100000;
            public const ulong PlayerBase = 0x200000;

            public int Map = 35;
            public short X = 100;
            public short Y = 100;
            public DateTime? FreshAt;
            public short FreshX = 100;
            public short FreshY = 100;
            public bool FailPointer;
            public bool FailMap;
            public bool FailPosition;

            public GameMemoryService CreateMemory(Action<string> log)
                => new(0, Read, Write, ModuleBase, 8, log);

            private bool Read(uint pid, ulong address, byte[] buffer, out uint bytesRead)
            {
                bytesRead = 0;

                if (address == ModuleBase + BotConstants.MemoryOffsets.PlayerPtr)
                {
                    if (FailPointer)
                        return false;
                    return Copy(buffer, BitConverter.GetBytes(PlayerBase), out bytesRead);
                }

                if (address >= PlayerBase && address < PlayerBase + 0x2000)
                {
                    ulong offset = address - PlayerBase;
                    (short x, short y) position = FreshAt.HasValue && DateTime.UtcNow >= FreshAt.Value
                        ? (FreshX, FreshY)
                        : (X, Y);

                    return offset switch
                    {
                        BotConstants.MemoryOffsets.CurrentMap when !FailMap =>
                            Copy(buffer, BitConverter.GetBytes(Map), out bytesRead),
                        BotConstants.MemoryOffsets.X when !FailPosition =>
                            Copy(buffer, BitConverter.GetBytes(position.x), out bytesRead),
                        BotConstants.MemoryOffsets.Y when !FailPosition =>
                            Copy(buffer, BitConverter.GetBytes(position.y), out bytesRead),
                        _ => false
                    };
                }

                return false;
            }

            private bool Write(uint pid, ulong address, byte[] buffer, out uint bytesWritten)
            {
                bytesWritten = 0;
                return false;
            }
        }
    }
}
