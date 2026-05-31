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
        public ZoneRestriction ZoneRestriction { get; set; } = ZoneRestriction.OutsideOnly;
        public List<PathPoint> Points { get; set; } = new List<PathPoint>();

        public PathSegment() { }
    }
}
