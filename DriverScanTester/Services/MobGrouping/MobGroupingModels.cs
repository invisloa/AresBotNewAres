using System;
using System.Threading;

namespace DriverScanTester.Services
{
    /// <summary>Client-relative screen point.</summary>
    public readonly record struct MobPoint(float X, float Y);

    public enum MobMarkerSource
    {
        CyanCenter,
        MagentaRing
    }

    /// <summary>A detected marker center in game-client coordinates.</summary>
    public readonly record struct MobMarker(
        MobPoint Center,
        float Radius,
        float Confidence,
        MobMarkerSource Source);

    public enum MobGroupingDirection
    {
        North,
        NorthEast,
        East,
        SouthEast,
        South,
        SouthWest,
        West,
        NorthWest
    }

    public enum MobGroupingState
    {
        Idle,
        Observing,
        GroupingMove,
        Settling,
        Verify,
        ResumeCombat,
        Cooldown
    }

    public enum MobGroupingDirective
    {
        None,
        StartMove,
        StopMovement,
        VerifyFormation,
        ResumeCombat,
        Cancelled
    }

    /// <summary>
    /// Where a gather walk takes its steering target from. Real fights showed that the bot is
    /// almost always STANDING while it fights, so a route-only target made the whole feature
    /// dead (NoMovementProgress in every evaluation). A standstill gather therefore walks
    /// toward the detected mob cluster instead of the route waypoint.
    /// </summary>
    public enum MobGroupingGatherTarget
    {
        /// <summary>Continue the live route waypoint (the bot is already walking the route).</summary>
        Route,
        /// <summary>Walk toward the detected mob cluster (the bot is standing in combat).</summary>
        ApproachMobs
    }

    public readonly record struct MobGroupingDecision(
        MobGroupingDirective Directive,
        /// <summary>
        /// Mob-direction analysis carried alongside the decision for diagnostics only.
        /// It never drives gather movement: MovementSystem resolves gather steering from
        /// the live route waypoint through MoveTowards (or from the mob approach vector when
        /// <see cref="GatherTarget"/> is <see cref="MobGroupingGatherTarget.ApproachMobs"/>).
        /// </summary>
        MobGroupingDirection? Direction = null,
        int MoveNumber = 0,
        string Reason = "",
        /// <summary>Stable machine-readable decision code (see <see cref="MobGroupingReason"/>).</summary>
        string Code = "",
        /// <summary>Steering target the host must use for this gather walk.</summary>
        MobGroupingGatherTarget GatherTarget = MobGroupingGatherTarget.Route);

    /// <summary>
    /// Stable diagnostic codes for grouping decisions. They are logged at state transitions
    /// only (never per observation) and are carried on <see cref="MobGroupingDecision.Code"/>.
    /// </summary>
    public static class MobGroupingReason
    {
        // Suppression of a new gather walk.
        public const string TwoMobNoGather = "TwoMobNoGather";
        public const string CompactLocalGroup = "CompactLocalGroup";
        public const string NoConvergenceEvidence = "NoConvergenceEvidence";
        /// <summary>
        /// A scattered formation is approached toward the route waypoint although the formation
        /// trend is not (yet) measurable. The walk is still bounded (time/displacement budget,
        /// one attempt per encounter); the trend is advisory evidence, not a hard gate.
        /// </summary>
        public const string ScatteredMove = "ScatteredMove";
        /// <summary>Two route-compatible improvements were established: the walk starts.</summary>
        public const string ConvergenceConfirmed = "ConvergenceConfirmed";
        /// <summary>
        /// The bot is standing in combat (no route-compatible progress), so the gather walks
        /// toward the detected mob cluster instead of the route waypoint.
        /// </summary>
        public const string StationaryApproach = "StationaryApproach";
        public const string MovementNotAligned = "MovementNotAligned";
        public const string NoMovementProgress = "NoMovementProgress";
        public const string NoFormationImprovement = "NoFormationImprovement";
        public const string FormationDeteriorated = "FormationDeteriorated";
        public const string TimeBudgetReached = "TimeBudgetReached";
        public const string DistanceBudgetReached = "DistanceBudgetReached";
        public const string DetectionUnreliable = "DetectionUnreliable";
        public const string CombatContextLost = "CombatContextLost";
        public const string LootOwnership = "LootOwnership";
        public const string RecoveryOwnership = "RecoveryOwnership";
        public const string PauseOwnership = "PauseOwnership";
        public const string TransitionOwnership = "TransitionOwnership";
        public const string WindowUnavailable = "WindowUnavailable";
        public const string WaypointReached = "WaypointReached";
        public const string NoNavigationTarget = "NoNavigationTarget";
        public const string AttemptConsumed = "AttemptConsumed";
        public const string FeatureDisabled = "FeatureDisabled";
        public const string ModeUnsupported = "ModeUnsupported";
        public const string EncounterReset = "EncounterReset";
        /// <summary>Synchronous cancellation driven by a host boundary (pause/stop/owner change).</summary>
        public const string ExternalCancellation = "ExternalCancellation";

        /// <summary>Formats one diagnostic line as "Code: detail".</summary>
        public static string Describe(string code, string detail) =>
            string.IsNullOrEmpty(detail) ? code : $"{code}: {detail}";
    }

