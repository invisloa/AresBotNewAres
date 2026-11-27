using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;

namespace DriverScanTester.Services
{
    public partial class MovementSystem
    {
        internal AutoBuffController? AutoBuffController { get; init; }

        private readonly object _autoBuffCameraOwner = new();
        private volatile bool _autoBuffCastActive;
        public bool IsAutoBuffCastActive => _autoBuffCastActive;

        private DateTime _nextAutoBuffCheck = DateTime.MinValue;
        private PlayerClass _pendingAutoBuffClass = PlayerClass.Unknown;
        private int _pendingAutoBuffMap;
        private AutoBuffController.BuffState[] _pendingAutoBuffs = Array.Empty<AutoBuffController.BuffState>();

        protected virtual DateTime AutoBuffNow => DateTime.UtcNow;

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

        protected virtual bool IsAutoBuffGameForeground()
        {
            nint foreground = GetForegroundWindow();
            return foreground != nint.Zero &&
                   GetWindowThreadProcessId(foreground, out uint processId) != 0 &&
                   processId == _memoryService.GameSessionIdentity.ProcessId;
        }

        protected virtual bool SendAutoBuffKey(int key) => GameInput.PressVirtualKey(key);

        protected virtual Task DelayAutoBuffAsync(int milliseconds, CancellationToken token) =>
            Task.Delay(milliseconds, token);

        protected virtual void SendSpeedPotionInput(byte key, byte scan) => GameInput.PressKey(key, scan);

        internal void PressSpeedPotion(byte key, byte scan)
        {
            lock (_inputLock)
            {
                if (IsAutoBuffCastActive)
                    return;

                _memoryService.RecordSpeedPotionInput(AutoBuffNow);
                SendSpeedPotionInput(key, scan);
            }
        }

        internal async Task<bool> TryCastAutoBuffsAsync(CancellationToken token)
        {
            var controller = AutoBuffController;
            if (controller == null)
                return false;

            token.ThrowIfCancellationRequested();
            DateTime now = AutoBuffNow;
            if (now >= _nextAutoBuffCheck)
            {
                // Keep this batch through temporary focus, combat or loot holds. Each queued
                // cast still requires a fresh snapshot just before its own key press.
                _nextAutoBuffCheck = now + BotConstants.AutoBuff.CheckInterval;
                _pendingAutoBuffs = Array.Empty<AutoBuffController.BuffState>();
                _pendingAutoBuffClass = _memoryService.GetPlayerClass();
                _pendingAutoBuffMap = _currentMapId;
                if (controller.IsConfigured(_pendingAutoBuffClass) &&
                    AutoBuffController.Snapshot.Read(_memoryService) is { } snapshot)
                    _pendingAutoBuffs = controller.Plan(_pendingAutoBuffClass, snapshot, now);
            }

            if (_pendingAutoBuffs.Length == 0)
                return false;

            bool canLearn = CanLearnAutoBuff(now);
            var candidates = canLearn ? _pendingAutoBuffs : _pendingAutoBuffs.Where(buff => buff.Value.HasValue).ToArray();
            if (candidates.Length == 0)
                return false;

            PlayerClass playerClass = _pendingAutoBuffClass;
            int map = _pendingAutoBuffMap;
            if (GetAutoBuffBlockReason(playerClass, map, beforePress: true) != null)
                return false;

            LootSystem? loot = LootSystemRef;
            if (loot != null && !loot.TryReserveInputForAutoBuff())
                return false;

            _autoBuffCastActive = true;
            _pendingAutoBuffs = canLearn
                ? Array.Empty<AutoBuffController.BuffState>()
                : _pendingAutoBuffs.Where(buff => !buff.Value.HasValue).ToArray();

            bool cameraHeld = false;
            try
            {
                StopMoving();
                ReleaseCombatKeys();
                if (_wKeyCameraController != null)
                {
                    cameraHeld = _wKeyCameraController.TryAcquireExternalCameraOwner(_autoBuffCameraOwner);
                    if (!cameraHeld)
                        return true;
                }

                await DelayAutoBuffAsync(BotConstants.AutoBuff.StopMovementSettleMs, token);
                foreach (var buff in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    if (!await TryActivateAutoBuffAsync(controller, playerClass, map, buff, token))
                        break;
                }
                return true;
            }
            finally
            {
                try
                {
                    if (cameraHeld)
                        _wKeyCameraController!.ReleaseExternalCameraOwner(_autoBuffCameraOwner, restoreStationary: false);

                    ResetActionStuckTracking();
                    ResetNavigationProgressTracking();
                    _connectionWatchdog.Reset();
                    _lastMoveProgressPos = null;
                    _lastMoveProgressTime = DateTime.MinValue;
                    _finalGoalLastProgressTime = DateTime.Now;
                }
                finally
                {
                    _autoBuffCastActive = false;
                    loot?.ReleaseInputForAutoBuff();
                }
            }
        }

