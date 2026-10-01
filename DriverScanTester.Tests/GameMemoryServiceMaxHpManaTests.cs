using System;
using System.Collections.Generic;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    public sealed class GameMemoryServiceMaxHpManaTests
    {
        private const uint Pid = 123;
        private const int PointerSize = 8;
        private const ulong ModuleBase = 0x400000;
        private const ulong PlayerBase = 0x12345000;

        [Fact]
        public void GetMaxHpMana_ResolvesAuthoritativeAddressesAndReadsInt16Values()
        {
            var memory = new MemoryFixture();
            ulong playerPointerAddress = ModuleBase + BotConstants.MemoryOffsets.PlayerPtr;
            ulong maxHpAddress = PlayerBase + BotConstants.MemoryOffsets.MaxHp;
            ulong maxManaAddress = PlayerBase + BotConstants.MemoryOffsets.MaxMp;
            memory.Set(playerPointerAddress, BitConverter.GetBytes(PlayerBase));
            memory.Set(maxHpAddress, BitConverter.GetBytes((short)2450));
            memory.Set(maxManaAddress, BitConverter.GetBytes((short)980));

            MaxHpManaReadResult result = memory.CreateService().GetMaxHpMana();

            Assert.True(result.Success);
            Assert.Equal(ModuleBase, result.ModuleBase);
            Assert.Equal(playerPointerAddress, result.PlayerPointerAddress);
            Assert.True(result.PlayerPointerReadSucceeded);
            Assert.Equal(PlayerBase, result.PlayerBase);
            Assert.Equal(maxHpAddress, result.MaxHpAddress);
            Assert.True(result.MaxHpReadSucceeded);
            Assert.Equal(2450, result.MaxHp);
            Assert.Equal(maxManaAddress, result.MaxManaAddress);
            Assert.True(result.MaxManaReadSucceeded);
            Assert.Equal(980, result.MaxMana);
            Assert.Contains((playerPointerAddress, PointerSize), memory.ReadRequests);
            Assert.Contains((maxHpAddress, sizeof(short)), memory.ReadRequests);
            Assert.Contains((maxManaAddress, sizeof(short)), memory.ReadRequests);
        }

        [Fact]
        public void GetMaxHpMana_PreservesSuccessfulStatWhenTheOtherReadFails()
        {
            var memory = new MemoryFixture();
            ulong playerPointerAddress = ModuleBase + BotConstants.MemoryOffsets.PlayerPtr;
            ulong maxHpAddress = PlayerBase + BotConstants.MemoryOffsets.MaxHp;
            ulong maxManaAddress = PlayerBase + BotConstants.MemoryOffsets.MaxMp;
            memory.Set(playerPointerAddress, BitConverter.GetBytes(PlayerBase));
            memory.Set(maxHpAddress, BitConverter.GetBytes((short)2000));
            memory.Set(maxManaAddress, BitConverter.GetBytes((short)1000));
            memory.FailRead(maxHpAddress);

            MaxHpManaReadResult result = memory.CreateService().GetMaxHpMana();

            Assert.False(result.Success);
            Assert.False(result.MaxHpReadSucceeded);
            Assert.Equal(0, result.MaxHp);
            Assert.True(result.MaxManaReadSucceeded);
            Assert.Equal(1000, result.MaxMana);
        }

        [Fact]
        public void GetMaxHpMana_ReportsPointerReadFailureWithoutReadingPlayerStats()
        {
            var memory = new MemoryFixture();
            ulong playerPointerAddress = ModuleBase + BotConstants.MemoryOffsets.PlayerPtr;
            memory.FailRead(playerPointerAddress);

            MaxHpManaReadResult result = memory.CreateService().GetMaxHpMana();

            Assert.False(result.Success);
            Assert.False(result.PlayerPointerReadSucceeded);
            Assert.False(result.PlayerBaseResolved);
            Assert.False(result.MaxHpReadSucceeded);
            Assert.False(result.MaxManaReadSucceeded);
            Assert.Single(memory.ReadRequests);
            Assert.Equal((playerPointerAddress, PointerSize), memory.ReadRequests[0]);
        }

        [Fact]
        public void GetMaxHpMana_DistinguishesSuccessfullyReadZeroFromFailedRead()
        {
            var memory = new MemoryFixture();
            ulong playerPointerAddress = ModuleBase + BotConstants.MemoryOffsets.PlayerPtr;
            memory.Set(playerPointerAddress, BitConverter.GetBytes(PlayerBase));
            memory.Set(PlayerBase + BotConstants.MemoryOffsets.MaxHp, BitConverter.GetBytes((short)0));
            memory.Set(PlayerBase + BotConstants.MemoryOffsets.MaxMp, BitConverter.GetBytes((short)0));

            MaxHpManaReadResult result = memory.CreateService().GetMaxHpMana();

            Assert.True(result.Success);
            Assert.True(result.MaxHpReadSucceeded);
            Assert.True(result.MaxManaReadSucceeded);
            Assert.Equal(0, result.MaxHp);
            Assert.Equal(0, result.MaxMana);
        }

        private sealed class MemoryFixture
        {
            private readonly Dictionary<ulong, byte[]> _memory = new();
            private readonly HashSet<ulong> _failedReads = new();

            public List<(ulong address, int length)> ReadRequests { get; } = new();

            public void Set(ulong address, byte[] value) => _memory[address] = value;

            public void FailRead(ulong address) => _failedReads.Add(address);

            public GameMemoryService CreateService()
            {
                GameMemoryService.ReadMemoryDelegate read =
                    (uint pid, ulong address, byte[] buffer, out uint bytesRead) =>
                    {
                        ReadRequests.Add((address, buffer.Length));
                        bytesRead = 0;
                        if (_failedReads.Contains(address) || !_memory.TryGetValue(address, out byte[]? value) || value.Length < buffer.Length)
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
