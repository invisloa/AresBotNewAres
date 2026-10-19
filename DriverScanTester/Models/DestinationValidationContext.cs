using System;

namespace DriverScanTester.Models
{
    /// <summary>
    /// Optional expectation a post-transition position must satisfy before it may be
    /// used for the next route / destination decision.
    ///
    /// A map read alone is never enough (the game can expose the destination map while
    /// X/Y still belong to the previous location) and neither is an arbitrary
    /// coordinate difference from the pre-transition snapshot (a shift of a couple of
    /// tiles around the portal is exactly what stale coordinates look like). When the
    /// workflow knows what the destination should look like (a route start position,
    /// a protected map, ...) it supplies this context so
    /// <see cref="Services.PositionSyncService"/> can validate the position against the
    /// destination instead of against "something changed".
    ///
    /// The acceptance radius is intentionally a fixed synchronization constant (see
    /// <see cref="BotConstants.Delays.PositionSyncDestinationValidationRadiusTiles"/>)
    /// and NOT the user's configurable start-protection tolerance: the tolerance must
    /// never be the sole proof of coordinate freshness.
    /// </summary>
    public sealed class DestinationValidationContext
    {
        public DestinationValidationContext(
            int expectedMap,
            int? expectedX,
            int? expectedY,
            int radiusTiles,
            string label)
        {
            ExpectedMap = expectedMap;
            ExpectedX = expectedX;
            ExpectedY = expectedY;
            RadiusTiles = Math.Max(0, radiusTiles);
            Label = string.IsNullOrWhiteSpace(label) ? "destination" : label;
        }

        /// <summary>Expected map number. Values &lt;= 0 mean "any map".</summary>
        public int ExpectedMap { get; }

        /// <summary>Expected X coordinate, or null when only the map is constrained.</summary>
        public int? ExpectedX { get; }

        /// <summary>Expected Y coordinate, or null when only the map is constrained.</summary>
        public int? ExpectedY { get; }

        /// <summary>Acceptance radius (tiles) around <see cref="ExpectedX"/>/<see cref="ExpectedY"/>.</summary>
        public int RadiusTiles { get; }

        /// <summary>Human readable label used in diagnostics (route name, start check, ...).</summary>
        public string Label { get; }

        /// <summary>True when a specific map number is constrained.</summary>
        public bool HasMapExpectation => ExpectedMap > 0;

        /// <summary>True when specific X/Y coordinates are constrained.</summary>
        public bool HasPositionExpectation => ExpectedX.HasValue && ExpectedY.HasValue;

        /// <summary>
        /// Builds a context from a route/profile start check. The acceptance radius is
        /// the fixed synchronization radius (not the caller's tolerance).
        /// </summary>
        public static DestinationValidationContext ForStartPosition(
            int mapNumber,
            int startX,
            int startY,
            string label)
            => new(
                mapNumber,
                startX,
                startY,
                BotConstants.Delays.PositionSyncDestinationValidationRadiusTiles,
                label);

        /// <summary>Builds a map-only context (the destination coordinates are unknown).</summary>
        public static DestinationValidationContext ForMap(int mapNumber, string label)
            => new(mapNumber, null, null, 0, label);

        /// <summary>Builds an explicit position context.</summary>
        public static DestinationValidationContext ForPosition(
            int mapNumber,
            int expectedX,
            int expectedY,
            int radiusTiles,
            string label)
            => new(mapNumber, expectedX, expectedY, radiusTiles, label);

        /// <summary>
        /// True when the snapshot satisfies the destination expectation: coherent read,
        /// the expected map (when constrained) and the expected coordinates within the
        /// acceptance radius (when constrained). Map and position are evaluated
        /// independently so a matching map never excuses a far position.
        /// </summary>
        public bool Matches(PlayerStateSnapshot state)
        {
            if (!state.HasCoherentState)
                return false;

            if (HasMapExpectation && state.Map != ExpectedMap)
                return false;

            if (!HasPositionExpectation)
                return true;

            return Math.Abs((int)state.X - ExpectedX!.Value) <= RadiusTiles &&
                   Math.Abs((int)state.Y - ExpectedY!.Value) <= RadiusTiles;
        }

        /// <summary>X offset of the snapshot from the expected position (0 when unknown/unreadable).</summary>
        public int OffsetX(PlayerStateSnapshot state)
            => HasPositionExpectation && state.PositionReadSucceeded
                ? (int)state.X - ExpectedX!.Value
                : 0;

        /// <summary>Y offset of the snapshot from the expected position (0 when unknown/unreadable).</summary>
        public int OffsetY(PlayerStateSnapshot state)
            => HasPositionExpectation && state.PositionReadSucceeded
                ? (int)state.Y - ExpectedY!.Value
                : 0;

        /// <summary>Structured description used in diagnostics.</summary>
        public string Describe()
        {
            string map = HasMapExpectation ? ExpectedMap.ToString() : "any";
            string position = HasPositionExpectation
                ? $"({ExpectedX!.Value},{ExpectedY!.Value})"
                : "(unconstrained)";
            return $"Label={Label} ExpectedMap={map} ExpectedPosition={position} Radius={RadiusTiles}";
        }
    }
}
