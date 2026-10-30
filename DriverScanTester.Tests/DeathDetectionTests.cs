using System;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Death-detector contract used by the workflow's recovery owner: a zero HP read only counts
    /// as a confirmed death when the player structure/max HP is readable, the notification fires
    /// exactly once per death episode, and it fires again only after the character was revived.
    /// </summary>
    public sealed class DeathDetectionTests
    {
        private sealed class Memory
        {
            private const ulong PlayerBase = 0x100000;

            public short Hp = 0;
            public short MaxHp = 1000;
            public bool MaxHpUnreadable;

            public GameMemoryService Create(Action<string> log)
            {
                GameMemoryService.ReadMemoryDelegate read = (uint pid, ulong address, byte[] buffer, out uint count) =>
                {
                    count = 0;
                    byte[]? value = address switch
                    {
                        0x486BC8 => BitConverter.GetBytes(PlayerBase),
                        PlayerBase + BotConstants.MemoryOffsets.Hp => BitConverter.GetBytes(Hp),
                        PlayerBase + BotConstants.MemoryOffsets.Mana => BitConverter.GetBytes((short)500),
                        PlayerBase + BotConstants.MemoryOffsets.MaxHp => MaxHpUnreadable ? null : BitConverter.GetBytes(MaxHp),
                        PlayerBase + BotConstants.MemoryOffsets.MaxMp => BitConverter.GetBytes((short)1000),
                        PlayerBase + BotConstants.MemoryOffsets.InventorySlotHPCount => BitConverter.GetBytes(10),
                        PlayerBase + BotConstants.MemoryOffsets.ManaPotionsCount => BitConverter.GetBytes(10),
                        _ => null
                    };
                    if (value == null) return false;
                    Buffer.BlockCopy(value, 0, buffer, 0, value.Length);
                    count = (uint)value.Length;
                    return true;
                };
                GameMemoryService.WriteMemoryDelegate write =
                    (uint pid, ulong address, byte[] buffer, out uint count) => { count = 0; return false; };
                return new GameMemoryService(0, read, write, 0, 8, log);
            }
        }

        [Fact]
        public async Task ConfirmedZeroHpNotifiesOncePerDeathEpisode()
        {
            var memory = new Memory { Hp = 0, MaxHp = 1000 };
            var system = new HealManaSystem(memory.Create(_ => { }), _ => { });
            int notifications = 0;
            system.PlayerDeathDetected = () => notifications++;

            await system.Update(CancellationToken.None);
            await system.Update(CancellationToken.None);
            await system.Update(CancellationToken.None);

            Assert.Equal(1, notifications);

            // Revival resets the latch; the next death is a new episode.
            memory.Hp = 800;
            await system.Update(CancellationToken.None);
            memory.Hp = 0;
            await system.Update(CancellationToken.None);

            Assert.Equal(2, notifications);
        }

        [Fact]
        public async Task ZeroHpWithUnreadableMaxHpIsNotAConfirmedDeath()
        {
            var memory = new Memory { Hp = 0, MaxHpUnreadable = true };
            var system = new HealManaSystem(memory.Create(_ => { }), _ => { });
            int notifications = 0;
            system.PlayerDeathDetected = () => notifications++;

            await system.Update(CancellationToken.None);
            await system.Update(CancellationToken.None);

            Assert.Equal(0, notifications);
        }

        [Fact]
        public async Task ThrowingDeathHandlerDoesNotStopTheHealSystemOrLoseTheState()
        {
            var memory = new Memory { Hp = 0, MaxHp = 1000 };
            var log = new System.Collections.Generic.List<string>();
            var system = new HealManaSystem(memory.Create(log.Add), log.Add);
            system.PlayerDeathDetected = () => throw new InvalidOperationException("handler failure");

            // The exception is contained; the death state stays latched and no second
            // notification is produced for the same episode.
            await system.Update(CancellationToken.None);
            await system.Update(CancellationToken.None);

            Assert.Contains(log, line => line.Contains("Death notification handler failed"));
        }
    }
}
