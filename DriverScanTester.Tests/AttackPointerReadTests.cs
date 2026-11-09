using System;
using System.Collections.Generic;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// The connection watchdog uses A_attack1
    /// (<c>[Ares.exe + 0x489BC8] + 0x47A</c>) as its crash heartbeat. The read must
    /// distinguish a real value from a broken pointer chain / failed read ("?").
    /// </summary>
    public sealed class AttackPointerReadTests
    {
        private const uint Pid = 123;
        private const int PointerSize = 8;
        private const ulong ModuleBase = 0x400000;
        private const ulong PlayerBase = 0x30000000;

        private static ulong PlayerPointerAddress
            => ModuleBase + BotConstants.MemoryOffsets.PlayerPtr;

        private static ulong AttackValueAddress
            => PlayerBase + BotConstants.MemoryOffsets.AttackSpeed1;

        [Fact]
        public void TryGetAttackSpeedReadsTheAttackPointerValue()
        {
            MemoryFixture memory = CreateValidFixture();
            memory.Set(AttackValueAddress, BitConverter.GetBytes((short)1234));

            Assert.True(memory.CreateService().TryGetAttackSpeed(out short attackSpeed));
            Assert.Equal(1234, attackSpeed);
        }

        [Fact]
        public void TryGetAttackSpeedAcceptsARealZeroValue()
        {
            MemoryFixture memory = CreateValidFixture();
            memory.Set(AttackValueAddress, BitConverter.GetBytes((short)0));

            Assert.True(memory.CreateService().TryGetAttackSpeed(out short attackSpeed));
            Assert.Equal(0, attackSpeed);
        }

        [Fact]
        public void TryGetAttackSpeedReportsAFailedValueRead()
        {
            MemoryFixture memory = CreateValidFixture();
            memory.Set(AttackValueAddress, BitConverter.GetBytes((short)1234));
            memory.FailRead(AttackValueAddress);

            GameMemoryService service = memory.CreateService();

            Assert.False(service.TryGetAttackSpeed(out short attackSpeed));
            Assert.Equal(0, attackSpeed);
            Assert.Equal(0, service.GetAttackSpeed());
        }

        [Fact]
        public void TryGetAttackSpeedReportsABrokenPlayerPointerChain()
        {
            var memory = new MemoryFixture();

            Assert.False(memory.CreateService().TryGetAttackSpeed(out short attackSpeed));
            Assert.Equal(0, attackSpeed);
        }

        private static MemoryFixture CreateValidFixture()
        {
            var memory = new MemoryFixture();
            memory.Set(PlayerPointerAddress, BitConverter.GetBytes(PlayerBase));
            return memory;
        }

        private sealed class MemoryFixture
        {
            private readonly Dictionary<ulong, byte[]> _memory = new();
            private readonly HashSet<ulong> _failedReads = new();

            public void Set(ulong address, byte[] value) => _memory[address] = value;

            public void FailRead(ulong address) => _failedReads.Add(address);

            public GameMemoryService CreateService()
            {
                GameMemoryService.ReadMemoryDelegate read =
                    (uint pid, ulong address, byte[] buffer, out uint bytesRead) =>
                    {
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
