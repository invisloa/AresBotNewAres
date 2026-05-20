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
        private const float UNSTUCK_TARGET_REACHED_EXTRA = 1.0f;
        private const float REAL_WAYPOINT_LOOKAHEAD_REACH_EXTRA = 2.5f;
        private const int REAL_WAYPOINT_LOOKAHEAD_LIMIT = 5;

        // Bearing state
        private const float UnsetBearing = -999f;
        private float _lastSetBearingDeg = UnsetBearing;
        private bool _hasLastGameAngle = false;
        private float _lastSetGameAngle = 0f;

        // Stuck Detection
        private DateTime _ignoreStuckUntil = DateTime.MinValue;
        private const double STUCK_GRACE_AFTER_START_SECONDS = 1.25;

        // Action-based stuck detection (secondary, for idle-action scenarios)
        private int _actionStuckCounter = 0;
        private const double ACTION_STUCK_GRACE_SECONDS = 0.5;
        private const int ActionStuckThreshold = 3;

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

        // ────────────────────────────────────────────────────────
        //  ACTION-BASED STUCK CONSTANTS
        // ────────────────────────────────────────────────────────

        private const float STUCK_SOFT_SKIP_DISTANCE = 5.5f;

        // ────────────────────────────────────────────────────────
        //  BUG2 NAVIGATION STATE
        // ────────────────────────────────────────────────────────

        private enum Bug2NavState
        {
            None,
            MoveToTarget,
            FollowBoundary
        }

        private Bug2NavState _bug2State = Bug2NavState.None;
        private Waypoint? _bug2Target;
        private (float X, float Y) _bug2StartPoint;
        private (float X, float Y) _bug2HitPoint;
        private float _bug2HitDistanceToTarget;
        private bool _bug2FollowLeft = true;
        private DateTime _bug2StartedAt;
        private int _bug2StepCount;
        private int _bug2FailedMoveCount;
        private int _bug2SameSideSteps;

        // Bug2 candidate testing (multi-tick)
        private bool _bug2CandidateIssued;
        private int _bug2CandidateIndex;
        private float _bug2CandidateTestBearing;
        private int _bug2CandidateTestCellX;
        private int _bug2CandidateTestCellY;
        private int _bug2CandidateAttemptedDirX;
        private int _bug2CandidateAttemptedDirY;
        private DateTime _bug2CandidateStartTime;

        private const double BUG2_CANDIDATE_OBSERVE_MS = 700.0;
        private const int BUG2_MAX_TOTAL_STEPS = 40;
        private const int BUG2_MAX_FAILED_MOVES = 12;
        private const double BUG2_MAX_DURATION_SECONDS = 20.0;
        private const float BUG2_M_LINE_TOLERANCE = 1.5f;
        private const float BUG2_LEAVE_MIN_IMPROVEMENT = 1.0f;
        private const int BUG2_MAX_STEPS_BEFORE_SIDE_SWITCH = 8;

        // ────────────────────────────────────────────────────────
        //  UNSTUCK STATE
        // ────────────────────────────────────────────────────────

        // Stages: 0=none, 1=preflight, 2=probes, 3=wallfollow, 4=bypass, 5=lastresort
        private bool _isUnstuckRoutineActive = false;
        private int _unstuckStage = 0;
        private DateTime _unstuckStageStartTime;
        private bool _unstuckStageCommandIssued = false;
        private DateTime _unstuckStageCommandAllowedAt = DateTime.MinValue;
        private DateTime _unstuckCooldownUntil = DateTime.MinValue;
        private const double UNSTUCK_COOLDOWN_SECONDS = 0.75;
        private const double UNSTUCK_STAGE_INPUT_RELEASE_MS = 100.0;

        // Escalation tracking
        private int _totalEscalationAttempts = 0;
        private (float X, float Y) _firstStuckLocation;
        private const float ESCALATION_AREA_RESET_DISTANCE = 30.0f;
        private const float ESCALATION_REPORT_DISTANCE = 10.0f;
        private const int ESCALATION_LIMIT = 10;

        // Anchor (where stuck was declared)
        private (float X, float Y) _stuckAnchor;
        private Waypoint _unstuckTarget;
        private bool _hasUnstuckTarget = false;
        private float _unstuckStartDistToTarget = 0f;
        private float _unstuckOriginalTargetBearing = 0f;

        // Best probe recovery state
        private (float X, float Y) _bestProbePosition;
        private float _bestProbeScore = 0f;
        private float _bestProbeDistToTarget = float.MaxValue;
        private int _bestProbeIndex = -1;
        private float _bestProbeOffset = 0f;

        // Probe iteration
        private int _currentProbeIndex = 0;
        private int _consecutiveNoMovementProbes = 0;
        private DateTime _probeStartTime;
        private (float X, float Y) _probeStartPos;
        private float _probeStartDist;
        private float _currentProbeOffset = 0f;
        private float _currentEscapeBearing = 0f;
        private float _currentEscapeOffset = 0f;
        private const double PROBE_INITIAL_DURATION = 0.350;     // 350ms
        private const double PROBE_EXTENDED_DURATION = 0.700;    // 700ms if improving
        private const double PROBE_MAX_DURATION = 0.950;         // 950ms cap
        private const float PROBE_EARLY_ABORT_WORSEN = 1.5f;     // abort if worsened by >1.5
        private const float PROBE_MIN_MOVEMENT = 0.25f;          // min movement to continue
        private const double PROBE_MIN_MOVEMENT_TIME = 0.200;    // 200ms to achieve min movement (must be < PROBE_INITIAL_DURATION 0.350)
        private const float PROBE_ROLLBACK_WORSEN = 1.5f;        // rollback if worsened by >1.5
        private const float PROBE_MAX_RADIUS = 30.0f;            // max anchor distance

        // Ordered probe offsets: first ring (shallow), second ring, third ring, then full reverse
        // Kept for rejoin phase; escape phase uses EscapeProbeOffsets below.
        private static readonly float[] UnstuckProbeAngles =
        {
            +35f, -35f,
            +60f, -60f,
            +90f, -90f,
            +120f, -120f,
            +150f, -150f,
            180f,
            +170f, -170f
        };

        // Escape probe offsets (relative to approachBearing, not targetBearing).
        // Order: back-left, back-right, harder back-left, harder back-right,
        //        shallower back-left, shallower back-right, pure back, side strafes
        private static readonly float[] EscapeProbeOffsets =
        {
            +135f, -135f,     // back-left, back-right
            +150f, -150f,     // harder back-left, back-right
            +120f, -120f,     // shallower back-left, back-right
            +180f,            // pure back
            +90f,  -90f       // left strafe, right strafe
        };

        // Sweep unstuck state
        private int _sweepSide = 0;                          // +1 (Right) or -1 (Left)
        private int _sweepSameSideAttempts = 0;              // how many times we retried same side
        private int _sweepTotalAttempts = 0;                 // total sweep attempts this episode
        private float _sweepStartBearing = UnsetBearing;     // camera bearing when sweep started
        private float _sweepCurrentBearing = UnsetBearing;   // current sweep bearing
        private float _sweepAccumulatedAngle = 0f;           // how many degrees swept so far this attempt
        private float _sweepBestClearance = 0f;              // best anchor clearance achieved this episode
        private DateTime _sweepLastCameraUpdateAt = DateTime.MinValue;
        private bool _sweepFirstTick = true;
        private int _sweepNoMovementRefreshCount = 0;

        // Sweep runtime state (new fields for Stage 2)
        private DateTime _sweepAttemptStartTime = DateTime.MinValue;
        private float _sweepStartPosX;
        private float _sweepStartPosY;
        private float _sweepStartBearingDeg = UnsetBearing;
        private float _sweepSweptDeg = 0f;
        private int _sweepInputRefreshCountThisAttempt = 0;
        private int _sweepCameraUpdatesThisAttempt = 0;
        private DateTime _sweepStage2EnteredAt = DateTime.MinValue;
        private bool _sweepNoMovementThisAttempt = false;

        // Sweep constants
        private const float SWEEP_CAMERA_DEG_PER_SECOND = 35.0f;
        private const float SWEEP_MAX_ANGLE_PER_ATTEMPT = 120.0f;
        private const double SWEEP_MAX_ATTEMPT_MS = 2500.0;
        private const double SWEEP_MIN_OBSERVE_MS = 500.0;
        private const float SWEEP_SUCCESS_CLEARANCE = 3.0f;
        private const float SWEEP_MIN_MOVEMENT = 0.75f;
        private const int SWEEP_SAME_SIDE_ATTEMPTS = 3;
        private const float SWEEP_REVERSE_EXTRA_CLEARANCE = 2.0f;
        private const float SWEEP_MAX_REVERSE_CLEARANCE = 8.0f;
        private const double SWEEP_CAMERA_UPDATE_INTERVAL_MS = 100.0; // ~10 updates/sec
        private const double FORCE_START_MIN_INTERVAL_MS = 700.0;
        private DateTime _lastForceStartMovingAt = DateTime.MinValue;

        // Healthy movement tracking
        private float _lastHealthyMoveBearingDeg = UnsetBearing;
        private (float X, float Y) _lastHealthyMovePos;
        private DateTime _lastHealthyMoveTime = DateTime.MinValue;

        // Commit mode (post-successful probe)
        private bool _isCommitMode = false;
        private DateTime _commitStartTime;
        private float _commitSideOffset;
        private float _commitStartDist;
        private const double COMMIT_MIN_DURATION = 0.400;   // 400ms
        private const double COMMIT_MAX_DURATION = 0.800;   // 800ms
        private const float COMMIT_HEALTHY_PROGRESS = 2.0f; // target improve to exit early

        // Wall-follow state (stage 3)
        private int _wallFollowSide = 0; // +1 or -1
        private float _wallFollowOffset = 60f;
        private DateTime _wallFollowLastGoodTime = DateTime.MinValue;
        private float _wallFollowBestDist = float.MaxValue;
        private float _stage3StartDist = float.MaxValue;
        private const double WALL_FOLLOW_MIN_DURATION = 1.200;
        private const double WALL_FOLLOW_MAX_DURATION = 4.000;
        private const float WALL_FOLLOW_OFFSET_INCREASE = 15f;
        private const float WALL_FOLLOW_OFFSET_DECREASE = -10f;

        // Ghost / bypass waypoint (stage 4)
        private bool _hasBypassWaypoint = false;
        private const float BYPASS_GHOST_DISTANCE = 12.0f;
        private const float BYPASS_FORWARD_DISTANCE = 5.0f;
        private const float LOCAL_UNSTUCK_MAX_TARGET_DISTANCE = 35.0f;
        private const int MAX_GHOSTS_PER_REAL_WAYPOINT = 1;
        private int _ghostsForCurrentRealWaypoint = 0;
        private bool _hardResetAlreadyAttempted = false;
        private bool _anyMovementDuringUnstuck = false;
        private double _ghostStuckTime = 0.0;
        private DateTime _lastGhostCheckTime = DateTime.MinValue;
        private float _ghostBestDist = float.MaxValue;

        // Unstuck time budget per waypoint
        private const double MAX_UNSTUCK_ATTEMPT_SECONDS = 6.0;
        private const int MAX_UNSTUCK_ATTEMPTS_PER_WAYPOINT = 2;
        private const double MAX_TOTAL_UNSTUCK_SECONDS_PER_WAYPOINT = 12.0;
        private bool _hasUnstuckBudgetState = false;
        private Waypoint _budgetWaypoint;
        private DateTime _currentUnstuckAttemptStartedAt = DateTime.MinValue;
        private DateTime _currentWaypointUnstuckWindowStartedAt = DateTime.MinValue;
        private int _currentWaypointUnstuckAttempts = 0;

        // Zero-movement escalation
        private const int MAX_ZERO_MOVEMENT_PROBES = 2;

        // Position jump detection — reset tracker if one tick moves more than this
        private const float MAX_REASONABLE_TICK_MOVEMENT = 8.0f;

        // Camera distance only.
        private const short CameraDistanceLock = 16980;

        // Human-like camera smoothing constants (normal movement, not unstuck)
        private const double CAMERA_MIN_UPDATE_INTERVAL_MS = 120.0;
        private const float CAMERA_MAX_STEP_DEG_NORMAL = 2.0f;
        private const float CAMERA_MAX_STEP_DEG_NEAR_TARGET = 0.75f;
        private const float CAMERA_NORMAL_DEADZONE_DEG = 3.0f;
        private const float CAMERA_NEAR_TARGET_DEADZONE_DEG = 10.0f;
        private const float CAMERA_NEAR_TARGET_RADIUS = 8.0f;
        private const float CAMERA_ARRIVAL_FREEZE_EXTRA = 2.5f;
        private const float CAMERA_LOOKAHEAD_RADIUS = 10.0f;

        // Legacy constants kept for reference but no longer used in MoveTowards
        private const float BearingUpdateThresholdDeg = 1.5f;  // unused, replaced by CAMERA_NORMAL_DEADZONE_DEG
        private const float NormalCameraStepDeg = 3.0f;         // unused
        private const int NormalCameraMaxMicroStepsPerUpdate = 8; // unused
        private const int NormalCameraMicroStepDelayMs = 1;      // unused

        // Camera state for smoothing / rate limiting
        private DateTime _lastCameraUpdateAt = DateTime.MinValue;
        private float _smoothedTargetBearingDeg = UnsetBearing;

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

            _localNavigationMap = new LocalNavigationMap(_log);

            _isInitialized = true;
            _log($"MovementSystem: Initialized with GameMemoryService, Default Precision: {GlobalPrecision}, Loop: {LoopPath}");
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
                _log($"[Tick {_tickCount}] Update skipped — not initialized.");
                return;
            }

            if (_goalReached)
            {
                _log($"[Tick {_tickCount}] Update skipped — goal already reached.");
                return;
            }

            token.ThrowIfCancellationRequested();

            // ── Periodic state dump ──
            if (_tickCount % _stateLogInterval == 0)
            {
                string modeStr = "none";
                if (_waypoints.Count > 0) modeStr = _waypoints.Peek().Mode.ToString();
                _log($"[State] Tick:{_tickCount} | WpQueue:{_waypoints.Count} | Mode:{modeStr} | MovingFwd:{_isMovingForward} | Unstuck:{_isUnstuckRoutineActive} | GoalReached:{_goalReached} | Loop:{LoopPath}");
            }

            // ── Repot / Report-and-go-back ──
            var repotAction = _repotHelper.EvaluateRepotTick(InternalRepotEnabled);
            if (repotAction != RepotAction.None)
            {
                _log($"[Repot] EvaluateRepotTick returned {repotAction}.");
            }
            if (repotAction == RepotAction.ReportAndGoBackActive)
            {
                _log($"[Tick {_tickCount}] Repot/Report-and-go-back active — skipping movement tick.");
                return;
            }
            if (repotAction == RepotAction.Repotting)
            {
                _log($"[Tick {_tickCount}] Repotting in progress — skipping movement tick.");
                return;
            }

            // Keep distance only.
            _memoryService.SetCameraDistance(CameraDistanceLock);

            // ── Attack speed / potion check ──
            if (_combatHandler.CheckAttackSpeed(_memoryService))
            {
                _log($"[Tick {_tickCount}] AttackSpeed 16341 detected. Using potions.");
                _log("[Key] Pressing 7 (potion 1)");
                GameInput.PressKey(GameInput.VK_7, GameInput.SCAN_7);
                await Task.Delay(500, token);
                _log("[Key] Pressing 8 (potion 2)");
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
                _log($"[Tick {_tickCount}] Combat action: {combatAction}");
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
                    var (stuckX, stuckY, stuckOk) = _memoryService.GetPlayerPosition();
                    _log($"[Tick {_tickCount}] CombatHandler requested UnstuckNeeded. Pos=({stuckX:F1},{stuckY:F1}) ok={stuckOk}");
                    _combatHandler.ResetStuckInAttack();
                    // Redirect to action-based flow: if the bot is movement-stuck (action 25/1),
                    // mark the obstacle and enter Bug2 instead of the old legacy unstuck.
                    if (stuckOk && _waypoints.Count > 0 && _bug2State == Bug2NavState.None)
                    {
                        byte combatActionStuck = _memoryService.GetCurrentAction();
                        if (IsActionIdleOrStuck(combatActionStuck))
                        {
                            var combatTarget = _waypoints.Peek();
                            _log($"[CombatUnstuck] Action={combatActionStuck} confirms stuck. Marking obstacle and entering Bug2.");
                            MarkObstacleFromActionStuck(stuckX, stuckY, combatTarget);
                            _localNavigationMap.SaveIfDirty();
                            EnterBug2Mode(stuckX, stuckY, combatTarget);
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
                _log($"[Tick {_tickCount}] [Pos] Failed to read player position");
                return;
            }

            // Position jump / outlier detection: if one tick moves more than MAX_REASONABLE_TICK_MOVEMENT, reset tracker
            if (_progressTracker.HasSamples)
            {
                float tickDelta = GeometryUtils.Distance(currX, currY, _progressTracker.LastX, _progressTracker.LastY);
                if (tickDelta > MAX_REASONABLE_TICK_MOVEMENT)
                {
                    _log($"[Progress] Position jump detected ({tickDelta:F1} > {MAX_REASONABLE_TICK_MOVEMENT:F0} tiles in one tick). Resetting tracker.");
                    _progressTracker.Reset();
                }
            }

            // Log position every 5 ticks
            if (_tickCount % 5 == 0)
                _log($"[Pos] Player at ({currX:F1}, {currY:F1})");

            byte currentAction = _memoryService.GetCurrentAction();

            // ── Waypoint re-queue when empty (obstacle bypass) ──
            if (_waypoints.Count == 0 && !_goalReached)
            {
                _localNavigationMap.SaveIfDirty();
                _log($"[Tick {_tickCount}] Waypoint queue empty, checking line-of-sight to final target ({Waypoint2.X:F1}, {Waypoint2.Y:F1}).");
                if (GeometryUtils.CheckLineOfSight(currX, currY, Waypoint2.X, Waypoint2.Y))
                {
                    _waypoints.Enqueue(new Waypoint(GeometryUtils.ObstacleCenter.X, GeometryUtils.ObstacleCenter.Y, GlobalPrecision, BotMode.OnlyMove));
                    _waypoints.Enqueue(new Waypoint(Waypoint2.X, Waypoint2.Y, GlobalPrecision, BotMode.OnlyMove));
                    _log($"[Tick {_tickCount}] Path blocked (line-of-sight detected obstacle). Added obstacle center ({GeometryUtils.ObstacleCenter.X:F1},{GeometryUtils.ObstacleCenter.Y:F1}) + final target.");
                }
                else
                {
                    _waypoints.Enqueue(new Waypoint(Waypoint2.X, Waypoint2.Y, GlobalPrecision, BotMode.OnlyMove));
                    _log($"[Tick {_tickCount}] Path clear (no obstacle in line-of-sight). Re-added final target waypoint.");
                }
            }

            // ── Main movement loop ──
            if (_waypoints.Count > 0)
            {
                var activeTarget = _waypoints.Peek();
                float distToTarget = GeometryUtils.Distance(currX, currY, activeTarget.X, activeTarget.Y);
                float reachThreshold = GetEffectiveWaypointReachThreshold(activeTarget);

                // ── Unstuck active path ──
                if (_isUnstuckRoutineActive)
                {
                    _log($"[Tick {_tickCount}] Unstuck routine active. Dist to target: {distToTarget:F2}.");
                    if (TryResolveRouteProgressDuringUnstuck(currX, currY))
                    {
                        _log($"[Tick {_tickCount}] Unstuck resolved via route progress during unstuck.");
                        return;
                    }

                    if (_waypoints.Count == 0)
                    {
                        _log($"[Tick {_tickCount}] Unstuck active but waypoints became empty — returning.");
                        return;
                    }

                    HandleUnstuckRoutine(currX, currY, activeTarget);
                    return;
                }

                // ── Normal movement path ──

                // Check final-waypoint soft completion
                if (IsLastWaypoint() && distToTarget <= FINAL_GOAL_SOFT_RADIUS)
                {
                    if (CheckFinalGoalSoftCompletion(currX, currY, activeTarget, distToTarget))
                    {
                        return;
                    }
                }

                _log($"[Tick {_tickCount}] Advancing reached waypoints from ({currX:F1},{currY:F1}).");
                AdvanceReachedWaypoints(currX, currY);

                if (_goalReached)
                {
                    _log($"[Tick {_tickCount}] Goal reached after advancing waypoints — returning.");
                    return;
                }

                if (_waypoints.Count == 0)
                {
                    _log($"[Tick {_tickCount}] Waypoints became empty after advancing — returning.");
                    return;
                }

                var target = _waypoints.Peek();
                float distNow = GeometryUtils.Distance(currX, currY, target.X, target.Y);
                float thresholdNow = GetEffectiveWaypointReachThreshold(target);

                // ── Bug2 / action-stuck local navigation ──
                if (_bug2State != Bug2NavState.None)
                {
                    RunBug2NavigationStep(currX, currY, target);
                    return;
                }

                if (IsActionStuck())
                {
                    _log($"[ActionStuck] Action={currentAction} while moving. Stuck confirmed by action.");
                    MarkObstacleFromActionStuck(currX, currY, target);
                    _localNavigationMap.SaveIfDirty();

                    if (TrySkipCurrentWaypointBecauseClose(currX, currY, target))
                    {
                        ResetBug2State();
                        return;
                    }

                    EnterBug2Mode(currX, currY, target);
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
                    _log($"[Route] Tick:{_tickCount} | Target:({target.X:F1},{target.Y:F1}){ghostFlag} | Dist:{distNow:F2} | ReachThreshold:{thresholdNow:F2} | Mode:{target.Mode} | Prec:{target.Precision} | {progStatus}");
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

                _log($"[Tick {_tickCount}] Moving towards ({target.X:F1},{target.Y:F1}) from ({currX:F1},{currY:F1}).");
                MoveTowards(currX, currY, target.X, target.Y);
            }
            else
            {
                if (!_goalReached)
                {
                    _log($"[Tick {_tickCount}] Waypoint queue empty and goal not reached — no action this tick.");
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
                _log($"[FinalGoal] Soft completion triggered. Dist:{distToTarget:F2} <= {effectiveThreshold:F2}, stallTime:{stallTime:F1}s >= {FINAL_GOAL_STALL_TIME:F1}s. Marking goal reached.");
                AdvanceReachedWaypoints(currX, currY);
                return true;
            }

            if (_tickCount % _stateLogInterval == 0 && distToTarget <= FINAL_GOAL_SOFT_RADIUS)
            {
                _log($"[FinalGoal] Within soft radius. BestDist:{_finalGoalBestDist:F2}, stallTime:{stallTime:F1}s/{FINAL_GOAL_STALL_TIME:F1}s");
            }

            return false;
        }

        // ========================================================================
        //  STUCK DETECTION — LEGACY (no longer called)
        //  All stuck detection now uses GetCurrentAction() via IsActionStuck().
        //  Kept only for reference — do not use in new code.
        // ========================================================================

        [Obsolete("Use IsActionStuck() instead. This method uses progress-based detection which is disabled.")]
        private bool DetectStuck(float currX, float currY, Waypoint target, byte? currentActionOverride = null)
        {
            _log($"[StuckCheck] Enter — Pos:({currX:F1},{currY:F1}) Target:({target.X:F1},{target.Y:F1})");

            if (_isUnstuckRoutineActive)
            {
                _log("[StuckCheck] Unstuck routine already active — returning true.");
                return true;
            }

            if (DateTime.Now < _unstuckCooldownUntil)
            {
                _log($"[StuckCheck] Skipped — unstuck cooldown active until {_unstuckCooldownUntil:HH:mm:ss.fff}.");
                return false;
            }

            if (DateTime.Now < _ignoreStuckUntil)
            {
                _log($"[StuckCheck] Skipped — grace period active until {_ignoreStuckUntil:HH:mm:ss.fff}.");
                return false;
            }

            if (DateTime.Now < _progressTracker.GraceUntil)
            {
                _log($"[StuckCheck] Skipped — progress tracker grace period active until {_progressTracker.GraceUntil:HH:mm:ss.fff}.");
                return false;
            }

            // Only detect stuck while actively trying to move forward
            if (!_isMovingForward)
            {
                _log("[StuckCheck] _isMovingForward is false — resetting counter and returning false.");
                _actionStuckCounter = 0;
                return false;
            }

            float distToTarget = GeometryUtils.Distance(currX, currY, target.X, target.Y);
            float reachThreshold = GetEffectiveWaypointReachThreshold(target);
            bool nearTarget = distToTarget <= reachThreshold + NEAR_TARGET_STUCK_IGNORE_EXTRA;
            _log($"[StuckCheck] DistToTarget:{distToTarget:F2} ReachThreshold:{reachThreshold:F2} NearIgnore:{reachThreshold + NEAR_TARGET_STUCK_IGNORE_EXTRA:F2} NearTarget:{nearTarget}");

            if (nearTarget)
            {
                _log($"[StuckCheck] Too close to target (dist {distToTarget:F2} <= {reachThreshold + NEAR_TARGET_STUCK_IGNORE_EXTRA:F2}) — resetting counter, not stuck.");
                _actionStuckCounter = 0;
                return false;
            }

            // ── Get tracker data ──
            byte action = currentActionOverride ?? _memoryService.GetCurrentAction();
            float disp = _progressTracker.GetWindowDisplacement();
            float distDelta = _progressTracker.GetWindowDistDelta();
            float projDelta = _progressTracker.GetWindowProjDelta();
            double elapsed = _progressTracker.GetWindowElapsed();

            // Log periodic progress status (warmup-aware)
            if (_tickCount % _stateLogInterval == 0)
            {
                string statusStr = _progressTracker.GetStatusString();
                if (_progressTracker.IsWarmingUp)
                {
                    _log($"[Progress] warming {statusStr} wIntended={_isMovingForward}");
                }
                else
                {
                    _log($"[Progress] {statusStr} wIntended={_isMovingForward}");
                }
            }

            // ── LAYER A: Idle-action stuck ──
            if (action == 25 || action == 1)
            {
                _actionStuckCounter++;
                _log($"[StuckCheck] IDLE_STUCK action={action} attempt={_actionStuckCounter}/{ActionStuckThreshold}");

                if (_actionStuckCounter >= ActionStuckThreshold)
                {
                    _log($"[StuckCheck] IDLE_STUCK THRESHOLD REACHED — starting preflight (stage 0).");
                    StartSweepUnstuck(currX, currY, target);
                    return true;
                }
                // Don't return false yet — also check progress-based layers
            }
            else
            {
                if (_actionStuckCounter > 0)
                    _log($"[StuckCheck] Action {action} recovered from idle — resetting counter.");
                _actionStuckCounter = 0;
            }

            // ── LAYERS B/C/D: Progress-based stuck — DISABLED ──
            // Stuck detection uses only GetCurrentAction() via IsActionStuck().
            // The progress-based checks below are kept only for reference / legacy.
            /*
            if (_progressTracker.IsHardStuck(_isMovingForward, nearTarget)) { ... }
            if (_progressTracker.IsSoftStuck(_isMovingForward, nearTarget)) { ... }
            if (_progressTracker.IsWrongWay(_isMovingForward, nearTarget)) { ... }
            */

            // ── Not stuck ──

            // Reset escalation if far from stuck area
            if (_totalEscalationAttempts > 0 &&
                GeometryUtils.Distance(currX, currY, _firstStuckLocation.X, _firstStuckLocation.Y) > ESCALATION_AREA_RESET_DISTANCE)
            {
                _log($"[StuckCheck] Escaped stuck area (Dist: {GeometryUtils.Distance(currX, currY, _firstStuckLocation.X, _firstStuckLocation.Y):F1} > {ESCALATION_AREA_RESET_DISTANCE:F1}). Resetting escalation.");
                _totalEscalationAttempts = 0;
            }

            return false;
        }

        /// <summary>
        /// Preflight repair: release W, face target, re-press W, verify progress.
        /// If progress resumes, cancel unstuck. Otherwise enter full unstuck routine.
        /// </summary>
        // ── Sweep unstuck entry ──

        private float _preflightStartDist;
        private DateTime _preflightStartTime;
        private bool _preflightCheckDone = false;

        private void StartSweepUnstuck(float currX, float currY, Waypoint target)
        {
            _log("[SweepUnstuck] Initializing sweep unstuck.");
            EnsureUnstuckBudgetState(target);

            // Decide sweep side
            if (_sweepTotalAttempts == 0)
            {
                // First attempt: random side
                _sweepSide = _rng.Next(2) == 0 ? 1 : -1;
            }
            else if (_sweepSameSideAttempts >= SWEEP_SAME_SIDE_ATTEMPTS)
            {
                // After 3 same-side failures: reverse side
                _sweepSide = -_sweepSide;
                _sweepSameSideAttempts = 0;
                _log($"[SweepUnstuck] Switching side after {SWEEP_SAME_SIDE_ATTEMPTS} failures. New side={(_sweepSide > 0 ? "Right" : "Left")}");
            }

            _isUnstuckRoutineActive = true;
            _unstuckStage = 1;
            _stuckAnchor = (currX, currY);
            _hasUnstuckTarget = false;
            _sweepStartBearing = _lastSetBearingDeg != UnsetBearing ? _lastSetBearingDeg :
                GeometryUtils.GetBearingToTargetDeg(currX, currY, target.X, target.Y);
            _sweepCurrentBearing = _sweepStartBearing;
            _sweepAccumulatedAngle = 0f;
            _sweepFirstTick = true;
            _sweepNoMovementRefreshCount = 0;
            _sweepTotalAttempts++;
            _sweepSameSideAttempts++;
            _preflightCheckDone = false;
            _preflightStartTime = DateTime.Now;

            _log($"[SweepUnstuck] Start anchor=({currX:F1},{currY:F1}) camera={_sweepStartBearing:F1} " +
                 $"side={(_sweepSide > 0 ? "Right" : "Left")} attempt={_sweepTotalAttempts} sameSide={_sweepSameSideAttempts}");
        }

        // ========================================================================
        //  UNSTUCK ROUTINE
        // ========================================================================

        private bool TryResolveRouteProgressDuringUnstuck(float currX, float currY)
        {
            _log($"[UnstuckResolve] Checking route progress at ({currX:F1},{currY:F1}). GoalReached={_goalReached}");

            bool advanced = AdvanceReachedWaypoints(currX, currY);

            if (_goalReached)
            {
                _log("[UnstuckResolve] Goal reached — returning true.");
                return true;
            }

            if (advanced)
            {
                _log("[UnstuckResolve] Route progress made during unstuck — ending unstuck.");
                EndUnstuck("progress_made");
                return true;
            }

            _log("[UnstuckResolve] No regular waypoint progress. Checking ghost bypass.");
            bool skippedGhost = TrySkipGhostsIfRealWaypointReached(currX, currY);

            if (_goalReached)
            {
                _log("[UnstuckResolve] Goal reached during ghost check — returning true.");
                return true;
            }

            if (skippedGhost)
            {
                _log("[UnstuckResolve] Ghost waypoint skipped because real waypoint was near — ending unstuck.");
                EndUnstuck("ghost_skipped");
                return true;
            }

            _log("[UnstuckResolve] No route progress detected.");
            return false;
        }

        // ────────────────────────────────────
        //  NEW UNSTUCK STAGES
        // ────────────────────────────────────

        private void EndUnstuck(string reason = "completed")
        {
            _log($"[Unstuck] EndUnstuck reason={reason} cooldown={UNSTUCK_COOLDOWN_SECONDS} grace={STUCK_GRACE_AFTER_START_SECONDS}");

            // On success reasons that advanced route, clear waypoint budget window
            // so subsequent stuck events on the new waypoint start fresh.
            if (reason == "commit_complete" || reason == "preflight_restored" ||
                reason == "progress_made" || reason == "target_reachable" ||
                reason == "wallfollow_success" || reason == "bypass_success" ||
                reason == "lastresort_success" || reason == "ghost_skipped" ||
                reason == "commit_timeout" || // commit_timeout has positive progress
                reason == "sweep_rejoin_success" || reason == "sweep_rejoin_close" ||
                reason == "sweep_init_clearance" || reason == "backout_clearance" ||
                reason == "rejoin_success")
            {
                _hasUnstuckBudgetState = false;
                _currentWaypointUnstuckWindowStartedAt = DateTime.MinValue;
                _currentWaypointUnstuckAttempts = 0;
            }

            StopMoving();
            _isUnstuckRoutineActive = false;
            _unstuckStage = 0;
            _currentProbeIndex = 0;
            _hasUnstuckTarget = false;
            _unstuckStageCommandIssued = false;
            _isCommitMode = false;
            _hasBypassWaypoint = false;
            _preflightCheckDone = false;
            _wallFollowSide = 0;
            _unstuckCooldownUntil = DateTime.Now.AddSeconds(UNSTUCK_COOLDOWN_SECONDS);
            _ignoreStuckUntil = DateTime.Now.AddSeconds(STUCK_GRACE_AFTER_START_SECONDS);
            _progressTracker.GraceUntil = DateTime.Now.AddSeconds(STUCK_GRACE_AFTER_START_SECONDS);
            _actionStuckCounter = 0;
            _consecutiveNoMovementProbes = 0;
            _stage3StartDist = float.MaxValue;
            _hardResetAlreadyAttempted = false;
            _anyMovementDuringUnstuck = false;
            _ghostStuckTime = 0.0;
            _ghostBestDist = float.MaxValue;
            _lastGhostCheckTime = DateTime.MinValue;
            _sweepSide = 0;
            _sweepSameSideAttempts = 0;
            _sweepTotalAttempts = 0;
            _sweepBestClearance = 0f;
            _sweepNoMovementRefreshCount = 0;

            // Reset attempt timer so next call to HandleUnstuckRoutine starts a fresh attempt.
            // Keep waypoint window unless this was a final success that advanced the route.
            _currentUnstuckAttemptStartedAt = DateTime.MinValue;
            ResetBearingState();
            _progressTracker.Reset();
            ResetActionStuckTracking();
            _finalGoalBestDist = float.MaxValue;
            _finalGoalLastProgressTime = DateTime.MinValue;
            _log("[Unstuck] EndUnstuck complete.");
        }

        private void StartUnstuckRoutine(float currX, float currY)
        {
            Waypoint targetForUnstuck;
            if (_waypoints.Count > 0)
                targetForUnstuck = _waypoints.Peek();
            else
                targetForUnstuck = new Waypoint(currX, currY, GlobalPrecision, BotMode.OnlyMove);
            StartUnstuckRoutine(currX, currY, targetForUnstuck);
        }

        private void StartUnstuckRoutine(float currX, float currY, Waypoint target)
        {
            _log($"[Unstuck] StartUnstuckRoutine called. Pos=({currX:F1},{currY:F1}) Target=({target.X:F1},{target.Y:F1})");
            if (_isUnstuckRoutineActive) { _log("[Unstuck] Already active — ignoring."); return; }

            float distToTarget = GeometryUtils.Distance(currX, currY, target.X, target.Y);
            float threshold = GetEffectiveWaypointReachThreshold(target) + UNSTUCK_TARGET_REACHED_EXTRA;
            _log($"[Unstuck] Dist to target: {distToTarget:F2}, Threshold: {threshold:F2}");
            if (distToTarget <= threshold)
            {
                _log($"[Unstuck] Skipped — target already close. Dist:{distToTarget:F2} <= {threshold:F2}");
                AdvanceReachedWaypoints(currX, currY);
                return;
            }

            StopMoving();
            _log($"[Unstuck] Starting. Escalation attempts: {_totalEscalationAttempts}");
            _isUnstuckRoutineActive = true;
            _isCommitMode = false;
            _hasBypassWaypoint = false;
            _preflightCheckDone = false;
            _wallFollowSide = 0;
            _stage3StartDist = float.MaxValue;
            _unstuckCooldownUntil = DateTime.MinValue;
            _actionStuckCounter = 0;
            _consecutiveNoMovementProbes = 0;
            _hardResetAlreadyAttempted = false;
            _anyMovementDuringUnstuck = false;

            // Budget tracking
            EnsureUnstuckBudgetState(target);
            // Note: EnsureUnstuckBudgetState increments _currentWaypointUnstuckAttempts
            // and logs the attempt, so we don't increment again here.
            _stuckAnchor = (currX, currY);
            _currentProbeIndex = 0;
            _unstuckTarget = target;
            _hasUnstuckTarget = true;
            _unstuckStartDistToTarget = distToTarget;
            _unstuckOriginalTargetBearing = GeometryUtils.GetBearingToTargetDeg(currX, currY, target.X, target.Y);
            _log($"[Unstuck] Anchor=({currX:F1},{currY:F1}) StartDist:{_unstuckStartDistToTarget:F2} OriginalBearing:{_unstuckOriginalTargetBearing:F1}");
            if (_totalEscalationAttempts == 0) { _firstStuckLocation = (currX, currY); _log($"[Unstuck] First stuck: ({currX:F1},{currY:F1})"); }

            _bestProbePosition = (currX, currY);
            _bestProbeScore = -999f;
            _bestProbeDistToTarget = distToTarget;
            _bestProbeIndex = -1;
            _bestProbeOffset = 0f;
            _progressTracker.Reset();
            _progressTracker.SetSegment(currX, currY, target.X, target.Y);
            ResetActionStuckTracking();
            EnterUnstuckStage(1, currX, currY); // stage 1 = preflight
        }

        private void EnterUnstuckStage(int stage, float currX, float currY)
        {
            _log($"[Unstuck] Entering stage {stage} at ({currX:F1},{currY:F1}). ProbeIndex={_currentProbeIndex} Escalation={_totalEscalationAttempts}");
            StopMoving();
            _unstuckStage = stage;
            _unstuckStageStartTime = DateTime.Now;
            _unstuckStageCommandIssued = false;
            _unstuckStageCommandAllowedAt = DateTime.Now.AddMilliseconds(UNSTUCK_STAGE_INPUT_RELEASE_MS);
            _ignoreStuckUntil = DateTime.Now.AddSeconds(STUCK_GRACE_AFTER_START_SECONDS);
            _progressTracker.GraceUntil = DateTime.Now.AddSeconds(STUCK_GRACE_AFTER_START_SECONDS);
            ResetBearingState();
        }

        private void HandleUnstuckRoutine(float currX, float currY, Waypoint target)
        {
            _log($"[Unstuck] HandleUnstuckRoutine tick:{_tickCount}. Stage:{_unstuckStage} Active:{_isUnstuckRoutineActive}");
            if (!_isUnstuckRoutineActive) return;

            if (!_hasUnstuckTarget)
            {
                _unstuckTarget = target;
                _hasUnstuckTarget = true;
                _unstuckStartDistToTarget = GeometryUtils.Distance(currX, currY, target.X, target.Y);
                _unstuckOriginalTargetBearing = GeometryUtils.GetBearingToTargetDeg(currX, currY, target.X, target.Y);
                _stuckAnchor = (currX, currY);
                _bestProbePosition = (currX, currY);
                _bestProbeScore = -999f;
                _bestProbeDistToTarget = _unstuckStartDistToTarget;
            }

            if (InternalRepotEnabled && _totalEscalationAttempts >= ESCALATION_LIMIT)
            {
                float d = GeometryUtils.Distance(currX, currY, _firstStuckLocation.X, _firstStuckLocation.Y);
                _log($"[Unstuck] Escalation limit ({ESCALATION_LIMIT}). From first stuck: {d:F2} (threshold:{ESCALATION_REPORT_DISTANCE:F1})");
                if (d < ESCALATION_REPORT_DISTANCE) { _repotHelper.ReportAndGoBack(); EndUnstuck("escalation_report"); return; }
            }

            // ── Time budget check (guarded against uninitialized state) ──
            if (ShouldFailFastBudget(target))
            {
                FailUnstuckAttempt(currX, currY, target, "budget_exceeded");
                return;
            }

            float distToTarget = GeometryUtils.Distance(currX, currY, target.X, target.Y);
            float reachThreshold = GetEffectiveWaypointReachThreshold(target) + UNSTUCK_TARGET_REACHED_EXTRA;
            if (distToTarget <= reachThreshold)
            {
                _log($"[Unstuck] Target reachable! Dist:{distToTarget:F2} <= Threshold:{reachThreshold:F2}.");
                AdvanceReachedWaypoints(currX, currY);
                if (!_goalReached) EndUnstuck("target_reachable"); else _log("[Unstuck] Goal reached.");
                return;
            }

            // Check commit mode
            if (_isCommitMode) { HandleCommitMode(currX, currY, target); return; }

            // Ghost stall detection: if current target is a ghost and distance is increasing or stalled >2s, remove it
            if (IsGhostWaypoint(target))
            {
                if (_lastGhostCheckTime == DateTime.MinValue)
                {
                    _lastGhostCheckTime = DateTime.Now;
                    _ghostStuckTime = 0.0;
                }
                else
                {
                    _ghostStuckTime += (DateTime.Now - _lastGhostCheckTime).TotalSeconds;
                    _lastGhostCheckTime = DateTime.Now;
                }
                float distToGhost = GeometryUtils.Distance(currX, currY, target.X, target.Y);
                if (distToGhost > _ghostBestDist + 0.5f || _ghostStuckTime > 2.0)
                {
                    _log($"[GhostStall] Ghost waypoint stalled or worsening. dist={distToGhost:F2} best={_ghostBestDist:F2} stuckTime={_ghostStuckTime:F1}s. Removing ghost.");
                    RemoveGhostWaypointRecord(target);
                    _waypoints.Dequeue();
                    _progressTracker.Reset();
                    _ghostStuckTime = 0.0;
                    _ghostBestDist = float.MaxValue;
                    _log($"[GhostStall] Ghost removed. Queue now has {_waypoints.Count} waypoints.");
                    return;
                }
                if (distToGhost < _ghostBestDist)
                {
                    _ghostBestDist = distToGhost;
                    _ghostStuckTime = 0.0;
                }
            }

            switch (_unstuckStage)
            {
                case 1: HandlePreflightStage(currX, currY, target); break;
                case 2: HandleProbeStage(currX, currY, target); break;
                case 3: HandleRejoinStage(currX, currY, target); break;
                case 4: HandleBypassStage(currX, currY, target); break;
                case 5: HandleLastResortStage(currX, currY, target); break;
                default: EndUnstuck("unknown_stage"); break;
            }
        }

        // ── Sweep helpers ──

        private void EnsureWDownForSweep(string reason)
        {
            if (!_isMovingForward)
            {
                _log($"[SweepUnstuck] W was not down in sweep ({reason}) — StartMoving.");
                StartMoving();
            }
        }

        private string SweepSideName(int side) => side > 0 ? "Right" : "Left";

        private void EnterSweepStage2(float currX, float currY)
        {
            _unstuckStage = 2;
            _sweepAttemptStartTime = DateTime.Now;
            _sweepLastCameraUpdateAt = DateTime.MinValue;
            _sweepStartPosX = currX;
            _sweepStartPosY = currY;
            _sweepCurrentBearing = _lastSetBearingDeg != UnsetBearing ? _lastSetBearingDeg : 0f;
            _sweepStartBearingDeg = _sweepCurrentBearing;
            _sweepSweptDeg = 0f;
            _sweepInputRefreshCountThisAttempt = 0;
            _sweepCameraUpdatesThisAttempt = 0;
            _sweepStage2EnteredAt = DateTime.Now;
            _preflightStartDist = GeometryUtils.Distance(currX, currY, _waypoints.Count > 0 ? _waypoints.Peek().X : currX, _waypoints.Count > 0 ? _waypoints.Peek().Y : currY);

            float requiredClearance = SWEEP_SUCCESS_CLEARANCE;
            if (_sweepSameSideAttempts > 1)
                requiredClearance = SWEEP_SUCCESS_CLEARANCE + (_sweepSameSideAttempts - 1) * 1.0f;

            _log($"[SweepUnstuck] Enter active sweep side={SweepSideName(_sweepSide)} startBearing={_sweepStartBearingDeg:F1} requiredClearance={requiredClearance:F1}");
            EnsureWDownForSweep("enter_stage2");
        }

        private void FailSweepAttemptAndRetryOrSwitchSide(float currX, float currY, Waypoint target, string reason)
        {
            _log($"[SweepUnstuck] Sweep attempt failed: reason={reason} side={SweepSideName(_sweepSide)} sameSideAttempts={_sweepSameSideAttempts}/{SWEEP_SAME_SIDE_ATTEMPTS}");

            if (_sweepSameSideAttempts >= SWEEP_SAME_SIDE_ATTEMPTS)
            {
                if (_sweepTotalAttempts >= SWEEP_SAME_SIDE_ATTEMPTS * 2)
                {
                    _log($"[SweepUnstuck] Both sides exhausted ({_sweepTotalAttempts} attempts). Escalating to bypass.");
                    _progressTracker.Reset();
                    EnterUnstuckStage(4, currX, currY);
                }
                else
                {
                    // Switch side
                    _sweepSide = -_sweepSide;
                    _sweepSameSideAttempts = 0;
                    _log($"[SweepUnstuck] Switching side to {SweepSideName(_sweepSide)} after {SWEEP_SAME_SIDE_ATTEMPTS} same-side failures.");
                    _preflightCheckDone = false;
                    EnterSweepStage2(currX, currY);
                }
            }
            else
            {
                // Same-side retry
                _sweepSameSideAttempts++;
                _sweepTotalAttempts++;
                _log($"[SweepUnstuck] Retrying same side {SweepSideName(_sweepSide)} attempt={_sweepSameSideAttempts}/{SWEEP_SAME_SIDE_ATTEMPTS}");
                _preflightCheckDone = false;
                EnterSweepStage2(currX, currY);
            }
        }

        // ── STAGE 1: SWEEP INIT ──

        private void HandlePreflightStage(float currX, float currY, Waypoint target)
        {
            if (!_preflightCheckDone)
            {
                _preflightCheckDone = true;
                _preflightStartTime = DateTime.Now;
                EnsureWDownForSweep("stage1_init");
                _log($"[SweepUnstuck] Stage 1 init: holding W at camera={_sweepStartBearing:F1}. Observing {SWEEP_MIN_OBSERVE_MS}ms.");
                return;
            }

            double elapsed = (DateTime.Now - _preflightStartTime).TotalMilliseconds;
            if (elapsed < SWEEP_MIN_OBSERVE_MS)
                return;

            float moved = GeometryUtils.Distance(currX, currY, _stuckAnchor.X, _stuckAnchor.Y);
            if (moved >= SWEEP_MIN_MOVEMENT)
            {
                _log($"[SweepUnstuck] Already moving with clearance={moved:F2} at init camera. Skipping sweep.");
                EndUnstuck("sweep_init_clearance");
                return;
            }

            _log($"[SweepUnstuck] No initial movement (moved={moved:F2}). Beginning active sweep.");
            _progressTracker.Reset();
            EnterSweepStage2(currX, currY);
        }

        // ── STAGE 2: ACTIVE SWEEP (smooth camera rotation while holding W) ──

        private void HandleProbeStage(float currX, float currY, Waypoint target)
        {
            HandleSweepStage2(currX, currY, target);
        }

        private void HandleSweepStage2(float currX, float currY, Waypoint target)
        {
            var now = DateTime.Now;
            double elapsedMs = (now - _sweepAttemptStartTime).TotalMilliseconds;
            float anchorClearance = GeometryUtils.Distance(currX, currY, _stuckAnchor.X, _stuckAnchor.Y);
            float movedThisAttempt = GeometryUtils.Distance(currX, currY, _sweepStartPosX, _sweepStartPosY);

            // Ensure W is held every tick
            EnsureWDownForSweep("stage2_tick");

            // ── Success: clearance achieved ──
            float requiredClearance = SWEEP_SUCCESS_CLEARANCE;
            if (_sweepSameSideAttempts > 1)
                requiredClearance = SWEEP_SUCCESS_CLEARANCE + (_sweepSameSideAttempts - 1) * 1.0f;

            if (anchorClearance >= requiredClearance)
            {
                _log($"[SweepUnstuck] Clearance achieved side={SweepSideName(_sweepSide)} clearance={anchorClearance:F2} swept={_sweepSweptDeg:F1} -> rejoin");
                _progressTracker.Reset();
                EnterUnstuckStage(3, currX, currY);
                return;
            }

            // ── Watchdog: if Stage 2 active >300ms with no camera updates, force update ──
            if (elapsedMs > 300.0 && _sweepCameraUpdatesThisAttempt == 0)
            {
                _log($"[SweepUnstuck] BUG: Stage2 active without camera updates; forcing first sweep update.");
                _sweepLastCameraUpdateAt = DateTime.MinValue;
                EnsureWDownForSweep("stage2_watchdog");
            }

            // ── Smooth camera rotation ──
            double msSinceCameraUpdate = (now - _sweepLastCameraUpdateAt).TotalMilliseconds;
            if (msSinceCameraUpdate >= SWEEP_CAMERA_UPDATE_INTERVAL_MS)
            {
                double dt = _sweepLastCameraUpdateAt == DateTime.MinValue
                    ? SWEEP_CAMERA_UPDATE_INTERVAL_MS / 1000.0
                    : (now - _sweepLastCameraUpdateAt).TotalSeconds;

                float step = SWEEP_CAMERA_DEG_PER_SECOND * (float)dt;
                float remaining = SWEEP_MAX_ANGLE_PER_ATTEMPT - _sweepSweptDeg;
                step = Math.Min(step, remaining);

                if (step > 0.01f)
                {
                    _sweepCurrentBearing = GeometryUtils.NormalizeBearingDeg(_sweepCurrentBearing + _sweepSide * step);
                    _sweepSweptDeg += step;
                    _sweepCameraUpdatesThisAttempt++;
                    _sweepLastCameraUpdateAt = now;

                    short gameAngle = GeometryUtils.ConvertBearingToGameAngle(_sweepCurrentBearing, _lastSetGameAngle, _hasLastGameAngle);
                    _hasLastGameAngle = true;
                    _lastSetGameAngle = gameAngle;
                    _lastSetBearingDeg = _sweepCurrentBearing;
                    _memoryService.SetCameraAngle(gameAngle);

                    _log($"[SweepUnstuck] side={SweepSideName(_sweepSide)} bearing={_sweepCurrentBearing:F1} swept={_sweepSweptDeg:F1} clearance={anchorClearance:F2} moved={movedThisAttempt:F2}");
                }
            }

            // ── No movement: input refresh ──
            if (elapsedMs >= SWEEP_MIN_OBSERVE_MS && movedThisAttempt < SWEEP_MIN_MOVEMENT)
            {
                _sweepNoMovementThisAttempt = true;
                if (_sweepInputRefreshCountThisAttempt < 2 &&
                    (now - _lastForceStartMovingAt).TotalMilliseconds >= FORCE_START_MIN_INTERVAL_MS)
                {
                    _sweepInputRefreshCountThisAttempt++;
                    _log($"[SweepUnstuck] No movement during sweep, input refresh {_sweepInputRefreshCountThisAttempt}/2");
                    StopMoving();
                    Thread.Sleep(120);
                    EnsureWDownForSweep("input_refresh");
                    _lastForceStartMovingAt = now;
                    _sweepStartPosX = currX;
                    _sweepStartPosY = currY;
                    _log($"[SweepUnstuck] Continuing sweep after input refresh side={SweepSideName(_sweepSide)} bearing={_sweepCurrentBearing:F1}");
                    return;
                }

                if (_sweepInputRefreshCountThisAttempt >= 2)
                {
                    _log($"[SweepUnstuck] No movement after 2 input refreshes. Failing attempt.");
                    FailSweepAttemptAndRetryOrSwitchSide(currX, currY, target, "no_movement_after_refresh");
                    return;
                }
            }

            // ── Sweep limit exceeded ──
            if (_sweepSweptDeg >= SWEEP_MAX_ANGLE_PER_ATTEMPT || elapsedMs >= SWEEP_MAX_ATTEMPT_MS)
            {
                _log($"[SweepUnstuck] Sweep limit: swept={_sweepSweptDeg:F1}/{SWEEP_MAX_ANGLE_PER_ATTEMPT:F0} time={elapsedMs:F0}/{SWEEP_MAX_ATTEMPT_MS:F0} clearance={anchorClearance:F2}");
                FailSweepAttemptAndRetryOrSwitchSide(currX, currY, target, "sweep_limit_reached");
                return;
            }
        }

        private void AdvanceProbe(float currX, float currY)
        {
            float distToTarget = _hasUnstuckTarget ? GeometryUtils.Distance(currX, currY, _unstuckTarget.X, _unstuckTarget.Y) : float.MaxValue;
            if (_bestProbeIndex < 0 || distToTarget < _bestProbeDistToTarget)
            {
                _bestProbePosition = (currX, currY);
                _bestProbeDistToTarget = distToTarget;
                _bestProbeIndex = _currentProbeIndex;
                _bestProbeScore = 1.0f;
                _bestProbeOffset = _currentProbeOffset;
                _log($"[UnstuckProbe] New best: ({currX:F1},{currY:F1}) dist={distToTarget:F2}");
            }
            _currentProbeIndex++;

            // Bounds check: if all probes exhausted, escalate immediately (never log 14/13)
            if (_currentProbeIndex >= UnstuckProbeAngles.Length)
            {
                _log($"[UnstuckProbe] All {UnstuckProbeAngles.Length} probes exhausted. Escalating to wall-follow.");
                _progressTracker.Reset();
                EnterUnstuckStage(3, currX, currY);
                return;
            }

            _log($"[UnstuckProbe] Advance to probe {_currentProbeIndex + 1}/{UnstuckProbeAngles.Length}.");
            _progressTracker.Reset();
            EnterUnstuckStage(2, currX, currY);
        }

        private void AttemptRollback(float currX, float currY, Waypoint target, float failedOffset)
        {
            float rollbackBearing = GeometryUtils.NormalizeBearingDeg(
                GeometryUtils.GetBearingToTargetDeg(currX, currY, target.X, target.Y) - failedOffset);
            _log($"[UnstuckProbe] Rollback along {rollbackBearing:F1} for {(int)(PROBE_INITIAL_DURATION * 500)}ms");
            MoveInDirection(rollbackBearing, true);
            Thread.Sleep((int)(PROBE_INITIAL_DURATION * 500));
            StopMoving();
            AdvanceProbe(currX, currY);
        }

        // ── Budget helpers ──

        private void EnsureUnstuckBudgetState(Waypoint target)
        {
            bool targetChanged =
                !_hasUnstuckBudgetState ||
                Math.Abs(_budgetWaypoint.X - target.X) > 0.01f ||
                Math.Abs(_budgetWaypoint.Y - target.Y) > 0.01f;

            if (targetChanged)
            {
                _budgetWaypoint = target;
                _currentWaypointUnstuckWindowStartedAt = DateTime.Now;
                _currentWaypointUnstuckAttempts = 0;
                _hasUnstuckBudgetState = true;
                _log($"[UnstuckBudget] New waypoint budget target=({target.X:F1},{target.Y:F1})");
            }

            if (_currentUnstuckAttemptStartedAt == DateTime.MinValue)
            {
                _currentUnstuckAttemptStartedAt = DateTime.Now;
                _currentWaypointUnstuckAttempts++;
                _log($"[UnstuckBudget] Attempt {_currentWaypointUnstuckAttempts} started for waypoint=({target.X:F1},{target.Y:F1})");
            }
        }

        private bool ShouldFailFastBudget(Waypoint target)
        {
            EnsureUnstuckBudgetState(target);

            if (!_hasUnstuckBudgetState) return false;
            if (_currentUnstuckAttemptStartedAt == DateTime.MinValue) return false;
            if (_currentWaypointUnstuckWindowStartedAt == DateTime.MinValue) return false;
            if (_currentWaypointUnstuckAttempts <= 0) return false;

            double attemptTime = (DateTime.Now - _currentUnstuckAttemptStartedAt).TotalSeconds;
            double totalTime = (DateTime.Now - _currentWaypointUnstuckWindowStartedAt).TotalSeconds;

            // Guard against uninitialized timestamps producing impossible values
            if (attemptTime < 0 || totalTime < 0 || attemptTime > 3600 || totalTime > 3600)
            {
                _log($"[UnstuckBudget] Invalid timer state attemptTime={attemptTime:F1}s totalTime={totalTime:F1}s; resetting budget for this target.");
                _budgetWaypoint = target;
                _currentWaypointUnstuckWindowStartedAt = DateTime.Now;
                _currentWaypointUnstuckAttempts = 0;
                _currentUnstuckAttemptStartedAt = DateTime.MinValue;
                _hasUnstuckBudgetState = true;
                return false;
            }

            if (attemptTime >= MAX_UNSTUCK_ATTEMPT_SECONDS ||
                totalTime >= MAX_TOTAL_UNSTUCK_SECONDS_PER_WAYPOINT ||
                _currentWaypointUnstuckAttempts > MAX_UNSTUCK_ATTEMPTS_PER_WAYPOINT)
            {
                _log($"[UnstuckFailFast] time_budget_exceeded attempts={_currentWaypointUnstuckAttempts}/{MAX_UNSTUCK_ATTEMPTS_PER_WAYPOINT} " +
                     $"totalTime={totalTime:F1}s/{MAX_TOTAL_UNSTUCK_SECONDS_PER_WAYPOINT:F0}s " +
                     $"attemptTime={attemptTime:F1}s/{MAX_UNSTUCK_ATTEMPT_SECONDS:F0}s " +
                     $"waypoint=({target.X:F1},{target.Y:F1})");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Clean failure that stops the current unstuck attempt without skipping waypoints.
        /// Resets state and lets normal route attempt resume on next tick.
        /// </summary>
        private void FailUnstuckAttempt(float currX, float currY, Waypoint target, string reason)
        {
            _log($"[UnstuckFailFast] reason={reason} — stopping unstuck attempt. Pos=({currX:F1},{currY:F1}) Target=({target.X:F1},{target.Y:F1})");
            StopMoving();

            // If current target is a ghost waypoint that failed, remove it from the queue
            if (_waypoints.Count > 0 && IsGhostWaypoint(_waypoints.Peek()))
            {
                var failedGhost = _waypoints.Peek();
                _log($"[UnstuckFailFast] Removing failed ghost waypoint ({failedGhost.X:F1},{failedGhost.Y:F1}) from queue.");
                RemoveGhostWaypointRecord(failedGhost);
                _waypoints.Dequeue();
                _log($"[UnstuckFailFast] Ghost removed. Queue now has {_waypoints.Count} waypoints.");
            }

            _isUnstuckRoutineActive = false;
            _unstuckStage = 0;
            _currentProbeIndex = 0;
            _hasUnstuckTarget = false;
            _unstuckStageCommandIssued = false;
            _isCommitMode = false;
            _hasBypassWaypoint = false;
            _preflightCheckDone = false;
            _wallFollowSide = 0;
            _stage3StartDist = float.MaxValue;
            _consecutiveNoMovementProbes = 0;
            _hardResetAlreadyAttempted = false;
            _anyMovementDuringUnstuck = false;
            _ghostStuckTime = 0.0;
            _ghostBestDist = float.MaxValue;
            _lastGhostCheckTime = DateTime.MinValue;
            _unstuckCooldownUntil = DateTime.Now.AddSeconds(UNSTUCK_COOLDOWN_SECONDS);
            _ignoreStuckUntil = DateTime.Now.AddSeconds(STUCK_GRACE_AFTER_START_SECONDS);
            _progressTracker.GraceUntil = DateTime.Now.AddSeconds(STUCK_GRACE_AFTER_START_SECONDS);
            ResetBearingState();
            _progressTracker.Reset();
            ResetActionStuckTracking();
            _log($"[UnstuckFailFast] Complete. Reason={reason}");
        }

        /// <summary>
        /// Hard input reset: release W, wait 120ms, face target, ForceStartMoving, observe 500ms.
        /// Retries the same probe index after reset.
        /// </summary>
        private void PerformHardInputReset(float currX, float currY, Waypoint target)
        {
            _log("[UnstuckProbe] Hard input reset: release W, wait 120ms, face target, ForceStartMoving, observe 500ms.");
            StopMoving();
            // Ensure W is fully released
            GameInput.keybd_event(GameInput.VK_W, GameInput.SCAN_W, (uint)GameInput.KEYEVENTF_KEYUP, 0);
            Thread.Sleep(120);

            // Face target
            float targetBearing = GeometryUtils.GetBearingToTargetDeg(currX, currY, target.X, target.Y);
            short gameAngle = GeometryUtils.ConvertBearingToGameAngle(targetBearing, _lastSetGameAngle, _hasLastGameAngle);
            _memoryService.SetCameraAngle(gameAngle);
            _hasLastGameAngle = true;
            _lastSetGameAngle = gameAngle;
            _lastSetBearingDeg = targetBearing;

            // Re-press W
            ForceStartMoving();

            // Observe
            _log("[UnstuckProbe] Hard input reset done. Observing 500ms...");
            Thread.Sleep(500);

            // Reset consecutive counter so the retry starts fresh
            _consecutiveNoMovementProbes = 0;

            // After observation, retry same probe by clearing command flag
            _unstuckStageCommandIssued = false;
            _log("[UnstuckProbe] Hard input reset observation complete. Retrying probe.");
        }

        // ── COMMIT MODE (after successful probe) ──

        private void StartCommitMode(float currX, float currY, Waypoint target, float successfulOffset)
        {
            _isCommitMode = true;
            _commitStartTime = DateTime.Now;
            _commitSideOffset = successfulOffset;
            _commitStartDist = GeometryUtils.Distance(currX, currY, target.X, target.Y);
            _log($"[UnstuckCommit] side={successfulOffset:F0} reducing offset, startDist={_commitStartDist:F2}");
            float targetBearing = GeometryUtils.GetBearingToTargetDeg(currX, currY, target.X, target.Y);
            float reducedOffset = successfulOffset * 0.7f;
            float commitBearing = GeometryUtils.NormalizeBearingDeg(targetBearing + reducedOffset);
            MoveInDirection(commitBearing, true);
        }

        private void HandleCommitMode(float currX, float currY, Waypoint target)
        {
            double elapsed = (DateTime.Now - _commitStartTime).TotalSeconds;
            float distNow = GeometryUtils.Distance(currX, currY, target.X, target.Y);
            float improvement = _commitStartDist - distNow;
            _log($"[UnstuckCommit] elapsed={elapsed:F2}s improvement={improvement:F2} dist={distNow:F2} offset={_commitSideOffset:F0}");

            if (improvement >= COMMIT_HEALTHY_PROGRESS)
            {
                _log($"[UnstuckCommit] Progress healthy (improvement={improvement:F2} >= {COMMIT_HEALTHY_PROGRESS:F1}) — returning to normal route.");
                _isCommitMode = false;
                EndUnstuck("commit_complete");
                return;
            }
            if (elapsed >= COMMIT_MIN_DURATION)
            {
                float targetBearing = GeometryUtils.GetBearingToTargetDeg(currX, currY, target.X, target.Y);
                float reducedOffset = _commitSideOffset * 0.5f;
                if (Math.Abs(reducedOffset) < 15f) reducedOffset = 0f;
                _commitSideOffset = reducedOffset;
                _log($"[UnstuckCommit] Reducing offset to {reducedOffset:F0}");
                MoveInDirection(GeometryUtils.NormalizeBearingDeg(targetBearing + reducedOffset), true);
            }
            if (elapsed >= COMMIT_MAX_DURATION)
            {
                // Fail if no meaningful progress was made
                if (improvement <= 0.50f)
                {
                    _log($"[UnstuckCommit] FAIL — no real progress (improvement={improvement:F2}) at max duration. Returning to probes.");
                    _isCommitMode = false;
                    // Go back to Stage 2 probes (don't EndUnstuck as success)
                    EnterUnstuckStage(2, currX, currY);
                }
                else
                {
                    _log($"[UnstuckCommit] Some progress (improvement={improvement:F2}) at max duration. Returning to normal route.");
                    _isCommitMode = false;
                    EndUnstuck("commit_timeout");
                }
            }
        }

        // ── STAGE 3: WALL FOLLOW ──

        // ── STAGE 3: SWEEP REJOIN (smooth rotation toward target after clearance) ──

        private void HandleRejoinStage(float currX, float currY, Waypoint target)
        {
            float targetBearingDeg = GeometryUtils.GetBearingToTargetDeg(currX, currY, target.X, target.Y);
            float distNow = GeometryUtils.Distance(currX, currY, target.X, target.Y);
            float anchorDist = GeometryUtils.Distance(currX, currY, _stuckAnchor.X, _stuckAnchor.Y);

            if (_sweepCurrentBearing == UnsetBearing)
                _sweepCurrentBearing = _lastSetBearingDeg != UnsetBearing ? _lastSetBearingDeg : targetBearingDeg;

            // ── Success via normal route progress ──
            if (_progressTracker.HasEnoughSamples && !_progressTracker.IsWarmingUp)
            {
                float disp = _progressTracker.GetWindowDisplacement();
                float distDelta = _progressTracker.GetWindowDistDelta();
                if (disp > 0.5f && distDelta < -0.5f)
                {
                    _log($"[SweepRejoin] Progress healthy: distDelta={distDelta:F2} disp={disp:F2}. Ending unstuck.");
                    EndUnstuck("sweep_rejoin_success");
                    return;
                }
            }

            // ── Check if target reachable (AdvanceReachedWaypoints will be called by normal flow) ──
            float reachThreshold = 5.0f;
            if (_waypoints.Count > 0)
                reachThreshold = GetEffectiveWaypointReachThreshold(_waypoints.Peek());
            if (distNow <= reachThreshold + 2.5f)
            {
                _log($"[SweepRejoin] Target close (dist={distNow:F2} <= {reachThreshold + 2.5f:F2}). Ending unstuck to let advance handle it.");
                EndUnstuck("sweep_rejoin_close");
                return;
            }

            // ── If we lost clearance, go back to sweep ──
            if (anchorDist < SWEEP_SUCCESS_CLEARANCE * 0.5f)
            {
                _log($"[SweepRejoin] Lost clearance (anchorDist={anchorDist:F2} < {SWEEP_SUCCESS_CLEARANCE * 0.5f:F1}). Returning to sweep.");
                _progressTracker.Reset();
                EnterUnstuckStage(2, currX, currY);
                return;
            }

            // ── Smooth rotate toward target bearing ──
            double msSinceUpdate = (DateTime.Now - _sweepLastCameraUpdateAt).TotalMilliseconds;
            if (msSinceUpdate >= SWEEP_CAMERA_UPDATE_INTERVAL_MS)
            {
                float diff = GeometryUtils.GetShortestBearingDiffDeg(_sweepCurrentBearing, targetBearingDeg);
                float absDiff = Math.Abs(diff);

                // Bias toward sweep side for first ~500ms to avoid snapping back into obstacle
                double rejoinElapsed = (DateTime.Now - _preflightStartTime).TotalMilliseconds;
                float targetForRotation = targetBearingDeg;
                if (rejoinElapsed < 600.0 && _sweepSide != 0)
                    targetForRotation = GeometryUtils.NormalizeBearingDeg(targetBearingDeg + _sweepSide * 30f);

                diff = GeometryUtils.GetShortestBearingDiffDeg(_sweepCurrentBearing, targetForRotation);
                absDiff = Math.Abs(diff);

                if (absDiff > 1.0f)
                {
                    // Max 6 degrees per update for smooth rejoin
                    float step = Math.Min(6.0f, absDiff);
                    _sweepCurrentBearing = GeometryUtils.NormalizeBearingDeg(_sweepCurrentBearing + Math.Sign(diff) * step);
                    _sweepLastCameraUpdateAt = DateTime.Now;

                    short gameAngle = GeometryUtils.ConvertBearingToGameAngle(_sweepCurrentBearing, _lastSetGameAngle, _hasLastGameAngle);
                    _hasLastGameAngle = true;
                    _lastSetGameAngle = gameAngle;
                    _lastSetBearingDeg = _sweepCurrentBearing;
                    _memoryService.SetCameraAngle(gameAngle);

                    if (_tickCount % 3 == 0)
                        _log($"[SweepRejoin] bearing={_sweepCurrentBearing:F1} target={targetBearingDeg:F1} diff={absDiff:F1} step={step:F1} progressHealthy={_progressTracker.HasEnoughSamples && !_progressTracker.IsWarmingUp}");
                }

                // Keep W held
                if (!_isMovingForward)
                    StartMoving();
            }

            // ── Escalate to sweep-retry if no progress after 2s ──
            double stageElapsed = (DateTime.Now - _preflightStartTime).TotalMilliseconds;
            if (stageElapsed > 2000.0)
            {
                float moved = GeometryUtils.Distance(currX, currY, _stuckAnchor.X, _stuckAnchor.Y);
                float distDelta = distNow - _preflightStartDist;
                if (moved < SWEEP_SUCCESS_CLEARANCE && distDelta > -1.0f)
                {
                    _log($"[SweepRejoin] No rejoin progress after {stageElapsed / 1000:F1}s. Returning to sweep. moved={moved:F2} distDelta={distDelta:F2}");
                    _progressTracker.Reset();
                    EnterUnstuckStage(2, currX, currY);
                    return;
                }
            }
        }

        // ── STAGE 4: BYPASS WAYPOINT ──

        private void HandleBypassStage(float currX, float currY, Waypoint target)
        {
            if (!_hasBypassWaypoint)
            {
                // Only create bypass if target is local (within LOCAL_UNSTUCK_MAX_TARGET_DISTANCE)
                float distToRealTarget = GeometryUtils.Distance(currX, currY, target.X, target.Y);
                if (distToRealTarget > LOCAL_UNSTUCK_MAX_TARGET_DISTANCE)
                {
                    _log($"[Bypass] Target too far ({distToRealTarget:F1} > {LOCAL_UNSTUCK_MAX_TARGET_DISTANCE:F0}) for local bypass. Failing fast.");
                    FailUnstuckAttempt(currX, currY, target, "target_too_far_for_bypass");
                    return;
                }

                // Check ghost limit per real waypoint
                if (_ghostsForCurrentRealWaypoint >= MAX_GHOSTS_PER_REAL_WAYPOINT)
                {
                    _log($"[Bypass] Ghost limit ({MAX_GHOSTS_PER_REAL_WAYPOINT}) reached for this waypoint. Failing fast.");
                    FailUnstuckAttempt(currX, currY, target, "ghost_limit_reached");
                    return;
                }

                float targetBearing = GeometryUtils.GetBearingToTargetDeg(currX, currY, target.X, target.Y);
                float sideBias = _bestProbeOffset != 0 ? Math.Sign(_bestProbeOffset) : 1f;
                float escapeAngle = GeometryUtils.NormalizeBearingDeg(targetBearing + (sideBias * 60f));
                _log($"[Bypass] Creating local bypass from ({currX:F1},{currY:F1}) sideBias={sideBias:F0}");
                CreateGhostWaypoint(currX, currY, target, escapeAngle, "bypass");
                _hasBypassWaypoint = true;
                return;
            }
            if (TryResolveRouteProgressDuringUnstuck(currX, currY))
            {
                _log($"[Bypass] Route progress resumed after bypass.");
                EndUnstuck("bypass_success");
                return;
            }
            _log($"[Bypass] Bypass did not resolve. Escalating to last resort.");
            _hasBypassWaypoint = false;
            _progressTracker.Reset();
            EnterUnstuckStage(5, currX, currY);
        }

        // ── STAGE 5: LAST RESORT ──

        private void HandleLastResortStage(float currX, float currY, Waypoint target)
        {
            _totalEscalationAttempts++;
            if (!_unstuckStageCommandIssued)
            {
                if (DateTime.Now < _unstuckStageCommandAllowedAt) return;
                StopMoving();
                float pb = GeometryUtils.NormalizeBearingDeg(
                    GeometryUtils.GetBearingToTargetDeg(currX, currY, target.X, target.Y) + 45f);
                _log($"[LastResort] Short diagonal probe bearing={pb:F1}");
                MoveInDirection(pb, true);
                _unstuckStageCommandIssued = true;
                _unstuckStageStartTime = DateTime.Now;
                return;
            }
            double elapsed = (DateTime.Now - _unstuckStageStartTime).TotalSeconds;
            if (elapsed >= 1.0)
            {
                StopMoving();
                float distNow = GeometryUtils.Distance(currX, currY, target.X, target.Y);
                if (distNow < _unstuckStartDistToTarget - 1.0f)
                {
                    _log($"[LastResort] Probe improved ({distNow:F2} vs {_unstuckStartDistToTarget:F2}) — resuming.");
                    EndUnstuck("lastresort_success");
                    return;
                }
                if (_waypoints.Count > 1)
                {
                    // Only skip if safe: not final waypoint AND (close enough OR next is closer)
                    var currentTarget = _waypoints.Peek();
                    bool isFinal = IsLastWaypoint();
                    float distToCurrent = GeometryUtils.Distance(currX, currY, currentTarget.X, currentTarget.Y);
                    bool closeEnoughToSkip = distToCurrent <= 15.0f;

                    // Check if next waypoint is closer
                    var items = _waypoints.ToList();
                    bool nextIsCloser = false;
                    if (items.Count >= 2)
                    {
                        float distToNext = GeometryUtils.Distance(currX, currY, items[1].X, items[1].Y);
                        nextIsCloser = distToNext < distToCurrent;
                        _log($"[LastResort] distToCurrent={distToCurrent:F2} distToNext={distToNext:F2} nextIsCloser={nextIsCloser}");
                    }

                    if (!isFinal && (closeEnoughToSkip || nextIsCloser) && _anyMovementDuringUnstuck)
                    {
                        _log($"[LastResort] Skipping blocked waypoint (safe: isFinal={isFinal} dist={distToCurrent:F2} moved={_anyMovementDuringUnstuck}).");
                        _waypoints.Dequeue();
                        _progressTracker.Reset();
                        ResetBearingState();
                        EndUnstuck("waypoint_skipped");
                        return;
                    }
                    _log($"[LastResort] NOT skipping waypoint — unsafe. isFinal={isFinal} distToCurrent={distToCurrent:F2} closeEnough={closeEnoughToSkip} anyMovement={_anyMovementDuringUnstuck}");
                }
                if (distNow <= FINAL_GOAL_SOFT_RADIUS)
                {
                    _log($"[LastResort] Soft completion ({distNow:F2} <= {FINAL_GOAL_SOFT_RADIUS:F1}).");
                    StopMoving();
                    _goalReached = true;
                    EndUnstuck("lastresort_goal");
                    return;
                }
                _log($"[LastResort] Cannot resolve. dist={distNow:F2} > {FINAL_GOAL_SOFT_RADIUS:F1}. Ending.");
                EndUnstuck("lastresort_failed");
            }
        }

        // ── GHOST WAYPOINT (shared) ──

        private void CreateGhostWaypoint(float currX, float currY, Waypoint target, float escapeBearingDeg, string reason)
        {
            _log($"[Ghost] Creating ghost waypoint. Reason:{reason} CurrPos:({currX:F1},{currY:F1}) EscapeBearing:{escapeBearingDeg:F1} Escalation:{_totalEscalationAttempts}");
            StopMoving();
            float targetBearing = GeometryUtils.GetBearingToTargetDeg(currX, currY, target.X, target.Y);
            float randomEscapeOffset = (float)(_rng.NextDouble() * 20.0 - 10.0);
            float adjustedEscapeBearing = GeometryUtils.NormalizeBearingDeg(escapeBearingDeg + randomEscapeOffset);
            float escapeDist = BYPASS_GHOST_DISTANCE + Math.Min(_totalEscalationAttempts * 4f, 20f);
            float forwardDist = BYPASS_FORWARD_DISTANCE + Math.Min(_totalEscalationAttempts * 2f, 10f);
            float ghostX = currX + (GeometryUtils.BearingToDeltaX(adjustedEscapeBearing) * escapeDist)
                                 + (GeometryUtils.BearingToDeltaX(targetBearing) * forwardDist);
            float ghostY = currY + (GeometryUtils.BearingToDeltaY(adjustedEscapeBearing) * escapeDist)
                                 + (GeometryUtils.BearingToDeltaY(targetBearing) * forwardDist);
            _log($"[Ghost] ghostPos=({ghostX:F1},{ghostY:F1})");
            var newQueue = new Queue<Waypoint>();
            var ghostWaypoint = new Waypoint(ghostX, ghostY, MovementPrecision.Accurate, BotMode.OnlyMove);
            newQueue.Enqueue(ghostWaypoint);
            _ghostWaypoints.Add((ghostX, ghostY));
            int originalCount = _waypoints.Count;
            while (_waypoints.Count > 0) newQueue.Enqueue(_waypoints.Dequeue());
            _waypoints = newQueue;
            _totalEscalationAttempts++;
            _progressTracker.SetSegment(currX, currY, ghostX, ghostY);
            _ghostsForCurrentRealWaypoint++;
            _log($"[Ghost] Created by {reason} at ({ghostX:F1},{ghostY:F1}). GhostsForThisRealWp={_ghostsForCurrentRealWaypoint}. Escalation:{_totalEscalationAttempts}. Resuming.");
            EndUnstuck("ghost_created");
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

                _log($"[AdvanceWp] Checking wp #{wpIndex}: ({target.X:F1},{target.Y:F1}) Prec:{target.Precision} Mode:{target.Mode} | Dist:{dist:F2} <= Threshold:{threshold:F2}");

                if (dist > threshold)
                {
                    _log($"[AdvanceWp] Not reached (dist {dist:F2} > {threshold:F2}) — stopping advance scan.");
                    break;
                }

                bool isGhost = IsGhostWaypoint(target);

                if (isGhost)
                {
                    _log($"[AdvanceWp] REACHED Ghost Waypoint ({target.X:F1}, {target.Y:F1}) [Dist: {dist:F1} <= {threshold:F1}]. Removing temporary waypoint.");
                    RemoveGhostWaypointRecord(target);
                }
                else
                {
                    _log($"[AdvanceWp] REACHED Waypoint ({target.X}, {target.Y}) [Dist: {dist:F1} <= {threshold:F1} (Prec: {target.Precision}, Mode: {target.Mode})].");
                }

                _waypoints.Dequeue();
                _log($"[AdvanceWp] Waypoint dequeued. Queue now has {_waypoints.Count} waypoints remaining.");
                ResetBearingState();
                _progressTracker.Reset();
                ResetActionStuckTracking();

                advanced = true;

                if (_waypoints.Count == 0)
                {
                    _log($"[AdvanceWp] Queue empty after dequeue — checking for loop/reached.");
                    HandleEmptyWaypointQueueAfterAdvance();

                    if (_goalReached)
                    {
                        _log($"[AdvanceWp] Goal reached flag set — returning.");
                        return true;
                    }
                }
            }

            if (!advanced)
                _log($"[AdvanceWp] No waypoints advanced this tick.");

            return advanced;
        }

        private bool TrySkipGhostsIfRealWaypointReached(float currX, float currY)
        {
            _log($"[GhostSkip] Checking ghost bypass. Queue count: {_waypoints.Count}");

            if (_waypoints.Count < 2)
            {
                _log($"[GhostSkip] Need at least 2 waypoints for ghost skip (have {_waypoints.Count}).");
                return false;
            }

            var currentTarget = _waypoints.Peek();

            if (!IsGhostWaypoint(currentTarget))
            {
                _log("[GhostSkip] Current target is not a ghost — no bypass needed.");
                return false;
            }

            _log($"[GhostSkip] Current target IS a ghost ({currentTarget.X:F1},{currentTarget.Y:F1}). Looking ahead for real waypoints.");

            var items = _waypoints.ToList();
            int limit = Math.Min(items.Count, REAL_WAYPOINT_LOOKAHEAD_LIMIT);
            _log($"[GhostSkip] Looking ahead through {limit} waypoints (queue has {items.Count} total).");

            for (int i = 1; i < limit; i++)
            {
                Waypoint candidate = items[i];

                if (IsGhostWaypoint(candidate))
                {
                    _log($"[GhostSkip] Candidate #{i} ({candidate.X:F1},{candidate.Y:F1}) is also a ghost — skipping.");
                    continue;
                }

                float dist = GeometryUtils.Distance(currX, currY, candidate.X, candidate.Y);
                float threshold = GeometryUtils.GetWaypointReachThreshold(candidate.Precision) + REAL_WAYPOINT_LOOKAHEAD_REACH_EXTRA;

                _log($"[GhostSkip] Checking real waypoint #{i}: ({candidate.X:F1},{candidate.Y:F1}) Dist:{dist:F2} Threshold:{threshold:F2}");

                if (dist <= threshold)
                {
                    _log($"[GhostSkip] REAL WAYPOINT REACHED! ({candidate.X:F1},{candidate.Y:F1}) Dist:{dist:F2} <= {threshold:F2}. Dropping ghost path.");

                    int ghostsRemoved = 0;
                    for (int j = 0; j <= i; j++)
                    {
                        if (IsGhostWaypoint(items[j]))
                        {
                            RemoveGhostWaypointRecord(items[j]);
                            ghostsRemoved++;
                        }
                    }
                    _log($"[GhostSkip] Removed {ghostsRemoved} ghost records.");

                    var newQueue = new Queue<Waypoint>();

                    for (int j = i + 1; j < items.Count; j++)
                    {
                        newQueue.Enqueue(items[j]);
                    }

                    _waypoints = newQueue;
                    _log($"[GhostSkip] New waypoint queue has {_waypoints.Count} entries (dropped first {i + 1}).");

                    ResetBearingState();
                    _progressTracker.Reset();
                    ResetActionStuckTracking();

                    if (_waypoints.Count == 0)
                    {
                        _log("[GhostSkip] Queue became empty after rebuild — calling HandleEmptyWaypointQueue.");
                        HandleEmptyWaypointQueueAfterAdvance();
                    }

                    return true;
                }
                else
                {
                    _log($"[GhostSkip] Real waypoint #{i} not yet near (dist {dist:F2} > {threshold:F2}).");
                }
            }

            _log("[GhostSkip] No real waypoint found near enough to skip ghosts.");
            return false;
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

        private void RemoveGhostWaypointRecord(Waypoint waypoint)
        {
            for (int i = _ghostWaypoints.Count - 1; i >= 0; i--)
            {
                if (GeometryUtils.Distance(waypoint.X, waypoint.Y, _ghostWaypoints[i].X, _ghostWaypoints[i].Y) <= GHOST_MATCH_EPSILON)
                {
                    _ghostWaypoints.RemoveAt(i);
                }
            }
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

        // ========================================================================
        //  MOVEMENT
        // ========================================================================

        private void MoveInDirection(float bearingDeg)
        {
            MoveInDirection(bearingDeg, false);
        }

        private void MoveInDirection(float bearingDeg, bool forceFreshMove)
        {
            bearingDeg = GeometryUtils.NormalizeBearingDeg(bearingDeg);

            short gameAngle = GeometryUtils.ConvertBearingToGameAngle(bearingDeg, _lastSetGameAngle, _hasLastGameAngle);

            _lastSetBearingDeg = bearingDeg;
            _hasLastGameAngle = true;
            _lastSetGameAngle = gameAngle;

            _log($"[MoveInDirection] Tick:{_tickCount} Bearing:{bearingDeg:F1} -> GameAngle:{gameAngle} forceFresh:{forceFreshMove}");
            _memoryService.SetCameraAngle(gameAngle);

            _log($"[MoveInDirection] StartMoving (W press) — tick:{_tickCount}");

            if (forceFreshMove)
            {
                ForceStartMoving();
            }
            else
            {
                StartMoving();
            }
        }

        private void MoveTowards(float currX, float currY, float targetX, float targetY)
        {
            float targetBearingDeg = GeometryUtils.GetBearingToTargetDeg(currX, currY, targetX, targetY);
            ++_moveLogCounter;

            // ── 1. Initial bearing setup (snap allowed once) ──
            if (_lastSetBearingDeg == UnsetBearing)
            {
                _lastSetBearingDeg = targetBearingDeg;
                _smoothedTargetBearingDeg = targetBearingDeg;

                short firstGameAngle = GeometryUtils.ConvertBearingToGameAngle(_lastSetBearingDeg, _lastSetGameAngle, _hasLastGameAngle);
                _hasLastGameAngle = true;
                _lastSetGameAngle = firstGameAngle;

                _log($"[Move] INITIAL BEARING SETUP. Bearing:{targetBearingDeg:F1} GameAngle:{firstGameAngle}");
                _memoryService.SetCameraAngle(firstGameAngle);
                _lastCameraUpdateAt = DateTime.Now;
                StartMoving();
                return;
            }

            _lastSetBearingDeg = GeometryUtils.NormalizeBearingDeg(_lastSetBearingDeg);

            // ── 2. Compute distance and waypoint info ──
            float distToTarget = GeometryUtils.Distance(currX, currY, targetX, targetY);
            float reachThreshold = 5.0f;
            bool isLastWp = true;

            if (_waypoints.Count > 0)
            {
                var curTarget = _waypoints.Peek();
                reachThreshold = GetEffectiveWaypointReachThreshold(curTarget);
                isLastWp = IsLastWaypoint();
            }

            // ── 3. Lookahead: near non-final waypoint, aim toward next real waypoint ──
            float desiredBearing = targetBearingDeg;
            bool suppressLookahead = false;

            // For Exact waypoints, suppress lookahead unless very close (threshold + 1.0)
            if ((int)MovementPrecision.Exact == 0 && _waypoints.Count > 0)
            {
                var curTarget = _waypoints.Peek();
                if (curTarget.Precision == MovementPrecision.Exact && distToTarget > reachThreshold + 1.0f)
                {
                    suppressLookahead = true;
                    _log($"[Camera] Lookahead suppressed: Exact waypoint dist={distToTarget:F2} threshold={reachThreshold:F2}");
                }
            }

            if (!suppressLookahead && !isLastWp && distToTarget <= CAMERA_LOOKAHEAD_RADIUS && _waypoints.Count >= 2)
            {
                var items = _waypoints.ToList();
                for (int i = 1; i < items.Count; i++)
                {
                    if (!IsGhostWaypoint(items[i]))
                    {
                        float lookBearing = GeometryUtils.GetBearingToTargetDeg(currX, currY, items[i].X, items[i].Y);
                        _log($"[Camera] Lookahead bearing={lookBearing:F1} to next real wp ({items[i].X:F1},{items[i].Y:F1})");
                        desiredBearing = lookBearing;
                        break;
                    }
                }
            }

            // ── 4. Arrival freeze: within reach radius, do NOT adjust camera ──
            float freezeRadius = reachThreshold + CAMERA_ARRIVAL_FREEZE_EXTRA;
            if (distToTarget <= freezeRadius)
            {
                _log($"[Camera] Arrival freeze: dist={distToTarget:F2} freezeRadius={freezeRadius:F2} keeping={_lastSetBearingDeg:F1}");
                StartMoving();
                return;
            }

            // ── 5. Rate limit camera updates ──
            double msSinceLastUpdate = (DateTime.Now - _lastCameraUpdateAt).TotalMilliseconds;
            if (msSinceLastUpdate < CAMERA_MIN_UPDATE_INTERVAL_MS)
            {
                if (_tickCount % 5 == 0)
                    _log($"[Camera] Skip update: rate-limit {msSinceLastUpdate:F0}ms/{CAMERA_MIN_UPDATE_INTERVAL_MS:F0}ms");
                StartMoving();
                return;
            }

            // ── 6. Compute diff vs desired bearing and apply deadzone ──
            float diff = GeometryUtils.GetShortestBearingDiffDeg(_lastSetBearingDeg, desiredBearing);
            float absDiff = Math.Abs(diff);
            bool nearTarget = distToTarget <= CAMERA_NEAR_TARGET_RADIUS;
            float deadzone = nearTarget ? CAMERA_NEAR_TARGET_DEADZONE_DEG : CAMERA_NORMAL_DEADZONE_DEG;

            if (absDiff <= deadzone)
            {
                if (_tickCount % 5 == 0)
                    _log($"[Camera] Within deadzone: diff={absDiff:F2} deadzone={deadzone:F1} near={nearTarget}");
                StartMoving();
                return;
            }

            // ── 7. Apply one smooth step (max one SetCameraAngle per tick) ──
            float maxStep = nearTarget ? CAMERA_MAX_STEP_DEG_NEAR_TARGET : CAMERA_MAX_STEP_DEG_NORMAL;
            float step = Math.Min(maxStep, absDiff);
            float newBearing = GeometryUtils.NormalizeBearingDeg(_lastSetBearingDeg + Math.Sign(diff) * step);
            short gameAngle = GeometryUtils.ConvertBearingToGameAngle(newBearing, _lastSetGameAngle, _hasLastGameAngle);

            _log($"[Camera] SmoothAdjust dist={distToTarget:F2} desired={desiredBearing:F1} current={_lastSetBearingDeg:F1} diff={absDiff:F2} step={step:F2} near={nearTarget} next={newBearing:F1} gameAngle={gameAngle}");

            _lastSetBearingDeg = newBearing;
            _smoothedTargetBearingDeg = newBearing;
            _hasLastGameAngle = true;
            _lastSetGameAngle = gameAngle;
            _memoryService.SetCameraAngle(gameAngle);
            _lastCameraUpdateAt = DateTime.Now;

            StartMoving();
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
            _actionStuckCounter = 0;
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
        }

        // ========================================================================
        //  ACTION-BASED STUCK DETECTION
        // ========================================================================

        private bool IsActionRunning(byte currentAction)
        {
            return currentAction == 27 || currentAction == 3;
        }

        private bool IsActionIdleOrStuck(byte currentAction)
        {
            return currentAction == 25 || currentAction == 1;
        }

        private bool IsActionStuck()
        {
            if (!_isMovingForward)
                return false;

            byte currentAction = _memoryService.GetCurrentAction();

            if (IsActionRunning(currentAction))
            {
                ResetActionStuckStateIfNeeded();
                return false;
            }

            if (IsActionIdleOrStuck(currentAction))
            {
                return true;
            }

            return false;
        }

        private void ResetActionStuckStateIfNeeded()
        {
            if (_actionStuckCounter > 0)
            {
                _actionStuckCounter = 0;
            }
        }

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
            if (!IsActionIdleOrStuck(action))
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

        /// <summary>
        /// If the bot is action-stuck very close to the current waypoint and there are
        /// more waypoints ahead, skip this waypoint instead of entering Bug2.
        /// </summary>
        private bool TrySkipCurrentWaypointBecauseClose(float currX, float currY, Waypoint target)
        {
            if (_waypoints.Count <= 1)
                return false;

            // Only skip when action confirms stuck (25 or 1)
            byte currentAction = _memoryService.GetCurrentAction();
            if (!IsActionIdleOrStuck(currentAction))
                return false;

            float dist = GeometryUtils.Distance(currX, currY, target.X, target.Y);
            float normalThreshold = GetEffectiveWaypointReachThreshold(target);
            float effectiveSkipDistance = Math.Max(normalThreshold, STUCK_SOFT_SKIP_DISTANCE);

            if (dist <= effectiveSkipDistance)
            {
                _log($"[SoftSkip] Action stuck near waypoint. Action={currentAction} Dist={dist:F2} <= {effectiveSkipDistance:F2}. Skipping waypoint ({target.X},{target.Y}).");
                _waypoints.Dequeue();
                ResetBearingState();
                _progressTracker.Reset();
                ResetActionStuckTracking();
                _log($"[SoftSkip] Waypoint skipped. Queue now has {_waypoints.Count} waypoints.");
                return true;
            }

            return false;
        }

        // ========================================================================
        //  BUG2 — ENTER / RESET
        // ========================================================================

        private void EnterBug2Mode(float currX, float currY, Waypoint target)
        {
            _bug2Target = target;
            _bug2StartPoint = (currX, currY);
            _bug2HitPoint = (currX, currY);
            _bug2HitDistanceToTarget = GeometryUtils.Distance(currX, currY, target.X, target.Y);
            _bug2FollowLeft = true;
            _bug2StartedAt = DateTime.Now;
            _bug2StepCount = 0;
            _bug2FailedMoveCount = 0;
            _bug2SameSideSteps = 0;
            _bug2State = Bug2NavState.FollowBoundary;
            _bug2CandidateIssued = false;
            _bug2CandidateIndex = 0;

            _log($"[Bug2] Enter. Start=({currX:F1},{currY:F1}), Hit=({currX:F1},{currY:F1}), Target=({target.X:F1},{target.Y:F1}), HitDist={_bug2HitDistanceToTarget:F2}");
        }

        private void ResetBug2State()
        {
            if (_bug2State != Bug2NavState.None)
            {
                _log("[Bug2] Reset.");
            }

            _bug2State = Bug2NavState.None;
            _bug2Target = null;
            _bug2StartPoint = (0, 0);
            _bug2HitPoint = (0, 0);
            _bug2HitDistanceToTarget = float.MaxValue;
            _bug2StepCount = 0;
            _bug2FailedMoveCount = 0;
            _bug2SameSideSteps = 0;
            _bug2CandidateIssued = false;

            SaveLocalMapIfDirty();
        }

        // ========================================================================
        //  BUG2 — NAVIGATION STEP
        // ========================================================================

        private void RunBug2NavigationStep(float currX, float currY, Waypoint target)
        {
            // 1. Advance reached waypoints (including the Bug2 target if reached)
            AdvanceReachedWaypoints(currX, currY);

            // 2. Check if waypoints became empty
            if (_waypoints.Count == 0)
            {
                _log("[Bug2] Waypoints empty after advance. Resetting Bug2.");
                ResetBug2State();
                SaveLocalMapIfDirty();
                return;
            }

            // 3. Soft skip if stuck very close to current waypoint
            Waypoint currentTarget = _waypoints.Peek();
            if (TrySkipCurrentWaypointBecauseClose(currX, currY, currentTarget))
            {
                _log("[Bug2] Waypoint skipped during Bug2. Resetting Bug2.");
                ResetBug2State();
                SaveLocalMapIfDirty();
                return;
            }

            // 4. Check Bug2 limits
            if (Bug2LimitsExceeded())
            {
                _log("[Bug2] Limits exceeded. Falling back.");
                ResetBug2State();
                StopMoving();
                SaveLocalMapIfDirty();
                return;
            }

            // 5. Check if we can leave boundary following
            if (CanLeaveBug2Boundary(currX, currY, currentTarget))
            {
                _log("[Bug2] On m-line and closer than hit point. Leaving boundary.");
                ResetBug2State();
                MoveTowards(currX, currY, currentTarget.X, currentTarget.Y);
                SaveLocalMapIfDirty();
                return;
            }

            // 6. Perform boundary following step
            TryBug2BoundaryStep(currX, currY, currentTarget);
        }

        private bool Bug2LimitsExceeded()
        {
            if (_bug2StepCount >= BUG2_MAX_TOTAL_STEPS)
            {
                _log($"[Bug2] Max total steps reached ({_bug2StepCount}/{BUG2_MAX_TOTAL_STEPS}).");
                return true;
            }

            if (_bug2FailedMoveCount >= BUG2_MAX_FAILED_MOVES)
            {
                _log($"[Bug2] Max failed moves reached ({_bug2FailedMoveCount}/{BUG2_MAX_FAILED_MOVES}).");
                return true;
            }

            double elapsed = (DateTime.Now - _bug2StartedAt).TotalSeconds;
            if (elapsed >= BUG2_MAX_DURATION_SECONDS)
            {
                _log($"[Bug2] Max duration reached ({elapsed:F1}s/{BUG2_MAX_DURATION_SECONDS:F0}s).");
                return true;
            }

            return false;
        }

        // ========================================================================
        //  BUG2 — LEAVE CONDITION
        // ========================================================================

        private bool CanLeaveBug2Boundary(float currX, float currY, Waypoint target)
        {
            // Condition 1: Close to m-line
            float distToMLine = DistancePointToLine(
                currX, currY,
                _bug2StartPoint.X, _bug2StartPoint.Y,
                target.X, target.Y);

            if (distToMLine > BUG2_M_LINE_TOLERANCE)
                return false;

            // Condition 2: Closer to target than hit point
            float distToTarget = GeometryUtils.Distance(currX, currY, target.X, target.Y);
            if (distToTarget >= _bug2HitDistanceToTarget - BUG2_LEAVE_MIN_IMPROVEMENT)
                return false;

            // Condition 3: No blocked cells on line to target
            if (IsKnownPathBlockedToTarget(currX, currY, target))
                return false;

            // Condition 4: Not currently action-stuck
            byte action = _memoryService.GetCurrentAction();
            if (IsActionIdleOrStuck(action))
                return false;

            return true;
        }

        private float DistancePointToLine(float px, float py, float ax, float ay, float bx, float by)
        {
            float dx = bx - ax;
            float dy = by - ay;
            float lengthSq = dx * dx + dy * dy;

            if (lengthSq < 0.0001f)
            {
                // Line is a point
                return GeometryUtils.Distance(px, py, ax, ay);
            }

            float t = ((px - ax) * dx + (py - ay) * dy) / lengthSq;
            t = Math.Clamp(t, 0f, 1f);

            float closestX = ax + t * dx;
            float closestY = ay + t * dy;

            return GeometryUtils.Distance(px, py, closestX, closestY);
        }

        private bool IsKnownPathBlockedToTarget(float currX, float currY, Waypoint target)
        {
            // Sample cells along the line from current to target every ~1 unit
            float dx = target.X - currX;
            float dy = target.Y - currY;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy);

            if (dist < 0.5f)
                return false;

            float steps = Math.Max(2, (int)Math.Ceiling(dist));
            float stepX = dx / steps;
            float stepY = dy / steps;

            for (int i = 1; i < steps; i++)
            {
                float sampleX = currX + stepX * i;
                float sampleY = currY + stepY * i;
                (int cx, int cy) = LocalNavigationMap.WorldToCell(sampleX, sampleY);

                if (_localNavigationMap.IsBlockedCell(cx, cy))
                    return true;
            }

            return false;
        }

        // ========================================================================
        //  BUG2 — BOUNDARY FOLLOWING
        // ========================================================================

        private void TryBug2BoundaryStep(float currX, float currY, Waypoint target)
        {
            // If we are in the middle of testing a candidate, check its result
            if (_bug2CandidateIssued)
            {
                CheckBug2CandidateResult(currX, currY, target);
                return;
            }

            // Generate candidates and find a viable one
            TryIssueBug2Candidate(currX, currY, target);
        }

        private void TryIssueBug2Candidate(float currX, float currY, Waypoint target)
        {
            // Generate candidate offsets based on follow direction
            float[] offsets = _bug2FollowLeft
                ? new[] { -90f, -45f, 0f, 45f, 90f, 135f, -135f, 180f }
                : new[] { 90f, 45f, 0f, -45f, -90f, -135f, 135f, 180f };

            float baseBearing;
            if (_lastSetBearingDeg != UnsetBearing)
                baseBearing = _lastSetBearingDeg;
            else
                baseBearing = GeometryUtils.GetBearingToTargetDeg(currX, currY, target.X, target.Y);

            (int sourceCellX, int sourceCellY) = LocalNavigationMap.WorldToCell(currX, currY);

            // Pre-scan: check which candidates are blocked by the local map
            var viableCandidates = new List<(int Index, float Bearing, int CellX, int CellY, int DirX, int DirY, bool IsRisky)>();

            for (int i = 0; i < offsets.Length; i++)
            {
                float candidateBearing = GeometryUtils.NormalizeBearingDeg(baseBearing + offsets[i]);
                float rad = GeometryUtils.DegToRad(candidateBearing);
                float ddx = (float)Math.Sin(rad);
                float ddy = (float)Math.Cos(rad);

                int dirX = ddx > 0.3f ? 1 : (ddx < -0.3f ? -1 : 0);
                int dirY = ddy > 0.3f ? 1 : (ddy < -0.3f ? -1 : 0);

                int cellX = sourceCellX + dirX;
                int cellY = sourceCellY + dirY;

                LocalCellState cellState = _localNavigationMap.GetCell(cellX, cellY);

                if (cellState == LocalCellState.Blocked)
                {
                    _log($"[Bug2] Candidate bearing={candidateBearing:F1} direction=({dirX},{dirY}) cell=({cellX},{cellY}) mapState=Blocked. Skipping.");
                    continue;
                }

                bool isRisky = cellState == LocalCellState.Risky;
                viableCandidates.Add((i, candidateBearing, cellX, cellY, dirX, dirY, isRisky));
            }

            if (viableCandidates.Count == 0)
            {
                _log("[Bug2] All candidates blocked by local map. No viable direction.");
                _bug2FailedMoveCount++;
                _bug2SameSideSteps++;
                CheckBug2SideSwitch();
                return;
            }

            // Prefer non-Risky (Free/Unknown) candidates; use Risky only if no other choice
            var bestCandidates = viableCandidates.Where(c => !c.IsRisky).ToList();
            if (bestCandidates.Count == 0)
            {
                bestCandidates = viableCandidates;
            }

            // Pick the first viable candidate
            var chosen = bestCandidates[0];

            _bug2CandidateIssued = true;
            _bug2CandidateIndex = chosen.Index;
            _bug2CandidateTestBearing = chosen.Bearing;
            _bug2CandidateTestCellX = chosen.CellX;
            _bug2CandidateTestCellY = chosen.CellY;
            _bug2CandidateAttemptedDirX = chosen.DirX;
            _bug2CandidateAttemptedDirY = chosen.DirY;
            _bug2CandidateStartTime = DateTime.Now;

            // Set camera and start moving
            short gameAngle = GeometryUtils.ConvertBearingToGameAngle(chosen.Bearing, _lastSetGameAngle, _hasLastGameAngle);
            _hasLastGameAngle = true;
            _lastSetGameAngle = gameAngle;
            _lastSetBearingDeg = chosen.Bearing;
            _memoryService.SetCameraAngle(gameAngle);

            _log($"[Bug2] Try candidate bearing={chosen.Bearing:F1} direction=({chosen.DirX},{chosen.DirY}) cell=({chosen.CellX},{chosen.CellY}) mapState={(_localNavigationMap.GetCell(chosen.CellX, chosen.CellY) == LocalCellState.Risky ? "Risky" : "Free/Unknown")}");

            ForceStartMoving();
        }

        private void CheckBug2CandidateResult(float currX, float currY, Waypoint target)
        {
            double elapsed = (DateTime.Now - _bug2CandidateStartTime).TotalMilliseconds;
            if (elapsed < BUG2_CANDIDATE_OBSERVE_MS)
            {
                // Still waiting for the candidate to take effect
                return;
            }

            byte currentAction = _memoryService.GetCurrentAction();

            if (IsActionRunning(currentAction))
            {
                // Candidate succeeded
                _log($"[Bug2] Candidate succeeded by action running. Action={currentAction}.");

                // Mark current position cell as Free
                _localNavigationMap.MarkFree(currX, currY, "bug2-candidate-success");

                _bug2StepCount++;
                _bug2SameSideSteps = 0;
                _bug2CandidateIssued = false;
                SaveLocalMapIfDirty();
            }
            else if (IsActionIdleOrStuck(currentAction))
            {
                // Candidate failed
                _log($"[Bug2] Candidate failed by action stuck. Action={currentAction}. Marking attempted cell.");

                StopMoving();

                (int sourceCellX, int sourceCellY) = LocalNavigationMap.WorldToCell(currX, currY);
                (int targetCellX, int targetCellY) = LocalNavigationMap.WorldToCell(target.X, target.Y);

                // Mark the attempted cell as Risky/Blocked
                _localNavigationMap.MarkStuckAttemptedCell(
                    _bug2CandidateTestCellX + 0.5f, _bug2CandidateTestCellY + 0.5f,
                    "blocked-when-moving-to-waypoint",
                    _bug2CandidateTestBearing,
                    _bug2CandidateAttemptedDirX, _bug2CandidateAttemptedDirY,
                    sourceCellX, sourceCellY,
                    targetCellX, targetCellY);

                _bug2FailedMoveCount++;
                _bug2SameSideSteps++;
                _bug2CandidateIssued = false;
                SaveLocalMapIfDirty();

                // Check side switch
                CheckBug2SideSwitch();

                // Next tick will try next candidate
            }
            else
            {
                // Unknown action — don't mark, just abort this candidate
                _log($"[Bug2] Candidate unknown action={currentAction}. Aborting candidate.");
                _bug2CandidateIssued = false;
                _bug2SameSideSteps++;
                // Fall through — next tick will try again
            }
        }

        private void CheckBug2SideSwitch()
        {
            if (_bug2SameSideSteps >= BUG2_MAX_STEPS_BEFORE_SIDE_SWITCH)
            {
                _bug2FollowLeft = !_bug2FollowLeft;
                _bug2SameSideSteps = 0;
                _log($"[Bug2] Switching side {( _bug2FollowLeft ? "Right -> Left" : "Left -> Right" )}.");
                _log($"[Bug2] State=FollowBoundary Side={(_bug2FollowLeft ? "Left" : "Right")} Step={_bug2StepCount} Failed={_bug2FailedMoveCount}");
            }
            else
            {
                _log($"[Bug2] State=FollowBoundary Side={(_bug2FollowLeft ? "Left" : "Right")} Step={_bug2StepCount} Failed={_bug2FailedMoveCount}");
            }
        }
    }
}
