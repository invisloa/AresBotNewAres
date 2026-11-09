using System;
using System.Collections.Generic;
using DriverScanTester.Models;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Sorcerer/Mage rebuff condition, evaluated on the same throttled cycle as the
    /// white/red speed-potion check:
    ///   • only outside the city,
    ///   • reads all six 16-bit buff slots and the rapid buff counts as active when ANY
    ///     slot equals <see cref="BotConstants.Rebuff.RapidBuffActiveValue"/> (3384 / 0x0D38),
    ///   • rebuffs only when every readable slot holds a different value,
    ///   • an unreadable slot makes the state unknown, so no buff keys are pressed,
    ///   • no other class has a rebuff condition yet.
    /// The action itself (key 4 twice, ~500 ms apart) is performed by
    /// <see cref="MovementSystem.PerformRebuffAsync"/>.
    /// </summary>
    public sealed class MageRebuffTests
    {
        private const uint Pid = 999;
        private const int PointerSize = 4; // Ares.exe is a 32-bit client
        private const ulong ModuleBase = 0x400000;
        private const ulong PlayerBase = 0x100000;
        private const ulong UiWindow = 0x21000000;
        private const ulong BuffHolder = 0x22000000;

        private static ulong UiWindowAddress => ModuleBase + BotConstants.MemoryOffsets.UiWindowM;
        private static ulong HolderPointerAddress =>
            UiWindow + (ulong)BotConstants.MemoryOffsets.BuffSlotsPtr;

        private static ulong SlotAddress(int slotIndex) =>
            BuffHolder
            + (ulong)BotConstants.MemoryOffsets.FirstBuffSlot
            + (ulong)(slotIndex * BotConstants.MemoryOffsets.BuffSlotSize);

        [Fact]
        public void Mage_NoRapidBuffValue_OutsideCity_Rebuffs()
        {
            var memory = new MemoryFixture { InCity = false };
            var combat = new CombatHandler(_ => { });

            Assert.True(combat.CheckRebuff(memory.CreateService(), PlayerClass.Mage));
            Assert.False(combat.LastRapidBuffActive);
            Assert.Equal(
                BotConstants.MemoryOffsets.BuffSlotCount,
                combat.LastBuffSlotValues.Length);
            Assert.All(combat.LastBuffSlotValues, value => Assert.Equal((short)0, value));
        }

        [Fact]
        public void Mage_RapidBuffInFirstSlot_DoesNotRebuff()
        {
            var memory = new MemoryFixture { InCity = false };
            memory.SetSlot(0, BotConstants.Rebuff.RapidBuffActiveValue);
            var combat = new CombatHandler(_ => { });

            Assert.False(combat.CheckRebuff(memory.CreateService(), PlayerClass.Mage));
            Assert.True(combat.LastRapidBuffActive);
            Assert.Equal(
                BotConstants.Rebuff.RapidBuffActiveValue,
                combat.LastBuffSlotValues[0]);
        }

        [Fact]
        public void Mage_RapidBuffInLastSlot_DoesNotRebuff()
        {
            var memory = new MemoryFixture { InCity = false };
            memory.SetSlot(5, BotConstants.Rebuff.RapidBuffActiveValue);
            var combat = new CombatHandler(_ => { });

            Assert.False(combat.CheckRebuff(memory.CreateService(), PlayerClass.Mage));
            Assert.True(combat.LastRapidBuffActive);
            Assert.Equal(
                BotConstants.Rebuff.RapidBuffActiveValue,
                combat.LastBuffSlotValues[5]);
        }

        [Fact]
        public void Mage_OtherValuesOnly_Rebuffs()
        {
            var memory = new MemoryFixture { InCity = false };
            short[] values = { 0, -16512, 1000, 3383, 3385, 42 };
            for (int i = 0; i < values.Length; i++)
                memory.SetSlot(i, values[i]);

            var combat = new CombatHandler(_ => { });

            Assert.True(combat.CheckRebuff(memory.CreateService(), PlayerClass.Mage));
            Assert.False(combat.LastRapidBuffActive);
            Assert.Equal(values, combat.LastBuffSlotValues);
        }

        [Fact]
        public void Mage_InactiveBuff_InsideCity_NeverRebuffs()
        {
            var memory = new MemoryFixture { InCity = true };
            var combat = new CombatHandler(_ => { });

            Assert.False(combat.CheckRebuff(memory.CreateService(), PlayerClass.Mage));

            // The city gate is checked first — the buff slots are not even read.
            Assert.DoesNotContain(memory.ReadRequests, r => r.address == SlotAddress(0));
        }

        [Theory]
        [InlineData(PlayerClass.Knight)]
        [InlineData(PlayerClass.Archer)]
        [InlineData(PlayerClass.Spear)]
        [InlineData(PlayerClass.Unknown)]
        public void OtherClasses_HaveNoRebuffConditionYet(PlayerClass playerClass)
        {
            var memory = new MemoryFixture { InCity = false };
            var combat = new CombatHandler(_ => { });

            Assert.False(combat.CheckRebuff(memory.CreateService(), playerClass));
        }

        [Fact]
        public void Mage_UnreadableBuffSlot_NeverRebuffs()
        {
            var memory = new MemoryFixture { InCity = false };
            memory.FailSlot(2);
            var combat = new CombatHandler(_ => { });

            // "No slot equals 3384" cannot be concluded from a partial read.
            Assert.False(combat.CheckRebuff(memory.CreateService(), PlayerClass.Mage));
        }

        [Fact]
        public void Mage_UnreadableBuffSlot_HidesMatchingValue_AndNeverRebuffs()
        {
            var memory = new MemoryFixture { InCity = false };
            memory.SetSlot(5, BotConstants.Rebuff.RapidBuffActiveValue);
            memory.FailSlot(2);
            var combat = new CombatHandler(_ => { });

            Assert.False(combat.CheckRebuff(memory.CreateService(), PlayerClass.Mage));
        }

        [Fact]
        public void Mage_UnreadableCityFlag_NeverRebuffs()
        {
            var memory = new MemoryFixture { CityFlagUnreadable = true };
            var combat = new CombatHandler(_ => { });

            Assert.False(combat.CheckRebuff(memory.CreateService(), PlayerClass.Mage));

            // An unknown city state is not permission to press buff keys.
            Assert.DoesNotContain(memory.ReadRequests, r => r.address == SlotAddress(0));
        }

        [Fact]
        public void RebuffCheck_IsThrottledToTheSpeedPotionInterval()
        {
            var memory = new MemoryFixture { InCity = false };
            GameMemoryService service = memory.CreateService();
            var combat = new CombatHandler(_ => { });

            Assert.True(combat.CheckRebuff(service, PlayerClass.Mage));

            // Immediately after a check the rebuff must stay silent: one rebuff per cycle,
            // exactly like the potion keys.
            Assert.False(combat.CheckRebuff(service, PlayerClass.Mage));
            Assert.False(combat.CheckRebuff(service, PlayerClass.Mage));
        }

        private sealed class MemoryFixture
        {
            private readonly Dictionary<ulong, byte[]> _memory = new();
            private readonly HashSet<ulong> _failedReads = new();
            private readonly HashSet<int> _unreadableSlots = new();

            public List<(ulong address, int length)> ReadRequests { get; } = new();

            public bool InCity;
            public bool CityFlagUnreadable;

            private ulong CityFlagAddress => PlayerBase + BotConstants.MemoryOffsets.IsInCity;

            public MemoryFixture()
            {
                Set(ModuleBase + BotConstants.MemoryOffsets.PlayerPtr,
                    BitConverter.GetBytes((uint)PlayerBase));
                Set(UiWindowAddress, BitConverter.GetBytes((uint)UiWindow));
                Set(HolderPointerAddress, BitConverter.GetBytes((uint)BuffHolder));

                // Start with all six slots readable and empty (no active buff).
                for (int i = 0; i < BotConstants.MemoryOffsets.BuffSlotCount; i++)
                    SetSlot(i, 0);
            }

            public void Set(ulong address, byte[] value) => _memory[address] = value;

            public void SetSlot(int slotIndex, short value) =>
                Set(SlotAddress(slotIndex), BitConverter.GetBytes(value));

            public void FailSlot(int slotIndex) => _unreadableSlots.Add(slotIndex);

            private void ApplyFailureState()
            {
                Set(CityFlagAddress, BitConverter.GetBytes(InCity ? 1 : 0));
                if (CityFlagUnreadable) _failedReads.Add(CityFlagAddress);
                else _failedReads.Remove(CityFlagAddress);

                for (int i = 0; i < BotConstants.MemoryOffsets.BuffSlotCount; i++)
                {
                    ulong address = SlotAddress(i);
                    if (_unreadableSlots.Contains(i)) _failedReads.Add(address);
                    else _failedReads.Remove(address);
                }
            }

            public GameMemoryService CreateService()
            {
                ApplyFailureState();

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
