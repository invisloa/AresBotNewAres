using System;
using System.Collections.Generic;
using System.Linq;
using DriverScanTester.Models;

namespace DriverScanTester.Services
{
    /// <summary>Owns calibration and retry state for the lifetime of one profile workflow.</summary>
    public sealed class AutoBuffController
    {
        private readonly string _profilePath;
        private readonly BuffCalibrationStore _store;
        private readonly Action<string> _log;
        private readonly Dictionary<PlayerClass, ClassState> _classes = new();

        internal static AutoBuffController? Create(BotProfile profile, Action<string> log, BuffCalibrationStore? store = null)
        {
            if (profile.SavedProfilePath == null || profile.AutoBuffByClass == null)
                return null;

            var controller = new AutoBuffController(profile, store ?? new BuffCalibrationStore(), log);
            return controller._classes.Count != 0 ? controller : null;
        }

        private AutoBuffController(BotProfile profile, BuffCalibrationStore store, Action<string> log)
        {
            _profilePath = profile.SavedProfilePath!;
            _store = store;
            _log = log;

            foreach (var (playerClass, settings) in profile.AutoBuffByClass)
            {
                if (!BotProfile.IsRepotClass(playerClass) || settings?.Enabled != true || settings.Buffs == null)
                    continue;

                var active = settings.Buffs.Where(buff => buff?.Enabled == true).ToArray();
                if (active.Length == 0)
                    continue;
                if (active.Length > BotConstants.MemoryOffsets.BuffSlotCount ||
                    active.Any(buff => !BotProfileLoader.IsAutoBuffKeyAvailable(profile, playerClass, buff.VirtualKey)) ||
                    active.Select(buff => buff.VirtualKey).Distinct().Count() != active.Length ||
                    settings.Buffs.Any(buff => buff == null || buff.Id == Guid.Empty) ||
                    settings.Buffs.Select(buff => buff.Id).Distinct().Count() != settings.Buffs.Count)
                {
                    _log($"[AutoBuff] Profile={profile.Name} Class={playerClass} has invalid buff settings.");
                    continue;
                }

                var buffs = settings.Buffs.Select(buff => new BuffState(
                    buff.Id, buff.Name, buff.Enabled, buff.VirtualKey,
                    _store.Load(_profilePath, playerClass, buff.Id, buff.VirtualKey))).ToArray();
                _classes.Add(playerClass, new ClassState(buffs));
                _log($"[AutoBuff] Profile={profile.Name} Class={playerClass} Configured={active.Length} Learned={buffs.Count(buff => buff.Enabled && buff.Value.HasValue)}");
            }
        }

        internal bool IsConfigured(PlayerClass playerClass) => _classes.ContainsKey(playerClass);

        internal BuffState[] Plan(PlayerClass playerClass, Snapshot snapshot, DateTime now)
        {
            var state = _classes[playerClass];
            var pending = new List<BuffState>();
            foreach (var buff in state.Buffs)
            {
                if (buff.Enabled && NeedsCast(buff, snapshot, now))
                    pending.Add(buff);
            }

            if (snapshot.HasFreeSlot)
                state.FullSlotsReported = false;
            if (pending.Count == 0 || !HasFreeSlot(playerClass, snapshot))
                return Array.Empty<BuffState>();

            return pending.ToArray();
        }

        internal bool NeedsCast(BuffState buff, Snapshot snapshot, DateTime now)
        {
            if (buff.Value is not short expected)
                return !buff.LearningAttempted;

            if (snapshot.Values.Contains(expected))
            {
                if (buff.RebuffFailures != 0)
                    _log($"[AutoBuff] Buff active again: {buff.Description} value={expected}.");
                buff.RebuffFailures = 0;
                buff.MissingReported = false;
                return false;
            }

            if (!buff.MissingReported)
            {
                _log($"[AutoBuff] Buff expired: {buff.Description} value={expected}.");
                buff.MissingReported = true;
            }

            return buff.RebuffFailures < 2 && (buff.RebuffFailures == 0 || now >= buff.RetryAfter);
        }

        internal bool HasFreeSlot(PlayerClass playerClass, Snapshot snapshot)
        {
            var state = _classes[playerClass];
            if (snapshot.HasFreeSlot)
            {
                state.FullSlotsReported = false;
                return true;
            }

            if (!state.FullSlotsReported)
            {
                _log($"[AutoBuff] Activation skipped for {playerClass}: no free buff slot.");
                state.FullSlotsReported = true;
            }
            return false;
        }

