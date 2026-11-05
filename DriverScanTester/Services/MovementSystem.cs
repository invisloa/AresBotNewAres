using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Models;
using DriverScanTester.Utils;

namespace DriverScanTester.Services
{
    public enum MovementPrecision
    {
        Exact = 0,
        Accurate = 2,
        Medium = 12,
        High = 20
    }

    public enum MovementSteeringMode
    {
        KeyboardTurn,
        DirectCamera
    }

    /// <summary>
    /// Pure KeyboardTurn steering decision from the live signed bearing error.
    /// Left/Right mirror the existing sign convention (negative shortest error = turn
    /// left with A, positive = turn right with D); Fast* additionally needs the cursor
    /// at the matching game-client edge for the game's natural x2 turn.
    /// </summary>
    internal enum KeyboardTurnDirective
    {
        None,
        NormalLeft,
        NormalRight,
        FastLeft,
        FastRight
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
        /// <summary>
        /// Per-waypoint approach steering override: KeyboardTurn = WAD keys (A/D turn while
        /// W drives), DirectCamera = write the camera angle directly. Null = use the
        /// MovementSystem's configured default mode (KeyboardTurn / WAD for normal routes,
        /// DirectCamera for auxiliary recovery routes).
        /// </summary>
        public MovementSteeringMode? SteeringMode { get; set; }

        /// <summary>
        /// Optional horizontal camera yaw (left/right camera) written once at the start
        /// of this waypoint's leg, while W is released and before the first W-down. The
        /// value is the exact 32-bit float bit pattern at +0x1A8, so a camera angle
        /// captured from the game round-trips without precision loss. Null = leave the
        /// camera untouched.
        /// </summary>
        public int? StartCameraAngleBits { get; set; }

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

        /// <summary>
        /// True when the owning path/profile disables all automatic loot. Carried on every
        /// generated waypoint so the effective policy is available for the active waypoint
        /// (including final-waypoint standby) and survives path advancement/loops.
        /// </summary>
        public bool DisableAllLoot { get; set; }

        /// <summary>
        /// Path-level policy: Alliance (green) faction markers are ignored while this path
        /// runs. Carried on every generated waypoint like <see cref="DisableAllLoot"/>, so the
        /// effective policy is available for the active waypoint (including final-waypoint
        /// standby) and survives path advancement/loops.
        /// </summary>
        public bool IgnoreAlliancePlayers { get; set; }

