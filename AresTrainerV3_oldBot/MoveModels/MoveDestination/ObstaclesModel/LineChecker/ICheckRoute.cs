// ============================================================
// DEPRECATED - Reference Only
// This file is part of the OLD bot (AresTrainerV3_oldBot).
// DO NOT MODIFY - kept for reference purposes only.
// For new development, use the DriverScanTester project.
// ============================================================
namespace AresTrainerV3.MoveModels.MoveToPoint.ObstaclesModel.LineChecker
{
    public interface ICheckRoute
    {
        public Line IntersectedLine
        {
            get;
        }
        bool CheckForObstacles(List<CoordsPoint> routeCoordinates, List<Obstacle> obstacles);
    }
}
