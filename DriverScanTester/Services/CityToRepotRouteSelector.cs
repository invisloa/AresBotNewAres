using System;
using DriverScanTester.Models;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Selects the appropriate city-to-repot path based on the player's current position (snapshot)
    /// and the configured profile's StartRoutes.
    /// </summary>
    public class CityToRepotRouteSelector
    {
        private readonly Action<string> _log;

        public CityToRepotRouteSelector(Action<string> log)
        {
            _log = log;
        }

        /// <summary>
        /// Finds the first StartRoute whose area contains the given snapshot.
        /// Returns the route on success, or null if no route matches.
        /// </summary>
        public StartRoute? SelectRoute(BotProfile profile, GameSnapshot snapshot)
        {
            if (profile.StartRoutes == null || profile.StartRoutes.Count == 0)
            {
                _log("[RouteSelector] Profile has no StartRoutes defined.");
                return null;
            }

            foreach (var route in profile.StartRoutes)
            {
                if (route.Area == null)
                {
                    _log($"[RouteSelector] StartRoute '{route.Name}' has no Area defined. Skipping.");
                    continue;
                }

                if (route.Area.Contains(snapshot))
                {
                    _log($"[RouteSelector] Matched StartRoute '{route.Name}' (Area: {route.Area}) -> Path: {route.PathFile}");
                    return route;
                }
            }

            _log($"[RouteSelector] No StartRoute matches current position: Map={snapshot.MapNumber}, X={snapshot.X:F1}, Y={snapshot.Y:F1}");
            return null;
        }
    }
}
