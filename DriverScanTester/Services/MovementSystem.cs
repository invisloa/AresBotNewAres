using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DriverScanTester.Services
{
    public enum MovementPrecision
    {
        Exact = 0,
        Accurate = 2,
        Medium = 12,
        High = 20
    }

    public enum BotMode
    {
        OnlyMove,
        MoveAndAttack,
        MoveAndAttackAndLoot
    }

    public enum ZoneRestriction
    {
        /// <summary>Movement allowed only outside city (default).</summary>
        OutsideOnly,
        /// <summary>Movement allowed only inside city.</summary>
        InCityOnly,
        /// <summary>Movement allowed both in and out of city.</summary>
        Both
    }

    public struct Waypoint
    {
        public const short DefaultCameraDistanceLock = BotConstants.Camera.DefaultDistanceLock;
        public const short DefaultAttackDisengageDistance = BotConstants.Combat.DefaultAttackDisengageDistance;

        public float X { get; set; }
        public float Y { get; set; }
        public MovementPrecision Precision { get; set; }
        public BotMode Mode { get; set; }
        public short CameraDistanceLock { get; set; }
        public short AttackDisengageDistance { get; set; }
        public ZoneRestriction ZoneRestriction { get; set; }

        public Waypoint(
            float x,
            float y,
            MovementPrecision precision,
            BotMode mode,
            short cameraDistanceLock = DefaultCameraDistanceLock,
            short attackDisengageDistance = DefaultAttackDisengageDistance,
            ZoneRestriction zoneRestriction = ZoneRestriction.OutsideOnly)
        {
            X = x;
            Y = y;
            Precision = precision;
            Mode = mode;
            CameraDistanceLock = cameraDistanceLock;
            AttackDisengageDistance = attackDisengageDistance;
            ZoneRestriction = zoneRestriction;
        }
    }

    public class MovementSystem
    {
        private readonly GameMemoryService _memoryService;
        private readonly Action<string> _log;
        private readonly CombatHandler _combatHandler;
        private readonly RepotHelper _repotHelper;

        public MovementPrecision GlobalPrecision { get; set; } = MovementPrecision.Medium;
        public bool LoopPath { get; set; } = false;

        /// <summary>If true (default), MovementSystem handles repot internally (legacy mode).
        /// When false, the external BotWorkflowCoordinator is responsible for repot decisions.</summary>
        public bool InternalRepotEnabled { get; set; } = true;

        /// <summary>Returns true when the final goal waypoint has been reached (non-loop path).</summary>
        public bool IsGoalReached => _goalReached;

        // Obstacle
        private (float X, float Y) Waypoint2;

        // State
        private Queue<Waypoint> _waypoints = new Queue<Waypoint>();
        private List<Waypoint> _initialPath = new List<Waypoint>();
        private bool _isInitialized = false;
        private bool _goalReached = false;

        // Route resync after combat
        private bool _routeResyncPendingAfterCombat = false;

        // Temporary ghost waypoint tracking
        private readonly List<(float X, float Y)> _ghostWaypoints = new List<(float X, float Y)>();
        private const float GHOST_MATCH_EPSILON = BotConstants.Movement.GhostMatchEpsilon;
        private const float GHOST_REACH_THRESHOLD = BotConstants.Movement.GhostReachThreshold;
        // Bearing state
        private const float UnsetBearing = BotConstants.Movement.UnsetBearing;
        private float _lastSetBearingDeg = UnsetBearing;
        private bool _hasLastGameAngle = false;
        private float _lastSetGameAngle = 0f;

        // Stuck Detection
        private DateTime _ignoreStuckUntil = DateTime.MinValue;
        private const double STUCK_GRACE_AFTER_START_SECONDS = BotConstants.Movement.StuckGraceAfterStartSeconds;

        // Near-target stuck ignore
        private const float NEAR_TARGET_STUCK_IGNORE_EXTRA = BotConstants.Movement.NearTargetStuckIgnoreExtra;

        // Final-waypoint soft completion
        private const float FINAL_GOAL_SOFT_RADIUS = BotConstants.Movement.FinalGoalSoftRadius;
        private const double FINAL_GOAL_STALL_TIME = BotConstants.Movement.FinalGoalStallTime;
        private DateTime _finalGoalLastProgressTime = DateTime.MinValue;
        private float _finalGoalBestDist = float.MaxValue;

        // ────────────────────────────────────────────────────────
        //  LOCAL NAVIGATION MAP
        // ────────────────────────────────────────────────────────

        private const float LOCAL_MAP_CELL_SIZE = BotConstants.Movement.LocalMapCellSize;

        private readonly LocalNavigationMap _localNavigationMap;
        private int _currentMapId = -1;

        private bool _isUnstuckRoutineActive = false;
        private const double FORCE_START_MIN_INTERVAL_MS = BotConstants.Movement.ForceStartMinIntervalMs;
        private DateTime _lastForceStartMovingAt = DateTime.MinValue;

        // Consecutive stuck attempts counters — after this many stuck detections without
        // reaching a waypoint, the bot triggers a repot (teleport to town).
        private const int STUCK_MAX_ATTEMPTS_OUTSIDE = 15;
        private const int STUCK_MAX_ATTEMPTS_IN_CITY = 3;
        private int _consecutiveStuckAttempts = 0;

        // When stuck in city triggers teleport, wait this long before resuming.
        private DateTime _inCityStuckCooldownUntil = DateTime.MinValue;

        private readonly ReverseDiagonalRecovery _reverseDiagonalRecovery;
        private readonly StuckDetector _stuckDetector;

        // Healthy movement tracking
        private float _lastHealthyMoveBearingDeg = UnsetBearing;
        private (float X, float Y) _lastHealthyMovePos;
        private DateTime _lastHealthyMoveTime = DateTime.MinValue;

        // ── Camera update filtering ───────────────────────────────────────────────
        // Prevents camera oscillation (jitter between adjacent game-angle values)
        // by applying deadband, hysteresis, cooldown, and heading freeze logic.

        /// <summary>Minimum absolute circular difference in radians to allow a camera update.</summary>
        private const float CameraDeadbandRadians = BotConstants.Camera.DeadbandRadians;

        /// <summary>If the circular difference exceeds this threshold, update immediately (ignoring cooldown).</summary>
        private const float CameraForceUpdateRadians = BotConstants.Camera.ForceUpdateRadians;

        /// <summary>Minimum interval between camera updates (for small/medium angle changes).</summary>
        private const double MinCameraUpdateIntervalMs = BotConstants.Camera.MinUpdateIntervalMs;

        /// <summary>Base freeze distance for heading lock near waypoints. Actual = max(reachThreshold*2, this).</summary>
        private const float HeadingFreezeDistanceBase = BotConstants.Camera.HeadingFreezeDistanceBase;

        // ── Camera filter state ──

        /// <summary>Last game-angle value that was actually written to the camera (radians, normalised to [0, 2π)).</summary>
        private float _cameraLastAppliedAngle;

        /// <summary>When the last camera write occurred.</summary>
        private DateTime _lastCameraUpdateTime = DateTime.MinValue;

        /// <summary>Candidate angle for hysteresis tracking (medium-size changes, radians).</summary>
        private float _cameraHysteresisCandidate;

        /// <summary>How many consecutive ticks _cameraHysteresisCandidate has been observed.</summary>
        private int _cameraHysteresisStableTicks;

        /// <summary>Whether a hysteresis candidate exists.</summary>
        private bool _hasCameraHysteresisCandidate;

        // Input
        private readonly object _inputLock = new object();

        private int _moveLogCounter = 0;
        private int _tickCount = 0;
        private int _stateLogInterval = 5; // Log periodic state every N ticks

        private bool _isMovingForward = false;
        private bool _isSkillThreeHeld = false;
        private bool _attackSuppressedForCurrentWaypoint = false;
        private int _startMoveCount = 0;
        private int _stopMoveCount = 0;
        private static readonly Random _rng = new Random();

        private enum RouteResyncResult
        {
            /// <summary>Queue was rebuilt with a new target.</summary>
            Applied,
            /// <summary>New target is the same as current queue peek — no change needed.</summary>
            SameTarget,
            /// <summary>Permanent condition — resync will never succeed (e.g. path too short).</summary>
            TerminalSkip,
            /// <summary>Temporary condition — resync may succeed later (e.g. unstuck active).</summary>
            TemporarySkip
        }

        private enum CombatRetargetCameraStage
        {
            None,
            VeryLowSearch,
            LowSearch,
            MidSearch
        }

        private const short CombatRetargetVeryLowCameraDistance = BotConstants.Camera.CombatRetargetVeryLowDistance;
        private const short CombatRetargetLowCameraDistance = BotConstants.Camera.CombatRetargetLowDistance;
        private const short CombatRetargetMidCameraDistance = BotConstants.Camera.CombatRetargetMidDistance;
        private CombatRetargetCameraStage _combatRetargetCameraStage = CombatRetargetCameraStage.None;
        private bool _combatRetargetAwaitingSelection = false;

        public MovementSystem(GameMemoryService memoryService, Action<string> log, float targetX, float targetY, MovementPrecision precision = MovementPrecision.Medium, IEnumerable<Waypoint>? customPath = null, BotMode initialMode = BotMode.OnlyMove, bool loopPath = false)
        {
            _memoryService = memoryService;
            _log = log;
            _combatHandler = new CombatHandler(log);
            _repotHelper = new RepotHelper(memoryService, log, StopMoving, () => _goalReached = true);
            Waypoint2 = (targetX, targetY);
            GlobalPrecision = precision;
            LoopPath = loopPath;

            int initialMapId = _memoryService.GetMapNumber();
            _currentMapId = initialMapId;
            _reverseDiagonalRecovery = new ReverseDiagonalRecovery(_memoryService, log, StartMoving, StopMoving);
            _stuckDetector = new StuckDetector(_memoryService, log, GetEffectiveWaypointReachThreshold, NEAR_TARGET_STUCK_IGNORE_EXTRA);
            _localNavigationMap = new LocalNavigationMap(_log, initialMapId);

            _isInitialized = true;
            _log($"MovementSystem: Initialized with GameMemoryService, Default Precision: {GlobalPrecision}, Loop: {LoopPath}");
            _log($"[LocalMap] Initial map ID = {initialMapId}.");
            _log($"[BearingCalib] Using pure float math: Math.Atan2(target-current) → radians written to camera (32-bit float).");

            if (customPath != null)
            {
                _initialPath = customPath.ToList();
                foreach (var p in _initialPath)
                {
                    _waypoints.Enqueue(p);
                }

                _log($"Loaded custom path with {_waypoints.Count} waypoints.");

                int index = 1;
                foreach (var p in _initialPath)
                {
                    _log($"[Path] #{index}: ({p.X:F1}, {p.Y:F1}) Precision:{p.Precision} Mode:{p.Mode} CamLock:{p.CameraDistanceLock} AtkDis:{p.AttackDisengageDistance}");
                    index++;
                }
            }
            else
            {
                var wp = new Waypoint(Waypoint2.X, Waypoint2.Y, GlobalPrecision, initialMode);
                _waypoints.Enqueue(wp);
                _log($"[Path] Single target: ({wp.X:F1}, {wp.Y:F1}) Precision:{wp.Precision} Mode:{wp.Mode} CamLock:{wp.CameraDistanceLock} AtkDis:{wp.AttackDisengageDistance}");
            }
        }

        public async Task Update(CancellationToken token)
        {
            _tickCount++;

            if (!_isInitialized)
            {
                _log($"[Tick {_tickCount}] Skipped — not initialized");
                return;
            }

            if (_goalReached)
            {
                _localNavigationMap.SaveIfDirty();
                _log($"[Tick {_tickCount}] Skipped — goal already reached");
                return;
            }

            token.ThrowIfCancellationRequested();

            // ── In-city stuck cooldown ──
            // After 3 stuck attempts in city, bot presses 6 and waits 10 minutes.
            if (DateTime.Now < _inCityStuckCooldownUntil)
            {
                if (_isMovingForward || _isSkillThreeHeld)
                {
                    StopMoving();
                    ReleaseSkillThree();
                }

                _log($"[Tick {_tickCount}] In-city stuck cooldown — waiting {(_inCityStuckCooldownUntil - DateTime.Now).TotalMinutes:F1} min more.");
                return;
            }

            // ── Zone restriction check ──
            // Skip zone blocking when ReportAndGoBack is active (bot is teleporting to/from town).
            if (!_repotHelper.IsReportAndGoBackActive)
            {
                bool inCity = _memoryService.GetIsInCity();
                ZoneRestriction currentRestriction = _waypoints.Count > 0
                    ? _waypoints.Peek().ZoneRestriction
                    : ZoneRestriction.OutsideOnly;

                // Block movement if we're in a zone the current waypoint doesn't allow.
                // OutsideOnly waypoints are blocked in city; InCityOnly waypoints are blocked outside.
                bool zoneBlocked = (inCity && currentRestriction == ZoneRestriction.OutsideOnly)
                                || (!inCity && currentRestriction == ZoneRestriction.InCityOnly);

                if (zoneBlocked)
                {
                    _log($"[Tick {_tickCount}] Zone blocked (inCity={inCity}, restriction={currentRestriction}, waypoints={_waypoints.Count}) — stopping movement.");

                    if (_isMovingForward || _isSkillThreeHeld)
                    {
                        StopMoving();
                        ReleaseSkillThree();
                        ClearCombatRetargetSearch();
                    }

                    return;
                }
            }

            // ── Periodic state dump ──
            if (_tickCount % _stateLogInterval == 0)
            {
                string modeStr = "none";
                if (_waypoints.Count > 0) modeStr = _waypoints.Peek().Mode.ToString();
                var camAngle = _memoryService.GetCameraAngle();
                _log($"[State] T:{_tickCount} Q:{_waypoints.Count} M:{modeStr} Fwd:{_isMovingForward} Unst:{_isUnstuckRoutineActive} Goal:{_goalReached} Loop:{LoopPath} Cam:{camAngle}");
            }

            // ── Repot / Report-and-go-back ──
            var repotAction = _repotHelper.EvaluateRepotTick(InternalRepotEnabled);
            if (repotAction != RepotAction.None)
            {
                _log($"[Repot] {repotAction}");
            }
            if (repotAction == RepotAction.ReportAndGoBackActive)
            {
                _log($"[Tick {_tickCount}] Report&GoBack — skip move");
                return;
            }
            if (repotAction == RepotAction.Repotting)
            {
                _log($"[Tick {_tickCount}] Repotting — skip move");
                return;
            }

            // ── Map change detection ──
            // Each game map has its own navigation file. If the player changed maps,
            // save the current map's data and load the new map's data.
            int currentMapId = _memoryService.GetMapNumber();
            if (currentMapId != _currentMapId)
            {
                _log($"[Tick {_tickCount}] Map: {_currentMapId} → {currentMapId}");
                _localNavigationMap.ChangeMap(currentMapId);
                _currentMapId = currentMapId;
            }

            // ── Attack speed / potion check ──
            if (_combatHandler.CheckAttackSpeed(_memoryService))
            {
                _log($"[Tick {_tickCount}] Speed {BotConstants.SpeedPotion.AttackSpeedThreshold} — using potions");
                _log("[Key] 7 (pot1)");
                GameInput.PressKey(GameInput.VK_7, GameInput.SCAN_7);
                await Task.Delay(BotConstants.SpeedPotion.PostPotionDelayMs, token);
                _log("[Key] 8 (pot2)");
                GameInput.PressKey(GameInput.VK_8, GameInput.SCAN_8);
            }

            var (currX, currY, success) = _memoryService.GetPlayerPosition();
            if (!success)
            {
                _log($"[Tick {_tickCount}] [Pos] Read failed");
                return;
            }

            // Log position every 5 ticks
            if (_tickCount % 5 == 0)
            {
                _log($"[Tick {_tickCount}] @ ({currX:F1},{currY:F1}) Cam:{_memoryService.GetCameraAngle()}");
            }

            byte currentAction = _memoryService.GetCurrentAction();
            int attackStatus = _memoryService.GetAttackStatus();
            bool mobSelected = _memoryService.IsMobSelected();

            BotMode currentMode = BotMode.OnlyMove;
            float manhattanDistanceToTarget = 0f;
            if (_waypoints.Count > 0)
            {
                var currentWaypoint = _waypoints.Peek();
                currentMode = currentWaypoint.Mode;
                manhattanDistanceToTarget = GeometryUtils.ManhattanDistance(currX, currY, currentWaypoint.X, currentWaypoint.Y);

                bool isAttackMode = currentWaypoint.Mode == BotMode.MoveAndAttack || currentWaypoint.Mode == BotMode.MoveAndAttackAndLoot;
                if (isAttackMode && manhattanDistanceToTarget > currentWaypoint.AttackDisengageDistance)
                {
                    if (!_attackSuppressedForCurrentWaypoint)
                    {
                        _attackSuppressedForCurrentWaypoint = true;
                        _combatHandler.ResetState();
                        ReleaseSkillThree();
                        ClearCombatRetargetSearch();
                        _log($"[CombatGate] Overshoot d:{manhattanDistanceToTarget:F1} > {currentWaypoint.AttackDisengageDistance:F1} at ({currentWaypoint.X:F1},{currentWaypoint.Y:F1}) — attack disabled until waypoint is reached.");
                    }

                    currentMode = BotMode.OnlyMove;
                }
                else if (_attackSuppressedForCurrentWaypoint && isAttackMode)
                {
                    currentMode = BotMode.OnlyMove;
                }

                bool canUseCombatRetargetSearch = currentMode == BotMode.MoveAndAttack || currentMode == BotMode.MoveAndAttackAndLoot;
                if (!canUseCombatRetargetSearch)
                {
                    ClearCombatRetargetSearch();
                }

                short cameraDistanceToApply = currentWaypoint.CameraDistanceLock;
                if (canUseCombatRetargetSearch && _combatRetargetCameraStage != CombatRetargetCameraStage.None)
                {
                    cameraDistanceToApply = GetCombatRetargetCameraDistance();
                }

                _memoryService.SetCameraDistance(cameraDistanceToApply);
                _memoryService.SetCameraVerticalLock(BotConstants.Camera.DefaultVerticalLock);

                if (canUseCombatRetargetSearch && _combatRetargetCameraStage != CombatRetargetCameraStage.None)
                {
                    if (attackStatus > 0)
                    {
                        _log($"[CombatRetarget] Mob selected at camera {cameraDistanceToApply}. Resuming normal combat.");
                        ClearCombatRetargetSearch();
                    }
                    else if (mobSelected)
                    {
                        _log($"[CombatRetarget] Mob selected at camera {cameraDistanceToApply}. Starting attack.");
                        ClearCombatRetargetSearch();
                        if (!_isSkillThreeHeld)
                        {
                            _log("[Key] 3 hold (attack skill)");
                            GameInput.keybd_event(GameInput.VK_3, GameInput.SCAN_3, 0, 0);
                            _isSkillThreeHeld = true;
                        }

                        if (_isMovingForward)
                        {
                            StopMoving();
                        }

                        await Task.Delay(30, token);
                        return;
                    }
                    else if (!_combatRetargetAwaitingSelection)
                    {
                        ReleaseSkillThree();
                        _log($"[CombatRetarget] Camera -> {cameraDistanceToApply}, TAB");
                        await Task.Delay(BotConstants.Delays.CombatRetargetTabMs, token);
                        GameInput.PressKey(GameInput.VK_TAB, GameInput.SCAN_TAB);
                        _combatRetargetAwaitingSelection = true;
                        await Task.Delay(BotConstants.Delays.PreTabWaitMs, token);
                        return;
                    }
                    else if (_combatRetargetCameraStage == CombatRetargetCameraStage.VeryLowSearch)
                    {
                        _log($"[CombatRetarget] No mob selected at {CombatRetargetVeryLowCameraDistance}. Retrying at {CombatRetargetLowCameraDistance}.");
                        _combatRetargetCameraStage = CombatRetargetCameraStage.LowSearch;
                        _combatRetargetAwaitingSelection = false;

                        _memoryService.SetCameraDistance(CombatRetargetLowCameraDistance);
                        _memoryService.SetCameraVerticalLock(BotConstants.Camera.DefaultVerticalLock);
                        ReleaseSkillThree();
                        _log($"[CombatRetarget] Camera -> {CombatRetargetLowCameraDistance}, TAB");
                        await Task.Delay(BotConstants.Delays.CombatRetargetTabMs, token);
                        GameInput.PressKey(GameInput.VK_TAB, GameInput.SCAN_TAB);
                        _combatRetargetAwaitingSelection = true;
                        await Task.Delay(BotConstants.Delays.PreTabWaitMs, token);
                        return;
                    }
                    else if (_combatRetargetCameraStage == CombatRetargetCameraStage.LowSearch)
                    {
                        _log($"[CombatRetarget] No mob selected at {CombatRetargetLowCameraDistance}. Retrying at {CombatRetargetMidCameraDistance}.");
                        _combatRetargetCameraStage = CombatRetargetCameraStage.MidSearch;
                        _combatRetargetAwaitingSelection = false;

                        _memoryService.SetCameraDistance(CombatRetargetMidCameraDistance);
                        _memoryService.SetCameraVerticalLock(BotConstants.Camera.DefaultVerticalLock);
                        ReleaseSkillThree();
                        _log($"[CombatRetarget] Camera -> {CombatRetargetMidCameraDistance}, TAB");
                        await Task.Delay(BotConstants.Delays.CombatRetargetTabMs, token);
                        GameInput.PressKey(GameInput.VK_TAB, GameInput.SCAN_TAB);
                        _combatRetargetAwaitingSelection = true;
                        await Task.Delay(BotConstants.Delays.PreTabWaitMs, token);
                        return;
                    }
                    else
                    {
                        _log($"[CombatRetarget] No mob selected at {CombatRetargetMidCameraDistance}. Restoring camera to {currentWaypoint.CameraDistanceLock}.");
                        _memoryService.SetCameraDistance(currentWaypoint.CameraDistanceLock);
                        _memoryService.SetCameraVerticalLock(BotConstants.Camera.DefaultVerticalLock);
                        ClearCombatRetargetSearch();
                        await Task.Delay(BotConstants.Delays.CombatAttackWaitMs, token);
                        return;
                    }
                }

            }

            // ── Combat mode handling ──
            var combatAction = _combatHandler.EvaluateCombatAction(_memoryService, currentMode, _isUnstuckRoutineActive);
            if (combatAction != CombatAction.None && combatAction != CombatAction.CombatWait)
            {
                _log($"[Tick {_tickCount}] Combat: {combatAction}");
            }

            // If any combat action interrupts movement, mark route resync as pending
            if (combatAction != CombatAction.None && !_routeResyncPendingAfterCombat)
            {
                _routeResyncPendingAfterCombat = true;
                _log($"[RouteResync] pending set: reason={combatAction}");
            }

            switch (combatAction)
            {
                case CombatAction.TabTarget:
                    if ((currentMode == BotMode.MoveAndAttack || currentMode == BotMode.MoveAndAttackAndLoot) &&
                        _isSkillThreeHeld &&
                        _combatRetargetCameraStage == CombatRetargetCameraStage.None)
                    {
                        _log($"[CombatRetarget] Target lost after attack. Lowering camera to {CombatRetargetVeryLowCameraDistance}.");
                        StartCombatRetargetSearch();
                        _memoryService.SetCameraDistance(CombatRetargetVeryLowCameraDistance);
                        _memoryService.SetCameraVerticalLock(BotConstants.Camera.DefaultVerticalLock);
                        ReleaseSkillThree();
                        _log($"[CombatRetarget] Camera -> {CombatRetargetVeryLowCameraDistance}, TAB");
                        await Task.Delay(BotConstants.Delays.CombatRetargetTabMs, token);
                        GameInput.PressKey(GameInput.VK_TAB, GameInput.SCAN_TAB);
                        _combatRetargetAwaitingSelection = true;
                        await Task.Delay(BotConstants.Delays.PreTabWaitMs, token);
                        return;
                    }

                    _log("[Key] TAB (target cycle)");
                    ReleaseSkillThree();
                    GameInput.PressKey(GameInput.VK_TAB, GameInput.SCAN_TAB);
                    await Task.Delay(BotConstants.Delays.PreTabWaitMs, token);
                    return;

                case CombatAction.Attack:
                    if (!_isSkillThreeHeld)
                    {
                        _log("[Key] 3 hold (attack skill)");
                        GameInput.keybd_event(GameInput.VK_3, GameInput.SCAN_3, 0, 0);
                        _isSkillThreeHeld = true;
                    }
                    if (_isMovingForward)
                    {
                        StopMoving();
                    }
                    await Task.Delay(BotConstants.Delays.CombatAttackWaitMs, token);
                    return;

                case CombatAction.CombatWait:
                    // Skill 3 is held, W is released — keep waiting in combat
                    await Task.Delay(BotConstants.Delays.CombatAttackWaitMs, token);
                    return;

                case CombatAction.PotionsUsed:
                    ReleaseSkillThree();
                    _log($"[Tick {_tickCount}] Potions used — brief delay.");
                    await Task.Delay(BotConstants.Delays.PotionsUsedMs, token);
                    return;
            }

            // Combat is over — release skill 3 if held
            ReleaseSkillThree();

            // ── Route resync after combat ──
            if (_routeResyncPendingAfterCombat)
            {
                _log($"[RouteResync] executing: pos=({currX:F1},{currY:F1}) waypoints={_waypoints.Count}");
                var resyncResult = RouteResyncFromCurrentPosition(currX, currY);

                if (resyncResult != RouteResyncResult.TemporarySkip)
                {
                    _routeResyncPendingAfterCombat = false;
                    _log($"[RouteResync] result={resyncResult} pendingCleared=True");
                }
                else
                {
                    _log($"[RouteResync] result={resyncResult} pendingKept=True");
                }
            }

            // ── Waypoint re-queue when empty (obstacle bypass) ──
            if (_waypoints.Count == 0 && !_goalReached)
            {
                _localNavigationMap.SaveIfDirty();
                _log($"[Tick {_tickCount}] Queue empty — LoS to ({Waypoint2.X:F1},{Waypoint2.Y:F1})");
                if (GeometryUtils.CheckLineOfSight(currX, currY, Waypoint2.X, Waypoint2.Y))
                {
                    _waypoints.Enqueue(new Waypoint(GeometryUtils.ObstacleCenter.X, GeometryUtils.ObstacleCenter.Y, GlobalPrecision, BotMode.OnlyMove));
                    _waypoints.Enqueue(new Waypoint(Waypoint2.X, Waypoint2.Y, GlobalPrecision, BotMode.OnlyMove));
                    _log($"[Tick {_tickCount}] Blocked — added obstacle ({GeometryUtils.ObstacleCenter.X:F1},{GeometryUtils.ObstacleCenter.Y:F1}) + target");
                }
                else
                {
                    _waypoints.Enqueue(new Waypoint(Waypoint2.X, Waypoint2.Y, GlobalPrecision, BotMode.OnlyMove));
                    _log($"[Tick {_tickCount}] Clear — re-added target");
                }
            }

            // ── Main movement loop ──
            if (_waypoints.Count > 0)
            {
                var activeTarget = _waypoints.Peek();
                float distToTarget = GeometryUtils.Distance(currX, currY, activeTarget.X, activeTarget.Y);

                // ── Normal movement path ──

                // Check final-waypoint soft completion
                if (IsLastWaypoint() && distToTarget <= FINAL_GOAL_SOFT_RADIUS)
                {
                    if (CheckFinalGoalSoftCompletion(currX, currY, activeTarget, distToTarget))
                    {
                        return;
                    }
                }

                AdvanceReachedWaypoints(currX, currY);

                if (_goalReached)
                {
                    _localNavigationMap.SaveIfDirty();
                    _log($"[Tick {_tickCount}] Goal reached ✓");
                    return;
                }

                if (_waypoints.Count == 0)
                {
                    _log($"[Tick {_tickCount}] WpQueue empty after advance");
                    return;
                }

                var target = _waypoints.Peek();
                _memoryService.SetCameraDistance(target.CameraDistanceLock);
                _memoryService.SetCameraVerticalLock(BotConstants.Camera.DefaultVerticalLock);
                float distNow = GeometryUtils.Distance(currX, currY, target.X, target.Y);
                float thresholdNow = GetEffectiveWaypointReachThreshold(target);

                // Reset stuck counter when close to target (within 15 units) — bot is making progress
                if (_consecutiveStuckAttempts > 0 && distNow <= 15f)
                {
                    _consecutiveStuckAttempts = 0;
                    _log($"[Unstuck] Reset counter — within 15 of target ({distNow:F1}).");
                }

                // ── Active ReverseDiagonalRecovery ──
                if (_reverseDiagonalRecovery.IsActive)
                {
                    RecoveryResult recoveryResult = _reverseDiagonalRecovery.Tick(currX, currY);
                    float bearingDeg = _reverseDiagonalRecovery.CurrentBearingDeg;
                    ApplyCameraBearing(bearingDeg); // camera only — W controlled by Recovery internally
                    switch (recoveryResult)
                    {
                        case RecoveryResult.InProgress:
                            return;
                        case RecoveryResult.Recovered:
                            _log($"[ReverseDiagonal] recovered.");
                            ResetActionStuckTracking();
                            return;
                        case RecoveryResult.Failed:
                            _log($"[ReverseDiagonal] failed - all attempts exhausted.");
                            _log($"[ActionStuck] Action={currentAction} while moving. Stuck confirmed by action.");
                            MarkObstacleFromActionStuck(currX, currY, target);
                            _localNavigationMap.SaveIfDirty();
                            return;
                    }
                }

                if (_stuckDetector.IsActionStuck(currX, currY, target, _isMovingForward))
                {
                    _log($"[ActionStuck] Action={currentAction} while moving. Starting ReverseDiagonalRecovery.");
                    StartReverseDiagonalRecovery(currX, currY, target, "");
                    return;
                }

                // Log route info periodically
                if (_tickCount % _stateLogInterval == 0)
                {
                    string ghostFlag = IsGhostWaypoint(target) ? " [GHOST]" : "";
                    _log($"[Route] T:{_tickCount} WP{ghostFlag}({target.X:F1},{target.Y:F1}) d:{distNow:F2} th:{thresholdNow:F2} M:{target.Mode} P:{target.Precision} Cam:{_memoryService.GetCameraAngle()}");
                }

                // Track healthy movement bearing for escape direction
                if (!_isUnstuckRoutineActive && StuckDetector.IsActionRunning(currentAction))
                {
                    _lastHealthyMoveBearingDeg = GeometryUtils.GetBearingToTargetDeg(currX, currY, target.X, target.Y);
                    _lastHealthyMovePos = (currX, currY);
                    _lastHealthyMoveTime = DateTime.Now;
                }

                _log($"[Tick {_tickCount}] → ({target.X:F1},{target.Y:F1}) d:{distNow:F2} Cam:{_memoryService.GetCameraAngle()}");
                MoveTowards(currX, currY, target.X, target.Y);
            }
            else
            {
                if (!_goalReached)
                {
                    _log($"[Tick {_tickCount}] No target — empty queue");
                }
            }
        }

        /// <summary>
        /// Returns true if the current waypoint is the last real (non-ghost) waypoint in the queue.
        /// </summary>
        private bool IsLastWaypoint()
        {
            if (_waypoints.Count == 0) return false;
            // If there is only one waypoint, it's the last
            if (_waypoints.Count == 1) return !IsGhostWaypoint(_waypoints.Peek());

            // If there are multiple but all remaining are ghosts, the first real one is last
            foreach (var wp in _waypoints)
            {
                if (!IsGhostWaypoint(wp))
                {
                    // Check if there's another real waypoint after this one
                    bool foundReal = false;
                    bool foundCurrent = false;
                    foreach (var wp2 in _waypoints)
                    {
                        if (!IsGhostWaypoint(wp2))
                        {
                            if (wp2.X == wp.X && wp2.Y == wp.Y && !foundCurrent)
                            {
                                foundCurrent = true;
                            }
                            else if (foundCurrent)
                            {
                                foundReal = true;
                                break;
                            }
                        }
                    }
                    return !foundReal;
                }
            }
            return true;
        }

        /// <summary>
        /// For the final waypoint: if the bot is within FINAL_GOAL_SOFT_RADIUS and
        /// has stalled (no progress) for FINAL_GOAL_STALL_TIME, complete the goal.
        /// </summary>
        private bool CheckFinalGoalSoftCompletion(float currX, float currY, Waypoint target, float distToTarget)
        {
            float reachThreshold = GetEffectiveWaypointReachThreshold(target);
            float effectiveThreshold = Math.Max(reachThreshold, FINAL_GOAL_SOFT_RADIUS);

            // Track best distance for this waypoint
            if (distToTarget < _finalGoalBestDist)
            {
                _finalGoalBestDist = distToTarget;
                _finalGoalLastProgressTime = DateTime.Now;
            }

            double stallTime = (DateTime.Now - _finalGoalLastProgressTime).TotalSeconds;

            if (distToTarget <= effectiveThreshold && stallTime >= FINAL_GOAL_STALL_TIME)
            {
                _log($"[FinalGoal] Soft complete d:{distToTarget:F2} <= {effectiveThreshold:F2} stall:{stallTime:F1}s >= {FINAL_GOAL_STALL_TIME:F1}s");
                AdvanceReachedWaypoints(currX, currY);
                return true;
            }

            if (_tickCount % _stateLogInterval == 0 && distToTarget <= FINAL_GOAL_SOFT_RADIUS)
            {
                _log($"[FinalGoal] Soft radius — best:{_finalGoalBestDist:F2} stall:{stallTime:F1}s/{FINAL_GOAL_STALL_TIME:F1}s");
            }

            return false;
        }





        // ========================================================================
        //  WAYPOINT ADVANCEMENT (preserved)
        // ========================================================================

        private bool AdvanceReachedWaypoints(float currX, float currY)
        {
            bool advanced = false;
            int wpIndex = 0;

            while (_waypoints.Count > 0)
            {
                var target = _waypoints.Peek();
                wpIndex++;

                float dist = GeometryUtils.Distance(currX, currY, target.X, target.Y);
                float threshold = GetEffectiveWaypointReachThreshold(target);

                if (dist > threshold)
                {
                    _log($"[AdvWp] #{wpIndex} ({target.X:F1},{target.Y:F1}) d:{dist:F2} > {threshold:F2}");
                    break;
                }

                bool isGhost = IsGhostWaypoint(target);
                string ghostTag = isGhost ? " [GHOST]" : "";

                _waypoints.Dequeue();
                _log($"[AdvWp] #{wpIndex} ({target.X:F1},{target.Y:F1}) reached{ghostTag} ✓ | Queue: {_waypoints.Count}");
                _consecutiveStuckAttempts = 0; // reset stuck counter — we made progress
                ResetBearingState();
                ResetActionStuckTracking();
                ResetCombatStateForWaypointChange();

                advanced = true;

                if (_waypoints.Count == 0)
                {
                    HandleEmptyWaypointQueueAfterAdvance();
                    if (_goalReached)
                        return true;
                }
            }

            return advanced;
        }



        private void HandleEmptyWaypointQueueAfterAdvance()
        {
            _log($"[WpQueue] HandleEmptyWaypointQueue. Loop={LoopPath}, InitialPathCount={_initialPath.Count}, GoalReached={_goalReached}");

            if (_waypoints.Count != 0)
            {
                _log($"[WpQueue] Queue not actually empty (count={_waypoints.Count}) — returning.");
                return;
            }

            if (LoopPath && _initialPath.Count > 0)
            {
                _log($"[WpQueue] End of path reached. Looping back to start ({_initialPath.Count} waypoints).");

                foreach (var p in _initialPath)
                {
                    _waypoints.Enqueue(p);
                }

                _log($"[WpQueue] Re-enqueued {_initialPath.Count} waypoints. Queue now has {_waypoints.Count} entries.");

                ResetBearingState();
                ResetActionStuckTracking();
                ResetCombatStateForWaypointChange();
            }
            else
            {
                _log($"[WpQueue] Final goal reached. Stopping movement.");
                StopMoving();
                _goalReached = true;
                _log("Final Goal Reached. Stopping.");
            }
        }

        private bool IsGhostWaypoint(Waypoint waypoint)
        {
            foreach (var ghost in _ghostWaypoints)
            {
                if (GeometryUtils.Distance(waypoint.X, waypoint.Y, ghost.X, ghost.Y) <= GHOST_MATCH_EPSILON)
                {
                    return true;
                }
            }

            return false;
        }

        private float GetEffectiveWaypointReachThreshold(Waypoint waypoint)
        {
            float threshold = GeometryUtils.GetWaypointReachThreshold(waypoint.Precision);

            // Exact (enum=0) must have a non-zero effective threshold, at least 1.5f
            if (waypoint.Precision == MovementPrecision.Exact)
            {
                threshold = Math.Max(threshold, BotConstants.Movement.ExactMinThreshold);
            }

            if (IsGhostWaypoint(waypoint))
            {
                return Math.Max(threshold, GHOST_REACH_THRESHOLD);
            }

            return threshold;
        }

        /// <summary>
        /// Applies a desired bearing: converts bearing to game camera angle (radians),
        /// sets the camera directly (subject to filtering), and holds W for forward movement.
        /// </summary>
        private void ApplySteeringBearing(float bearingDeg)
        {
            float cameraRadians = GeometryUtils.ConvertBearingToRadians(bearingDeg);
            _lastSetBearingDeg = bearingDeg;
            _lastSetGameAngle = cameraRadians;

            // Capture whether this is a fresh segment BEFORE mutating _hasLastGameAngle.
            // ResetBearingState() sets _hasLastGameAngle=false; the very next call to
            // ApplySteeringBearing should bypass the camera filter so the new heading
            // is applied immediately.
            bool isFreshSegment = !_hasLastGameAngle;
            _hasLastGameAngle = true;

            // ── Camera update filter ──
            if (ShouldUpdateCamera(cameraRadians, isFreshSegment, out string? skipReason))
            {
                _log($"[Camera] Apply target={cameraRadians:F4}rad last={_cameraLastAppliedAngle:F4}rad diff={CircularGameAngleDiff(cameraRadians, _cameraLastAppliedAngle):F4}");
                _memoryService.SetCameraAngle(cameraRadians);
                _cameraLastAppliedAngle = cameraRadians;
                _lastCameraUpdateTime = DateTime.Now;
                _hasCameraHysteresisCandidate = false;
            }
            else
            {
                _log($"[Camera] Skip target={cameraRadians:F4}rad last={_cameraLastAppliedAngle:F4}rad diff={CircularGameAngleDiff(cameraRadians, _cameraLastAppliedAngle):F4} reason={skipReason}");
            }

            StartMoving();
        }

        /// <summary>
        /// Applies a desired bearing: converts bearing to game camera angle (radians),
        /// sets the camera directly (subject to filtering) — WITHOUT pressing W.
        /// Used by ReverseDiagonalRecovery which controls W itself.
        /// </summary>
        private void ApplyCameraBearing(float bearingDeg)
        {
            float cameraRadians = GeometryUtils.ConvertBearingToRadians(bearingDeg);
            _lastSetBearingDeg = bearingDeg;
            _lastSetGameAngle = cameraRadians;

            bool isFreshSegment = !_hasLastGameAngle;
            _hasLastGameAngle = true;

            if (ShouldUpdateCamera(cameraRadians, isFreshSegment, out string? skipReason))
            {
                _log($"[Camera] Apply target={cameraRadians:F4}rad last={_cameraLastAppliedAngle:F4}rad diff={CircularGameAngleDiff(cameraRadians, _cameraLastAppliedAngle):F4}");
                _memoryService.SetCameraAngle(cameraRadians);
                _cameraLastAppliedAngle = cameraRadians;
                _lastCameraUpdateTime = DateTime.Now;
                _hasCameraHysteresisCandidate = false;
            }
            else
            {
                _log($"[Camera] Skip target={cameraRadians:F4}rad last={_cameraLastAppliedAngle:F4}rad diff={CircularGameAngleDiff(cameraRadians, _cameraLastAppliedAngle):F4} reason={skipReason}");
            }
            // NOTE: W is NOT pressed here — callers that need W use ApplySteeringBearing instead.
        }

        /// <summary>
        /// Determines whether the camera should actually be updated with the desired game angle.
        /// Implements deadband, cooldown, hysteresis, and waypoint-change detection.
        /// </summary>
        /// <param name="desiredAngle">The newly computed game angle (radians) to consider.</param>
        /// <param name="isFreshSegment">True when the waypoint just changed (bearing state was reset),
        /// so filtering should be bypassed for an immediate camera update.</param>
        /// <param name="skipReason">Set to a non-null string describing why the update was skipped.</param>
        private bool ShouldUpdateCamera(float desiredAngle, bool isFreshSegment, out string? skipReason)
        {
            skipReason = null;

            // 1. Fresh segment (waypoint just changed via ResetBearingState): allow immediate update.
            if (isFreshSegment)
            {
                return true;
            }

            // 2. No previous camera write ever: allow.
            if (_lastCameraUpdateTime == DateTime.MinValue)
            {
                return true;
            }

            // 3. Exactly the same angle: skip.
            if (desiredAngle == _cameraLastAppliedAngle)
            {
                skipReason = "same-angle";
                return false;
            }

            float diff = CircularGameAngleDiff(desiredAngle, _cameraLastAppliedAngle);
            float absDiff = Math.Abs(diff);

            // 4. Very small difference (within deadband): skip unconditionally.
            if (absDiff <= CameraDeadbandRadians)
            {
                skipReason = "deadband";
                return false;
            }

            // 5. Large difference: allow immediately, bypass cooldown and hysteresis.
            if (absDiff >= CameraForceUpdateRadians)
            {
                return true;
            }

            // 6. Medium difference: apply cooldown + hysteresis.

            // 6a. Cooldown: if we just updated recently and the change isn't urgent, wait.
            double msSinceLastUpdate = (DateTime.Now - _lastCameraUpdateTime).TotalMilliseconds;
            if (msSinceLastUpdate < MinCameraUpdateIntervalMs)
            {
                skipReason = $"cooldown ({msSinceLastUpdate:F0}ms < {MinCameraUpdateIntervalMs:F0}ms)";
                return false;
            }

            // 6b. Hysteresis: require the same desired angle to persist for 2 consecutive ticks.
            if (_hasCameraHysteresisCandidate && _cameraHysteresisCandidate == desiredAngle)
            {
                _cameraHysteresisStableTicks++;
                if (_cameraHysteresisStableTicks >= 2)
                {
                    return true; // stable for 2 ticks → allow
                }
                skipReason = $"hysteresis-wait ({_cameraHysteresisStableTicks}/2)";
                return false;
            }

            // First sighting of this angle (or it changed from the previous candidate).
            _cameraHysteresisCandidate = desiredAngle;
            _cameraHysteresisStableTicks = 1;
            _hasCameraHysteresisCandidate = true;
            skipReason = "hysteresis-start";
            return false;
        }

        /// <summary>
        /// Computes the shortest circular difference between two camera-angle values in
        /// radians, wrapping at the full 2π circumference. Result is in [-π, +π].
        /// </summary>
        private static float CircularGameAngleDiff(float a, float b)
        {
            float diff = a - b;
            float fullSpin = (float)(2 * Math.PI);
            float halfSpin = fullSpin / 2f;
            while (diff > halfSpin) diff -= fullSpin;
            while (diff < -halfSpin) diff += fullSpin;
            return diff;
        }

        private void MoveTowards(float currX, float currY, float targetX, float targetY)
        {
            float targetBearingDeg = GeometryUtils.GetBearingToTargetDeg(currX, currY, targetX, targetY);
            ++_moveLogCounter;
            _log($"[Move] Bearing:{targetBearingDeg:F1} Target:({targetX:F1},{targetY:F1})");

            // ── Heading freeze near waypoint ──
            // When close to the current waypoint, the bearing-to-target oscillates
            // wildly due to position jitter.  Freeze the last stable camera angle
            // and just keep moving forward until the waypoint is reached.
            if (_hasLastGameAngle)
            {
                float dist = GeometryUtils.Distance(currX, currY, targetX, targetY);
                // Use the same default threshold calculation as GetEffectiveWaypointReachThreshold
                // but with a minimum floor so very-precise waypoints don't break freeze.
                float reachBase = GeometryUtils.GetWaypointReachThreshold(MovementPrecision.Medium);
                float freezeThreshold = Math.Max(reachBase * 2.0f, HeadingFreezeDistanceBase);

                if (dist <= freezeThreshold)
                {
                    _log($"[Camera] Freeze near waypoint d={dist:F1} keep={_cameraLastAppliedAngle} th={freezeThreshold:F1}");
                    StartMoving();
                    return;
                }
            }

            ApplySteeringBearing(targetBearingDeg);
        }

        // ========================================================================
        //  STATE RESETS
        // ========================================================================

        private void ResetBearingState()
        {
            _lastSetBearingDeg = UnsetBearing;
            _hasLastGameAngle = false;
            _lastSetGameAngle = 0f;
            _hasCameraHysteresisCandidate = false;
        }

        private void ResetActionStuckTracking()
        {
            _stuckDetector.ResetTracking();
        }

        private void ResetCombatStateForWaypointChange()
        {
            _attackSuppressedForCurrentWaypoint = false;
            _combatHandler.ResetState();
            ClearCombatRetargetSearch();
            ReleaseSkillThree();
        }

        /// <summary>
        /// Starts ReverseDiagonalRecovery and applies the initial camera bearing + W key.
        /// </summary>
        private void StartReverseDiagonalRecovery(float currX, float currY, Waypoint target, string logSuffix)
        {
            _consecutiveStuckAttempts++;
            bool inCity = _memoryService.GetIsInCity();
            int maxAttempts = inCity ? STUCK_MAX_ATTEMPTS_IN_CITY : STUCK_MAX_ATTEMPTS_OUTSIDE;
            _log($"[Unstuck] Consecutive stuck attempts: {_consecutiveStuckAttempts}/{maxAttempts} ({(inCity ? "in city" : "outside")})");

            if (_consecutiveStuckAttempts >= maxAttempts)
            {
                _consecutiveStuckAttempts = 0;
                CaptureStuckScreenshot();
                _reverseDiagonalRecovery.Stop();

                if (inCity)
                {
                    _log($"[Unstuck] Reached {maxAttempts} stuck attempts in city — pressing 6 and waiting 10 minutes.");
                    StopMoving();
                    GameInput.PressKey(GameInput.VK_6, GameInput.SCAN_6);
                    _inCityStuckCooldownUntil = DateTime.Now.AddMinutes(10);
                }
                else
                {
                    _log($"[Unstuck] Reached {maxAttempts} stuck attempts outside — triggering ReportAndGoBack.");
                    _repotHelper.ReportAndGoBack();
                }

                return;
            }

            _reverseDiagonalRecovery.Start(currX, currY, target.X, target.Y,
                _lastHealthyMoveBearingDeg != UnsetBearing ? _lastHealthyMoveBearingDeg : (float?)null,
                _lastHealthyMoveTime);
            float bearingDeg = _reverseDiagonalRecovery.CurrentBearingDeg;
            ApplyCameraBearing(bearingDeg); // camera only — Recovery controls W
            _log($"[ReverseDiagonal] begin{logSuffix}. Bearing={bearingDeg:F1}°");
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(nint hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);

        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        private struct POINT { public int X; public int Y; }

        /// <summary>
        /// Finds the game window and captures its client area as a PNG file
        /// in Screenshots/Stuck/ with the current date/time.
        /// Called before triggering ReportAndGoBack after too many consecutive stuck attempts.
        /// </summary>
        private void CaptureStuckScreenshot()
        {
            try
            {
                nint hwnd = FindWindow(null, "Legend of Ares");
                if (hwnd == nint.Zero) hwnd = FindWindow(null, "Ares");
                if (hwnd == nint.Zero) hwnd = FindWindow(null, "Nostalgia");
                if (hwnd == nint.Zero) hwnd = FindWindow(null, "Epic Of Ares Client");

                int captureX = 0, captureY = 0, captureW = BotConstants.Loot.BitmapWidth, captureH = BotConstants.Loot.BitmapHeight;

                if (hwnd != nint.Zero)
                {
                    if (GetClientRect(hwnd, out RECT clientRect))
                    {
                        POINT topLeft = new POINT { X = 0, Y = 0 };
                        if (ClientToScreen(hwnd, ref topLeft))
                        {
                            captureX = topLeft.X;
                            captureY = topLeft.Y;
                            captureW = clientRect.Right - clientRect.Left;
                            captureH = clientRect.Bottom - clientRect.Top;
                        }
                    }
                }

                using (Bitmap bitmap = new Bitmap(captureW, captureH))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(captureX, captureY, 0, 0, bitmap.Size);

                    string screenshotsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Screenshots", "Stuck");
                    Directory.CreateDirectory(screenshotsDir);

                    string fileName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";
                    string filePath = Path.Combine(screenshotsDir, fileName);

                    bitmap.Save(filePath, ImageFormat.Png);
                    _log($"[Unstuck] Screenshot saved: {filePath}");
                }
            }
            catch (Exception ex)
            {
                _log($"[Unstuck] Failed to capture screenshot: {ex.Message}");
            }
        }

        // ========================================================================
        //  INPUT — W key management
        // ========================================================================

        private void StartMoving()
        {
            lock (_inputLock)
            {
                if (_isMovingForward)
                {
                    _log($"[Input] StartMoving called but already moving (call #{_startMoveCount + 1}) — skipping.");
                    return;
                }

                _isMovingForward = true;
                _startMoveCount++;
                _ignoreStuckUntil = DateTime.Now.AddSeconds(STUCK_GRACE_AFTER_START_SECONDS);

                _log($"[Input] W down (StartMoving) — call #{_startMoveCount}. Tick:{_tickCount}");

                GameInput.keybd_event(GameInput.VK_W, GameInput.SCAN_W, 0, 0);
            }
        }

        private void ForceStartMoving()
        {
            lock (_inputLock)
            {
                // Rate-limit: prevent spam ForceStartMoving (only allowed every FORCE_START_MIN_INTERVAL_MS)
                double msSinceLast = (DateTime.Now - _lastForceStartMovingAt).TotalMilliseconds;
                if (msSinceLast < FORCE_START_MIN_INTERVAL_MS && _isMovingForward)
                {
                    _log($"[Input] ForceStartMoving suppressed — rate-limit {msSinceLast:F0}ms/{FORCE_START_MIN_INTERVAL_MS:F0}ms");
                    return;
                }

                _lastForceStartMovingAt = DateTime.Now;
                _log($"[Input] ForceStartMoving — releasing W key (call #{_startMoveCount + 1}). Tick:{_tickCount}");
                GameInput.keybd_event(GameInput.VK_W, GameInput.SCAN_W, (uint)GameInput.KEYEVENTF_KEYUP, 0);
                Thread.Sleep(BotConstants.Delays.ForceStartReleaseGapMs);

                _isMovingForward = true;
                _startMoveCount++;
                _ignoreStuckUntil = DateTime.Now.AddSeconds(STUCK_GRACE_AFTER_START_SECONDS);

                _log($"[Input] W down (ForceStartMoving) — call #{_startMoveCount}. Tick:{_tickCount}");

                GameInput.keybd_event(GameInput.VK_W, GameInput.SCAN_W, 0, 0);
            }
        }

        public void StopMoving()
        {
            lock (_inputLock)
            {
                _isMovingForward = false;
                _stopMoveCount++;

                _log($"[Input] W up (StopMoving) — call #{_stopMoveCount}. Tick:{_tickCount}");

                GameInput.keybd_event(GameInput.VK_W, GameInput.SCAN_W, (uint)GameInput.KEYEVENTF_KEYUP, 0);
            }

            // Defensive cleanup: release A/D in case they were pressed by a previous version
            GameInput.keybd_event(GameInput.VK_A, GameInput.SCAN_A, (uint)GameInput.KEYEVENTF_KEYUP, 0);
            GameInput.keybd_event(GameInput.VK_D, GameInput.SCAN_D, (uint)GameInput.KEYEVENTF_KEYUP, 0);
        }

        // ── Skill 3 management ──

        private void ReleaseSkillThree()
        {
            if (_isSkillThreeHeld)
            {
                _log("[Key] 3 up");
                GameInput.keybd_event(GameInput.VK_3, GameInput.SCAN_3, (uint)GameInput.KEYEVENTF_KEYUP, 0);
                _isSkillThreeHeld = false;
            }
        }

        private void StartCombatRetargetSearch()
        {
            _combatRetargetCameraStage = CombatRetargetCameraStage.VeryLowSearch;
            _combatRetargetAwaitingSelection = false;
        }

        private void ClearCombatRetargetSearch()
        {
            _combatRetargetCameraStage = CombatRetargetCameraStage.None;
            _combatRetargetAwaitingSelection = false;
        }

        private short GetCombatRetargetCameraDistance()
        {
            return _combatRetargetCameraStage switch
            {
                CombatRetargetCameraStage.VeryLowSearch => CombatRetargetVeryLowCameraDistance,
                CombatRetargetCameraStage.LowSearch => CombatRetargetLowCameraDistance,
                CombatRetargetCameraStage.MidSearch => CombatRetargetMidCameraDistance,
                _ => CombatRetargetVeryLowCameraDistance
            };
        }

        // ========================================================================
        //  ACTION-BASED STUCK DETECTION
        // ========================================================================

        // ========================================================================
        //  LOCAL NAVIGATION MAP HELPERS
        // ========================================================================

        private void SaveLocalMapIfDirty()
        {
            if (_localNavigationMap.IsDirty)
            {
                _localNavigationMap.Save();
            }
        }

        /// <summary>
        /// Public entry point to persist the local navigation map.
        /// Call this from cleanup / shutdown paths so stuck-cell data is not lost
        /// when the bot is stopped or the path completes.
        /// </summary>
        public void SaveLocalMap()
        {
            _localNavigationMap.SaveIfDirty();
        }

        // ========================================================================
        //  MARK OBSTACLE FROM ACTION STUCK
        // ========================================================================

        private void MarkObstacleFromActionStuck(float currX, float currY, Waypoint target)
        {
            // Verify we really are stuck (action-based)
            byte action = _memoryService.GetCurrentAction();
            if (!StuckDetector.IsActionIdleOrStuck(action))
            {
                _log($"[MarkObstacle] Action={action} is not stuck state. Skipping marking.");
                return;
            }

            // 1. Mark current position as Free (we know we can stand here)
            _localNavigationMap.MarkFree(currX, currY, "current-position-before-action-stuck");

            // 2. Compute world-space direction based on current → target vector.
            //    The map coordinate system matches the world coordinates: X increases east,
            //    Y increases south.  Therefore (1,-1) = east + north = north-east.
            float dx = target.X - currX;
            float dy = target.Y - currY;
            const float eps = 0.3f;

            int attemptedDirX = dx > eps ? 1 : (dx < -eps ? -1 : 0);
            int attemptedDirY = dy > eps ? 1 : (dy < -eps ? -1 : 0);

            float? attemptedBearing = _lastSetBearingDeg != UnsetBearing
                ? _lastSetBearingDeg
                : (float?)null;

            // 3. Compute attempted cell
            (int sourceCellX, int sourceCellY) = LocalNavigationMap.WorldToCell(currX, currY);
            int attemptedCellX = sourceCellX + attemptedDirX;
            int attemptedCellY = sourceCellY + attemptedDirY;

            // 4. Determine reason string
            string reason = attemptedBearing.HasValue
                ? "blocked-when-moving-bearing"
                : "blocked-when-moving-to-waypoint";

            // 5. Mark the attempted cell as Risky or Blocked depending on confidence
            (int targetCellX, int targetCellY) = LocalNavigationMap.WorldToCell(target.X, target.Y);

            _localNavigationMap.MarkStuckAttemptedCell(
                attemptedCellX + 0.5f, attemptedCellY + 0.5f,
                reason,
                attemptedBearing,
                attemptedDirX, attemptedDirY,
                sourceCellX, sourceCellY,
                targetCellX, targetCellY);

            _log($"[ActionStuck] Marked: source=({sourceCellX},{sourceCellY}) attempted=({attemptedCellX},{attemptedCellY}) dir=({attemptedDirX},{attemptedDirY}) bearing={attemptedBearing} reason={reason}");
        }

        // ========================================================================
        //  ROUTE RESYNC AFTER COMBAT
        // ========================================================================

        /// <summary>
        /// Finds the index of the current queue-peek waypoint within <see cref="_initialPath"/>.
        /// Returns 0 if no match is found or the queue is empty.
        /// </summary>
        private int GetCurrentWaypointIndex()
        {
            if (_waypoints.Count == 0 || _initialPath.Count == 0)
                return 0;

            var current = _waypoints.Peek();
            for (int i = 0; i < _initialPath.Count; i++)
            {
                if (GeometryUtils.Distance(current.X, current.Y, _initialPath[i].X, _initialPath[i].Y) < 0.5f)
                    return i;
            }
            return 0;
        }

        /// <summary>
        /// Finds the index of the nearest waypoint in <see cref="_initialPath"/>
        /// to the given world position. Fallback when player is far from all segments.
        /// </summary>
        private int FindNearestWaypointIndex(float currX, float currY)
        {
            if (_initialPath.Count == 0) return 0;

            int bestIdx = 0;
            float bestDist = float.MaxValue;
            for (int i = 0; i < _initialPath.Count; i++)
            {
                float d = GeometryUtils.Distance(currX, currY, _initialPath[i].X, _initialPath[i].Y);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestIdx = i;
                }
            }
            return bestIdx;
        }

        /// <summary>
        /// Rebuilds <see cref="_waypoints"/> from <paramref name="startIndex"/> to the end of
        /// <see cref="_initialPath"/>. Does nothing if the new target is effectively the same
        /// as the current queue peek (within 0.5 distance). Clears relevant state on change.
        /// </summary>
        /// <returns>
        /// <see cref="RouteResyncResult.TerminalSkip"/> if <paramref name="startIndex"/> is invalid;
        /// <see cref="RouteResyncResult.SameTarget"/> if the new target equals the current queue peek;
        /// <see cref="RouteResyncResult.Applied"/> if the queue was rebuilt.
        /// </returns>
        private RouteResyncResult RebuildWaypointQueueFromIndex(int startIndex, Waypoint oldTarget)
        {
            if (startIndex < 0 || startIndex >= _initialPath.Count)
            {
                _log($"[RouteResync] Rebuild skipped: invalid startIndex={startIndex}");
                return RouteResyncResult.TerminalSkip;
            }

            // Check if new target is effectively the same as current target
            if (_waypoints.Count > 0)
            {
                var currentTarget = _waypoints.Peek();
                var newTarget = _initialPath[startIndex];
                if (GeometryUtils.Distance(currentTarget.X, currentTarget.Y, newTarget.X, newTarget.Y) < 0.5f)
                {
                    _log($"[RouteResync] Rebuild skipped: same target (index={startIndex})");
                    return RouteResyncResult.SameTarget;
                }
            }

            int oldCount = _waypoints.Count;
            _waypoints.Clear();

            for (int i = startIndex; i < _initialPath.Count; i++)
            {
                _waypoints.Enqueue(_initialPath[i]);
            }

            _log($"[RouteResync] Queue rebuilt: oldCount={oldCount} newCount={_waypoints.Count} startIndex={startIndex}");

            // Reset movement state since the target changed
            ResetBearingState();
            ResetActionStuckTracking();
            ResetCombatStateForWaypointChange();
            _consecutiveStuckAttempts = 0;

            return RouteResyncResult.Applied;
        }

        /// <summary>
        /// Called after combat ends. Synchronizes the movement queue with the player's
        /// actual world position by finding the nearest route segment and updating the
        /// next target accordingly.
        /// </summary>
        /// <returns>RouteResyncResult indicating whether the queue was changed or why it was skipped.</returns>
        private RouteResyncResult RouteResyncFromCurrentPosition(float currX, float currY)
        {
            // ── Guard conditions ──
            if (_initialPath.Count < 2)
            {
                _log("[RouteResync] skipped terminal: path-too-short (count < 2)");
                return RouteResyncResult.TerminalSkip;
            }
            if (_isUnstuckRoutineActive)
            {
                _log("[RouteResync] skipped temporary: unstuck-active");
                return RouteResyncResult.TemporarySkip;
            }
            if (_repotHelper.IsReportAndGoBackActive)
            {
                _log("[RouteResync] skipped temporary: report-and-go-back-active");
                return RouteResyncResult.TemporarySkip;
            }
            if (_goalReached)
            {
                _log("[RouteResync] skipped terminal: goal-reached");
                return RouteResyncResult.TerminalSkip;
            }

            var oldTarget = _waypoints.Count > 0
                ? _waypoints.Peek()
                : new Waypoint(0, 0, MovementPrecision.Medium, BotMode.OnlyMove);

            int currentTargetIdx = _waypoints.Count > 0 ? GetCurrentWaypointIndex() : -1;

            // ── Find the best (nearest) route segment ──
            int bestSegmentStart = -1;
            int bestSegmentEnd = -1;
            float bestDist = float.MaxValue;
            float bestT = 0f;

            int normalSegmentCount = _initialPath.Count - 1;
            int segmentCount = normalSegmentCount + (LoopPath ? 1 : 0);

            for (int i = 0; i < segmentCount; i++)
            {
                int idxA = i;
                int idxB = i + 1;

                // Handle wrap-around segment for loop paths: last waypoint -> first waypoint
                if (i >= normalSegmentCount)
                {
                    if (LoopPath)
                    {
                        idxA = _initialPath.Count - 1;
                        idxB = 0;
                    }
                    else
                    {
                        break;
                    }
                }

                var a = _initialPath[idxA];
                var b = _initialPath[idxB];

                var (_, _, t, dist) = GeometryUtils.ProjectPointOnSegment(
                    currX, currY, a.X, a.Y, b.X, b.Y);

                // Primary criterion: smallest distance to segment
                const float tieEpsilon = BotConstants.Movement.RouteResyncTieDistanceEpsilon;
                if (dist < bestDist - tieEpsilon)
                {
                    bestDist = dist;
                    bestSegmentStart = idxA;
                    bestSegmentEnd = idxB;
                    bestT = t;
                }
                else if (dist <= bestDist + tieEpsilon && currentTargetIdx >= 0)
                {
                    // Tie-breaker: prefer segment whose end index is closer to the current queue target
                    int segmentEndIdx = (LoopPath && idxA == _initialPath.Count - 1) ? 0 : idxA + 1;
                    int bestEndIdx = (LoopPath && bestSegmentStart == _initialPath.Count - 1) ? 0 : bestSegmentStart + 1;

                    int newDiff = Math.Abs(segmentEndIdx - currentTargetIdx);
                    int bestDiff = Math.Abs(bestEndIdx - currentTargetIdx);

                    if (newDiff < bestDiff)
                    {
                        bestDist = dist;
                        bestSegmentStart = idxA;
                        bestSegmentEnd = idxB;
                        bestT = t;
                    }
                }
            }

            if (bestSegmentStart < 0)
            {
                _log("[RouteResync] skipped terminal: no-valid-segment");
                return RouteResyncResult.TerminalSkip;
            }

            // ── Determine next waypoint index ──
            string reason;
            int nextWpIndex;

            float maxSegDist = BotConstants.Movement.RouteResyncMaxSegmentDistance;

            if (bestDist > maxSegDist)
            {
                // Fallback: player is far from all segments — go to nearest waypoint
                nextWpIndex = FindNearestWaypointIndex(currX, currY);
                reason = $"fallback-nearest-wp d={bestDist:F2}";
            }
            else if (bestT >= BotConstants.Movement.RouteResyncVeryCloseToSegmentEndT)
            {
                // Very close to the end of this segment — skip to the next segment
                if (LoopPath && bestSegmentStart == _initialPath.Count - 1)
                {
                    // Wrap segment: last->first. End = 0, next after end = 1.
                    nextWpIndex = 1;
                }
                else if (LoopPath && bestSegmentStart == _initialPath.Count - 2)
                {
                    // Last normal segment before wrap (N-2 -> N-1). Next after end wraps to 0.
                    nextWpIndex = 0;
                }
                else
                {
                    nextWpIndex = bestSegmentStart + 2;
                }

                if (!LoopPath && nextWpIndex >= _initialPath.Count)
                    nextWpIndex = _initialPath.Count - 1;

                reason = "near-end-of-segment";
            }
            else
            {
                // In the middle or near the start — go to end of this segment
                if (LoopPath && bestSegmentStart == _initialPath.Count - 1)
                {
                    // Wrap segment: last->first. End = 0.
                    nextWpIndex = 0;
                }
                else
                {
                    nextWpIndex = bestSegmentStart + 1;
                }

                if (!LoopPath && nextWpIndex >= _initialPath.Count)
                    nextWpIndex = _initialPath.Count - 1;

                reason = "nearest-segment";
            }

            // ── Non-loop guard: if already near the last waypoint, let natural completion handle it ──
            if (!LoopPath && nextWpIndex >= _initialPath.Count - 1)
            {
                float distToLast = GeometryUtils.Distance(currX, currY, _initialPath[^1].X, _initialPath[^1].Y);
                if (distToLast <= BotConstants.Movement.RouteResyncNearWaypointDistance)
                {
                    _log($"[RouteResync] skipped terminal: already-near-last-waypoint d={distToLast:F2}");
                    return RouteResyncResult.TerminalSkip;
                }
            }

            // ── Log and apply ──
            _log($"[RouteResync] bestSegment={bestSegmentStart}->{bestSegmentEnd} t={bestT:F2} dist={bestDist:F2} nextIndex={nextWpIndex} reason={reason}");
            _log($"[RouteResync] oldTarget=({oldTarget.X:F1},{oldTarget.Y:F1}) newTarget=({_initialPath[nextWpIndex].X:F1},{_initialPath[nextWpIndex].Y:F1})");

            return RebuildWaypointQueueFromIndex(nextWpIndex, oldTarget);
        }

        // ========================================================================
        //  SOFT SKIP
        // ========================================================================

    }
}
