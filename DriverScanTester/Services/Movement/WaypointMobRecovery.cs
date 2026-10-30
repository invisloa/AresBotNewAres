using System;

namespace DriverScanTester.Services
{
    internal enum WaypointMobRecoveryResult
    {
        InProgress,
        Recovered,
        Failed
    }

    /// <summary>
    /// Tick-driven AttackMob recovery for an explicitly configured waypoint.
    /// It owns targeting and attack input only while the recovery is active; W is
    /// deliberately never pressed by this state machine.
    /// </summary>
    internal sealed class WaypointMobRecovery
    {
        private enum RecoveryStage
        {
            CameraSettle,
            Searching,
            Attacking
        }

        private readonly GameMemoryService _memoryService;
        private readonly Action<string> _log;
        private readonly Action _stopMoving;
        private readonly Action _holdSkillThree;
        private readonly Action _releaseSkillThree;
        private readonly Func<bool>? _cameraControlBlocked;
        private readonly Func<bool>? _applyStationaryCameraLock;
        /// <summary>
        /// Central attack-safety predicate: true only for a CONFIRMED monster target. When
        /// null the legacy raw mob-selected indicator is used. TAB/attack decisions use it so
        /// this recovery can never select or keep attacking another player.
        /// </summary>
        private readonly Func<bool>? _isConfirmedMonsterTarget;

        private RecoveryStage _stage;
        private DateTime _startedAt;
        private DateTime _stageStartedAt;
        private DateTime _nextTabAt;
        private (float X, float Y) _startPosition;
        private bool _active;

        public bool IsActive => _active;

        public WaypointMobRecovery(
            GameMemoryService memoryService,
            Action<string> log,
            Action stopMoving,
            Action holdSkillThree,
            Action releaseSkillThree,
            Func<bool>? cameraControlBlocked = null,
            Func<bool>? applyStationaryCameraLock = null,
            Func<bool>? isConfirmedMonsterTarget = null)
        {
            _memoryService = memoryService ?? throw new ArgumentNullException(nameof(memoryService));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _stopMoving = stopMoving ?? throw new ArgumentNullException(nameof(stopMoving));
            _holdSkillThree = holdSkillThree ?? throw new ArgumentNullException(nameof(holdSkillThree));
            _releaseSkillThree = releaseSkillThree ?? throw new ArgumentNullException(nameof(releaseSkillThree));
            _cameraControlBlocked = cameraControlBlocked;
            _applyStationaryCameraLock = applyStationaryCameraLock;
            _isConfirmedMonsterTarget = isConfirmedMonsterTarget;
        }

        /// <summary>True only when the currently selected target may be attacked.</summary>
        private bool HasAttackableTarget()
            => _isConfirmedMonsterTarget?.Invoke() ?? _memoryService.IsMobSelected();

        public void Start(float currX, float currY, short cameraDistance)
        {
            _active = true;
            _startedAt = DateTime.UtcNow;
            _stageStartedAt = _startedAt;
            _startPosition = (currX, currY);
            _stage = RecoveryStage.CameraSettle;
            _nextTabAt = DateTime.MaxValue;

            _stopMoving();
            _releaseSkillThree();
            if (_cameraControlBlocked?.Invoke() == true)
            {
                _log("[WaypointRecovery] AttackMob camera setup skipped — mandatory loot owns the camera.");
            }
            else
            {
                _memoryService.SetCameraDistance(cameraDistance);
                bool cameraApplied = _applyStationaryCameraLock?.Invoke() ??
                    _memoryService.TrySetCameraVerticalLock(BotConstants.Camera.StationaryVerticalLock);
                if (!cameraApplied)
                    _log("[WaypointRecovery] Stationary camera lock was not changed because camera ownership or verification blocked it.");
            }
            _log($"[WaypointRecovery] AttackMob started. CameraDistance={cameraDistance}.");
        }

        public WaypointMobRecoveryResult Tick(float currX, float currY)
        {
            if (!_active)
                return WaypointMobRecoveryResult.Failed;

            if ((DateTime.UtcNow - _startedAt).TotalMilliseconds >= BotConstants.WaypointRecovery.AttackMobTimeoutMs)
            {
                _releaseSkillThree();
                _active = false;
                return WaypointMobRecoveryResult.Failed;
            }

            DateTime now = DateTime.UtcNow;
            switch (_stage)
            {
                case RecoveryStage.CameraSettle:
                    if ((now - _stageStartedAt).TotalMilliseconds < BotConstants.Delays.PostCameraRetargetMs)
                        return WaypointMobRecoveryResult.InProgress;

                    if (HasAttackableTarget())
                    {
                        BeginAttack();
                    }
                    else
                    {
                        _stage = RecoveryStage.Searching;
                        _stageStartedAt = now;
                        _nextTabAt = now;
                    }
                    return WaypointMobRecoveryResult.InProgress;

                case RecoveryStage.Searching:
                    if (HasAttackableTarget())
                    {
                        BeginAttack();
                        return WaypointMobRecoveryResult.InProgress;
                    }

                    if (now < _nextTabAt)
                        return WaypointMobRecoveryResult.InProgress;

                    _log("[WaypointRecovery] No mob selected — TAB.");
                    GameInput.PressKey(GameInput.VK_TAB, GameInput.SCAN_TAB);
                    _nextTabAt = now.AddMilliseconds(Math.Max(
                        BotConstants.Delays.PreTabWaitMs,
                        BotConstants.Combat.MoveModeTabIntervalSeconds * 1000.0));
                    return WaypointMobRecoveryResult.InProgress;

                case RecoveryStage.Attacking:
                    if (!HasAttackableTarget())
                    {
                        _releaseSkillThree();
                        _stage = RecoveryStage.Searching;
                        _stageStartedAt = now;
                        _nextTabAt = now.AddMilliseconds(BotConstants.Delays.PreTabWaitMs);
                        return WaypointMobRecoveryResult.InProgress;
                    }

                    _holdSkillThree();
                    float moved = GeometryUtils.Distance(
                        currX, currY, _startPosition.X, _startPosition.Y);
                    if (moved >= BotConstants.Movement.StuckProgressResetDistance)
                    {
                        _releaseSkillThree();
                        _active = false;
                        _log($"[WaypointRecovery] AttackMob recovered: moved {moved:F1} units.");
                        return WaypointMobRecoveryResult.Recovered;
                    }
                    return WaypointMobRecoveryResult.InProgress;
            }

            return WaypointMobRecoveryResult.Failed;
        }

        public void Stop()
        {
            if (!_active)
                return;

            _releaseSkillThree();
            _active = false;
        }

        private void BeginAttack()
        {
            _stage = RecoveryStage.Attacking;
            _stageStartedAt = DateTime.UtcNow;
            _log("[WaypointRecovery] Mob selected — holding attack skill 3.");
            _holdSkillThree();
        }
    }
}
