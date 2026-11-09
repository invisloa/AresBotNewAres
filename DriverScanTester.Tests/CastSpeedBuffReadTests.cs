using System;
using System.Collections.Generic;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Covers the cast-speed buff read:
    /// <c>[[Ares.exe + 0x489BE8] + 0x178] + 0x13A</c> (16-bit), where the verified live
    /// value -16512 (0xBF80) means the buff is NOT active.
    /// </summary>
    public sealed class CastSpeedBuffReadTests
    {
        private const uint Pid = 5555;
        private const int PointerSize = 4; // Ares.exe is a 32-bit client
        private const ulong ModuleBase = 0x400000;
        private const ulong UiWindow = 0x21000000;
        private const ulong CastSpeedHolder = 0x22000000;

        private static ulong UiWindowAddress => ModuleBase + BotConstants.MemoryOffsets.UiWindowM;
        private static ulong CastSpeedHolderAddress =>
            UiWindow + (ulong)BotConstants.MemoryOffsets.CastSpeedBuffPtr;
        private static ulong CastSpeedValueAddress =>
            CastSpeedHolder + (ulong)BotConstants.MemoryOffsets.CastSpeedBuff;

        [Fact]
        public void ReadsTheVerifiedInactiveValueThroughTheUiWindowChain()
        {
            var memory = new MemoryFixture();
            memory.SetShort(CastSpeedValueAddress, unchecked((short)0xBF80));

            GameMemoryService service = memory.CreateService();

            Assert.True(service.TryGetCastSpeedBuff(out short value));
            Assert.Equal(unchecked((short)0xBF80), value);
            Assert.Equal(-16512, value);
            Assert.Equal(BotConstants.CastSpeed.BuffInactiveValue, value);
            Assert.False(service.IsCastSpeedBuffActive());

            Assert.Contains((UiWindowAddress, PointerSize), memory.ReadRequests);
            Assert.Contains((CastSpeedHolderAddress, PointerSize), memory.ReadRequests);
            Assert.Contains((CastSpeedValueAddress, sizeof(short)), memory.ReadRequests);
        }

        [Fact]
        public void AnyOtherReadableValueCountsAsActive()
        {
            var memory = new MemoryFixture();
            memory.SetShort(CastSpeedValueAddress, unchecked((short)0x3F80));

            GameMemoryService service = memory.CreateService();

            Assert.True(service.TryGetCastSpeedBuff(out short value));
            Assert.Equal(unchecked((short)0x3F80), value);
            Assert.True(service.IsCastSpeedBuffActive());
        }

        [Fact]
        public void UnreadableValueIsUnknownNotInactive()
        {
            var memory = new MemoryFixture();
            memory.FailRead(CastSpeedValueAddress);

            GameMemoryService service = memory.CreateService();

            Assert.False(service.TryGetCastSpeedBuff(out short value));
            Assert.Equal(0, value);
            Assert.Null(service.IsCastSpeedBuffActive());
        }

        [Fact]
        public void BrokenPointerChainIsUnknownAndNeverReadsPastTheBreak()
        {
            var memory = new MemoryFixture();
            memory.Set(CastSpeedHolderAddress, BitConverter.GetBytes(0u)); // second link null

            GameMemoryService service = memory.CreateService();

            Assert.False(service.TryGetCastSpeedBuff(out short value));
            Assert.Null(service.IsCastSpeedBuffActive());
            Assert.DoesNotContain(
                memory.ReadRequests,
                request => request.address == CastSpeedValueAddress);
        }

        [Fact]
        public void UnreadableUiWindowPointerIsUnknown()
        {
            var memory = new MemoryFixture();
            memory.FailRead(UiWindowAddress);

            GameMemoryService service = memory.CreateService();

            Assert.False(service.TryGetCastSpeedBuff(out short value));
            Assert.Null(service.IsCastSpeedBuffActive());

            // Each call resolves the UI window pointer first and stops there; the deeper
            // links of the chain are never touched.
            Assert.Equal(2, memory.ReadRequests.Count);
            Assert.All(memory.ReadRequests,
                request => Assert.Equal((UiWindowAddress, PointerSize), request));
        }

        private sealed class MemoryFixture
        {
            private readonly Dictionary<ulong, byte[]> _memory = new();
            private readonly HashSet<ulong> _failedReads = new();

            public List<(ulong address, int length)> ReadRequests { get; } = new();

            public MemoryFixture()
            {
                // Verified chain: [Ares.exe + 0x489BE8] -> UiWindow, [UiWindow + 0x178] -> holder.
                Set(UiWindowAddress, BitConverter.GetBytes((uint)UiWindow));
                Set(CastSpeedHolderAddress, BitConverter.GetBytes((uint)CastSpeedHolder));
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
