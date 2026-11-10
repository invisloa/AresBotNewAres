using System;
using System.Collections.Generic;
using DriverScanTester.Models;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Sorcerer/Mage drinks only the white speed potion (key 7); every other class keeps
    /// the two-key rebuff (key 6 = red, key 7 = white) inherited from the legacy bot
    /// (SkillSelectorMageAlliance used checkForWhitePotBuff, the rest checkForWhiteRed).
    /// </summary>
    public sealed class SpeedPotionClassTests
    {
        private const int Reserve = BotConstants.Repot.PotionSlotReserve;

        [Fact]
        public void Mage_UsesOnlyTheWhitePotion()
        {
            MovementSystem.SpeedPotionPlan plan = MovementSystem.PlanSpeedPotions(PlayerClass.Mage, 10, 10);

            Assert.False(plan.PressRed);
            Assert.True(plan.PressWhite);
            Assert.False(plan.BlockedByReserve);
            Assert.True(plan.WhitePotionOnly);
        }

        [Fact]
        public void Mage_RedPotionCountNeverBlocksTheWhitePotion()
        {
            // A mage does not drink red pots, so a zero/one red count (the slot reserve)
            // must not suppress the white potion.
            Assert.False(MovementSystem.PlanSpeedPotions(PlayerClass.Mage, 0, 10).BlockedByReserve);
            Assert.False(MovementSystem.PlanSpeedPotions(PlayerClass.Mage, Reserve, 10).BlockedByReserve);
            Assert.False(MovementSystem.PlanSpeedPotions(PlayerClass.Mage, Reserve, 10).PressRed);
        }

        [Theory]
        [InlineData(Reserve)]
        [InlineData(0)]
        public void Mage_WhitePotionsAtReserveBlockTheRebuff(int whitePots)
        {
            MovementSystem.SpeedPotionPlan plan = MovementSystem.PlanSpeedPotions(PlayerClass.Mage, 50, whitePots);

            Assert.True(plan.BlockedByReserve);
            Assert.False(plan.PressRed);
            Assert.False(plan.PressWhite);
        }

        [Theory]
        [InlineData(PlayerClass.Knight)]
        [InlineData(PlayerClass.Archer)]
        [InlineData(PlayerClass.Spear)]
        [InlineData(PlayerClass.Unknown)]
        public void EveryOtherClass_KeepsBothPotions(PlayerClass playerClass)
        {
            MovementSystem.SpeedPotionPlan plan = MovementSystem.PlanSpeedPotions(playerClass, 10, 10);

            Assert.True(plan.PressRed);
            Assert.True(plan.PressWhite);
            Assert.False(plan.BlockedByReserve);
            Assert.False(plan.WhitePotionOnly);
        }

        [Theory]
        [InlineData(PlayerClass.Knight)]
        [InlineData(PlayerClass.Archer)]
        [InlineData(PlayerClass.Spear)]
        [InlineData(PlayerClass.Unknown)]
        public void EveryOtherClass_ReserveOnEitherTypeStillSkipsBothKeys(PlayerClass playerClass)
        {
            Assert.True(MovementSystem.PlanSpeedPotions(playerClass, Reserve, 10).BlockedByReserve);
            Assert.True(MovementSystem.PlanSpeedPotions(playerClass, 10, Reserve).BlockedByReserve);
            Assert.False(MovementSystem.PlanSpeedPotions(playerClass, Reserve + 1, Reserve + 1).BlockedByReserve);
        }

        // ── class resolution (cached so one bad read cannot flip the behaviour) ──

        [Fact]
        public void ResolvePlayerClass_ReturnsTheClassReadFromMemory()
        {
            var memory = new Memory { ClassValue = BotConstants.PlayerClassIds.Mage };
            var rig = new Rig(memory.Create());

            Assert.Equal(PlayerClass.Mage, rig.ResolvePlayerClassForPotions());
        }

        [Fact]
        public void ResolvePlayerClass_KeepsTheLastConfirmedClassWhenTheReadFails()
        {
            var memory = new Memory { ClassValue = BotConstants.PlayerClassIds.Mage };
            var rig = new Rig(memory.Create());

            Assert.Equal(PlayerClass.Mage, rig.ResolvePlayerClassForPotions());

            memory.ClassValue = null; // transient unreadable sample
            Assert.Equal(PlayerClass.Mage, rig.ResolvePlayerClassForPotions());
            Assert.Equal(PlayerClass.Mage, rig.ResolvePlayerClassForPotions());
        }

        [Fact]
        public void ResolvePlayerClass_IsUnknownUntilAClassCanBeRead()
        {
            var memory = new Memory { ClassValue = null };
            var rig = new Rig(memory.Create());

            Assert.Equal(PlayerClass.Unknown, rig.ResolvePlayerClassForPotions());

            memory.ClassValue = BotConstants.PlayerClassIds.Spear;
            Assert.Equal(PlayerClass.Spear, rig.ResolvePlayerClassForPotions());
        }

        // ── sorcerer trigger: run speed instead of attack speed ──

        [Fact]
        public void MageTrigger_DrinksWhileRunSpeedIsTheNoPotionValue()
        {
            var memory = new SpeedMemory { RunSpeed = BotConstants.SpeedPotion.RunSpeedNoPotionValue };
            var combat = new CombatHandler(_ => { });

            Assert.True(combat.CheckRunSpeed(memory.Create()));
            Assert.Equal(BotConstants.SpeedPotion.RunSpeedNoPotionValue, combat.LastRunSpeed);
        }

        [Fact]
        public void MageTrigger_DoesNotDrinkWhenRunSpeedDiffersFrom179()
        {
            // The post-potion value is irrelevant: ONLY 179 asks for the white potion.
            var memory = new SpeedMemory { RunSpeed = 0xCD };
            var combat = new CombatHandler(_ => { });

            Assert.False(combat.CheckRunSpeed(memory.Create()));
            Assert.Equal(0xCD, combat.LastRunSpeed);
        }

        [Fact]
        public void MageTrigger_UnreadableRunSpeedNeverSpamsTheWhitePotion()
        {
            var memory = new SpeedMemory { RunSpeedUnreadable = true };
            var combat = new CombatHandler(_ => { });

            Assert.False(combat.CheckRunSpeed(memory.Create()));
            Assert.Equal(0, combat.LastRunSpeed);
        }

        [Fact]
        public void MageTrigger_IsThrottledToTheSpeedPotionInterval()
        {
            var memory = new SpeedMemory { RunSpeed = BotConstants.SpeedPotion.RunSpeedNoPotionValue };
            var combat = new CombatHandler(_ => { });

            Assert.True(combat.CheckRunSpeed(memory.Create()));

            // Immediately after a check the trigger must stay silent even though the next
            // sample (179, if it were read) would ask for another potion.
            Assert.False(combat.CheckRunSpeed(memory.Create()));
            Assert.False(combat.CheckRunSpeed(memory.Create()));
        }

        [Fact]
        public void MageTrigger_UnknownRunSpeedValueIsTreatedAsBuffActive()
        {
            // Only 179 means "drink". Anything else — including the legacy 128/153 values
            // or a value produced by run-speed gear — must not fire the key.
            var memory = new SpeedMemory { RunSpeed = 128 };
            var combat = new CombatHandler(_ => { });

            Assert.False(combat.CheckRunSpeed(memory.Create()));
            Assert.Equal(128, combat.LastRunSpeed);
        }

        [Fact]
        public void RunSpeedConstants_MatchTheVerifiedLiveAddressAndValues()
        {
            Assert.Equal(0xEFAUL, BotConstants.MemoryOffsets.RunSpeed);
            Assert.Equal(0xB3, BotConstants.SpeedPotion.RunSpeedNoPotionValue); // 179 = +0 run speed
        }

        [Fact]
        public void GetRunSpeed_ReadsTheByteAtTheRunSpeedOffset()
        {
            const byte runSpeedValue = 0xB3;
            var memory = new SpeedMemory { RunSpeed = runSpeedValue };
            GameMemoryService service = memory.Create();

            Assert.Equal(runSpeedValue, service.GetRunSpeed());
            Assert.True(service.TryGetRunSpeed(out int runSpeed));
            Assert.Equal(runSpeedValue, runSpeed);
            Assert.Contains((PlayerBase + BotConstants.MemoryOffsets.RunSpeed, sizeof(byte)), memory.ReadRequests);
        }

        [Fact]
        public void GetRunSpeed_FailedReadIsZeroAndTryVariantReportsFailure()
        {
            var memory = new SpeedMemory { RunSpeedUnreadable = true };
            GameMemoryService service = memory.Create();

            Assert.Equal(0, service.GetRunSpeed());
            Assert.False(service.TryGetRunSpeed(out int runSpeed));
            Assert.Equal(0, runSpeed);
        }

        private sealed class Rig : MovementSystem
        {
            public Rig(GameMemoryService memory) : base(memory, _ => { }, 5000, 5000,
                MovementPrecision.Medium, customPath: new List<Waypoint> {
                    new Waypoint(5000, 5000, MovementPrecision.Medium, BotMode.MoveAndAttack) },
                initialMode: BotMode.MoveAndAttack, loopPath: true,
                targetSafety: CombatSafetyFixtures.WithPresence(
                    () => 0, new StubCombatPlayerPresenceScanner())) { }

            protected override void SendSkillThreeKeyEvent(bool keyUp) { }
            protected override void SendSyntheticForwardKeyEvent(bool keyUp) { }
            protected override void SendSyntheticBackwardKeyEvent(bool keyUp) { }
        }

        private sealed class Memory
        {
            /// <summary>Class id stored at playerBase + 0xA9C; null = unreadable.</summary>
            public int? ClassValue = BotConstants.PlayerClassIds.Archer;

            public GameMemoryService Create()
            {
                GameMemoryService.ReadMemoryDelegate read =
                    (uint pid, ulong address, byte[] buffer, out uint count) =>
                    {
                        byte[]? value = address switch
                        {
                            0x489BC8 => BitConverter.GetBytes(0x100000UL),
                            0x100A9C => ClassValue.HasValue ? BitConverter.GetBytes(ClassValue.Value) : null,
                            _ => null
                        };

                        count = 0;
                        if (value == null || value.Length < buffer.Length) return false;

                        Buffer.BlockCopy(value, 0, buffer, 0, buffer.Length);
                        count = (uint)buffer.Length;
                        return true;
                    };

                GameMemoryService.WriteMemoryDelegate write =
                    (uint pid, ulong address, byte[] buffer, out uint count) => { count = 0; return false; };

                return new GameMemoryService(0, read, write, 0, 8, _ => { });
            }
        }

        private const ulong PlayerBase = 0x100000;

        /// <summary>
        /// Memory stub for the sorcerer run-speed trigger: player pointer + run-speed BYTE
        /// (+0xEFA) and attack speed (+0x47A), with an explicit unreadable switch.
        /// </summary>
        private sealed class SpeedMemory
        {
            public byte RunSpeed = BotConstants.SpeedPotion.RunSpeedNoPotionValue;
            public bool RunSpeedUnreadable;

            public List<(ulong address, int length)> ReadRequests { get; } = new();

            public GameMemoryService Create()
            {
                GameMemoryService.ReadMemoryDelegate read =
                    (uint pid, ulong address, byte[] buffer, out uint count) =>
                    {
                        ReadRequests.Add((address, buffer.Length));
                        byte[]? value = address switch
                        {
                            0x489BC8 => BitConverter.GetBytes(PlayerBase),
                            PlayerBase + 0xEFA => RunSpeedUnreadable ? null : new[] { RunSpeed },
                            _ => null
                        };

                        count = 0;
                        if (value == null || value.Length < buffer.Length) return false;

                        Buffer.BlockCopy(value, 0, buffer, 0, buffer.Length);
                        count = (uint)buffer.Length;
                        return true;
                    };

                GameMemoryService.WriteMemoryDelegate write =
                    (uint pid, ulong address, byte[] buffer, out uint count) => { count = 0; return false; };

                return new GameMemoryService(0, read, write, 0, 8, _ => { });
            }
        }
    }
}