        internal void RecordCast(BuffState buff, Snapshot before)
        {
            if (buff.Value.HasValue)
            {
                _log($"[AutoBuff] Rebuffing {buff.Description} value={buff.Value}.");
                return;
            }

            buff.LearningAttempted = true;
            _log($"[AutoBuff] Learning {buff.Description}. Before=[{string.Join(",", before.Slots)}]");
        }

        internal bool TryGetCandidate(PlayerClass playerClass, BuffState buff, Snapshot before, Snapshot after, out short value)
        {
            value = 0;
            short[] added = after.Values.Except(before.Values).ToArray();
            if (added.Length == 0)
                return FailLearning(buff, "no new value detected. Start first calibration while this buff is inactive");
            if (added.Length > 1)
                return FailLearning(buff, $"multiple new values detected [{string.Join(",", added)}]");
            if (_classes[playerClass].Buffs.Any(other => other != buff && other.Value == added[0]))
                return FailLearning(buff, $"value {added[0]} is already assigned to another buff");

            value = added[0];
            return true;
        }

        internal void ConfirmLearning(PlayerClass playerClass, BuffState buff, short value, Snapshot confirmation)
        {
            if (!confirmation.Values.Contains(value))
            {
                FailLearning(buff, $"value {value} disappeared before confirmation");
                return;
            }

            if (!_store.TrySave(_profilePath, playerClass, buff.Id, buff.Key, value, out string? error))
            {
                FailLearning(buff, $"calibration could not be saved ({error})");
                return;
            }

            buff.Value = value;
            _log($"[AutoBuff] Learned {buff.Description} value={value}. Calibration saved.");
        }

        internal void VerifyRebuff(BuffState buff, Snapshot after, DateTime now)
        {
            short expected = buff.Value!.Value;
            if (!after.Values.Contains(expected))
            {
                FailCast(buff, $"expected value {expected} not present", now);
                return;
            }

            buff.RebuffFailures = 0;
            buff.MissingReported = false;
            _log($"[AutoBuff] Rebuff successful: {buff.Description} value={expected}.");
        }

        internal void FailCast(BuffState buff, string reason, DateTime now)
        {
            if (!buff.Value.HasValue)
            {
                FailLearning(buff, reason);
                return;
            }

            buff.RebuffFailures++;
            buff.RetryAfter = now + BotConstants.AutoBuff.RetryDelay;
            string retry = buff.RebuffFailures < 2
                ? "Retry in 30 seconds."
                : "Renewal suspended until the buff reappears or the workflow restarts.";
            _log($"[AutoBuff] Rebuff failed for {buff.Description}: {reason}. {retry}");
        }

        private bool FailLearning(BuffState buff, string reason)
        {
            _log($"[AutoBuff] Learning failed for {buff.Description}: {reason}. No further learning attempts in this workflow.");
            return false;
        }

        internal sealed class Snapshot
        {
            internal short[] Slots { get; }
            internal HashSet<short> Values { get; }
            internal bool HasFreeSlot { get; }

            private Snapshot(short[] slots)
            {
                Slots = slots;
                Values = slots.Where(value => value != 0).ToHashSet();
                HasFreeSlot = slots.Contains((short)0);
            }

            internal static Snapshot? Read(GameMemoryService memory) =>
                memory.TryGetBuffSlotValues(out var slots) ? new Snapshot(slots) : null;
        }

        internal sealed class BuffState(Guid id, string name, bool enabled, int key, short? value)
        {
            internal Guid Id { get; } = id;
            internal bool Enabled { get; } = enabled;
            internal int Key { get; } = key;
            internal short? Value { get; set; } = value;
            internal string Description { get; } = $"'{name}' key={GameInput.FormatVirtualKey(key)}";
            internal bool LearningAttempted { get; set; }
            internal bool MissingReported { get; set; }
            internal int RebuffFailures { get; set; }
            internal DateTime RetryAfter { get; set; }
        }

        private sealed class ClassState(BuffState[] buffs)
        {
            internal BuffState[] Buffs { get; } = buffs;
            internal bool FullSlotsReported { get; set; }
        }
    }
}
