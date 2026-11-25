using DriverScanTester;
using DriverScanTester.Models;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// LOOT PRIORITY weight relief: before the weight-triggered town teleport the bot
    /// drinks surplus potions — white/red buff potions first, then mana down to 4, then
    /// HP down to 4 — and only repots when every stack is at its keep value.
    /// </summary>
    public sealed class LootPriorityWeightDumpTests
    {
        // ── Gate: every stack must be above its keep value ──

        [Theory]
        // All four stacks above the described limits → dump allowed.
        [InlineData(5, 5, 2, 2, true)]
        [InlineData(120, 99, 4, 4, true)]
        // HP must be MORE than 4.
        [InlineData(4, 50, 3, 3, false)]
        [InlineData(0, 50, 3, 3, false)]
        // Mana must be MORE than 4.
        [InlineData(50, 4, 3, 3, false)]
        // White must be MORE than 1.
        [InlineData(50, 50, 1, 3, false)]
        [InlineData(50, 50, 0, 3, false)]
        // Red must be MORE than 1.
        [InlineData(50, 50, 3, 1, false)]
        [InlineData(50, 50, 3, 0, false)]
        // Nothing at all (unreadable offsets).
        [InlineData(0, 0, 0, 0, false)]
        public void DumpOnlyStartsWhenEveryStackIsAboveItsKeepValue(
            int hp, int mana, int white, int red, bool expected)
        {
            var counts = new LootPriorityWeightDumpService.PotionCounts(hp, mana, white, red);
            Assert.Equal(expected, LootPriorityWeightDumpService.HasSurplus(counts));
        }

        // ── Drink order: buff → mana → HP is verified by the press sequence below ──

        [Fact]
        public async Task Dump_DrinksBuffsFirstThenManaThenHpAndKeepsTheReserves()
        {
            var simulation = new SimulatedPotions
            {
                HpPotions = 20,
                ManaPotions = 20,
                WhitePotions = 3,
                RedPotions = 3
            };
            var log = new List<string>();
            var service = new RecordingWeightDumpService(simulation, log.Add);

            bool dumped = await service.TryDumpSurplusAsync(CancellationToken.None);

            Assert.True(dumped);
            // Buff potions first (both keys, 3 → 1), then mana 20 → 4, then HP 20 → 4.
            Assert.Equal(1, simulation.WhitePotions);
            Assert.Equal(1, simulation.RedPotions);
            Assert.Equal(4, simulation.ManaPotions);
            Assert.Equal(4, simulation.HpPotions);

            int firstMana = service.Presses.IndexOf("Mana");
            int firstHp = service.Presses.IndexOf("Hp");
            Assert.True(firstMana > 0, "mana potions must be drunk after the buff potions");
            Assert.True(firstHp > firstMana, "HP potions must be drunk last");
            Assert.DoesNotContain(service.Presses.Take(firstMana), key => key != "Red" && key != "White");
            Assert.DoesNotContain(service.Presses.Skip(firstMana).Take(firstHp - firstMana), key => key != "Mana");
            Assert.DoesNotContain(service.Presses.Skip(firstHp), key => key != "Hp");
            Assert.Contains(log, line => line.Contains("[WeightDump] Dump finished"));
        }

        [Fact]
        public async Task Dump_IsSkippedWhenAnyStackIsAlreadyAtItsKeepValue()
        {
            var simulation = new SimulatedPotions
            {
                HpPotions = 50,
                ManaPotions = 50,
                WhitePotions = 1, // white at the keep value gates the whole dump
                RedPotions = 3
            };
            var log = new List<string>();
            var service = new RecordingWeightDumpService(simulation, log.Add);

            bool dumped = await service.TryDumpSurplusAsync(CancellationToken.None);

            Assert.False(dumped);
            Assert.Empty(service.Presses);
            Assert.Equal(50, simulation.HpPotions);
            Assert.Equal(50, simulation.ManaPotions);
            Assert.Equal(1, simulation.WhitePotions);
            Assert.Equal(3, simulation.RedPotions);
            Assert.Contains(log, line => line.Contains("no surplus to drink"));
        }

        [Fact]
        public async Task Dump_DoesNotSpamBuffKeysThatConsumeNothing()
        {
            var simulation = new SimulatedPotions
            {
                HpPotions = 20,
                ManaPotions = 20,
                WhitePotions = 3,
                RedPotions = 3
            };
            var log = new List<string>();
            var service = new RecordingWeightDumpService(simulation, log.Add)
            {
                WhiteKeyConsumes = false,
                RedKeyConsumes = false
            };

            bool dumped = await service.TryDumpSurplusAsync(CancellationToken.None);

            Assert.True(dumped);
            // Both buff keys are abandoned after the bounded stall count (3 presses each).
            Assert.Equal(3, service.Presses.Count(key => key == "Red"));
            Assert.Equal(3, service.Presses.Count(key => key == "White"));
            Assert.Equal(3, simulation.WhitePotions);
            Assert.Equal(3, simulation.RedPotions);
            // The drinkable stacks are still dumped to their keep values.
            Assert.Equal(4, simulation.ManaPotions);
            Assert.Equal(4, simulation.HpPotions);
            Assert.Contains(log, line => line.Contains("consumed nothing after 3 presses"));
        }

        [Fact]
        public async Task Dump_NeverDrinksTheLastBuffPotionOfEitherType()
        {
            var simulation = new SimulatedPotions
            {
                HpPotions = 20,
                ManaPotions = 20,
                WhitePotions = 3,
                RedPotions = 3
            };
            var log = new List<string>();
            // The white key consumes nothing (e.g. buff already active on that key), so only
            // red is drained. The buff round stops as soon as red reaches its keep value and
            // never presses the buff keys again, so no buff stack can drop below 1.
            var service = new RecordingWeightDumpService(simulation, log.Add) { WhiteKeyConsumes = false };

            await service.TryDumpSurplusAsync(CancellationToken.None);

            Assert.Equal(3, simulation.WhitePotions);
            Assert.Equal(1, simulation.RedPotions);
            Assert.Equal(4, simulation.ManaPotions);
            Assert.Equal(4, simulation.HpPotions);
        }

        [Fact]
        public async Task Dump_AbortsWhenThePlayerAppearsInTheCity()
        {
            var simulation = new SimulatedPotions
            {
                InCity = true,
                HpPotions = 20,
                ManaPotions = 20,
                WhitePotions = 3,
                RedPotions = 3
            };
            var log = new List<string>();
            var service = new RecordingWeightDumpService(simulation, log.Add);

            bool dumped = await service.TryDumpSurplusAsync(CancellationToken.None);

            Assert.False(dumped);
            Assert.Empty(service.Presses);
            Assert.Contains(log, line => line.Contains("Player is in the city during the potion dump"));
        }

        // ── The dump must not run when the profile limit would repot right after it ──

        [Theory]
        [InlineData(0, 0, true)]
        [InlineData(2, 2, true)]
        [InlineData(3, 3, true)]
        [InlineData(4, 2, false)]
        [InlineData(2, 4, false)]
        [InlineData(10, 10, false)]
        public void DumpIsSkippedWhenTheProfilePotionLimitWouldRepotRightAfterIt(
            int minHpPotions, int minManaPotions, bool expected)
        {
            Assert.Equal(expected, LootPriorityWeightDumpService.IsDumpUseful(minHpPotions, minManaPotions));
        }

        [Fact]
        public async Task Dump_AbortsImmediatelyWhenThePlayerIsDead()
        {
            var simulation = new SimulatedPotions
            {
                HpValue = 0,
                HpPotions = 20,
                ManaPotions = 20,
                WhitePotions = 3,
                RedPotions = 3
            };
            var log = new List<string>();
            var service = new RecordingWeightDumpService(simulation, log.Add);

            bool dumped = await service.TryDumpSurplusAsync(CancellationToken.None);

            Assert.False(dumped);
            Assert.Empty(service.Presses);
            Assert.Contains(log, line => line.Contains("Player death detected during the buff potion dump"));
        }

        // ── Repot reason classification (only a pure weight trigger may dump) ──

        [Fact]
        public void WeightTrigger_IsReportedSeparatelyFromLowPotionTriggers()
        {
            var detector = new RepotDetectorService(_ => { })
            {
                MinHpPotions = 2,
                MinManaPotions = 2,
                MaxWeightValue = 1600,
                WeightRepotEnabled = true
            };

            Assert.Equal(RepotReason.Weight, detector.Diagnose(Snapshot(hpPotions: 40, manaPotions: 40, weight: 1600)));
            Assert.Equal(RepotReason.None, detector.Diagnose(Snapshot(hpPotions: 40, manaPotions: 40, weight: 1599)));
            // A low potion stock wins over the weight trigger — that repot must never dump.
            Assert.Equal(RepotReason.LowHpPotions, detector.Diagnose(Snapshot(hpPotions: 2, manaPotions: 40, weight: 1600)));
            Assert.Equal(RepotReason.LowManaPotions, detector.Diagnose(Snapshot(hpPotions: 40, manaPotions: 2, weight: 1600)));
        }

        [Fact]
        public void WeightTrigger_IsIgnoredWithoutLootPriority()
        {
            var detector = new RepotDetectorService(_ => { })
            {
                MinHpPotions = 2,
                MinManaPotions = 2,
                MaxWeightValue = 1600,
                WeightRepotEnabled = false
            };

            Assert.False(detector.IsWeightRepotNeeded(Snapshot(40, 40, 1600)));
            Assert.Equal(RepotReason.None, detector.Diagnose(Snapshot(40, 40, 1600)));
        }

        private static GameSnapshot Snapshot(int hpPotions, int manaPotions, int weight) =>
            new(0f, 0f, 1, 1, false, hp: 5000, mana: 5000,
                hpPotions: hpPotions, manaPotions: manaPotions,
                currentWeight: weight, maxWeight: 1631,
                whitePotions: 4, redPotions: 4);

        /// <summary>
        /// Potion-count simulation backed by a real <see cref="GameMemoryService"/> read
        /// delegate, so the service reads exactly what the game memory would report.
        /// </summary>
        private sealed class SimulatedPotions
        {
            private const ulong ModuleBase = 0x100000;
            private const ulong PlayerBase = 0x200000;
            private const ulong PlayerRange = 0x2000;

            public short HpValue = 1000;
            public short ManaValue = 1000;
            public bool InCity;
            public int HpPotions = 4;
            public int ManaPotions = 4;
            public int WhitePotions = 1;
            public int RedPotions = 1;

            public GameMemoryService CreateMemory(Action<string> log)
                => new(0, Read, Write, ModuleBase, 8, log);

            private bool Read(uint pid, ulong address, byte[] buffer, out uint bytesRead)
            {
                bytesRead = 0;
                if (buffer.Length == 0)
                    return false;

                if (address == ModuleBase + BotConstants.MemoryOffsets.PlayerPtr)
                    return Copy(buffer, BitConverter.GetBytes(PlayerBase), out bytesRead);

                if (address < PlayerBase || address >= PlayerBase + PlayerRange)
                    return false;

                ulong offset = address - PlayerBase;
                byte[] value = offset switch
                {
                    BotConstants.MemoryOffsets.Hp => BitConverter.GetBytes(HpValue),
                    BotConstants.MemoryOffsets.Mana => BitConverter.GetBytes(ManaValue),
                    BotConstants.MemoryOffsets.IsInCity => BitConverter.GetBytes(InCity ? 1 : 0),
                    BotConstants.MemoryOffsets.MaxHp => BitConverter.GetBytes((short)2000),
                    BotConstants.MemoryOffsets.MaxMp => BitConverter.GetBytes((short)2000),
                    BotConstants.MemoryOffsets.InventorySlotHPCount => BitConverter.GetBytes((short)HpPotions),
                    BotConstants.MemoryOffsets.ManaPotionsCount => BitConverter.GetBytes((short)ManaPotions),
                    BotConstants.MemoryOffsets.WhitePotionsCount => BitConverter.GetBytes((short)WhitePotions),
                    BotConstants.MemoryOffsets.RedPotionsCount => BitConverter.GetBytes((short)RedPotions),
                    _ => Array.Empty<byte>()
                };
                return Copy(buffer, value, out bytesRead);
            }

            private static bool Write(uint pid, ulong address, byte[] buffer, out uint bytesWritten)
            {
                bytesWritten = (uint)buffer.Length;
                return true;
            }

            private static bool Copy(byte[] buffer, byte[] value, out uint bytesRead)
            {
                Array.Clear(buffer, 0, buffer.Length);
                int count = Math.Min(buffer.Length, value.Length);
                if (count > 0)
                    Array.Copy(value, buffer, count);
                bytesRead = (uint)buffer.Length;
                return true;
            }
        }

        /// <summary>
        /// Records drink keys and applies their simulated potion consumption.
        /// Key mapping does not matter for the dump: the buff round presses both keys.
        /// </summary>
        private sealed class RecordingWeightDumpService : LootPriorityWeightDumpService
        {
            private readonly SimulatedPotions _simulation;

            public List<string> Presses { get; } = new();

            /// <summary>False simulates a white-potion key that cannot be consumed (buff active).</summary>
            public bool WhiteKeyConsumes { get; init; } = true;

            /// <summary>False simulates a red-potion key that cannot be consumed.</summary>
            public bool RedKeyConsumes { get; init; } = true;

            public RecordingWeightDumpService(SimulatedPotions simulation, Action<string> log)
                : base(simulation.CreateMemory(log), log, (_, _) => Task.CompletedTask)
            {
                _simulation = simulation;
            }

            protected override void PressHpPotion()
            {
                Presses.Add("Hp");
                if (_simulation.HpPotions > 0)
                    _simulation.HpPotions--;
            }

            protected override void PressManaPotion()
            {
                Presses.Add("Mana");
                if (_simulation.ManaPotions > 0)
                    _simulation.ManaPotions--;
            }

            protected override void PressRedPotion()
            {
                Presses.Add("Red");
                if (RedKeyConsumes && _simulation.RedPotions > 0)
                    _simulation.RedPotions--;
            }

            protected override void PressWhitePotion()
            {
                Presses.Add("White");
                if (WhiteKeyConsumes && _simulation.WhitePotions > 0)
                    _simulation.WhitePotions--;
            }
        }
    }
}
