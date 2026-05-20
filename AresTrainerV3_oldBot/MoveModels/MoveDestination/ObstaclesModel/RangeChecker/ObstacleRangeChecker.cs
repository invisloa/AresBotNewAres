// ============================================================
// DEPRECATED - Reference Only
// This file is part of the OLD bot (AresTrainerV3_oldBot).
// DO NOT MODIFY - kept for reference purposes only.
// For new development, use the DriverScanTester project.
// ============================================================
using AresTrainerV3.MoveModels.MoveToPoint.RouteCalculations;

namespace AresTrainerV3.MoveModels.MoveToPoint.ObstaclesModel.RangeChecker
{
	public class ObstacleRangeChecker : IObstacleRangeChecker
	{
		private readonly IObstacleChecker _obstacleChecker = FactoryMoveToPoint.CreateNewObstacleChecker();

		public Obstacle ObstacleIntersected { get; set; }

		public bool CheckForObstacles(List<CoordsPoint> routeCoordinates)
		{
			int moveNumber = 0;
			CoordsPoint abstractCurrentPosPoint = FactoryMoveToPoint.GetCurrentCoordPointXY;

			while (moveNumber < routeCoordinates.Count)
			{
				if (_obstacleChecker.CheckForObstacles(routeCoordinates[moveNumber]))
				{
					ObstacleIntersected = _obstacleChecker.ObstacleIntersected;
					return true;
				}
				abstractCurrentPosPoint = routeCoordinates[moveNumber];
				moveNumber++;
			}
			return false;
		}
	}

}