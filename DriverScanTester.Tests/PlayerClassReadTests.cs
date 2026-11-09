using System;
using System.Collections.Generic;
using DriverScanTester.Models;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Covers the current-class read at
    /// <c>[Ares.exe + 0x489BC8] + 0xA9C</c> (M_CurrentCLassPlayer):
    /// 0 = Knight, 1 = Archer, 2 = Mage/Sorcerer, 3 = Spear.
    /// </summary>
    public sealed class PlayerClassReadTests
    {
        private const uint Pid = 4242;
        private const int PointerSize = 4; // Ares.exe is a 32-bit client
        private const ulong ModuleBase = 0x400000;
        private const ulong PlayerBase = 0x12345000;

        [Fact]
        public void TryGetPlayerClass_ReadsTheIntAtTheVerifiedPlayerOffset()
        {
            var memory = new MemoryFixture();
            memory.SetPlayerBase(PlayerBase);
            memory.SetClassValue(BotConstants.PlayerClassIds.Archer);

            GameMemoryService service = memory.CreateService();
            bool success = service.TryGetPlayerClass(out int classId);

            Assert.True(success);
            Assert.Equal(1, classId);
            Assert.Equal(PlayerClass.Archer, service.GetPlayerClass());
            Assert.Contains(
                (PlayerBase + BotConstants.MemoryOffsets.CurrentClassPlayer, sizeof(int)),
                memory.ReadRequests);
        }

        [Theory]
        [InlineData(BotConstants.PlayerClassIds.Knight, PlayerClass.Knight)]
        [InlineData(BotConstants.PlayerClassIds.Archer, PlayerClass.Archer)]
        [InlineData(BotConstants.PlayerClassIds.Mage, PlayerClass.Mage)]
        [InlineData(BotConstants.PlayerClassIds.Spear, PlayerClass.Spear)]
        public void GetPlayerClass_MapsEveryKnownValue(int rawValue, PlayerClass expected)
        {
            var memory = new MemoryFixture();
            memory.SetPlayerBase(PlayerBase);
            memory.SetClassValue(rawValue);

            Assert.Equal(expected, memory.CreateService().GetPlayerClass());
        }

        [Fact]
        public void GetPlayerClass_UnrecognizedValueIsUnknownNotKnight()
        {
            var memory = new MemoryFixture();
            memory.SetPlayerBase(PlayerBase);
            memory.SetClassValue(7);

            GameMemoryService service = memory.CreateService();

            Assert.Equal(PlayerClass.Unknown, service.GetPlayerClass());
            Assert.True(service.TryGetPlayerClass(out int rawValue));
            Assert.Equal(7, rawValue);
        }

        [Fact]
        public void TryGetPlayerClass_FailedClassReadReportsUnknownInsteadOfZero()
        {
            var memory = new MemoryFixture();
            memory.SetPlayerBase(PlayerBase);
            memory.SetClassValue(BotConstants.PlayerClassIds.Knight);
            memory.FailRead(PlayerBase + BotConstants.MemoryOffsets.CurrentClassPlayer);

            GameMemoryService service = memory.CreateService();

            Assert.False(service.TryGetPlayerClass(out int classId));
            Assert.Equal(BotConstants.PlayerClassIds.Unknown, classId);
            Assert.Equal(PlayerClass.Unknown, service.GetPlayerClass());
        }

        [Fact]
        public void TryGetPlayerClass_UnreadablePlayerPointerNeverReadsTheClassValue()
        {
            var memory = new MemoryFixture();
            memory.FailRead(ModuleBase + BotConstants.MemoryOffsets.PlayerPtr);

            GameMemoryService service = memory.CreateService();

            Assert.False(service.TryGetPlayerClass(out int classId));
            Assert.Equal(BotConstants.PlayerClassIds.Unknown, classId);
            Assert.Equal(PlayerClass.Unknown, service.GetPlayerClass());

            // Each call resolves the player pointer first; the class offset is never read.
            Assert.Equal(2, memory.ReadRequests.Count);
            Assert.All(memory.ReadRequests,
                request => Assert.Equal((ModuleBase + BotConstants.MemoryOffsets.PlayerPtr, PointerSize), request));
        }

        private sealed class MemoryFixture
        {
            private readonly Dictionary<ulong, byte[]> _memory = new();
            private readonly HashSet<ulong> _failedReads = new();

            public List<(ulong address, int length)> ReadRequests { get; } = new();

            public void Set(ulong address, byte[] value) => _memory[address] = value;

            public void SetPlayerBase(ulong playerBase) =>
                Set(ModuleBase + BotConstants.MemoryOffsets.PlayerPtr, BitConverter.GetBytes((uint)playerBase));

            public void SetClassValue(int value) =>
                Set(PlayerBase + BotConstants.MemoryOffsets.CurrentClassPlayer, BitConverter.GetBytes(value));

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
