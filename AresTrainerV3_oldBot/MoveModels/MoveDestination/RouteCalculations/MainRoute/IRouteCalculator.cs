// ============================================================
// DEPRECATED - Reference Only
// This file is part of the OLD bot (AresTrainerV3_oldBot).
// DO NOT MODIFY - kept for reference purposes only.
// For new development, use the DriverScanTester project.
// ============================================================
namespace AresTrainerV3.MoveModels.MoveToPoint.RouteCalculations.MainRoute
{
    public interface IRouteCalculator
    {
        List<CoordsPoint> CalculateMainRouteCoordinates(CoordsPoint end);
        public CoordsPoint CalculateAlternateLineEndPoint(CoordsPoint endPointOrigin, Line intersectedLine);

    }
}
