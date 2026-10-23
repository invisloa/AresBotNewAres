using System;
using System.Collections.Generic;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class GameMemoryServiceCameraSetupTests
    {
        private const uint Pid = 321;
        private const int PointerSize = 8;
        private const ulong ModuleBase = 0x400000;
        private const ulong CameraBase = 0x23456000;

        [Fact]
        public void WKeyCameraSettingsKeepMovementAndStationaryValuesDistinct()
        {
            Assert.Equal((short)16160, BotConstants.Camera.MovementVerticalLock);
            Assert.Equal((short)16320, BotConstants.Camera.StationaryVerticalLock);
            Assert.Equal((ulong)0x1BE, BotConstants.MemoryOffsets.CameraVerticalLockOffset);
        }

        [Fact]
        public void TrySetCameraScanViewWritesAndVerifiesDistanceAndVerticalLock()
        {
            var memory = new MemoryFixture();
            memory.Set(ModuleBase + BotConstants.MemoryOffsets.CameraPtr, BitConverter.GetBytes(CameraBase));
            GameMemoryService service = memory.CreateService();

            Assert.Equal(16950, BotConstants.Camera.OtherPlayerMarkerScanDistance);
            Assert.True(service.TrySetCameraDistance(BotConstants.Camera.OtherPlayerMarkerScanDistance));
            Assert.True(service.TrySetCameraVerticalLock(BotConstants.Camera.StationaryVerticalLock));
            Assert.Equal(BotConstants.Camera.OtherPlayerMarkerScanDistance,
                memory.ReadShort(CameraBase + BotConstants.MemoryOffsets.CameraDistance));
            Assert.Equal(BotConstants.Camera.StationaryVerticalLock,
                memory.ReadShort(CameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset));
        }

        [Fact]
        public void TryGetCameraDistanceReadsTheCurrentValueAndFailsOnMissingPointer()
        {
            var memory = new MemoryFixture();
            memory.Set(ModuleBase + BotConstants.MemoryOffsets.CameraPtr, BitConverter.GetBytes(CameraBase));
            memory.Set(CameraBase + BotConstants.MemoryOffsets.CameraDistance, BitConverter.GetBytes((short)17020));
            GameMemoryService service = memory.CreateService();

            Assert.True(service.TryGetCameraDistance(out short distance));
            Assert.Equal((short)17020, distance);

            GameMemoryService noCamera = new MemoryFixture().CreateService();
            Assert.False(noCamera.TryGetCameraDistance(out _));
        }

        [Fact]
        public void TryGetCameraVerticalLockReadsTheCurrentValueAndFailsOnMissingPointer()
        {
            var memory = new MemoryFixture();
            memory.Set(ModuleBase + BotConstants.MemoryOffsets.CameraPtr, BitConverter.GetBytes(CameraBase));
            memory.Set(CameraBase + BotConstants.MemoryOffsets.CameraVerticalLockOffset,
                BitConverter.GetBytes((short)16310));
            GameMemoryService service = memory.CreateService();

            Assert.True(service.TryGetCameraVerticalLock(out short verticalLock));
            Assert.Equal((short)16310, verticalLock);

            GameMemoryService noCamera = new MemoryFixture().CreateService();
            Assert.False(noCamera.TryGetCameraVerticalLock(out _));
        }

        [Fact]
        public void TrySetSodSopLootScanViewUses17050Distance()
        {
            var memory = new MemoryFixture();
            memory.Set(ModuleBase + BotConstants.MemoryOffsets.CameraPtr, BitConverter.GetBytes(CameraBase));
            GameMemoryService service = memory.CreateService();

            Assert.Equal((short)17050, BotConstants.Camera.SodSopLootScanDistance);
            Assert.True(service.TrySetCameraDistance(BotConstants.Camera.SodSopLootScanDistance));
            Assert.Equal((short)17050,
                memory.ReadShort(CameraBase + BotConstants.MemoryOffsets.CameraDistance));
        }

        [Fact]
        public void TrySetCameraScanViewFailsWhenTheWriteCannotBeVerified()
        {
            var memory = new MemoryFixture { IgnoreWrites = true };
            memory.Set(ModuleBase + BotConstants.MemoryOffsets.CameraPtr, BitConverter.GetBytes(CameraBase));
            GameMemoryService service = memory.CreateService();

            Assert.False(service.TrySetCameraDistance(BotConstants.Camera.LootScanDistance));
            Assert.False(service.TrySetCameraVerticalLock(BotConstants.Camera.StationaryVerticalLock));
        }

        private sealed class MemoryFixture
        {
            private readonly Dictionary<ulong, byte[]> _memory = new();
            public bool IgnoreWrites { get; init; }

            public void Set(ulong address, byte[] bytes) => _memory[address] = bytes;

            public short ReadShort(ulong address)
                => BitConverter.ToInt16(_memory[address], 0);

            public GameMemoryService CreateService()
            {
                GameMemoryService.ReadMemoryDelegate read =
                    (uint pid, ulong address, byte[] buffer, out uint bytesRead) =>
                    {
                        bytesRead = 0;
                        if (!_memory.TryGetValue(address, out byte[]? bytes) || bytes.Length < buffer.Length)
                            return false;

                        Buffer.BlockCopy(bytes, 0, buffer, 0, buffer.Length);
                        bytesRead = (uint)buffer.Length;
                        return true;
                    };
                GameMemoryService.WriteMemoryDelegate write =
                    (uint pid, ulong address, byte[] buffer, out uint bytesWritten) =>
                    {
                        bytesWritten = 0;
                        if (IgnoreWrites)
                            return true;

                        _memory[address] = (byte[])buffer.Clone();
                        bytesWritten = (uint)buffer.Length;
                        return true;
                    };

                return new GameMemoryService(Pid, read, write, ModuleBase, PointerSize, _ => { });
            }
        }
    }
}
