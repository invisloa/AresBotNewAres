// ============================================================
// DEPRECATED - Reference Only
// This file is part of the OLD bot (AresTrainerV3_oldBot).
// DO NOT MODIFY - kept for reference purposes only.
// For new development, use the DriverScanTester project.
// ============================================================
namespace AresTrainerV3.MoveModels.MoveToPoint.ObstaclesModel.RangeChecker
{
	public interface IObstacleRangeChecker
	{
		public Obstacle ObstacleIntersected
		{
			get;
			set;
		}

		bool CheckForObstacles(List<CoordsPoint> routeCoordinates);
	}
}