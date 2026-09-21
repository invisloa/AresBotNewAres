using System;
using System.Reflection;
using DriverScanTester.Services;
using Xunit;

namespace DriverScanTester.Tests
{
    /// <summary>
    /// Regression tests for the combat "attack not connecting" detector.
    ///
    /// Bug this guards: the detector used to treat ANY change of the mana value as
    /// attack progress (mana != previous). Natural mana regeneration (a slow +1 MP
    /// every few seconds) therefore reset the 5 s stall window over and over, so a
    /// phantom attack against an unreachable mob never triggered
    /// <see cref="CombatAction.RepositionAndRetry"/> nor the standard unstuck — the
    /// bot stood in combat forever with a mob selected and no mana consumed.
    ///
    /// Correct semantics: only a mana DECREASE proves the skill actually consumed
    /// mana (the attack connected). Mana increases (regen / potions) must NOT reset
    /// the stall timer.
    /// </summary>
    public sealed class CombatManaStuckTests
    {
        private const ulong PlayerPtrOffset = 0x486BC8;
        private const ulong PlayerBase = 0x100000;
        private const ulong TargetSelectedOffset = 0x60;
        private const ulong CurrentActionOffset = 0x3B0;
        private const ulong Animation1Offset = 0x3BA;
        private const ulong ManaOffset = 0xC58;

        private sealed class MemoryStub
        {
            public short Mana = 1400;
            public int TargetId = 5000; // valid mob (< MaxMobTargetId)
            public byte Action = 39;    // 39 = attacking (non-idle)
            public bool ManaReadFails;

            public GameMemoryService Create(Action<string> log)
            {
                GameMemoryService.ReadMemoryDelegate read =
                    (uint pid, ulong address, byte[] buffer, out uint bytesRead) =>
                    {
                        bytesRead = 0;

                        if (address == PlayerPtrOffset)
                            return Fill(buffer, BitConverter.GetBytes(PlayerBase), out bytesRead);

                        if (address == PlayerBase + TargetSelectedOffset)
                            return Fill(buffer, BitConverter.GetBytes(TargetId), out bytesRead);

                        if (address == PlayerBase + Animation1Offset)
                            return Fill(buffer, BitConverter.GetBytes(0), out bytesRead);

                        if (address == PlayerBase + CurrentActionOffset)
                        {
                            buffer[0] = Action;
                            bytesRead = 1;
                            return true;
                        }

                        if (address == PlayerBase + ManaOffset)
                        {
                            if (ManaReadFails) return false;
                            return Fill(buffer, BitConverter.GetBytes(Mana), out bytesRead);
                        }

                        return false;
                    };

                GameMemoryService.WriteMemoryDelegate write =
                    (uint pid, ulong address, byte[] buffer, out uint bytesWritten) =>
                    {
                        bytesWritten = 0;
                        return false;
                    };

                return new GameMemoryService(0, read, write, 0, 8, log);
            }

            private static bool Fill(byte[] buffer, byte[] data, out uint bytesRead)
            {
                Buffer.BlockCopy(data, 0, buffer, 0, data.Length);
                bytesRead = (uint)data.Length;
                return true;
            }
        }

        private static readonly Action<string> NoLog = _ => { };

        private static void Backdate(CombatHandler handler, string fieldName, double seconds)
        {
            FieldInfo field = typeof(CombatHandler).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Field {fieldName} not found.");
            field.SetValue(handler, DateTime.Now.AddSeconds(-seconds));
        }

        private static CombatAction Evaluate(CombatHandler handler, GameMemoryService memory)
            => handler.EvaluateCombatAction(memory, BotMode.MoveAndAttackAndLoot, isUnstuckActive: false, currX: 10f, currY: 10f);

        [Fact]
        public void ManaIncrease_DoesNotResetStallTimer_TriggersReposition()
        {
            var stub = new MemoryStub { Mana = 1400 };
            var handler = new CombatHandler(NoLog);
            GameMemoryService memory = stub.Create(NoLog);

            // First tick with a selected mob / attack status baselines the attack state.
            Assert.Equal(CombatAction.Attack, Evaluate(handler, memory));

            // Simulate 6 s of phantom attacking: mana only went UP (regen/potion).
            Backdate(handler, "_lastManaChangeAt", 6.0);
            stub.Mana = 1402;

            CombatAction action = Evaluate(handler, memory);

            Assert.Equal(CombatAction.RepositionAndRetry, action);
        }

        [Fact]
        public void ManaDecrease_CountsAsAttackProgress_NoReposition()
        {
            var stub = new MemoryStub { Mana = 1400 };
            var handler = new CombatHandler(NoLog);
            GameMemoryService memory = stub.Create(NoLog);

            Assert.Equal(CombatAction.Attack, Evaluate(handler, memory));
            Assert.Equal(CombatAction.CombatWait, Evaluate(handler, memory));

            // 6 s later the skill finally consumes mana — attack IS connecting.
            Backdate(handler, "_lastManaChangeAt", 6.0);
            stub.Mana = 1380;

            Assert.Equal(CombatAction.CombatWait, Evaluate(handler, memory));
        }

        [Fact]
        public void ManaStable_TriggersReposition()
        {
            var stub = new MemoryStub { Mana = 1400 };
            var handler = new CombatHandler(NoLog);
            GameMemoryService memory = stub.Create(NoLog);

            Assert.Equal(CombatAction.Attack, Evaluate(handler, memory));
            Assert.Equal(CombatAction.CombatWait, Evaluate(handler, memory));

            Backdate(handler, "_lastManaChangeAt", 6.0);

            Assert.Equal(CombatAction.RepositionAndRetry, Evaluate(handler, memory));
        }

        [Fact]
        public void UnreadableMana_WithStalePosition_FallsBackToStandardUnstuck()
        {
            var stub = new MemoryStub { Mana = 1400, ManaReadFails = true };
            var handler = new CombatHandler(NoLog);
            GameMemoryService memory = stub.Create(NoLog);

            Assert.Equal(CombatAction.Attack, Evaluate(handler, memory));

            // 11 s of no mana readings and no position change (same currX/currY).
            Backdate(handler, "_lastManaChangeAt", 11.0);
            Backdate(handler, "_lastCombatPosChangeAt", 11.0);

            Assert.Equal(CombatAction.Unstuck, Evaluate(handler, memory));
        }
    }
}
