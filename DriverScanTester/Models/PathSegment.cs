using System.Collections.Generic;
using DriverScanTester.Services;

namespace DriverScanTester.Models
{
    public class PathSegment
    {
        public string Name { get; set; } = "NewSegment";
        public MovementPrecision Precision { get; set; } = MovementPrecision.Medium;
        public BotMode Mode { get; set; } = BotMode.OnlyMove;
        public bool LoopRoute { get; set; } = false;

        /// <summary>
        /// When true, every waypoint of this saved path runs without automatic looting:
        /// no normal loot scans, SOD/SOP scans, loot camera changes, loot screenshots or
        /// item collection. Applies both to a standalone editor run and to a path loaded
        /// by a bot profile. Only automatic loot is disabled; combat/movement continue.
        /// </summary>
        public bool DisableAllLoot { get; set; } = false;

        public ZoneRestriction ZoneRestriction { get; set; } = ZoneRestriction.OutsideOnly;
        public List<PathPoint> Points { get; set; } = new List<PathPoint>();

        public PathSegment() { }
    }
}
