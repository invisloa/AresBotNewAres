using System;
using System.Collections.Generic;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Covers the money ("segel") read at <c>[Ares.exe + 0x489BC8] + 0xEBC</c>
    /// (lock-tool name: XMoneySegel, ValueType = CLong). The bot only observes the
    /// value: the balance read at profile start is the money zero point.
    /// </summary>
    public sealed class MoneySegelReadTests
    {
        private const uint Pid = 4343;
        private const int PointerSize = 4; // Ares.exe is a 32-bit client
        private const ulong ModuleBase = 0x400000;
        private const ulong PlayerBase = 0x2468A000;

        [Fact]
        public void MoneySegelOffsetMatchesTheLockToolAddress()
        {
            Assert.Equal(0xEBCUL, BotConstants.MemoryOffsets.MoneySegel);
            Assert.Equal(0x489BC8UL, BotConstants.MemoryOffsets.PlayerPtr);
        }

        [Fact]
        public void TryReadMoneySegel_ReadsTheIntAtTheVerifiedPlayerOffset()
        {
            var memory = new MemoryFixture();
            memory.SetPlayerBase(PlayerBase);
            memory.SetMoney(4321);

            GameMemoryService service = memory.CreateService();

            Assert.True(service.TryReadMoneySegel(out int money));
            Assert.Equal(4321, money);
            Assert.Contains(
                (PlayerBase + BotConstants.MemoryOffsets.MoneySegel, sizeof(int)),
                memory.ReadRequests);
        }

        [Fact]
        public void TryReadMoneySegel_ReturnsTheLiveBalanceIncludingZero()
        {
            var memory = new MemoryFixture();
            memory.SetPlayerBase(PlayerBase);
            memory.SetMoney(0);

            Assert.True(memory.CreateService().TryReadMoneySegel(out int money));
            Assert.Equal(0, money);
        }

        [Fact]
        public void TryReadMoneySegel_FailedReadIsReportedAsFailureNotAsZero()
        {
            var memory = new MemoryFixture();
            memory.SetPlayerBase(PlayerBase);
            memory.SetMoney(999);
            memory.FailRead(PlayerBase + BotConstants.MemoryOffsets.MoneySegel);

            Assert.False(memory.CreateService().TryReadMoneySegel(out int money));
            Assert.Equal(0, money);
        }

        [Fact]
        public void TryReadMoneySegel_UnreadablePlayerPointerNeverReadsTheMoneyValue()
        {
            var memory = new MemoryFixture();
            memory.FailRead(ModuleBase + BotConstants.MemoryOffsets.PlayerPtr);

            Assert.False(memory.CreateService().TryReadMoneySegel(out int money));
            Assert.Equal(0, money);
            Assert.Single(memory.ReadRequests);
            Assert.Equal(
                (ModuleBase + BotConstants.MemoryOffsets.PlayerPtr, PointerSize),
                memory.ReadRequests[0]);
        }

        private sealed class MemoryFixture
        {
            private readonly Dictionary<ulong, byte[]> _memory = new();
            private readonly HashSet<ulong> _failedReads = new();

            public List<(ulong address, int length)> ReadRequests { get; } = new();

            public void Set(ulong address, byte[] value) => _memory[address] = value;

            public void SetPlayerBase(ulong playerBase) =>
                Set(ModuleBase + BotConstants.MemoryOffsets.PlayerPtr, BitConverter.GetBytes((uint)playerBase));

            public void SetMoney(int value) =>
                Set(PlayerBase + BotConstants.MemoryOffsets.MoneySegel, BitConverter.GetBytes(value));

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