        private bool CanLearnAutoBuff(DateTime now) =>
            now - _memoryService.LastSpeedPotionInputAt >= BotConstants.AutoBuff.SpeedPotionSettle;

        private async Task<bool> TryActivateAutoBuffAsync(
            AutoBuffController controller, PlayerClass playerClass, int map, AutoBuffController.BuffState buff,
            CancellationToken token)
        {
            if (GetAutoBuffBlockReason(playerClass, map, beforePress: true) != null)
                return false;

            bool learning = !buff.Value.HasValue;
            var potionCounts = learning ? _memoryService.ReadSpeedPotionCounts() : null;
            if (learning && (potionCounts == null || !CanLearnAutoBuff(AutoBuffNow)))
            {
                if (potionCounts == null)
                    _log($"[AutoBuff] Learning deferred for {buff.Description}: speed potion counts unreadable before cast.");
                return true;
            }

            var before = AutoBuffController.Snapshot.Read(_memoryService);
            if (before == null)
            {
                _log($"[AutoBuff] Activation deferred for {buff.Description}: BEFORE buff slots unreadable.");
                return false;
            }
            if (!controller.NeedsCast(buff, before, AutoBuffNow))
                return true;
            if (!controller.HasFreeSlot(playerClass, before))
                return false;

            lock (_inputLock)
            {
                token.ThrowIfCancellationRequested();
                if (GetAutoBuffBlockReason(playerClass, map, beforePress: true) != null)
                    return false;
                if (learning && !CanLearnAutoBuff(AutoBuffNow))
                    return true;
                if (!SendAutoBuffKey(buff.Key))
                {
                    _log($"[AutoBuff] Activation deferred for {buff.Description}: no scan code for this key.");
                    return true;
                }
                controller.RecordCast(buff, before);
            }

            try
            {
                string? interruption = await ObserveAutoBuffCastAsync(
                    BotConstants.AutoBuff.CastSettleMs, playerClass, map, potionCounts, token);
                if (interruption != null)
                {
                    controller.FailCast(buff, interruption, AutoBuffNow);
                    return false;
                }

                var after = AutoBuffController.Snapshot.Read(_memoryService);
                if (after == null)
                {
                    controller.FailCast(buff, "AFTER buff slots unreadable", AutoBuffNow);
                    return false;
                }
                if (!learning)
                {
                    controller.VerifyRebuff(buff, after, AutoBuffNow);
                    return true;
                }
                if (!controller.TryGetCandidate(playerClass, buff, before, after, out short value))
                    return true;

                interruption = await ObserveAutoBuffCastAsync(
                    BotConstants.AutoBuff.LearningConfirmationMs, playerClass, map, potionCounts, token);
                if (interruption != null)
                {
                    controller.FailCast(buff, interruption, AutoBuffNow);
                    return false;
                }

                var confirmation = AutoBuffController.Snapshot.Read(_memoryService);
                lock (_inputLock)
                {
                    token.ThrowIfCancellationRequested();
                    interruption = GetAutoBuffInterruption(playerClass, map, potionCounts);
                    if (interruption != null || confirmation == null)
                    {
                        controller.FailCast(buff, interruption ?? "confirmation buff slots unreadable", AutoBuffNow);
                        return false;
                    }

                    controller.ConfirmLearning(playerClass, buff, value, confirmation);
                    return true;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                controller.FailCast(buff, "cast interrupted by cancellation", AutoBuffNow);
                throw;
            }
        }

        private async Task<string?> ObserveAutoBuffCastAsync(
            int duration, PlayerClass playerClass, int map, (short Red, short White)? potionCounts, CancellationToken token)
        {
            DateTime until = AutoBuffNow.AddMilliseconds(duration);
            TimeSpan remaining = until - AutoBuffNow;
            while (remaining > TimeSpan.Zero)
            {
                int delay = (int)Math.Ceiling(Math.Min(remaining.TotalMilliseconds, BotConstants.AutoBuff.InterruptionCheckMs));
                await DelayAutoBuffAsync(delay, token);
                token.ThrowIfCancellationRequested();
                string? reason = GetAutoBuffInterruption(playerClass, map, potionCounts);
                if (reason != null)
                    return reason;
                remaining = until - AutoBuffNow;
            }
            return null;
        }

        private string? GetAutoBuffInterruption(PlayerClass playerClass, int map, (short Red, short White)? potionCounts)
        {
            string? reason = GetAutoBuffBlockReason(playerClass, map, beforePress: false);
            if (reason != null || potionCounts == null)
                return reason;

            if (!CanLearnAutoBuff(AutoBuffNow))
                return "speed potion input detected during calibration";

            var current = _memoryService.ReadSpeedPotionCounts();
            if (current == null)
                return "speed potion counts unreadable during calibration";
            if (current != potionCounts)
                return "speed potion counts changed during calibration";

            return null;
        }

        private string? GetAutoBuffBlockReason(PlayerClass playerClass, int map, bool beforePress)
        {
            if (Volatile.Read(ref _stopRequested) != 0)
                return "movement stopped";
            try
            {
                if (MobGroupingPausePredicate?.Invoke() == true)
                    return "workflow paused";
            }
            catch
            {
                return "pause state unreadable";
            }

            if (IsManaRegenCastControlOwnedByRecovery() || _waypointRepotRequested)
                return "recovery or repot owns input";
            if (_manaRegenActive)
                return "mana regeneration is active";
            if (IsManaRegenCastLootOrGroupingActive() || LootSystemRef?.HasActiveMandatoryLootRequest == true)
                return "loot or mob grouping owns input";
            if (_combatRetargetCameraStage != CombatRetargetCameraStage.None || _combatRetargetAwaitingSelection ||
                _postKillCloseTargetCheckPending || _postPinkScanTargetCheckPending || IsPostKillCloseMobCheckPending)
                return "retarget search is active";
            if (_isSkillThreeHeld || _dotOpeningController.OwnsAttackSequence ||
                !_memoryService.TryGetSelectedTargetId(out int targetId) || targetId > 0)
                return "combat or DoT opening is active, or target state is unreadable";
            byte action = _memoryService.GetCurrentAction();
            if (action is BotConstants.ActionValues.HitWeapon or BotConstants.ActionValues.HitNoWeapon ||
                (beforePress && action is BotConstants.ActionValues.Attacking or BotConstants.ActionValues.AttackingNoWeapon))
                return "combat action detected";

            if (!_memoryService.TryGetIsInCity(out bool inCity) || inCity)
                return "character is in town or town state is unreadable";
            if (_memoryService.GetHealManaValues().value1 is not > 0)
                return "character is dead or HP is unreadable";
            if (map <= 0 || _memoryService.GetMapNumber() != map)
                return "map changed or is unreadable";
            if (_memoryService.GetPlayerClass() != playerClass)
                return "character class changed or is unreadable";
            return IsAutoBuffGameForeground() ? null : "game lost foreground focus";
        }
    }
}
