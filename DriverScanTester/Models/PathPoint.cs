using DriverScanTester.Services;

namespace DriverScanTester.Models
{
    public class PathPoint
    {
        public const short DefaultCameraDistanceLock = BotConstants.Camera.DefaultDistanceLock;
        public const short DefaultAttackDisengageDistance = BotConstants.Combat.DefaultAttackDisengageDistance;

        public float X { get; set; }
        public float Y { get; set; }
        public MovementPrecision Precision { get; set; } = MovementPrecision.Medium;
        public BotMode Mode { get; set; } = BotMode.OnlyMove;
        public short CameraDistanceLock { get; set; } = DefaultCameraDistanceLock;
        public short AttackDisengageDistance { get; set; } = DefaultAttackDisengageDistance;
        public ZoneRestriction ZoneRestriction { get; set; } = ZoneRestriction.OutsideOnly;

        public PathPoint() { }
        public PathPoint(
            float x,
            float y,
            MovementPrecision precision = MovementPrecision.Medium,
            BotMode mode = BotMode.OnlyMove,
            short cameraDistanceLock = DefaultCameraDistanceLock,
            short attackDisengageDistance = DefaultAttackDisengageDistance,
            ZoneRestriction zoneRestriction = ZoneRestriction.OutsideOnly)
        { 
            X = x; 
            Y = y; 
            Precision = precision;
            Mode = mode;
            CameraDistanceLock = cameraDistanceLock;
            AttackDisengageDistance = attackDisengageDistance;
            ZoneRestriction = zoneRestriction;
        }
    }
}
