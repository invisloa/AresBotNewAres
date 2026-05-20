using DriverScanTester.Services;

namespace DriverScanTester.Models
{
    public class PathPoint
    {
        public float X { get; set; }
        public float Y { get; set; }
        public MovementPrecision Precision { get; set; } = MovementPrecision.Medium;
        public BotMode Mode { get; set; } = BotMode.OnlyMove;

        public PathPoint() { }
        public PathPoint(float x, float y, MovementPrecision precision = MovementPrecision.Medium, BotMode mode = BotMode.OnlyMove) 
        { 
            X = x; 
            Y = y; 
            Precision = precision;
            Mode = mode;
        }
    }
}