        /// <summary>
        /// Path-level policy: Empire (yellow) faction markers are ignored while this path
        /// runs. Carried on every generated waypoint like <see cref="DisableAllLoot"/>.
        /// </summary>
        public bool IgnoreEmpirePlayers { get; set; }

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
            bool isOperationStep = false,
            MovementSteeringMode? steeringMode = null,
            bool disableAllLoot = false,
            bool ignoreAlliancePlayers = false,
            bool ignoreEmpirePlayers = false,
            int? startCameraAngleBits = null)
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
            SteeringMode = steeringMode;
            DisableAllLoot = disableAllLoot;
            IgnoreAlliancePlayers = ignoreAlliancePlayers;
            IgnoreEmpirePlayers = ignoreEmpirePlayers;
            StartCameraAngleBits = startCameraAngleBits;
        }
    }

    /// <summary>
    /// How one combat reposition attempt ended. The reposition and its retry TAB are only
    /// allowed to continue for <see cref="Moved"/>; every other outcome is a failure/abort
    /// that must leave the combat baselines and the target selection untouched.
    /// </summary>
    internal enum CombatRepositionResult
    {
        /// <summary>
        /// Real final displacement was observed AND the walk ended closer to the waypoint —
        /// the retry TAB/attack may proceed.
        /// </summary>
        Moved,

        /// <summary>Forward input was accepted but the final position did not move enough.</summary>
        InsufficientDisplacement,

        /// <summary>
        /// The character did move, but not toward the waypoint: the final waypoint distance
        /// increased (walked away) or improved by less than
        /// <see cref="BotConstants.ReverseDiagonal.SuccessDistImprove"/> (walked sideways).
        /// </summary>
        NoWaypointProgress,

        /// <summary>The forward key-down was never accepted (a navigation hold or other owner blocks it).</summary>
        InputDenied,

        /// <summary>The caller cancelled the operation — input was released and nothing succeeded.</summary>
        Cancelled,

        /// <summary>Combat-recovery authorization was lost mid-attempt (target/focus/request change).</summary>
        AuthorizationLost
    }

    /// <summary>
    /// Why one combat reposition movement loop stopped. Logged only — it never decides success
    /// on its own: reaching the waypoint still requires a verified physical displacement and a
    /// positive result relative to the waypoint.
    /// </summary>
    internal enum CombatRepositionTermination
    {
        /// <summary>The waypoint's existing reach threshold was reached — W is released immediately.</summary>
        WaypointReached,

        /// <summary>The maximum movement budget expired before the waypoint was reached.</summary>
        DurationLimit,

        /// <summary>The forward key-down was never accepted.</summary>
        InputDenied,

        /// <summary>The caller cancelled the operation.</summary>
        Cancelled,

        /// <summary>Combat-recovery authorization was lost mid-attempt.</summary>
        AuthorizationLost,

        /// <summary>The position could not be read when the movement loop ended.</summary>
        PositionUnreadable
    }

    public class MovementSystem
    {
        private readonly GameMemoryService _memoryService;
        private readonly Action<string> _log;
        private readonly CombatHandler _combatHandler;
        private readonly CombatTargetSafety _targetSafety;
        private readonly RepotHelper _repotHelper;
        private readonly bool _enableWaypointSpecialRecoveries;
        private readonly MovementSteeringMode _steeringMode;
        private readonly MobMarkerDetector _mobMarkerDetector;
        private readonly MobGroupingController _mobGroupingController;
        private readonly object _mobGroupingLock = new();
        private DateTime _nextMobGroupingDetectionAt = DateTime.MinValue;
        private MobGroupAnalysis? _lastMobGroupingAnalysis;
        private bool _lastMobGroupingCaptureSucceeded;
        /// <summary>Camera yaw at the moment of the last mob-grouping detection sample.</summary>
        private float _lastMobGroupingCameraYawRadians;
        /// <summary>False when the camera yaw could not be read; the trend comparison is then skipped.</summary>
        private bool _lastMobGroupingHasCameraYaw;
        /// <summary>Client size of the last mob-grouping detection sample (trend validity).</summary>
        private int _lastMobGroupingClientWidth;
        private int _lastMobGroupingClientHeight;
        private bool _mobGroupingOwnsMovement;
        private bool _resumeCombatSkillAfterGrouping;
        private bool _mobGroupingDisposed;

        public WaypointRecoveryExecutor? WaypointRecoveryExecutor { get; set; }
        /// <summary>Set by the host so grouping cannot start while its shared pause switch is active.</summary>
        public Func<bool>? MobGroupingPausePredicate { get; set; }

        public MobGroupingState CurrentMobGroupingState => _mobGroupingController.State;

        /// <summary>
        /// Central target classifier/authorizer used for every attack decision. Exposed for
        /// regression tests so the other-player evidence source can be replaced by a fixture
        /// (the production instance scans the real game client).
        /// </summary>
        internal CombatTargetSafety TargetSafety => _targetSafety;
        public string MobGroupingStatus => !MobGroupingRuntimeSettings.Enabled
            ? "Off"
            : CurrentMobGroupingState switch
            {
                MobGroupingState.Idle => "Waiting for combat",
                MobGroupingState.Observing => "Observing",
                MobGroupingState.GroupingMove => "Moving",
                MobGroupingState.Settling => "Settling",
                MobGroupingState.Verify => "Verifying",
                MobGroupingState.ResumeCombat => "Resuming combat",
                MobGroupingState.Cooldown => "Cooldown",
                _ => "Idle"
            };

        public MovementPrecision GlobalPrecision { get; set; } = MovementPrecision.Medium;
        public bool LoopPath { get; set; } = false;

        /// <summary>If true (default), MovementSystem handles repot internally (legacy mode).
        /// When false, the external BotWorkflowCoordinator is responsible for repot decisions.</summary>
        public bool InternalRepotEnabled { get; set; } = true;

        /// <summary>
        /// If true (default), the production speed-potion rebuff (keys 7+8) runs in every
        /// mode exactly as before. The movement-synchronization diagnostic sets this to
        /// false for TEST 9 so its isolated OnlyMove experiment cannot emit unrelated
        /// potion input. Production callers never change it.
        /// </summary>
        public bool AllowSpeedPotionBuff { get; set; } = true;

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

        /// <summary>True when repeated navigation failures require the workflow coordinator to return to town.</summary>
        public bool IsNavigationFailureEscalationRequested => _navigationFailureEscalationRequested;

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
        private MovementSteeringMode? _finalStandbySteeringMode;
        private short _finalStandbyAtkDis = 0;
        /// <summary>Last route waypoint that entered standby; reused for gather reach-threshold semantics.</summary>
        private Waypoint _finalStandbyWaypoint;

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

        // ── Mandatory MoveAndAttackAndLoot navigation barrier ──
        // Explicit lifecycle contract with LootSystem. Navigation is released only by
        // a camera-verified Completed request for the exact armed generation; inactive
        // loot, timeouts, cancellations and failures never release it.
        private readonly LootNavigationBarrier _lootBarrier = new();
        private volatile bool _lootNavigationBarrierHoldsInput;
        private bool _lootBarrierBlockedLogged;
        private bool _lootBarrierFailureLogged;
        private bool _lootTargetCycleSuppressedLogged;
        private bool _lootMoveSuppressedLogged;

        // ── Combat-owned physical recovery authorization ──
        // A Deferred post-kill obligation keeps ordinary waypoint navigation and input
        // blocked, but a combat-owned recovery (RepositionAndRetry / combat unstuck) must
        // be able to turn the camera and hold W to physically escape a phantom or
        // unreachable target — otherwise every recovery input is suppressed while combat
        // keeps TABbing into the same target. The authorization is scoped to one exact
        // Deferred PostKill request generation and is revalidated before every authorized
        // input. Ordinary waypoint navigation never sets it.
        private bool _combatRecoveryMovementAuthorized;
        private long _combatRecoveryMovementRequestId;
        private bool _combatRecoveryMovementDeniedLogged;

        // ── CombatGate waypoint return recovery ──
        // CombatGate sets _attackSuppressedForCurrentWaypoint once the player is outside the
        // current waypoint's AttackDisengageDistance. Without the deferred-post-kill return,
        // the loot obligation blocks ordinary navigation while the CombatGate latch suppresses
        // attacks. The target is released before this return takes the existing
        // combat-recovery handoff, so the deferred mandatory scan can resume afterward
        // (2026-11-04 02:06 deadlock). This is a narrowly scoped, tick-driven return to the CURRENT
        // waypoint that reuses the existing combat-recovery handoff. It never advances the
        // waypoint, never completes the loot obligation and is cancelled by every lifecycle
        // reset (stop, map change, teleport, waypoint replacement, route failure).
        private bool _combatGateReturnActive;
        private bool _combatGateReturnHandoffReserved;
        private long _combatGateReturnRequestId;
        private int _combatGateReturnTargetId;
        private float _combatGateReturnWaypointX;
        private float _combatGateReturnWaypointY;
        private float _combatGateReturnStartX;
        private float _combatGateReturnStartY;
        private float _combatGateReturnLastX;
        private float _combatGateReturnLastY;
        private DateTime _combatGateReturnStartedAt = DateTime.MinValue;
        private DateTime _combatGateReturnLastProgressAt = DateTime.MinValue;
        private DateTime _combatGateReturnLastProgressLogAt = DateTime.MinValue;
        private float _combatGateReturnBestDistance = float.MaxValue;
        private int _combatGateReturnFailedAttempts;
        private int _combatGateReturnInputDenials;
        private bool _combatGateReturnWAcceptedLogged;
        private bool _combatGateReturnInputDeniedLogged;
        private DateTime _combatGateReturnDeadlockSince = DateTime.MinValue;
        private string _combatGateReturnDeadlockDetail = string.Empty;
        private bool _combatGateReturnDeadlockEscalated;

        // A target selected when CombatGate disengages is released once and kept out of
        // combat until the route has advanced beyond that waypoint and 15 seconds have elapsed.
        private const int CombatGateTargetReleaseMaximumClicks = 2;
        private const int CombatGateTargetReleaseRetryDelayMs = 200;
        private const int PostKillTargetReleaseMaximumClicks = 2;
        private const int PostKillTargetReleaseRetryDelayMs = 200;
        private const int CombatGateTargetSuppressionSeconds = 15;
        private int _combatGateSuppressedTargetId;
        private Waypoint _combatGateSuppressedWaypoint;
        private DateTime _combatGateTargetDisengagedAtUtc = DateTime.MinValue;
        private bool _combatGateSuppressionWaypointReached;
        private bool _combatGateSuppressionRouteProgressed;
        private bool _combatGateSuppressedTargetTabAttempted;
        private bool _combatGateSuppressedTabSuppressionLogged;
        private bool _combatGateSuppressionSelectionHandled;
        private bool _combatGateTargetReleasePending;
        private int _combatGateTargetReleaseTargetId;
        private int _combatGateTargetReleaseClickCount;
        private DateTime _combatGateTargetReleaseRetryAtUtc = DateTime.MinValue;
        private bool _combatGateTargetReleaseWaitLogged;
        private bool _combatGateTargetReleaseVerified;

        // ── Post-kill SOD/SOP pink-scan hold (MoveAndAttack) ──
        // After a kill, first TAB at the closest camera distance. A nearby mob takes
        // priority; only when that check finds none does the loot system run its pink
        // scan, holding combat/movement until the drop check finishes.
        /// <summary>True while the pink-scan hold is active (prevents log spam).</summary>
        private bool _pinkScanHoldActive = false;
        /// <summary>When the current pink-scan hold started (UtcNow) — safety timeout.</summary>
        private DateTime _pinkScanHoldSince = DateTime.MinValue;
        /// <summary>True once the current pink-scan hold was force-released by the safety timeout.</summary>
        private bool _pinkScanHoldTimedOut = false;

        /// <summary>
        /// Optional reference to the running LootSystem (set by the host — workflow
        /// coordinator or MainViewModel). Used to hold movement until a full loot scan
        /// pass completes after a kill. Null means no loot system is running.
        /// </summary>
        public LootSystem? LootSystemRef { get; set; }

        /// <summary>
        /// Profile-level override for the current run. When true this MovementSystem
        /// performs no automatic loot work regardless of the per-waypoint path flag.
        /// Set by the host before the first movement update; it is never written back
        /// into saved path data.
        /// </summary>
        public bool DisableAllLootOverride { get; set; }

        /// <summary>
        /// Profile-level override for the current run: when true, Alliance (green) player
        /// markers are ignored regardless of the per-waypoint path flag. Set by the host from
        /// the active profile; never written back into saved path data.
        /// </summary>
        public bool IgnoreAlliancePlayersOverride { get; set; }

        /// <summary>
        /// Profile-level override for the current run: when true, Empire (yellow) player
        /// markers are ignored regardless of the per-waypoint path flag.
        /// </summary>
        public bool IgnoreEmpirePlayersOverride { get; set; }

        /// <summary>
        /// Effective automatic-loot policy for the active waypoint/profile. True when the
        /// profile override is set or the current waypoint (including final-waypoint
        /// standby) carries the path-level disable flag. Movement, combat, targeting and
        /// recovery are unaffected; only automatic loot is suppressed.
        /// </summary>
        public bool IsAutomaticLootDisabled =>
            DisableAllLootOverride || CurrentWaypointDisableAllLoot;

        /// <summary>
        /// Path-level flag of the effective waypoint. Standby uses the final waypoint that
        /// entered standby, so the policy can never become stale after the queue empties.
        /// </summary>
        private bool CurrentWaypointDisableAllLoot
        {
            get
            {
                try
                {
                    if (_finalStandbyActive)
                        return _finalStandbyWaypoint.DisableAllLoot;
                    if (_waypoints.Count > 0)
                        return _waypoints.Peek().DisableAllLoot;
                }
                catch (InvalidOperationException)
                {
                    // Queue raced from a parallel reader — safest default is "no loot".
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Effective other-player ignore policy for the active waypoint combined with the
        /// global runtime options. Any enabled source ignores that faction, so a path saved
        /// with "Ignore Alliance players" keeps working regardless of the bot-window toggle.
        /// Only the STOP/attack-block behavior is affected; player-id targets stay blocked.
        /// </summary>
        internal bool ShouldIgnoreOtherPlayerMarker(PlayerMarkerCandidate candidate)
            => OtherPlayerIgnoreSettings.ShouldIgnore(
                candidate.Faction,
                OtherPlayerIgnoreSettings.IgnoreAlliancePlayers ||
                    IgnoreAlliancePlayersOverride ||
                    CurrentWaypointIgnoresAlliancePlayers,
                OtherPlayerIgnoreSettings.IgnoreEmpirePlayers ||
                    IgnoreEmpirePlayersOverride ||
                    CurrentWaypointIgnoresEmpirePlayers);

        private bool CurrentWaypointIgnoresAlliancePlayers
            => CurrentWaypointPlayerIgnorePolicy(static waypoint => waypoint.IgnoreAlliancePlayers);

        private bool CurrentWaypointIgnoresEmpirePlayers
            => CurrentWaypointPlayerIgnorePolicy(static waypoint => waypoint.IgnoreEmpirePlayers);

        /// <summary>
        /// Path-level flag of the effective waypoint (same standby-aware lookup as the loot
        /// policy), so a per-path option can never become stale after the queue empties.
        /// </summary>
        private bool CurrentWaypointPlayerIgnorePolicy(Func<Waypoint, bool> selector)
        {
            try
            {
                if (_finalStandbyActive)
                    return selector(_finalStandbyWaypoint);
                if (_waypoints.Count > 0)
                    return selector(_waypoints.Peek());
            }
            catch (InvalidOperationException)
            {
                // Queue raced from a parallel reader — safest default is "not ignored".
            }
            return false;
        }

        /// <summary>
        /// True while the mandatory MoveAndAttackAndLoot loot obligation still blocks
        /// ordinary waypoint navigation. False only after a verified Completed request
        /// (or when no obligation is armed, or loot is disabled by policy).
        /// </summary>
        public bool IsLootNavigationBlocked =>
            !IsAutomaticLootDisabled && _lootBarrier.IsBlockingNavigation;

        /// <summary>Barrier state exposed for regression tests and host diagnostics.</summary>
        internal LootNavigationBarrier LootBarrier => _lootBarrier;

        /// <summary>Pure mode gate: mandatory loot applies only to MoveAndAttackAndLoot.</summary>
        internal static bool RequiresMandatoryLootBarrier(BotMode mode)
            => mode == BotMode.MoveAndAttackAndLoot;

        /// <summary>
        /// True while the mandatory MoveAndAttackAndLoot obligation still owns movement
        /// (Pending/Running/Deferred, or a terminal failure that keeps navigation blocked).
        /// Mob grouping treats this as authoritative loot ownership: a Deferred request
        /// with no active collection still forbids grouping movement.
        /// </summary>
        internal bool IsMandatoryLootBarrierActive =>
            !IsAutomaticLootDisabled &&
            RequiresMandatoryLootBarrier(CurrentMode) && _lootBarrier.IsBlockingNavigation;

        /// <summary>
        /// Authoritative "loot owns movement/camera" condition for mob grouping: an active
        /// collection/pink scan, loot-priority looting, or the mandatory navigation barrier.
        /// Disabled loot never blocks grouping.
        /// </summary>
        internal bool IsMobGroupingLootHoldActive =>
            !IsAutomaticLootDisabled &&
            (IsMandatoryLootBarrierActive ||
             (LootSystemRef != null &&
              (LootSystemRef.IsCollecting || LootSystemRef.IsPinkScanPendingOrActive ||
               (LootPriorityMode && LootSystemRef.IsLootingActive))));

        // ── Rejected combat target (player / unknown) target-cycle budget ──
        // TAB must not loop forever on the same forbidden target: after a bounded number of
        // cycles the bot waits for the selection to change on its own.
        private int _rejectedTargetId;
        private int _rejectedTargetTabCount;
        private DateTime _rejectedTargetFirstSeenAt = DateTime.MinValue;
        private bool _rejectedTargetTabLimitLogged;
        /// <summary>Rejected player target already reported to the other-player response (log throttle).</summary>
        private int _reportedPlayerTargetId = int.MinValue;

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

        /// <summary>
        /// Loop cadence (ms) the host must use between <see cref="Update"/> calls.
        /// OnlyMove is the position-critical mode — it is pure navigation and the only
        /// mode that samples the player position on the faster tick
        /// (<see cref="BotConstants.Delays.PathRunnerOnlyMoveTickMs"/>). Every
        /// combat-capable mode keeps <see cref="BotConstants.Delays.PathRunnerTickMs"/>.
        /// </summary>
        public int TickIntervalMs => IsMoveOnlyActive
            ? BotConstants.Delays.PathRunnerOnlyMoveTickMs
            : BotConstants.Delays.PathRunnerTickMs;

        /// <summary>
        /// Cancels only the grouping-owned sequence and releases inputs through the existing
        /// MovementSystem ownership helpers. Hosts call this at pause/stop/phase boundaries.
        /// </summary>
        public void CancelMobGrouping(string reason)
        {
            lock (_mobGroupingLock)
            {
                MobGroupingDecision decision = _mobGroupingController.Cancel(DateTime.UtcNow, reason);
                if (decision.Directive != MobGroupingDirective.Cancelled)
                    return;
                FinishMobGroupingOwnership(resumeCombat: true, DateTime.UtcNow);
            }
        }

        /// <summary>Disposes the reusable client ROI bitmap after the movement loop has ended.</summary>
        public void DisposeMobGroupingDetector()
        {
            CancelMobGrouping("movement system stopped");
            lock (_mobGroupingLock)
            {
                if (_mobGroupingDisposed)
                    return;
                _mobGroupingDisposed = true;
                _mobMarkerDetector.Dispose();
            }
        }

        /// <summary>Manually captures and saves one annotated combat-ROI diagnostic image.</summary>
        public bool CaptureMobGroupingDebugSnapshot()
        {
            lock (_mobGroupingLock)
            {
                if (_mobGroupingDisposed)
                    return false;

                using Bitmap? frame = _mobMarkerDetector.CaptureDebugFrame(out MobMarkerDetectionResult? result);
                if (frame == null || result == null)
                    return false;

                _lastMobGroupingAnalysis = MobGroupAnalyzer.Analyze(result.Markers, result.PlayerAnchor);
                return MobGroupingDebugRenderer.Save(
                    frame, result, _lastMobGroupingAnalysis, CurrentMobGroupingState.ToString(), _log,
                    _mobGroupingController.LastDecisionReason, _mobGroupingController.LastTrend);
            }
        }

        private void StartMobGroupingMove(MobGroupingDecision decision)
        {
            CancelCombatGateWaypointReturn("mob grouping owns movement");
            ReleaseSkillThree();
            StopMoving();
            _combatHandler.SuspendForExternalMovement();
            _resumeCombatSkillAfterGrouping = true;
            _mobGroupingOwnsMovement = true;

            // Nothing is snapshotted here. ApplyGatherMovement resolves the live
            // world-space route waypoint every tick and reuses MoveTowards so the desired
            // heading follows the player naturally; RecommendedEscapeDirection never
            // drives physical movement.
        }

        /// <summary>
        /// Pure bearing used by <see cref="MoveTowards"/>: exposed so tests can verify the
        /// gather walk recomputes its heading from the player's current world position
        /// instead of reusing a bearing captured once at movement start.
        /// </summary>
        internal static float ComputeMoveTowardsBearingDeg(
            float currX, float currY, float targetX, float targetY)
            => GeometryUtils.GetBearingToTargetDeg(currX, currY, targetX, targetY);

        /// <summary>
        /// Resolves the temporary gather navigation target from the ordinary route: the
        /// current positional waypoint, or the final-standby point while standby owns the
        /// route. Operation-only steps and already-reached targets are NOT valid gather
        /// destinations - the ordinary path pipeline must process those first. The
        /// waypoint's own steering override is resolved with exactly the same fallback as
        /// normal route movement; gather movement never forces a steering type.
        /// </summary>
        internal bool TryGetGatherNavigationTarget(
            float playerX,
            float playerY,
            out float targetX,
            out float targetY,
            out MovementSteeringMode steeringMode)
        {
            targetX = 0f;
            targetY = 0f;
            steeringMode = _steeringMode;

            if (_finalStandbyActive)
            {
                if (_finalStandbyMode != BotMode.MoveAndAttack &&
                    _finalStandbyMode != BotMode.MoveAndAttackAndLoot)
                    return false;

                if (GeometryUtils.Distance(playerX, playerY, _finalStandbyX, _finalStandbyY) <=
                    GetEffectiveWaypointReachThreshold(_finalStandbyWaypoint))
                    return false;

                targetX = _finalStandbyX;
                targetY = _finalStandbyY;
                steeringMode = ResolveSteeringMode(_finalStandbySteeringMode, _steeringMode);
                return true;
            }

            if (_waypoints.Count == 0)
                return false;

            Waypoint waypoint = _waypoints.Peek();
            if (waypoint.IsOperationStep)
                return false;

            if (GeometryUtils.Distance(playerX, playerY, waypoint.X, waypoint.Y) <=
                GetEffectiveWaypointReachThreshold(waypoint))
                return false;

            targetX = waypoint.X;
            targetY = waypoint.Y;
            steeringMode = ResolveSteeringMode(waypoint.SteeringMode, _steeringMode);
            return true;
        }

        /// <summary>
        /// True when MovementSystem currently exposes a valid positional navigation target
        /// for a gather walk. MobGroupingController receives this as a boolean and never
        /// touches the route itself.
        /// </summary>
        internal bool CanGatherTowardCurrentNavigationTarget(float playerX, float playerY) =>
            TryGetGatherNavigationTarget(playerX, playerY, out _, out _, out _);

        /// <summary>
        /// One tick of the temporary gather walk. The target is the live world-space route
        /// waypoint, so the desired bearing is recomputed from the player's current position
        /// through the ordinary MoveTowards implementation (including per-waypoint steering
        /// and KeyboardTurn feedback). Returns false when no valid target remains; the
        /// controller observes the same fact through HasGatherNavigationTarget and ends the
        /// gather on this tick.
        /// </summary>
        private bool ApplyGatherMovement(float currX, float currY)
        {
            if (!TryGetGatherNavigationTarget(currX, currY,
                    out float targetX, out float targetY, out MovementSteeringMode steeringMode))
                return false;

            MoveTowards(currX, currY, targetX, targetY, steeringMode);
            return true;
        }

        private void FinishMobGroupingOwnership(bool resumeCombat, DateTime nowUtc)
        {
            if (_mobGroupingOwnsMovement)
            {
                StopMoving();
                _mobGroupingOwnsMovement = false;
            }
            ReleaseSkillThree();
            _combatHandler.ResumeAfterExternalMovement();
            _resumeCombatSkillAfterGrouping =
                resumeCombat && MobGroupingController.IsSupportedCombatMode(CurrentMode);
            if (resumeCombat)
                _mobGroupingController.CompleteResume(nowUtc);
        }

        /// <summary>True while grouping has temporarily taken control away from route/combat.</summary>
        private bool IsMobGroupingSequenceActive => _mobGroupingController.IsSequenceActive;

        private bool TryObserveOrStartMobGrouping(
            BotMode currentMode,
            bool hasCombatContext,
            float currX,
            float currY,
            bool positionValid,
            CancellationToken token)
        {
            if (_mobGroupingDisposed)
                return false;

            bool enabled = MobGroupingRuntimeSettings.Enabled;
            // Both attack modes are eligible; the mandatory MoveAndAttackAndLoot loot
            // obligation is not excluded here because the controller receives it as an
            // authoritative hold and cancels/blocks the gather instead of bypassing it.
            bool eligible = enabled &&
                MobGroupingController.IsSupportedCombatMode(CurrentMode) &&
                MobGroupingController.IsSupportedCombatMode(currentMode);

            // The controller is always ticked (even when disabled or outside an attack
            // mode) so its time-based combat-absence lifecycle can reset the encounter
            // grouping count without depending on detection running. Detection stays gated:
            // screen capture only happens while grouping may actually act.
            DateTime now = DateTime.UtcNow;
            bool detectionCaptured = false;
            if (eligible && now >= _nextMobGroupingDetectionAt)
            {
                CaptureMobGroupingSample();
                detectionCaptured = true;
            }

            bool windowValid = _mobMarkerDetector.IsGameWindowValid();
            MobGroupingDecision decision;
            lock (_mobGroupingLock)
            {
                var input = CreateMobGroupingInput(
                    now, enabled, hasCombatContext, windowValid, detectionCaptured,
                    detectionCaptured ? _lastMobGroupingAnalysis : null,
                    positionValid, currX, currY, token);
                decision = _mobGroupingController.Tick(input);
                ApplyMobGroupingDecision(decision, now);

                // StartMove only releases combat control here; the first waypoint-steered
                // gather step runs immediately on this same tick through the ordinary
                // MoveTowards path.
                if (decision.Directive == MobGroupingDirective.StartMove && _mobGroupingOwnsMovement)
                    ApplyGatherMovement(currX, currY);
            }

            return decision.Directive != MobGroupingDirective.None;
        }

        /// <summary>
        /// Ticks an already-active operation before the normal combat pipeline. Returning false
        /// means grouping still owns this movement tick; true means it was cancelled and normal
        /// higher-priority processing may continue immediately.
        /// </summary>
        private bool TickActiveMobGrouping(
            BotMode currentMode,
            bool hasCombatContext,
            float currX,
            float currY,
            bool positionValid,
            CancellationToken token)
        {
            DateTime now = DateTime.UtcNow;
            bool windowValid = _mobMarkerDetector.IsGameWindowValid();
            bool detectionCaptured = false;

            // The gather walk samples the mob layout every GatherRescanIntervalMs (500 ms).
            // NeedsGatherRescan is false at/after the gather deadline, so a rescan can never
            // delay the deadline-driven stop and no scan is started past the deadline.
            if (_mobGroupingController.NeedsGatherRescan(now))
            {
                CaptureMobGroupingSample();
                detectionCaptured = true;
            }

            MobGroupingDecision decision;
            MobGroupingTickInput input = default;
            lock (_mobGroupingLock)
            {
                input = CreateMobGroupingInput(
                    now, MobGroupingRuntimeSettings.Enabled, hasCombatContext, windowValid, detectionCaptured,
                    detectionCaptured ? _lastMobGroupingAnalysis : null,
                    positionValid, currX, currY, token);
                decision = _mobGroupingController.Tick(input);

                ApplyMobGroupingDecision(decision, now);

                // Continue the LIVE waypoint-steered gather walk for the whole movement.
                // Every tick recomputes the desired bearing from the player's current
                // position to the current route waypoint through MoveTowards, so
                // KeyboardTurn / DirectCamera and the waypoint steering override behave
                // exactly like ordinary route movement.
                if (decision.Directive != MobGroupingDirective.StartMove &&
                    decision.Directive != MobGroupingDirective.Cancelled &&
                    _mobGroupingOwnsMovement &&
                    _mobGroupingController.State == MobGroupingState.GroupingMove)
                {
                    ApplyGatherMovement(currX, currY);
                }
            }

            if (decision.Directive == MobGroupingDirective.Cancelled)
            {
                bool paused;
                try { paused = MobGroupingPausePredicate?.Invoke() == true; }
                catch { paused = true; }
                return CanContinueAfterGroupingCancellation(
                    paused || input.IsPaused,
                    input.IsStopping || token.IsCancellationRequested,
                    input.IsTransitioning,
                    input.IsRecoveryActive,
                    input.IsLootHoldActive || input.IsMandatoryLootBarrierActive,
                    windowValid);
            }

            return false;
        }

        /// <summary>
        /// Pure same-tick handoff decision after a grouping cancellation: when no other
        /// higher-priority owner (pause, stop/cancellation, transition, recovery, loot hold
        /// or an invalid/unfocused window) holds the update, the ordinary combat pipeline
        /// must run on the SAME tick so a lost target can produce TabAfterKill and
        /// the close-camera retarget check immediately.
        /// </summary>
        internal static bool CanContinueAfterGroupingCancellation(
            bool paused,
            bool cancellationRequested,
            bool transitioning,
            bool recoveryActive,
            bool lootHoldActive,
            bool windowValid)
            => !paused && !cancellationRequested && !transitioning &&
               !recoveryActive && !lootHoldActive && windowValid;

        private void CaptureMobGroupingSample()
        {
            MobMarkerDetectionResult result = _mobMarkerDetector.CaptureAndDetect();
            MobGroupAnalysis? analysis = result.Success
                ? MobGroupAnalyzer.Analyze(result.Markers, result.PlayerAnchor)
                : null;
            // Camera yaw is only read once per logical observation (never per tick), so the
            // bounded formation history can invalidate trends across large camera changes
            // without adding a second capture or memory-read bottleneck.
            float cameraYawRadians = 0f;
            bool hasCameraYaw = result.Success && TryReadCameraYawRadians(out cameraYawRadians);
            if (!hasCameraYaw)
                cameraYawRadians = 0f;
            lock (_mobGroupingLock)
            {
                _nextMobGroupingDetectionAt = DateTime.UtcNow.AddMilliseconds(BotConstants.MobGrouping.DetectionIntervalMs);
                _lastMobGroupingCaptureSucceeded = result.Success;
                _lastMobGroupingAnalysis = analysis;
                _lastMobGroupingHasCameraYaw = hasCameraYaw;
                _lastMobGroupingCameraYawRadians = cameraYawRadians;
                _lastMobGroupingClientWidth = result.ClientWidth;
                _lastMobGroupingClientHeight = result.ClientHeight;
            }
        }

        /// <summary>Reads the camera yaw for a grouping observation; never throws.</summary>
        private bool TryReadCameraYawRadians(out float radians)
        {
            radians = 0f;
            try
            {
                radians = _memoryService.GetCameraAngle();
                return !float.IsNaN(radians) && !float.IsInfinity(radians);
            }
            catch
            {
                return false;
            }
        }

        private MobGroupingTickInput CreateMobGroupingInput(
            DateTime nowUtc,
            bool enabled,
            bool hasCombatContext,
            bool windowValid,
            bool detectionCaptured,
            MobGroupAnalysis? analysis,
            bool positionValid,
            float currX,
            float currY,
            CancellationToken token)
        {
            bool paused;
            try { paused = MobGroupingPausePredicate?.Invoke() == true; }
            catch { paused = true; }

            bool lootHold = IsMobGroupingLootHoldActive;
            bool mandatoryLootBarrier = IsMandatoryLootBarrierActive;
            bool transition = _repotHelper.IsReportAndGoBackActive || _waypointRepotRequested ||
                DateTime.Now < _inCityStuckCooldownUntil;
            bool recovery = _isUnstuckRoutineActive || _reverseDiagonalRecovery.IsActive ||
                IsWaypointSpecialRecoveryActive;

            // The controller receives the live waypoint target and the resolved steering
            // mode purely for logging/diagnostics; the physical gather step still runs
            // through ApplyGatherMovement/TryGetGatherNavigationTarget.
            bool hasGatherTarget = TryGetGatherNavigationTarget(
                currX, currY, out float gatherTargetX, out float gatherTargetY,
                out MovementSteeringMode gatherSteeringMode);

            return new MobGroupingTickInput(
                nowUtc,
                enabled,
                CurrentMode,
                hasCombatContext,
                paused,
                token.IsCancellationRequested,
                transition,
                recovery,
                lootHold,
                windowValid,
                detectionCaptured,
                analysis,
                positionValid,
                currX,
                currY,
                hasGatherTarget,
                gatherTargetX,
                gatherTargetY,
                gatherSteeringMode,
                mandatoryLootBarrier,
                _lastMobGroupingCameraYawRadians,
                _lastMobGroupingHasCameraYaw,
                _lastMobGroupingClientWidth,
                _lastMobGroupingClientHeight);
        }

        private void ApplyMobGroupingDecision(MobGroupingDecision decision, DateTime nowUtc)
        {
            switch (decision.Directive)
            {
                case MobGroupingDirective.StartMove:
                    StartMobGroupingMove(decision);
                    break;
                case MobGroupingDirective.StopMovement:
                    StopMoving();
                    _mobGroupingOwnsMovement = false;
                    break;
                case MobGroupingDirective.ResumeCombat:
                    FinishMobGroupingOwnership(resumeCombat: true, nowUtc);
                    break;
                case MobGroupingDirective.Cancelled:
                    FinishMobGroupingOwnership(resumeCombat: true, nowUtc);
                    break;
            }
        }

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
        private float? _keyboardFrozenBearingDeg;

        // ── First-step heading alignment ──
        // A newly started path must not move forward from a bad camera angle: the very
        // first route step turns in place (A/D + fast-turn mouse, no W) until the camera
        // is within the steering tolerance. Cleared after that first step and re-armed
        // only by a map change / teleport, so later waypoints keep the normal
        // move-and-turn steering.
        private bool _initialHeadingAlignmentPending = true;
        private bool _initialHeadingAlignmentInProgress;
        private int _initialHeadingAlignmentLastTick;
        private DateTime _initialHeadingAlignmentDeadlineUtc = DateTime.MinValue;

        // ── Per-waypoint start camera angle ──
        // Applied once per waypoint leg, while W is released, before that leg's first
        // W-down. Tracking the last applied position + value makes repeated Update
        // ticks, route resync and combat re-entry idempotent within the same leg.
        private bool _startCameraAngleApplied;
        private float _startCameraAngleAppliedX;
        private float _startCameraAngleAppliedY;
        private int _startCameraAngleAppliedBits;

        // DirectCamera steering writes the target bearing before pressing W by design.
        // When an explicit start angle was just applied, the leg's first W must happen
        // at that configured bearing instead, so the direct write is deferred by one
        // W transition.
        private bool _startCameraAngleAwaitingFirstW;

        private enum TurnKeyState
        {
            None,
            LeftA,
            RightD
        }

        private TurnKeyState _heldTurnKey = TurnKeyState.None;

        /// <summary>True while MovementSystem owns the accelerated-turn cursor position.</summary>
        private bool _keyboardFastTurnMouseActive = false;

        /// <summary>Edge owned while fast turning (<see cref="TurnKeyState.LeftA"/> = left, RightD = right).</summary>
        private TurnKeyState _keyboardFastTurnMouseDirection = TurnKeyState.None;

        /// <summary>
        /// Set when fast-turn assistance ended while its cursor could not be neutralized
        /// right away (loot owns the cursor, or the client rectangle is temporarily
        /// unresolvable). The next steering update/cleanup retries the neutralization.
        /// </summary>
        private bool _keyboardFastTurnMouseNeutralizePending = false;

        /// <summary>Throttles the "fast-turn mouse unavailable" diagnostic to one message per episode.</summary>
        private bool _keyboardFastTurnMouseFailureLogged = false;

        /// <summary>Throttles the "cursor did not physically reach the fast-turn edge" warning to one message per episode.</summary>
        private bool _keyboardFastTurnMouseWarningLogged = false;

        /// <summary>
        /// Human-override suppression deadline (UTC). While the current time is before it,
        /// no fast-turn cursor write may happen. Reacquisition additionally requires the
        /// game to be foreground and the cursor to be back inside the game client.
        /// </summary>
        private DateTime _keyboardFastTurnMouseSuppressedUntilUtc = DateTime.MinValue;

        /// <summary>True once the current human-override episode was logged (throttles the per-tick check).</summary>
        private bool _keyboardFastTurnMouseHumanOverrideLogged = false;

        /// <summary>
        /// Edge that still needs a deferred neutralization because another owner (loot)
        /// held the cursor at release time. Used to confirm the human did not move the
        /// cursor before the delayed neutralization runs.
        /// </summary>
        private TurnKeyState _keyboardFastTurnMouseNeutralizePendingDirection = TurnKeyState.None;

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

        // ── EXP route map invariant ──
        // A route that declares an expected map (route ProtectionMapNumber) must never
        // keep navigating old-map waypoints after the player leaves that map. The guard
        // uses one coherent player-state snapshot per tick (never independent reads),
        // holds all input while a change is unconfirmed, and permanently invalidates the
        // route once a different valid map is confirmed. ExpectedMap = 0 disables the
        // invariant so multi-map travel paths keep their existing behavior.
        private bool _mapTransitionGuardActive;
        private bool _mapTransitionConfirmed;
        private int _mapTransitionObservedMap;
        private int _mapTransitionConsecutiveReads;
        private PlayerStateSnapshot? _lastCoherentSnapshot;

        /// <summary>Expected map number for this route; 0 = no map restriction.</summary>
        public int ExpectedMap { get; set; }

        /// <summary>Route label used by [ExpMapGuard] diagnostics (path file name).</summary>
        public string RouteLabel { get; set; } = "";

        /// <summary>True while a suspected map change is holding navigation but is not confirmed yet.</summary>
        public bool IsMapTransitionPending => _mapTransitionGuardActive && !_mapTransitionConfirmed;

        /// <summary>True when a confirmed unexpected map change permanently invalidated this route.</summary>
        public bool IsMapRouteInvalidated => _mapTransitionConfirmed;

        /// <summary>Map number observed when the unexpected change was confirmed.</summary>
        public int ConfirmedTransitionMap { get; private set; }

        /// <summary>Last coherent snapshot taken while still on the expected map (post-transition sync source).</summary>
        public PlayerStateSnapshot? LastCoherentSnapshotBeforeMapChange => _lastCoherentSnapshot;

        private bool _isUnstuckRoutineActive = false;
        private const double FORCE_START_MIN_INTERVAL_MS = BotConstants.Movement.ForceStartMinIntervalMs;
        private DateTime _lastForceStartMovingAt = DateTime.MinValue;

        // Consecutive navigation failures for the current unresolved waypoint.
        // This is deliberately NOT a "did the character physically move?" counter:
        // ReverseDiagonalRecovery may successfully walk away from an obstacle and the
        // bot can still be unable to reach the same waypoint. The counter is reset when
        // the waypoint is consumed, a genuinely different route target is activated, a
        // map/teleport invalidates the navigation context, or ordinary route movement
        // proves real progress toward the waypoint (the previous blockage is resolved).
        // Recovery displacement alone never resets it.
        private const int STUCK_MAX_ATTEMPTS_OUTSIDE = 3;
        // In the city the unstuck must not hammer the same spot: after 2 failed attempts
        // the route is stopped and the coordinator waits 10 minutes, resets and restarts
        // from the Repot step (go repot → start again).
        private const int STUCK_MAX_ATTEMPTS_IN_CITY = 2;
        private const double NAVIGATION_NO_PROGRESS_TIMEOUT_SECONDS = 15;
        private const float NAVIGATION_PROGRESS_MIN_DISTANCE = BotConstants.Movement.NoProgressMinDistance;
        private int _consecutiveStuckAttempts = 0;
        private bool _navigationFailureEscalationRequested;
        private int _stopRequested;

        /// <summary>Player position when the last reverse-diagonal recovery started.
        /// Diagnostic only — physical displacement must never decide navigation success.</summary>
        private (float X, float Y)? _lastStuckAttemptPos = null;

        // Best distance and deadline are scoped to the unresolved waypoint. ReverseDiagonal
        // movement is deliberately excluded: backing away and re-approaching an obstacle is
        // not navigation progress until ordinary route movement beats the previous best.
        // The deadline starts with the waypoint's first forward-movement tick and is cleared
        // by intentional input holds (never by an elapsed timer alone).
        private string _navigationProgressWaypointId = "";
        private float _navigationProgressTargetX = float.NaN;
        private float _navigationProgressTargetY = float.NaN;
        private int _navigationWaypointSequence;
        private float _bestWaypointDistance = float.MaxValue;
        private float _lastNavigationProgressDistance = float.MaxValue;
        private DateTime _lastNavigationProgressTime = DateTime.MinValue;
        private bool _awaitingNavigationRecoveryProof;

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

        // Combat unstuck escalations that could not start because the mandatory loot hold
        // refused the recovery authorization. Bounded: once the limit is reached no further
        // combat recovery movement is attempted while the hold owns the input, so a recovery
        // can never be marked active while it cannot advance.
        private const int COMBAT_UNSTUCK_MAX_DENIED_ATTEMPTS = 4;
        private int _combatUnstuckDeniedCount = 0;
        private bool _combatRecoverySuspendedByLootHold;

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
        private readonly GameConnectionWatchdog _connectionWatchdog = new();
        private bool _gameUnresponsiveDetected;
        private DateTime _positionReadFailureSince = DateTime.MinValue;

        /// <summary>Called once when active movement makes no position progress for the connection watchdog timeout.</summary>
        public Action<string>? OnGameUnresponsiveDetected { get; set; }

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
        private readonly WKeyCameraController? _wKeyCameraController;
        private readonly WKeyCameraOwner? _wKeyCameraOwner;

        private int _tickCount = 0;
        private int _stateLogInterval = 5; // Log periodic state every N ticks

        private bool _isMovingForward = false;
        private bool _isSkillThreeHeld = false;
        private DateTime _attackRetryStartedAt = DateTime.MinValue;
        private int _attackRetryLastMana = int.MinValue;
        private int _attackRetryTargetId;
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
        private const int CombatRetargetCameraMaximumAttempts = 3;
        private CombatRetargetCameraStage _combatRetargetCameraStage = CombatRetargetCameraStage.None;
        private bool _combatRetargetAwaitingSelection = false;
        private int _combatRetargetCameraFailureAttempts;
        private bool _combatRetargetFailed;
        private long _postKillRetargetRequestId;
        private int _postKillTargetReleaseId;
        private int _postKillTargetReleaseAttempts;
        private bool _postKillTargetReleaseRefusalLogged;
        private DateTime _postKillTargetReleaseRetryAtUtc = DateTime.MinValue;
        private bool _postKillCloseTargetCheckPending;
        private int _postKillTargetIdBeforeTab;
        private bool _postPinkScanTargetCheckPending;

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
            WaypointRecoveryExecutor? waypointRecoveryExecutor = null,
            MovementSteeringMode steeringMode = MovementSteeringMode.KeyboardTurn,
            WKeyCameraController? wKeyCameraController = null,
            CombatTargetSafety? targetSafety = null)
        {
            _memoryService = memoryService;
            _log = log;
            _wKeyCameraController = wKeyCameraController;
            _wKeyCameraOwner = wKeyCameraController?.CreateOwner("MovementSystem", SendSyntheticForwardKeyEvent);
            _combatHandler = new CombatHandler(log);
            _targetSafety = targetSafety ?? new CombatTargetSafety(
                () => memoryService.GetSelectedTargetId(),
                new ScreenCombatPlayerPresenceScanner(
                    log,
                    shouldIgnoreOtherPlayer: ShouldIgnoreOtherPlayerMarker),
                () => memoryService.GetIsInCity(),
                log,
                shouldIgnoreOtherPlayer: ShouldIgnoreOtherPlayerMarker);
            _mobMarkerDetector = new MobMarkerDetector(log);
            _mobGroupingController = new MobGroupingController(log);
            _repotHelper = new RepotHelper(memoryService, log, StopMoving, () => _goalReached = true);
            _enableWaypointSpecialRecoveries = enableWaypointSpecialRecoveries;
            _steeringMode = steeringMode;
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
            _log($"[Steering] DefaultMode={_steeringMode} (per-waypoint overrides allowed) tolerance={BotConstants.Movement.KeyboardTurnToleranceDegrees:F1}°.");
            _log($"[LocalMap] Initial map ID = {initialMapId}.");
            _log(_steeringMode == MovementSteeringMode.DirectCamera
                ? "[BearingCalib] Using pure float math: Math.Atan2(target-current) → radians written directly to camera (32-bit float). Legacy direct steering active."
                : "[BearingCalib] Using calibrated bearing math; actual camera yaw feeds held A/D keyboard steering.");

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
                    string steeringTag = p.SteeringMode?.ToString() ?? "Default";
                    string startCamTag = p.StartCameraAngleBits.HasValue
                        ? $" StartCam:{CameraAngleValue.Describe(p.StartCameraAngleBits)}"
                        : "";
                    _log($"[Path] #{index}: ({p.X:F1}, {p.Y:F1}) Precision:{p.Precision} Mode:{p.Mode} CamLock:{p.CameraDistanceLock} AtkDis:{p.AttackDisengageDistance} Steer:{steeringTag}{startCamTag}{methodTag}");
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

        private void StopForUnresponsiveGame(string reason)
        {
            if (_gameUnresponsiveDetected)
                return;

            _gameUnresponsiveDetected = true;
            _log($"[ConnectionWatchdog] {reason} Stopping movement and requesting a full bot stop.");
            CancelCombatGateWaypointReturn("game connection watchdog");
            CancelMobGrouping("game connection watchdog");
            EndCombatRecoveryMovement("game connection watchdog");
            StopMoving();
            ReleaseCombatKeys();
            OnGameUnresponsiveDetected?.Invoke(reason);
        }

        /// <summary>
        /// Releases any loot-owned navigation/camera state when the effective policy is
        /// "automatic loot disabled". Idempotent: a stale request id is cancelled, the
        /// barrier is abandoned (never marked as a completed scan) and all loot hold
        /// bookkeeping is cleared so ordinary navigation/combat resume immediately.
        /// </summary>
        private void ApplyAutomaticLootDisabledPolicy()
        {
            if (!IsAutomaticLootDisabled)
                return;

            bool resumePostKillSearchWithoutLoot = _postKillRetargetRequestId > 0;
            CancelCombatGateWaypointReturn("automatic loot disabled by profile/path policy");

            if (_combatRecoveryMovementAuthorized)
                EndCombatRecoveryMovement("automatic loot disabled by profile/path policy");

            ReleaseLootBarrierForDisabledPolicy("automatic loot disabled by profile/path policy");

            _lootWaitActive = false;
            _lootWaitDeadline = DateTime.MinValue;
            _lootPriorityHoldActive = false;
            _lootPriorityHoldSince = DateTime.MinValue;
            _lootPriorityHoldTimedOut = false;
            _pinkScanHoldActive = false;
            _pinkScanHoldSince = DateTime.MinValue;
            _pinkScanHoldTimedOut = false;
            _postPinkScanTargetCheckPending = false;
            if (resumePostKillSearchWithoutLoot)
            {
                _postKillRetargetRequestId = 0;
                _postKillTargetReleaseId = 0;
                _postKillTargetReleaseAttempts = 0;
                _postKillTargetReleaseRetryAtUtc = DateTime.MinValue;
                if (MobGroupingController.IsSupportedCombatMode(CurrentMode))
                {
                    _log("[CombatRetarget] Automatic loot disabled while waiting for PostKill — continuing without a loot wait.");
                    StartCombatRetargetSearch();
                }
                else
                {
                    _postKillCloseTargetCheckPending = false;
                    _postKillTargetIdBeforeTab = 0;
                }
            }
        }

        /// <summary>
        /// Pushes the effective profile/path policy into the attached loot component at
        /// the actual waypoint transition boundary. The host loop also re-evaluates it
        /// every tick, but this makes a newly active disabled waypoint cancel pending
        /// loot work immediately instead of one host poll later.
        /// </summary>
        private void SynchronizeAutomaticLootPolicy()
        {
            LootSystem? loot = LootSystemRef;
            if (loot == null)
                return;

            bool disabled = IsAutomaticLootDisabled;
            if (loot.AutomaticLootDisabled != disabled)
                loot.AutomaticLootDisabled = disabled;
        }

        /// <summary>
        /// Cancels the exact armed obligation (when one exists) and abandons the local
        /// barrier without ever producing a success state. The release is idempotent and
        /// clears the input-hold flag so movement/camera ownership is handed back.
        /// </summary>
        private void ReleaseLootBarrierForDisabledPolicy(string reason)
        {
            bool hadOwnership = _lootNavigationBarrierHoldsInput ||
                                _lootBarrier.IsActive ||
                                _lootBarrier.HasTerminalFailure;
            if (!hadOwnership)
                return;

            if (_lootBarrier.TryGetRequestId(out long requestId))
                LootSystemRef?.CancelMandatoryLootRequest(requestId, reason);

            _lootBarrier.Abandon(reason);
            SetLootNavigationBarrierHoldsInput(false);
            _lootBarrierBlockedLogged = false;
            _lootBarrierFailureLogged = false;
            _lootTargetCycleSuppressedLogged = false;
            _log($"[LootBarrier] Released — {reason}; navigation continues.");
        }

        /// <summary>
        /// Arms the explicit navigation barrier and creates/coalesces the authoritative
        /// LootSystem request. StopMoving releases W/A/D before any loot work starts.
        /// </summary>
        private void ArmMandatoryLootBarrier(LootBarrierReason reason)
        {
            if (IsAutomaticLootDisabled)
            {
                ReleaseLootBarrierForDisabledPolicy("automatic loot disabled by profile/path policy");
                return;
            }

            if (!RequiresMandatoryLootBarrier(CurrentMode))
                return;

            LootSystem? loot = LootSystemRef;
            if (loot == null)
            {
                SetLootNavigationBarrierHoldsInput(true);
                _lootBarrier.Fail("MoveAndAttackAndLoot has no loot system attached");
                StopMoving();
                ReleaseSkillThree();
                if (!_lootBarrierFailureLogged)
                {
                    _lootBarrierFailureLogged = true;
                    _log("[LootBarrier] FATAL: MoveAndAttackAndLoot has no loot system — navigation blocked.");
                }
                return;
            }

            bool barrierWasActive = _lootBarrier.IsActive;
            long requestId = loot.BeginMandatoryLootRequest(reason);
            if (requestId <= 0)
            {
                SetLootNavigationBarrierHoldsInput(true);
                _lootBarrier.Fail("loot system did not return a request id");
                StopMoving();
                ReleaseSkillThree();
                if (!_lootBarrierFailureLogged)
                {
                    _lootBarrierFailureLogged = true;
                    _log("[LootBarrier] FATAL: loot system rejected the mandatory request — navigation blocked.");
                }
                return;
            }

            if (!barrierWasActive)
            {
                _lootBarrierBlockedLogged = false;
                _lootBarrierFailureLogged = false;
                _lootTargetCycleSuppressedLogged = false;
            }

            _lootBarrier.Arm(requestId, reason);
            SetLootNavigationBarrierHoldsInput(true);
            StopMoving();
            ReleaseSkillThree();
            loot.NotifyNavigationHeld(requestId);

            if (!_lootBarrierBlockedLogged)
            {
                _lootBarrierBlockedLogged = true;
                _log($"[LootBarrier] Armed reason={reason} request={requestId}; waypoint navigation blocked.");
            }
            else
            {
                _log($"[LootBarrier] Obligation preserved request={requestId} reason={reason} state={_lootBarrier.Phase}.");
            }
        }

        private void SetLootNavigationBarrierHoldsInput(bool holdsInput)
        {
            _lootNavigationBarrierHoldsInput = holdsInput;
            if (_wKeyCameraController != null && _wKeyCameraOwner != null)
                _wKeyCameraController.SetMandatoryLootNavigationBlock(_wKeyCameraOwner, holdsInput);
        }

        /// <summary>
        /// Applies the authoritative LootSystem snapshot to the navigation barrier and
        /// performs the only navigation release paths: a camera-verified Completed status
        /// for the exact request generation armed by this MovementSystem, or an explicit
        /// Unavailable travel verification (never reported as an empty scan).
        ///
        /// It also adopts a travel-candidate request created by the passive pre-scan:
        /// LootSystem is the single writer of the obligation, movement only mirrors it.
        /// </summary>
        private void RefreshLootBarrier()
        {
            if (IsAutomaticLootDisabled)
            {
                bool resumePostKillSearchWithoutLoot = _postKillRetargetRequestId > 0;
                ReleaseLootBarrierForDisabledPolicy("automatic loot disabled by profile/path policy");
                if (resumePostKillSearchWithoutLoot)
                {
                    _postKillRetargetRequestId = 0;
                    _postKillTargetReleaseId = 0;
                    _postKillTargetReleaseAttempts = 0;
                    _postKillTargetReleaseRetryAtUtc = DateTime.MinValue;
                    if (MobGroupingController.IsSupportedCombatMode(CurrentMode))
                        StartCombatRetargetSearch();
                    else
                        ResetPostKillRetargetState();
                }
                return;
            }

            if (!RequiresMandatoryLootBarrier(CurrentMode))
            {
                if (_lootBarrier.IsActive || _lootBarrier.HasTerminalFailure)
                {
                    if (_lootBarrier.TryGetRequestId(out long cancelledId))
                        LootSystemRef?.CancelMandatoryLootRequest(cancelledId, $"mode changed to {CurrentMode}");
                    _lootBarrier.Abandon($"mode changed to {CurrentMode}");
                    SetLootNavigationBarrierHoldsInput(false);
                    _log($"[LootBarrier] Released — mode changed to {CurrentMode}; no mandatory loot applies.");
                }
                bool combatModeStillAllowed = MobGroupingController.IsSupportedCombatMode(CurrentMode);
                if (!combatModeStillAllowed &&
                    (_postKillRetargetRequestId > 0 || _postKillTargetReleaseId > 0 ||
                     _combatRetargetCameraStage != CombatRetargetCameraStage.None ||
                     _postKillCloseTargetCheckPending))
                {
                    ResetPostKillRetargetState();
                }
                else if (combatModeStillAllowed &&
                         (_postKillRetargetRequestId > 0 || _postKillTargetReleaseId > 0))
                {
                    // MA has no mandatory normal scan, but combat remains allowed. Drop
                    // the obsolete request context and continue the close-first search.
                    _postKillRetargetRequestId = 0;
                    _postKillTargetReleaseId = 0;
                    _postKillTargetReleaseAttempts = 0;
                    _postKillTargetReleaseRetryAtUtc = DateTime.MinValue;
                    if (_combatRetargetCameraStage == CombatRetargetCameraStage.None)
                        StartCombatRetargetSearch();
                }
                return;
            }

            LootSystem? loot = LootSystemRef;

            if (!_lootBarrier.IsActive)
            {
                if (loot != null &&
                    loot.TryGetActiveMandatoryLootRequest(out long pendingId, out LootBarrierReason pendingReason))
                {
                    _lootBarrier.Arm(pendingId, pendingReason);
                    SetLootNavigationBarrierHoldsInput(true);
                    _lootBarrierBlockedLogged = false;
                    _lootBarrierFailureLogged = false;
                    StopMoving();
                    ReleaseSkillThree();
                    loot.NotifyNavigationHeld(pendingId);
                    _log($"[LootBarrier] Adopted request={pendingId} reason={pendingReason}; navigation held.");
                }
                return;
            }

            if (!_lootBarrier.TryGetRequestId(out long requestId))
                return;

            if (loot == null)
            {
                SetLootNavigationBarrierHoldsInput(true);
                _lootBarrier.Fail("loot system disappeared while the obligation was pending");
                if (_postKillRetargetRequestId == requestId)
                    ResetPostKillRetargetState();
                return;
            }

            if (!loot.TryGetMandatoryLootRequestStatus(requestId, out LootRequestStatus status))
            {
                SetLootNavigationBarrierHoldsInput(true);
                _lootBarrier.Fail($"request {requestId} status unavailable");
                if (_postKillRetargetRequestId == requestId)
                    ResetPostKillRetargetState();
                return;
            }

            LootBarrierPhase previousPhase = _lootBarrier.Phase;
            LootBarrierPhase phase = _lootBarrier.ApplyStatus(status);
            if (phase == LootBarrierPhase.Completed)
            {
                SetLootNavigationBarrierHoldsInput(false);
                _lootBarrierBlockedLogged = false;
                if (status.Reason == LootBarrierReason.PostKill)
                    _log($"[LootBarrier] PostKill completed request={requestId} cameraVerified={status.CameraVerified}");
                _log($"[LootBarrier] Completed request={requestId} items={status.ItemsCollected}; " +
                     $"Navigation released — resuming waypoint {DescribeCurrentWaypoint()}.");
            }
            else if (phase == LootBarrierPhase.Unavailable)
            {
                SetLootNavigationBarrierHoldsInput(false);
                _lootBarrierBlockedLogged = false;
                _log($"[LootBarrier] Travel verification unavailable request={requestId}; " +
                     $"navigation released — resuming waypoint {DescribeCurrentWaypoint()}.");
            }
            else if (phase == LootBarrierPhase.Failed || phase == LootBarrierPhase.Cancelled)
            {
                SetLootNavigationBarrierHoldsInput(true);
                StopMoving();
                ReleaseSkillThree();
                if (_postKillRetargetRequestId == requestId)
                    ResetPostKillRetargetState();
                if (!_lootBarrierFailureLogged)
                {
                    _lootBarrierFailureLogged = true;
                    _log($"[LootBarrier] FATAL request={requestId} state={phase} detail={_lootBarrier.Detail}; " +
                         "waypoint navigation stays blocked.");
                }
            }
            else if (phase != previousPhase)
            {
                SetLootNavigationBarrierHoldsInput(true);
                _log($"[LootBarrier] request={requestId} state={phase}; navigation remains blocked.");
            }
        }

        /// <summary>
        /// Holds ordinary movement/advancement for the active barrier. Returns false
        /// immediately when no obligation blocks navigation.
        /// </summary>
        private async Task<bool> HoldNavigationForMandatoryLootAsync(CancellationToken token)
        {
            if (IsAutomaticLootDisabled)
            {
                ReleaseLootBarrierForDisabledPolicy("automatic loot disabled by profile/path policy");
                return false;
            }

            if (!_lootBarrier.IsBlockingNavigation)
                return false;

            SetLootNavigationBarrierHoldsInput(true);

            // An authorized combat-owned recovery currently owns W/camera. The hold must not
            // release the key the recovery is using, and the loot system was already handed
            // this obligation as "combat recovery movement active" so no stationary
            // camera/scan can start. Ordinary navigation stays blocked either way.
            bool combatRecoveryOwnsInput = IsCombatRecoveryMovementAuthorized;
            if (!combatRecoveryOwnsInput)
                StopMoving();
            ReleaseSkillThree();

            // The loot system only applies its stationary camera after this explicit
            // acknowledgement that W/steering are released (or its bounded grace fallback).
            if (!combatRecoveryOwnsInput && _lootBarrier.TryGetRequestId(out long heldRequestId))
                LootSystemRef?.NotifyNavigationHeld(heldRequestId);

            if (_lootBarrier.HasTerminalFailure && !_lootBarrierFailureLogged)
            {
                _lootBarrierFailureLogged = true;
                _log($"[LootBarrier] FATAL: navigation blocked by request={_lootBarrier.RequestId} " +
                     $"state={_lootBarrier.Phase} detail={_lootBarrier.Detail}.");
            }

            await Task.Delay(BotConstants.Delays.LootUpdateMs, token);
            return true;
        }

        /// <summary>
        /// True while a combat-owned recovery is physically repositioning the player under
        /// an authorized Deferred post-kill loot obligation.
        /// </summary>
        internal bool IsCombatRecoveryMovementAuthorized => _combatRecoveryMovementAuthorized;

        /// <summary>
        /// Grants the narrow combat-recovery authorization: the exact armed request remains
        /// Deferred with a PostKill reason, no in-flight loot collection, and an atomic handoff
        /// through <see cref="LootSystem"/>. The CombatGate return may use its captured target
        /// after the release click clears selection. Ordinary navigation and
        /// ordinary stuck recovery never call this, so the barrier stays fully in force for
        /// them.
        /// </summary>
        private bool TryBeginCombatRecoveryMovement(string reason)
        {
            if (IsAutomaticLootDisabled)
                return false;

            if (!RequiresMandatoryLootBarrier(CurrentMode))
                return false;

            if (!_lootBarrier.TryGetRequestId(out long requestId) ||
                _lootBarrier.Phase != LootBarrierPhase.Deferred ||
                _lootBarrier.Reason != LootBarrierReason.PostKill)
                return false;

            bool isCombatGateReturn = string.Equals(reason, "CombatGate waypoint return", StringComparison.Ordinal);
            bool isCombatGateTargetRelease = string.Equals(reason, "CombatGate target release", StringComparison.Ordinal);
            bool targetReleaseBelongsToActiveGate =
                isCombatGateTargetRelease && _attackSuppressedForCurrentWaypoint;
            bool combatGateReturnHasReleasedTarget =
                (isCombatGateReturn || targetReleaseBelongsToActiveGate) &&
                _waypoints.Count > 0 &&
                IsCombatGateTargetSuppressionActive &&
                (isCombatGateTargetRelease || !_combatGateTargetReleasePending) &&
                Math.Abs(_combatGateSuppressedWaypoint.X - _waypoints.Peek().X) <= 0.01f &&
                Math.Abs(_combatGateSuppressedWaypoint.Y - _waypoints.Peek().Y) <= 0.01f;
            if (!_memoryService.IsMobSelected() && !combatGateReturnHasReleasedTarget)
                return false;

            if (LootCollectionStillOwnsInput())
                return false;

            LootSystem? loot = LootSystemRef;
            if (loot == null ||
                !loot.TryGetMandatoryLootRequestStatus(requestId, out LootRequestStatus status) ||
                status.State != LootRequestState.Deferred ||
                status.Reason != LootBarrierReason.PostKill)
                return false;

            if (isCombatGateReturn && _combatGateReturnHandoffReserved &&
                _combatRecoveryMovementAuthorized && _combatRecoveryMovementRequestId == requestId)
                return true;

            if (!loot.TryBeginCombatRecoveryMovement(requestId))
                return false;

            _combatRecoveryMovementAuthorized = true;
            _combatRecoveryMovementRequestId = requestId;
            _combatRecoveryMovementDeniedLogged = false;
            _combatUnstuckDeniedCount = 0;
            _combatRecoverySuspendedByLootHold = false;
            _log($"[CombatRecovery] Authorized physical recovery movement request={requestId} for {reason}.");
            return true;
        }

        /// <summary>
        /// Revalidates the active combat-recovery authorization against live state. It stays
        /// valid only while the same post-kill generation is still Deferred and the game client
        /// still owns the foreground. Other combat recoveries still require a selected mob; the
        /// CombatGate return is the narrow exception after its captured target is released. A request
        /// phase transition or supersession, focus loss, stop and route-fatal states all
        /// revoke it; the loot obligation itself is never touched here.
        /// </summary>
        private bool IsCombatRecoveryMovementStillValid()
        {
            if (!_combatRecoveryMovementAuthorized)
                return false;

            if (IsAutomaticLootDisabled ||
                !RequiresMandatoryLootBarrier(CurrentMode) ||
                _gameUnresponsiveDetected || _isCityStuckFatal)
                return false;

            if (!_lootBarrier.TryGetRequestId(out long requestId) ||
                requestId != _combatRecoveryMovementRequestId ||
                _lootBarrier.Phase != LootBarrierPhase.Deferred ||
                _lootBarrier.Reason != LootBarrierReason.PostKill)
                return false;

            bool combatGateReturnOwnsTargetRelease =
                _combatGateReturnActive &&
                _combatGateReturnTargetId > 0 &&
                _combatGateReturnTargetId == _combatGateSuppressedTargetId &&
                !_combatGateTargetReleasePending;
            if (!_memoryService.IsMobSelected() && !combatGateReturnOwnsTargetRelease)
                return false;

            LootSystem? loot = LootSystemRef;
            if (loot == null ||
                !loot.TryGetMandatoryLootRequestStatus(requestId, out LootRequestStatus status) ||
                status.State != LootRequestState.Deferred ||
                !loot.IsCombatRecoveryMovementActiveFor(requestId))
                return false;

            return _combatGateReturnActive
                ? TryResolveCombatGateClientArea(out _, out _, out _, out _, out _)
                : IsGameClientForegroundForRecovery();
        }

        /// <summary>
        /// Revokes the combat-recovery authorization and releases the loot handoff. The
        /// obligation keeps its original request id and stays Deferred, so it resumes
        /// normally once combat ends. Idempotent; callers stop their own input first.
        /// </summary>
        private void EndCombatRecoveryMovement(string reason)
        {
            if (!_combatRecoveryMovementAuthorized)
            {
                _combatGateReturnHandoffReserved = false;
                return;
            }

            long requestId = _combatRecoveryMovementRequestId;
            _combatGateReturnHandoffReserved = false;
            _combatRecoveryMovementAuthorized = false;
            _combatRecoveryMovementRequestId = 0;
            _combatRecoveryMovementDeniedLogged = false;
            LootSystemRef?.EndCombatRecoveryMovement(requestId);
            _log($"[CombatRecovery] Physical recovery authorization released request={requestId} reason={reason}; loot obligation preserved.");
        }

        /// <summary>
        /// True while the game client still owns the foreground during a combat-owned
        /// recovery. An unresolvable client window reports true (no focus evidence), so a
        /// client rename can never block a legitimate recovery; only a positive mismatch
        /// aborts the recovery input.
        /// </summary>
        protected virtual bool IsGameClientForegroundForRecovery()
        {
            nint hwnd = FindWindow(null, "Legend of Ares");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Ares");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Nostalgia");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Epic Of Ares Client");
            if (hwnd == nint.Zero)
                return true;

            nint foreground = GetForegroundWindow();
            return foreground == nint.Zero || foreground == hwnd;
        }

        /// <summary>
        /// Sends one synthesised forward-key event. Production always uses the real
        /// keybd_event path; tests override it so they never inject a real key-down into
        /// user32 (the established convention of this project's tests).
        /// </summary>
        protected virtual void SendSyntheticForwardKeyEvent(bool keyUp)
            => GameInput.keybd_event(GameInput.VK_W, GameInput.SCAN_W,
                keyUp ? (uint)GameInput.KEYEVENTF_KEYUP : 0u, 0);

        protected virtual void SendSyntheticBackwardKeyEvent(bool keyUp)
            => GameInput.keybd_event(GameInput.VK_S, GameInput.SCAN_S,
                keyUp ? (uint)GameInput.KEYEVENTF_KEYUP : 0u, 0);

        /// <summary>
        /// Presses the target-cycle (TAB) key. Virtual so tests can record a retry TAB
        /// instead of injecting a real keystroke; production behaviour is unchanged.
        /// </summary>
        protected virtual void SendTargetCycleKey()
            => GameInput.PressKey(GameInput.VK_TAB, GameInput.SCAN_TAB);

        /// <summary>
        /// Advances an authorized combat-owned reverse-diagonal recovery while the mandatory
        /// loot hold blocks ordinary navigation. Only the recovery's own camera/W input is
        /// allowed here; waypoint navigation, resync and every other movement stay blocked.
        /// </summary>
        private void TickCombatRecoveryUnderLootHold(float currX, float currY)
        {
            if (!IsCombatRecoveryMovementStillValid())
            {
                _reverseDiagonalRecovery.Stop();
                EndCombatRecoveryMovement("authorization lost during recovery");
                _isUnstuckRoutineActive = false;
                ReleaseRecoveryOwnership("combat recovery authorization lost");
                _log("[CombatRecovery] Recovery aborted — target, request obligation or focus changed; input released, loot obligation preserved.");
                return;
            }

            RecoveryResult recoveryResult = _reverseDiagonalRecovery.Tick(currX, currY);
            ApplyCameraBearing(_reverseDiagonalRecovery.CurrentBearingDeg); // camera only — Recovery controls W
            switch (recoveryResult)
            {
                case RecoveryResult.InProgress:
                    return;
                case RecoveryResult.Recovered:
                    // The barrier still blocks ordinary navigation, so the release handoff
                    // must return a released W to the loot owner instead of leaving it down.
                    StopMoving();
                    EndCombatRecoveryMovement("ReverseDiagonal recovered");
                    _isUnstuckRoutineActive = false;
                    ReleaseRecoveryOwnership("ReverseDiagonal recovered");
                    _log("[ReverseDiagonal] recovered under the mandatory loot hold — input handed back, loot obligation preserved.");
                    return;
                case RecoveryResult.Failed:
                    EndCombatRecoveryMovement("ReverseDiagonal failed");
                    _isUnstuckRoutineActive = false;
                    ReleaseRecoveryOwnership("ReverseDiagonal failed");
                    _log("[ReverseDiagonal] failed under the mandatory loot hold — all attempts exhausted.");
                    return;
            }
        }

        // ========================================================================
        //  CombatGate waypoint return recovery
        // ========================================================================

        private bool IsCombatGateTargetSuppressionActive =>
            _combatGateSuppressedTargetId > 0 &&
            _combatGateTargetDisengagedAtUtc != DateTime.MinValue;

        private void BeginCombatGateTargetSuppression(int targetId, Waypoint waypoint)
        {
            if (targetId <= 0)
                return;

            _combatGateSuppressedTargetId = targetId;
            _combatGateSuppressedWaypoint = waypoint;
            _combatGateTargetDisengagedAtUtc = DateTime.UtcNow;
            _combatGateSuppressionWaypointReached = false;
            _combatGateSuppressionRouteProgressed = false;
            _combatGateSuppressedTargetTabAttempted = false;
            _combatGateSuppressedTabSuppressionLogged = false;
            _combatGateSuppressionSelectionHandled = false;
            _combatGateTargetReleaseVerified = false;
            BeginCombatGateTargetRelease(targetId);
        }

        private void BeginCombatGateTargetRelease(int targetId)
        {
            if (targetId <= 0 || _combatGateTargetReleasePending)
                return;

            _combatGateTargetReleasePending = true;
            _combatGateTargetReleaseTargetId = targetId;
            _combatGateTargetReleaseClickCount = 0;
            _combatGateTargetReleaseRetryAtUtc = DateTime.MinValue;
            _combatGateTargetReleaseWaitLogged = false;
        }

        /// <summary>
        /// Processes one bounded target-release interaction. False means input is not yet
        /// authorized or the single verification interval has not elapsed; callers must keep
        /// combat and movement paused for this tick. True means release was verified or the
        /// bounded attempts failed and the existing safe recovery path may continue.
        /// </summary>
        private bool ProcessCombatGateTargetRelease(CancellationToken token)
        {
            if (!_combatGateTargetReleasePending)
                return true;

            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _stopRequested) != 0)
                return false;

            int targetId = _combatGateTargetReleaseTargetId;
            int currentTargetId = _memoryService.GetSelectedTargetId();
            CombatTargetValidation validation = _targetSafety.Validate();
            if (currentTargetId != targetId || validation.TargetId != targetId)
            {
                CompleteCombatGateTargetRelease(targetId,
                    $"selection changed to {currentTargetId}/{validation.Kind}", verified: true);
                return true;
            }

            DateTime now = DateTime.UtcNow;
            if (_combatGateTargetReleaseRetryAtUtc != DateTime.MinValue &&
                now < _combatGateTargetReleaseRetryAtUtc)
                return false;

            if (_combatGateTargetReleaseClickCount >= CombatGateTargetReleaseMaximumClicks)
            {
                CompleteCombatGateTargetRelease(targetId, "target still selected after bounded attempts", verified: false);
                return true;
            }

            if (!TryClickCombatGateClientCenter(token, out bool clickAttempted, out string failure))
            {
                if (clickAttempted)
                {
                    _combatGateTargetReleaseClickCount++;
                    if (_combatGateTargetReleaseClickCount >= CombatGateTargetReleaseMaximumClicks)
                    {
                        CompleteCombatGateTargetRelease(targetId, failure, verified: false);
                        return true;
                    }
                    _combatGateTargetReleaseRetryAtUtc = DateTime.UtcNow.AddMilliseconds(CombatGateTargetReleaseRetryDelayMs);
                }
                else if (!_combatGateTargetReleaseWaitLogged)
                {
                    _combatGateTargetReleaseWaitLogged = true;
                    _log($"[CombatGate] Target release deferred: {failure}.");
                }
                return false;
            }

            _combatGateTargetReleaseClickCount++;
            _combatGateTargetReleaseWaitLogged = false;
            int targetAfterClick = _memoryService.GetSelectedTargetId();
            CombatTargetValidation afterClick = _targetSafety.Validate();
            if (targetAfterClick != targetId || afterClick.TargetId != targetId)
            {
                CompleteCombatGateTargetRelease(targetId,
                    $"selection changed to {targetAfterClick}/{afterClick.Kind}", verified: true);
                return true;
            }

            if (_combatGateTargetReleaseClickCount < CombatGateTargetReleaseMaximumClicks)
            {
                _combatGateTargetReleaseRetryAtUtc = DateTime.UtcNow.AddMilliseconds(CombatGateTargetReleaseRetryDelayMs);
                return false;
            }

            CompleteCombatGateTargetRelease(targetId, "target still selected after bounded attempts", verified: false);
            return true;
        }

        private void CompleteCombatGateTargetRelease(int targetId, string detail, bool verified)
        {
            _combatGateTargetReleasePending = false;
            _combatGateTargetReleaseRetryAtUtc = DateTime.MinValue;
            _combatGateTargetReleaseVerified = verified;
            _combatGateSuppressionSelectionHandled = true;

            if (verified)
            {
                _log($"[CombatGate] Target release result: Cleared ({detail}).");
                _log($"[CombatGate] Suppressed target={targetId} for route recovery.");
            }
            else
            {
                _log($"[CombatGate] Target release FAILED: {detail}.");
                _log($"[CombatGate] Suppressed target={targetId} for route recovery despite unconfirmed release.");
            }
        }

        private bool TryClickCombatGateClientCenter(
            CancellationToken token,
            out bool clickAttempted,
            out string failure)
        {
            clickAttempted = false;
            failure = string.Empty;
            bool clickSent = false;
            bool localClickAttempted = false;
            string localFailure = string.Empty;

            lock (_inputLock)
            {
                if (token.IsCancellationRequested || Volatile.Read(ref _stopRequested) != 0)
                {
                    failure = "bot stopped or operation cancelled";
                    return false;
                }

                bool cursorActionRan = TryRunMovementCursorAction(() =>
                {
                    if (token.IsCancellationRequested || Volatile.Read(ref _stopRequested) != 0)
                    {
                        localFailure = "bot stopped or operation cancelled";
                        return;
                    }

                    if (!TryResolveCombatGateClientArea(
                            out nint hwnd, out int clientLeft, out int clientTop,
                            out int clientWidth, out int clientHeight))
                    {
                        localFailure = "game client is unavailable or not foreground";
                        return;
                    }

                    int centerX = clientLeft + clientWidth / 2;
                    int centerY = clientTop + clientHeight / 2;
                    bool leftDownAttempted = false;
                    bool leftDownCompleted = false;
                    bool leftUpCompleted = false;
                    bool cursorMoveAttempted = false;

                    try
                    {
                        cursorMoveAttempted = true;
                        MoveCombatGateMouseCursor(centerX, centerY);
                        if (!TryGetCombatGateCursorPosition(out int cursorX, out int cursorY) ||
                            cursorX != centerX || cursorY != centerY)
                        {
                            localClickAttempted = true;
                            localFailure = $"cursor did not reach client center ({centerX},{centerY})";
                            return;
                        }

                        if (!IsCombatGateClientForeground(hwnd))
                        {
                            localFailure = "game client lost foreground before click";
                            return;
                        }

                        if (token.IsCancellationRequested || Volatile.Read(ref _stopRequested) != 0)
                        {
                            localFailure = "bot stopped or operation cancelled";
                            return;
                        }
                        if (!IsCombatGateClientForeground(hwnd))
                        {
                            localFailure = "game client lost foreground before click";
                            return;
                        }

                        _log($"[CombatGate] LeftClick center client=({clientWidth / 2},{clientHeight / 2}) " +
                             $"screen=({centerX},{centerY})");
                        localClickAttempted = true;
                        leftDownAttempted = true;
                        SendCombatGateMouseEvent(MouseOperations.MouseEventFlags.LeftDown);
                        leftDownCompleted = true;
                        Thread.Sleep(50);
                    }
                    catch (Exception ex)
                    {
                        if (cursorMoveAttempted)
                            localClickAttempted = true;
                        localFailure = $"left-click input failed: {ex.Message}";
                    }
                    finally
                    {
                        if (leftDownAttempted)
                        {
                            try
                            {
                                SendCombatGateMouseEvent(MouseOperations.MouseEventFlags.LeftUp);
                                leftUpCompleted = true;
                            }
                            catch (Exception ex)
                            {
                                localFailure = string.IsNullOrEmpty(localFailure)
                                    ? $"left-button release failed: {ex.Message}"
                                    : $"{localFailure}; left-button release failed: {ex.Message}";
                                try
                                {
                                    SendCombatGateMouseEvent(MouseOperations.MouseEventFlags.LeftUp);
                                    leftUpCompleted = true;
                                }
                                catch { }
                            }
                        }
                    }
                    clickSent = leftDownCompleted && leftUpCompleted;
                    if (leftDownAttempted &&
                        _memoryService.GetSelectedTargetId() != _combatGateTargetReleaseTargetId &&
                        !_combatGateReturnHandoffReserved &&
                        TryBeginCombatRecoveryMovement("CombatGate target release"))
                    {
                        _combatGateReturnHandoffReserved = true;
                        _log("[CombatRecovery] CombatGate return handoff reserved after target release, before loot can reclaim input.");
                    }
                });

                if (!cursorActionRan)
                    localFailure = "mouse cursor is owned by the loot workflow";
            }

            clickAttempted = localClickAttempted;
            failure = localFailure;
            return clickSent;
        }

        /// <summary>
        /// Releases the killed target without invoking CombatGate's return handoff. The
        /// cursor ownership gate, client-area centre and physical cursor verification are
        /// shared with CombatGate, but this click has no route-recovery side effects.
        /// </summary>
        private bool TryClickPostKillClientCenter(
            CancellationToken token,
            out bool clickWasSent,
            out string failure)
        {
            clickWasSent = false;
            failure = string.Empty;
            bool clickCompleted = false;
            bool localClickWasSent = false;
            string localFailure = string.Empty;

            lock (_inputLock)
            {
                if (token.IsCancellationRequested || Volatile.Read(ref _stopRequested) != 0)
                {
                    failure = "bot stopped or operation cancelled";
                    return false;
                }

                bool cursorActionRan = TryRunMovementCursorAction(() =>
                {
                    if (token.IsCancellationRequested || Volatile.Read(ref _stopRequested) != 0)
                    {
                        localFailure = "bot stopped or operation cancelled";
                        return;
                    }

                    if (!TryResolveCombatGateClientArea(
                            out nint hwnd, out int clientLeft, out int clientTop,
                            out int clientWidth, out int clientHeight))
                    {
                        localFailure = "game client is unavailable or not foreground";
                        return;
                    }

                    int centerX = clientLeft + clientWidth / 2;
                    int centerY = clientTop + clientHeight / 2;
                    bool leftDownAttempted = false;
                    bool leftDownCompleted = false;
                    bool leftUpCompleted = false;
                    try
                    {
                        MoveCombatGateMouseCursor(centerX, centerY);
                        if (!TryGetCombatGateCursorPosition(out int cursorX, out int cursorY) ||
                            cursorX != centerX || cursorY != centerY)
                        {
                            localFailure = "cursor did not reach game client center";
                            return;
                        }

                        if (token.IsCancellationRequested || Volatile.Read(ref _stopRequested) != 0)
                        {
                            localFailure = "bot stopped or operation cancelled";
                            return;
                        }
                        if (!IsCombatGateClientForeground(hwnd))
                        {
                            localFailure = "game client lost foreground before click";
                            return;
                        }

                        leftDownAttempted = true;
                        SendCombatGateMouseEvent(MouseOperations.MouseEventFlags.LeftDown);
                        leftDownCompleted = true;
                        Thread.Sleep(50);
                    }
                    catch (Exception ex)
                    {
                        localFailure = $"left-click input failed: {ex.Message}";
                    }
                    finally
                    {
                        if (leftDownAttempted)
                        {
                            try
                            {
                                SendCombatGateMouseEvent(MouseOperations.MouseEventFlags.LeftUp);
                                leftUpCompleted = true;
                            }
                            catch (Exception ex)
                            {
                                localFailure = string.IsNullOrEmpty(localFailure)
                                    ? $"left-button release failed: {ex.Message}"
                                    : $"{localFailure}; left-button release failed: {ex.Message}";
                                try
                                {
                                    SendCombatGateMouseEvent(MouseOperations.MouseEventFlags.LeftUp);
                                    leftUpCompleted = true;
                                }
                                catch { }
                            }
                        }
                    }

                    localClickWasSent = leftDownCompleted;
                    clickCompleted = leftDownCompleted && leftUpCompleted;
                });

                if (!cursorActionRan)
                    localFailure = "mouse cursor is owned by the loot workflow";
            }

            clickWasSent = localClickWasSent;
            failure = localFailure;
            return clickCompleted;
        }

        private void ProcessPostKillTargetRelease(CancellationToken token)
        {
            int targetId = _postKillTargetReleaseId;
            if (targetId <= 0)
                return;

            if (token.IsCancellationRequested || Volatile.Read(ref _stopRequested) != 0)
                return;

            int selectedTargetId = _memoryService.GetSelectedTargetId();
            if (selectedTargetId != targetId)
            {
                _postKillTargetReleaseId = 0;
                _postKillTargetReleaseAttempts = 0;
                _postKillTargetReleaseRetryAtUtc = DateTime.MinValue;
                return;
            }

            if (_postKillTargetReleaseRetryAtUtc != DateTime.MinValue &&
                DateTime.UtcNow < _postKillTargetReleaseRetryAtUtc)
                return;

            if (_postKillTargetReleaseAttempts >= PostKillTargetReleaseMaximumClicks)
            {
                _log($"[PostKillTarget] LeftClick release failed target={targetId} after {PostKillTargetReleaseMaximumClicks} attempts; loot obligation preserved.");
                _postKillTargetReleaseId = 0;
                _postKillTargetReleaseAttempts = 0;
                _postKillTargetReleaseRetryAtUtc = DateTime.MinValue;
                return;
            }

            TryClickPostKillClientCenter(token, out bool clickWasSent, out string clickFailure);
            if (!clickWasSent)
            {
                // Focus loss / loot cursor ownership / unverified cursor are NOT clicks:
                // defer to the next tick and report the reason only once per episode.
                if (!_postKillTargetReleaseRefusalLogged)
                {
                    _postKillTargetReleaseRefusalLogged = true;
                    string detail = string.IsNullOrEmpty(clickFailure) ? "click not sent" : clickFailure;
                    _log($"[PostKillTarget] LeftClick deferred target={targetId} — {detail}; retrying next tick.");
                }
                return;
            }

            _postKillTargetReleaseRefusalLogged = false;
            _postKillTargetReleaseAttempts++;
            int targetAfterClick = _memoryService.GetSelectedTargetId();
            if (targetAfterClick != targetId)
            {
                _log("[PostKillTarget] LeftClick center — release verified");
                _postKillTargetReleaseId = 0;
                _postKillTargetReleaseAttempts = 0;
                _postKillTargetReleaseRetryAtUtc = DateTime.MinValue;
                return;
            }

            if (_postKillTargetReleaseAttempts >= PostKillTargetReleaseMaximumClicks)
            {
                _log($"[PostKillTarget] LeftClick release failed target={targetId} after {PostKillTargetReleaseMaximumClicks} attempts; loot obligation preserved.");
                _postKillTargetReleaseId = 0;
                _postKillTargetReleaseAttempts = 0;
                _postKillTargetReleaseRetryAtUtc = DateTime.MinValue;
                return;
            }

            _postKillTargetReleaseRetryAtUtc = DateTime.UtcNow.AddMilliseconds(PostKillTargetReleaseRetryDelayMs);
        }

        private void UpdateCombatGateTargetSuppression(float playerX, float playerY)
        {
            if (!IsCombatGateTargetSuppressionActive)
                return;

            Waypoint recoveryWaypoint = _combatGateSuppressedWaypoint;
            float reachThreshold = GetEffectiveWaypointReachThreshold(recoveryWaypoint);
            if (GeometryUtils.Distance(playerX, playerY, recoveryWaypoint.X, recoveryWaypoint.Y) <= reachThreshold)
                _combatGateSuppressionWaypointReached = true;

            if (_combatGateSuppressionWaypointReached)
            {
                bool routeMovedBeyondWaypoint = false;
                if (_waypoints.Count > 0)
                {
                    Waypoint current = _waypoints.Peek();
                    routeMovedBeyondWaypoint = current.IsOperationStep ||
                        Math.Abs(current.X - recoveryWaypoint.X) > 0.01f ||
                        Math.Abs(current.Y - recoveryWaypoint.Y) > 0.01f;
                }
                else if (_finalStandbyActive)
                {
                    routeMovedBeyondWaypoint =
                        Math.Abs(_finalStandbyX - recoveryWaypoint.X) > 0.01f ||
                        Math.Abs(_finalStandbyY - recoveryWaypoint.Y) > 0.01f;
                }

                if (routeMovedBeyondWaypoint)
                    _combatGateSuppressionRouteProgressed = true;
            }

            if (_combatGateSuppressionRouteProgressed &&
                (DateTime.UtcNow - _combatGateTargetDisengagedAtUtc).TotalSeconds >= CombatGateTargetSuppressionSeconds)
            {
                int releasedTarget = _combatGateSuppressedTargetId;
                ClearCombatGateTargetSuppression();
                _log($"[CombatGate] Target suppression expired after route progression and {CombatGateTargetSuppressionSeconds}s: target={releasedTarget}.");
            }
        }

        private void ClearCombatGateTargetSuppression()
        {
            _combatGateSuppressedTargetId = 0;
            _combatGateSuppressedWaypoint = default;
            _combatGateTargetDisengagedAtUtc = DateTime.MinValue;
            _combatGateSuppressionWaypointReached = false;
            _combatGateSuppressionRouteProgressed = false;
            _combatGateSuppressedTargetTabAttempted = false;
            _combatGateSuppressedTabSuppressionLogged = false;
            _combatGateSuppressionSelectionHandled = false;
            _combatGateTargetReleasePending = false;
            _combatGateTargetReleaseTargetId = 0;
            _combatGateTargetReleaseClickCount = 0;
            _combatGateTargetReleaseRetryAtUtc = DateTime.MinValue;
            _combatGateTargetReleaseWaitLogged = false;
            _combatGateTargetReleaseVerified = false;
        }

        /// <summary>
        /// Effective attack-disengage boundary of a waypoint. A non-positive or non-finite
        /// configured value falls back to the project default: an impossible boundary would
        /// otherwise turn the return into an endless start/finish loop instead of a bounded
        /// attempt that escalates through the existing navigation-failure path.
        /// </summary>
        internal static float GetEffectiveAttackDisengageDistance(Waypoint waypoint)
            => GetEffectiveAttackDisengageDistance((float)waypoint.AttackDisengageDistance);

        /// <summary>Effective attack-disengage boundary for a raw configured value.</summary>
        internal static float GetEffectiveAttackDisengageDistance(float configured)
        {
            if (float.IsNaN(configured) || float.IsInfinity(configured) || configured <= 0f)
                return Waypoint.DefaultAttackDisengageDistance;
            return configured;
        }

        /// <summary>
        /// Completion contract of the return: BOTH the waypoint's existing reach threshold AND
        /// the attack-disengage boundary must be satisfied. The boundary is reduced by a small
        /// safety margin so floating-point/coordinate noise cannot immediately re-trigger the
        /// CombatGate after W is released. Elapsed time and animation bytes are never proof of
        /// arrival.
        /// </summary>
        internal static bool IsCombatGateReturnComplete(
            float distanceToWaypoint,
            float manhattanDistanceToWaypoint,
            float reachThreshold,
            float attackDisengageDistance)
        {
            if (float.IsNaN(distanceToWaypoint) || float.IsNaN(manhattanDistanceToWaypoint))
                return false;

            if (distanceToWaypoint > reachThreshold)
                return false;

            float boundary = Math.Max(0f, GetEffectiveAttackDisengageDistance(attackDisengageDistance) -
                                           BotConstants.CombatGateReturn.CompletionMargin);
            return manhattanDistanceToWaypoint <= boundary;
        }

        /// <summary>
        /// One-line diagnostic context shared by every <c>[CombatGateReturn]</c> event. It only
        /// reads state that is already available for the tick; it never moves, scans or blocks.
        /// </summary>
        private string DescribeCombatGateReturnContext(float x, float y)
        {
            Waypoint waypoint = _waypoints.Count > 0 ? _waypoints.Peek() : default;
            float distance = _waypoints.Count > 0
                ? GeometryUtils.Distance(x, y, waypoint.X, waypoint.Y)
                : 0f;
            float manhattan = _waypoints.Count > 0
                ? GeometryUtils.ManhattanDistance(x, y, waypoint.X, waypoint.Y)
                : 0f;
            float disengage = _waypoints.Count > 0
                ? GetEffectiveAttackDisengageDistance(waypoint)
                : Waypoint.DefaultAttackDisengageDistance;

            bool lootInput = _lootNavigationBarrierHoldsInput || LootCollectionStillOwnsInput();
            bool recoveryInput = _recoveryOwnershipHeld ||
                                 _isUnstuckRoutineActive ||
                                 _reverseDiagonalRecovery.IsActive ||
                                 _isWaypointSpecialRecoveryActive ||
                                 _waypointMobRecovery?.IsActive == true;
            string camera = _wKeyCameraController == null
                ? "none"
                : _wKeyCameraController.IsAcceptingOperations
                    ? $"ready(owners={_wKeyCameraController.ActiveWOwnerCount})"
                    : $"blocked(owners={_wKeyCameraController.ActiveWOwnerCount})";

            return $"pos=({x:F1},{y:F1}) waypoint={(waypoint.X):F1},{(waypoint.Y):F1} distance={distance:F2} manhattan={manhattan:F1} " +
                   $"attackDisengage={disengage:F1} request={_lootBarrier.RequestId}/{_lootBarrier.Phase}/{_lootBarrier.Reason} " +
                   $"target={_combatGateReturnTargetId} targetReleased={_combatGateTargetReleaseVerified} " +
                   $"lootInput={lootInput} recoveryInput={recoveryInput} " +
                   $"camera={camera} w={_isMovingForward}";
        }

        /// <summary>
        /// True while the mandatory loot obligation still permits this exact return: the same
        /// Deferred PostKill generation, the same current waypoint, the same authorized handoff
        /// and an unchanged combat mode. A new request id, a terminal state, a waypoint change or
        /// a released handoff invalidates the return immediately.
        /// </summary>
        private bool IsCombatGateReturnAuthorizationValid(out string failure)
        {
            failure = string.Empty;

            if (!_combatGateReturnActive)
            {
                failure = "return operation is not active";
                return false;
            }

            if (!_combatRecoveryMovementAuthorized ||
                _combatRecoveryMovementRequestId != _combatGateReturnRequestId)
            {
                failure = "combat-recovery handoff is no longer held";
                return false;
            }

            if (!IsCombatRecoveryMovementStillValid())
            {
                failure = "authorization revalidation failed (request/target/focus/mode)";
                return false;
            }

            LootSystem? loot = LootSystemRef;
            if (loot == null ||
                !loot.TryGetMandatoryLootRequestStatus(_combatGateReturnRequestId, out LootRequestStatus status) ||
                status.State != LootRequestState.Deferred ||
                status.Reason != LootBarrierReason.PostKill ||
                !loot.IsCombatRecoveryMovementActiveFor(_combatGateReturnRequestId))
            {
                failure = "loot request generation/phase changed or handoff released";
                return false;
            }

            if (_waypoints.Count == 0)
            {
                failure = "waypoint queue is empty";
                return false;
            }

            Waypoint current = _waypoints.Peek();
            if (current.IsOperationStep ||
                !RequiresMandatoryLootBarrier(current.Mode) ||
                Math.Abs(current.X - _combatGateReturnWaypointX) > 0.01f ||
                Math.Abs(current.Y - _combatGateReturnWaypointY) > 0.01f)
            {
                failure = "current waypoint changed";
                return false;
            }

            if (IsAutomaticLootDisabled)
            {
                failure = "automatic loot was disabled";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Starts the dedicated return only for the exact interaction this operation exists for.
        /// The mandatory loot obligation is verified from the authoritative LootSystem snapshot
        /// and the physical handoff is then taken through the existing combat-recovery
        /// authorization, so the barrier contract itself is never bypassed.
        /// </summary>
        private bool TryStartCombatGateWaypointReturn(
            float currX,
            float currY,
            bool confirmedMonsterTarget,
            int targetId,
            out string denialReason)
        {
            denialReason = string.Empty;

            if (IsAutomaticLootDisabled)
            {
                denialReason = "automatic loot disabled by profile/path policy";
                return false;
            }

            if (_finalStandbyActive)
            {
                denialReason = "final-waypoint standby owns movement";
                return false;
            }

            if (_waypoints.Count == 0)
            {
                denialReason = "no current waypoint";
                return false;
            }

            Waypoint waypoint = _waypoints.Peek();
            if (waypoint.IsOperationStep)
            {
                denialReason = "current waypoint is an operation step";
                return false;
            }

            if (!RequiresMandatoryLootBarrier(waypoint.Mode))
            {
                denialReason = $"waypoint mode={waypoint.Mode} is not MoveAndAttackAndLoot";
                return false;
            }

            if (!_attackSuppressedForCurrentWaypoint)
            {
                denialReason = "CombatGate attack suppression is not active";
                return false;
            }

            if (IsMobGroupingSequenceActive)
            {
                denialReason = "mob grouping owns movement";
                return false;
            }

            float attackDisengage = GetEffectiveAttackDisengageDistance(waypoint);
            float manhattan = GeometryUtils.ManhattanDistance(currX, currY, waypoint.X, waypoint.Y);
            if (manhattan <= attackDisengage)
            {
                denialReason = $"waypoint is already inside the attack-disengage boundary (manhattan={manhattan:F1})";
                return false;
            }

            if (!_lootBarrier.IsActive ||
                _lootBarrier.Phase != LootBarrierPhase.Deferred ||
                _lootBarrier.Reason != LootBarrierReason.PostKill ||
                !_lootBarrier.TryGetRequestId(out long requestId))
            {
                denialReason = $"loot obligation is not a Deferred PostKill request (state={_lootBarrier.Phase} reason={_lootBarrier.Reason})";
                return false;
            }

            LootSystem? loot = LootSystemRef;
            if (loot == null)
            {
                denialReason = "no loot system attached";
                return false;
            }

            if (!loot.TryGetMandatoryLootRequestStatus(requestId, out LootRequestStatus status) ||
                status.State != LootRequestState.Deferred ||
                status.Reason != LootBarrierReason.PostKill)
            {
                denialReason = $"request {requestId} is not Deferred PostKill";
                return false;
            }

            bool capturedTargetForThisWaypoint =
                IsCombatGateTargetSuppressionActive &&
                !_combatGateTargetReleasePending &&
                Math.Abs(_combatGateSuppressedWaypoint.X - waypoint.X) <= 0.01f &&
                Math.Abs(_combatGateSuppressedWaypoint.Y - waypoint.Y) <= 0.01f;
            if (capturedTargetForThisWaypoint)
            {
                targetId = _combatGateSuppressedTargetId;
            }
            else if (targetId <= 0 || !confirmedMonsterTarget)
            {
                denialReason = $"selected target is not a confirmed monster (TargetId={targetId})";
                return false;
            }

            if (Volatile.Read(ref _stopRequested) != 0)
            {
                denialReason = "movement system is stopping";
                return false;
            }

            if (_gameUnresponsiveDetected)
            {
                denialReason = "game connection watchdog is active";
                return false;
            }

            if (_isCityStuckFatal || _navigationFailureEscalationRequested)
            {
                denialReason = "route failure escalation is active";
                return false;
            }

            if (_repotHelper.IsReportAndGoBackActive)
            {
                denialReason = "ReportAndGoBack teleport is active";
                return false;
            }

            if (_mapTransitionGuardActive || _mapTransitionConfirmed)
            {
                denialReason = "map transition hold is active";
                return false;
            }

            if (IsZoneBlocked)
            {
                denialReason = "zone restriction holds movement";
                return false;
            }

            if (_isUnstuckRoutineActive ||
                _reverseDiagonalRecovery.IsActive ||
                _isWaypointSpecialRecoveryActive ||
                _waypointMobRecovery?.IsActive == true ||
                _recoveryOwnershipHeld)
            {
                denialReason = "another recovery owns movement/input";
                return false;
            }

            if (LootCollectionStillOwnsInput() || loot.IsCollecting)
            {
                denialReason = "an in-flight loot collection owns movement/cursor";
                return false;
            }

            if (loot.IsScanActive || loot.IsPinkRescanActive)
            {
                denialReason = "an active loot scan owns the camera";
                return false;
            }

            if (!TryResolveCombatGateClientArea(out _, out _, out _, out _, out _))
            {
                denialReason = "game client is unavailable or not foreground";
                return false;
            }

            if (!(_combatGateReturnHandoffReserved &&
                  _combatRecoveryMovementAuthorized &&
                  _combatRecoveryMovementRequestId == requestId) &&
                !TryBeginCombatRecoveryMovement("CombatGate waypoint return"))
            {
                denialReason = "combat-recovery handoff refused (request/target/camera ownership)";
                return false;
            }

            DateTime now = DateTime.UtcNow;
            _combatGateReturnActive = true;
            _combatGateReturnHandoffReserved = false;
            _combatGateReturnRequestId = requestId;
            _combatGateReturnTargetId = targetId;
            _combatGateReturnWaypointX = waypoint.X;
            _combatGateReturnWaypointY = waypoint.Y;
            _combatGateReturnStartX = currX;
            _combatGateReturnStartY = currY;
            _combatGateReturnLastX = currX;
            _combatGateReturnLastY = currY;
            _combatGateReturnStartedAt = now;
            _combatGateReturnLastProgressAt = now;
            _combatGateReturnLastProgressLogAt = DateTime.MinValue;
            _combatGateReturnBestDistance = GeometryUtils.Distance(currX, currY, waypoint.X, waypoint.Y);
            _combatGateReturnInputDenials = 0;
            _combatGateReturnWAcceptedLogged = false;
            _combatGateReturnInputDeniedLogged = false;
            _combatGateReturnDeadlockSince = DateTime.MinValue;
            _combatGateReturnDeadlockDetail = string.Empty;
            _combatGateReturnDeadlockEscalated = false;

            // A fresh segment bypasses the camera filter so the very first return tick applies
            // the waypoint bearing immediately instead of waiting for hysteresis.
            ResetBearingState();

            _log("[CombatGateReturn] Starting waypoint recovery after target release.");
            _log($"[CombatGateReturn] START reason=CombatGateOvershootUnderDeferredPostKill {DescribeCombatGateReturnContext(currX, currY)}.");
            _log($"[CombatGateReturn] Authorization granted request={requestId} state={status.State} reason={status.Reason} " +
                 $"target={targetId} waypoint=({waypoint.X:F1},{waypoint.Y:F1}) " +
                 $"startPos=({currX:F1},{currY:F1}) startDistance={_combatGateReturnBestDistance:F2} " +
                 $"reachThreshold={GetEffectiveWaypointReachThreshold(waypoint):F2} attackDisengage={attackDisengage:F1}.");
            return true;
        }

        /// <summary>
        /// Tick-driven CombatGate return. Returns true when this operation owns the tick and
        /// ordinary navigation/combat must not run. It never advances the waypoint queue, never
        /// runs arrival operations, never resynchronizes the route, never attacks and never
        /// touches the mandatory loot obligation.
        /// </summary>
        private async Task<bool> TickCombatGateWaypointReturnUnderLootBarrierAsync(
            float currX,
            float currY,
            bool confirmedMonsterTarget,
            int targetId,
            CancellationToken token)
        {
            if (!_combatGateReturnActive)
            {
                if (!TryStartCombatGateWaypointReturn(currX, currY, confirmedMonsterTarget, targetId, out string denialReason))
                {
                    if (_combatGateReturnHandoffReserved)
                    {
                        _combatGateReturnHandoffReserved = false;
                        EndCombatRecoveryMovement($"CombatGate return did not start: {denialReason}");
                    }
                    ObserveCombatGateReturnDeadlock(currX, currY, confirmedMonsterTarget, targetId, denialReason, token);
                    return false;
                }
            }

            if (token.IsCancellationRequested)
            {
                // A cancelled tick must not leave this operation holding the handoff (or, later in
                // the tick, W). Release exactly its own ownership before propagating.
                CancelCombatGateWaypointReturn("operation cancelled");
                token.ThrowIfCancellationRequested();
            }

            DateTime now = DateTime.UtcNow;
            var (sampleX, sampleY, sampleRead) = _memoryService.GetPlayerPosition();
            if (!sampleRead)
            {
                // An unreadable position is never proof of movement: keep the last known one so
                // the stall window keeps running.
                sampleX = _combatGateReturnLastX;
                sampleY = _combatGateReturnLastY;
            }
            else
            {
                _combatGateReturnLastX = sampleX;
                _combatGateReturnLastY = sampleY;
            }

            // The selected monster was deliberately released before this return began. A
            // cleared selection is therefore expected; a different non-monster selection still
            // follows the established safety abort, but recovery itself never TABs or attacks.
            CombatTargetValidation validation = _targetSafety.Validate();
            if (validation.TargetId > 0 && !validation.IsConfirmedMonster)
            {
                string context = DescribeCombatGateReturnContext(sampleX, sampleY);
                CancelCombatGateWaypointReturn($"target rejected ({validation.Kind})");
                HandleRejectedCombatTarget(validation);
                _log($"[CombatGateReturn] ABORT reason=TargetRejected {validation.Describe()} {context}; " +
                     "no TAB, no attack, and no unsafe continuation.");
                return true;
            }

            if (!IsCombatGateReturnAuthorizationValid(out string authorizationFailure))
            {
                string context = DescribeCombatGateReturnContext(sampleX, sampleY);
                CancelCombatGateWaypointReturn($"authorization lost: {authorizationFailure}");
                _log($"[CombatGateReturn] ABORT reason=AuthorizationLost detail={authorizationFailure} {context}.");
                return true;
            }

            Waypoint waypoint = _waypoints.Peek();
            float distance = GeometryUtils.Distance(sampleX, sampleY, waypoint.X, waypoint.Y);
            float manhattan = GeometryUtils.ManhattanDistance(sampleX, sampleY, waypoint.X, waypoint.Y);
            float reachThreshold = GetEffectiveWaypointReachThreshold(waypoint);
            float attackDisengage = GetEffectiveAttackDisengageDistance(waypoint);
            double elapsedMs = (now - _combatGateReturnStartedAt).TotalMilliseconds;

            // ── Completion (real geometry only, never elapsed time or an animation byte) ──
            if (IsCombatGateReturnComplete(distance, manhattan, reachThreshold, attackDisengage))
            {
                CompleteCombatGateWaypointReturn(sampleX, sampleY, distance, manhattan, reachThreshold, attackDisengage, elapsedMs);
                return true;
            }

            // ── Meaningful progress: only real waypoint-directed improvement counts ──
            if (_combatGateReturnBestDistance - distance >= BotConstants.CombatGateReturn.MeaningfulProgressUnits)
            {
                float improvement = _combatGateReturnBestDistance - distance;
                _combatGateReturnBestDistance = distance;
                _combatGateReturnLastProgressAt = now;
                _combatGateReturnInputDenials = 0;

                if ((now - _combatGateReturnLastProgressLogAt).TotalMilliseconds >=
                    BotConstants.CombatGateReturn.ProgressLogIntervalMs)
                {
                    _combatGateReturnLastProgressLogAt = now;
                    _log($"[CombatGateReturn] PROGRESS improvement={improvement:F2} bestDistance={distance:F2} " +
                         $"elapsed={(now - _combatGateReturnStartedAt).TotalMilliseconds:F0}ms " +
                         $"request={_combatGateReturnRequestId} target={_combatGateReturnTargetId} " +
                         $"pos=({sampleX:F1},{sampleY:F1}) waypoint=({waypoint.X:F1},{waypoint.Y:F1}) " +
                         $"attackDisengage={attackDisengage:F1}.");
                }
            }

            double sinceProgressMs = (now - _combatGateReturnLastProgressAt).TotalMilliseconds;
            if (sinceProgressMs >= BotConstants.CombatGateReturn.StallTimeoutMs)
            {
                await FailCombatGateWaypointReturnAsync(
                    "STALLED",
                    $"no waypoint-directed progress for {sinceProgressMs:F0}ms (limit {BotConstants.CombatGateReturn.StallTimeoutMs}ms)",
                    sampleX, sampleY, distance, manhattan, reachThreshold, attackDisengage, elapsedMs, token);
                return true;
            }

            if (elapsedMs >= BotConstants.CombatGateReturn.AttemptBudgetMs)
            {
                await FailCombatGateWaypointReturnAsync(
                    "STALLED",
                    $"attempt budget exhausted after {elapsedMs:F0}ms (limit {BotConstants.CombatGateReturn.AttemptBudgetMs}ms)",
                    sampleX, sampleY, distance, manhattan, reachThreshold, attackDisengage, elapsedMs, token);
                return true;
            }

            // ── Authorized camera + forward input ──
            // ApplyCameraBearing is the existing authorized-recovery camera path: it refuses to
            // write while the loot barrier holds input and no valid combat-recovery authorization
            // exists. W is requested through TryStartMoving, which is the only place that can
            // press it and reports refusal instead of pretending the character moved.
            float bearing = GeometryUtils.GetBearingToTargetDeg(sampleX, sampleY, waypoint.X, waypoint.Y);
            ApplyCameraBearing(bearing);

            if (!_isMovingForward)
            {
                bool inputAccepted = TryStartMoving();
                if (!inputAccepted)
                {
                    _combatGateReturnInputDenials++;
                    if (!_combatGateReturnInputDeniedLogged)
                    {
                        _combatGateReturnInputDeniedLogged = true;
                        _log($"[CombatGateReturn] INPUT_DENIED {DescribeCombatGateReturnContext(sampleX, sampleY)} " +
                             $"elapsed={elapsedMs:F0}ms denials={_combatGateReturnInputDenials}/{BotConstants.CombatGateReturn.MaximumInputDenials} " +
                             $"waypoint=({waypoint.X:F1},{waypoint.Y:F1}) attackDisengage={attackDisengage:F1} request={_combatGateReturnRequestId} " +
                             $"target={_combatGateReturnTargetId}.");
                    }

                    if (_combatGateReturnInputDenials >= BotConstants.CombatGateReturn.MaximumInputDenials)
                    {
                        await FailCombatGateWaypointReturnAsync(
                            "INPUT_DENIED",
                            $"forward input was refused {_combatGateReturnInputDenials} times (camera/ownership/vertical-lock)",
                            sampleX, sampleY, distance, manhattan, reachThreshold, attackDisengage, elapsedMs, token);
                        return true;
                    }
                }
                else if (!_combatGateReturnWAcceptedLogged)
                {
                    _combatGateReturnWAcceptedLogged = true;
                    _log($"[CombatGateReturn] W accepted bearing={bearing:F1}deg pos=({sampleX:F1},{sampleY:F1}) " +
                         $"waypoint=({waypoint.X:F1},{waypoint.Y:F1}) distance={distance:F2} " +
                         $"attackDisengage={attackDisengage:F1} request={_combatGateReturnRequestId} target={_combatGateReturnTargetId}.");
                }
            }
            else
            {
                _combatGateReturnInputDenials = 0;
            }

            return true;
        }

        /// <summary>
        /// Successful return: stop input, release the handoff, clear the CombatGate latch and let
        /// the ordinary tick pipeline resume. The waypoint stays current and the mandatory loot
        /// request keeps its id and Deferred state (only a camera-verified scan may finish it).
        /// </summary>
        private void CompleteCombatGateWaypointReturn(
            float x,
            float y,
            float distance,
            float manhattan,
            float reachThreshold,
            float attackDisengage,
            double elapsedMs)
        {
            long requestId = _combatGateReturnRequestId;
            float waypointX = _combatGateReturnWaypointX;
            float waypointY = _combatGateReturnWaypointY;
            int targetId = _combatGateReturnTargetId;

            // 1. Stop forward movement.
            StopMoving();
            // 2. Release any turn input owned by this recovery.
            ReleaseTurnKey("CombatGate waypoint return reached");
            // 3./4. Release the handoff and clear the dedicated return state. The success path
            // has already stopped W/turn input above and must not emit an ABORT event.
            CancelCombatGateWaypointReturn("reached waypoint", releaseInput: false, logAbort: false);

            _combatGateReturnFailedAttempts = 0;
            _combatGateReturnDeadlockSince = DateTime.MinValue;
            _combatGateReturnDeadlockDetail = string.Empty;
            _combatGateReturnDeadlockEscalated = false;

            // 5./6. Clear the gate latch and reset the combat handler for reacquisition.
            _attackSuppressedForCurrentWaypoint = false;
            _combatGateSuppressionWaypointReached = true;
            _combatHandler.ResetState();
            ClearCombatRetargetSearch();
            _resumeCombatSkillAfterGrouping = false;

            _log($"[CombatGateReturn] REACHED waypoint pos=({x:F1},{y:F1}) waypoint=({waypointX:F1},{waypointY:F1}) " +
                 $"distance={distance:F2} reachThreshold={reachThreshold:F2} manhattan={manhattan:F1} " +
                 $"attackDisengage={attackDisengage:F1} elapsed={elapsedMs:F0}ms request={requestId} target={targetId}.");
            _log($"[CombatGateReturn] COMPLETE request={requestId} state=Deferred waypoint=({waypointX:F1},{waypointY:F1}) " +
                 $"elapsed={elapsedMs:F0}ms — W released, handoff released, CombatGate cleared; " +
                 "waypoint unchanged and loot obligation preserved (camera-verified completion still required).");
            _log("[CombatGate] Recovery complete; pending loot handling continues.");
        }

        /// <summary>
        /// Bounded failure path of the return: stop W, release everything this operation owns,
        /// keep the loot obligation, capture diagnostics and hand the situation to the project's
        /// existing recovery/escalation chain. No fake completion is ever produced.
        /// </summary>
        private async Task FailCombatGateWaypointReturnAsync(
            string kind,
            string detail,
            float x,
            float y,
            float distance,
            float manhattan,
            float reachThreshold,
            float attackDisengage,
            double elapsedMs,
            CancellationToken token)
        {
            long requestId = _combatGateReturnRequestId;
            float waypointX = _combatGateReturnWaypointX;
            float waypointY = _combatGateReturnWaypointY;
            int targetId = _combatGateReturnTargetId;
            float bestDistance = _combatGateReturnBestDistance;
            long lootStateCode = (long)_lootBarrier.Phase;
            string lootState = $"{_lootBarrier.Phase}/{_lootBarrier.Reason}";

            // Stop W and the turn keys on the spot, BEFORE handing the situation to the
            // recovery chain: the unstuck cannot always start (suspended by the loot hold,
            // collection still owns input, cancelled token), and a failed attempt must never
            // leave forward input held. Release only what this operation owns.
            CancelCombatGateWaypointReturn(kind == "INPUT_DENIED"
                ? "forward input denied"
                : "no waypoint-directed progress", releaseInput: true);

            _combatGateReturnFailedAttempts++;
            int attempts = _combatGateReturnFailedAttempts;

            _log($"[CombatGateReturn] {kind} pos=({x:F1},{y:F1}) waypoint=({waypointX:F1},{waypointY:F1}) " +
                 $"distance={distance:F2} reachThreshold={reachThreshold:F2} manhattan={manhattan:F1} " +
                 $"attackDisengage={attackDisengage:F1} request={requestId} loot={lootState} target={targetId} " +
                 $"startPos=({_combatGateReturnStartX:F1},{_combatGateReturnStartY:F1}) elapsed={elapsedMs:F0}ms " +
                 $"bestDistance={bestDistance:F2} attempt={attempts}/{BotConstants.CombatGateReturn.MaximumFailedAttempts} detail={detail}.");

            CaptureCombatGateReturnFailureScreenshot();

            if (attempts >= BotConstants.CombatGateReturn.MaximumFailedAttempts)
            {
                _combatGateReturnFailedAttempts = 0;
                _log($"[CombatGateReturn] ABORT reason=RepeatedFailures attempts={attempts} request={requestId} " +
                     $"lootStateCode={lootStateCode} — escalating through the existing navigation-failure path " +
                     "instead of retrying the return indefinitely.");
                EscalateNavigationFailure(
                    $"CombatGate waypoint return failed {attempts} times for waypoint ({waypointX:F1},{waypointY:F1}) under request={requestId}",
                    token);
                return;
            }

            _log($"[CombatGateReturn] UNSTUCK attempt={attempts} — starting the standard combat unstuck (reverse-diagonal recovery).");
            StartCombatUnstuck(x, y);
            await Task.Delay(BotConstants.CombatGateReturn.FailureSettleMs, CancellationToken.None);
        }

        /// <summary>
        /// Idempotent cleanup of the return operation. It releases only the input and handoff
        /// this operation actually holds: the mandatory loot request keeps its id and its
        /// Deferred state, the waypoint queue is untouched and no other subsystem's recovery is
        /// cancelled.
        /// </summary>
        private void CancelCombatGateWaypointReturn(string reason, bool releaseInput = true, bool logAbort = true)
        {
            if (!_combatGateReturnActive)
                return;

            long requestId = _combatGateReturnRequestId;
            float waypointX = _combatGateReturnWaypointX;
            float waypointY = _combatGateReturnWaypointY;
            int targetId = _combatGateReturnTargetId;
            double elapsedMs = _combatGateReturnStartedAt == DateTime.MinValue
                ? 0
                : (DateTime.UtcNow - _combatGateReturnStartedAt).TotalMilliseconds;
            bool wWasHeld = _isMovingForward;

            _combatGateReturnActive = false;
            _combatGateReturnRequestId = 0;
            _combatGateReturnTargetId = 0;
            _combatGateReturnWaypointX = 0f;
            _combatGateReturnWaypointY = 0f;
            _combatGateReturnStartedAt = DateTime.MinValue;
            _combatGateReturnLastProgressAt = DateTime.MinValue;
            _combatGateReturnLastProgressLogAt = DateTime.MinValue;
            _combatGateReturnBestDistance = float.MaxValue;
            _combatGateReturnInputDenials = 0;
            _combatGateReturnWAcceptedLogged = false;
            _combatGateReturnInputDeniedLogged = false;

            if (releaseInput)
            {
                StopMoving();
                ReleaseTurnKey($"CombatGate waypoint return released: {reason}");
            }

            EndCombatRecoveryMovement($"CombatGate waypoint return released: {reason}");

            if (logAbort)
            {
                _log($"[CombatGateReturn] ABORT reason={reason} request={requestId} waypoint=({waypointX:F1},{waypointY:F1}) " +
                     $"target={targetId} elapsed={elapsedMs:F0}ms wHeld={wWasHeld} pos=({_combatGateReturnLastX:F1},{_combatGateReturnLastY:F1}) — " +
                     "input and handoff released, loot obligation preserved.");
            }
        }

        /// <summary>
        /// Narrow deadlock monitor for exactly this interaction: CombatGate active, Deferred
        /// PostKill obligation, captured target release complete, no combat action, no waypoint-directed
        /// movement and no other valid recovery. When the handoff cannot be obtained the precise
        /// reason is logged once (never per tick); if the same state persists beyond the bounded
        /// window, diagnostics are captured and the established route-failure escalation runs.
        /// The loot obligation is never released by a timeout.
        /// </summary>
        private void ObserveCombatGateReturnDeadlock(
            float currX,
            float currY,
            bool confirmedMonsterTarget,
            int targetId,
            string denialReason,
            CancellationToken token)
        {
            bool otherRecoveryActive = _isUnstuckRoutineActive ||
                                       _reverseDiagonalRecovery.IsActive ||
                                       _isWaypointSpecialRecoveryActive ||
                                       _waypointMobRecovery?.IsActive == true ||
                                       _recoveryOwnershipHeld;

            bool exactDeadlock =
                !IsAutomaticLootDisabled &&
                !_finalStandbyActive &&
                _attackSuppressedForCurrentWaypoint &&
                _waypoints.Count > 0 &&
                !_waypoints.Peek().IsOperationStep &&
                RequiresMandatoryLootBarrier(_waypoints.Peek().Mode) &&
                _lootBarrier.IsActive &&
                _lootBarrier.Phase == LootBarrierPhase.Deferred &&
                _lootBarrier.Reason == LootBarrierReason.PostKill &&
                ((confirmedMonsterTarget && targetId > 0) ||
                 (IsCombatGateTargetSuppressionActive &&
                  !_combatGateTargetReleasePending &&
                  Math.Abs(_combatGateSuppressedWaypoint.X - _waypoints.Peek().X) <= 0.01f &&
                  Math.Abs(_combatGateSuppressedWaypoint.Y - _waypoints.Peek().Y) <= 0.01f)) &&
                !otherRecoveryActive;

            if (!exactDeadlock)
            {
                _combatGateReturnDeadlockSince = DateTime.MinValue;
                _combatGateReturnDeadlockDetail = string.Empty;
                _combatGateReturnDeadlockEscalated = false;
                return;
            }

            DateTime now = DateTime.UtcNow;
            string detail = denialReason ?? string.Empty;

            if (_combatGateReturnDeadlockSince == DateTime.MinValue)
            {
                _combatGateReturnDeadlockSince = now;
                _combatGateReturnDeadlockDetail = detail;
                _log($"[CombatGateReturn] ABORT reason=HandoffUnavailable detail={detail} " +
                     $"wAccepted={_isMovingForward} {DescribeCombatGateReturnContext(currX, currY)} — " +
                     "the deadlock is monitored and will escalate if it persists.");
                return;
            }

            if (!string.Equals(_combatGateReturnDeadlockDetail, detail, StringComparison.Ordinal))
            {
                _combatGateReturnDeadlockDetail = detail;
                _log($"[CombatGateReturn] ABORT reason=HandoffUnavailable detail={detail} " +
                     $"wAccepted={_isMovingForward} {DescribeCombatGateReturnContext(currX, currY)}.");
            }

            double persistedMs = (now - _combatGateReturnDeadlockSince).TotalMilliseconds;
            if (!_combatGateReturnDeadlockEscalated &&
                persistedMs >= BotConstants.CombatGateReturn.DeadlockDiagnosticWindowMs)
            {
                _combatGateReturnDeadlockEscalated = true;
                _log($"[CombatGateReturn] ABORT reason=DeadlockWindowExpired persisted={persistedMs:F0}ms " +
                     $"detail={detail} {DescribeCombatGateReturnContext(currX, currY)} — capturing diagnostics and escalating.");
                CaptureCombatGateReturnFailureScreenshot();
                EscalateNavigationFailure(
                    $"CombatGate return recovery could not obtain the movement handoff for {persistedMs:F0}ms (request={_lootBarrier.RequestId})",
                    token);
            }
        }

        private string DescribeCurrentWaypoint()
        {
            if (_finalStandbyActive)
                return $"standby ({_finalStandbyX:F1},{_finalStandbyY:F1})";
            if (_waypoints.Count > 0)
            {
                Waypoint waypoint = _waypoints.Peek();
                return $"({waypoint.X:F1},{waypoint.Y:F1})";
            }
            return "none";
        }

        /// <summary>
        /// Periodic TAB cycling must not interrupt a running mandatory loot cycle. The
        /// explicit TabAfterKill action has already performed the immediate retarget
        /// check; later timer-driven TabTarget ticks are suppressed until loot completes.
        /// Ordinary TabTarget while travelling is never treated as a combat encounter.
        /// </summary>
        private bool ShouldSuppressPeriodicTargetCycle(CombatAction action)
        {
            if (IsAutomaticLootDisabled)
                return false;

            if (action != CombatAction.TabTarget ||
                CurrentMode != BotMode.MoveAndAttackAndLoot ||
                !_lootBarrier.IsActive)
                return false;

            if (!_lootTargetCycleSuppressedLogged)
            {
                _lootTargetCycleSuppressedLogged = true;
                _log("[LootBarrier] Periodic TAB target cycle suppressed while mandatory loot runs.");
            }
            return true;
        }

        internal static bool CanRunRouteResyncAfterCombat(bool pending, bool lootBarrierBlocked)
            => pending && !lootBarrierBlocked;

        public async Task Update(CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                // A cancelled tick must never leave the CombatGate return holding W, the turn
                // keys or the loot handoff; release exactly its own ownership before cancelling.
                CancelCombatGateWaypointReturn("operation cancelled");
                EndCombatRecoveryMovement("operation cancelled");
                ClearCombatGateTargetSuppression();
                token.ThrowIfCancellationRequested();
            }

            if (Volatile.Read(ref _stopRequested) != 0)
            {
                StopMoving();
                return;
            }

            if (_gameUnresponsiveDetected)
                return;

            // Once the workflow city-stuck escalation fires, this MovementSystem must
            // never move or start another recovery again. PathRunner observes the flag
            // immediately after this Update and aborts the route.
            if (_isCityStuckFatal)
            {
                CancelCombatGateWaypointReturn("city stuck fatal");
                EndCombatRecoveryMovement("city stuck fatal");
                StopMoving();
                ReleaseCombatKeys();
                return;
            }

            // A profile/path disable policy must release stale loot ownership BEFORE any
            // navigation, combat, standby or recovery code can observe it. Keeping the
            // attached LootSystem in sync here also closes the gap between a waypoint
            // transition and the host's next loot-loop tick.
            SynchronizeAutomaticLootPolicy();
            ApplyAutomaticLootDisabledPolicy();

            _tickCount++;

            // The first-step turn holds A/D across ticks while W stays released. If the
            // previous tick did not reach the movement section (combat, loot, recovery,
            // pause, ...), release the turn keys here so they can never leak into that
            // action. The next movement tick re-acquires them and continues the turn.
            if (_initialHeadingAlignmentPending &&
                _heldTurnKey != TurnKeyState.None &&
                _tickCount - _initialHeadingAlignmentLastTick > 1)
            {
                ReleaseTurnKey("first-step alignment interrupted");
                _initialHeadingAlignmentInProgress = false;
            }

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

            // ── EXP route map invariant (highest movement priority) ──
            // A confirmed unexpected map change must stop navigation, combat and
            // recovery before any loot/combat/recovery routine below can delay it.
            // ExpectedMap = 0 keeps the legacy multi-map travel behavior untouched.
            if (ExpectedMap > 0 && !EvaluateRouteMapGuard())
                return;

            if (_isWaypointSpecialRecoveryActive || _waypointMobRecovery?.IsActive == true)
                CancelMobGrouping("waypoint special recovery owns control");

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
                CancelMobGrouping("ReportAndGoBack/teleport transition");
                if (_isMovingForward || _isSkillThreeHeld || _isUnstuckRoutineActive ||
                    _reverseDiagonalRecovery.IsActive || _consecutiveStuckAttempts != 0 ||
                    _postKillRetargetRequestId > 0 || _postKillTargetReleaseId > 0 ||
                    _combatRetargetCameraStage != CombatRetargetCameraStage.None)
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
            // After the in-city stuck limit (2 attempts), the bot presses 6 and waits 10 minutes.
            if (DateTime.Now < _inCityStuckCooldownUntil)
            {
                CancelCombatGateWaypointReturn("in-city teleport cooldown");
                CancelMobGrouping("in-city teleport cooldown");
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
                    CancelCombatGateWaypointReturn("zone restriction hold");
                    CancelMobGrouping("zone restriction hold");
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
                CancelMobGrouping("Repot transition");
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
                _pinkScanHoldActive = false;
                _pinkScanHoldSince = DateTime.MinValue;
                _pinkScanHoldTimedOut = false;
                _postPinkScanTargetCheckPending = false;
                if (LootSystemRef != null && (LootSystemRef.IsCollecting || LootSystemRef.IsLootingActive))
                {
                    if (_tickCount % _stateLogInterval == 0)
                        _log($"[Tick {_tickCount}] OnlyMove active — ignoring loot (mandatory move, no loot/attack).");
                }
            }
            else if (!IsAutomaticLootDisabled)
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
                CancelCombatGateWaypointReturn("loot collection owns input");
                CancelMobGrouping("loot collection hold");
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

            // ── Post-kill SOD/SOP pink-scan hold (MoveAndAttack) ──
            // The close-camera TAB check runs first. If it found no nearby mob, the
            // loot system scans for SOD/SOP drops and repeats until no pink pixels are
            // left; combat and waypoint movement stay suspended for that whole scan.
            if (LootSystemRef != null && LootSystemRef.IsPinkScanPendingOrActive)
            {
                if (_pinkScanHoldSince == DateTime.MinValue)
                    _pinkScanHoldSince = DateTime.UtcNow;

                // Never resume combat on a timer while pink loot is still pending.
                if (LootSystemRef.IsPinkScanPendingOrActive)
                {
                    CancelCombatGateWaypointReturn("SOD/SOP pink scan owns the camera");
                    CancelMobGrouping("SOD/SOP pink-scan hold");
                    if (!_pinkScanHoldActive)
                    {
                        _pinkScanHoldActive = true;
                        _combatHandler.ResetState();
                        _log($"[Tick {_tickCount}] SOD/SOP pink scan — pausing combat/movement until the pink loot check finishes.");
                    }
                    ReleaseSkillThree();
                    StopMoving();
                    ClearCombatRetargetSearch();
                    await Task.Delay(BotConstants.Delays.LootUpdateMs, token);
                    return;
                }

            }
            else
            {
                if (_pinkScanHoldActive)
                    _postPinkScanTargetCheckPending = true;
                _pinkScanHoldSince = DateTime.MinValue;
                _pinkScanHoldTimedOut = false;
                _pinkScanHoldActive = false;
            }

            // ── Loot-priority post-kill hold ──
            // In loot priority mode, after a mob dies (mob selected → no target) the loot
            // machine loots until a full scan pass finds no more items (IsLootingActive).
            // During that phase the bot must NOT select the next target (no TAB / no
            // attack) and must NOT move to the next waypoint — loot is the priority:
            // targeting and movement wait until the loot scan is finished and no loot
            // is found.
            if (LootPriorityMode && LootSystemRef != null && LootSystemRef.IsLootingActive)
            {
                CancelCombatGateWaypointReturn("loot-priority hold");
                CancelMobGrouping("loot-priority hold");
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
                _connectionWatchdog.Reset();
                CancelMobGrouping("map/teleport transition");
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
            if (AllowSpeedPotionBuff && _combatHandler.CheckAttackSpeed(_memoryService))
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
                _connectionWatchdog.Reset();
                if (_positionReadFailureSince == DateTime.MinValue)
                {
                    _positionReadFailureSince = DateTime.UtcNow;
                    _log($"[Tick {_tickCount}] [Pos] Read failed; starting connection-safety timeout.");
                }
                else if ((DateTime.UtcNow - _positionReadFailureSince).TotalMilliseconds >=
                         BotConstants.Connection.PositionReadFailureTimeoutMs)
                {
                    StopForUnresponsiveGame($"Player position memory reads have failed for {BotConstants.Connection.PositionReadFailureTimeoutMs / 1000.0:F0} seconds; the game may be disconnected or no longer in a playable state.");
                }
                return;
            }
            _positionReadFailureSince = DateTime.MinValue;

            // If movement/recovery input has been active but the server-side position
            // has not changed for a sustained period, the client may be disconnected.
            // Stop before running another combat, loot, or recovery action.
            bool movementExpected = _isMovingForward || _isUnstuckRoutineActive || _reverseDiagonalRecovery.IsActive;
            if (_connectionWatchdog.Observe(movementExpected, currX, currY, DateTime.UtcNow))
            {
                string reason = $"Player position did not advance for {BotConstants.Connection.NoMovementTimeoutMs / 1000.0:F0} seconds while movement was being requested; the game may be disconnected or unresponsive.";
                StopForUnresponsiveGame(reason);
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

            // Apply the optional per-waypoint start camera before any combat/movement
            // path in this tick can press W. Route resync above may have selected a
            // different first waypoint, so this runs after it.
            if (_waypoints.Count > 0)
            {
                await EnsureWaypointStartCameraAngleAsync(_waypoints.Peek(), token);
            }

            byte currentAction = _memoryService.GetCurrentAction();
            int targetId = _memoryService.GetSelectedTargetId();

            // Raw "some entity is selected" indicator: kept for the existing combat/loot
            // timing logic. It is NOT proof of a monster (a real player was selected with
            // TargetId=718 on 2026-10-29), so it is never used to authorize an attack.
            bool mobSelected = _memoryService.IsMobSelected();

            // Central, single-source target classification for this tick. Only a confirmed
            // monster may be attacked; players/NPCs/unknown targets never are.
            CombatTargetValidation targetValidation = targetId > 0
                ? _targetSafety.Validate()
                : default;
            bool confirmedMonsterTarget = targetValidation.IsConfirmedMonster;
            UpdateCombatGateTargetSuppression(currX, currY);

            // Log position every 5 ticks (action bytes included — key for stuck diagnosis:
            // 25=idle, 27/3=running, 28=being hit, 39=attacking).
            // Mob: is the CONFIRMED monster state; Raw: the raw selected-entity indicator.
            if (_tickCount % 5 == 0)
            {
                _log($"[Tick {_tickCount}] @ ({currX:F1},{currY:F1}) Act:{currentAction} Mob:{confirmedMonsterTarget} Raw:{mobSelected} TargetId:{targetId} Type:{targetValidation.Kind} Cam:{_memoryService.GetCameraAngle()}");
            }

            BotMode currentMode = BotMode.OnlyMove;
            float manhattanDistanceToTarget = 0f;

            // Refresh and release a killed target before standby bounds or ordinary
            // combat evaluation can issue input. This path deliberately does not use the
            // CombatGate release helper, whose click reserves a route-return handoff.
            RefreshLootBarrier();
            if (_postKillTargetReleaseId > 0)
            {
                ProcessPostKillTargetRelease(token);
                return;
            }

            if (_finalStandbyActive && _lootBarrier.IsBlockingNavigation &&
                !confirmedMonsterTarget && !_combatGateReturnActive)
            {
                if (await HoldNavigationForMandatoryLootAsync(token))
                    return;
            }

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
                if (distFromFinal > _finalStandbyAtkDis + 2f && !IsMobGroupingSequenceActive)
                {
                    _log($"[Standby] Outside AtkDis ({distFromFinal:F1} > {_finalStandbyAtkDis}) — returning to final waypoint.");
                    MoveTowards(currX, currY, _finalStandbyX, _finalStandbyY, ResolveSteeringMode(_finalStandbySteeringMode, _steeringMode));
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
                if (IsMobGroupingSequenceActive &&
                    !MobGroupingController.IsSupportedCombatMode(CurrentMode))
                    CancelMobGrouping($"effective mode changed to {CurrentMode}");
                goto RUN_COMBAT_ONLY;
            }

            if (_waypoints.Count > 0)
            {
                var currentWaypoint = _waypoints.Peek();
                currentMode = currentWaypoint.Mode;
                manhattanDistanceToTarget = GeometryUtils.ManhattanDistance(currX, currY, currentWaypoint.X, currentWaypoint.Y);

                bool isAttackMode = currentWaypoint.Mode == BotMode.MoveAndAttack || currentWaypoint.Mode == BotMode.MoveAndAttackAndLoot;
                bool targetAlreadySuppressed = IsCombatGateTargetSuppressionActive &&
                                               targetId == _combatGateSuppressedTargetId;
                if (isAttackMode && manhattanDistanceToTarget > currentWaypoint.AttackDisengageDistance &&
                    !IsMobGroupingSequenceActive)
                {
                    if (!_attackSuppressedForCurrentWaypoint)
                    {
                        _attackSuppressedForCurrentWaypoint = true;
                        _combatHandler.ResetState();
                        ReleaseSkillThree();
                        ClearCombatRetargetSearch();
                        if (confirmedMonsterTarget && targetId > 0 && !targetAlreadySuppressed)
                        {
                            _log($"[CombatGate] Overshoot d:{manhattanDistanceToTarget:F1} > {currentWaypoint.AttackDisengageDistance:F1} at ({currentWaypoint.X:F1},{currentWaypoint.Y:F1}).");
                            _log("[CombatGate] Overshoot detected. Releasing combat target.");
                            _log($"[CombatGate] Target before release: {targetId}");
                            BeginCombatGateTargetSuppression(targetId, currentWaypoint);
                        }
                        else
                        {
                            _log($"[CombatGate] Overshoot d:{manhattanDistanceToTarget:F1} > {currentWaypoint.AttackDisengageDistance:F1} at ({currentWaypoint.X:F1},{currentWaypoint.Y:F1}) — attack disabled until waypoint is reached.");
                        }
                    }

                    currentMode = BotMode.OnlyMove;
                }
                else if (_attackSuppressedForCurrentWaypoint && isAttackMode &&
                         !IsMobGroupingSequenceActive)
                {
                    if (confirmedMonsterTarget && targetId > 0 && !targetAlreadySuppressed)
                    {
                        _combatHandler.ResetState();
                        ReleaseSkillThree();
                        ClearCombatRetargetSearch();
                        _log("[CombatGate] Monster selected while disengaged; releasing combat target.");
                        _log($"[CombatGate] Target before release: {targetId}");
                        BeginCombatGateTargetSuppression(targetId, currentWaypoint);
                    }
                    currentMode = BotMode.OnlyMove;
                }

                if (_combatGateTargetReleasePending && !ProcessCombatGateTargetRelease(token))
                    return;

                if (IsCombatGateTargetSuppressionActive && targetId == _combatGateSuppressedTargetId)
                {
                    if (!_combatGateSuppressionSelectionHandled)
                    {
                        _log($"[CombatGate] Target {targetId} skipped: disengagement suppression active.");
                        ReleaseSkillThree();
                        _combatHandler.ResetState();
                        ClearCombatRetargetSearch();
                        BeginCombatGateTargetRelease(targetId);
                        if (!ProcessCombatGateTargetRelease(token))
                            return;

                        // The local target snapshot predates the click. Do not let this same tick
                        // feed that stale selection to CombatHandler or start another TAB cycle.
                        return;
                    }

                    currentMode = BotMode.OnlyMove;
                }
                else if (IsCombatGateTargetSuppressionActive)
                {
                    _combatGateSuppressionSelectionHandled = false;
                }

                if (IsMobGroupingSequenceActive &&
                    (!MobGroupingController.IsSupportedCombatMode(CurrentMode) ||
                     !MobGroupingController.IsSupportedCombatMode(currentMode)))
                    CancelMobGrouping($"effective combat mode changed to {CurrentMode}");

                bool canUseCombatRetargetSearch = currentMode == BotMode.MoveAndAttack || currentMode == BotMode.MoveAndAttackAndLoot;
                if (!canUseCombatRetargetSearch)
                    ClearCombatRetargetSearch();

                // While the loot system owns the camera, preserve its scan view. An active
                // retarget stage applies its own verified distance at the shared combat point.
                bool lootScanning = !IsAutomaticLootDisabled &&
                                    canUseCombatRetargetSearch &&
                                    LootSystemRef != null &&
                                    (LootSystemRef.IsLootCycleActive || LootSystemRef.IsPinkRescanActive);
                if (!lootScanning && _combatRetargetCameraStage == CombatRetargetCameraStage.None)
                    TryApplyMovementCameraDistance(currentWaypoint.CameraDistanceLock);

            }

            // ── Combat mode handling ──
        RUN_COMBAT_ONLY:
            if (IsCombatGateTargetSuppressionActive && targetId == _combatGateSuppressedTargetId)
            {
                if (!_combatGateSuppressionSelectionHandled)
                {
                    _log($"[CombatGate] Target {targetId} skipped: disengagement suppression active.");
                    ReleaseSkillThree();
                    _combatHandler.ResetState();
                    ClearCombatRetargetSearch();
                    BeginCombatGateTargetRelease(targetId);
                    if (!ProcessCombatGateTargetRelease(token))
                        return;

                    // The current target snapshot predates the click. No combat/TAB action may
                    // reuse it on this tick, even when the release could not be confirmed.
                    return;
                }

                currentMode = BotMode.OnlyMove;
            }
            else if (IsCombatGateTargetSuppressionActive)
            {
                _combatGateSuppressionSelectionHandled = false;
            }

            // Refresh here (before combat evaluation and target cycling) so a newly
            // created or completed request is visible to retargeting on this same tick.
            RefreshLootBarrier();

            // The delayed SOD/SOP sweep owns the zoomed camera and cursor. Let any
            // current attack finish, but do not run target cycling while the camera is
            // at loot distance; resume watchdog timing after the sweep is done.
            if (!IsAutomaticLootDisabled && LootSystemRef?.IsPinkRescanActive == true)
            {
                // The loot rescan owns the camera/cursor — an active return must release its
                // input instead of holding W across a tick this branch never reaches.
                CancelCombatGateWaypointReturn("loot pink rescan owns the camera");
                lock (_inputLock)
                    CancelAttackKeyRetry("loot rescan owns input");
                _combatHandler.SuspendForExternalMovement();
                await Task.Delay(BotConstants.Delays.LootUpdateMs, token);
                return;
            }
            _combatHandler.ResumeAfterExternalMovement();

            if (await HandlePostKillRetargetRequestAsync(token))
                return;

            if (_combatRetargetCameraStage != CombatRetargetCameraStage.None)
            {
                bool retargetMonsterSelected = IsCurrentCombatRetargetMonsterSelected();
                if (_lootBarrier.IsBlockingNavigation && !retargetMonsterSelected)
                {
                    if (_combatGateReturnActive &&
                        await TickCombatGateWaypointReturnUnderLootBarrierAsync(
                            currX, currY, confirmedMonsterTarget, targetId, token))
                        return;
                    if (await HoldNavigationForMandatoryLootAsync(token))
                        return;
                }

                short restoreCameraDistance = GetRetargetRestoreCameraDistance();
                if (await HandleCombatRetargetCameraStageAsync(currentMode, restoreCameraDistance, token))
                    return;
            }

            // After the post-kill pink check, restore the waypoint's normal camera
            // distance before looking for another target. A delayed rescan can postpone
            // this TAB, but it must never happen at the loot-scan zoom.
            if (currentMode != BotMode.MoveAndAttack)
                _postPinkScanTargetCheckPending = false;

            if (_postPinkScanTargetCheckPending && currentMode == BotMode.MoveAndAttack)
            {
                _postPinkScanTargetCheckPending = false;
                short normalCameraDistance = _finalStandbyActive
                    ? _finalStandbyWaypoint.CameraDistanceLock
                    : _waypoints.Count > 0
                        ? _waypoints.Peek().CameraDistanceLock
                        : Waypoint.DefaultCameraDistanceLock;
                if (!TryApplyMovementCameraDistance(normalCameraDistance))
                {
                    _postPinkScanTargetCheckPending = true;
                    return;
                }

                if (!mobSelected)
                {
                    _log($"[CombatRetarget] Pink scan finished — camera restored to {normalCameraDistance}; checking for mobs with TAB.");
                    ReleaseSkillThree();
                    await Task.Delay(BotConstants.Delays.CombatRetargetTabMs, token);
                    if (!TryPressTargetCycleKey())
                    {
                        _postPinkScanTargetCheckPending = true;
                        _combatHandler.SuspendForExternalMovement();
                        return;
                    }
                    await Task.Delay(BotConstants.Delays.PreTabWaitMs, token);
                    return;
                }
            }

            // Single authoritative combat-context read for both the active-grouping
            // cancellation and the observation gate below. A combat context exists only for a
            // CONFIRMED monster target (player/NPC/unknown targets never count).
            bool hasCombatContext =
                MobGroupingController.IsSupportedCombatMode(CurrentMode) &&
                MobGroupingController.IsSupportedCombatMode(currentMode) &&
                confirmedMonsterTarget &&
                _combatRetargetCameraStage == CombatRetargetCameraStage.None;

            if (IsMobGroupingSequenceActive)
            {
                // A gather walk returns before the ordinary RefreshLootBarrier below, so a
                // mandatory obligation armed by the parallel loot task (or one that just
                // completed) must be adopted/released here. The controller then sees the
                // authoritative ownership and cancels the walk on THIS tick instead of
                // taking another step under an active loot hold. Idempotent and a no-op
                // outside MoveAndAttackAndLoot.
                RefreshLootBarrier();

                if (!TickActiveMobGrouping(currentMode, hasCombatContext, currX, currY, success, token))
                    return;
            }

            var combatAction = _combatHandler.EvaluateCombatAction(_memoryService, currentMode, _isUnstuckRoutineActive, currX, currY, _targetSafety);
            if (combatAction == CombatAction.Unstuck || combatAction == CombatAction.RepositionAndRetry)
                CancelMobGrouping($"{combatAction} has priority");
            if (combatAction == CombatAction.TargetRejected)
                CancelMobGrouping("combat target rejected");
            if (combatAction != CombatAction.None && combatAction != CombatAction.CombatWait &&
                combatAction != CombatAction.TargetRejected)
            {
                _log($"[Tick {_tickCount}] Combat: {combatAction}");
            }

            // Combat tracking happens BEFORE the switch because Attack/CombatWait/
            // TabTarget/PotionsUsed return early. A post-kill loot obligation is armed
            // only by the explicit CombatAction.TabAfterKill kill signal below — a
            // normal TAB or waypoint transition must never create one. The barrier is
            // the authoritative completion contract (timestamps never release it).
            bool combatActionActive = combatAction == CombatAction.Attack ||
                                      combatAction == CombatAction.CombatWait ||
                                      combatAction == CombatAction.TabAfterKill ||
                                      combatAction == CombatAction.PotionsUsed ||
                                      combatAction == CombatAction.RepositionAndRetry ||
                                      combatAction == CombatAction.TargetRejected ||
                                      combatAction == CombatAction.Unstuck;
            if (confirmedMonsterTarget)
            {
                _lastCombatSeenAt = DateTime.UtcNow;
            }
            else if (combatActionActive || combatAction == CombatAction.TabTarget)
            {
                _lastCombatSeenAt = DateTime.UtcNow;
            }

            RefreshLootBarrier();

            if (combatAction == CombatAction.TabTarget &&
                _lootBarrier.HasTerminalFailure &&
                _lootBarrier.Reason == LootBarrierReason.PostKill)
                combatAction = CombatAction.None;

            if (ShouldSuppressPeriodicTargetCycle(combatAction))
                combatAction = CombatAction.None;

            if (combatAction == CombatAction.TabTarget && _combatRetargetFailed)
                combatAction = CombatAction.None;

            if (combatAction == CombatAction.TabTarget && IsCombatGateTargetSuppressionActive)
            {
                if (!_combatGateSuppressedTargetTabAttempted)
                {
                    // Allow one normal acquisition attempt so another valid monster can still be
                    // selected. If it finds only the disengaged target, do not TAB forever.
                    _combatGateSuppressedTargetTabAttempted = true;
                }
                else
                {
                    combatAction = CombatAction.None;
                    if (!_combatGateSuppressedTabSuppressionLogged)
                    {
                        _combatGateSuppressedTabSuppressionLogged = true;
                        _log($"[CombatGate] TAB suppressed while target {_combatGateSuppressedTargetId} remains in disengagement suppression.");
                    }
                }
            }

            // If any combat action interrupts movement, mark route resync as pending
            if (combatAction != CombatAction.None && !_routeResyncPendingAfterCombat)
            {
                _routeResyncPendingAfterCombat = true;
                _log($"[RouteResync] pending set: reason={combatAction}");
            }

            if (combatAction != CombatAction.TabAfterKill &&
                combatAction != CombatAction.Unstuck && combatAction != CombatAction.RepositionAndRetry &&
                combatAction != CombatAction.TargetRejected &&
                TryObserveOrStartMobGrouping(
                    currentMode,
                    hasCombatContext,
                    currX,
                    currY,
                    success,
                    token))
                return;

            switch (combatAction)
            {
                case CombatAction.TabAfterKill:
                    _resumeCombatSkillAfterGrouping = false;
                    // Explicit kill signal — arm/refresh the mandatory obligation before
                    // any retarget TAB. Consecutive kills coalesce into the same request.
                    ArmMandatoryLootBarrier(LootBarrierReason.PostKill);
                    if (CurrentMode == BotMode.MoveAndAttackAndLoot)
                    {
                        _postKillCloseTargetCheckPending = true;
                        _postKillTargetIdBeforeTab = targetId;
                        ReleaseSkillThree();
                        StopMoving();

                        if (!IsAutomaticLootDisabled)
                        {
                            long requestId = _lootBarrier.RequestId;
                            LootSystem? loot = LootSystemRef;
                            bool activePostKillRequest = requestId > 0 &&
                                _lootBarrier.Reason == LootBarrierReason.PostKill &&
                                (_lootBarrier.Phase == LootBarrierPhase.Pending ||
                                 _lootBarrier.Phase == LootBarrierPhase.Running ||
                                 _lootBarrier.Phase == LootBarrierPhase.Deferred) &&
                                loot != null &&
                                loot.TryGetMandatoryLootRequestStatus(requestId, out LootRequestStatus requestStatus) &&
                                requestStatus.Reason == LootBarrierReason.PostKill &&
                                (requestStatus.State == LootRequestState.Pending ||
                                 requestStatus.State == LootRequestState.Running ||
                                 requestStatus.State == LootRequestState.Deferred);
                            if (!activePostKillRequest)
                            {
                                // The failed barrier is intentionally left blocking. Never
                                // bypass a missing/rejected loot obligation with a TAB.
                                ResetPostKillRetargetState();
                                _log($"[PostKillTarget] Mandatory request unavailable request={requestId}; retarget remains blocked.");
                                return;
                            }

                            _postKillRetargetRequestId = requestId;
                            _postKillTargetReleaseId = targetId > 0 ? targetId : 0;
                            _postKillTargetReleaseAttempts = 0;
                            _postKillTargetReleaseRefusalLogged = false;
                            _postKillTargetReleaseRetryAtUtc = DateTime.MinValue;
                            _log($"[PostKillTarget] Request={requestId} PreviousTarget={targetId}");
                        }
                        else
                        {
                            _postKillRetargetRequestId = 0;
                            _postKillTargetReleaseId = 0;
                            StartCombatRetargetSearch();
                            _log("[CombatRetarget] PostKill with automatic loot disabled — starting close-first search.");
                        }
                        return;
                    }

                    if (!IsAutomaticLootDisabled &&
                        currentMode == BotMode.MoveAndAttack && LootSystemRef?.PinkLootOnlyMode == true)
                    {
                        // First cycle TAB at the closest retarget camera. Keep fighting if
                        // that finds a nearby mob; only request the SOD/SOP scan when the
                        // close-range TAB finds none.
                        LootSystemRef.SchedulePinkRescanAfterKill();
                        _postKillCloseTargetCheckPending = true;
                        _postKillTargetIdBeforeTab = targetId;
                        StartCombatRetargetSearch();
                        ReleaseSkillThree();
                        _log($"[CombatRetarget] Mob killed — checking for nearby mobs at {CombatRetargetVeryLowCameraDistance} before SOD/SOP loot scan.");
                        return;
                    }

                    if (!IsAutomaticLootDisabled)
                        LootSystemRef?.RequestPinkScan();
                    goto case CombatAction.TabTarget;

                case CombatAction.TabTarget:
                    _resumeCombatSkillAfterGrouping = false;
                    if ((currentMode == BotMode.MoveAndAttack || currentMode == BotMode.MoveAndAttackAndLoot) &&
                        _isSkillThreeHeld &&
                        _combatRetargetCameraStage == CombatRetargetCameraStage.None)
                    {
                        _log($"[CombatRetarget] Target lost after attack. Lowering camera to {CombatRetargetVeryLowCameraDistance}.");
                        StartCombatRetargetSearch();
                        ReleaseSkillThree();
                        return;
                    }

                    ReleaseSkillThree();
                    if (!TryPressTargetCycleKey())
                    {
                        _postPinkScanTargetCheckPending =
                            !IsAutomaticLootDisabled && currentMode == BotMode.MoveAndAttack;
                        _combatHandler.SuspendForExternalMovement();
                        return;
                    }
                    await Task.Delay(BotConstants.Delays.PreTabWaitMs, token);
                    return;

                case CombatAction.Attack:
                    _resumeCombatSkillAfterGrouping = false;
                    // Extra safety check on every newly selected attack target: verify that
                    // no other-player marker (player pixels) is visible on screen with the
                    // camera zoomed out BEFORE the first attack swing is held.
                    await RunAttackStartPlayerMarkerCheck(token);
                    if (token.IsCancellationRequested)
                        return;
                    HoldSkillThree();
                    if (_isMovingForward)
                    {
                        StopMoving();
                    }
                    await Task.Delay(BotConstants.Delays.CombatAttackWaitMs, token);
                    return;

                case CombatAction.CombatWait:
                    // A grouping walk released the existing combat-owned hold. Reacquire it
                    // here through MovementSystem once ordinary combat evaluation resumes.
                    // Both attack modes restore the skill; the mode check only excludes a
                    // temporary OnlyMove override (attack disengage / standby downgrade).
                    if (_resumeCombatSkillAfterGrouping &&
                        MobGroupingController.IsSupportedCombatMode(currentMode))
                    {
                        HoldSkillThree();
                        _resumeCombatSkillAfterGrouping = false;
                    }
                    CheckAttackKeyRetry(token);
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

                case CombatAction.TargetRejected:
                    // The selected target is not a confirmed monster. Release 3 immediately,
                    // invalidate the combat context, cancel the pending retry and - for a
                    // confirmed other player - run the project's existing player response.
                    CombatTargetValidation rejected = _targetSafety.Validate();
                    HandleRejectedCombatTarget(rejected);
                    if (ShouldCycleTargetAwayFromRejectedTarget(rejected.TargetId))
                    {
                        _log($"[CombatSafety] Cycling target away from rejected TargetId={rejected.TargetId} " +
                             $"(attempt {_rejectedTargetTabCount}/{BotConstants.CombatSafety.MaxTargetCycleAttemptsForRejectedTarget}).");
                        if (TryPressTargetCycleKey())
                            await Task.Delay(BotConstants.Delays.PreTabWaitMs, token);
                    }
                    else
                    {
                        await Task.Delay(BotConstants.Delays.CombatAttackWaitMs, token);
                    }
                    return;

                case CombatAction.PotionsUsed:
                    ReleaseSkillThree();
                    _log($"[Tick {_tickCount}] Potions used — brief delay.");
                    await Task.Delay(BotConstants.Delays.PotionsUsedMs, token);
                    return;
            }

            // Combat is over — release skill 3 if held
            ReleaseSkillThree();

            // ── Mandatory MoveAndAttackAndLoot barrier ──
            // This is the authoritative post-combat navigation gate. The old timestamp-
            // based heuristic below is retained only for legacy MoveAndAttack (SOD/SOP)
            // behavior; it is never used as proof that a MoveAndAttackAndLoot loot pass
            // completed. Only a camera-verified Completed request releases movement.
            RefreshLootBarrier();

            // ── CombatGate waypoint return ──
            // The exact interaction this operation exists for: CombatGate suppressed the attack
            // (player outside the waypoint's AttackDisengageDistance), released the selected
            // monster, and a Deferred PostKill loot obligation blocks ordinary navigation. The
            // return runs BEFORE the mandatory hold because that hold stops all movement; it
            // is tick-driven, keeps the waypoint and the obligation untouched, and owns the tick
            // exclusively so no navigation/resync/grouping runs concurrently.
            if (await TickCombatGateWaypointReturnUnderLootBarrierAsync(currX, currY, confirmedMonsterTarget, targetId, token))
                return;

            if (await HoldNavigationForMandatoryLootAsync(token))
            {
                // Only an authorized combat-owned recovery may still act while the hold is
                // active — it keeps ticking its own camera/W recovery. Waypoint navigation,
                // route resync, passive scans and every other movement remain blocked.
                if (IsCombatRecoveryMovementAuthorized && _reverseDiagonalRecovery.IsActive)
                    TickCombatRecoveryUnderLootHold(currX, currY);
                return;
            }

            // ── Post-combat loot wait (legacy MoveAndAttack only) ──
            // In MoveAndAttack mode the bot must NOT walk away while the SOD/SOP pink
            // scan (running in a parallel task) is still collecting. These timestamps are
            // retained for that legacy hold only. MoveAndAttackAndLoot uses the explicit
            // barrier above and never starts a loot wait from IsLootCycleActive=false.
            // Skipped entirely while the unstuck routine is active — the recovery runs
            // uninterrupted in the movement section below.
            if (currentMode == BotMode.MoveAndAttack && !_isUnstuckRoutineActive &&
                !IsAutomaticLootDisabled)
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
            // The resync request is preserved while loot is pending; this branch can only
            // run after the mandatory barrier released navigation.
            if (CanRunRouteResyncAfterCombat(
                    _routeResyncPendingAfterCombat,
                    !IsAutomaticLootDisabled && _lootBarrier.IsBlockingNavigation))
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
                // A waypoint reached in this tick may have just exposed the next leg;
                // apply its start camera before this same tick's W-down.
                await EnsureWaypointStartCameraAngleAsync(target, token);
                TryApplyMovementCameraDistance(target.CameraDistanceLock);
                if (await UpdateNavigationProgress(target, currX, currY, token))
                    return;
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

                // Navigation failure accounting is intentionally NOT tied to physical
                // displacement. ReverseDiagonalRecovery can legitimately move the player
                // 3+ units away from an obstacle and still be unable to reach the same
                // waypoint; resetting here caused the confirmed infinite city loop.
                // The counter is preserved until the waypoint is consumed, the route
                // context genuinely changes (see AdvanceReachedWaypoints / resync / map)
                // or UpdateNavigationProgress confirms route-directed progress toward the
                // waypoint (the reported NavigationRecovered=True incident).

                // ── Active ReverseDiagonalRecovery ──
                // A combat-owned recovery may only keep driving W/camera while its Deferred
                // post-kill authorization is still valid. Losing it (target gone, request
                // transitioned/superseded, focus lost) must stop the recovery input at once
                // and hand the still-Deferred obligation back to the loot system.
                if (_combatRecoveryMovementAuthorized && _reverseDiagonalRecovery.IsActive &&
                    !IsCombatRecoveryMovementStillValid())
                {
                    _reverseDiagonalRecovery.Stop();
                    EndCombatRecoveryMovement("authorization lost during recovery");
                    _isUnstuckRoutineActive = false;
                    ReleaseRecoveryOwnership("combat recovery authorization lost");
                }

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
                            if (_combatRecoveryMovementAuthorized)
                            {
                                // The barrier still blocks ordinary navigation, so the handoff
                                // must return a released W to the loot owner.
                                StopMoving();
                                EndCombatRecoveryMovement("ReverseDiagonal recovered");
                            }
                            _isUnstuckRoutineActive = false;
                            ReleaseRecoveryOwnership("ReverseDiagonal recovered");
                            _log("[ReverseDiagonal] PhysicalRecoverySucceeded=True.");
                            // Physical success only means the escape maneuver worked. The
                            // navigation failure against the current waypoint is still
                            // unresolved until ordinary route movement proves it can advance.
                            _awaitingNavigationRecoveryProof = true;
                            _log($"[Navigation] NavigationRecovered=False (awaiting route-directed progress) waypoint={_navigationProgressWaypointId} failures={_consecutiveStuckAttempts} bestDistance={_bestWaypointDistance:F2}.");
                            bool recoveredInCity = _memoryService.GetIsInCity();
                            int recoveredLimit = recoveredInCity ? STUCK_MAX_ATTEMPTS_IN_CITY : STUCK_MAX_ATTEMPTS_OUTSIDE;
                            float recoveredDisplacement = _lastStuckAttemptPos.HasValue
                                ? GeometryUtils.Distance(currX, currY, _lastStuckAttemptPos.Value.X, _lastStuckAttemptPos.Value.Y)
                                : 0f;
                            _log($"[Unstuck] PhysicalRecoverySucceeded=True moved={recoveredDisplacement:F1} waypoint={_navigationProgressWaypointId} Attempt={_consecutiveStuckAttempts}/{recoveredLimit} bestDistance={_bestWaypointDistance:F2}.");
                            _log($"[Unstuck] NavigationRecovered=False; navigation failure count preserved: {_consecutiveStuckAttempts}/{recoveredLimit}.");
                            ResetActionStuckTracking();
                            return;
                        case RecoveryResult.Failed:
                            EndCombatRecoveryMovement("ReverseDiagonal failed");
                            _isUnstuckRoutineActive = false;
                            ReleaseRecoveryOwnership("ReverseDiagonal failed");
                            _log($"[ReverseDiagonal] failed - all attempts exhausted.");
                            _log($"[ActionStuck] Action={currentAction} while moving. Stuck confirmed by action.");
                            bool failedInCity = _memoryService.GetIsInCity();
                            int failedLimit = failedInCity ? STUCK_MAX_ATTEMPTS_IN_CITY : STUCK_MAX_ATTEMPTS_OUTSIDE;
                            _awaitingNavigationRecoveryProof = false;
                            _log($"[Unstuck] PhysicalRecoverySucceeded=False; NavigationRecovered=False; waypoint={_navigationProgressWaypointId} bestDistance={_bestWaypointDistance:F2} navigation failure count preserved: {_consecutiveStuckAttempts}/{failedLimit}.");
                            MarkObstacleFromActionStuck(currX, currY, target);
                            _localNavigationMap.SaveIfDirty();
                            return;
                    }
                }

                // Action-stuck detection is ignored during the start grace period so a
                // freshly pressed W key never triggers a false stuck recovery. The debounce
                // is expressed against the effective tick interval, so the faster OnlyMove
                // tick keeps the same 200 ms (not 100 ms) idle window before firing.
                _stuckDetector.SampleIntervalMs = TickIntervalMs;
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
                    _log($"[Route] T:{_tickCount} WP{ghostFlag}({target.X:F1},{target.Y:F1}) d:{distNow:F2} th:{thresholdNow:F2} Brg:{brgToWp:F1}deg Act:{currentAction} Mob:{confirmedMonsterTarget} TargetId:{targetId} Type:{targetValidation.Kind} M:{target.Mode} P:{target.Precision} Steer:{ResolveSteeringMode(target.SteeringMode, _steeringMode)} Cam:{_memoryService.GetCameraAngle()}");
                }

                // Track healthy movement bearing for escape direction
                if (!_isUnstuckRoutineActive && StuckDetector.IsActionRunning(currentAction))
                {
                    _lastHealthyMoveBearingDeg = GeometryUtils.GetBearingToTargetDeg(currX, currY, target.X, target.Y);
                    _lastHealthyMovePos = (currX, currY);
                    _lastHealthyMoveTime = DateTime.Now;
                }

                MoveTowards(currX, currY, target.X, target.Y, ResolveSteeringMode(target.SteeringMode, _steeringMode));
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
                bool advanced = await AdvanceReachedWaypoints(currX, currY, token);
                // Returning true only stops this tick. If the mandatory loot barrier
                // prevented advancement, later ticks re-enter the same check.
                return advanced || _goalReached ||
                       (!IsAutomaticLootDisabled && _lootBarrier.IsBlockingNavigation);
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
                // A consumed waypoint ends its leg; the next target must be able to apply
                // its own start camera even when it repeats the same coordinates.
                ClearStartCameraAngleApplication();
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
                // Reset the navigation failure counter only when the waypoint is
                // actually consumed. Physical movement / recovery displacement does NOT
                // prove that the navigation problem is solved; only route-directed
                // progress confirmed in UpdateNavigationProgress may reset it earlier.
                if (_consecutiveStuckAttempts > 0)
                {
                    _log($"[Unstuck] Navigation failure count reset — waypoint ({target.X:F1},{target.Y:F1}) reached.");
                }
                _consecutiveStuckAttempts = 0;
                _lastStuckAttemptPos = null;
                ResetNavigationProgressTracking();
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
                    _finalStandbySteeringMode = target.SteeringMode;
                    _finalStandbyWaypoint = target;
                    _finalStandbyActive = true;
                    // Standby must carry the final waypoint's path policy immediately.
                    SynchronizeAutomaticLootPolicy();
                    ApplyAutomaticLootDisabledPolicy();

                    HandleEmptyWaypointQueueAfterAdvance();
                    if (_goalReached)
                        return true;
                }
            }

            // The next waypoint is now authoritative for the path-level policy. Release
            // any loot ownership on the same tick so navigation cannot stall waiting for
            // a scan that this waypoint forbids.
            SynchronizeAutomaticLootPolicy();
            ApplyAutomaticLootDisabledPolicy();
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
        /// Per-waypoint steering resolution: an explicit waypoint mode wins, otherwise the
        /// MovementSystem/path-runner configured default is used.
        /// </summary>
        internal static MovementSteeringMode ResolveSteeringMode(
            MovementSteeringMode? waypointMode,
            MovementSteeringMode defaultMode)
            => waypointMode ?? defaultMode;

        /// <summary>
        /// Applies a desired bearing using the supplied steering mode.
        /// When <paramref name="moveForward"/> is false the turn is performed in place:
        /// A/D and the fast-turn mouse may be used, but W is never held.
        /// </summary>
        private void ApplySteeringBearing(float bearingDeg, MovementSteeringMode steeringMode, bool moveForward = true)
        {
            if (Volatile.Read(ref _stopRequested) != 0)
            {
                StopMoving();
                return;
            }

            if (_lootNavigationBarrierHoldsInput)
            {
                StopMoving();
                return;
            }

            if (steeringMode == MovementSteeringMode.DirectCamera)
            {
                ApplyDirectSteeringBearing(bearingDeg, moveForward);
                return;
            }

            ApplyKeyboardSteeringBearing(bearingDeg, moveForward);
        }

        /// <summary>
        /// Routes yaw writes through the shared camera lease when available. This makes
        /// external operations such as the NPC scan authoritative for the entire time they
        /// own the camera, including against direct-camera movement steering.
        /// </summary>
        private bool TrySetCameraAngle(float radians)
        {
            if (_wKeyCameraController != null)
                return _wKeyCameraController.TrySetCameraAngle(radians);

            _memoryService.SetCameraAngle(radians);
            return true;
        }

        /// <summary>
        /// Legacy direct-camera steering. Its filtering and write state intentionally
        /// remain separate from the feedback-controlled keyboard steering state.
        /// </summary>
        private void ApplyDirectSteeringBearing(float bearingDeg, bool moveForward = true)
        {
            if (Volatile.Read(ref _stopRequested) != 0)
            {
                StopMoving();
                return;
            }

            if (_lootNavigationBarrierHoldsInput)
            {
                StopMoving();
                return;
            }

            // A configured start angle must be the camera orientation at the leg's first
            // W-down. DirectCamera normally writes the target bearing before W; press W
            // first for this one transition so the explicit angle is not overwritten
            // before the game sees the forward input.
            if (_startCameraAngleAwaitingFirstW)
            {
                _startCameraAngleAwaitingFirstW = false;
                if (moveForward)
                {
                    if (!TryStartMoving())
                    {
                        // W was not accepted; keep the deferral so a later attempt still
                        // presses W at the configured start bearing.
                        _startCameraAngleAwaitingFirstW = true;
                        return;
                    }
                }
                else if (_isMovingForward)
                    StopMoving();
            }

            ReleaseTurnKey("direct camera steering");

            float cameraRadians = GeometryUtils.ConvertBearingToRadians(bearingDeg);
            _lastSetBearingDeg = bearingDeg;
            _lastSetGameAngle = cameraRadians;

            // Capture whether this is a fresh segment BEFORE mutating _hasLastGameAngle.
            // ResetBearingState() sets _hasLastGameAngle=false; the very next direct
            // steering call bypasses the camera filter so the new heading is applied immediately.
            bool isFreshSegment = !_hasLastGameAngle;
            _hasLastGameAngle = true;

            // ── Camera update filter ──
            if (ShouldUpdateCamera(cameraRadians, isFreshSegment, out _))
            {
                _log($"[Camera] Apply brg={bearingDeg:F1}deg target={cameraRadians:F4}rad last={_cameraLastAppliedAngle:F4}rad diff={CircularGameAngleDiff(cameraRadians, _cameraLastAppliedAngle):F4}");
                if (TrySetCameraAngle(cameraRadians))
                {
                    _cameraLastAppliedAngle = cameraRadians;
                    _lastCameraUpdateTime = DateTime.Now;
                    _hasCameraHysteresisCandidate = false;
                }
            }

            if (moveForward)
                StartMoving();
            else if (_isMovingForward)
                StopMoving();
        }

        /// <summary>
        /// Feedback-controls A/D from the actual horizontal camera angle while keeping W held.
        /// The live camera is re-read on every update: inside the tolerance no turn key is
        /// held, up to and including 60° A/D alone steers, and above 60° the matching mouse
        /// edge is also held so the game's natural x2 turn applies. W stays held in every
        /// branch; the mouse assistance is released again as soon as the error drops back to
        /// the normal A/D range.
        /// </summary>
        private void ApplyKeyboardSteeringBearing(float bearingDeg, bool moveForward = true)
        {
            if (Volatile.Read(ref _stopRequested) != 0)
            {
                StopMoving();
                return;
            }

            float desiredBearingDeg = GeometryUtils.NormalizeBearingDeg(bearingDeg);

            // Keep the complete W + turn-key + mouse-edge ownership transition atomic with StopMoving.
            lock (_inputLock)
            {
                if (moveForward)
                    StartMoving();
                else if (_isMovingForward)
                    StopMoving();

                float currentRadians = _memoryService.GetCameraAngle();
                float currentBearingDeg = GeometryUtils.ConvertRadiansToBearingDeg(currentRadians);
                float signedError = GeometryUtils.GetShortestBearingDiffDeg(currentBearingDeg, desiredBearingDeg);
                KeyboardTurnDirective directive = DecideKeyboardTurn(signedError);

                string details = $"current={currentBearingDeg:F1}° target={desiredBearingDeg:F1}° error={signedError:+0.0;-0.0;0.0}°";

                TurnKeyState desiredTurnKey = GetTurnKeyForDirective(directive);
                if (directive == KeyboardTurnDirective.None)
                    SetTurnKey(TurnKeyState.None, $"target reached. {details}");
                else
                    SetTurnKey(desiredTurnKey, details);

                ApplyKeyboardFastTurnMouse(directive, currentBearingDeg, desiredBearingDeg, signedError, details);
            }
        }

        /// <summary>
        /// Pure KeyboardTurn decision from the live signed bearing error: within the
        /// inclusive tolerance no key, up to and including the fast-turn threshold normal
        /// A/D steering, above it A/D plus the matching mouse edge. Strictly greater than
        /// the threshold: an error of exactly 60° stays normal.
        /// </summary>
        internal static KeyboardTurnDirective DecideKeyboardTurn(float signedError)
        {
            float absError = Math.Abs(signedError);
            if (absError <= BotConstants.Movement.KeyboardTurnToleranceDegrees)
                return KeyboardTurnDirective.None;

            bool fast = absError > BotConstants.Movement.KeyboardFastTurnThresholdDegrees;
            bool left = GetTurnKeyForSignedError(signedError) == TurnKeyState.LeftA;

            if (left)
                return fast ? KeyboardTurnDirective.FastLeft : KeyboardTurnDirective.NormalLeft;
            return fast ? KeyboardTurnDirective.FastRight : KeyboardTurnDirective.NormalRight;
        }

        /// <summary>Maps a pure steering directive to the A/D key that must be held.</summary>
        private static TurnKeyState GetTurnKeyForDirective(KeyboardTurnDirective directive) => directive switch
        {
            KeyboardTurnDirective.NormalLeft or KeyboardTurnDirective.FastLeft => TurnKeyState.LeftA,
            KeyboardTurnDirective.NormalRight or KeyboardTurnDirective.FastRight => TurnKeyState.RightD,
            _ => TurnKeyState.None
        };

        /// <summary>Maps a pure steering directive to the mouse edge that provides the x2 turn.</summary>
        private static TurnKeyState GetFastTurnMouseSide(KeyboardTurnDirective directive) => directive switch
        {
            KeyboardTurnDirective.FastLeft => TurnKeyState.LeftA,
            KeyboardTurnDirective.FastRight => TurnKeyState.RightD,
            _ => TurnKeyState.None
        };

        /// <summary>
        /// Pure keyboard steering decision: positive shortest error means D, negative means A;
        /// error within the inclusive tolerance means no turn key.
        /// </summary>
        private static TurnKeyState GetDesiredTurnKey(
            float currentBearingDeg,
            float desiredBearingDeg,
            out float signedError)
        {
            float normalizedTarget = GeometryUtils.NormalizeBearingDeg(desiredBearingDeg);
            signedError = GeometryUtils.GetShortestBearingDiffDeg(currentBearingDeg, normalizedTarget);
            return GetTurnKeyForDirective(DecideKeyboardTurn(signedError));
        }

        /// <summary>
        /// Central signed-error mapping: increasing bearings use D and decreasing bearings use A.
        /// Callers that control physical steering must apply the tolerance before this mapping.
        /// </summary>
        private static TurnKeyState GetTurnKeyForSignedError(float signedError)
        {
            return signedError > 0f ? TurnKeyState.RightD : TurnKeyState.LeftA;
        }

        // ── Fast-turn mouse ownership ────────────────────────────────────────────
        // Above 60° each keyboard steering update also holds the cursor near the
        // matching game-client edge, which makes the game turn at x2. MovementSystem
        // owns that cursor position only while the fast turn is active: it is moved to
        // the edge once on entry (and re-asserted only if something else moved it),
        // returned to the horizontal client centre the moment the turn drops back to
        // normal, and cleared by every movement cleanup path. It never fights an active
        // loot/pink-scan cursor and never throws out of the movement update.

        /// <summary>
        /// Applies the mouse half of the KeyboardTurn controller for the current directive:
        /// normal/none releases any fast-turn help, fast acquires (or re-asserts) the
        /// matching client edge. Called on every keyboard steering update.
        /// The human user always outranks this automation: the cursor is only touched while
        /// the game is foreground, the cursor is inside the game client, no human-override
        /// suppression is active and no loot/pink scan owns the mouse.
        /// </summary>
        private void ApplyKeyboardFastTurnMouse(
            KeyboardTurnDirective directive,
            float currentBearingDeg,
            float desiredBearingDeg,
            float signedError,
            string details)
        {
            TurnKeyState requestedSide = GetFastTurnMouseSide(directive);
            if (requestedSide == TurnKeyState.None)
            {
                // Natural end of the fast turn (10°..60° / target reached): release the
                // edge without fighting a human who has taken the cursor.
                string normalTarget = directive switch
                {
                    KeyboardTurnDirective.NormalLeft => "NORMAL LEFT",
                    KeyboardTurnDirective.NormalRight => "NORMAL RIGHT",
                    _ => "NO TURN"
                };
                ReleaseKeyboardFastTurnMouse($"{normalTarget} error={signedError:+0.0;-0.0;0.0}° mouse released. {details}");
                return;
            }

            // Priority 2: an active loot/pink scan owns the cursor. Yield without touching
            // it; A/D feedback steering continues unaffected. Loot movement is never
            // classified as human movement.
            if (IsLootMouseOperationActive())
            {
                ReleaseKeyboardFastTurnMouse($"loot cursor active. {details}");
                return;
            }

            // Resolve the game client and the physical cursor once for this update.
            bool clientResolved = TryResolveKeyboardFastTurnClient(out KeyboardFastTurnClient client);
            if (!clientResolved)
            {
                ReleaseKeyboardFastTurnMouse("game client rect unavailable");
                return;
            }

            bool gameForeground = IsKeyboardFastTurnGameForeground(client);
            bool cursorKnown = MouseOperations.TryGetCursorPosition(out int cursorX, out int cursorY);
            bool cursorInside = cursorKnown &&
                                IsCursorInsideKeyboardFastTurnClient(client, cursorX, cursorY);

            // Priority 1: the human user always wins. If the game is not foreground or the
            // cursor is outside the client, relinquish immediately and leave the physical
            // cursor exactly where the user put it — never pull it back into the game.
            if (!gameForeground)
            {
                ReleaseKeyboardFastTurnMouseForHumanOverride(
                    "Game lost foreground focus — mouse acceleration released without repositioning cursor.");
                return;
            }

            if (!cursorKnown)
            {
                ReleaseKeyboardFastTurnMouseForHumanOverride(
                    "Cursor position unavailable — mouse acceleration suspended.");
                return;
            }

            if (!cursorInside)
            {
                ReleaseKeyboardFastTurnMouseForHumanOverride(
                    "Human mouse override detected — cursor outside the game client. Mouse acceleration suspended.");
                return;
            }

            string diagnosticContext =
                $"current={currentBearingDeg:F1} target={desiredBearingDeg:F1} error={signedError:+0.0;-0.0;0.0} " +
                $"turn={(requestedSide == TurnKeyState.LeftA ? "A" : "D")} held={GetTurnKeyName(_heldTurnKey)}";

            // We already own an edge: verify the human did not pull the cursor away from it.
            if (_keyboardFastTurnMouseActive)
            {
                TurnKeyState ownedSide = _keyboardFastTurnMouseDirection;
                int ownedTargetX = GetKeyboardFastTurnEdgeX(client, ownedSide);
                int inward = ComputeKeyboardFastTurnInwardDisplacement(
                    ownedSide == TurnKeyState.LeftA, ownedTargetX, cursorX);

                if (IsKeyboardFastTurnHumanDisplacement(
                        ownedSide == TurnKeyState.LeftA,
                        ownedTargetX,
                        cursorX,
                        BotConstants.Movement.KeyboardFastTurnHumanOverrideDistancePx))
                {
                    ReleaseKeyboardFastTurnMouseForHumanOverride(
                        $"Human mouse override detected — cursor moved {inward}px away from the automated edge. Mouse acceleration suspended.");
                    return;
                }

                if (ownedSide == requestedSide)
                {
                    ReassertKeyboardFastTurnMouse(requestedSide, client, cursorX, diagnosticContext);
                    return;
                }

                // Live heading changed side: swap the edge in this same update.
                _keyboardFastTurnMouseActive = false;
                _keyboardFastTurnMouseDirection = TurnKeyState.None;
                _log($"[Steering] Fast turn switched to {(requestedSide == TurnKeyState.LeftA ? "LEFT" : "RIGHT")}: {details}");
            }

            // Eligibility gate: heading > 60° and no human cooldown and no other mouse owner.
            if (!MayUseKeyboardFastTurnMouse(
                    headingRequiresFastTurn: true,
                    gameForeground: gameForeground,
                    cursorInsideGameClient: cursorInside,
                    suppressionExpired: DateTime.UtcNow >= _keyboardFastTurnMouseSuppressedUntilUtc,
                    otherMouseOwnerActive: false))
            {
                // Human-override cooldown still running (or a race on any other flag):
                // A/D steering continues and the cursor is never touched.
                return;
            }

            if (_keyboardFastTurnMouseSuppressedUntilUtc != DateTime.MinValue)
            {
                _keyboardFastTurnMouseSuppressedUntilUtc = DateTime.MinValue;
                _keyboardFastTurnMouseHumanOverrideLogged = false;
                _log("[Steering][FastTurn] Mouse acceleration available again — game focused and cursor returned inside client.");
            }

            if (MoveKeyboardFastTurnMouseToEdge(requestedSide, diagnosticContext, isEntry: true, out int targetMouseX, out string failure))
            {
                _keyboardFastTurnMouseActive = true;
                _keyboardFastTurnMouseDirection = requestedSide;
                _keyboardFastTurnMouseNeutralizePending = false;
                _keyboardFastTurnMouseNeutralizePendingDirection = TurnKeyState.None;
                _keyboardFastTurnMouseFailureLogged = false;
                _keyboardFastTurnMouseHumanOverrideLogged = false;
                _log($"[Steering] FAST {(requestedSide == TurnKeyState.LeftA ? "LEFT" : "RIGHT")} entered error={signedError:+0.0;-0.0;0.0}° " +
                     $"{(requestedSide == TurnKeyState.LeftA ? "A=DOWN D=UP" : "A=UP D=DOWN")} targetMouseX={targetMouseX}");
            }
            else if (!_keyboardFastTurnMouseFailureLogged)
            {
                _keyboardFastTurnMouseFailureLogged = true;
                _log($"[Steering] Fast turn mouse unavailable ({failure}) — continuing with A/D only. {details}");
            }
        }

        /// <summary>
        /// Human-override release: the user took the physical cursor (moved it out of the
        /// game client, away from the automated edge, focused another window, or the
        /// cursor became unreadable). Clears the logical fast-turn ownership, suppresses
        /// reacquisition for the cooldown and NEVER moves the cursor — the user's current
        /// physical position is preserved exactly. A/D steering continues unaffected.
        /// Safe to call repeatedly; the transition is logged only once per episode.
        /// </summary>
        private void ReleaseKeyboardFastTurnMouseForHumanOverride(string message)
        {
            bool wasActive;
            TurnKeyState previousSide;
            bool alreadyLogged;

            lock (_inputLock)
            {
                wasActive = _keyboardFastTurnMouseActive;
                previousSide = _keyboardFastTurnMouseDirection;
                alreadyLogged = _keyboardFastTurnMouseHumanOverrideLogged;

                _keyboardFastTurnMouseActive = false;
                _keyboardFastTurnMouseDirection = TurnKeyState.None;
                _keyboardFastTurnMouseNeutralizePending = false;
                _keyboardFastTurnMouseNeutralizePendingDirection = TurnKeyState.None;
                _keyboardFastTurnMouseSuppressedUntilUtc = DateTime.UtcNow.AddMilliseconds(
                    BotConstants.Movement.KeyboardFastTurnHumanOverrideCooldownMs);

                if (wasActive || !alreadyLogged)
                    _keyboardFastTurnMouseHumanOverrideLogged = true;

                _keyboardFastTurnMouseFailureLogged = false;
                _keyboardFastTurnMouseWarningLogged = false;
            }

            if (wasActive || !alreadyLogged)
            {
                string side = previousSide == TurnKeyState.LeftA
                    ? "LEFT"
                    : previousSide == TurnKeyState.RightD ? "RIGHT" : "NONE";
                string releaseTag = wasActive ? $" (fast {side} released)" : string.Empty;
                _log($"[Steering][FastTurn] {message}{releaseTag}");
            }
        }

        /// <summary>
        /// Central idempotent release of the KeyboardTurn mouse ownership for natural
        /// transitions and movement cleanup. Returns the cursor to the horizontal client
        /// centre so it stops contributing to the turn, but ONLY while the bot still
        /// clearly owns the interaction (game foreground, cursor inside the client, no
        /// human suppression and the cursor still near the automated edge). If the human
        /// moved the cursor it is left exactly where it is. When loot owns the cursor the
        /// neutralization is deferred instead of fighting it. Safe to call repeatedly,
        /// from inside <see cref="_inputLock"/>, and never throws out of movement cleanup.
        /// </summary>
        private void ReleaseKeyboardFastTurnMouse(string reason)
        {
            bool wasActive;
            TurnKeyState previousSide;
            bool hadPendingNeutralize;
            TurnKeyState pendingSide;

            lock (_inputLock)
            {
                wasActive = _keyboardFastTurnMouseActive;
                previousSide = _keyboardFastTurnMouseDirection;
                hadPendingNeutralize = _keyboardFastTurnMouseNeutralizePending;
                pendingSide = _keyboardFastTurnMouseNeutralizePendingDirection;

                _keyboardFastTurnMouseActive = false;
                _keyboardFastTurnMouseDirection = TurnKeyState.None;
                if (wasActive)
                {
                    _keyboardFastTurnMouseFailureLogged = false;
                    _keyboardFastTurnMouseWarningLogged = false;
                }
            }

            if (!wasActive && !hadPendingNeutralize)
                return;

            if (wasActive)
                _log($"[Steering] Fast turn {(previousSide == TurnKeyState.LeftA ? "LEFT" : "RIGHT")} released — {reason}");

            if (IsLootMouseOperationActive())
            {
                _keyboardFastTurnMouseNeutralizePending = true;
                _keyboardFastTurnMouseNeutralizePendingDirection = wasActive ? previousSide : pendingSide;
                return;
            }

            // Never reposition if the human/focus/client state says the bot no longer
            // owns the cursor; NeutralizeKeyboardFastTurnMouse re-checks before writing.
            _keyboardFastTurnMouseNeutralizePending = false;
            _keyboardFastTurnMouseNeutralizePendingDirection = TurnKeyState.None;
            NeutralizeKeyboardFastTurnMouse(wasActive ? previousSide : pendingSide);
        }

        private bool TryApplyMovementCameraDistance(short distance)
        {
            if (!IsAutomaticLootDisabled && _lootNavigationBarrierHoldsInput)
                return false;

            void Apply() => _memoryService.SetCameraDistance(distance);

            LootSystem? loot = LootSystemRef;
            if (loot == null)
            {
                Apply();
                return true;
            }
            return loot.TryRunMovementCameraAction(Apply);
        }

        /// <summary>
        /// Combat-retarget variant of <see cref="TryApplyMovementCameraDistance"/>: the same
        /// barrier/ownership gate plus a read-back verification of the physical write. The
        /// result bounds the retarget camera retries so a failing write can never degrade
        /// into a blind TAB at the wrong zoom.
        /// </summary>
        private bool TryApplyVerifiedMovementCameraDistance(short distance, out bool writeFailed)
        {
            writeFailed = false;
            if (!IsAutomaticLootDisabled && _lootNavigationBarrierHoldsInput)
                return false;

            bool writeSucceeded = false;
            void Apply() => writeSucceeded = _memoryService.TrySetCameraDistance(distance);

            LootSystem? loot = LootSystemRef;
            if (loot == null)
            {
                Apply();
            }
            else if (!loot.TryRunMovementCameraAction(Apply))
            {
                return false;
            }

            writeFailed = !writeSucceeded;
            return writeSucceeded;
        }

        /// <summary>
        /// Extra player-pixel safety check performed once per newly selected attack target
        /// (right after TAB picked a target and the bot is about to start attacking):
        /// briefly zooms the camera out to the marker-scan distance, captures a fresh game
        /// client frame and runs the central combat safety validation (faction-marker player
        /// detector with the compact local-character exclusion). When a confirmed other player
        /// is found the loot system runs the same stop/return-to-city behavior as a confirmed
        /// periodic detection, and the subsequent hold of key 3 is refused by the same central
        /// validation. The camera is always restored afterwards.
        /// </summary>
        private async Task RunAttackStartPlayerMarkerCheck(CancellationToken token)
        {
            if (_memoryService.GetIsInCity())
                return;

            short normalCameraDistance = _finalStandbyActive
                ? _finalStandbyWaypoint.CameraDistanceLock
                : _waypoints.Count > 0
                    ? _waypoints.Peek().CameraDistanceLock
                    : Waypoint.DefaultCameraDistanceLock;

            // If the loot system currently owns the camera, do not interfere — the normal
            // attack proceeds and the periodic marker scan stays responsible.
            if (!TryApplyMovementCameraDistance(BotConstants.Camera.OtherPlayerMarkerScanDistance))
            {
                // The zoom-out is unavailable, but the safety decision is NOT skipped: the
                // scan below still runs on the current camera frame.
                _log("[PlayerPixels] Attack-start marker-scan camera distance was not applied; validating the target on the current camera frame.");
            }

            try
            {
                // Give the client a moment to render the zoomed-out view.
                await Task.Delay(BotConstants.Camera.PlayerMarkerCheckCameraSettleMs, token);

                CombatTargetValidation validation = _targetSafety.Validate(forcePlayerScan: true);
                if (validation.Kind != TargetEntityKind.OtherPlayer ||
                    validation.PlayerMarker is not { } candidate)
                    return;

                LootSystem? loot = LootSystemRef;
                if (loot == null)
                    return;

                loot.ReportImmediateOtherPlayerMarker(candidate, "Attack-start player-pixel check");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log($"[PlayerPixels] Attack-start player-pixel check failed: {ex.Message}");
            }
            finally
            {
                TryApplyMovementCameraDistance(normalCameraDistance);
            }
        }

        private bool TryRunMovementCursorAction(Action action)
        {
            LootSystem? loot = LootSystemRef;
            if (loot == null)
            {
                action();
                return true;
            }
            return loot.TryRunMovementCursorAction(action);
        }

        /// <summary>
        /// True while an existing loot/pink-scan operation owns the mouse cursor. The
        /// fast-turn cursor must yield during those operations. A failing loot query
        /// never takes movement down — it simply means "no active loot cursor".
        /// </summary>
        private bool IsLootMouseOperationActive()
        {
            try
            {
                if (IsAutomaticLootDisabled)
                    return false;

                LootSystem? loot = LootSystemRef;
                if (loot == null)
                    return false;

                return loot.IsPinkScanPendingOrActive ||
                       loot.IsPinkRescanActive ||
                       loot.IsLootCycleActive ||
                       loot.IsCollecting ||
                       loot.IsLootingActive;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Keeps the owned edge while the fast turn is active. Large inward movement is
        /// handled as a human override by the caller before this is reached; this only
        /// corrects small jitter within the human-override distance and does nothing when
        /// the cursor is already at the edge. Every write is re-checked by
        /// <see cref="MoveKeyboardFastTurnMouseToEdge"/> and never throws.
        /// </summary>
        private void ReassertKeyboardFastTurnMouse(
            TurnKeyState side,
            KeyboardFastTurnClient client,
            int cursorX,
            string context)
        {
            int targetX = GetKeyboardFastTurnEdgeX(client, side);
            if (Math.Abs(cursorX - targetX) <= BotConstants.Movement.KeyboardFastTurnMouseReassertTolerancePx)
                return;

            MoveKeyboardFastTurnMouseToEdge(side, context, isEntry: false, out _, out _);
        }

        /// <summary>
        /// Moves the physical cursor to the requested fast-turn client edge, reads it back
        /// with GetCursorPos and logs the physical-input diagnostic on fast entries and
        /// direction changes. Returns false with a short reason only when the client rect
        /// or the native move cannot be resolved; the caller then continues with normal
        /// A/D steering (never a direct camera snap). A cursor that did not physically
        /// reach the edge keeps the logical fast-turn ownership so the next update retries,
        /// but the warning is throttled so a stolen cursor cannot flood the log.
        /// </summary>
        private bool MoveKeyboardFastTurnMouseToEdge(
            TurnKeyState side,
            string context,
            bool isEntry,
            out int targetX,
            out string failure)
        {
            targetX = 0;
            failure = string.Empty;
            try
            {
                // Every physical fast-turn write must pass the ownership gate: no human
                // suppression, game foreground and cursor inside the client.
                if (!CanKeyboardFastTurnMouseWrite(TurnKeyState.None, out KeyboardFastTurnClient client, out _, out _))
                {
                    failure = "mouse not available (human override / focus / cursor)";
                    return false;
                }

                int edgeX = GetKeyboardFastTurnEdgeX(client, side);
                targetX = edgeX;
                int targetY = ResolveKeyboardFastTurnCursorY(client);
                int actualX = 0;
                int actualY = targetY;
                bool actualKnown = false;
                if (!TryRunMovementCursorAction(() =>
                    {
                        MouseOperations.SetCursorPositionAbsolute(edgeX, targetY);
                        actualKnown = MouseOperations.TryGetCursorPosition(out actualX, out actualY);
                        if (!actualKnown)
                            actualY = targetY;
                    }))
                {
                    failure = "loot scan owns the cursor";
                    return false;
                }

                int clientWidth = client.Right - client.Left + 1;
                string diagnostic =
                    $"[Steering][FastTurn] {context} client=({client.Left}..{client.Right}) width={clientWidth} " +
                    $"edgeTargetX={targetX} actualCursorX={(actualKnown ? actualX.ToString() : "?")} cursorY={actualY}";

                bool edgeReached = actualKnown &&
                    Math.Abs(actualX - targetX) <= BotConstants.Movement.KeyboardFastTurnMouseReassertTolerancePx;

                if (edgeReached)
                {
                    _keyboardFastTurnMouseWarningLogged = false;
                    if (isEntry)
                        _log(diagnostic);
                }
                else if (!_keyboardFastTurnMouseWarningLogged)
                {
                    _keyboardFastTurnMouseWarningLogged = true;
                    _log($"{diagnostic} — WARNING requested {(side == TurnKeyState.LeftA ? "left" : "right")} edge X={targetX} " +
                         $"but actual cursor X={(actualKnown ? actualX.ToString() : "unreadable")} — fast-turn acceleration may not activate.");
                }

                return true;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Returns the cursor to the horizontal client centre while keeping its Y, so the
        /// edge position stops contributing to camera turning. The write is skipped unless
        /// the bot still fully owns the cursor (game foreground, cursor inside the client,
        /// no human suppression and the cursor still near the owned edge) — any human
        /// signal leaves the physical cursor exactly where the user put it. Never throws.
        /// </summary>
        private void NeutralizeKeyboardFastTurnMouse(TurnKeyState ownedSide)
        {
            lock (_inputLock)
            {
                try
                {
                    if (!CanKeyboardFastTurnMouseWrite(ownedSide, out KeyboardFastTurnClient client, out _, out _))
                        return;

                    int neutralX = GetKeyboardFastTurnNeutralX(client);
                    int neutralY = ResolveKeyboardFastTurnCursorY(client);
                    if (!TryRunMovementCursorAction(() =>
                        MouseOperations.SetCursorPositionAbsolute(neutralX, neutralY)))
                    {
                        _keyboardFastTurnMouseNeutralizePending = true;
                        _keyboardFastTurnMouseNeutralizePendingDirection = ownedSide;
                    }
                }
                catch (Exception ex)
                {
                    if (!_keyboardFastTurnMouseFailureLogged)
                    {
                        _keyboardFastTurnMouseFailureLogged = true;
                        _log($"[Steering] Fast turn mouse neutralization failed: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Single ownership gate for every physical fast-turn cursor write. Returns false
        /// when a human-override suppression is active, the game is not foreground, the
        /// cursor cannot be read or lies outside the game client, or — when an owned edge
        /// is given — the cursor moved significantly away from that edge. All failures
        /// mean "do not touch the mouse"; A/D steering continues unaffected.
        /// </summary>
        private bool CanKeyboardFastTurnMouseWrite(
            TurnKeyState ownedSide,
            out KeyboardFastTurnClient client,
            out int cursorX,
            out int cursorY)
        {
            client = default;
            cursorX = 0;
            cursorY = 0;

            if (DateTime.UtcNow < _keyboardFastTurnMouseSuppressedUntilUtc)
                return false;

            try
            {
                if (!TryResolveKeyboardFastTurnClient(out client))
                    return false;

                if (!IsKeyboardFastTurnGameForeground(client))
                    return false;

                if (!MouseOperations.TryGetCursorPosition(out cursorX, out cursorY))
                    return false;

                if (!IsCursorInsideKeyboardFastTurnClient(client, cursorX, cursorY))
                    return false;

                if (ownedSide != TurnKeyState.None)
                {
                    int targetX = GetKeyboardFastTurnEdgeX(client, ownedSide);
                    if (IsKeyboardFastTurnHumanDisplacement(
                            ownedSide == TurnKeyState.LeftA,
                            targetX,
                            cursorX,
                            BotConstants.Movement.KeyboardFastTurnHumanOverrideDistancePx))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// True while the resolved game window owns the foreground. Fast-turn mouse
        /// automation must never reposition the cursor while the user is in another
        /// window (bot UI, editor, browser, Alt+Tab target).
        /// </summary>
        private static bool IsKeyboardFastTurnGameForeground(KeyboardFastTurnClient client)
        {
            if (client.Hwnd == nint.Zero)
                return false;

            nint foreground = GetForegroundWindow();
            return foreground != nint.Zero && foreground == client.Hwnd;
        }

        /// <summary>
        /// Resolves the live drawable client rectangle and requires the game client itself to
        /// own foreground focus. CombatGate target release must never click a different window.
        /// </summary>
        protected virtual bool TryResolveCombatGateClientArea(
            out nint hwnd,
            out int clientScreenLeft,
            out int clientScreenTop,
            out int clientWidth,
            out int clientHeight)
        {
            hwnd = nint.Zero;
            clientScreenLeft = 0;
            clientScreenTop = 0;
            clientWidth = 0;
            clientHeight = 0;

            if (!TryResolveKeyboardFastTurnClient(out KeyboardFastTurnClient client) ||
                !IsKeyboardFastTurnGameForeground(client))
                return false;

            hwnd = client.Hwnd;
            clientScreenLeft = client.Left;
            clientScreenTop = client.Top;
            clientWidth = client.Right - client.Left + 1;
            clientHeight = client.Bottom - client.Top + 1;
            return clientWidth > 0 && clientHeight > 0;
        }

        protected virtual bool IsCombatGateClientForeground(nint hwnd)
            => hwnd != nint.Zero && GetForegroundWindow() == hwnd;

        protected virtual void MoveCombatGateMouseCursor(int screenX, int screenY)
            => MouseOperations.SetCursorPositionAbsolute(screenX, screenY);

        protected virtual bool TryGetCombatGateCursorPosition(out int screenX, out int screenY)
            => MouseOperations.TryGetCursorPosition(out screenX, out screenY);

        protected virtual void SendCombatGateMouseEvent(MouseOperations.MouseEventFlags flags)
            => MouseOperations.MouseEvent(flags);

        /// <summary>Validated game window handle and client rectangle in screen coordinates.</summary>
        private readonly struct KeyboardFastTurnClient
        {
            public KeyboardFastTurnClient(nint hwnd, int left, int top, int right, int bottom)
            {
                Hwnd = hwnd;
                Left = left;
                Top = top;
                Right = right;
                Bottom = bottom;
            }

            public nint Hwnd { get; }
            public int Left { get; }
            public int Top { get; }
            public int Right { get; }
            public int Bottom { get; }
        }

        /// <summary>
        /// Resolves the current game client rectangle in screen coordinates with the existing
        /// Win32 pattern (GetClientRect + ClientToScreen). Returns false when the window or
        /// rectangle cannot be resolved; callers then fall back to plain A/D steering.
        /// </summary>
        private static bool TryResolveKeyboardFastTurnClient(out KeyboardFastTurnClient client)
        {
            client = default;

            nint hwnd = FindWindow(null, "Legend of Ares");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Ares");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Nostalgia");
            if (hwnd == nint.Zero) hwnd = FindWindow(null, "Epic Of Ares Client");
            if (hwnd == nint.Zero)
                return false;

            if (!GetClientRect(hwnd, out RECT rect))
                return false;

            POINT topLeft = new POINT { X = rect.Left, Y = rect.Top };
            if (!ClientToScreen(hwnd, ref topLeft))
                return false;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0)
                return false;

            client = new KeyboardFastTurnClient(
                hwnd,
                topLeft.X,
                topLeft.Y,
                topLeft.X + width - 1,
                topLeft.Y + height - 1);
            return true;
        }

        /// <summary>Horizontal edge X for a fast-turn side, clamped inside the client.</summary>
        private static int GetKeyboardFastTurnEdgeX(KeyboardFastTurnClient client, TurnKeyState side)
            => ComputeKeyboardFastTurnEdgeX(
                client.Left,
                client.Right - client.Left + 1,
                side == TurnKeyState.LeftA,
                BotConstants.Movement.KeyboardFastTurnMouseEdgeMarginPx);

        /// <summary>Horizontal client centre X — the neutral fast-turn position.</summary>
        private static int GetKeyboardFastTurnNeutralX(KeyboardFastTurnClient client)
            => ComputeKeyboardFastTurnNeutralX(client.Left, client.Right - client.Left + 1);

        /// <summary>
        /// Pure fast-turn edge X for a client rectangle: <paramref name="clientWidth"/> is
        /// the client's pixel width, the margin keeps the cursor 1–2 px INSIDE the visible
        /// client (the game only activates its x2 border turn at the actual boundary), and
        /// the result is clamped inside the inclusive client range.
        /// </summary>
        internal static int ComputeKeyboardFastTurnEdgeX(int clientLeft, int clientWidth, bool leftSide, int edgeMargin)
        {
            int clientRight = clientLeft + Math.Max(clientWidth, 1) - 1;
            int x = leftSide
                ? clientLeft + edgeMargin
                : clientRight - edgeMargin;
            return Math.Clamp(x, clientLeft, clientRight);
        }

        /// <summary>Pure horizontally-neutral fast-turn X (client horizontal centre).</summary>
        internal static int ComputeKeyboardFastTurnNeutralX(int clientLeft, int clientWidth)
            => clientLeft + Math.Max(clientWidth, 1) / 2;

        /// <summary>
        /// Pure fast-turn mouse eligibility gate. The human user always outranks this
        /// automation: every flag must allow it before any cursor write. A/D steering is
        /// independent and continues whenever this returns false.
        /// </summary>
        internal static bool MayUseKeyboardFastTurnMouse(
            bool headingRequiresFastTurn,
            bool gameForeground,
            bool cursorInsideGameClient,
            bool suppressionExpired,
            bool otherMouseOwnerActive)
            => headingRequiresFastTurn
               && gameForeground
               && cursorInsideGameClient
               && suppressionExpired
               && !otherMouseOwnerActive;

        /// <summary>
        /// Inward cursor displacement (px) from the automated fast-turn edge. Positive
        /// means the cursor moved toward the game interior, i.e. away from the edge the
        /// bot owns — the signature of deliberate human movement.
        /// </summary>
        internal static int ComputeKeyboardFastTurnInwardDisplacement(
            bool leftSide,
            int targetEdgeX,
            int cursorX)
            => leftSide ? cursorX - targetEdgeX : targetEdgeX - cursorX;

        /// <summary>
        /// True when the cursor moved farther inward than the configured human-override
        /// distance: beyond that the bot must relinquish the cursor without moving it.
        /// </summary>
        internal static bool IsKeyboardFastTurnHumanDisplacement(
            bool leftSide,
            int targetEdgeX,
            int cursorX,
            int overrideDistancePx)
            => ComputeKeyboardFastTurnInwardDisplacement(leftSide, targetEdgeX, cursorX) > overrideDistancePx;

        /// <summary>
        /// True when the cursor lies inside the inclusive game-client rectangle. The
        /// game-client area (GetClientRect + ClientToScreen) is used, never the outer
        /// window rectangle, so the bot UI and desktop stay human-owned.
        /// </summary>
        internal static bool IsCursorInsideKeyboardFastTurnClient(
            int clientLeft,
            int clientTop,
            int clientRight,
            int clientBottom,
            int cursorX,
            int cursorY)
            => cursorX >= clientLeft && cursorX <= clientRight &&
               cursorY >= clientTop && cursorY <= clientBottom;

        /// <summary>Overload using an already-resolved client rectangle.</summary>
        private static bool IsCursorInsideKeyboardFastTurnClient(
            KeyboardFastTurnClient client,
            int cursorX,
            int cursorY)
            => IsCursorInsideKeyboardFastTurnClient(
                client.Left, client.Top, client.Right, client.Bottom, cursorX, cursorY);

        /// <summary>
        /// Keeps the current cursor Y when it can be read (clamped inside the client with a
        /// small vertical safety margin), otherwise uses the client vertical centre. The
        /// turn is horizontal, so Y is never changed intentionally.
        /// </summary>
        private static int ResolveKeyboardFastTurnCursorY(KeyboardFastTurnClient client)
        {
            int margin = BotConstants.Movement.KeyboardFastTurnMouseEdgeMarginPx;
            int minY = client.Top + margin;
            int maxY = Math.Max(minY, client.Bottom - margin);

            if (MouseOperations.TryGetCursorPosition(out _, out int cursorY))
                return Math.Clamp(cursorY, minY, maxY);

            return client.Top + (client.Bottom - client.Top) / 2;
        }

        /// <summary>
        /// Pure ownership transition. Returned values are the key to release first and
        /// the key to press second; equal requests produce no key events.
        /// </summary>
        private static (TurnKeyState Release, TurnKeyState Press) GetTurnKeyTransition(
            TurnKeyState current,
            TurnKeyState desired)
        {
            if (current == desired)
                return (TurnKeyState.None, TurnKeyState.None);

            return (current, desired);
        }

        private bool SetTurnKey(TurnKeyState desired, string transitionDetails)
        {
            lock (_inputLock)
            {
                if (Volatile.Read(ref _stopRequested) != 0 && desired != TurnKeyState.None)
                    desired = TurnKeyState.None;
                TurnKeyState previous = _heldTurnKey;
                (TurnKeyState release, TurnKeyState press) = GetTurnKeyTransition(previous, desired);
                if (release == TurnKeyState.None && press == TurnKeyState.None)
                    return false;

                if (release != TurnKeyState.None)
                    SendTurnKeyEvent(release == TurnKeyState.LeftA, keyUp: true);
                if (press != TurnKeyState.None)
                    SendTurnKeyEvent(press == TurnKeyState.LeftA, keyUp: false);

                _heldTurnKey = desired;

                if (desired == TurnKeyState.None)
                    _log($"[Steering] {GetTurnKeyName(previous)} up — {transitionDetails}");
                else if (previous != TurnKeyState.None)
                    _log($"[Steering] {GetTurnKeyName(previous)} -> {GetTurnKeyName(desired)}. {transitionDetails}");
                else
                    _log($"[Steering] {GetTurnKeyName(desired)} down. {transitionDetails}");

                return true;
            }
        }

        private static string GetTurnKeyName(TurnKeyState key) => key switch
        {
            TurnKeyState.LeftA => "A",
            TurnKeyState.RightD => "D",
            _ => "None"
        };

        protected virtual void SendTurnKeyEvent(bool leftA, bool keyUp)
        {
            byte vk = leftA ? GameInput.VK_A : GameInput.VK_D;
            byte scan = leftA ? GameInput.SCAN_A : GameInput.SCAN_D;
            uint flags = keyUp ? (uint)GameInput.KEYEVENTF_KEYUP : 0u;
            GameInput.keybd_event(vk, scan, flags, 0);
        }

        private void ReleaseTurnKey(string reason)
        {
            lock (_inputLock)
            {
                SetTurnKey(TurnKeyState.None, reason);
                _keyboardFrozenBearingDeg = null;
                // Direct-camera takeover / recovery release: never leave the accelerated
                // turn cursor behind when keyboard steering no longer owns the turn.
                ReleaseKeyboardFastTurnMouse(reason);
            }
        }

        /// <summary>
        /// Applies a desired bearing: converts bearing to game camera angle (radians),
        /// sets the camera directly (subject to filtering) — WITHOUT pressing W.
        /// Used by ReverseDiagonalRecovery which controls W itself.
        /// </summary>
        private void ApplyCameraBearing(float bearingDeg)
        {
            if (Volatile.Read(ref _stopRequested) != 0)
            {
                StopMoving();
                return;
            }

            if (_lootNavigationBarrierHoldsInput && !IsCombatRecoveryMovementStillValid())
            {
                StopMoving();
                return;
            }

            ReleaseTurnKey("direct camera recovery");

            float cameraRadians = GeometryUtils.ConvertBearingToRadians(bearingDeg);
            _lastSetBearingDeg = bearingDeg;
            _lastSetGameAngle = cameraRadians;

            bool isFreshSegment = !_hasLastGameAngle;
            _hasLastGameAngle = true;

            if (ShouldUpdateCamera(cameraRadians, isFreshSegment, out _))
            {
                _log($"[Camera] Apply brg={bearingDeg:F1}deg target={cameraRadians:F4}rad last={_cameraLastAppliedAngle:F4}rad diff={CircularGameAngleDiff(cameraRadians, _cameraLastAppliedAngle):F4} (no W)");
                if (TrySetCameraAngle(cameraRadians))
                {
                    _cameraLastAppliedAngle = cameraRadians;
                    _lastCameraUpdateTime = DateTime.Now;
                    _hasCameraHysteresisCandidate = false;
                }
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

        private void MoveTowards(float currX, float currY, float targetX, float targetY, MovementSteeringMode steeringMode)
        {
            if (_lootNavigationBarrierHoldsInput)
            {
                StopMoving();
                return;
            }

            float targetBearingDeg = ComputeMoveTowardsBearingDeg(currX, currY, targetX, targetY);
            float dist = GeometryUtils.Distance(currX, currY, targetX, targetY);
            float reachBase = GeometryUtils.GetWaypointReachThreshold(MovementPrecision.Medium);
            float freezeThreshold = Math.Max(reachBase * 2.0f, HeadingFreezeDistanceBase);
            bool nearWaypointHeadingFrozen = dist <= freezeThreshold;

            if (steeringMode == MovementSteeringMode.KeyboardTurn)
            {
                // Keep the existing freeze distance calculation; freeze the desired
                // waypoint bearing instead of the last directly-written camera angle.
                if (nearWaypointHeadingFrozen)
                {
                    if (!_keyboardFrozenBearingDeg.HasValue)
                    {
                        _keyboardFrozenBearingDeg = targetBearingDeg;
                        _log($"[Steering] Near-waypoint heading frozen at {_keyboardFrozenBearingDeg.Value:F1}°");
                    }

                    targetBearingDeg = _keyboardFrozenBearingDeg.Value;
                }
                else
                {
                    _keyboardFrozenBearingDeg = null;
                }
            }

            // First step of a new path: finish the turn in place before W is pressed.
            // Later waypoints keep the normal move-and-turn steering.
            if (_initialHeadingAlignmentPending &&
                TryAlignInitialHeading(targetBearingDeg, steeringMode))
            {
                return;
            }

            if (steeringMode == MovementSteeringMode.KeyboardTurn)
            {
                // Continue feedback steering against the frozen target so an unfinished
                // turn is never left held indefinitely when entering the freeze region.
                ApplySteeringBearing(targetBearingDeg, steeringMode);
                return;
            }

            // Preserve the legacy direct-camera near-waypoint behavior: once a camera
            // heading exists, keep W held and stop changing that heading inside the
            // original freeze distance.
            if (_hasLastGameAngle && nearWaypointHeadingFrozen)
            {
                StartMoving();
                return;
            }

            ApplySteeringBearing(targetBearingDeg, steeringMode);
        }

        /// <summary>
        /// True when the current camera bearing is close enough to the first waypoint's
        /// desired bearing that forward movement can safely start.
        /// </summary>
        internal static bool IsInitialHeadingAligned(float currentBearingDeg, float desiredBearingDeg)
            => Math.Abs(GeometryUtils.GetShortestBearingDiffDeg(currentBearingDeg, desiredBearingDeg)) <=
               BotConstants.Movement.InitialHeadingAlignmentToleranceDegrees;

        /// <summary>
        /// Applies a waypoint's optional horizontal camera yaw exactly once per leg.
        /// The value is the same 32-bit float written at +0x1A8 (left/right camera).
        /// W is released before the verified yaw write, and the method only returns
        /// after the configured settle delay, so the caller's next W-down is always the
        /// first forward input at the new bearing. The explicit bearing replaces the
        /// first-step in-place A/D alignment: turning away from it before W would defeat
        /// the whole purpose of the setting.
        /// </summary>
        private async Task EnsureWaypointStartCameraAngleAsync(
            Waypoint target,
            CancellationToken token)
        {
            // Operation steps have no position/movement leg of their own; their named
            // operation owns the camera while it runs.
            if (target.IsOperationStep)
                return;

            if (!target.StartCameraAngleBits.HasValue)
            {
                // Leaving a leg that had a configured angle without ever consuming the
                // DirectCamera deferral must not leak it into this leg.
                _startCameraAngleAwaitingFirstW = false;
                return;
            }

            int angleBits = target.StartCameraAngleBits.Value;

            if (_startCameraAngleApplied &&
                Math.Abs(_startCameraAngleAppliedX - target.X) < 0.01f &&
                Math.Abs(_startCameraAngleAppliedY - target.Y) < 0.01f &&
                _startCameraAngleAppliedBits == angleBits)
                return;

            // A new leg supersedes any unconsumed DirectCamera deferral from the old one.
            _startCameraAngleAwaitingFirstW = false;

            // The camera must be final before W goes down. StopMoving is unconditional
            // and idempotent; it also clears any turn key left from the previous leg.
            StopMoving();

            bool applied = _wKeyCameraController != null
                ? _wKeyCameraController.TrySetCameraAngleBits(angleBits)
                : _memoryService.TrySetCameraAngleBits(angleBits);

            string description = CameraAngleValue.Describe(angleBits);
            if (!applied)
            {
                _log($"[StartCam] Waypoint ({target.X:F1},{target.Y:F1}) — could not write start camera {description}; the route continues and the write will be retried on the next tick.");
                return;
            }

            _log($"[StartCam] Waypoint ({target.X:F1},{target.Y:F1}) — camera set to {description} with W released; settling {BotConstants.Camera.StartAngleSettleMs} ms before W.");
            await Task.Delay(BotConstants.Camera.StartAngleSettleMs, token);

            // The configured bearing is the intended W-down orientation. Do not run the
            // first-step A/D alignment, which would rotate the camera away from it.
            _initialHeadingAlignmentPending = false;
            _initialHeadingAlignmentInProgress = false;
            _initialHeadingAlignmentDeadlineUtc = DateTime.MinValue;

            _startCameraAngleApplied = true;
            _startCameraAngleAppliedX = target.X;
            _startCameraAngleAppliedY = target.Y;
            _startCameraAngleAppliedBits = angleBits;
            _startCameraAngleAwaitingFirstW = true;
        }

        /// <summary>
        /// Forgets the last applied start-angle target without touching the camera. Called
        /// when the route consumes a waypoint or rebuilds its queue so the next leg is
        /// evaluated from scratch.
        /// </summary>
        private void ClearStartCameraAngleApplication()
        {
            _startCameraAngleApplied = false;
            _startCameraAngleAppliedX = 0f;
            _startCameraAngleAppliedY = 0f;
            _startCameraAngleAppliedBits = 0;
        }

        /// <summary>
        /// Handles the first route step's heading alignment. Returns true while the bot is
        /// still turning in place (the caller must not start moving); false once the turn
        /// has finished or the safety timeout expired, so the normal steering/movement
        /// path can run. Only the very first step of a freshly started path calls this.
        /// </summary>
        private bool TryAlignInitialHeading(float desiredBearingDeg, MovementSteeringMode steeringMode)
        {
            DateTime now = DateTime.UtcNow;
            float currentBearingDeg = GeometryUtils.ConvertRadiansToBearingDeg(_memoryService.GetCameraAngle());
            float error = GeometryUtils.GetShortestBearingDiffDeg(currentBearingDeg, desiredBearingDeg);

            if (IsInitialHeadingAligned(currentBearingDeg, desiredBearingDeg))
            {
                _initialHeadingAlignmentPending = false;
                _initialHeadingAlignmentInProgress = false;
                _log($"[InitialAlign] Heading aligned at {currentBearingDeg:F1}° (target {desiredBearingDeg:F1}°, error {error:+0.0;-0.0;0.0}°) — starting forward movement.");
                return false;
            }

            if (_initialHeadingAlignmentInProgress && now >= _initialHeadingAlignmentDeadlineUtc)
            {
                _initialHeadingAlignmentPending = false;
                _initialHeadingAlignmentInProgress = false;
                _log($"[InitialAlign] Timeout after {BotConstants.Movement.InitialHeadingAlignmentTimeoutMs}ms (current {currentBearingDeg:F1}°, target {desiredBearingDeg:F1}°, error {error:+0.0;-0.0;0.0}°) — starting forward movement anyway.");
                return false;
            }

            if (!_initialHeadingAlignmentInProgress)
            {
                // Guarantee no stale W/A/D from the previous path before the in-place turn.
                // The turn keys are re-acquired immediately by ApplySteeringBearing below.
                StopMoving();
                _initialHeadingAlignmentInProgress = true;
                _initialHeadingAlignmentDeadlineUtc = now.AddMilliseconds(
                    BotConstants.Movement.InitialHeadingAlignmentTimeoutMs);
                _log($"[InitialAlign] New path first step — turning in place to {desiredBearingDeg:F1}° before moving (current {currentBearingDeg:F1}°, error {error:+0.0;-0.0;0.0}°, steering={steeringMode}).");
            }

            // Turn in place: A/D and the fast-turn mouse may run, but W must stay released
            // until the first step's heading is within tolerance.
            ApplySteeringBearing(desiredBearingDeg, steeringMode, moveForward: false);
            _initialHeadingAlignmentLastTick = _tickCount;
            return true;
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
            _keyboardFrozenBearingDeg = null;
        }

        /// <summary>
        /// Re-arms the first-step heading alignment after a teleport / map change. The
        /// next route step is then treated like the first step of a new path: turn in
        /// place to the target bearing before W is pressed.
        /// </summary>
        private void RearmInitialHeadingAlignment(string reason)
        {
            bool wasPending = _initialHeadingAlignmentPending;
            _initialHeadingAlignmentPending = true;
            _initialHeadingAlignmentInProgress = false;
            _initialHeadingAlignmentDeadlineUtc = DateTime.MinValue;

            if (!wasPending)
                _log($"[InitialAlign] Re-armed for the next first step ({reason}).");
        }

        private void ResetActionStuckTracking()
        {
            _stuckDetector.ResetTracking();
        }

        // ── Recovery input ownership (Phase D) ──
        // Recovery routines (reverse diagonal, waypoint special recovery, map-transition
        // hold) own movement and camera exclusively. The parallel loot task must not
        // start new pixel scans while this is true, and recovery start cancels any
        // in-flight loot scan before it takes the input.
        private bool _recoveryOwnershipHeld;
        private DateTime _recoveryOwnershipDeferLogAt = DateTime.MinValue;

        /// <summary>
        /// True while a recovery routine or a map-transition hold owns movement/camera.
        /// The host's loot loop checks this and must not start new scans while it is true.
        /// </summary>
        public bool IsRecoveryInputOwnershipActive =>
            _recoveryOwnershipHeld ||
            Volatile.Read(ref _stopRequested) != 0 ||
            _navigationFailureEscalationRequested ||
            _isUnstuckRoutineActive ||
            _reverseDiagonalRecovery.IsActive ||
            _isWaypointSpecialRecoveryActive ||
            _waypointMobRecovery?.IsActive == true ||
            _mapTransitionGuardActive ||
            _mapTransitionConfirmed;

        /// <summary>
        /// Takes exclusive input ownership for a recovery/map-transition hold and
        /// synchronously quiesces any in-flight loot scan (pending obligations are
        /// deferred, never completed). Idempotent.
        /// </summary>
        private void AcquireRecoveryOwnership(string reason)
        {
            // Cancel/quiesce the scan first: the loot task may be mid-capture or
            // mid-collection and must not race the recovery input.
            LootSystemRef?.SuspendForRecovery(reason);
            if (_recoveryOwnershipHeld)
                return;

            _recoveryOwnershipHeld = true;
            _log($"[LootOwnership] Recovery ownership acquired — {reason}.");
        }

        private void ReleaseRecoveryOwnership(string reason)
        {
            if (!_recoveryOwnershipHeld)
                return;

            _recoveryOwnershipHeld = false;
            _log($"[LootOwnership] Recovery ownership released — {reason}.");
        }

        /// <summary>
        /// True while an in-flight loot collection (mouse left-click, character walking
        /// to the item) still owns movement/cursor. Recovery input must wait for it.
        /// </summary>
        private bool LootCollectionStillOwnsInput() =>
            !IsAutomaticLootDisabled && LootSystemRef?.IsCollecting == true;

        private void LogRecoveryDeferredForLoot()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _recoveryOwnershipDeferLogAt).TotalSeconds < 1.0)
                return;
            _recoveryOwnershipDeferLogAt = now;
            _log("[LootOwnership] Recovery deferred — an in-flight loot collection still owns movement/cursor.");
        }

        /// <summary>
        /// Authoritative map invariant for a route that declares an expected map. Uses one
        /// coherent player-state snapshot per tick (never independent map/position reads).
        ///
        /// Returns false while navigation must stay held: either a different valid map was
        /// confirmed (route permanently invalidated) or a suspected change is still being
        /// confirmed. Unreadable / zero map reads are ignored as destination evidence and
        /// only keep an already-suspended route suspended.
        ///
        /// When the expected map is confirmed again before a different map is confirmed,
        /// the old navigation baselines are discarded and the route resumes safely.
        /// </summary>
        private bool EvaluateRouteMapGuard()
        {
            if (_mapTransitionConfirmed)
                return false;

            PlayerStateSnapshot snapshot = _memoryService.GetPlayerStateSnapshot();

            if (!snapshot.HasCoherentState)
            {
                // Transient invalid/zero map or position read — never evidence of a
                // destination. A suspended route stays suspended until a coherent read.
                return !_mapTransitionGuardActive;
            }

            if (snapshot.Map == ExpectedMap)
            {
                _lastCoherentSnapshot = snapshot;

                if (_currentMapId != ExpectedMap)
                {
                    _connectionWatchdog.Reset();
                    _localNavigationMap.ChangeMap(ExpectedMap);
                    _currentMapId = ExpectedMap;
                }

                if (_mapTransitionGuardActive)
                {
                    _mapTransitionGuardActive = false;
                    _mapTransitionObservedMap = 0;
                    _mapTransitionConsecutiveReads = 0;
                    // Back on the expected map: discard every baseline tied to the
                    // aborted transition before resuming. Never carry a stuck count or
                    // a recovery state across the map boundary.
                    CancelWaypointSpecialRecovery();
                    ResetStuckAndUnstuckState($"exp map restored {ExpectedMap}");
                    ReleaseRecoveryOwnership($"expected map {ExpectedMap} re-confirmed");
                    _log($"[ExpMapGuard] expected={ExpectedMap} observed={snapshot.Map} route='{RouteLabel}' confirmation=restored — baselines reset, resuming.");
                }

                return true;
            }

            // A coherent, nonzero map different from the expected one.
            if (!_mapTransitionGuardActive)
            {
                _mapTransitionGuardActive = true;
                _mapTransitionObservedMap = snapshot.Map;
                _mapTransitionConsecutiveReads = 1;
                HoldRouteForMapTransition(snapshot);
            }
            else if (snapshot.Map == _mapTransitionObservedMap)
            {
                _mapTransitionConsecutiveReads++;
            }
            else
            {
                // The destination changed again — restart confirmation on the new map.
                _mapTransitionObservedMap = snapshot.Map;
                _mapTransitionConsecutiveReads = 1;
            }

            if (_mapTransitionConsecutiveReads >= BotConstants.Delays.MapTransitionStableReadCount)
            {
                _mapTransitionConfirmed = true;
                ConfirmedTransitionMap = snapshot.Map;
                _log($"[ExpMapGuard] expected={ExpectedMap} observed={snapshot.Map} route='{RouteLabel}' confirmation=confirmed.");
                _log($"[ExpMapGuard] Route invalidation reason: player left expected map {ExpectedMap} for map {snapshot.Map} (route '{RouteLabel}').");
                return false;
            }

            return false;
        }

        /// <summary>
        /// Suspends every input at the first valid read of a different map. No attack,
        /// navigation or recovery may start while the change is unconfirmed, and no
        /// ReverseDiagonal is started merely because the coordinates changed.
        /// </summary>
        private void HoldRouteForMapTransition(PlayerStateSnapshot observed)
        {
            AcquireRecoveryOwnership($"map transition hold (expected={ExpectedMap} observed={observed.Map})");
            CancelWaypointSpecialRecovery();
            CancelCombatGateWaypointReturn("map transition hold");
            CancelMobGrouping("map transition hold");
            StopMoving();
            ReleaseCombatKeys();
            _log($"[ExpMapGuard] expected={ExpectedMap} observed={observed.Map} route='{RouteLabel}' confirmation=pending — navigation held.");
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
            CancelCombatGateWaypointReturn($"route reset: {reason}");
            CancelMobGrouping($"unstuck state reset: {reason}");
            _reverseDiagonalRecovery.Stop();
            EndCombatRecoveryMovement($"unstuck reset: {reason}");
            ReleaseRecoveryOwnership($"unstuck reset: {reason}");
            _isUnstuckRoutineActive = false;
            _consecutiveStuckAttempts = 0;
            _lastStuckAttemptPos = null;
            ResetNavigationProgressTracking();
            ResetActionStuckTracking();
            _lastMoveProgressPos = null;
            _lastMoveProgressTime = DateTime.MinValue;
            _repositionRetryCount = 0;
            _lastRepositionPos = null;
            _combatUnstuckDeniedCount = 0;
            _combatRecoverySuspendedByLootHold = false;
            _combatGateReturnFailedAttempts = 0;
            _combatGateReturnDeadlockSince = DateTime.MinValue;
            _combatGateReturnDeadlockDetail = string.Empty;
            _combatGateReturnDeadlockEscalated = false;
            _routeResyncPendingAfterCombat = false;
            _attackSuppressedForCurrentWaypoint = false;
            ClearCombatGateTargetSuppression();
            _combatHandler.ResetState();
            _resumeCombatSkillAfterGrouping = false;
            ResetPostKillRetargetState();
            ReleaseSkillThree();
            StopMoving();
            // The teleport/map change invalidates the old camera context: the next route
            // step must turn in place to its bearing before moving again.
            RearmInitialHeadingAlignment(reason);
            _log($"[Unstuck] Full reset ({reason}) — old route/unstuck discarded.");
        }

        private void ResetCombatStateForWaypointChange()
        {
            CancelCombatGateWaypointReturn("waypoint replaced");
            _combatGateReturnFailedAttempts = 0;
            _attackSuppressedForCurrentWaypoint = false;
            _combatHandler.ResetState();
            _resumeCombatSkillAfterGrouping = false;
            ResetPostKillRetargetState();
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
            CancelMobGrouping("waypoint special recovery started");
            _resumeCombatSkillAfterGrouping = false;
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
            TryApplyMovementCameraDistance(target.CameraDistanceLock);
            if (succeeded)
            {
                ResetBearingState();
                _routeResyncPendingAfterCombat = true;
            }

            _activeSpecialRecoveryTarget = null;
            _isWaypointSpecialRecoveryActive = false;
            _isUnstuckRoutineActive = false;
            ReleaseRecoveryOwnership($"special recovery finished ({target.X:F0},{target.Y:F0})");
        }

        private void EnsureNavigationProgressWaypoint(Waypoint target, float currX, float currY)
        {
            if (_navigationProgressWaypointId.Length > 0 &&
                Math.Abs(_navigationProgressTargetX - target.X) < 0.01f &&
                Math.Abs(_navigationProgressTargetY - target.Y) < 0.01f)
                return;

            _navigationWaypointSequence++;
            _navigationProgressTargetX = target.X;
            _navigationProgressTargetY = target.Y;
            _navigationProgressWaypointId = $"WP{_navigationWaypointSequence}({target.X:F1},{target.Y:F1})";
            _bestWaypointDistance = GeometryUtils.Distance(currX, currY, target.X, target.Y);
            _lastNavigationProgressDistance = _bestWaypointDistance;
            _lastNavigationProgressTime = DateTime.MinValue;
            _awaitingNavigationRecoveryProof = false;
            _log($"[NavigationProgress] Tracking waypoint={_navigationProgressWaypointId} bestDistance={_bestWaypointDistance:F2}.");
        }

        private async Task<bool> UpdateNavigationProgress(
            Waypoint target,
            float currX,
            float currY,
            CancellationToken token)
        {
            EnsureNavigationProgressWaypoint(target, currX, currY);
            DateTime now = DateTime.UtcNow;
            float distance = GeometryUtils.Distance(currX, currY, target.X, target.Y);
            bool recoveryOwnsMovement = _reverseDiagonalRecovery.IsActive || _isUnstuckRoutineActive;

            // The no-progress window starts with the first forward-movement tick of a
            // tracked waypoint. It must NOT wait for a previous stuck attempt: the other
            // detectors are vetoed close to the waypoint and on unknown action bytes, so
            // gating this clock on _consecutiveStuckAttempts > 0 made the first stall of
            // every waypoint invisible (noProgress stayed 0 while W was held forever).
            // Intentional stops (loot/combat holds, in-place first-turn alignment, pause)
            // end the window; the next forward-movement tick starts a fresh one.
            if (!recoveryOwnsMovement)
            {
                if (_isMovingForward)
                {
                    if (_lastNavigationProgressTime == DateTime.MinValue)
                    {
                        _lastNavigationProgressDistance = distance;
                        _lastNavigationProgressTime = now;
                    }
                }
                else if (_lastNavigationProgressTime != DateTime.MinValue)
                {
                    // Navigation intentionally has no forward input: do not count the
                    // stationary period against the waypoint.
                    _lastNavigationProgressDistance = distance;
                    _lastNavigationProgressTime = DateTime.MinValue;
                }
            }

            // Keep the best physical distance monotonically, but a smaller distance
            // observed during ReverseDiagonal is not navigation progress and must not
            // refresh the 15-second clock or prove route recovery. Ordinary route
            // movement must beat this best value after the local maneuver has ended.
            if (distance < _bestWaypointDistance - 0.01f)
                _bestWaypointDistance = distance;

            if (_reverseDiagonalRecovery.IsActive)
            {
                if (distance < _lastNavigationProgressDistance - 0.01f)
                {
                    // A recovery that happened to move toward the waypoint may improve
                    // the best/reference distance, but it never refreshes the timer.
                    _lastNavigationProgressDistance = distance;
                    _log($"[NavigationProgress] waypoint={_navigationProgressWaypointId} bestDistance={_bestWaypointDistance:F2} observedDuringRecovery=True; navigation timer unchanged.");
                }
            }
            else if (distance <= _lastNavigationProgressDistance - NAVIGATION_PROGRESS_MIN_DISTANCE)
            {
                // Genuine route-directed progress, independent of whether an earlier
                // unstuck attempt happened. Small fluctuations and recovery detours are
                // excluded by the 1.0-unit threshold. This is the ONLY physical-movement
                // evidence allowed to resolve the waypoint's failure history: arbitrary
                // displacement or a successful escape maneuver alone never does.
                _lastNavigationProgressDistance = distance;
                if (_lastNavigationProgressTime != DateTime.MinValue)
                    _lastNavigationProgressTime = now;

                bool recoveryProven = _awaitingNavigationRecoveryProof;
                int previousFailures = _consecutiveStuckAttempts;
                if (recoveryProven)
                    _awaitingNavigationRecoveryProof = false;

                if (previousFailures > 0)
                {
                    // The previous blockage is demonstrably resolved: navigation is moving
                    // toward the destination again. Keeping the old counter made the NEXT
                    // ActionStuck escalate immediately (2/2 in the incident) even though
                    // NavigationRecovered=True and the distance had improved 8.25 -> 3.61.
                    // The next genuine stuck therefore starts a fresh unstuck budget.
                    _consecutiveStuckAttempts = 0;
                    _lastStuckAttemptPos = null;
                }

                if (recoveryProven)
                {
                    _log($"[Navigation] NavigationRecovered=True waypoint={_navigationProgressWaypointId} bestDistance={_bestWaypointDistance:F2}; navigation failure count reset (was {previousFailures}) after confirmed route-directed progress.");
                }
                else if (previousFailures > 0)
                {
                    _log($"[NavigationProgress] waypoint={_navigationProgressWaypointId} bestDistance={_bestWaypointDistance:F2}; route-directed progress confirmed; navigation failure count reset (was {previousFailures}).");
                }
            }

            if (_tickCount % _stateLogInterval == 0)
            {
                int maxAttempts = _memoryService.GetIsInCity()
                    ? STUCK_MAX_ATTEMPTS_IN_CITY
                    : STUCK_MAX_ATTEMPTS_OUTSIDE;
                double noProgressSeconds = _lastNavigationProgressTime == DateTime.MinValue
                    ? 0
                    : (now - _lastNavigationProgressTime).TotalSeconds;
                _log($"[NavigationProgress] waypoint={_navigationProgressWaypointId} pos=({currX:F1},{currY:F1}) distance={distance:F2} bestDistance={_bestWaypointDistance:F2} failures={_consecutiveStuckAttempts}/{maxAttempts} noProgress={noProgressSeconds:F1}/{NAVIGATION_NO_PROGRESS_TIMEOUT_SECONDS:F0}s.");
            }

            if (_lastNavigationProgressTime != DateTime.MinValue)
            {
                double noProgressSeconds = (now - _lastNavigationProgressTime).TotalSeconds;
                if (noProgressSeconds >= NAVIGATION_NO_PROGRESS_TIMEOUT_SECONDS)
                {
                    if (_consecutiveStuckAttempts == 0)
                    {
                        // First navigation stall for this waypoint: hand it to the existing
                        // confirmed-stuck dispatcher (same reverse-diagonal / configured
                        // waypoint recovery used by ActionStuck and NoProgress, including
                        // loot-ownership deferral). Leaving the timestamp in place lets a
                        // deferred dispatch retry on the next navigation tick.
                        _log($"[NavigationStall] waypoint={_navigationProgressWaypointId} pos=({currX:F1},{currY:F1}) distance={distance:F2} bestDistance={_bestWaypointDistance:F2} noProgress={noProgressSeconds:F1}s/{NAVIGATION_NO_PROGRESS_TIMEOUT_SECONDS:F0}s while forward movement was requested — invoking existing unstuck.");
                        await HandleConfirmedNavigationStuckAsync(currX, currY, target, token, "NavigationNoProgress");
                        return true;
                    }

                    string reason = $"no genuine waypoint progress for {noProgressSeconds:F1}s (limit {NAVIGATION_NO_PROGRESS_TIMEOUT_SECONDS:F0}s)";
                    return EscalateNavigationFailure(reason, token);
                }
            }

            return false;
        }

        private bool EscalateNavigationFailure(string reason, CancellationToken token)
        {
            if (token.IsCancellationRequested || Volatile.Read(ref _stopRequested) != 0)
                return false;

            int maxAttempts = _memoryService.GetIsInCity()
                ? STUCK_MAX_ATTEMPTS_IN_CITY
                : STUCK_MAX_ATTEMPTS_OUTSIDE;
            double noProgressSeconds = _lastNavigationProgressTime == DateTime.MinValue
                ? 0
                : (DateTime.UtcNow - _lastNavigationProgressTime).TotalSeconds;
            _log($"[NavigationFailure] Escalating to city workflow: reason={reason}; waypoint={_navigationProgressWaypointId}; failures={_consecutiveStuckAttempts}/{maxAttempts}; bestDistance={_bestWaypointDistance:F2}; noProgress={noProgressSeconds:F1}s.");

            if (!InternalRepotEnabled)
            {
                _navigationFailureEscalationRequested = true;
                LootSystemRef?.SuspendForRecovery("navigation failure escalation");
                CancelCombatGateWaypointReturn("navigation failure escalation");
                CancelMobGrouping("navigation failure escalation");
                _reverseDiagonalRecovery.Stop();
                EndCombatRecoveryMovement("navigation failure escalation");
                _isUnstuckRoutineActive = false;
                ReleaseRecoveryOwnership("navigation failure escalation");
                Stop();
                return true;
            }

            CancelCombatGateWaypointReturn("navigation failure escalation");
            CancelMobGrouping("navigation failure escalation");
            _reverseDiagonalRecovery.Stop();
            EndCombatRecoveryMovement("navigation failure escalation");
            _isUnstuckRoutineActive = false;
            ReleaseRecoveryOwnership("navigation failure escalation");
            StopMoving();
            ReleaseCombatKeys();
            if (token.IsCancellationRequested || Volatile.Read(ref _stopRequested) != 0)
                return true;
            _repotHelper.ReportAndGoBack();
            return true;
        }

        private void ResetNavigationProgressTracking()
        {
            _navigationProgressWaypointId = "";
            _navigationProgressTargetX = float.NaN;
            _navigationProgressTargetY = float.NaN;
            _bestWaypointDistance = float.MaxValue;
            _lastNavigationProgressDistance = float.MaxValue;
            _lastNavigationProgressTime = DateTime.MinValue;
            _awaitingNavigationRecoveryProof = false;
        }

        private async Task HandleConfirmedNavigationStuckAsync(
            float currX,
            float currY,
            Waypoint target,
            CancellationToken token,
            string source)
        {
            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _stopRequested) != 0)
                return;

            // Recovery owns movement/camera exclusively: quiesce any in-flight loot
            // scan first, and wait out a loot collection that is already walking the
            // character to an item. The stuck dispatch is retried next tick.
            AcquireRecoveryOwnership($"stuck dispatch ({source}) at ({currX:F0},{currY:F0})");
            if (LootCollectionStillOwnsInput())
            {
                LogRecoveryDeferredForLoot();
                return;
            }

            // This branch deliberately remains the direct legacy path. Do not put
            // special-recovery preparation before it: Default must have the same
            // escalation/counter/reverse-diagonal behavior as current master.
            if (!_enableWaypointSpecialRecoveries ||
                target.StuckRecoveryType == WaypointStuckRecoveryType.Default)
            {
                StartReverseDiagonalRecovery(currX, currY, target, token);
                return;
            }

            if (_specialRecoveryAttempted)
            {
                _log("[WaypointRecovery] Special recovery already attempted at this stuck location with no real progress — using standard unstuck.");
                StartReverseDiagonalRecovery(currX, currY, target, token);
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
                    if (!token.IsCancellationRequested && Volatile.Read(ref _stopRequested) == 0)
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
                    ReleaseSkillThree,
                    () => _lootNavigationBarrierHoldsInput,
                    _wKeyCameraController == null
                        ? null
                        : () => _wKeyCameraController.TrySetStationaryWhenIdle("waypoint mob recovery"),
                    () => _targetSafety.IsConfirmedMonster());
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

            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _stopRequested) != 0)
            {
                FinishSpecialRecovery(target, succeeded: false);
                return;
            }

            _log("[WaypointRecovery] Special recovery failed — falling back to standard unstuck.");
            FinishSpecialRecovery(target, succeeded: false);
            StartReverseDiagonalRecovery(currX, currY, target, token);
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
        private void StartReverseDiagonalRecovery(
            float currX,
            float currY,
            Waypoint target,
            CancellationToken token = default)
        {
            if (token.IsCancellationRequested || Volatile.Read(ref _stopRequested) != 0)
                return;

            // A failed workflow route must never start another recovery. This also makes
            // the fatal state robust against a late/duplicate stuck dispatch while
            // PathRunner is aborting the route.
            if (_isCityStuckFatal)
            {
                _isUnstuckRoutineActive = false;
                StopMoving();
                ReleaseCombatKeys();
                return;
            }

            // Recovery owns movement/camera exclusively. Quiesce any in-flight loot
            // scan and wait out a collection that is already walking the character to
            // an item; the stuck detection re-dispatches next tick.
            AcquireRecoveryOwnership($"ReverseDiagonal at ({currX:F0},{currY:F0})");
            if (LootCollectionStillOwnsInput())
            {
                LogRecoveryDeferredForLoot();
                return;
            }

            CancelMobGrouping("ReverseDiagonalRecovery started");
            EnsureNavigationProgressWaypoint(target, currX, currY);
            _consecutiveStuckAttempts++;
            _lastStuckAttemptPos = (currX, currY);
            // A recovery attempt owns the movement input: start its own verified-progress
            // window so the elapsed time from the preceding stall cannot immediately
            // escalate (or re-dispatch) while the maneuver is still running.
            _lastNavigationProgressDistance = _bestWaypointDistance;
            _lastNavigationProgressTime = DateTime.UtcNow;
            bool inCity = _memoryService.GetIsInCity();
            int maxAttempts = inCity ? STUCK_MAX_ATTEMPTS_IN_CITY : STUCK_MAX_ATTEMPTS_OUTSIDE;
            double noProgressSeconds = (DateTime.UtcNow - _lastNavigationProgressTime).TotalSeconds;
            _log($"[Unstuck] waypoint={_navigationProgressWaypointId} Attempt={_consecutiveStuckAttempts}/{maxAttempts} bestDistance={_bestWaypointDistance:F2} noProgress={noProgressSeconds:F1}s City={inCity}.");

            if (_consecutiveStuckAttempts >= maxAttempts)
            {
                CaptureStuckScreenshot();
                _reverseDiagonalRecovery.Stop();
                _isUnstuckRoutineActive = false;

                if (!InternalRepotEnabled)
                {
                    // Workflow mode: the coordinator owns repot. Stop the route right here —
                    // no teleport key and no idle on this dead route. The coordinator waits
                    // 10 minutes, resets the failure bookkeeping and restarts the flow from
                    // the Repot step (go repot → start again).
                    _log($"[Unstuck] CITY STUCK LIMIT REACHED ({_consecutiveStuckAttempts}/{maxAttempts}) — stopping the route; the coordinator waits 10 min, resets and restarts from the Repot step.");
                    EscalateNavigationFailure(
                        $"navigation failure limit reached ({_consecutiveStuckAttempts}/{maxAttempts}) for the same waypoint",
                        token);
                    return;
                }

                _consecutiveStuckAttempts = 0;
                _lastStuckAttemptPos = null;

                if (inCity)
                {
                    // Legacy mode: no external repot flow — press the town teleport and
                    // wait before retrying.
                    _log("[Unstuck] CITY STUCK LIMIT REACHED.");
                    _log($"[Unstuck] Reached {maxAttempts} stuck attempts in city — pressing 6 and waiting 10 minutes.");
                    StopMoving();
                    ReleaseCombatKeys();
                    if (token.IsCancellationRequested || Volatile.Read(ref _stopRequested) != 0)
                        return;
                    GameInput.PressKey(GameInput.VK_6, GameInput.SCAN_6);

                    // Screenshot immediately after the scroll is triggered — before the
                    // town starts loading — so the image shows the state at teleport time.
                    ScreenshotService.CaptureFullScreen(ScreenshotService.TownPortalsFolder, _log, "[Unstuck]");

                    _inCityStuckCooldownUntil = DateTime.Now.AddMinutes(10);
                }
                else
                {
                    _log($"[Unstuck] OUTSIDE STUCK LIMIT REACHED ({maxAttempts}).");
                    _log($"[Unstuck] Triggering ReportAndGoBack.");
                    StopMoving();
                    ReleaseCombatKeys();
                    if (token.IsCancellationRequested || Volatile.Read(ref _stopRequested) != 0)
                        return;
                    _repotHelper.ReportAndGoBack();
                }

                return;
            }

            lock (_inputLock)
            {
                if (token.IsCancellationRequested || Volatile.Read(ref _stopRequested) != 0)
                {
                    ReleaseRecoveryOwnership("navigation recovery cancelled before start");
                    return;
                }

                _reverseDiagonalRecovery.Start(currX, currY, target.X, target.Y,
                    _lastHealthyMoveBearingDeg != UnsetBearing ? _lastHealthyMoveBearingDeg : (float?)null,
                    _lastHealthyMoveTime);

                // Mark the unstuck routine as active: while it runs, the combat handler
                // returns None (no TAB / no attack) and the route resync is skipped, so the
                // recovery is never interrupted and the player really walks away from the
                // stuck spot.
                _isUnstuckRoutineActive = true;
            }
            ApplyCameraBearing(_reverseDiagonalRecovery.CurrentBearingDeg); // camera only — Recovery controls W
        }

        /// <summary>
        /// Standard unstuck for the combat case: an unreachable / not-dying mob triggers
        /// the same reverse-diagonal recovery as a movement stuck, so the player really
        /// walks away from the spot instead of just TABbing. The recovery runs
        /// uninterrupted (combat is suppressed via <see cref="_isUnstuckRoutineActive"/>).
        /// </summary>
        private void StartCombatUnstuck(float currX, float currY)
        {
            CancelMobGrouping("combat unstuck started");
            ReleaseSkillThree();

            // The bounded denial limit already tripped: stay quiet and inactive while the
            // loot hold still owns the input (it was reported once).
            if (_combatRecoverySuspendedByLootHold)
            {
                if (_lootNavigationBarrierHoldsInput)
                    return;

                // The hold released — the normal bounded recovery chain may run again.
                _combatRecoverySuspendedByLootHold = false;
                _combatUnstuckDeniedCount = 0;
                _combatRecoveryMovementDeniedLogged = false;
            }

            // A combat-owned recovery may physically walk only under the exact Deferred
            // post-kill authorization; ordinary waypoint recovery never receives it. When the
            // mandatory loot hold owns the input and refuses the authorization, starting the
            // ReverseDiagonalRecovery would leave the unstuck routine active while neither the
            // camera nor W can run — so it is not started at all and the denial is bounded.
            if (_lootNavigationBarrierHoldsInput && !TryBeginCombatRecoveryMovement("combat unstuck"))
            {
                _combatUnstuckDeniedCount++;
                if (_combatUnstuckDeniedCount >= COMBAT_UNSTUCK_MAX_DENIED_ATTEMPTS)
                {
                    _combatUnstuckDeniedCount = 0;
                    _combatRecoverySuspendedByLootHold = true;
                    _combatRecoveryMovementDeniedLogged = false;
                    _log("[CombatRecovery] FATAL: combat unstuck cannot run under the mandatory loot hold — " +
                         "no recovery is active and no recovery movement is attempted; loot obligation preserved.");
                }
                else
                {
                    _log($"[CombatRecovery] Combat unstuck not started ({_combatUnstuckDeniedCount}/{COMBAT_UNSTUCK_MAX_DENIED_ATTEMPTS}) — " +
                         "recovery authorization refused by the mandatory loot hold; loot obligation preserved.");
                }
                return;
            }

            Waypoint target = _waypoints.Count > 0
                ? _waypoints.Peek()
                : new Waypoint(Waypoint2.X, Waypoint2.Y, GlobalPrecision, BotMode.OnlyMove);
            _log("[Combat] Unreachable mob — starting standard unstuck (reverse-diagonal recovery).");

            StartReverseDiagonalRecovery(currX, currY, target);

            // The recovery never started (fatal route / loot collection still owns input):
            // drop the authorization immediately so no input owner is left dangling.
            if (!_reverseDiagonalRecovery.IsActive)
                EndCombatRecoveryMovement("reverse diagonal did not start");
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
            CancelMobGrouping("RepositionAndRetry has priority");
            ReleaseSkillThree();
            ClearCombatRetargetSearch();

            // Bounded combat-unstuck denials suspended recovery movement while the mandatory
            // loot hold owns the input. Retry only once the hold is gone.
            if (_combatRecoverySuspendedByLootHold)
            {
                if (_lootNavigationBarrierHoldsInput)
                {
                    if (!_combatRecoveryMovementDeniedLogged)
                    {
                        _combatRecoveryMovementDeniedLogged = true;
                        _log("[CombatRecovery] Combat recovery movement suspended by the mandatory loot hold — " +
                             "no reposition and no TAB; loot obligation preserved.");
                    }

                    await Task.Delay(BotConstants.Combat.CombatRepositionPollMs, token);
                    return;
                }

                _combatRecoverySuspendedByLootHold = false;
                _combatUnstuckDeniedCount = 0;
                _combatRecoveryMovementDeniedLogged = false;
                _repositionRetryCount = 0;
                _lastRepositionPos = null;
            }

            Waypoint target = _waypoints.Count > 0
                ? _waypoints.Peek()
                : new Waypoint(Waypoint2.X, Waypoint2.Y, GlobalPrecision, BotMode.OnlyMove);
            float reachThreshold = GetEffectiveWaypointReachThreshold(target);
            float initialDistance = GeometryUtils.Distance(currX, currY, target.X, target.Y);

            // ── Escalation ──
            // Count consecutive reposition cycles. Only movement that really reduced the
            // distance to the current waypoint resets the counter: walking away from (or
            // sideways to) the waypoint is not progress, so arbitrary displacement in any
            // direction can never postpone the escalation. Repeated cycles from an unchanged
            // position mean the player cannot leave (geometry block + phantom target
            // re-selected by TAB) — escalate to the standard unstuck so the existing
            // escalation chain (reverse-diagonal → ReportAndGoBack) applies.
            if (_lastRepositionPos.HasValue)
            {
                float previousDistance = GeometryUtils.Distance(
                    _lastRepositionPos.Value.X, _lastRepositionPos.Value.Y, target.X, target.Y);
                if (previousDistance - initialDistance >= BotConstants.ReverseDiagonal.SuccessDistImprove)
                {
                    _repositionRetryCount = 0;
                }
            }

            // The baseline always advances to the position this attempt starts from. Progress
            // must be measured per attempt: a stale pre-move baseline kept resetting the
            // counter forever after one successful move, so stalled recoveries never escalated.
            _lastRepositionPos = (currX, currY);

            _repositionRetryCount++;
            if (_repositionRetryCount >= REPOSITION_MAX_ATTEMPTS)
            {
                _repositionRetryCount = 0;
                _lastRepositionPos = null;
                _log($"[Combat] {REPOSITION_MAX_ATTEMPTS} reposition attempts without escaping the broken target — starting standard unstuck.");
                StartCombatUnstuck(currX, currY);
                return;
            }

            // ── Already at the waypoint ──
            // There is nothing to walk to: W is never pressed, the 2500 ms budget is never
            // spent and being nearby is never reported as a successful reposition. The attempt
            // stays a failure, so the escalation counter above and the existing failed-recovery
            // mechanism decide what happens to the still-unsuccessful attack.
            if (initialDistance <= reachThreshold)
            {
                _log($"[Combat] Reposition skipped — start=({currX:F1},{currY:F1}) is already inside the waypoint reach threshold " +
                     $"(target=({target.X:F1},{target.Y:F1}), initialDistance={initialDistance:F2}, reachThreshold={reachThreshold:F2}, " +
                     $"attempt={_repositionRetryCount}/{REPOSITION_MAX_ATTEMPTS}); no W, no retry TAB, combat baselines kept.");
                return;
            }

            int walkMs = BotConstants.Combat.CombatRepositionDurationMs;

            // ── Input authorization ──
            // Without a mandatory loot hold the reposition is unrestricted (the original
            // behaviour, unchanged for MoveAndAttack and for MoveAndAttackAndLoot without a
            // request). Only a hold that blocks input needs the explicit combat-recovery
            // authorization for the exact Deferred PostKill request.
            bool authorizedRecovery = false;
            if (_lootNavigationBarrierHoldsInput)
            {
                authorizedRecovery = TryBeginCombatRecoveryMovement("RepositionAndRetry");
                if (!authorizedRecovery)
                {
                    if (!_combatRecoveryMovementDeniedLogged)
                    {
                        _combatRecoveryMovementDeniedLogged = true;
                        _log("[CombatRecovery] Reposition denied — mandatory loot barrier holds input and combat recovery is not authorized " +
                             "(no selected mob / request no longer Deferred PostKill / loot owns input). Not waiting and not TABbing into the same target.");
                    }

                    // Bounded retry through the existing escalation policy (counted by the
                    // next invocations), never a long fake movement wait.
                    await Task.Delay(BotConstants.Combat.CombatRepositionPollMs, token);
                    return;
                }
            }

            _log($"[Combat] Attack not consuming mana — repositioning: start=({currX:F1},{currY:F1}) target=({target.X:F1},{target.Y:F1}) " +
                 $"initialDistance={initialDistance:F2} reachThreshold={reachThreshold:F2} maxBudget={walkMs}ms, then TAB + attack.");

            CombatRepositionObservation observation;
            try
            {
                // Force an immediate camera update to the waypoint direction (fresh segment
                // bypasses the camera filter) and walk forward until the waypoint threshold is
                // reached or the maximum movement budget expires.
                ResetBearingState();
                ApplyCameraBearing(GeometryUtils.GetBearingToTargetDeg(currX, currY, target.X, target.Y));

                // A performed key-down and a performed movement are tracked separately: only
                // the second one may ever count as a successful reposition.
                bool inputAccepted = TryStartMoving();
                observation = inputAccepted
                    ? await WaitForCombatRepositionAsync(
                        currX, currY, target.X, target.Y, reachThreshold, initialDistance, walkMs, authorizedRecovery, token)
                    : CombatRepositionObservation.InputDenied(currX, currY, initialDistance);
            }
            finally
            {
                StopMoving();
                EndCombatRecoveryMovement("reposition finished");
            }

            CombatRepositionResult result = observation.Result;
            float displacement = observation.Displacement;
            float finalDistance = observation.FinalDistance;
            float distanceImprovement = initialDistance - finalDistance;
            string finalPosition = observation.EndPositionRead
                ? $"({observation.EndX:F1},{observation.EndY:F1})"
                : "unreadable";
            string report =
                $"start=({currX:F1},{currY:F1}), target=({target.X:F1},{target.Y:F1}), " +
                $"initialDistance={initialDistance:F2}, reachThreshold={reachThreshold:F2}, " +
                $"final={finalPosition}, finalDistance={finalDistance:F2}, " +
                $"displacement={displacement:F2}, improvement={distanceImprovement:F2}, " +
                $"termination={observation.Termination}";

            // ── Completion ──
            // Only a confirmed physical reposition may reset the combat baselines and re-TAB.
            // Every other outcome returns here, so an aborted or failed attempt is never
            // reported as success and a repeated failure is never masked.
            switch (result)
            {
                case CombatRepositionResult.Moved:
                    _log($"[Combat] Reposition confirmed by {displacement:F2} units of real displacement — {report}.");
                    break;

                case CombatRepositionResult.InsufficientDisplacement:
                    _log($"[Combat] Reposition failed — moved only {displacement:F2} units (< {BotConstants.ReverseDiagonal.SuccessDisplacement:F2}); {report}.");
                    return;

                case CombatRepositionResult.NoWaypointProgress:
                    string direction = distanceImprovement < 0f
                        ? "moved away from the waypoint"
                        : "no meaningful progress toward the waypoint";
                    _log($"[Combat] Reposition failed — {direction} (improvement {distanceImprovement:F2} < {BotConstants.ReverseDiagonal.SuccessDistImprove:F2}); {report}.");
                    return;

                case CombatRepositionResult.InputDenied:
                    _log($"[Combat] Reposition failed — forward input was not accepted; not reselecting the same target. {report}.");
                    return;

                case CombatRepositionResult.AuthorizationLost:
                    _log($"[Combat] Reposition aborted — authorization lost mid-attempt; combat baselines kept, no retry TAB. {report}.");
                    return;

                case CombatRepositionResult.Cancelled:
                    _log($"[Combat] Reposition cancelled — input released, no retry TAB. {report}.");
                    token.ThrowIfCancellationRequested();
                    return;
            }

            // Fresh combat state for the retry: the next tick re-baselines the mana
            // stuck detection and presses 3 when a target is selected.
            _combatHandler.ResetState();

            // ── Central attack safety gate ──
            // The reposition walk can take seconds; the target may have become another player
            // in the meantime. Revalidate with fresh evidence BEFORE TABbing into the same
            // target so a rejected target cannot be reactivated by the retry path.
            if (!IsRetryTargetStillAttackable())
                return;

            _log("[Combat] Retrying attack — TAB, then attack if a target is selected.");
            if (!TryPressTargetCycleKey())
            {
                _combatHandler.SuspendForExternalMovement();
                return;
            }
            await Task.Delay(BotConstants.Delays.PreTabWaitMs, token);
        }

        /// <summary>
        /// Revalidates the selected target before the RepositionAndRetry retry TAB. A rejected
        /// target releases combat immediately and the retry TAB is NOT pressed.
        /// </summary>
        private bool IsRetryTargetStillAttackable()
        {
            CombatTargetValidation validation = _targetSafety.Validate(forcePlayerScan: true);
            if (validation.AttackAllowed)
                return true;

            HandleRejectedCombatTarget(validation);
            _log($"[CombatSafety] RepositionAndRetry cancelled — TargetId={validation.TargetId} " +
                 $"Reason={validation.Kind} Source={validation.Source}; no retry TAB.");
            return false;
        }

        /// <summary>
        /// One reposition attempt's observation: where the walk ended, how far it got and how
        /// the movement loop terminated. Only <see cref="CombatRepositionResult.Moved"/> may be
        /// treated as success by the caller.
        /// </summary>
        private readonly record struct CombatRepositionObservation(
            CombatRepositionResult Result,
            CombatRepositionTermination Termination,
            bool EndPositionRead,
            float EndX,
            float EndY,
            float FinalDistance,
            float Displacement)
        {
            public static CombatRepositionObservation InputDenied(float startX, float startY, float initialDistance)
                => new(
                    CombatRepositionResult.InputDenied,
                    CombatRepositionTermination.InputDenied,
                    EndPositionRead: false,
                    startX,
                    startY,
                    initialDistance,
                    Displacement: 0f);
        }

        /// <summary>
        /// Bounded, small-interval observation of one combat reposition. The walk ends as soon as
        /// the character is inside the waypoint's existing reach threshold, so the budget is a
        /// MAXIMUM and never an instruction to keep walking (holding W for the whole budget is
        /// what carried the character through and past the waypoint).
        ///
        /// The result distinguishes a performed movement from a performed key-down: success
        /// requires a readable final position that both moved more than coordinate noise AND
        /// ended closer to the waypoint. The largest historical reading is deliberately NOT used —
        /// a character that walked away and came back did not reposition. In an authorized
        /// recovery the attempt aborts as soon as the authorization is lost (target/focus/request
        /// change); unrestricted repositioning is observed up to the same maximum budget.
        /// </summary>
        private async Task<CombatRepositionObservation> WaitForCombatRepositionAsync(
            float startX,
            float startY,
            float targetX,
            float targetY,
            float reachThreshold,
            float initialDistance,
            int budgetMs,
            bool authorizedRecovery,
            CancellationToken token)
        {
            CombatRepositionObservation Observe(
                CombatRepositionResult result,
                CombatRepositionTermination termination,
                bool positionRead,
                float x,
                float y)
                => new(
                    result,
                    termination,
                    positionRead,
                    x,
                    y,
                    positionRead ? GeometryUtils.Distance(x, y, targetX, targetY) : initialDistance,
                    positionRead ? GeometryUtils.Distance(x, y, startX, startY) : 0f);

            DateTime deadline = DateTime.Now.AddMilliseconds(budgetMs);
            float lastX = startX;
            float lastY = startY;
            bool anyPositionRead = false;

            while (DateTime.Now < deadline)
            {
                // Cancellation is reported as an explicit outcome (the caller re-throws it after
                // releasing the input) so it can never be mistaken for a successful reposition.
                if (token.IsCancellationRequested)
                    return Observe(CombatRepositionResult.Cancelled, CombatRepositionTermination.Cancelled, anyPositionRead, lastX, lastY);

                await Task.Delay(BotConstants.Combat.CombatRepositionPollMs, CancellationToken.None);

                // A failed position read is not proof of motion: it leaves the last known
                // position untouched.
                var (sampleX, sampleY, sampleRead) = _memoryService.GetPlayerPosition();
                if (sampleRead)
                {
                    anyPositionRead = true;
                    lastX = sampleX;
                    lastY = sampleY;
                }

                if (authorizedRecovery && !IsCombatRecoveryMovementStillValid())
                {
                    _log("[CombatRecovery] Reposition aborted — recovery authorization lost (target/request/focus/stop changed).");
                    return Observe(CombatRepositionResult.AuthorizationLost, CombatRepositionTermination.AuthorizationLost, anyPositionRead, lastX, lastY);
                }

                // Waypoint-distance-aware termination: as soon as the character is inside the
                // waypoint's own reach threshold the walk is finished and W is released by the
                // caller's finally block.
                if (sampleRead && GeometryUtils.Distance(sampleX, sampleY, targetX, targetY) <= reachThreshold)
                {
                    CombatRepositionObservation reached = Observe(
                        CombatRepositionResult.Moved, CombatRepositionTermination.WaypointReached, true, sampleX, sampleY);

                    // Standing inside the threshold is not proof that the character walked:
                    // a real displacement is still required.
                    return reached.Displacement >= BotConstants.ReverseDiagonal.SuccessDisplacement
                        ? reached
                        : reached with { Result = CombatRepositionResult.InsufficientDisplacement };
                }
            }

            if (token.IsCancellationRequested)
                return Observe(CombatRepositionResult.Cancelled, CombatRepositionTermination.Cancelled, anyPositionRead, lastX, lastY);

            // Final position sample: the decision is made on where the character ended the walk.
            // A failed final read can never be reported as a successful reposition.
            var (endX, endY, endRead) = _memoryService.GetPlayerPosition();
            if (!endRead)
                return Observe(CombatRepositionResult.InsufficientDisplacement, CombatRepositionTermination.PositionUnreadable, anyPositionRead, lastX, lastY);

            CombatRepositionObservation final = Observe(
                CombatRepositionResult.Moved, CombatRepositionTermination.DurationLimit, true, endX, endY);

            if (final.Displacement < BotConstants.ReverseDiagonal.SuccessDisplacement)
                return final with { Result = CombatRepositionResult.InsufficientDisplacement };

            // Displacement alone is not progress: the walk must also have ended closer to the
            // waypoint than it started, otherwise walking away or sideways counted as success.
            if (initialDistance - final.FinalDistance < BotConstants.ReverseDiagonal.SuccessDistImprove)
                return final with { Result = CombatRepositionResult.NoWaypointProgress };

            return final;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint FindWindow(string? lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

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
        protected virtual void CaptureStuckScreenshot()
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

        /// <summary>
        /// Diagnostic screenshot for a failed/cancelled CombatGate waypoint return. Reuses the
        /// project's existing stuck-screenshot infrastructure (same folder, same file naming, same
        /// failure handling) instead of introducing a second screenshot framework; tests override
        /// this hook so no desktop capture is performed.
        /// </summary>
        protected virtual void CaptureCombatGateReturnFailureScreenshot()
            => CaptureStuckScreenshot();

        // ========================================================================
        //  INPUT — W key management
        // ========================================================================

        private void StartMoving()
        {
            TryStartMoving();
        }

        /// <summary>
        /// Presses W unless a navigation hold (or another owner) forbids it. Returns true only
        /// when forward input is actually accepted, so a caller can never mistake an elapsed
        /// timer for a performed movement. The mandatory loot hold keeps blocking ordinary
        /// navigation; it is bypassed only for an authorized combat-owned recovery of the
        /// exact Deferred post-kill obligation (see <see cref="TryBeginCombatRecoveryMovement"/>).
        /// </summary>
        private bool TryStartMoving()
        {
            lock (_inputLock)
            {
                if (Volatile.Read(ref _stopRequested) != 0)
                    return false;

                bool combatRecoveryBarrierAuthorization =
                    _lootNavigationBarrierHoldsInput && IsCombatRecoveryMovementStillValid();
                if (_lootNavigationBarrierHoldsInput && !combatRecoveryBarrierAuthorization)
                {
                    if (!_lootMoveSuppressedLogged)
                    {
                        _lootMoveSuppressedLogged = true;
                        _log("[LootBarrier] StartMoving suppressed — mandatory loot navigation barrier is active.");
                    }
                    return false;
                }

                if (_isMovingForward)
                {
                    return true;
                }

                if (_wKeyCameraController != null && _wKeyCameraOwner != null)
                {
                    if (!_wKeyCameraController.TryAcquireW(
                        _wKeyCameraOwner,
                        authorizedDuringLootBarrier: combatRecoveryBarrierAuthorization))
                    {
                        _log("[WCamera] StartMoving suppressed — movement vertical lock was not verified or the camera is owned by another operation.");
                        return false;
                    }
                }

                _log($"[Input] W down (StartMoving) — call #{_startMoveCount + 1}. Tick:{_tickCount}");

                if (_wKeyCameraController == null)
                {
                    try
                    {
                        // Legacy path: no shared camera controller means this movement
                        // system owns its own physical W transition.
                        SendSyntheticForwardKeyEvent(keyUp: false);
                    }
                    catch (Exception ex)
                    {
                        try { SendSyntheticForwardKeyEvent(keyUp: true); }
                        catch (Exception cleanupEx)
                        {
                            _log($"[Input] W-up cleanup after failed StartMoving also failed: {cleanupEx.Message}");
                        }
                        _log($"[Input] W down (StartMoving) failed: {ex.Message}");
                        return false;
                    }
                }
                // With a shared controller, the physical W-down was performed only
                // after the movement vertical lock was applied and verified.

                _isMovingForward = true;
                _startMoveCount++;
                _startCameraAngleAwaitingFirstW = false;
                _lootMoveSuppressedLogged = false;
                _ignoreStuckUntil = DateTime.Now.AddSeconds(STUCK_GRACE_AFTER_START_SECONDS);
                return true;
            }
        }

        private void ForceStartMoving()
        {
            lock (_inputLock)
            {
                if (Volatile.Read(ref _stopRequested) != 0)
                    return;

                if (_lootNavigationBarrierHoldsInput)
                {
                    if (!_lootMoveSuppressedLogged)
                    {
                        _lootMoveSuppressedLogged = true;
                        _log("[LootBarrier] ForceStartMoving suppressed — mandatory loot navigation barrier is active.");
                    }
                    return;
                }

                // Rate-limit: prevent spam ForceStartMoving attempts (only allowed every FORCE_START_MIN_INTERVAL_MS).
                // Failed attempts also update the attempt timestamp, so a failed restart cannot
                // immediately flood W-down attempts; suppressed attempts deliberately do not.
                double msSinceLast = (DateTime.Now - _lastForceStartMovingAt).TotalMilliseconds;
                if (msSinceLast < FORCE_START_MIN_INTERVAL_MS)
                {
                    _log($"[Input] ForceStartMoving suppressed — rate-limit {msSinceLast:F0}ms/{FORCE_START_MIN_INTERVAL_MS:F0}ms");
                    return;
                }

                if (_wKeyCameraController != null && _wKeyCameraOwner != null)
                {
                    if (!_wKeyCameraController.TryAcquireW(_wKeyCameraOwner))
                    {
                        _log("[WCamera] ForceStartMoving suppressed — movement vertical lock was not verified or the camera is owned by another operation.");
                        return;
                    }

                    // The controller keeps the movement camera across the internal
                    // up/down gap and reports whether the restart actually happened.
                    _log($"[Input] ForceStartMoving — restarting W (call #{_startMoveCount + 1}). Tick:{_tickCount}");
                    WKeyRestartResult restartResult = _wKeyCameraController.TryRestartW(
                        _wKeyCameraOwner, BotConstants.Delays.ForceStartReleaseGapMs);
                    switch (restartResult)
                    {
                        case WKeyRestartResult.Restarted:
                            _lastForceStartMovingAt = DateTime.Now;
                            _isMovingForward = true;
                            _startMoveCount++;
                            _lootMoveSuppressedLogged = false;
                            _ignoreStuckUntil = DateTime.Now.AddSeconds(STUCK_GRACE_AFTER_START_SECONDS);
                            return;

                        case WKeyRestartResult.Suppressed:
                            // Do not update the successful-restart timestamp, counters or
                            // stuck grace: no corrective W transition was performed.
                            _log("[Input] ForceStartMoving suppressed — another W owner is active; stuck grace was not reset.");
                            return;

                        default:
                            // The controller records any unresolved physical W release for a
                            // later Stop/cleanup retry, but this attempt did not restore motion.
                            _lastForceStartMovingAt = DateTime.Now;
                            _isMovingForward = false;
                            _log("[Input] ForceStartMoving failed — W restart did not complete; restart counters and stuck grace were not updated.");
                            return;
                    }
                }

                _lastForceStartMovingAt = DateTime.Now;

                _log($"[Input] ForceStartMoving — releasing W key (call #{_startMoveCount + 1}). Tick:{_tickCount}");
                try
                {
                    // Keep this one logical W ownership across the physical up/down gap.
                    // Releasing the camera owner here would flash stationary view.
                    SendSyntheticForwardKeyEvent(keyUp: true);
                    Thread.Sleep(BotConstants.Delays.ForceStartReleaseGapMs);

                    _log($"[Input] W down (ForceStartMoving) — call #{_startMoveCount + 1}. Tick:{_tickCount}");
                    SendSyntheticForwardKeyEvent(keyUp: false);

                    _isMovingForward = true;
                    _startMoveCount++;
                    _lootMoveSuppressedLogged = false;
                    _ignoreStuckUntil = DateTime.Now.AddSeconds(STUCK_GRACE_AFTER_START_SECONDS);
                }
                catch (Exception ex)
                {
                    try { SendSyntheticForwardKeyEvent(keyUp: true); }
                    catch (Exception cleanupEx)
                    {
                        _log($"[Input] W-up cleanup after failed ForceStartMoving also failed: {cleanupEx.Message}");
                    }
                    _isMovingForward = false;
                    _log($"[Input] ForceStartMoving failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Permanently stops this route instance. The latch is set before recovery
        /// cancellation and key-up cleanup, so callbacks from an in-flight recovery can
        /// never start W/A/D after Stop begins.
        /// </summary>
        public void Stop()
        {
            Interlocked.Exchange(ref _stopRequested, 1);
            ResetPostKillRetargetState();
            ClearCombatGateTargetSuppression();
            ReleaseStoppedInputs();
            void TryCleanup(Action cleanup, string action)
            {
                try { cleanup(); }
                catch (Exception ex) { _log($"[MovementStop] {action} failed: {ex.Message}"); }
            }

            try
            {
                TryCleanup(() => LootSystemRef?.SuspendForRecovery("movement system stopped"), "loot suspension");
                TryCleanup(() => CancelMobGrouping("movement system stopped"), "mob grouping cancellation");
                TryCleanup(() => CancelCombatGateWaypointReturn("movement system stopped"), "CombatGate return cancellation");
                TryCleanup(() => EndCombatRecoveryMovement("movement system stopped"), "combat recovery cancellation");
                TryCleanup(() =>
                {
                    lock (_inputLock)
                    {
                        _reverseDiagonalRecovery.Stop();
                        _waypointMobRecovery?.Stop();
                    }
                    _waypointMobRecovery = null;
                }, "ReverseDiagonal cancellation");
                TryCleanup(CancelWaypointSpecialRecovery, "waypoint recovery cancellation");
            }
            finally
            {
                _isUnstuckRoutineActive = false;
                try { ReleaseRecoveryOwnership("movement system stopped"); }
                catch (Exception ex) { _log($"[MovementStop] Recovery ownership release failed: {ex.Message}"); }
                finally { ReleaseStoppedInputs(); }
            }
        }

        private void ReleaseStoppedInputs()
        {
            try { StopMoving(); }
            catch (Exception ex) { _log($"[Input] Movement key release after stop failed: {ex.Message}"); }
            try { SendSyntheticBackwardKeyEvent(keyUp: true); }
            catch (Exception ex) { _log($"[Input] S-up cleanup after movement stop failed: {ex.Message}"); }
            try { ReleaseCombatKeys(); }
            catch (Exception ex) { _log($"[Input] Combat key release after movement stop failed: {ex.Message}"); }
        }

        public void StopMoving()
        {
            lock (_inputLock)
            {
                // Log only on a real transition — repeated StopMoving calls (multiple
                // callers per tick) used to flood the log with identical W-up lines.
                bool wasMovingForward = _isMovingForward;
                if (wasMovingForward)
                {
                    _isMovingForward = false;
                    _stopMoveCount++;
                }

                // Physical cleanup is unconditional and logical turn ownership is reset
                // even if one of the native KEYUP calls fails. With a shared camera
                // controller only the final logical W owner sends the physical W-up;
                // stopping one component can never release another component's hold.
                try
                {
                    try
                    {
                        if (_wKeyCameraController != null && _wKeyCameraOwner != null)
                            _wKeyCameraController.ReleaseW(_wKeyCameraOwner);
                        else
                            SendSyntheticForwardKeyEvent(keyUp: true);
                    }
                    finally
                    {
                        // W is physically released before the last owner restores the
                        // stationary camera view. Repeated StopMoving remains idempotent.
                    }
                }
                finally
                {
                    try
                    {
                        SendTurnKeyEvent(leftA: true, keyUp: true);
                    }
                    finally
                    {
                        try
                        {
                            SendTurnKeyEvent(leftA: false, keyUp: true);
                        }
                        finally
                        {
                            _heldTurnKey = TurnKeyState.None;
                            _keyboardFrozenBearingDeg = null;
                            ReleaseKeyboardFastTurnMouse("StopMoving");
                            // An external stop (combat/loot/pause) interrupts the in-place
                            // first-step turn; restart its safety timeout when movement
                            // resumes instead of counting the paused time.
                            if (_initialHeadingAlignmentPending)
                                _initialHeadingAlignmentInProgress = false;
                        }
                    }
                }

                if (wasMovingForward)
                    _log($"[Input] W up (StopMoving) — call #{_stopMoveCount}. Tick:{_tickCount}");
            }
        }

        /// <summary>
        /// Terminates this movement instance's camera-controller participation.
        /// It releases only its own W owner and its own mandatory-loot navigation
        /// barrier registration; the authoritative LootSystem obligation is untouched.
        /// Safe to call repeatedly and from every route/workflow termination path.
        /// </summary>
        public void ReleaseCameraOwnership()
        {
            try
            {
                SetLootNavigationBarrierHoldsInput(false);
            }
            finally
            {
                StopMoving();
            }
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
            lock (_inputLock)
            {
                if (Volatile.Read(ref _stopRequested) != 0 || _isSkillThreeHeld)
                    return;

                // ── Central attack safety gate ──
                // Every code path that presses 3 funnels through here. Only a CONFIRMED
                // monster may be attacked; a player/NPC/invalid/unknown target never is.
                // A new target always triggers a fresh other-player scan first.
                CombatTargetValidation validation = _targetSafety.Validate();
                if (!validation.AttackAllowed)
                {
                    CancelAttackKeyRetry($"target not attackable ({validation.Kind})");
                    return;
                }

                _attackRetryTargetId = _memoryService.GetSelectedTargetId();
                _attackRetryLastMana = _memoryService.TryGetMana(out short mana) ? mana : int.MinValue;
                _log("[Key] 3 hold (attack skill)");
                SendSkillThreeKeyEvent(keyUp: false);
                _isSkillThreeHeld = true;
                _attackRetryStartedAt = DateTime.UtcNow;
                _log("[Combat] Attack key 3 activated; retry check in 1000ms.");
            }
        }

        private void CheckAttackKeyRetry(CancellationToken token)
        {
            lock (_inputLock)
            {
                if (_attackRetryStartedAt == DateTime.MinValue)
                    return;

                int targetId = _memoryService.GetSelectedTargetId();

                // The one-second retry may never restart an attack on a target that is not a
                // confirmed monster (e.g. the player that TAB selected next). The current target
                // is validated even when it changed since the key was pressed.
                CombatTargetValidation validation = targetId > 0 ? _targetSafety.Validate() : default;
                bool targetRejected = targetId > 0 && !validation.AttackAllowed;

                string? cancelReason = null;
                if (token.IsCancellationRequested || Volatile.Read(ref _stopRequested) != 0)
                    cancelReason = "stop or cancellation";
                else if (!_isSkillThreeHeld)
                    cancelReason = "attack input released";
                else if (_isUnstuckRoutineActive)
                    cancelReason = "unstuck active";
                else if (IsMobGroupingSequenceActive)
                    cancelReason = "grouping owns movement";
                else if (!IsAutomaticLootDisabled &&
                         (LootSystemRef?.IsCollecting == true || LootSystemRef?.IsPinkScanPendingOrActive == true))
                    cancelReason = "loot owns input";
                else if (targetRejected)
                    cancelReason = $"target not attackable ({validation.Kind}: {validation.Source})";
                else if (!GameMemoryService.IsMobTargetId(targetId) || targetId != _attackRetryTargetId)
                    cancelReason = "target lost or changed";

                if (cancelReason != null)
                {
                    CancelAttackKeyRetry(cancelReason);
                    if (targetRejected)
                        ReleaseSkillThree();
                    return;
                }

                if (!_memoryService.TryGetMana(out short mana))
                    return;
                if (_attackRetryLastMana == int.MinValue)
                {
                    // A failed initial read cannot prove that mana was not consumed.
                    _attackRetryLastMana = mana;
                    _attackRetryStartedAt = DateTime.UtcNow;
                    return;
                }
                if (mana < _attackRetryLastMana)
                {
                    _attackRetryStartedAt = DateTime.MinValue;
                    _log("[Combat] Mana consumption detected — attack retry not needed.");
                    return;
                }
                _attackRetryLastMana = mana;
                if ((DateTime.UtcNow - _attackRetryStartedAt).TotalMilliseconds < 1000)
                    return;

                _attackRetryStartedAt = DateTime.MinValue;
                _log("[Combat] No mana consumption after 1000ms — releasing and pressing key 3 again (retry 1/1).");
                SendSkillThreeKeyEvent(keyUp: true);
                _isSkillThreeHeld = false;
                SendSkillThreeKeyEvent(keyUp: false);
                _isSkillThreeHeld = true;
            }
        }

        private void CancelAttackKeyRetry(string reason)
        {
            if (_attackRetryStartedAt == DateTime.MinValue)
                return;
            _attackRetryStartedAt = DateTime.MinValue;
            _log($"[Combat] Attack retry cancelled — reason={reason}.");
        }

        private void ReleaseSkillThree()
        {
            lock (_inputLock)
            {
                CancelAttackKeyRetry("attack input released");
                if (!_isSkillThreeHeld)
                    return;

                _log("[Key] 3 up");
                SendSkillThreeKeyEvent(keyUp: true);
                _isSkillThreeHeld = false;
            }
        }

        /// <summary>
        /// Central reaction to a target that is not a confirmed monster (player, NPC,
        /// invalid or unknown/unreadable). Releases the attack key immediately, cancels the
        /// pending one-second retry, invalidates the combat context and - for a confirmed
        /// other player - runs the project's existing other-player response. Idempotent.
        /// </summary>
        private void HandleRejectedCombatTarget(CombatTargetValidation validation)
        {
            bool attackWasHeld = _isSkillThreeHeld;

            ReleaseSkillThree();
            CancelAttackKeyRetry($"target rejected ({validation.Kind})");
            _combatHandler.ResetState();
            CancelMobGrouping("combat target rejected");
            _resumeCombatSkillAfterGrouping = false;
            ClearCombatRetargetSearch();

            if (attackWasHeld)
            {
                _log($"[CombatSafety] AttackInterrupted TargetId={validation.TargetId} " +
                     $"Reason={validation.Kind} Source={validation.Source}.");
            }

            if (validation.Kind == TargetEntityKind.OtherPlayer &&
                validation.PlayerMarker is { } candidate &&
                _reportedPlayerTargetId != validation.TargetId)
            {
                // Existing project response to a confirmed other player on screen: stop the
                // EXP loop and return to town (LootSystem owns the stop-on-player policy).
                // Reported once per rejected player target — this method runs on every combat
                // tick while the same target stays selected.
                _reportedPlayerTargetId = validation.TargetId;
                try
                {
                    LootSystemRef?.ReportImmediateOtherPlayerMarker(candidate, "Combat target safety");
                }
                catch (Exception ex)
                {
                    _log($"[CombatSafety] Other-player response failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Bounded target-cycle policy for a rejected target. TAB is never pressed endlessly
        /// on the same forbidden target: after
        /// <see cref="BotConstants.CombatSafety.MaxTargetCycleAttemptsForRejectedTarget"/> the
        /// bot waits for the selection to change, and only restores the budget after
        /// <see cref="BotConstants.CombatSafety.RejectedTargetTabCooldownMs"/>.
        /// </summary>
        private bool ShouldCycleTargetAwayFromRejectedTarget(int targetId)
        {
            DateTime now = DateTime.UtcNow;
            if (_rejectedTargetId != targetId ||
                _rejectedTargetFirstSeenAt == DateTime.MinValue ||
                (now - _rejectedTargetFirstSeenAt).TotalMilliseconds >
                    BotConstants.CombatSafety.RejectedTargetTabCooldownMs)
            {
                _rejectedTargetId = targetId;
                _rejectedTargetFirstSeenAt = now;
                _rejectedTargetTabCount = 0;
                _rejectedTargetTabLimitLogged = false;
            }

            if (_rejectedTargetTabCount < BotConstants.CombatSafety.MaxTargetCycleAttemptsForRejectedTarget)
            {
                _rejectedTargetTabCount++;
                return true;
            }

            if (!_rejectedTargetTabLimitLogged)
            {
                _rejectedTargetTabLimitLogged = true;
                _log($"[CombatSafety] Target-cycle budget used for rejected TargetId={targetId} — " +
                     "no further TAB until the selection changes (avoids an endless TAB/unknown loop).");
            }
            return false;
        }

        protected virtual void SendSkillThreeKeyEvent(bool keyUp)
            => GameInput.keybd_event(GameInput.VK_3, GameInput.SCAN_3,
                keyUp ? (uint)GameInput.KEYEVENTF_KEYUP : 0u, 0);

        private void StartCombatRetargetSearch()
        {
            _combatRetargetCameraStage = CombatRetargetCameraStage.VeryLowSearch;
            _combatRetargetAwaitingSelection = false;
            _combatRetargetCameraFailureAttempts = 0;
            _combatRetargetFailed = false;
        }

        private async Task<bool> HandlePostKillRetargetRequestAsync(CancellationToken token)
        {
            long requestId = _postKillRetargetRequestId;
            if (requestId <= 0)
                return false;

            if (_combatGateReturnActive)
                return false;

            if (CurrentMode != BotMode.MoveAndAttackAndLoot)
            {
                ResetPostKillRetargetState();
                return false;
            }

            if (IsAutomaticLootDisabled)
            {
                _postKillRetargetRequestId = 0;
                _postKillTargetReleaseId = 0;
                _postKillTargetReleaseAttempts = 0;
                _postKillTargetReleaseRetryAtUtc = DateTime.MinValue;
                StartCombatRetargetSearch();
                _log("[CombatRetarget] Automatic loot disabled while waiting for PostKill — starting close-first search.");
                return true;
            }

            LootSystem? loot = LootSystemRef;
            LootRequestStatus status = default;
            bool hasStatus = loot != null && loot.TryGetMandatoryLootRequestStatus(requestId, out status);
            bool requestCompleted = hasStatus &&
                status.RequestId == requestId &&
                status.Reason == LootBarrierReason.PostKill &&
                status.State == LootRequestState.Completed &&
                status.CameraVerified &&
                _lootBarrier.RequestId == requestId &&
                _lootBarrier.Reason == LootBarrierReason.PostKill &&
                _lootBarrier.Phase == LootBarrierPhase.Completed &&
                _lootBarrier.CameraVerified;

            if (!requestCompleted)
            {
                int currentTargetId = _memoryService.GetSelectedTargetId();
                CombatTargetValidation currentTarget = currentTargetId > 0
                    ? _targetSafety.Validate()
                    : default;
                if (currentTargetId > 0 && !currentTarget.IsConfirmedMonster)
                    return false;

                bool liveMonsterSelected = currentTarget.IsConfirmedMonster;
                if (!liveMonsterSelected && _lootBarrier.IsBlockingNavigation && !_combatGateReturnActive)
                    return await HoldNavigationForMandatoryLootAsync(token);

                // Pending/Running/Deferred and every terminal non-success state are not
                // proof of loot completion. Keep the post-kill request armed and prevent
                // its ordinary fallback TAB from using the loot/previous camera.
                if (!liveMonsterSelected)
                {
                    await Task.Delay(BotConstants.Delays.CombatAttackWaitMs, token);
                    return true;
                }
                return false;
            }

            int selectedTargetId = _memoryService.GetSelectedTargetId();
            CombatTargetValidation validation = selectedTargetId > 0
                ? _targetSafety.Validate()
                : default;
            if (validation.IsConfirmedMonster)
            {
                _log($"[CombatRetarget] PostKill request={requestId} already has monster targetId={selectedTargetId}; resuming combat.");
                _postKillRetargetRequestId = 0;
                _postKillCloseTargetCheckPending = false;
                _postKillTargetIdBeforeTab = 0;
                return false;
            }

            _postKillRetargetRequestId = 0;
            StartCombatRetargetSearch();
            _log($"[CombatRetarget] PostKill request={requestId} — starting close-first search");
            return true;
        }

        private async Task<bool> HandleCombatRetargetCameraStageAsync(
            BotMode currentMode,
            short restoreCameraDistance,
            CancellationToken token)
        {
            CombatRetargetCameraStage stage = _combatRetargetCameraStage;
            if (stage == CombatRetargetCameraStage.None)
                return false;

            if ((currentMode != BotMode.MoveAndAttack && currentMode != BotMode.MoveAndAttackAndLoot) ||
                !MobGroupingController.IsSupportedCombatMode(CurrentMode))
            {
                ClearCombatRetargetSearch();
                return false;
            }

            if (!IsAutomaticLootDisabled && LootSystemRef?.IsPinkRescanActive == true)
                return true;

            int selectedTargetId = _memoryService.GetSelectedTargetId();
            CombatTargetValidation validation = selectedTargetId > 0
                ? _targetSafety.Validate()
                : default;
            bool sameKilledTarget = _postKillCloseTargetCheckPending &&
                                    selectedTargetId == _postKillTargetIdBeforeTab;
            if (validation.IsConfirmedMonster && !sameKilledTarget)
            {
                _log($"[CombatRetarget] Monster selected targetId={selectedTargetId} stage={GetCombatRetargetStageName(stage)}");
                ClearCombatRetargetSearch();
                return false;
            }

            // A player/NPC/unknown target remains under the central CombatHandler safety
            // path. Retargeting never treats its presence as proof of a nearby monster.
            if (selectedTargetId > 0 && !validation.IsConfirmedMonster)
                return false;

            if (_combatRetargetAwaitingSelection)
            {
                bool shouldRunPinkCheck = _postKillCloseTargetCheckPending &&
                    stage == CombatRetargetCameraStage.VeryLowSearch &&
                    CurrentMode == BotMode.MoveAndAttack &&
                    currentMode == BotMode.MoveAndAttack &&
                    !IsAutomaticLootDisabled &&
                    LootSystemRef?.PinkLootOnlyMode == true;
                if (shouldRunPinkCheck)
                {
                    _log($"[CombatRetarget] No close mob selected at {CombatRetargetVeryLowCameraDistance} — starting SOD/SOP loot scan.");
                    ReleaseSkillThree();
                    ClearCombatRetargetSearch();
                    LootSystemRef?.RequestPinkScan();
                    await Task.Delay(BotConstants.Delays.LootUpdateMs, token);
                    return true;
                }

                if (stage == CombatRetargetCameraStage.VeryLowSearch)
                {
                    _combatRetargetCameraStage = CombatRetargetCameraStage.LowSearch;
                    _combatRetargetCameraFailureAttempts = 0;
                }
                else if (stage == CombatRetargetCameraStage.LowSearch)
                {
                    _combatRetargetCameraStage = CombatRetargetCameraStage.MidSearch;
                    _combatRetargetCameraFailureAttempts = 0;
                }
                else
                {
                    _log($"[CombatRetarget] No mob selected at {CombatRetargetMidCameraDistance}. Restoring camera to {restoreCameraDistance}.");
                    if (!TryApplyMovementCameraDistance(restoreCameraDistance))
                        return true;
                    ClearCombatRetargetSearch();
                    await Task.Delay(BotConstants.Delays.CombatAttackWaitMs, token);
                    return true;
                }

                _combatRetargetAwaitingSelection = false;
                stage = _combatRetargetCameraStage;
            }

            short expectedDistance = GetCombatRetargetCameraDistance();
            if (!TryApplyVerifiedMovementCameraDistance(expectedDistance, out bool writeFailed))
            {
                if (writeFailed)
                    RecordCombatRetargetCameraFailure(stage, expectedDistance, null, restoreCameraDistance);
                return true;
            }

            ReleaseSkillThree();
            await Task.Delay(BotConstants.Delays.CombatRetargetTabMs, token);

            bool cameraCheckAttempted;
            bool cameraReadSucceeded;
            short actualDistance;
            if (!TryPressTargetCycleKey(
                    expectedDistance, out cameraCheckAttempted, out cameraReadSucceeded, out actualDistance))
            {
                if (cameraCheckAttempted)
                {
                    RecordCombatRetargetCameraFailure(
                        stage,
                        expectedDistance,
                        cameraReadSucceeded ? actualDistance : null,
                        restoreCameraDistance);
                }
                else
                {
                    // Loot acquired the target-action gate after the camera write. Keep
                    // this exact stage and retry once ownership has been returned.
                    _combatHandler.SuspendForExternalMovement();
                }
                return true;
            }

            _combatRetargetCameraFailureAttempts = 0;
            string stageName = GetCombatRetargetStageName(stage);
            _log($"[CombatRetarget] Stage={stageName} cameraRequested={expectedDistance} cameraActual={actualDistance}");
            _log($"[CombatRetarget] TAB sent stage={stageName}");
            _combatRetargetAwaitingSelection = true;
            await Task.Delay(BotConstants.Delays.PreTabWaitMs, token);
            return true;
        }

        private void RecordCombatRetargetCameraFailure(
            CombatRetargetCameraStage stage,
            short requestedDistance,
            short? actualDistance,
            short restoreCameraDistance)
        {
            int attempt = ++_combatRetargetCameraFailureAttempts;
            string actual = actualDistance.HasValue ? actualDistance.Value.ToString() : "unavailable";
            string stageName = GetCombatRetargetStageName(stage);
            _log($"[CombatRetarget] Stage={stageName} cameraRequested={requestedDistance} cameraActual={actual} verification failed ({attempt}/{CombatRetargetCameraMaximumAttempts}).");
            if (attempt < CombatRetargetCameraMaximumAttempts)
                return;

            _log($"[CombatRetarget] Retarget failed at stage={stageName} after {CombatRetargetCameraMaximumAttempts} camera attempts; no blind TAB sent.");
            TryApplyMovementCameraDistance(restoreCameraDistance);
            ClearCombatRetargetSearch();
            _combatRetargetFailed = true;
        }

        private bool IsCurrentCombatRetargetMonsterSelected()
        {
            int targetId = _memoryService.GetSelectedTargetId();
            if (targetId <= 0)
                return false;
            CombatTargetValidation validation = _targetSafety.Validate();
            return validation.IsConfirmedMonster &&
                   (!_postKillCloseTargetCheckPending || targetId != _postKillTargetIdBeforeTab);
        }

        private short GetRetargetRestoreCameraDistance()
            => _finalStandbyActive
                ? _finalStandbyWaypoint.CameraDistanceLock
                : _waypoints.Count > 0
                    ? _waypoints.Peek().CameraDistanceLock
                    : Waypoint.DefaultCameraDistanceLock;

        private bool TryPressTargetCycleKey()
        {
            bool pressed = TryPressTargetCycleKey(
                expectedCameraDistance: null,
                out _, out _, out _);
            if (pressed)
                _log("[Key] TAB (target cycle)");
            return pressed;
        }

        private bool TryPressTargetCycleKey(
            short? expectedCameraDistance,
            out bool cameraCheckAttempted,
            out bool cameraReadSucceeded,
            out short actualCameraDistance)
        {
            bool pressed = false;
            bool localCameraCheckAttempted = false;
            bool localCameraReadSucceeded = false;
            short localActualCameraDistance = 0;

            void Press()
            {
                if (expectedCameraDistance.HasValue)
                {
                    localCameraCheckAttempted = true;
                    localCameraReadSucceeded = _memoryService.TryGetCameraDistance(out localActualCameraDistance);
                    if (!localCameraReadSucceeded || localActualCameraDistance != expectedCameraDistance.Value)
                        return;
                }

                SendTargetCycleKey();
                pressed = true;
                _combatHandler.NotifyTargetCyclePressed();
            }

            LootSystem? loot = LootSystemRef;
            if (loot == null)
                Press();
            else
                loot.TryRunMovementTargetAction(Press);

            cameraCheckAttempted = localCameraCheckAttempted;
            cameraReadSucceeded = localCameraReadSucceeded;
            actualCameraDistance = localActualCameraDistance;
            return pressed;
        }

        private void ClearCombatRetargetSearch()
        {
            _combatRetargetCameraStage = CombatRetargetCameraStage.None;
            _combatRetargetAwaitingSelection = false;
            _combatRetargetCameraFailureAttempts = 0;
            _combatRetargetFailed = false;
            _postKillCloseTargetCheckPending = false;
            _postKillTargetIdBeforeTab = 0;
        }

        private void ResetPostKillRetargetState()
        {
            _postKillRetargetRequestId = 0;
            _postKillTargetReleaseId = 0;
            _postKillTargetReleaseAttempts = 0;
            _postKillTargetReleaseRefusalLogged = false;
            _postKillTargetReleaseRetryAtUtc = DateTime.MinValue;
            _postKillCloseTargetCheckPending = false;
            _postKillTargetIdBeforeTab = 0;
            ClearCombatRetargetSearch();
        }

        private static string GetCombatRetargetStageName(CombatRetargetCameraStage stage)
            => stage switch
            {
                CombatRetargetCameraStage.VeryLowSearch => "VeryLow",
                CombatRetargetCameraStage.LowSearch => "Low",
                CombatRetargetCameraStage.MidSearch => "Mid",
                _ => "None"
            };

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
            // Route resync starts a new leg selection. A later return to a previously
            // used waypoint must be allowed to apply its start camera again.
            ClearStartCameraAngleApplication();

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
            // A genuinely different target is a new navigation context. Same-target
            // resynchronization returned SameTarget above and keeps the failure history.
            if (_consecutiveStuckAttempts > 0)
            {
                var newTarget = _waypoints.Count > 0 ? _waypoints.Peek() : oldTarget;
                _log($"[Unstuck] Navigation failure count reset — new route target ({newTarget.X:F1},{newTarget.Y:F1}).");
            }
            _consecutiveStuckAttempts = 0;
            _lastStuckAttemptPos = null;
            ResetNavigationProgressTracking();

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
            // The initial resync is the point where the route decides its first target, so
            // the coordinates it uses must be logged explicitly: the coordinator only
            // starts a route after PositionSync validated the position (after the mandatory
            // post-town-teleport W refresh when one was required).
            if (isInitialResync)
                _log($"[RouteResync] Starting from validated position: ({currX:F1},{currY:F1})");

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

            // ── Initial-resync start-camera guard ──
            // The nearest-segment rule targets the END of the segment it starts on, which
            // drops the point the player is standing on (e.g. the first path point right
            // after a town teleport). When a dropped point carries an explicit start
            // camera angle, its leg never runs, so the configured yaw is never written:
            // the bot presses W with the old camera (or turns with A/D) instead of setting
            // the angle first. Keep such a point as the target, but only for a route that
            // starts where it was recorded to start (segment 0, t≈0) and only while the
            // point is truly closer than the resync's own choice, so a route resumed
            // further along never walks backwards.
            bool atRouteStart = bestSegmentStart == 0 &&
                                bestT <= 1f - BotConstants.Movement.RouteResyncVeryCloseToSegmentEndT;
            if (isInitialResync && atRouteStart)
            {
                int startCameraIndex = FindSkippedStartCameraWaypointIndex(currX, currY, nextWpIndex);
                if (startCameraIndex >= 0)
                {
                    var kept = _initialPath[startCameraIndex];
                    _log($"[RouteResync] start-camera guard: waypoint {startCameraIndex + 1} ({kept.X:F1},{kept.Y:F1}) has StartCam:{CameraAngleValue.Describe(kept.StartCameraAngleBits)} and would be skipped — targeting it first so the camera is written before W.");
                    nextWpIndex = startCameraIndex;
                    reason += "+startcam-guard";
                }
            }

            // ── Log and apply ──
            _log($"[RouteResync] bestSegment={bestSegmentStart}->{bestSegmentEnd} t={bestT:F2} dist={bestDist:F2} nextIndex={nextWpIndex} reason={reason}");
            _log($"[RouteResync] oldTarget=({oldTarget.X:F1},{oldTarget.Y:F1}) newTarget=({_initialPath[nextWpIndex].X:F1},{_initialPath[nextWpIndex].Y:F1})");

            return RebuildWaypointQueueFromIndex(nextWpIndex, oldTarget, preserveLeadingMethodSteps: isInitialResync);
        }

        /// <summary>
        /// Returns the index of the nearest waypoint the resync would skip although it
        /// carries a start camera angle, or -1 when nothing has to be kept. Only points
        /// before <paramref name="nextWpIndex"/> are considered, and only when they are
        /// closer to the player than the resync's own target; points already inside their
        /// reach threshold are excluded because they would be consumed in the same tick
        /// without ever writing their start camera.
        /// </summary>
        private int FindSkippedStartCameraWaypointIndex(float currX, float currY, int nextWpIndex)
        {
            if (nextWpIndex <= 0 || nextWpIndex >= _initialPath.Count)
                return -1;

            var target = _initialPath[nextWpIndex];
            float bestDistance = GeometryUtils.Distance(currX, currY, target.X, target.Y);
            int bestIndex = -1;

            for (int i = 0; i < nextWpIndex; i++)
            {
                var candidate = _initialPath[i];
                if (candidate.IsOperationStep || !candidate.StartCameraAngleBits.HasValue)
                    continue;

                float distance = GeometryUtils.Distance(currX, currY, candidate.X, candidate.Y);
                if (distance <= GetEffectiveWaypointReachThreshold(candidate))
                    continue; // Already reached — there is no leg left to configure.
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                bestIndex = i;
            }

            return bestIndex;
        }

        // ========================================================================
        //  SOFT SKIP
        // ========================================================================

    }
}