    /// <summary>
    /// Immutable output of the pure marker geometry analysis. Spread values are in client pixels.
    /// </summary>
    public sealed record MobGroupAnalysis
    {
        public bool IsValid { get; init; }
        public int DetectedMobCount { get; init; }
        public MobPoint PlayerAnchor { get; init; }
        public MobPoint Centroid { get; init; }
        public float AverageSpread { get; init; }
        public float MaximumSpread { get; init; }
        public float DominantMobDirectionX { get; init; }
        public float DominantMobDirectionY { get; init; }
        public bool HasDominantMobDirection { get; init; }
        public MobGroupingDirection RecommendedEscapeDirection { get; init; }
        public bool IsScattered { get; init; }
        /// <summary>
        /// True when the WHOLE detected formation satisfies the success thresholds.
        /// Distinct from <see cref="HasAttackReadyCluster"/>.
        /// </summary>
        public bool IsSufficientlyClustered { get; init; }
        /// <summary>
        /// True when more than <see cref="BotConstants.MobGrouping.AttackReadyClusterMobCount"/>
        /// mobs were detected and SOME subset of exactly that size already satisfies the same
        /// success thresholds. This lets the bot attack a valid local cluster even while
        /// outliers elsewhere in the formation would keep the whole-formation spread bad.
        /// Distinct from <see cref="IsSufficientlyClustered"/>.
        /// </summary>
        public bool HasAttackReadyCluster { get; init; }
        /// <summary>Size of the attack-ready subset, or 0 when <see cref="HasAttackReadyCluster"/> is false.</summary>
        public int AttackReadyClusterMobCount { get; init; }
        public float Confidence { get; init; }
        public MobMarker? CentralMob { get; init; }

        // ── Local cluster assessment (outlier tolerant) ─────────────────────────
        /// <summary>Average valid marker radius used to normalize every geometric threshold.</summary>
        public float EffectiveMarkerRadius { get; init; }
        /// <summary>Number of markers inside the compact neighborhood of the most compact pair.</summary>
        public int BestLocalClusterSize { get; init; }
        /// <summary>Midpoint of the most compact pair: the center of the best local cluster.</summary>
        public MobPoint BestLocalClusterCenter { get; init; }
        /// <summary>Screen-space distance of the most compact pair (pixels).</summary>
        public float BestPairDistancePx { get; init; }
        /// <summary>Most compact pair distance divided by <see cref="EffectiveMarkerRadius"/>.</summary>
        public float BestPairDistanceNormalized { get; init; }
        public float MeanNearestNeighborDistancePx { get; init; }
        /// <summary>Mean nearest-neighbor distance divided by <see cref="EffectiveMarkerRadius"/>.</summary>
        public float MeanNearestNeighborDistanceNormalized { get; init; }
        /// <summary>Compact-pair score in (0..1]; 1 means a perfectly overlapped pair.</summary>
        public float LocalCompactnessScore { get; init; }
        /// <summary>
        /// Normalized LOCAL formation quality in (0..1]; higher is more compact. This is the
        /// single score compared over time by <see cref="MobFormationHistory"/>. It is a
        /// screen-space proximity signal only: it makes no claim about forward-AoE geometry.
        /// </summary>
        public float FormationQuality { get; init; }
        /// <summary>
        /// A compact local group (pair or larger, see <see cref="BestLocalClusterSize"/>) already
        /// exists. Remote outliers never invalidate it; this is the primary reason NOT to walk.
        /// </summary>
        public bool HasCompactLocalGroup { get; init; }
        /// <summary>
        /// True when this sample may be used for a MOVEMENT decision: a valid detection, at
        /// least <see cref="BotConstants.MobGrouping.MinimumGatherMarkers"/> markers, at most
        /// <see cref="BotConstants.MobGrouping.MaximumMarkersForEvaluation"/> markers and enough
        /// mean marker confidence. Unreliable samples always fall back to normal combat.
        /// </summary>
        public bool IsMovementSampleReliable { get; init; }

        public static MobGroupAnalysis Invalid(MobPoint anchor) => new()
        {
            IsValid = false,
            PlayerAnchor = anchor
        };
    }

    /// <summary>
    /// The single process-wide runtime setting. It is deliberately not part of profile/path data
    /// and is not persisted. Volatile access allows the UI to toggle it while movement is running.
    /// </summary>
    public static class MobGroupingRuntimeSettings
    {
        private static int _enabled = BotConstants.MobGrouping.EnabledDefault ? 1 : 0;

        public static bool Enabled
        {
            get => Volatile.Read(ref _enabled) != 0;
            set => Volatile.Write(ref _enabled, value ? 1 : 0);
        }
    }

    internal static class MobGroupingDirectionMath
    {
        private static readonly MobGroupingDirection[] Directions =
        {
            MobGroupingDirection.North,
            MobGroupingDirection.NorthEast,
            MobGroupingDirection.East,
            MobGroupingDirection.SouthEast,
            MobGroupingDirection.South,
            MobGroupingDirection.SouthWest,
            MobGroupingDirection.West,
            MobGroupingDirection.NorthWest
        };

        /// <summary>Quantizes a screen-space vector (positive Y points down) to an eight-way direction.</summary>
        internal static MobGroupingDirection QuantizeScreenVector(float x, float y)
        {
            if (Math.Abs(x) < 0.0001f && Math.Abs(y) < 0.0001f)
                return MobGroupingDirection.North;

            double clockwiseDegrees = Math.Atan2(x, -y) * 180.0 / Math.PI;
            if (clockwiseDegrees < 0)
                clockwiseDegrees += 360.0;
            int sector = (int)Math.Floor((clockwiseDegrees + 22.5) / 45.0) % 8;
            return Directions[sector];
        }
    }
}
