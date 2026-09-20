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

    public enum WaypointStuckRecoveryType
    {
        Default = 0,
        Repot = 1,
        Operation = 2,
        RecoveryPath = 3,
        AttackMob = 4
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
        public WaypointStuckRecoveryType StuckRecoveryType { get; set; }
        public string StuckRecoveryOperation { get; set; }
        public string StuckRecoveryPath { get; set; }
        public short StuckRecoveryMobCameraDistance { get; set; }
        /// <summary>
        /// Named bot operation (see <see cref="BotOperations"/>) executed once when
        /// this waypoint is reached, before the route continues. Empty = none.
        /// Saved per-point in the path file, so operations can live on the path itself
        /// instead of only as profile flow steps.
        /// </summary>
        public string OnArrivalOperation { get; set; }
        /// <summary>
        /// When true, this entry is a standalone method step, not a position: it runs
        /// in path order regardless of where the player currently is (no distance
        /// check), then the route continues. X/Y and movement fields are ignored.
        /// </summary>
        public bool IsOperationStep { get; set; }

        public Waypoint(
            float x,
            float y,
            MovementPrecision precision,
            BotMode mode,
            short cameraDistanceLock = DefaultCameraDistanceLock,
            short attackDisengageDistance = DefaultAttackDisengageDistance,
            ZoneRestriction zoneRestriction = ZoneRestriction.OutsideOnly,
            WaypointStuckRecoveryType stuckRecoveryType = WaypointStuckRecoveryType.Default,
            string stuckRecoveryOperation = "",
            string stuckRecoveryPath = "",
            short stuckRecoveryMobCameraDistance = DefaultCameraDistanceLock,
            string onArrivalOperation = "",
            bool isOperationStep = false)
        {
            X = x;
            Y = y;
            Precision = precision;
            Mode = mode;
            CameraDistanceLock = cameraDistanceLock;
            AttackDisengageDistance = attackDisengageDistance;
            ZoneRestriction = zoneRestriction;
            StuckRecoveryType = stuckRecoveryType;
            StuckRecoveryOperation = stuckRecoveryOperation;
            StuckRecoveryPath = stuckRecoveryPath;
            StuckRecoveryMobCameraDistance = stuckRecoveryMobCameraDistance;
            OnArrivalOperation = onArrivalOperation;
            IsOperationStep = isOperationStep;
        }
    }

    public class MovementSystem
    {
        private readonly GameMemoryService _memoryService;
        private readonly Action<string> _log;
        private readonly CombatHandler _combatHandler;
        private readonly RepotHelper _repotHelper;
        private readonly bool _enableWaypointSpecialRecoveries;

        public WaypointRecoveryExecutor? WaypointRecoveryExecutor { get; set; }

        public MovementPrecision GlobalPrecision { get; set; } = MovementPrecision.Medium;
        public bool LoopPath { get; set; } = false;

        /// <summary>If true (default), MovementSystem handles repot internally (legacy mode).
        /// When false, the external BotWorkflowCoordinator is responsible for repot decisions.</summary>
        public bool InternalRepotEnabled { get; set; } = true;

        private bool _isWaypointSpecialRecoveryActive;
        private bool _waypointRepotRequested;
        private WaypointMobRecovery? _waypointMobRecovery;
        private Waypoint? _activeSpecialRecoveryTarget;
        private bool _specialRecoveryAttempted;
        private (float X, float Y)? _specialRecoveryAnchor;
        private float _specialRecoveryTargetX;
        private float _specialRecoveryTargetY;

        public bool IsWaypointSpecialRecoveryActive =>
            _isWaypointSpecialRecoveryActive || _waypointMobRecovery?.IsActive == true;

        public bool IsWaypointRepotRequested => _waypointRepotRequested;

        /// <summary>Returns true when the final goal waypoint has been reached (non-loop path).</summary>
        public bool IsGoalReached => _goalReached;

        /// <summary>
        /// Returns true when a non-loop path has consumed all waypoints and entered
        /// final-waypoint standby. In workflow mode (InternalRepotEnabled=false) the
        /// goal flag is never set by the internal repot helper, so standby is the
        /// completion signal for non-loop routes.
        /// </summary>
        public bool IsFinalStandbyActive => _finalStandbyActive;

        /// <summary>
        /// True while movement is blocked because the player is in a zone the current
        /// waypoint does not allow (e.g. teleported to the city while the route expects
        /// the wilderness). Used by PathRunner to abort routes after a sustained block.
        /// </summary>
        public bool IsZoneBlocked => _zoneBlockedSince != DateTime.MinValue;

        /// <summary>How long the bot has been continuously zone-blocked (TimeSpan.Zero when not blocked).</summary>
        public TimeSpan ZoneBlockedDuration =>
            _zoneBlockedSince == DateTime.MinValue ? TimeSpan.Zero : DateTime.Now - _zoneBlockedSince;

        /// <summary>
        /// True when the in-city stuck escalation fired while running in workflow mode
        /// (InternalRepotEnabled=false). The route cannot complete — PathRunner aborts it so the
        /// coordinator can retry from the city instead of standing idle for 10 minutes.
        /// </summary>
        public bool IsCityStuckFatal => _isCityStuckFatal;

        /// <summary>
        /// True while a ReportAndGoBack teleport was requested (town scroll pressed).
        /// In workflow mode (InternalRepotEnabled=false) the old route + old unstuck are
        /// dead from this moment: PathRunner must abort the path so the coordinator can
        /// start Repot from scratch instead of continuing the stale Exp route in town.
        /// </summary>
        public bool IsReportAndGoBackRequested => _repotHelper.IsReportAndGoBackActive;

        // Obstacle
        private (float X, float Y) Waypoint2;

        // State
        private Queue<Waypoint> _waypoints = new Queue<Waypoint>();
        private List<Waypoint> _initialPath = new List<Waypoint>();
        private bool _isInitialized = false;
        private bool _goalReached = false;

        // ── Loop direction (ping-pong for open paths) ──
        // True = currently walking forward (index 0 → N-1).
        // False = currently walking back (index N-1 → 0).
        // Only used when LoopPath is true and the path is open
        // (end-to-start gap > LoopClosureMaxDistance). Closed loops keep
        // jumping straight from end to start.
        private bool _loopForward = true;

        // ── Final-waypoint standby ──
        // When the last waypoint is reached on a non-loop path, instead of stopping
        // completely the bot enters standby.  For Attack/AttackAndLoot modes it keeps
        // fighting/looting within the AtkDis boundary; for OnlyMove it just idles.
        private bool _finalStandbyActive = false;
        private float _finalStandbyX;
        private float _finalStandbyY;
        private BotMode _finalStandbyMode = BotMode.OnlyMove;
        private short _finalStandbyAtkDis = 0;

        // Initial route resync (first tick — find nearest segment)
        private bool _initialResyncDone = false;

        // Route resync after combat
        private bool _routeResyncPendingAfterCombat = false;

        // ── Post-combat loot wait ──
        // After a kill in MoveAndAttackAndLoot mode the bot holds movement until the
        // loot system (parallel task) completes a full scan pass with no confirmed
        // items. The wait only arms right after combat; during normal walking the
        // loot cycle runs in parallel without blocking movement.
        /// <summary>Last time combat was active (mob selected / combat action) — used to arm the loot wait.</summary>
        private DateTime _lastCombatSeenAt = DateTime.MinValue;
        /// <summary>True while the movement system is holding for the loot scan to finish.</summary>
        private bool _lootWaitActive = false;
        /// <summary>Safety deadline for the current loot wait session.</summary>
        private DateTime _lootWaitDeadline = DateTime.MinValue;

        /// <summary>
        /// Optional reference to the running LootSystem (set by the host — workflow
        /// coordinator or MainViewModel). Used to hold movement until a full loot scan
        /// pass completes after a kill. Null means no loot system is running.
        /// </summary>
        public LootSystem? LootSystemRef { get; set; }

        /// <summary>
        /// Loot-priority mode (profile flag "Loot Priority"): looting outranks combat
        /// and waypoint movement. While the loot system is in its pixel-scan state
        /// (scanning for, clicking and walking to ground items), ALL attack actions and
        /// movement toward the next waypoint are suspended — the character stands still
        /// and every item gets collected. Combat/movement resume when the loot machine
        /// reports a full empty scan pass (ScanComplete).
        /// </summary>
        public bool LootPriorityMode { get; set; } = false;

        /// <summary>
        /// Current effective movement mode (final-standby mode when in standby,
        /// otherwise the current waypoint's mode). Empty queue defaults to OnlyMove.
        /// Thread-safe for parallel loot-task reads (defaults to OnlyMove on race).
        /// </summary>
        public BotMode CurrentMode
        {
            get
            {
                try
                {
                    if (_finalStandbyActive)
                        return _finalStandbyMode;
                    if (_waypoints.Count > 0)
                        return _waypoints.Peek().Mode;
                }
                catch (InvalidOperationException)
                {
                    // Queue raced (peek on empty) from the parallel loot task — safest
                    // default is OnlyMove (suppress loot, keep moving).
                }
                return BotMode.OnlyMove;
            }
        }

        /// <summary>
        /// True when the current effective mode is OnlyMove. OnlyMove is mandatory
        /// above all movement-option actions: while true, movement must not attempt
        /// to loot or attack. Heal stays always on (it is not a movement action and
        /// runs in its own HealManaSystem task).
        /// </summary>
        public bool IsMoveOnlyActive => CurrentMode == BotMode.OnlyMove;

        /// <summary>True while the loot-priority hold is active (prevents log spam).</summary>
        private bool _lootPriorityHoldActive = false;

        /// <summary>Last time the loot-priority hold progress was logged (throttles the per-tick wait log to ~1/s).</summary>
        private DateTime _lootPriorityHoldLogLast = DateTime.MinValue;

        /// <summary>When the loot-priority post-kill hold started (UtcNow) — for the safety timeout. MinValue = not holding.</summary>
        private DateTime _lootPriorityHoldSince = DateTime.MinValue;

        /// <summary>True once the loot-priority hold was force-released by the safety timeout (until the loot phase ends).</summary>
        private bool _lootPriorityHoldTimedOut = false;

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

        /// <summary>Player position when the last reverse-diagonal recovery started.
        /// Used to decide whether the consecutive-stuck counter should reset (real progress).</summary>
        private (float X, float Y)? _lastStuckAttemptPos = null;

        // Position-based no-progress stuck detection (ghost-walk stuck spots): the walk
        // animation keeps playing while the player is actually pushing against an obstacle,
        // so the action byte never reads idle and the action-based detector cannot fire.
        // Baseline position + time while W is held; zero displacement for the timeout = stuck.
        private (float X, float Y)? _lastMoveProgressPos = null;
        private DateTime _lastMoveProgressTime = DateTime.MinValue;

        // Reposition-and-retry escalation (attack not consuming mana): after several
        // reposition cycles without real movement progress, the bot escalates to the
        // standard unstuck (reverse-diagonal recovery → ReportAndGoBack after repeated
        // failures) instead of cycling forever against a phantom target the player
        // cannot walk away from.
        private int _repositionRetryCount = 0;
        private (float X, float Y)? _lastRepositionPos = null;
        private const int REPOSITION_MAX_ATTEMPTS = 4;

        // When stuck in city triggers teleport, wait this long before resuming.
        private DateTime _inCityStuckCooldownUntil = DateTime.MinValue;

        /// <summary>Timestamp when the player first became zone-blocked (in a zone the current
        /// waypoint does not allow). MinValue = not blocked. Exposed to PathRunner so routes can
        /// be aborted when the player is stuck in the wrong zone (e.g. teleported to the city).</summary>
        private DateTime _zoneBlockedSince = DateTime.MinValue;

        /// <summary>True when the in-city stuck escalation fired while running in workflow mode
        /// (InternalRepotEnabled=false). The route cannot complete — PathRunner aborts it so the
        /// coordinator can retry from the city instead of pressing 6 and idling for 10 minutes.</summary>
        private bool _isCityStuckFatal = false;

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

        private int _tickCount = 0;
        private int _stateLogInterval = 5; // Log periodic state every N ticks

        private bool _isMovingForward = false;
        private bool _isSkillThreeHeld = false;
        private bool _attackSuppressedForCurrentWaypoint = false;
        /// <summary>
        /// One-shot flag for the speed-pot slot-reserve log: while red or white
        /// potions sit at the reserve (1), keys 7+8 are skipped and this suppresses
        /// the repeat log until drinking resumes.
        /// </summary>
        private bool _speedPotReserveLogged = false;
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

        public MovementSystem(
            GameMemoryService memoryService,
            Action<string> log,
            float targetX,
            float targetY,
            MovementPrecision precision = MovementPrecision.Medium,
            IEnumerable<Waypoint>? customPath = null,
            BotMode initialMode = BotMode.OnlyMove,
            bool loopPath = false,
            bool enableWaypointSpecialRecoveries = true,
            WaypointRecoveryExecutor? waypointRecoveryExecutor = null)
        {
            _memoryService = memoryService;
            _log = log;
            _combatHandler = new CombatHandler(log);
            _repotHelper = new RepotHelper(memoryService, log, StopMoving, () => _goalReached = true);
            _enableWaypointSpecialRecoveries = enableWaypointSpecialRecoveries;
            WaypointRecoveryExecutor = waypointRecoveryExecutor;
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
                    string methodTag = p.IsOperationStep ? $" METHOD:{p.OnArrivalOperation} (no position check)" : "";
                    _log($"[Path] #{index}: ({p.X:F1}, {p.Y:F1}) Precision:{p.Precision} Mode:{p.Mode} CamLock:{p.CameraDistanceLock} AtkDis:{p.AttackDisengageDistance}{methodTag}");
                    index++;
                }

                if (LoopPath && _initialPath.Count >= 2)
                {
                    float gap = LoopEndToStartDistance();
                    if (gap <= BotConstants.Movement.LoopClosureMaxDistance)
                    {
                        _log($"[Loop] Closed circular loop (end→start gap {gap:F1} <= {BotConstants.Movement.LoopClosureMaxDistance:F1}) — wrap jumps straight to start.");
                    }
                    else
                    {
                        _log($"[Loop] Open path (end→start gap {gap:F1} > {BotConstants.Movement.LoopClosureMaxDistance:F1}) — ping-pong mode: bot walks back along the recorded points instead of cutting straight to start.");
                    }
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

            // AttackMob is the only special recovery that remains tick-driven. External
            // operation/recovery-path actions await inside the confirmed-stuck dispatcher,
            // so this guard also prevents normal route/combat logic from competing with one.
            if (_waypointMobRecovery != null)
            {
                var (recoveryX, recoveryY, recoveryPositionRead) = _memoryService.GetPlayerPosition();
                TickWaypointMobRecovery(
                    recoveryPositionRead ? recoveryX : float.NaN,
                    recoveryPositionRead ? recoveryY : float.NaN);
                return;
            }

            if (_isWaypointSpecialRecoveryActive)
                return;

            // ── ReportAndGoBack teleport (workflow mode): full reset, hold movement ──
            // In workflow mode (InternalRepotEnabled=false) RepotHelper.EvaluateRepotTick
            // does nothing, so the flag must be checked directly. While the teleport is
            // pending the old route + old unstuck are dead: stop everything, discard
            // stuck counters / reverse-diagonal / progress baselines, and do NOT run
            // ActionStuck / NoProgress / MoveTowards on the stale waypoint. PathRunner
            // aborts the path via IsReportAndGoBackRequested so the coordinator starts
            // Repot from its first command. Legacy mode (InternalRepotEnabled=true) is
            // still handled by EvaluateRepotTick below (15s wait + goal reached).
            if (!InternalRepotEnabled && _repotHelper.IsReportAndGoBackActive)
            {
                if (_isMovingForward || _isSkillThreeHeld || _isUnstuckRoutineActive ||
                    _reverseDiagonalRecovery.IsActive || _consecutiveStuckAttempts != 0)
                {
                    ResetStuckAndUnstuckState("report-and-go-back-active");
                    _log($"[Tick {_tickCount}] ReportAndGoBack active — old route/unstuck discarded, movement held for coordinator Repot.");
                }
                if (_isMovingForward || _isSkillThreeHeld)
                {
                    StopMoving();
                    ReleaseSkillThree();
                }
                return;
            }

            // ── In-city stuck cooldown ──
            // After 3 stuck attempts in city, bot presses 6 and waits 10 minutes.
            if (DateTime.Now < _inCityStuckCooldownUntil)
            {
                if (_isMovingForward || _isSkillThreeHeld)
                {
                    StopMoving();
                    ReleaseSkillThree();
                }

                if (_tickCount % _stateLogInterval == 0)
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
                    // Log only on the transition into the blocked state, not every tick.
                    if (_zoneBlockedSince == DateTime.MinValue)
                    {
                        _zoneBlockedSince = DateTime.Now;
                        string hint = (inCity && currentRestriction == ZoneRestriction.OutsideOnly)
                            ? " (player likely teleported to the city)"
                            : "";
                        _log($"[Tick {_tickCount}] Zone blocked (inCity={inCity}, restriction={currentRestriction}, waypoints={_waypoints.Count}) — stopping movement{hint}.");
                    }

                    if (_isMovingForward || _isSkillThreeHeld)
                    {
                        StopMoving();
                        ReleaseSkillThree();
                        ClearCombatRetargetSearch();
                    }

                    return;
                }
            }

            // Not zone-blocked (or report-and-go-back active) — clear the block timer.
            _zoneBlockedSince = DateTime.MinValue;

            // ── Periodic state dump ──
            if (_tickCount % _stateLogInterval == 0)
            {
                string modeStr = "none";
                if (_waypoints.Count > 0) modeStr = _waypoints.Peek().Mode.ToString();
                var camAngle = _memoryService.GetCameraAngle();
                _log($"[State] T:{_tickCount} Q:{_waypoints.Count} M:{modeStr} Fwd:{_isMovingForward} Unst:{_isUnstuckRoutineActive} Goal:{_goalReached} Loop:{LoopPath} Cam:{camAngle} Act:{_memoryService.GetCurrentAction()}");
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

            // ── OnlyMove is mandatory above all movement-option actions ──
            // While the current waypoint (or final standby) is OnlyMove, the bot must
            // not try to loot or attack. Every loot hold below is skipped so waypoint
            // movement is never suspended for a pickup/scan. Heal stays always on —
            // it is not a movement action and runs in its own HealManaSystem task.
            bool moveOnlyActive = IsMoveOnlyActive;
            if (moveOnlyActive)
            {
                _lootPriorityHoldActive = false;
                _lootPriorityHoldSince = DateTime.MinValue;
                _lootPriorityHoldTimedOut = false;
                if (LootSystemRef != null && (LootSystemRef.IsCollecting || LootSystemRef.IsLootingActive))
                {
                    if (_tickCount % _stateLogInterval == 0)
                        _log($"[Tick {_tickCount}] OnlyMove active — ignoring loot (mandatory move, no loot/attack).");
                }
            }
            else
            {
            // ── Loot collection interrupts combat/movement ──
            // The loot system scans for ground items WHILE the character keeps attacking
            // (scanning alone never interrupts the attack). Only when the scan actually
            // found an item — mouseover confirmed, the loot system left-clicked and the
            // character auto-walks to it (IsCollecting) — are the attack and waypoint
            // movement suspended, so the pickup is not fought over. Combat and movement
            // resume as soon as the collection finishes.
            if (LootSystemRef != null && LootSystemRef.IsCollecting)
            {
                if (!_lootPriorityHoldActive)
                {
                    _lootPriorityHoldActive = true;
                    _combatHandler.ResetState();
                    _lootPriorityHoldLogLast = DateTime.UtcNow;
                    _log($"[Tick {_tickCount}] Loot item found — suspending combat while collecting (cycle={LootSystemRef.IsLootCycleActive}).");
                }
                ReleaseSkillThree();
                StopMoving();
                ClearCombatRetargetSearch();
                await Task.Delay(BotConstants.Delays.LootUpdateMs, token);
                return;
            }
            // NOTE: no _lootPriorityHoldActive reset here — the flag is shared with the
            // loot-priority post-kill hold below and resetting it every tick caused the
            // "waiting for the loot scan" line to spam once per tick. It is cleared in
            // the else branch when the loot phase actually ends.

            // ── Loot-priority post-kill hold ──
            // In loot priority mode, after a mob dies (mob selected → no target) the loot
            // machine loots until a full scan pass finds no more items (IsLootingActive).
            // During that phase the bot must NOT select the next target (no TAB / no
            // attack) and must NOT move to the next waypoint — loot is the priority:
            // targeting and movement wait until the loot scan is finished and no loot
            // is found.
            if (LootPriorityMode && LootSystemRef != null && LootSystemRef.IsLootingActive)
            {
                if (_lootPriorityHoldSince == DateTime.MinValue)
                {
                    _lootPriorityHoldSince = DateTime.UtcNow;
                }

                // The hold is released ONLY when the loot scan finished with no items
                // (IsLootingActive false) or when the loot machine is no longer
                // actively scanning/collecting (e.g. the loot task died) — while the
                // machine is mid-scan the bot must keep waiting, never interrupt it.
                bool scanning = LootSystemRef.IsLootCycleActive;
                bool holdTimedOut = !scanning &&
                    (DateTime.UtcNow - _lootPriorityHoldSince).TotalMilliseconds >= BotConstants.Delays.MaxLootWaitMs;

                if (!holdTimedOut)
                {
                    if (!_lootPriorityHoldActive)
                    {
                        _lootPriorityHoldActive = true;
                        _combatHandler.ResetState();
                        _lootPriorityHoldLogLast = DateTime.UtcNow;
                        _log($"[Tick {_tickCount}] Loot priority — hold START, waiting for loot scan (cycle={LootSystemRef.IsLootCycleActive}, scan={LootSystemRef.IsScanActive}).");
                    }
                    else if ((DateTime.UtcNow - _lootPriorityHoldLogLast).TotalSeconds >= 1.0)
                    {
                        _lootPriorityHoldLogLast = DateTime.UtcNow;
                        double holdMs = (DateTime.UtcNow - _lootPriorityHoldSince).TotalMilliseconds;
                        _log($"[Tick {_tickCount}] Loot priority — still waiting {holdMs:F0}ms (cycle={LootSystemRef.IsLootCycleActive}, scan={LootSystemRef.IsScanActive}, collecting={LootSystemRef.IsCollecting}).");
                    }
                    ReleaseSkillThree();
                    StopMoving();
                    ClearCombatRetargetSearch();
                    await Task.Delay(BotConstants.Delays.LootUpdateMs, token);
                    return;
                }

                if (!_lootPriorityHoldTimedOut)
                {
                    _lootPriorityHoldTimedOut = true;
                    _log($"[Tick {_tickCount}] Loot-priority hold timed out after {BotConstants.Delays.MaxLootWaitMs}ms with the loot machine idle — resuming combat/movement.");
                }
            }
            else
            {
                // Loot phase ended (or not loot priority) — reset the hold bookkeeping.
                _lootPriorityHoldSince = DateTime.MinValue;
                _lootPriorityHoldTimedOut = false;
                _lootPriorityHoldActive = false;
            }
            }

            // ── Map change detection ──
            // Each game map has its own navigation file. If the player changed maps,
            // save the current map's data and load the new map's data.
            int currentMapId = _memoryService.GetMapNumber();
            if (currentMapId != _currentMapId)
            {
                int oldMapId = _currentMapId;
                _log($"[Tick {_tickCount}] Map: {oldMapId} → {currentMapId}");
                _localNavigationMap.ChangeMap(currentMapId);
                _currentMapId = currentMapId;
                // Teleport / map change invalidates everything from the old map:
                // bearings, stuck positions and progress baselines. Discard them so the
                // old Exp unstuck never continues on the new (city) map.
                ResetStuckAndUnstuckState($"map-change {oldMapId}->{currentMapId}");
            }

            // ── Attack speed / potion check (keys 7+8 = red/white speed pots) ──
            // Runs in EVERY mode, including OnlyMove/city: the white pot gives run
            // speed, which is exactly what city walking needs. Previously this was
            // suppressed under OnlyMove, so the bot never rebuffed speed pots while
            // walking the city (repot/shop paths) even with the buff missing.
            // CheckAttackSpeed throttles itself to once per CheckIntervalSeconds.
            if (_combatHandler.CheckAttackSpeed(_memoryService))
            {
                // Slot reserve: the last red/white potion is never drunk — one of each
                // must stay so the inventory slots keep their places. The 7:red / 8:white
                // mapping is not pinned down anywhere in code, so when EITHER type is at
                // reserve both keys are skipped — a mapping-independent rule that can never
                // eat the last potion of the wrong type. Buff is sacrificed to save the slot.
                int redPots = _memoryService.GetRedPotionCount();
                int whitePots = _memoryService.GetWhitePotionCount();
                if (redPots <= BotConstants.Repot.PotionSlotReserve ||
                    whitePots <= BotConstants.Repot.PotionSlotReserve)
                {
                    if (!_speedPotReserveLogged)
                    {
                        _log($"[Tick {_tickCount}] Speed pot buff missing but potions at slot reserve (red={redPots}, white={whitePots}) — keeping last potions, skipping keys 7+8.");
                        _speedPotReserveLogged = true;
                    }
                }
                else
                {
                    _speedPotReserveLogged = false;
                    _log($"[Tick {_tickCount}] Speed pot buff missing (atkSpd={_combatHandler.LastAttackSpeed}, need!={BotConstants.SpeedPotion.AttackSpeedThreshold}) — using potions (keys 7+8)");
                    _log("[Key] 7 (pot1)");
                    GameInput.PressKey(GameInput.VK_7, GameInput.SCAN_7);
                    await Task.Delay(BotConstants.SpeedPotion.PostPotionDelayMs, token);
                    _log("[Key] 8 (pot2)");
                    GameInput.PressKey(GameInput.VK_8, GameInput.SCAN_8);
                }
            }

            var (currX, currY, success) = _memoryService.GetPlayerPosition();
            if (!success)
            {
                _log($"[Tick {_tickCount}] [Pos] Read failed");
                return;
            }

            // ── Initial waypoint optimisation (first tick only) ──
            // Instead of always targeting waypoint #1, find the closest route segment
            // or waypoint to the player's actual position and start from there.
            // This prevents the bot from going backwards when the player is already
            // near the end of the path.
            if (!_initialResyncDone && _waypoints.Count > 0 && _initialPath.Count >= 2)
            {
                _log($"[Tick {_tickCount}] Initial position=({currX:F1},{currY:F1}) — finding optimal starting waypoint...");
                var result = RouteResyncFromCurrentPosition(currX, currY, isInitialResync: true);
                _log($"[Tick {_tickCount}] Initial waypoint optimisation result={result} queue={_waypoints.Count}");
                _initialResyncDone = true;
            }
            else if (!_initialResyncDone)
            {
                // No path to optimise — mark as done anyway
                _initialResyncDone = true;
            }

            byte currentAction = _memoryService.GetCurrentAction();
            int attackStatus = _memoryService.GetAttackStatus();
            bool mobSelected = _memoryService.IsMobSelected();

            // Log position every 5 ticks (action bytes included — key for stuck diagnosis:
            // 25=idle, 27/3=running, 28=being hit, 39=attacking).
            if (_tickCount % 5 == 0)
            {
                _log($"[Tick {_tickCount}] @ ({currX:F1},{currY:F1}) Act:{currentAction} Mob:{mobSelected} AtkSt:{attackStatus} Cam:{_memoryService.GetCameraAngle()}");
            }

            BotMode currentMode = BotMode.OnlyMove;
            float manhattanDistanceToTarget = 0f;

            // ── Final-waypoint standby ──
            // When the path is complete, the bot stays at the final waypoint.
            // Attack modes keep fighting/looting within the AtkDis boundary.
            if (_finalStandbyActive)
            {
                _localNavigationMap.SaveIfDirty();

                if (_finalStandbyMode == BotMode.OnlyMove)
                {
                    if (_tickCount % _stateLogInterval == 0)
                        _log($"[Tick {_tickCount}] Standby (OnlyMove) @ ({_finalStandbyX:F1},{_finalStandbyY:F1})");
                    return;
                }

                // Attack mode: check AtkDis boundary. If outside → move back.
                float distFromFinal = GeometryUtils.Distance(currX, currY, _finalStandbyX, _finalStandbyY);
                if (distFromFinal > _finalStandbyAtkDis + 2f)
                {
                    _log($"[Standby] Outside AtkDis ({distFromFinal:F1} > {_finalStandbyAtkDis}) — returning to final waypoint.");
                    MoveTowards(currX, currY, _finalStandbyX, _finalStandbyY);
                    return;
                }

                // Inside boundary — run combat handler and return.
                // The loot system (separate task) handles looting independently,
                // so strip AndLoot from the mode to prevent the combat handler
                // from suppressing TAB (which would stop the bot from ever
                // initiating combat on its own in standby).
                currentMode = _finalStandbyMode == BotMode.MoveAndAttackAndLoot
                    ? BotMode.MoveAndAttack
                    : _finalStandbyMode;
                goto RUN_COMBAT_ONLY;
            }

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

                // While the loot system is mid-cycle (MoveAndAttackAndLoot), do NOT
                // override the camera — the pixel scan needs its zoomed view
                // (LootScanDistance) to detect ground items. Without this the movement
                // reverts the camera every tick and the live scan misses everything that
                // the "Test Loot" scan (which runs without movement) detects. The
                // movement restores its own camera distance once the loot cycle finishes.
                bool lootScanning = currentMode == BotMode.MoveAndAttackAndLoot &&
                                    LootSystemRef != null &&
                                    LootSystemRef.IsLootCycleActive;
                if (!lootScanning)
                {
                    _memoryService.SetCameraDistance(cameraDistanceToApply);
                    _memoryService.SetCameraVerticalLock(BotConstants.Camera.DefaultVerticalLock);
                }

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
                        HoldSkillThree();

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
        RUN_COMBAT_ONLY:
            var combatAction = _combatHandler.EvaluateCombatAction(_memoryService, currentMode, _isUnstuckRoutineActive, currX, currY);
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
                    HoldSkillThree();
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

                case CombatAction.Unstuck:
                    // The selected mob is unreachable/not dying (combat watchdog fired).
                    // Perform the STANDARD unstuck action so the player really walks away
                    // from the stuck spot. Combat is suppressed while the recovery is
                    // active (_isUnstuckRoutineActive), so TAB/attack cannot interrupt it.
                    StartCombatUnstuck(currX, currY);
                    return;

                case CombatAction.RepositionAndRetry:
                    // The attack animation plays but mana is not consumed (attack not
                    // connecting — phantom/unreachable target, mob HP never drops).
                    // Walk toward the next waypoint for a short time, then TAB and
                    // attack the (re-selected) target again.
                    await RepositionAndRetryAttack(currX, currY, token);
                    return;

                case CombatAction.PotionsUsed:
                    ReleaseSkillThree();
                    _log($"[Tick {_tickCount}] Potions used — brief delay.");
                    await Task.Delay(BotConstants.Delays.PotionsUsedMs, token);
                    return;
            }

            // Combat is over — release skill 3 if held
            ReleaseSkillThree();

            // ── Post-combat loot wait ──
            // In MoveAndAttackAndLoot mode the bot must NOT walk away while the loot
            // system (running in a parallel task) is still collecting. Wait until a full
            // scan pass completes with no confirmed items (LootSystem.IsLootCycleActive
            // becomes false) or until a safety timeout. The wait only arms right after
            // combat — during normal walking the loot cycle runs without blocking.
            // Skipped entirely while the unstuck routine is active — the recovery runs
            // uninterrupted in the movement section below.
            if (currentMode == BotMode.MoveAndAttackAndLoot && !_isUnstuckRoutineActive)
            {
                bool isCombatStillActive = combatAction == CombatAction.Attack ||
                                           combatAction == CombatAction.CombatWait ||
                                           combatAction == CombatAction.TabTarget ||
                                           combatAction == CombatAction.PotionsUsed;
                bool mobSelectedNow = _memoryService.IsMobSelected();

                if (isCombatStillActive || mobSelectedNow)
                {
                    // Combat active — remember it so the loot wait can arm when it ends,
                    // and drop any wait session in progress (it re-arms after this combat).
                    _lastCombatSeenAt = DateTime.UtcNow;
                    _lootWaitActive = false;
                    _lootWaitDeadline = DateTime.MinValue;
                }
                else
                {
                    // No combat, no mob selected — possibly waiting for loot after a kill.
                    bool lootDone = LootSystemRef == null || !LootSystemRef.IsLootCycleActive;

                    if (lootDone)
                    {
                        // Full scan passed with no confirmed items (or no loot system) —
                        // free to move on.
                        _lootWaitActive = false;
                        _lootWaitDeadline = DateTime.MinValue;
                    }
                    else if (_lootWaitActive)
                    {
                        // Wait session in progress — hold movement until the loot scan
                        // completes or the safety deadline expires.
                        if (DateTime.UtcNow < _lootWaitDeadline)
                        {
                            await Task.Delay(BotConstants.Delays.LootUpdateMs, token);
                            return;
                        }

                        // The deadline expired — but if the loot system is still actively
                        // collecting (an item was picked up recently), extend the wait
                        // instead of walking away with drops still on the ground.
                        if (LootSystemRef != null &&
                            LootSystemRef.TimeSinceLastItemCollected <
                            TimeSpan.FromMilliseconds(BotConstants.Delays.LootWaitProgressGraceMs))
                        {
                            _lootWaitDeadline = DateTime.UtcNow.AddMilliseconds(BotConstants.Delays.MaxLootWaitMs);
                            _log($"[LootWait] Loot still collecting items — extending wait ({BotConstants.Delays.MaxLootWaitMs}ms).");
                            await Task.Delay(BotConstants.Delays.LootUpdateMs, token);
                            return;
                        }

                        _log("[LootWait] Loot wait timeout — moving on.");
                        _lootWaitActive = false;
                        _lootWaitDeadline = DateTime.MinValue;
                    }
                    else if ((DateTime.UtcNow - _lastCombatSeenAt).TotalMilliseconds <=
                             BotConstants.Delays.LootWaitArmWindowMs)
                    {
                        // Combat just ended and the loot system is still collecting —
                        // start waiting so every item gets picked up before the bot
                        // walks to the next waypoint.
                        _lootWaitActive = true;
                        _lootWaitDeadline = DateTime.UtcNow.AddMilliseconds(BotConstants.Delays.MaxLootWaitMs);
                        _log($"[LootWait] Waiting for loot scan to finish (up to {BotConstants.Delays.MaxLootWaitMs}ms)...");
                        await Task.Delay(BotConstants.Delays.LootUpdateMs, token);
                        return;
                    }
                    else
                    {
                        // Normal walking (no recent combat) — the loot cycle runs in
                        // parallel, do not block movement.
                        _lootWaitActive = false;
                        _lootWaitDeadline = DateTime.MinValue;
                    }
                }
            }
            else
            {
                // Not in loot mode — no loot wait.
                _lootWaitActive = false;
                _lootWaitDeadline = DateTime.MinValue;
            }

            // If in standby mode, don't fall through to waypoint queue logic.
            if (_finalStandbyActive)
            {
                if (_tickCount % _stateLogInterval == 0)
                    _log($"[Tick {_tickCount}] Standby (combat idle) @ ({_finalStandbyX:F1},{_finalStandbyY:F1})");
                return;
            }

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
                    if (await CheckFinalGoalSoftCompletion(currX, currY, activeTarget, distToTarget, token))
                    {
                        return;
                    }
                }

                await AdvanceReachedWaypoints(currX, currY, token);

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

                if (_specialRecoveryAttempted &&
                    (Math.Abs(target.X - _specialRecoveryTargetX) > 0.01f ||
                     Math.Abs(target.Y - _specialRecoveryTargetY) > 0.01f))
                {
                    ResetSpecialRecoveryAttempt();
                }
                else if (_specialRecoveryAttempted && _specialRecoveryAnchor.HasValue &&
                         GeometryUtils.Distance(currX, currY, _specialRecoveryAnchor.Value.X, _specialRecoveryAnchor.Value.Y) >=
                         BotConstants.Movement.StuckProgressResetDistance)
                {
                    _log("[WaypointRecovery] Special recovery guard reset after real movement progress.");
                    ResetSpecialRecoveryAttempt();
                }

                // Reset stuck counter only when the player has actually moved away from the
                // position where the last stuck attempt began (real progress). Proximity to
                // the target alone must NOT reset it — otherwise a bot stuck right next to its
                // waypoint loops the reverse-diagonal recovery forever without escalating.
                if (_consecutiveStuckAttempts > 0 && _lastStuckAttemptPos.HasValue)
                {
                    float movedSinceStuck = GeometryUtils.Distance(currX, currY, _lastStuckAttemptPos.Value.X, _lastStuckAttemptPos.Value.Y);
                    if (movedSinceStuck > BotConstants.Movement.StuckProgressResetDistance)
                    {
                        _consecutiveStuckAttempts = 0;
                        _lastStuckAttemptPos = null;
                        _log($"[Unstuck] Reset counter — moved {movedSinceStuck:F1} units since last stuck attempt.");
                    }
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
                            _isUnstuckRoutineActive = false;
                            _log($"[ReverseDiagonal] recovered.");
                            ResetActionStuckTracking();
                            return;
                        case RecoveryResult.Failed:
                            _isUnstuckRoutineActive = false;
                            _log($"[ReverseDiagonal] failed - all attempts exhausted.");
                            _log($"[ActionStuck] Action={currentAction} while moving. Stuck confirmed by action.");
                            MarkObstacleFromActionStuck(currX, currY, target);
                            _localNavigationMap.SaveIfDirty();
                            return;
                    }
                }

                // Action-stuck detection is ignored during the start grace period so a
                // freshly pressed W key never triggers a false stuck recovery.
                if (DateTime.Now >= _ignoreStuckUntil &&
                    _stuckDetector.IsActionStuck(currX, currY, target, _isMovingForward))
                {
                    _log($"[ActionStuck] Action={currentAction} while moving. Starting ReverseDiagonalRecovery.");
                    await HandleConfirmedNavigationStuckAsync(currX, currY, target, token, "ActionStuck");
                    return;
                }

                // ── Position-based no-progress stuck detection ──
                // Some stuck spots keep the walk animation playing (action byte stays
                // "running") while the player is actually pushing against an obstacle and
                // standing still — the action-based detector above can never fire there.
                // Track real displacement while W is held; zero progress for the timeout
                // is treated as stuck, regardless of what the animation claims.
                if (_isMovingForward && !_reverseDiagonalRecovery.IsActive &&
                    DateTime.Now >= _ignoreStuckUntil &&
                    distNow > thresholdNow + BotConstants.Movement.NoProgressNearTargetExtra)
                {
                    if (_lastMoveProgressPos.HasValue && _lastMoveProgressTime != DateTime.MinValue)
                    {
                        float movedSince = GeometryUtils.Distance(currX, currY, _lastMoveProgressPos.Value.X, _lastMoveProgressPos.Value.Y);
                        if (movedSince < BotConstants.Movement.NoProgressMinDistance)
                        {
                            if ((DateTime.Now - _lastMoveProgressTime).TotalMilliseconds >= BotConstants.Movement.NoProgressTimeoutMs)
                            {
                                _log($"[NoProgress] Moved {movedSince:F2} units in {BotConstants.Movement.NoProgressTimeoutMs} ms while W held (action={currentAction}) — treating as stuck.");
                                _lastMoveProgressPos = null;
                                _lastMoveProgressTime = DateTime.MinValue;
                                await HandleConfirmedNavigationStuckAsync(currX, currY, target, token, "NoProgress");
                                return;
                            }
                        }
                        else
                        {
                            // Real progress — restart the measurement window.
                            _lastMoveProgressPos = (currX, currY);
                            _lastMoveProgressTime = DateTime.Now;
                        }
                    }
                    else
                    {
                        _lastMoveProgressPos = (currX, currY);
                        _lastMoveProgressTime = DateTime.Now;
                    }
                }
                else
                {
                    // Not moving forward / near target / grace period / recovery active —
                    // no progress baseline.
                    _lastMoveProgressPos = null;
                    _lastMoveProgressTime = DateTime.MinValue;
                }

                // Log route info periodically
                if (_tickCount % _stateLogInterval == 0)
                {
                    string ghostFlag = IsGhostWaypoint(target) ? " [GHOST]" : "";
                    float brgToWp = GeometryUtils.GetBearingToTargetDeg(currX, currY, target.X, target.Y);
                    _log($"[Route] T:{_tickCount} WP{ghostFlag}({target.X:F1},{target.Y:F1}) d:{distNow:F2} th:{thresholdNow:F2} Brg:{brgToWp:F1}deg Act:{currentAction} Mob:{mobSelected} M:{target.Mode} P:{target.Precision} Cam:{_memoryService.GetCameraAngle()}");
                }

                // Track healthy movement bearing for escape direction
                if (!_isUnstuckRoutineActive && StuckDetector.IsActionRunning(currentAction))
                {
                    _lastHealthyMoveBearingDeg = GeometryUtils.GetBearingToTargetDeg(currX, currY, target.X, target.Y);
                    _lastHealthyMovePos = (currX, currY);
                    _lastHealthyMoveTime = DateTime.Now;
                }

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
        private async Task<bool> CheckFinalGoalSoftCompletion(float currX, float currY, Waypoint target, float distToTarget, CancellationToken token)
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
                await AdvanceReachedWaypoints(currX, currY, token);
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

        private async Task<bool> AdvanceReachedWaypoints(float currX, float currY, CancellationToken token)
        {
            bool advanced = false;
            int wpIndex = 0;

            while (_waypoints.Count > 0)
            {
                var target = _waypoints.Peek();
                wpIndex++;

                bool isGhost = IsGhostWaypoint(target);
                string ghostTag = isGhost ? " [GHOST]" : "";

                // Standalone method steps run in path order regardless of where the
                // player currently is — no distance check, X/Y are ignored.
                // Positional waypoints must actually be reached first.
                if (!target.IsOperationStep)
                {
                    float dist = GeometryUtils.Distance(currX, currY, target.X, target.Y);
                    float threshold = GetEffectiveWaypointReachThreshold(target);

                    if (dist > threshold)
                    {
                        break;
                    }
                }

                _waypoints.Dequeue();
                if (target.IsOperationStep)
                    _log($"[AdvWp] #{wpIndex} method-step '{target.OnArrivalOperation}'{ghostTag} → run (no position check) | Queue: {_waypoints.Count}");
                else
                    _log($"[AdvWp] #{wpIndex} ({target.X:F1},{target.Y:F1}) reached{ghostTag} ✓ | Queue: {_waypoints.Count}");

                // Per-waypoint arrival operation: a named bot operation stored on the
                // path point itself (e.g. EtanaRepotUnstuck). Runs once on arrival
                // (or unconditionally for method steps), before the route continues.
                // Ghost duplicates never trigger it.
                if (!isGhost && !string.IsNullOrWhiteSpace(target.OnArrivalOperation))
                {
                    await RunArrivalOperationAsync(target, token);
                }
                else if (target.IsOperationStep && !isGhost)
                {
                    _log($"[AdvWp] WARNING: method-step #{wpIndex} has NO method assigned — nothing to run (pick one in Path Editor, On Arrival column).");
                }

                ResetSpecialRecoveryAttempt();
                _consecutiveStuckAttempts = 0; // reset stuck counter — we made progress
                _lastStuckAttemptPos = null;
                ResetBearingState();
                ResetActionStuckTracking();
                ResetCombatStateForWaypointChange();

                advanced = true;

                if (_waypoints.Count == 0)
                {
                    // Save the last waypoint's info BEFORE calling HandleEmpty,
                    // so standby mode knows its position, mode and AtkDis.
                    _finalStandbyX = target.X;
                    _finalStandbyY = target.Y;
                    _finalStandbyMode = target.Mode;
                    _finalStandbyAtkDis = target.AttackDisengageDistance;
                    _finalStandbyActive = true;

                    HandleEmptyWaypointQueueAfterAdvance();
                    if (_goalReached)
                        return true;
                }
            }

            return advanced;
        }

        /// <summary>
        /// Runs a waypoint's <see cref="Waypoint.OnArrivalOperation"/> through the
        /// waypoint recovery executor (same operation runner as stuck-recovery and
        /// profile flow steps). Movement is stopped first so the operation owns the
        /// inputs; tracking state is reset afterwards because the operation may have
        /// moved the player (e.g. a 2 s unstuck walk). A failed operation only logs —
        /// the route continues. Cancellation propagates to the caller.
        /// </summary>
        private async Task RunArrivalOperationAsync(Waypoint target, CancellationToken token)
        {
            string opName = target.OnArrivalOperation;
            if (WaypointRecoveryExecutor == null)
            {
                _log($"[OnArrival] Waypoint ({target.X:F1},{target.Y:F1}) wants operation '{opName}' but no recovery executor is configured — skipping.");
                return;
            }

            _log($"[OnArrival] Waypoint ({target.X:F1},{target.Y:F1}) — running operation '{opName}'.");
            StopMoving();
            ReleaseSkillThree();
            ClearCombatRetargetSearch();
            _combatHandler.ResetState();

            bool succeeded = await WaypointRecoveryExecutor.RunOperationAsync(opName, token);
            _log(succeeded
                ? $"[OnArrival] Operation '{opName}' completed — resuming route."
                : $"[OnArrival] Operation '{opName}' failed — resuming route anyway.");

            StopMoving();
            ReleaseSkillThree();
            ResetBearingState();
            ResetActionStuckTracking();
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
                // Open paths must NOT jump straight from end to start — that leg was
                // never recorded and usually cuts through walls (e.g. 113 units in the
                // reported log). Walk back along the recorded points instead.
                if (!IsClosedLoop() && _initialPath.Count >= 2)
                {
                    if (_loopForward)
                    {
                        // Just finished the forward leg (at index N-1) — go back.
                        _loopForward = false;
                        for (int i = _initialPath.Count - 2; i >= 0; i--)
                        {
                            _waypoints.Enqueue(_initialPath[i]);
                        }

                        var next = _waypoints.Peek();
                        _log($"[WpQueue] End of open path reached. Ping-pong: walking BACK ({_waypoints.Count} waypoints, next=({next.X:F1},{next.Y:F1})).");
                    }
                    else
                    {
                        // Just finished the backward leg (at index 0) — go forward.
                        _loopForward = true;
                        for (int i = 1; i < _initialPath.Count; i++)
                        {
                            _waypoints.Enqueue(_initialPath[i]);
                        }

                        var next = _waypoints.Peek();
                        _log($"[WpQueue] Start of open path reached. Ping-pong: walking FORWARD ({_waypoints.Count} waypoints, next=({next.X:F1},{next.Y:F1})).");
                    }

                    // Reset standby flag — we are looping, not staying at the final waypoint.
                    _finalStandbyActive = false;

                    ResetSpecialRecoveryAttempt();
                    ResetBearingState();
                    ResetActionStuckTracking();
                    ResetCombatStateForWaypointChange();
                    return;
                }

                _log($"[WpQueue] End of path reached. Looping back to start ({_initialPath.Count} waypoints).");

                foreach (var p in _initialPath)
                {
                    _waypoints.Enqueue(p);
                }

                _log($"[WpQueue] Re-enqueued {_initialPath.Count} waypoints. Queue now has {_waypoints.Count} entries.");

                // Reset standby flag — we are looping, not staying at the final waypoint.
                _finalStandbyActive = false;

                ResetSpecialRecoveryAttempt();
                ResetBearingState();
                ResetActionStuckTracking();
                ResetCombatStateForWaypointChange();
            }
            else
            {
                // Don't stop — enter standby at the final waypoint for continued
                // combat/loot operations within the AtkDis boundary.
                // The last waypoint info was saved in _finalStandby fields.
                StopMoving();
                _log("[WpQueue] Final waypoint reached — entering standby mode.");
                _log($"[WpQueue] Standby: mode={_finalStandbyMode}, pos=({_finalStandbyX:F1},{_finalStandbyY:F1}), AtkDis={_finalStandbyAtkDis}");
            }
        }

        /// <summary>
        /// Straight-line distance between the first and the last waypoint of the
        /// recorded path. Used to decide between a closed circular loop and an open
        /// ping-pong (there-and-back) loop.
        /// </summary>
        private float LoopEndToStartDistance()
        {
            if (_initialPath.Count < 2) return 0f;
            var first = _initialPath[0];
            var last = _initialPath[_initialPath.Count - 1];
            return GeometryUtils.Distance(first.X, first.Y, last.X, last.Y);
        }

        /// <summary>
        /// True when the recorded loop is closed (end near start), so jumping
        /// straight from end to start is safe. False for open paths, where the
        /// bot must walk back along the recorded points.
        /// </summary>
        private bool IsClosedLoop()
        {
            if (_initialPath.Count < 2) return true;
            return LoopEndToStartDistance() <= BotConstants.Movement.LoopClosureMaxDistance;
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
            if (ShouldUpdateCamera(cameraRadians, isFreshSegment, out _))
            {
                _log($"[Camera] Apply brg={bearingDeg:F1}deg target={cameraRadians:F4}rad last={_cameraLastAppliedAngle:F4}rad diff={CircularGameAngleDiff(cameraRadians, _cameraLastAppliedAngle):F4}");
                _memoryService.SetCameraAngle(cameraRadians);
                _cameraLastAppliedAngle = cameraRadians;
                _lastCameraUpdateTime = DateTime.Now;
                _hasCameraHysteresisCandidate = false;
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

            if (ShouldUpdateCamera(cameraRadians, isFreshSegment, out _))
            {
                _log($"[Camera] Apply brg={bearingDeg:F1}deg target={cameraRadians:F4}rad last={_cameraLastAppliedAngle:F4}rad diff={CircularGameAngleDiff(cameraRadians, _cameraLastAppliedAngle):F4} (no W)");
                _memoryService.SetCameraAngle(cameraRadians);
                _cameraLastAppliedAngle = cameraRadians;
                _lastCameraUpdateTime = DateTime.Now;
                _hasCameraHysteresisCandidate = false;
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

        /// <summary>
        /// Full reset of everything tied to the previous route/position: reverse-diagonal
        /// recovery, consecutive-stuck counter, stuck/progress baselines, reposition
        /// escalation, pending resync and combat state. Used on ReportAndGoBack teleport
        /// and on map change, where bearings / stuck positions from the old map are invalid
        /// and continuing them in town is the bug (old Exp unstuck running on Repot map).
        /// </summary>
        private void ResetStuckAndUnstuckState(string reason)
        {
            _reverseDiagonalRecovery.Stop();
            _isUnstuckRoutineActive = false;
            _consecutiveStuckAttempts = 0;
            _lastStuckAttemptPos = null;
            ResetActionStuckTracking();
            _lastMoveProgressPos = null;
            _lastMoveProgressTime = DateTime.MinValue;
            _repositionRetryCount = 0;
            _lastRepositionPos = null;
            _routeResyncPendingAfterCombat = false;
            _attackSuppressedForCurrentWaypoint = false;
            _combatHandler.ResetState();
            ClearCombatRetargetSearch();
            ReleaseSkillThree();
            StopMoving();
            _log($"[Unstuck] Full reset ({reason}) — old route/unstuck discarded.");
        }

        private void ResetCombatStateForWaypointChange()
        {
            _attackSuppressedForCurrentWaypoint = false;
            _combatHandler.ResetState();
            ClearCombatRetargetSearch();
            ReleaseSkillThree();
        }

        private void ResetSpecialRecoveryAttempt()
        {
            _specialRecoveryAttempted = false;
            _specialRecoveryAnchor = null;
            _specialRecoveryTargetX = 0f;
            _specialRecoveryTargetY = 0f;
        }

        private void PrepareSpecialRecovery(Waypoint target)
        {
            _activeSpecialRecoveryTarget = target;
            _isWaypointSpecialRecoveryActive = true;
            _isUnstuckRoutineActive = true;
            StopMoving();
            ReleaseSkillThree();
            ClearCombatRetargetSearch();
            _combatHandler.ResetState();
            _lastMoveProgressPos = null;
            _lastMoveProgressTime = DateTime.MinValue;
            ResetActionStuckTracking();
        }

        private void FinishSpecialRecovery(Waypoint target, bool succeeded)
        {
            _waypointMobRecovery?.Stop();
            _waypointMobRecovery = null;
            StopMoving();
            ReleaseSkillThree();
            ClearCombatRetargetSearch();
            _combatHandler.ResetState();
            ResetActionStuckTracking();
            _lastMoveProgressPos = null;
            _lastMoveProgressTime = DateTime.MinValue;
            _memoryService.SetCameraDistance(target.CameraDistanceLock);
            _memoryService.SetCameraVerticalLock(BotConstants.Camera.DefaultVerticalLock);
            if (succeeded)
            {
                ResetBearingState();
                _routeResyncPendingAfterCombat = true;
            }

            _activeSpecialRecoveryTarget = null;
            _isWaypointSpecialRecoveryActive = false;
            _isUnstuckRoutineActive = false;
        }

        private async Task HandleConfirmedNavigationStuckAsync(
            float currX,
            float currY,
            Waypoint target,
            CancellationToken token,
            string source)
        {
            // This branch deliberately remains the direct legacy path. Do not put
            // special-recovery preparation before it: Default must have the same
            // escalation/counter/reverse-diagonal behavior as current master.
            if (!_enableWaypointSpecialRecoveries ||
                target.StuckRecoveryType == WaypointStuckRecoveryType.Default)
            {
                StartReverseDiagonalRecovery(currX, currY, target);
                return;
            }

            if (_specialRecoveryAttempted)
            {
                _log("[WaypointRecovery] Special recovery already attempted at this stuck location with no real progress — using standard unstuck.");
                StartReverseDiagonalRecovery(currX, currY, target);
                return;
            }

            _specialRecoveryAttempted = true;
            _specialRecoveryAnchor = (currX, currY);
            _specialRecoveryTargetX = target.X;
            _specialRecoveryTargetY = target.Y;
            _log($"[WaypointRecovery] Stuck at waypoint ({target.X:F1},{target.Y:F1}), configured={target.StuckRecoveryType}, source={source}.");

            if (target.StuckRecoveryType == WaypointStuckRecoveryType.Repot)
            {
                PrepareSpecialRecovery(target);
                if (InternalRepotEnabled)
                {
                    _log("[WaypointRecovery] Repot requested by waypoint (manual mode).");
                    _repotHelper.ReportAndGoBack();
                    FinishSpecialRecovery(target, succeeded: false);
                }
                else
                {
                    _log("[WaypointRecovery] Repot requested by waypoint.");
                    _waypointRepotRequested = true;
                    FinishSpecialRecovery(target, succeeded: false);
                }
                return;
            }

            if (target.StuckRecoveryType == WaypointStuckRecoveryType.AttackMob)
            {
                PrepareSpecialRecovery(target);
                _waypointMobRecovery = new WaypointMobRecovery(
                    _memoryService,
                    _log,
                    StopMoving,
                    HoldSkillThree,
                    ReleaseSkillThree);
                _waypointMobRecovery.Start(currX, currY, target.StuckRecoveryMobCameraDistance);
                return;
            }

            PrepareSpecialRecovery(target);
            bool succeeded = false;
            try
            {
                if (WaypointRecoveryExecutor != null)
                {
                    succeeded = target.StuckRecoveryType == WaypointStuckRecoveryType.Operation
                        ? await WaypointRecoveryExecutor.RunOperationAsync(target.StuckRecoveryOperation, token)
                        : await WaypointRecoveryExecutor.RunRecoveryPathAsync(target.StuckRecoveryPath, token);
                }
                else
                {
                    _log("[WaypointRecovery] No recovery executor is configured.");
                }
            }
            catch (OperationCanceledException)
            {
                FinishSpecialRecovery(target, succeeded: false);
                throw;
            }
            catch (Exception ex)
            {
                _log($"[WaypointRecovery] Special recovery threw: {ex.Message}");
            }

            if (succeeded)
            {
                _log($"[WaypointRecovery] {target.StuckRecoveryType} succeeded — resuming original route.");
                FinishSpecialRecovery(target, succeeded: true);
                return;
            }

            _log("[WaypointRecovery] Special recovery failed — falling back to standard unstuck.");
            FinishSpecialRecovery(target, succeeded: false);
            StartReverseDiagonalRecovery(currX, currY, target);
        }

        private void TickWaypointMobRecovery(float currX, float currY)
        {
            if (_waypointMobRecovery == null)
                return;

            WaypointMobRecoveryResult result = _waypointMobRecovery.Tick(currX, currY);
            if (result == WaypointMobRecoveryResult.InProgress)
                return;

            Waypoint target = _activeSpecialRecoveryTarget ??
                (_waypoints.Count > 0
                    ? _waypoints.Peek()
                    : new Waypoint(Waypoint2.X, Waypoint2.Y, GlobalPrecision, BotMode.OnlyMove));

            _waypointMobRecovery = null;
            if (result == WaypointMobRecoveryResult.Recovered)
            {
                FinishSpecialRecovery(target, succeeded: true);
                return;
            }

            _log("[WaypointRecovery] AttackMob timed out — falling back to standard unstuck.");
            FinishSpecialRecovery(target, succeeded: false);
            StartReverseDiagonalRecovery(currX, currY, target);
        }

        public void CancelWaypointSpecialRecovery()
        {
            if (_waypointMobRecovery != null)
            {
                _waypointMobRecovery.Stop();
                _waypointMobRecovery = null;
            }

            if (_isWaypointSpecialRecoveryActive && _activeSpecialRecoveryTarget.HasValue)
            {
                FinishSpecialRecovery(_activeSpecialRecoveryTarget.Value, succeeded: false);
            }
        }

        /// <summary>
        /// Starts ReverseDiagonalRecovery and applies the initial camera bearing + W key.
        /// </summary>
        private void StartReverseDiagonalRecovery(float currX, float currY, Waypoint target)
        {
            _consecutiveStuckAttempts++;
            _lastStuckAttemptPos = (currX, currY);
            bool inCity = _memoryService.GetIsInCity();
            int maxAttempts = inCity ? STUCK_MAX_ATTEMPTS_IN_CITY : STUCK_MAX_ATTEMPTS_OUTSIDE;
            _log($"[Unstuck] Consecutive stuck attempts: {_consecutiveStuckAttempts}/{maxAttempts} ({(inCity ? "in city" : "outside")})");

            if (_consecutiveStuckAttempts >= maxAttempts)
            {
                _consecutiveStuckAttempts = 0;
                _lastStuckAttemptPos = null;
                CaptureStuckScreenshot();
                _reverseDiagonalRecovery.Stop();
                _isUnstuckRoutineActive = false;

                if (inCity)
                {
                    if (InternalRepotEnabled)
                    {
                        // Legacy mode: no external repot flow — press the town teleport and
                        // wait before retrying.
                        _log($"[Unstuck] Reached {maxAttempts} stuck attempts in city — pressing 6 and waiting 10 minutes.");
                        StopMoving();
                        GameInput.PressKey(GameInput.VK_6, GameInput.SCAN_6);
                        _inCityStuckCooldownUntil = DateTime.Now.AddMinutes(10);
                    }
                    else
                    {
                        // Workflow mode: the coordinator handles repot, so do NOT press the
                        // teleport key / idle for 10 minutes here. Signal a fatal city-stuck
                        // state so PathRunner aborts the route and the coordinator retries
                        // (and eventually fails visibly instead of standing silently).
                        _log($"[Unstuck] Reached {maxAttempts} stuck attempts in city (workflow) — aborting path so the coordinator can retry from the city.");
                        _isCityStuckFatal = true;
                        StopMoving();
                    }
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
            ApplyCameraBearing(_reverseDiagonalRecovery.CurrentBearingDeg); // camera only — Recovery controls W

            // Mark the unstuck routine as active: while it runs, the combat handler
            // returns None (no TAB / no attack) and the route resync is skipped, so the
            // recovery is never interrupted and the player really walks away from the
            // stuck spot.
            _isUnstuckRoutineActive = true;
        }

        /// <summary>
        /// Standard unstuck for the combat case: an unreachable / not-dying mob triggers
        /// the same reverse-diagonal recovery as a movement stuck, so the player really
        /// walks away from the spot instead of just TABbing. The recovery runs
        /// uninterrupted (combat is suppressed via <see cref="_isUnstuckRoutineActive"/>).
        /// </summary>
        private void StartCombatUnstuck(float currX, float currY)
        {
            ReleaseSkillThree();
            Waypoint target = _waypoints.Count > 0
                ? _waypoints.Peek()
                : new Waypoint(Waypoint2.X, Waypoint2.Y, GlobalPrecision, BotMode.OnlyMove);
            _log("[Combat] Unreachable mob — starting standard unstuck (reverse-diagonal recovery).");
            StartReverseDiagonalRecovery(currX, currY, target);
        }

        /// <summary>
        /// Attack-not-connecting recovery (see <see cref="CombatAction.RepositionAndRetry"/>):
        /// the attack animation plays but mana is not consumed, so the attack is not
        /// connecting (phantom/unreachable target — the mob's HP never drops). Release the
        /// skill, walk toward the next waypoint for a short time to physically reposition,
        /// then TAB and let the combat handler re-acquire/attack the target on the next tick
        /// (skill 3 is pressed again as soon as a mob is selected).
        /// </summary>
        private async Task RepositionAndRetryAttack(float currX, float currY, CancellationToken token)
        {
            ReleaseSkillThree();
            ClearCombatRetargetSearch();

            // ── Escalation ──
            // Count consecutive reposition cycles. Real movement progress (the player
            // actually walked away from the broken spot) resets the counter; repeated
            // cycles with zero progress mean the player cannot leave (geometry block +
            // phantom target re-selected by TAB) — escalate to the standard unstuck so
            // the existing escalation chain (reverse-diagonal → ReportAndGoBack) applies.
            if (_lastRepositionPos.HasValue)
            {
                float moved = GeometryUtils.Distance(currX, currY, _lastRepositionPos.Value.X, _lastRepositionPos.Value.Y);
                if (moved > BotConstants.Movement.StuckProgressResetDistance)
                {
                    _repositionRetryCount = 0;
                }
            }
            else
            {
                _lastRepositionPos = (currX, currY);
            }

            _repositionRetryCount++;
            if (_repositionRetryCount >= REPOSITION_MAX_ATTEMPTS)
            {
                _repositionRetryCount = 0;
                _lastRepositionPos = null;
                _log($"[Combat] {REPOSITION_MAX_ATTEMPTS} reposition attempts without escaping the broken target — starting standard unstuck.");
                StartCombatUnstuck(currX, currY);
                return;
            }

            Waypoint target = _waypoints.Count > 0
                ? _waypoints.Peek()
                : new Waypoint(Waypoint2.X, Waypoint2.Y, GlobalPrecision, BotMode.OnlyMove);

            int walkMs = BotConstants.Combat.CombatRepositionDurationMs;
            _log($"[Combat] Attack not consuming mana — repositioning: walk toward waypoint ({target.X:F1},{target.Y:F1}) for {walkMs}ms, then TAB + attack.");

            // Force an immediate camera update to the waypoint direction (fresh segment
            // bypasses the camera filter) and walk forward for the reposition duration.
            ResetBearingState();
            ApplyCameraBearing(GeometryUtils.GetBearingToTargetDeg(currX, currY, target.X, target.Y));
            StartMoving();
            try
            {
                await Task.Delay(walkMs, token);
            }
            finally
            {
                StopMoving();
            }

            // Fresh combat state for the retry: the next tick re-baselines the mana
            // stuck detection and presses 3 when a target is selected.
            _combatHandler.ResetState();

            _log("[Combat] Retrying attack — TAB, then attack if a target is selected.");
            GameInput.PressKey(GameInput.VK_TAB, GameInput.SCAN_TAB);
            await Task.Delay(BotConstants.Delays.PreTabWaitMs, token);
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
        /// Captures ALL screens (full virtual screen across every monitor) as a PNG file
        /// in Screenshots/Stuck/ with the current date/time.
        /// Called before triggering ReportAndGoBack after too many consecutive stuck attempts.
        /// </summary>
        private void CaptureStuckScreenshot()
        {
            try
            {
                var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;

                using (Bitmap bitmap = new Bitmap(virtualScreen.Width, virtualScreen.Height))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(virtualScreen.X, virtualScreen.Y, 0, 0, bitmap.Size);

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
                // Log only on a real transition — repeated StopMoving calls (multiple
                // callers per tick) used to flood the log with identical W-up lines.
                if (_isMovingForward)
                {
                    _isMovingForward = false;
                    _stopMoveCount++;
                    _log($"[Input] W up (StopMoving) — call #{_stopMoveCount}. Tick:{_tickCount}");
                }

                GameInput.keybd_event(GameInput.VK_W, GameInput.SCAN_W, (uint)GameInput.KEYEVENTF_KEYUP, 0);
            }

            // Defensive cleanup: release A/D in case they were pressed by a previous version
            GameInput.keybd_event(GameInput.VK_A, GameInput.SCAN_A, (uint)GameInput.KEYEVENTF_KEYUP, 0);
            GameInput.keybd_event(GameInput.VK_D, GameInput.SCAN_D, (uint)GameInput.KEYEVENTF_KEYUP, 0);
        }

        // ── Skill 3 management ──

        /// <summary>
        /// True while the combat attack key (skill 3) is logically held down.
        /// Exposed so hosts and tests can verify cleanup through the existing
        /// ownership state instead of duplicating key-state tracking.
        /// </summary>
        public bool IsAttackKeyHeld => _isSkillThreeHeld;

        /// <summary>
        /// Releases combat-owned held keys (currently the attack skill 3).
        /// Idempotent: calling it when no combat key is held sends nothing and
        /// leaves the next combat cycle unaffected.
        /// Must be called before another workflow phase takes control (e.g. the
        /// EXP → Repot handoff) so a combat key held at interruption time can
        /// never leak across the phase boundary. Uses the existing
        /// <see cref="_isSkillThreeHeld"/> ownership flag and the existing
        /// "[Key] 3 up" release logging — no second tracking, no per-tick key-ups.
        /// </summary>
        public void ReleaseCombatKeys()
        {
            ReleaseSkillThree();
        }

        private void HoldSkillThree()
        {
            if (_isSkillThreeHeld)
                return;

            _log("[Key] 3 hold (attack skill)");
            GameInput.keybd_event(GameInput.VK_3, GameInput.SCAN_3, 0, 0);
            _isSkillThreeHeld = true;
        }

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

            // Method steps have no meaningful position — only positional waypoints
            // participate. Returns -1 when there is no positional waypoint at all
            // (the rebuild then reports TerminalSkip and the queue is left alone).
            int bestIdx = -1;
            float bestDist = float.MaxValue;
            for (int i = 0; i < _initialPath.Count; i++)
            {
                if (_initialPath[i].IsOperationStep)
                    continue;
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
        /// Rebuilds <see cref="_waypoints"/> from <paramref name="startIndex"/> onwards.
        /// For forward legs (or non-loop / closed loops) the queue runs to the end of
        /// <see cref="_initialPath"/>; for the backward leg of an open ping-pong loop it
        /// runs back down to index 0. Does nothing if the new target is effectively the
        /// same as the current queue peek (within 0.5 distance). Clears relevant state on change.
        /// </summary>
        /// <returns>
        /// <see cref="RouteResyncResult.TerminalSkip"/> if <paramref name="startIndex"/> is invalid;
        /// <see cref="RouteResyncResult.SameTarget"/> if the new target equals the current queue peek;
        /// <see cref="RouteResyncResult.Applied"/> if the queue was rebuilt.
        /// </returns>
        private RouteResyncResult RebuildWaypointQueueFromIndex(int startIndex, Waypoint oldTarget, bool preserveLeadingMethodSteps = false)
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

            bool goBackward = LoopPath && !IsClosedLoop() && !_loopForward;

            int oldCount = _waypoints.Count;
            _waypoints.Clear();

            if (goBackward)
            {
                for (int i = startIndex; i >= 0; i--)
                {
                    _waypoints.Enqueue(_initialPath[i]);
                }

                _log($"[RouteResync] Queue rebuilt BACKWARD: oldCount={oldCount} newCount={_waypoints.Count} startIndex={startIndex}");
            }
            else
            {
                // Leading method steps (index < startIndex) never ran yet on the
                // initial resync — keep them at the head so they execute in path
                // order with no position check. On later resyncs they already ran.
                if (preserveLeadingMethodSteps)
                {
                    int preserved = 0;
                    for (int i = 0; i < startIndex; i++)
                    {
                        if (_initialPath[i].IsOperationStep)
                        {
                            _waypoints.Enqueue(_initialPath[i]);
                            preserved++;
                        }
                    }
                    if (preserved > 0)
                        _log($"[RouteResync] Preserved {preserved} leading method-step(s) before index {startIndex}.");
                }

                for (int i = startIndex; i < _initialPath.Count; i++)
                {
                    _waypoints.Enqueue(_initialPath[i]);
                }

                _log($"[RouteResync] Queue rebuilt: oldCount={oldCount} newCount={_waypoints.Count} startIndex={startIndex}");
            }

            // Reset movement state since the target changed
            ResetBearingState();
            ResetActionStuckTracking();
            ResetCombatStateForWaypointChange();
            ResetSpecialRecoveryAttempt();
            _consecutiveStuckAttempts = 0;
            _lastStuckAttemptPos = null;

            return RouteResyncResult.Applied;
        }

        /// <summary>
        /// Called after combat ends. Synchronizes the movement queue with the player's
        /// actual world position by finding the nearest route segment and updating the
        /// next target accordingly.
        /// </summary>
        /// <returns>RouteResyncResult indicating whether the queue was changed or why it was skipped.</returns>
        private RouteResyncResult RouteResyncFromCurrentPosition(float currX, float currY, bool isInitialResync = false)
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
            // The wrap segment (last → first) only exists for CLOSED loops. For open
            // ping-pong loops it would cut straight through unrecorded terrain.
            bool useWrapSegment = LoopPath && IsClosedLoop();
            int segmentCount = normalSegmentCount + (useWrapSegment ? 1 : 0);

            for (int i = 0; i < segmentCount; i++)
            {
                int idxA = i;
                int idxB = i + 1;

                // Handle wrap-around segment for closed loop paths: last waypoint -> first waypoint
                if (i >= normalSegmentCount)
                {
                    if (useWrapSegment)
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

                // Method steps have no meaningful position — resync geometry is
                // purely positional, so segments touching one are skipped.
                if (a.IsOperationStep || b.IsOperationStep)
                    continue;

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
                    int segmentEndIdx = (useWrapSegment && idxA == _initialPath.Count - 1) ? 0 : idxA + 1;
                    int bestEndIdx = (useWrapSegment && bestSegmentStart == _initialPath.Count - 1) ? 0 : bestSegmentStart + 1;

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
            // Open ping-pong loops keep the current travel direction: the forward leg
            // targets increasing indices, the backward leg targets decreasing ones.
            string reason;
            int nextWpIndex;

            float maxSegDist = BotConstants.Movement.RouteResyncMaxSegmentDistance;
            bool openLoopBackward = LoopPath && !IsClosedLoop() && !_loopForward;

            if (bestDist > maxSegDist)
            {
                // Fallback: player is far from all segments — go to nearest waypoint.
                // RebuildWaypointQueueFromIndex applies the current leg direction.
                nextWpIndex = FindNearestWaypointIndex(currX, currY);
                reason = $"fallback-nearest-wp d={bestDist:F2} dir={(openLoopBackward ? "backward" : "forward")}";
            }
            else if (openLoopBackward)
            {
                // Backward leg: segment i->i+1 is travelled B→A (decreasing index).
                // t≈0 means almost at A — skip one more backwards.
                float nearStartT = 1f - BotConstants.Movement.RouteResyncVeryCloseToSegmentEndT;
                if (bestT <= nearStartT)
                {
                    nextWpIndex = bestSegmentStart - 1;
                    if (nextWpIndex < 0) nextWpIndex = 0;
                    reason = "near-start-of-segment-backward";
                }
                else
                {
                    nextWpIndex = bestSegmentStart;
                    reason = "nearest-segment-backward";
                }
            }
            else if (bestT >= BotConstants.Movement.RouteResyncVeryCloseToSegmentEndT)
            {
                // Very close to the end of this segment — skip to the next segment
                if (useWrapSegment && bestSegmentStart == _initialPath.Count - 1)
                {
                    // Wrap segment: last->first. End = 0, next after end = 1.
                    nextWpIndex = 1;
                }
                else if (useWrapSegment && bestSegmentStart == _initialPath.Count - 2)
                {
                    // Last normal segment before wrap (N-2 -> N-1). Next after end wraps to 0.
                    nextWpIndex = 0;
                }
                else
                {
                    nextWpIndex = bestSegmentStart + 2;
                }

                if ((!LoopPath || !useWrapSegment) && nextWpIndex >= _initialPath.Count)
                    nextWpIndex = _initialPath.Count - 1;

                reason = "near-end-of-segment";
            }
            else
            {
                // In the middle or near the start — go to end of this segment
                if (useWrapSegment && bestSegmentStart == _initialPath.Count - 1)
                {
                    // Wrap segment: last->first. End = 0.
                    nextWpIndex = 0;
                }
                else
                {
                    nextWpIndex = bestSegmentStart + 1;
                }

                if ((!LoopPath || !useWrapSegment) && nextWpIndex >= _initialPath.Count)
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

            return RebuildWaypointQueueFromIndex(nextWpIndex, oldTarget, preserveLeadingMethodSteps: isInitialResync);
        }

        // ========================================================================
        //  SOFT SKIP
        // ========================================================================

    }
}
