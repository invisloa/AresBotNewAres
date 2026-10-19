using System;

namespace DriverScanTester.Models
{
    /// <summary>
    /// One coherent read of the live player object. The map and the coordinates are
    /// read from the SAME resolved player base, so a map change occurring between
    /// reads can never be paired with X/Y from a different player object.
    ///
    /// A failed read is kept explicit through the success flags. Callers must never
    /// treat an unavailable read as position (0, 0) or as a confirmed map.
    /// </summary>
    public readonly record struct PlayerStateSnapshot(
        bool PlayerPointerReadSucceeded,
        ulong PlayerBase,
        bool MapReadSucceeded,
        int Map,
        bool PositionReadSucceeded,
        short X,
        short Y,
        DateTime UtcTimestamp)
    {
        /// <summary>
        /// True only when the player pointer, map and both coordinates were read
        /// successfully and the map number is a usable (nonzero) value.
        /// </summary>
        public bool HasCoherentState =>
            PlayerPointerReadSucceeded &&
            PlayerBase != 0 &&
            MapReadSucceeded &&
            Map != 0 &&
            PositionReadSucceeded;

        /// <summary>True when two snapshots describe different tile coordinates.</summary>
        public bool PositionChangedFrom(PlayerStateSnapshot other) => X != other.X || Y != other.Y;

        public string Describe()
        {
            string map = MapReadSucceeded ? Map.ToString() : "unreadable";
            string position = PositionReadSucceeded ? $"({X},{Y})" : "(unreadable)";
            string pointer = PlayerPointerReadSucceeded ? $"0x{PlayerBase:X}" : "unreadable";
            return $"Map={map} Position={position} PointerRead={PlayerPointerReadSucceeded} PlayerBase={pointer} ReadSuccess={HasCoherentState}";
        }
    }
}
