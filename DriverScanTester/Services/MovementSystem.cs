using System;
using System.Collections.Generic;
using System.Linq;
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

    public struct Waypoint
    {
        public float X { get; set; }
        public float Y { get; set; }
        public MovementPrecision Precision { get; set; }
        public BotMode Mode { get; set; }

        public Waypoint(float x, float y, MovementPrecision precision, BotMode mode)
        {
            X = x;
            Y = y;
            Precision = precision;
            Mode = mode;
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

        // Temporary ghost waypoint tracking
        private readonly List<(float X, float Y)> _ghostWaypoints = new List<(float X, float Y)>();
        private const float GHOST_MATCH_EPSILON = 0.35f;
        private const float GHOST_REACH_THRESHOLD = 7.0f;
        // Bearing state
        private const float UnsetBearing = -999f;
        private float _lastSetBearingDeg = UnsetBearing;
        private bool _hasLastGameAngle = false;
        private float _lastSetGameAngle = 0f;

        // Stuck Detection
        private DateTime _ignoreStuckUntil = DateTime.MinValue;
        private const double STUCK_GRACE_AFTER_START_SECONDS = 1.25;

        // Progress tracker
        private readonly MovementProgressTracker _progressTracker;

        // Near-target stuck ignore
        private const float NEAR_TARGET_STUCK_IGNORE_EXTRA = 1.0f;

        // Final-waypoint soft completion
        private const float FINAL_GOAL_SOFT_RADIUS = 3.0f;
        private const double FINAL_GOAL_STALL_TIME = 2.0;
        private DateTime _finalGoalLastProgressTime = DateTime.MinValue;
        private float _finalGoalBestDist = float.MaxValue;

        // ────────────────────────────────────────────────────────
        //  LOCAL NAVIGATION MAP
        // ────────────────────────────────────────────────────────

        private const float LOCAL_MAP_CELL_SIZE = 1.0f;

        private readonly LocalNavigationMap _localNavigationMap;
        private int _currentMapId = -1;

        // ────────────────────────────────────────────────────────
        //  ACTION-BASED STUCK CONSTANTS
        // ────────────────────────────────────────────────────────

        private const float STUCK_SOFT_SKIP_DISTANCE = 5.5f;

        // (Bug2 state moved to Bug2Recovery strategy class)

        private bool _isUnstuckRoutineActive = false;
        private const double FORCE_START_MIN_INTERVAL_MS = 700.0;
        private DateTime _lastForceStartMovingAt = DateTime.MinValue;

        private readonly ReverseDiagonalRecovery _reverseDiagonalRecovery;
        private readonly StuckDetector _stuckDetector;
        private readonly WaypointSkipPolicy _skipPolicy;
        private readonly Bug2Recovery _bug2Recovery;

        // Keyboard steering controller (replaces direct SetCameraAngle)
        private readonly KeyboardSteeringController _steeringController;

        // Healthy movement tracking
        private float _lastHealthyMoveBearingDeg = UnsetBearing;
        private (float X, float Y) _lastHealthyMovePos;
        private DateTime _lastHealthyMoveTime = DateTime.MinValue;

        // Position jump detection — reset tracker if one tick moves more than this
        private const float MAX_REASONABLE_TICK_MOVEMENT = 8.0f;

        // Camera distance only.
        private const short CameraDistanceLock = 16980;

        // Camera is now set directly each tick — no smoothing, no deadzone, no rate limiting.
        // All previous smoothing constants (CAMERA_MIN_UPDATE_INTERVAL_MS,
        // CAMERA_MAX_STEP_DEG_*, CAMERA_NORMAL_DEADZONE_DEG, etc.) removed.

        // Input
        private readonly object _inputLock = new object();

        private int _moveLogCounter = 0;
        private int _tickCount = 0;
        private int _stateLogInterval = 5; // Log periodic state every N ticks

        private bool _isMovingForward = false;
        private int _startMoveCount = 0;
        private int _stopMoveCount = 0;
        private static readonly Random _rng = new Random();

        public MovementSystem(GameMemoryService memoryService, Action<string> log, float targetX, float targetY, MovementPrecision precision = MovementPrecision.Medium, IEnumerable<Waypoint>? customPath = null, BotMode initialMode = BotMode.OnlyMove, bool loopPath = false)
        {
            _memoryService = memoryService;
            _log = log;
            _combatHandler = new CombatHandler(log);
            _repotHelper = new RepotHelper(memoryService, log, StopMoving, () => _goalReached = true);
            _progressTracker = new MovementProgressTracker(log);
            Waypoint2 = (targetX, targetY);
            GlobalPrecision = precision;
            LoopPath = loopPath;

            int initialMapId = _memoryService.GetMapNumber();
            _currentMapId = initialMapId;
            _reverseDiagonalRecovery = new ReverseDiagonalRecovery(_memoryService, log);
            _stuckDetector = new StuckDetector(_memoryService, log, GetEffectiveWaypointReachThreshold, NEAR_TARGET_STUCK_IGNORE_EXTRA);
            _skipPolicy = new WaypointSkipPolicy(_memoryService, log, GetEffectiveWaypointReachThreshold);
            _localNavigationMap = new LocalNavigationMap(_log, initialMapId);
            _steeringController = new KeyboardSteeringController(_memoryService, log);
            _bug2Recovery = new Bug2Recovery(
                _memoryService, log, _localNavigationMap,
                StopMoving, ForceStartMoving,
                (x, y, tx, ty) => MoveTowards(x, y, tx, ty),
                (x, y) => AdvanceReachedWaypoints(x, y),
                () => SaveLocalMapIfDirty(),
                (x, y, wp, q, rb, rpt, ra) => _skipPolicy.TrySkip(x, y, wp, q, rb, rpt, ra),
                ResetBearingState, ResetActionStuckTracking,
                () => _lastSetGameAngle, () => _hasLastGameAngle,
                v => _lastSetBearingDeg = v, v => _hasLastGameAngle = v, v => _lastSetGameAngle = v,
                ApplySteeringBearing);

            _isInitialized = true;
            _log($"MovementSystem: Initialized with GameMemoryService, Default Precision: {GlobalPrecision}, Loop: {LoopPath}");
            _log($"[LocalMap] Initial map ID = {initialMapId}.");
            _log("[BearingCalib] Using manual N/E/S/W bearing table: N=16581, E=16632, S=16662, W=16688, N2=16710.");

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
                    _log($"[Path] #{index}: ({p.X:F1}, {p.Y:F1}) Precision:{p.Precision} Mode:{p.Mode}");
                    index++;
                }
            }
            else
            {
                var wp = new Waypoint(Waypoint2.X, Waypoint2.Y, GlobalPrecision, initialMode);
                _waypoints.Enqueue(wp);
                _log($"[Path] Single target: ({wp.X:F1}, {wp.Y:F1}) Precision:{wp.Precision} Mode:{wp.Mode}");
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
                _log($"[Tick {_tickCount}] Skipped — goal already reached");
                return;
            }

            token.ThrowIfCancellationRequested();

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

            // Keep distance only.
            _memoryService.SetCameraDistance(CameraDistanceLock);

            // ── Attack speed / potion check ──
            if (_combatHandler.CheckAttackSpeed(_memoryService))
            {
                _log($"[Tick {_tickCount}] Speed 16341 — using potions");
                _log("[Key] 7 (pot1)");
                GameInput.PressKey(GameInput.VK_7, GameInput.SCAN_7);
                await Task.Delay(500, token);
                _log("[Key] 8 (pot2)");
                GameInput.PressKey(GameInput.VK_8, GameInput.SCAN_8);
            }

            BotMode currentMode = BotMode.OnlyMove;
            if (_waypoints.Count > 0)
            {
                currentMode = _waypoints.Peek().Mode;
            }

            // ── Combat mode handling ──
            var combatAction = _combatHandler.EvaluateCombatAction(_memoryService, currentMode, _isUnstuckRoutineActive);
            if (combatAction != CombatAction.None)
            {
                    _log($"[Tick {_tickCount}] Combat: {combatAction}");
            }
            switch (combatAction)
            {
                case CombatAction.TabTarget:
                    _log("[Key] TAB (target cycle)");
                    GameInput.PressKey(GameInput.VK_TAB, GameInput.SCAN_TAB);
                    await Task.Delay(50, token);
                    return;

                case CombatAction.Attack:
                    _log("[Key] 3 (attack skill)");
                    StopMoving();
                    GameInput.PressKey(GameInput.VK_3, GameInput.SCAN_3);
                    await Task.Delay(50, token);
                    return;

                case CombatAction.StuckTabAttack:
                    _log("[Key] TAB (target cycle — stuck in attack)");
                    GameInput.PressKey(GameInput.VK_TAB, GameInput.SCAN_TAB);
                    await Task.Delay(50, token);
                    return;

                case CombatAction.UnstuckNeeded:
                    var (combatStuckX, combatStuckY, combatStuckOk) = _memoryService.GetPlayerPosition();
                    _log($"[Tick {_tickCount}] CombatHandler requested UnstuckNeeded. Pos=({combatStuckX:F1},{combatStuckY:F1}) ok={combatStuckOk}");
                    _combatHandler.ResetStuckInAttack();
                    if (combatStuckOk && _waypoints.Count > 0 && !_bug2Recovery.IsActive)
                    {
                        byte combatActionStuck = _memoryService.GetCurrentAction();
                        if (StuckDetector.IsActionIdleOrStuck(combatActionStuck))
                        {
                            var combatTarget = _waypoints.Peek();
                            _log($"[CombatUnstuck] Action={combatActionStuck} stuck. Starting reverse-diagonal recovery.");
                            StartReverseDiagonalRecovery(combatStuckX, combatStuckY, combatTarget, " from combat");
                        }
                        else
                        {
                            _log($"[CombatUnstuck] Action={combatActionStuck} is not stuck. Letting normal movement handle it.");
                        }
                    }
                    return;

                case CombatAction.PotionsUsed:
                    _log($"[Tick {_tickCount}] Potions used — brief delay.");
                    await Task.Delay(50, token);
                    return;
            }

            var (currX, currY, success) = _memoryService.GetPlayerPosition();
            if (!success)
            {
                _log($"[Tick {_tickCount}] [Pos] Read failed");
                return;
            }

            // Position jump / outlier detection: if one tick moves more than MAX_REASONABLE_TICK_MOVEMENT, reset tracker
            if (_progressTracker.HasSamples)
            {
                float tickDelta = GeometryUtils.Distance(currX, currY, _progressTracker.LastX, _progressTracker.LastY);
                if (tickDelta > MAX_REASONABLE_TICK_MOVEMENT)
                {
                    _log($"[Progress] Jump! Δ{tickDelta:F1} > {MAX_REASONABLE_TICK_MOVEMENT:F0} — reset");
                    _progressTracker.Reset();
                }
            }

            // Log position every 5 ticks
            if (_tickCount % 5 == 0)
            {
                _log($"[Tick {_tickCount}] @ ({currX:F1},{currY:F1}) Cam:{_memoryService.GetCameraAngle()}");
            }

            byte currentAction = _memoryService.GetCurrentAction();

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
                float reachThreshold = GetEffectiveWaypointReachThreshold(activeTarget);

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
                    _log($"[Tick {_tickCount}] Goal reached ✓");
                    return;
                }

                if (_waypoints.Count == 0)
                {
                    _log($"[Tick {_tickCount}] WpQueue empty after advance");
                    return;
                }

                var target = _waypoints.Peek();
                float distNow = GeometryUtils.Distance(currX, currY, target.X, target.Y);
                float thresholdNow = GetEffectiveWaypointReachThreshold(target);

                // ── Active ReverseDiagonalRecovery ──
                if (_reverseDiagonalRecovery.IsActive)
                {
                    RecoveryResult recoveryResult = _reverseDiagonalRecovery.Tick(currX, currY);
                    float bearingDeg = _reverseDiagonalRecovery.CurrentBearingDeg;
                    ApplySteeringBearing(bearingDeg);
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
                            if (_skipPolicy.TrySkip(currX, currY, target, _waypoints, ResetBearingState, () => _progressTracker.Reset(), ResetActionStuckTracking))
                            {
                                _log($"[Bug2Gate] skip waypoint before map marking.");
                                _bug2Recovery.Reset();
                                return;
                            }
                            _log($"[Bug2Gate] marking obstacle and entering Bug2.");
                            _log($"[ActionStuck] Action={currentAction} while moving. Stuck confirmed by action.");
                            MarkObstacleFromActionStuck(currX, currY, target);
                            _localNavigationMap.SaveIfDirty();
                            _bug2Recovery.Enter(currX, currY, target);
                            return;
                    }
                }

                // ── Bug2 / action-stuck local navigation ──
                if (_bug2Recovery.IsActive)
                {
                    _bug2Recovery.RunStep(currX, currY, target, _waypoints, GetEffectiveWaypointReachThreshold, () => _progressTracker.Reset());
                    return;
                }

                if (_stuckDetector.IsActionStuck(currX, currY, target, _isMovingForward))
                {
                    _log($"[ActionStuck] Action={currentAction} while moving. Starting ReverseDiagonalRecovery.");
                    StartReverseDiagonalRecovery(currX, currY, target, "");
                    return;
                }

                // Set segment only when target changes, not every tick.
                if (!_progressTracker.HasSegment ||
                    _progressTracker.SegmentEndX != target.X ||
                    _progressTracker.SegmentEndY != target.Y)
                {
                    _progressTracker.SetSegment(currX, currY, target.X, target.Y);
                }
                _progressTracker.RecordSample(currX, currY, target.X, target.Y, currentAction);

                if (_tickCount % _stateLogInterval == 0)
                {
                    string ghostFlag = IsGhostWaypoint(target) ? " [GHOST]" : "";
                    string progStatus = _progressTracker.GetStatusString();
                    _log($"[Route] T:{_tickCount} WP{ghostFlag}({target.X:F1},{target.Y:F1}) d:{distNow:F2} th:{thresholdNow:F2} M:{target.Mode} P:{target.Precision} {progStatus} Cam:{_memoryService.GetCameraAngle()}");
                }

                // Track healthy movement bearing for escape direction
                if (!_isUnstuckRoutineActive && currentAction != 25 && currentAction != 1 &&
                    _progressTracker.HasEnoughSamples && !_progressTracker.IsWarmingUp)
                {
                    float disp = _progressTracker.GetWindowDisplacement();
                    float distDelta = _progressTracker.GetWindowDistDelta();
                    if (disp > 0.5f && distDelta < 0.5f) // moving and not worsening
                    {
                        _lastHealthyMoveBearingDeg = GeometryUtils.GetBearingToTargetDeg(currX, currY, target.X, target.Y);
                        _lastHealthyMovePos = (currX, currY);
                        _lastHealthyMoveTime = DateTime.Now;
                    }
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

        public void TestMove(short angle)
        {
            _log($"[TestMove] Setting camera to raw game angle {angle}");
            _memoryService.SetCameraAngle(angle);
            _hasLastGameAngle = true;
            _lastSetGameAngle = angle;
            _log("[TestMove] StartMoving (W press)");
            StartMoving();
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
                ResetBearingState();
                _progressTracker.Reset();
                ResetActionStuckTracking();

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
                _progressTracker.Reset();
                ResetActionStuckTracking();
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
                threshold = Math.Max(threshold, 1.5f);
            }

            if (IsGhostWaypoint(waypoint))
            {
                return Math.Max(threshold, GHOST_REACH_THRESHOLD);
            }

            return threshold;
        }

        /// <summary>
        /// Applies a desired bearing: uses keyboard steering (A/D) when enabled,
        /// falls back to legacy SetCameraAngle otherwise.
        /// Always ensures W is held for forward movement.
        /// </summary>
        private void ApplySteeringBearing(float bearingDeg)
        {
            _lastSetBearingDeg = bearingDeg;
            if (MovementTuning.UseKeyboardSteering)
            {
                short gameAngle = GeometryUtils.ConvertBearingToGameAngle(bearingDeg, _lastSetGameAngle, _hasLastGameAngle);
                _hasLastGameAngle = true;
                _lastSetGameAngle = gameAngle;
                _steeringController.SteerTowards(bearingDeg, keepMovingForward: true);
                StartMoving();
            }
            else
            {
                short gameAngle = GeometryUtils.ConvertBearingToGameAngle(bearingDeg, _lastSetGameAngle, _hasLastGameAngle);
                _lastSetGameAngle = gameAngle;
                _hasLastGameAngle = true;
                _log($"[Move-Legacy] Bearing:{bearingDeg:F1} GameAngle:{gameAngle}");
                _memoryService.SetCameraAngle(gameAngle);
                StartMoving();
            }
        }

        private void MoveTowards(float currX, float currY, float targetX, float targetY)
        {
            float targetBearingDeg = GeometryUtils.GetBearingToTargetDeg(currX, currY, targetX, targetY);
            ++_moveLogCounter;
            _log($"[Move] Bearing:{targetBearingDeg:F1} Target:({targetX:F1},{targetY:F1})");
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
        }

        private void ResetActionStuckTracking()
        {
            _stuckDetector.ResetTracking();
        }

        /// <summary>
        /// Starts ReverseDiagonalRecovery and applies the initial camera bearing + W key.
        /// Used by both normal action-stuck flow and CombatAction.UnstuckNeeded.
        /// </summary>
        private void StartReverseDiagonalRecovery(float currX, float currY, Waypoint target, string logSuffix)
        {
            _reverseDiagonalRecovery.Start(currX, currY, target.X, target.Y,
                _lastHealthyMoveBearingDeg != UnsetBearing ? _lastHealthyMoveBearingDeg : (float?)null,
                _lastHealthyMoveTime);
            float bearingDeg = _reverseDiagonalRecovery.CurrentBearingDeg;
            ApplySteeringBearing(bearingDeg);
            _log($"[ReverseDiagonal] begin{logSuffix}. Bearing={bearingDeg:F1}°");
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
                Thread.Sleep(25);

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

            // Release steering keys (A/D) when stopping
            if (MovementTuning.UseKeyboardSteering)
            {
                _steeringController.ReleaseSteering();
            }
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
        //  SOFT SKIP
        // ========================================================================

    }
}
