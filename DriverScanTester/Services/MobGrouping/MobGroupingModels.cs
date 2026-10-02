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

    public readonly record struct MobGroupingDecision(
        MobGroupingDirective Directive,
        MobGroupingDirection? Direction = null,
        int MoveNumber = 0,
        string Reason = "");

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

        internal static (float X, float Y) ToScreenVector(MobGroupingDirection direction)
        {
            double radians = (int)direction * Math.PI / 4.0;
            return ((float)Math.Sin(radians), (float)-Math.Cos(radians));
        }

        /// <summary>Clockwise camera-bearing offset, with North=0 and East=90 degrees.</summary>
        internal static float ToBearingOffset(MobGroupingDirection direction) => (int)direction * 45f;
    }
}
