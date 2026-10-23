using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class BotOperationsWCameraTests
    {
        private const uint Pid = 992;
        private const ulong ModuleBase = 0x610000;
        private const ulong CameraBase = 0x3500000;
        private const int EtanaYawBits = 1070165538;

        [Fact]
        public async Task EtanaRepotUnstuckKeepsYawAndWDurationWithCameraModeTransitions()
        {
            var memory = new MemoryFixture();
            var events = memory.Events;
            GameMemoryService service = memory.CreateService();
            using var cameraController = new WKeyCameraController(service, _ => { });
            var pathRunner = new PathRunnerService(service, _ => { }, wKeyCameraController: cameraController);
            var operationContext = new OperationContext(
                service,
                pathRunner,
                new ItemSellerService(service, _ => { }, cameraController),
                new BotProfile(),
                _ => { },
                () => { },
                wKeyCameraController: cameraController,
                sendWKeyEvent: keyUp => events.Add(keyUp ? "W-up" : "W-down"));

            var timer = Stopwatch.StartNew();
            bool succeeded = await BotOperations.EtanaRepotUnstuck(operationContext, CancellationToken.None);
            timer.Stop();

            Assert.True(succeeded);
            Assert.Equal(EtanaYawBits, memory.ReadCameraYawBits());
            Assert.InRange(timer.ElapsedMilliseconds, 2450, 5000);

            int movementLockIndex = events.IndexOf($"Vertical:{BotConstants.Camera.MovementVerticalLock}");
            int wDownIndex = events.IndexOf("W-down");
            int wUpIndex = events.IndexOf("W-up");
            int stationaryLockIndex = events.IndexOf($"Vertical:{BotConstants.Camera.StationaryVerticalLock}");
            Assert.True(movementLockIndex >= 0 && movementLockIndex < wDownIndex);
            Assert.True(wDownIndex < wUpIndex && wUpIndex < stationaryLockIndex,
                $"Unexpected W/camera event order: {string.Join(", ", events)}");
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());
            Assert.Equal(0, cameraController.ActiveWOwnerCount);

            pathRunner.Stop();
            cameraController.EndSession();
        }

        [Fact]
        public async Task CancelledEtanaRepotUnstuckReleasesWAndCameraOwnership()
        {
            var memory = new MemoryFixture();
            var events = memory.Events;
            GameMemoryService service = memory.CreateService();
            using var cameraController = new WKeyCameraController(service, _ => { });
            var pathRunner = new PathRunnerService(service, _ => { }, wKeyCameraController: cameraController);
            var operationContext = new OperationContext(
                service,
                pathRunner,
                new ItemSellerService(service, _ => { }, cameraController),
                new BotProfile(),
                _ => { },
                () => { },
                wKeyCameraController: cameraController,
                sendWKeyEvent: keyUp => events.Add(keyUp ? "W-up" : "W-down"));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => BotOperations.EtanaRepotUnstuck(operationContext, cancellation.Token));

            int movementLockIndex = events.IndexOf($"Vertical:{BotConstants.Camera.MovementVerticalLock}");
            int wDownIndex = events.IndexOf("W-down");
            int wUpIndex = events.IndexOf("W-up");
            int stationaryLockIndex = events.IndexOf($"Vertical:{BotConstants.Camera.StationaryVerticalLock}");
            Assert.True(movementLockIndex >= 0 && movementLockIndex < wDownIndex);
            Assert.True(wDownIndex < wUpIndex && wUpIndex < stationaryLockIndex,
                $"Unexpected cancellation event order: {string.Join(", ", events)}");
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock, memory.ReadCameraVerticalLock());
            Assert.Equal(0, cameraController.ActiveWOwnerCount);
            Assert.Equal(EtanaYawBits, memory.ReadCameraYawBits());

            pathRunner.Stop();
            cameraController.EndSession();
        }

        private sealed class MemoryFixture
        {
            private readonly Dictionary<ulong, byte[]> _memory = new();
            public List<string> Events { get; } = new();

            public MemoryFixture()
            {
                Set(ModuleBase + BotConstants.MemoryOffsets.CameraPtr, BitConverter.GetBytes(CameraBase));
                Set(CameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset,
                    BitConverter.GetBytes(BotConstants.Camera.StationaryVerticalLock));
                Set(CameraBase + BotConstants.MemoryOffsets.CameraAngle, BitConverter.GetBytes(0));
            }

            private void Set(ulong address, byte[] bytes) => _memory[address] = bytes;

            public short ReadCameraVerticalLock()
                => BitConverter.ToInt16(_memory[CameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset], 0);

            public int ReadCameraYawBits()
                => BitConverter.ToInt32(_memory[CameraBase + BotConstants.MemoryOffsets.CameraAngle], 0);

            public GameMemoryService CreateService()
            {
                GameMemoryService.ReadMemoryDelegate read =
                    (uint pid, ulong address, byte[] buffer, out uint bytesRead) =>
                    {
                        bytesRead = 0;
                        if (!_memory.TryGetValue(address, out byte[]? stored) || stored.Length < buffer.Length)
                            return false;
                        Buffer.BlockCopy(stored, 0, buffer, 0, buffer.Length);
                        bytesRead = (uint)buffer.Length;
                        return true;
                    };
                GameMemoryService.WriteMemoryDelegate write =
                    (uint pid, ulong address, byte[] buffer, out uint bytesWritten) =>
                    {
                        _memory[address] = (byte[])buffer.Clone();
                        bytesWritten = (uint)buffer.Length;
                        if (address == CameraBase + BotConstants.MemoryOffsets.CameraAngle)
                            Events.Add($"Yaw:{BitConverter.ToInt32(buffer, 0)}");
                        else if (address == CameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset)
                            Events.Add($"Vertical:{BitConverter.ToInt16(buffer, 0)}");
                        return true;
                    };

                return new GameMemoryService(Pid, read, write, ModuleBase, 8, _ => { });
            }
        }
    }
}
