using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DriverScanTester.Models;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Loads path segments from SavedPaths JSON files and converts them into Waypoint lists
    /// usable by MovementSystem / PathRunnerService.
    /// 
    /// Convention: filenames like "Kharon_StartA_ToRepot.json", "Kharon_Repot_To_Wolves.json", etc.
    /// </summary>
    public class SavedPathLoader
    {
        private const string SAVE_DIR = "SavedPaths";
        private readonly Action<string> _log;

        public SavedPathLoader(Action<string> log)
        {
            _log = log;
        }

        /// <summary>
        /// Loads a segment file by name (with or without .json extension) from SavedPaths.
        /// Returns the list of Waypoints, or null if the file is missing or invalid.
        /// </summary>
        public List<Waypoint>? LoadSegment(string segmentFileName)
        {
            if (!segmentFileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                segmentFileName += ".json";

            string path = Path.Combine(SAVE_DIR, segmentFileName);

            if (!File.Exists(path))
            {
                _log($"[SavedPathLoader] File not found: {path}");
                return null;
            }

            try
            {
                string json = File.ReadAllText(path);
                var segment = JsonSerializer.Deserialize<PathSegment>(json);
                if (segment == null || segment.Points == null || segment.Points.Count == 0)
                {
                    _log($"[SavedPathLoader] Segment '{segmentFileName}' is empty or invalid.");
                    return null;
                }

                var waypoints = new List<Waypoint>();
                foreach (var pt in segment.Points)
                {
                    waypoints.Add(new Waypoint(pt.X, pt.Y, pt.Precision, pt.Mode, pt.CameraDistanceLock, pt.AttackDisengageDistance, pt.ZoneRestriction));
                }

                _log($"[SavedPathLoader] Loaded '{segmentFileName}': {waypoints.Count} points.");
                return waypoints;
            }
            catch (Exception ex)
            {
                _log($"[SavedPathLoader] Error loading '{segmentFileName}': {ex.Message}");
                return null;
            }
        }
    }
}
