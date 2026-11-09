using System;
using System.Collections.Generic;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// The slow NPC discovery scan must be able to tell a failed memory read apart from a
    /// successfully read "nothing is under the cursor" (0). These tests use the same fake
    /// driver as the other memory tests.
    /// </summary>
    public sealed class MouseOverMemoryReadTests
    {
        private const uint Pid = 123;
        private const int PointerSize = 8;
        private const ulong ModuleBase = 0x400000;
        private const ulong SellerHoverBuffer = 0x20000000;
        private const ulong NpcHighlightBuffer = 0x20001000;

        private static ulong SellerValueAddress
            => SellerHoverBuffer + BotConstants.MemoryOffsets.IsSellerPointed;

        private static ulong NpcHighlightAddress
            => NpcHighlightBuffer + BotConstants.MemoryOffsets.IsNpcMousePointed;

        private static MemoryFixture CreateHoverFixture()
        {
            var memory = new MemoryFixture();
            memory.Set(ModuleBase + BotConstants.MemoryOffsets.IsSellerPointedPtr,
                BitConverter.GetBytes(SellerHoverBuffer));
            memory.Set(ModuleBase + BotConstants.MemoryOffsets.IsNpcMousePointedPtr,
                BitConverter.GetBytes(NpcHighlightBuffer));
            return memory;
        }

        [Fact]
        public void TryReadIsSellerPointedReportsTheHoveredObjectsValue()
        {
            MemoryFixture memory = CreateHoverFixture();
            memory.Set(SellerValueAddress, BitConverter.GetBytes(138599240));

            Assert.True(memory.CreateService().TryReadIsSellerPointed(out int value));
            Assert.Equal(138599240, value);
        }

        [Fact]
        public void TryReadIsSellerPointedReturnsTrueForARealZeroValue()
        {
            MemoryFixture memory = CreateHoverFixture();
            memory.Set(SellerValueAddress, BitConverter.GetBytes(0));

            Assert.True(memory.CreateService().TryReadIsSellerPointed(out int value));
            Assert.Equal(0, value);
        }

        [Fact]
        public void TryReadIsSellerPointedReportsAFailedValueRead()
        {
            MemoryFixture memory = CreateHoverFixture();
            memory.Set(SellerValueAddress, BitConverter.GetBytes(138599240));
            memory.FailRead(SellerValueAddress);

            Assert.False(memory.CreateService().TryReadIsSellerPointed(out int value));
            Assert.Equal(0, value);
        }

        [Fact]
        public void TryReadIsSellerPointedReportsABrokenPointerChain()
        {
            var memory = new MemoryFixture();

            Assert.False(memory.CreateService().TryReadIsSellerPointed(out int value));
            Assert.Equal(0, value);
        }

        [Fact]
        public void TryReadIsNpcMousePointedReportsTheFlagState()
        {
            MemoryFixture pointed = CreateHoverFixture();
            pointed.Set(NpcHighlightAddress, new byte[] { 1 });
            Assert.True(pointed.CreateService().TryReadIsNpcMousePointed(out bool isPointed));
            Assert.True(isPointed);

            MemoryFixture notPointed = CreateHoverFixture();
            notPointed.Set(NpcHighlightAddress, new byte[] { 0 });
            Assert.True(notPointed.CreateService().TryReadIsNpcMousePointed(out bool isNotPointed));
            Assert.False(isNotPointed);
        }

        [Fact]
        public void TryReadIsNpcMousePointedReportsFailedReads()
        {
            MemoryFixture failedFlagRead = CreateHoverFixture();
            failedFlagRead.Set(NpcHighlightAddress, new byte[] { 1 });
            failedFlagRead.FailRead(NpcHighlightAddress);
            Assert.False(failedFlagRead.CreateService().TryReadIsNpcMousePointed(out bool pointed));
            Assert.False(pointed);

            var brokenChain = new MemoryFixture();
            Assert.False(brokenChain.CreateService().TryReadIsNpcMousePointed(out bool chainPointed));
            Assert.False(chainPointed);
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
