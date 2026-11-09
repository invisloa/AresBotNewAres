using System;
using System.Collections.Generic;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Covers the buff-slot array:
    /// <c>[[Ares.exe + 0x489BE8] + 0x178] + 0x12C + i * 0x50</c> (16-bit values).
    /// Slot 0 (0x3EFF9FEC) and slot 1 (0x3EFFA03C) were verified live; the remaining
    /// slots follow the measured 0x50 stride.
    /// </summary>
    public sealed class BuffSlotReadTests
    {
        private const uint Pid = 5555;
        private const int PointerSize = 4; // Ares.exe is a 32-bit client
        private const ulong ModuleBase = 0x400000;
        private const ulong UiWindow = 0x21000000;

        // CastSpeedBuff resolved live to 0x3EFF9FFA; 0x3EFF9FFA - 0x13A = 0x3EFF9EC0.
        private const ulong BuffHolder = 0x3EFF9EC0;

        private static ulong UiWindowAddress => ModuleBase + BotConstants.MemoryOffsets.UiWindowM;
        private static ulong HolderPointerAddress =>
            UiWindow + (ulong)BotConstants.MemoryOffsets.BuffSlotsPtr;

        [Theory]
        [InlineData(0, 0x3EFF9FECUL)]
        [InlineData(1, 0x3EFFA03CUL)]
        [InlineData(2, 0x3EFFA08CUL)]
        [InlineData(3, 0x3EFFA0DCUL)]
        [InlineData(4, 0x3EFFA12CUL)]
        [InlineData(5, 0x3EFFA17CUL)]
        public void ReadsEachSlotThroughThe0x50Stride(int slotIndex, ulong expectedAddress)
        {
            var memory = new MemoryFixture();
            short expected = (short)(1000 + slotIndex);
            memory.SetShort(expectedAddress, expected);

            GameMemoryService service = memory.CreateService();

            Assert.True(service.TryGetBuffSlotValue(slotIndex, out short value));
            Assert.Equal(expected, value);
            Assert.Contains((expectedAddress, sizeof(short)), memory.ReadRequests);
        }

        [Fact]
        public void LastSlotStartsAtFirstSlotPlusFiveStrides()
        {
            Assert.Equal(
                0x3EFF9FECUL + (5 * (ulong)BotConstants.MemoryOffsets.BuffSlotSize),
                0x3EFFA17CUL);

            Assert.Equal(
                BotConstants.MemoryOffsets.CastSpeedBuff,
                BotConstants.MemoryOffsets.FirstBuffSlot + 0xE);
        }

        [Fact]
        public void OutOfRangeSlotIsRejectedWithoutReading()
        {
            var memory = new MemoryFixture();
            GameMemoryService service = memory.CreateService();

            Assert.False(service.TryGetBuffSlotValue(-1, out _));
            Assert.False(service.TryGetBuffSlotValue(6, out _));
            Assert.Empty(memory.ReadRequests);
        }

        [Fact]
        public void UnreadableSlotValueIsFalseNotZero()
        {
            var memory = new MemoryFixture();
            memory.FailRead(0x3EFFA17C);
            GameMemoryService service = memory.CreateService();

            Assert.False(service.TryGetBuffSlotValue(5, out short value));
            Assert.Equal(0, value);
        }

        private sealed class MemoryFixture
        {
            private readonly Dictionary<ulong, byte[]> _memory = new();
            private readonly HashSet<ulong> _failedReads = new();

            public List<(ulong address, int length)> ReadRequests { get; } = new();

            public MemoryFixture()
            {
                Set(UiWindowAddress, BitConverter.GetBytes((uint)UiWindow));
                Set(HolderPointerAddress, BitConverter.GetBytes((uint)BuffHolder));
            }

            public void Set(ulong address, byte[] value) => _memory[address] = value;

            public void SetShort(ulong address, short value) =>
                Set(address, BitConverter.GetBytes(value));

            public void FailRead(ulong address) => _failedReads.Add(address);

            public GameMemoryService CreateService()
            {
                GameMemoryService.ReadMemoryDelegate read =
                    (uint pid, ulong address, byte[] buffer, out uint bytesRead) =>
                    {
                        ReadRequests.Add((address, buffer.Length));
                        bytesRead = 0;
                        if (_failedReads.Contains(address) ||
                            !_memory.TryGetValue(address, out byte[]? value) ||
                            value.Length < buffer.Length)
                            return false;

                        Buffer.BlockCopy(value, 0, buffer, 0, buffer.Length);
                        bytesRead = (uint)buffer.Length;
                        return true;
                    };

                GameMemoryService.WriteMemoryDelegate write =
                    (uint pid, ulong address, byte[] buffer, out uint bytesWritten) =>
                    {
                        bytesWritten = 0;
                        return false;
                    };

                return new GameMemoryService(Pid, read, write, ModuleBase, PointerSize, _ => { });
            }
        }
    }
}
