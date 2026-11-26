using System;
using System.Threading;
using System.Threading.Tasks;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Which potion stack the LOOT PRIORITY weight dump drinks.
    /// </summary>
    internal enum WeightDumpPotion
    {
        /// <summary>White (movement) and red (attack) buff potions — keys 6 and 7.</summary>
        Buff = 0,

        /// <summary>Mana potions — key 2.</summary>
        Mana,

        /// <summary>HP potions — key 1.</summary>
        Hp
    }

    /// <summary>
    /// LOOT PRIORITY weight relief that runs BEFORE the weight-triggered town teleport.
    ///
    /// Looting fills the bag up to the class weight limit. When the limit is reached the
    /// bot has to teleport to the city and repot — unless it can free weight by drinking
    /// potions it does not need to keep. The dump is only allowed while EVERY stack is
    /// above its keep value (the hunt must still have a usable potion supply) and drinks
    /// in this order:
    ///   1. white (movement) + red (attack) buff potions, down to the keep values,
    ///   2. mana potions down to
    ///      <see cref="BotConstants.Repot.LootPriorityWeightDumpKeepManaPotions"/>,
    ///   3. HP potions down to
    ///      <see cref="BotConstants.Repot.LootPriorityWeightDumpKeepHpPotions"/>.
    /// When a stack is already at/below its keep value the dump is not started at all and
    /// the workflow teleports to the city exactly as before.
    ///
    /// Every press is verified against the memory potion counts: a key that does not
    /// consume a potion (buff already active, key bound to a skill, empty slot) is
    /// abandoned after <see cref="BotConstants.Repot.LootPriorityWeightDumpMaxStalledPresses"/>
    /// presses instead of being spammed forever.
    /// </summary>
    internal class LootPriorityWeightDumpService
    {
        private readonly GameMemoryService _memoryService;
        private readonly Action<string> _log;
        private readonly Func<int, CancellationToken, Task> _delay;

        internal LootPriorityWeightDumpService(
            GameMemoryService memoryService,
            Action<string> log,
            Func<int, CancellationToken, Task>? delay = null)
        {
            _memoryService = memoryService;
            _log = log;
            _delay = delay ?? ((milliseconds, token) => Task.Delay(milliseconds, token));
        }

        /// <summary>Potion counts read from the potion inventory slots.</summary>
        internal readonly record struct PotionCounts(int Hp, int Mana, int White, int Red);

        /// <summary>
        /// The dump drinks HP/mana down to their keep values. It makes sense only when the
        /// profile's own potion repot limit is BELOW those keep values — otherwise the very
        /// next repot check sees the dumped stock at/below the limit and fires a low-potion
        /// repot, so the dump would only waste potions before the same town teleport.
        /// </summary>
        internal static bool IsDumpUseful(int minHpPotions, int minManaPotions)
        {
            return BotConstants.Repot.LootPriorityWeightDumpKeepHpPotions > minHpPotions
                && BotConstants.Repot.LootPriorityWeightDumpKeepManaPotions > minManaPotions;
        }

        /// <summary>
        /// True when EVERY potion stack is above its keep value — the only situation in
        /// which the potion dump may start. A stack at/below its keep value means the bot
        /// is already running on a minimal supply, so it must repot instead of drinking more.
        /// </summary>
        internal static bool HasSurplus(PotionCounts counts)
        {
            return counts.Hp > BotConstants.Repot.LootPriorityWeightDumpKeepHpPotions
                && counts.Mana > BotConstants.Repot.LootPriorityWeightDumpKeepManaPotions
                && counts.White > BotConstants.Repot.LootPriorityWeightDumpKeepWhitePotions
                && counts.Red > BotConstants.Repot.LootPriorityWeightDumpKeepRedPotions;
        }

        // ── Overridable drink keys (regression tests record presses instead of
        //    sending real keyboard input). ──

        protected virtual void PressHpPotion() => GameInput.PressKey(GameInput.VK_1, GameInput.SCAN_1);

        protected virtual void PressManaPotion() => GameInput.PressKey(GameInput.VK_2, GameInput.SCAN_2);

        /// <summary>Key 6 — the red (attack) potion on the current client mapping.</summary>
        protected virtual void PressRedPotion() => GameInput.PressKey(GameInput.VK_6, GameInput.SCAN_6);

        /// <summary>Key 7 — the white (movement-speed) potion on the current client mapping.</summary>
        protected virtual void PressWhitePotion() => GameInput.PressKey(GameInput.VK_7, GameInput.SCAN_7);

        /// <summary>
        /// Drinks the surplus potions when the weight limit is reached. Returns true when at
        /// least one potion was actually consumed. A false return means the dump was not
        /// allowed (a stack is at its keep value) or no key consumed a potion — the caller
        /// then falls back to the normal weight-triggered town teleport.
        /// </summary>
        internal async Task<bool> TryDumpSurplusAsync(CancellationToken token)
        {
            PotionCounts before = ReadPotionCounts();

            if (!HasSurplus(before))
            {
                _log($"[WeightDump] At least one potion stack is at/below its keep value (HP {before.Hp} <= {BotConstants.Repot.LootPriorityWeightDumpKeepHpPotions}, mana {before.Mana} <= {BotConstants.Repot.LootPriorityWeightDumpKeepManaPotions}, white {before.White} <= {BotConstants.Repot.LootPriorityWeightDumpKeepWhitePotions}, red {before.Red} <= {BotConstants.Repot.LootPriorityWeightDumpKeepRedPotions}) — no surplus to drink, the weight repot continues.");
                return false;
            }

            _log($"[WeightDump] Weight limit reached with surplus potions (HP {before.Hp}, mana {before.Mana}, white {before.White}, red {before.Red}) — drinking buff potions first, then mana down to {BotConstants.Repot.LootPriorityWeightDumpKeepManaPotions} and HP down to {BotConstants.Repot.LootPriorityWeightDumpKeepHpPotions} to free weight before any town teleport.");

            // The dump runs inside the EXP loop's repot check, so it must stay bounded:
            // a death, a city teleport or a long dump must never delay the workflow.
            DateTime dumpDeadline = DateTime.UtcNow.AddMilliseconds(BotConstants.Repot.LootPriorityWeightDumpMaxDurationMs);

            PotionCounts counts = before;
            counts = await DumpBuffPotionsAsync(counts, dumpDeadline, token);
            counts = await DumpSinglePotionTypeAsync(WeightDumpPotion.Mana, counts, dumpDeadline, token);
            counts = await DumpSinglePotionTypeAsync(WeightDumpPotion.Hp, counts, dumpDeadline, token);

            int drunk = Math.Max(0, before.Hp - counts.Hp)
                      + Math.Max(0, before.Mana - counts.Mana)
                      + Math.Max(0, before.White - counts.White)
                      + Math.Max(0, before.Red - counts.Red);

            _log($"[WeightDump] Dump finished — drank {drunk} potion(s); left HP {counts.Hp}, mana {counts.Mana}, white {counts.White}, red {counts.Red}.");

            return drunk > 0;
        }

        private enum BuffStack { Unknown, White, Red }

        private async Task<PotionCounts> DumpBuffPotionsAsync(PotionCounts counts, DateTime dumpDeadline, CancellationToken token)
        {
            (Action Press, string Key)[] keys = [(PressRedPotion, "6"), (PressWhitePotion, "7")];
            var observedStacks = new BuffStack[keys.Length];
            var stalledPresses = new int[keys.Length];
            int presses = 0;
            int maxPresses = BotConstants.Repot.LootPriorityWeightDumpMaxPressesPerType;
            int maxStalledPresses = BotConstants.Repot.LootPriorityWeightDumpMaxStalledPresses;

            while (presses < maxPresses)
            {
                bool pressed = false;
                for (int i = 0; i < keys.Length && presses < maxPresses; i++)
                {
                    if (ShouldStopDump("buff", dumpDeadline, token))
                        return counts;
                    if (stalledPresses[i] >= maxStalledPresses)
                        continue;

                    counts = ReadPotionCounts();
                    bool whiteSurplus = counts.White > BotConstants.Repot.LootPriorityWeightDumpKeepWhitePotions;
                    bool redSurplus = counts.Red > BotConstants.Repot.LootPriorityWeightDumpKeepRedPotions;
                    bool canPress = observedStacks[i] switch
                    {
                        BuffStack.White => whiteSurplus,
                        BuffStack.Red => redSurplus,
                        _ => whiteSurplus && redSurplus
                    };
                    if (!canPress)
                        continue;

                    PotionCounts before = counts;
                    keys[i].Press();
                    pressed = true;
                    presses++;
                    counts = await WaitForConsumptionAsync(WeightDumpPotion.Buff, before, token);
                    bool whiteConsumed = counts.White < before.White;
                    bool redConsumed = counts.Red < before.Red;
                    if (whiteConsumed || redConsumed)
                    {
                        // Learn the mapping from inventory so either binding can reach its reserve.
                        observedStacks[i] = whiteConsumed == redConsumed
                            ? BuffStack.Unknown
                            : whiteConsumed ? BuffStack.White : BuffStack.Red;
                        stalledPresses[i] = 0;
                    }
                    else if (++stalledPresses[i] == maxStalledPresses)
                    {
                        _log($"[WeightDump] Buff key {keys[i].Key} consumed nothing after {maxStalledPresses} presses; moving on.");
                    }

                    await _delay(BotConstants.Delays.PotionDumpBuffKeyGapMs, token);
                }

                if (!pressed)
                    break;
            }

            if (presses >= maxPresses)
                _log($"[WeightDump] Buff potion press limit ({maxPresses}) reached; moving on (white {counts.White}, red {counts.Red}).");

            return counts;
        }

        /// <summary>
        /// Dumps one potion type down to its keep value. Every press is verified: a key
        /// that consumes nothing (full HP/mana, cooldown) is retried a bounded number of
        /// times and then abandoned.
        /// </summary>
        private async Task<PotionCounts> DumpSinglePotionTypeAsync(
            WeightDumpPotion potion, PotionCounts counts, DateTime dumpDeadline, CancellationToken token)
        {
            int stalledPresses = 0;
            int presses = 0;

            while (IsAboveKeep(potion, counts))
            {
                if (ShouldStopDump(PotionName(potion), dumpDeadline, token))
                    break;

                if (presses >= BotConstants.Repot.LootPriorityWeightDumpMaxPressesPerType)
                {
                    _log($"[WeightDump] {PotionName(potion)} press limit ({BotConstants.Repot.LootPriorityWeightDumpMaxPressesPerType}) reached — moving on ({Describe(counts)}).");
                    break;
                }

                PotionCounts before = counts;
                PressPotion(potion);
                presses++;

                counts = await WaitForConsumptionAsync(potion, before, token);
                bool consumed = CountOf(potion, counts) < CountOf(potion, before);

                if (consumed)
                {
                    stalledPresses = 0;
                }
                else if (++stalledPresses >= BotConstants.Repot.LootPriorityWeightDumpMaxStalledPresses)
                {
                    _log($"[WeightDump] {PotionName(potion)} key consumed nothing after {stalledPresses} presses (full stat, cooldown or empty slot) — moving on ({Describe(counts)}).");
                    break;
                }
            }

            return counts;
        }

        /// <summary>
        /// Polls the potion counts until the pressed type is observed to drop (the client
        /// applies the drink asynchronously) or the bounded confirm timeout expires.
        /// </summary>
        private async Task<PotionCounts> WaitForConsumptionAsync(
            WeightDumpPotion potion, PotionCounts before, CancellationToken token)
        {
            PotionCounts current = ReadPotionCounts();
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(BotConstants.Delays.PotionDumpConfirmTimeoutMs);

            while (!HasConsumed(potion, before, current)
                && !token.IsCancellationRequested
                && DateTime.UtcNow < deadline)
            {
                await _delay(BotConstants.Delays.PotionDumpPollMs, token);
                current = ReadPotionCounts();
            }

            return current;
        }

        private static bool HasConsumed(WeightDumpPotion potion, PotionCounts before, PotionCounts current)
        {
            if (potion == WeightDumpPotion.Buff)
                return current.White < before.White || current.Red < before.Red;

            return CountOf(potion, current) < CountOf(potion, before);
        }

        private static bool IsAboveKeep(WeightDumpPotion potion, PotionCounts counts)
        {
            return potion switch
            {
                WeightDumpPotion.Mana => counts.Mana > BotConstants.Repot.LootPriorityWeightDumpKeepManaPotions,
                WeightDumpPotion.Hp => counts.Hp > BotConstants.Repot.LootPriorityWeightDumpKeepHpPotions,
                _ => false
            };
        }

        private static int CountOf(WeightDumpPotion potion, PotionCounts counts)
        {
            return potion switch
            {
                WeightDumpPotion.Mana => counts.Mana,
                WeightDumpPotion.Hp => counts.Hp,
                _ => 0
            };
        }

        private void PressPotion(WeightDumpPotion potion)
        {
            switch (potion)
            {
                case WeightDumpPotion.Mana:
                    PressManaPotion();
                    break;
                case WeightDumpPotion.Hp:
                    PressHpPotion();
                    break;
                case WeightDumpPotion.Buff:
                    PressRedPotion();
                    PressWhitePotion();
                    break;
            }
        }

        private PotionCounts ReadPotionCounts()
        {
            return new PotionCounts(
                Hp: _memoryService.GetHpPotionCount(),
                Mana: _memoryService.GetManaPotionCount(),
                White: _memoryService.GetWhitePotionCount(),
                Red: _memoryService.GetRedPotionCount());
        }

        /// <summary>
        /// A confirmed zero HP, an unexpected city teleport or an over-long dump aborts the
        /// drink sequence — the workflow owns death/city recovery and no further drink keys
        /// may be sent once it has to react.
        /// </summary>
        private bool ShouldStopDump(string phase, DateTime dumpDeadline, CancellationToken token)
        {
            if (token.IsCancellationRequested)
                return true;

            var (hp, _) = _memoryService.GetHealManaValues();
            if (hp.HasValue && hp.Value <= 0)
            {
                _log($"[WeightDump] Player death detected during the {phase} potion dump — stopping the dump.");
                return true;
            }

            if (_memoryService.GetIsInCity())
            {
                _log("[WeightDump] Player is in the city during the potion dump — stopping the dump so the workflow can react.");
                return true;
            }

            if (DateTime.UtcNow >= dumpDeadline)
            {
                _log($"[WeightDump] Potion dump reached its {BotConstants.Repot.LootPriorityWeightDumpMaxDurationMs} ms limit during the {phase} phase — stopping the dump.");
                return true;
            }

            return false;
        }

        private static string PotionName(WeightDumpPotion potion)
        {
            return potion switch
            {
                WeightDumpPotion.Buff => "buff",
                WeightDumpPotion.Mana => "mana",
                WeightDumpPotion.Hp => "HP",
                _ => "potion"
            };
        }

        private static string Describe(PotionCounts counts)
            => $"HP {counts.Hp}, mana {counts.Mana}, white {counts.White}, red {counts.Red}";
    }
}
