using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DriverScanTester.Services
{
    public enum LocalCellState
    {
        Unknown = 0,
        Free = 1,
        Risky = 2,
        Blocked = 3
    }

    /// <summary>
    /// Persistent local navigation map that records cell states (Free / Risky / Blocked)
    /// learned from action-stuck events.  Stored as human-readable JSON on disk.
    /// </summary>
    public sealed class LocalNavigationMap
    {
        private const float CELL_SIZE = 1.0f;

        // ── JSON DTO ────────────────────────────────────────────────

        private sealed class MapFile
        {
            public int Version { get; set; } = 1;
            public float CellSize { get; set; } = CELL_SIZE;
            public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
            public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
            public List<CellEntry> Cells { get; set; } = new List<CellEntry>();
        }

        private sealed class CellEntry
        {
            public int X { get; set; }
            public int Y { get; set; }
            public string State { get; set; } = "Unknown";
            public int Confidence { get; set; }
            public string? LastReason { get; set; }
            public float? AttemptedBearing { get; set; }
            public int AttemptedDirectionX { get; set; }
            public int AttemptedDirectionY { get; set; }
            public int SourceX { get; set; }
            public int SourceY { get; set; }
            public int TargetX { get; set; }
            public int TargetY { get; set; }
            public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        }

        // ── Internal representation ─────────────────────────────────

        private sealed class CellData
        {
            public LocalCellState State;
            public int Confidence;
            public string LastReason = string.Empty;
            public float? AttemptedBearing;
            public int AttemptedDirectionX;
            public int AttemptedDirectionY;
            public int SourceX;
            public int SourceY;
            public int TargetX;
            public int TargetY;
            public DateTime UpdatedAtUtc = DateTime.UtcNow;
        }

        private readonly Dictionary<(int X, int Y), CellData> _cells = new Dictionary<(int X, int Y), CellData>();
        private readonly Action<string> _log;
        private readonly string _filePath;
        private readonly string _directoryPath;
        private bool _isDirty;
        private bool _loaded;

        /// <summary>True when the map has unsaved changes.</summary>
        public bool IsDirty => _isDirty;

        public string FilePath => _filePath;
        public float CellSize => CELL_SIZE;

        public LocalNavigationMap(Action<string> log)
        {
            _log = log;
            _directoryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Navigation");
            _filePath = Path.Combine(_directoryPath, "local_navigation_map.json");
            Load();
        }

        // ── Load / Save ────────────────────────────────────────────

        public void Load()
        {
            _loaded = false;
            _cells.Clear();

            if (!File.Exists(_filePath))
            {
                _log($"[LocalMap] Created empty map at {_filePath}");
                _isDirty = false;
                _loaded = true;
                return;
            }

            try
            {
                string json = File.ReadAllText(_filePath);
                var mapFile = JsonSerializer.Deserialize<MapFile>(json);

                if (mapFile == null || mapFile.Cells == null)
                {
                    _log("[LocalMap] Map file is empty or malformed. Starting fresh.");
                    BackupCorrupted(json);
                    _isDirty = false;
                    _loaded = true;
                    return;
                }

                foreach (var entry in mapFile.Cells)
                {
                    var key = (entry.X, entry.Y);
                    if (_cells.ContainsKey(key))
                        continue;

                    var data = new CellData
                    {
                        State = ParseState(entry.State),
                        Confidence = entry.Confidence,
                        LastReason = entry.LastReason ?? string.Empty,
                        AttemptedBearing = entry.AttemptedBearing,
                        AttemptedDirectionX = entry.AttemptedDirectionX,
                        AttemptedDirectionY = entry.AttemptedDirectionY,
                        SourceX = entry.SourceX,
                        SourceY = entry.SourceY,
                        TargetX = entry.TargetX,
                        TargetY = entry.TargetY,
                        UpdatedAtUtc = entry.UpdatedAtUtc
                    };

                    _cells[key] = data;
                }

                _log($"[LocalMap] Loaded {_cells.Count} cells from {_filePath}");
                _isDirty = false;
                _loaded = true;
            }
            catch (Exception ex)
            {
                _log($"[LocalMap] Error loading map: {ex.Message}");
                try
                {
                    string corrupted = File.ReadAllText(_filePath);
                    BackupCorrupted(corrupted);
                }
                catch { /* best effort */ }
                _isDirty = false;
                _loaded = true;
            }
        }

        public void Save()
        {
            if (!_loaded)
                return;

            try
            {
                Directory.CreateDirectory(_directoryPath);

                var mapFile = new MapFile
                {
                    UpdatedAtUtc = DateTime.UtcNow,
                    Cells = new List<CellEntry>(_cells.Count)
                };

                foreach (var kvp in _cells)
                {
                    var data = kvp.Value;
                    mapFile.Cells.Add(new CellEntry
                    {
                        X = kvp.Key.X,
                        Y = kvp.Key.Y,
                        State = StateToString(data.State),
                        Confidence = data.Confidence,
                        LastReason = data.LastReason,
                        AttemptedBearing = data.AttemptedBearing,
                        AttemptedDirectionX = data.AttemptedDirectionX,
                        AttemptedDirectionY = data.AttemptedDirectionY,
                        SourceX = data.SourceX,
                        SourceY = data.SourceY,
                        TargetX = data.TargetX,
                        TargetY = data.TargetY,
                        UpdatedAtUtc = data.UpdatedAtUtc
                    });
                }

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };

                string json = JsonSerializer.Serialize(mapFile, options);
                File.WriteAllText(_filePath, json);
                _isDirty = false;

                _log($"[LocalMap] Saved {_cells.Count} cells to {_filePath}");
            }
            catch (Exception ex)
            {
                _log($"[LocalMap] Error saving map: {ex.Message}");
            }
        }

        public void SaveIfDirty()
        {
            if (_isDirty)
            {
                Save();
            }
        }

        // ─── Query ─────────────────────────────────────────────────

        public LocalCellState GetCell(int x, int y)
        {
            if (_cells.TryGetValue((x, y), out var data))
                return data.State;
            return LocalCellState.Unknown;
        }

        public bool IsBlocked(float worldX, float worldY)
        {
            var (cx, cy) = WorldToCell(worldX, worldY);
            return GetCell(cx, cy) == LocalCellState.Blocked;
        }

        public bool IsRisky(float worldX, float worldY)
        {
            var (cx, cy) = WorldToCell(worldX, worldY);
            return GetCell(cx, cy) == LocalCellState.Risky;
        }

        public bool IsKnownFree(float worldX, float worldY)
        {
            var (cx, cy) = WorldToCell(worldX, worldY);
            return GetCell(cx, cy) == LocalCellState.Free;
        }

        public bool IsUnknown(float worldX, float worldY)
        {
            var (cx, cy) = WorldToCell(worldX, worldY);
            return GetCell(cx, cy) == LocalCellState.Unknown;
        }

        /// <summary>Returns true if the cell is considered impassable (Blocked).</summary>
        public bool IsBlockedCell(int x, int y)
        {
            return GetCell(x, y) == LocalCellState.Blocked;
        }

        /// <summary>Returns true if the cell is risky (avoid unless necessary).</summary>
        public bool IsRiskyCell(int x, int y)
        {
            return GetCell(x, y) == LocalCellState.Risky;
        }

        // ─── Mark ──────────────────────────────────────────────────

        public void MarkFree(float worldX, float worldY, string reason)
        {
            var (cx, cy) = WorldToCell(worldX, worldY);
            var key = (cx, cy);

            if (!_cells.TryGetValue(key, out var data))
            {
                data = new CellData { State = LocalCellState.Unknown };
                _cells[key] = data;
            }

            if (data.State != LocalCellState.Free)
            {
                data.State = LocalCellState.Free;
                data.Confidence = Math.Max(data.Confidence + 1, 1);
                data.LastReason = reason;
                data.UpdatedAtUtc = DateTime.UtcNow;
                _isDirty = true;
                _log($"[LocalMap] MarkFree cell=({cx},{cy}) reason={reason}");
            }
            else
            {
                // Already Free — increase confidence
                data.Confidence = Math.Min(data.Confidence + 1, 10);
                data.UpdatedAtUtc = DateTime.UtcNow;
                _isDirty = true;
            }
        }

        public void MarkRisky(float worldX, float worldY, string reason,
            float? attemptedBearing = null,
            int attemptedDirectionX = 0, int attemptedDirectionY = 0,
            int sourceX = 0, int sourceY = 0,
            int targetX = 0, int targetY = 0)
        {
            var (cx, cy) = WorldToCell(worldX, worldY);
            var key = (cx, cy);

            if (!_cells.TryGetValue(key, out var data))
            {
                data = new CellData { State = LocalCellState.Unknown };
                _cells[key] = data;
            }

            // Always update state to Risky with metadata
            data.State = LocalCellState.Risky;
            data.Confidence = Math.Max(data.Confidence + 1, 1);
            data.LastReason = reason;
            data.AttemptedBearing = attemptedBearing;
            data.AttemptedDirectionX = attemptedDirectionX;
            data.AttemptedDirectionY = attemptedDirectionY;
            data.SourceX = sourceX;
            data.SourceY = sourceY;
            data.TargetX = targetX;
            data.TargetY = targetY;
            data.UpdatedAtUtc = DateTime.UtcNow;
            _isDirty = true;

            _log($"[LocalMap] MarkRisky cell=({cx},{cy}) confidence={data.Confidence} reason={reason} source=({sourceX},{sourceY}) target=({targetX},{targetY}) bearing={attemptedBearing}");
        }

        public void MarkBlocked(float worldX, float worldY, string reason,
            float? attemptedBearing = null,
            int attemptedDirectionX = 0, int attemptedDirectionY = 0,
            int sourceX = 0, int sourceY = 0,
            int targetX = 0, int targetY = 0)
        {
            var (cx, cy) = WorldToCell(worldX, worldY);
            var key = (cx, cy);

            if (!_cells.TryGetValue(key, out var data))
            {
                data = new CellData { State = LocalCellState.Unknown };
                _cells[key] = data;
            }

            data.State = LocalCellState.Blocked;
            data.Confidence = Math.Max(data.Confidence + 1, 3);
            data.LastReason = reason;
            data.AttemptedBearing = attemptedBearing;
            data.AttemptedDirectionX = attemptedDirectionX;
            data.AttemptedDirectionY = attemptedDirectionY;
            data.SourceX = sourceX;
            data.SourceY = sourceY;
            data.TargetX = targetX;
            data.TargetY = targetY;
            data.UpdatedAtUtc = DateTime.UtcNow;
            _isDirty = true;

            _log($"[LocalMap] MarkBlocked cell=({cx},{cy}) confidence={data.Confidence} reason={reason} source=({sourceX},{sourceY}) target=({targetX},{targetY}) bearing={attemptedBearing}");
        }

        /// <summary>
        /// Marks an attempted cell based on action-stuck feedback.
        /// If confidence is already high enough, mark as Blocked; otherwise Risky.
        /// </summary>
        public void MarkStuckAttemptedCell(float worldX, float worldY, string reason,
            float? attemptedBearing = null,
            int attemptedDirectionX = 0, int attemptedDirectionY = 0,
            int sourceX = 0, int sourceY = 0,
            int targetX = 0, int targetY = 0)
        {
            var (cx, cy) = WorldToCell(worldX, worldY);
            var key = (cx, cy);

            if (!_cells.TryGetValue(key, out var data))
            {
                data = new CellData { State = LocalCellState.Unknown };
                _cells[key] = data;
            }

            data.Confidence++;
            data.LastReason = reason;
            data.AttemptedBearing = attemptedBearing;
            data.AttemptedDirectionX = attemptedDirectionX;
            data.AttemptedDirectionY = attemptedDirectionY;
            data.SourceX = sourceX;
            data.SourceY = sourceY;
            data.TargetX = targetX;
            data.TargetY = targetY;
            data.UpdatedAtUtc = DateTime.UtcNow;

            // Threshold: confidence >= 3 => Blocked, otherwise Risky
            if (data.Confidence >= 3)
            {
                data.State = LocalCellState.Blocked;
                _log($"[LocalMap] MarkBlocked cell=({cx},{cy}) confidence={data.Confidence} reason={reason} source=({sourceX},{sourceY}) target=({targetX},{targetY}) bearing={attemptedBearing}");
            }
            else
            {
                data.State = LocalCellState.Risky;
                _log($"[LocalMap] MarkRisky cell=({cx},{cy}) confidence={data.Confidence} reason={reason} source=({sourceX},{sourceY}) target=({targetX},{targetY}) bearing={attemptedBearing}");
            }

            _isDirty = true;
        }

        /// <summary>Decrease confidence for a cell (e.g. after successful traversal).</summary>
        public void ReduceConfidence(int cellX, int cellY)
        {
            var key = (cellX, cellY);
            if (!_cells.TryGetValue(key, out var data))
                return;

            data.Confidence = Math.Max(0, data.Confidence - 1);
            data.UpdatedAtUtc = DateTime.UtcNow;

            if (data.Confidence <= 0 && data.State != LocalCellState.Free)
            {
                _cells.Remove(key);
                _log($"[LocalMap] Removed cell=({cellX},{cellY}) after confidence reached 0");
            }
            else if (data.State == LocalCellState.Blocked && data.Confidence < 3)
            {
                data.State = LocalCellState.Risky;
                _log($"[LocalMap] Downgraded cell=({cellX},{cellY}) from Blocked to Risky (confidence={data.Confidence})");
            }
            else if (data.State == LocalCellState.Risky && data.Confidence < 1)
            {
                _cells.Remove(key);
                _log($"[LocalMap] Removed cell=({cellX},{cellY}) after confidence reached 0");
            }

            _isDirty = true;
        }

        // ─── Cell conversion ───────────────────────────────────────

        public static (int CellX, int CellY) WorldToCell(float worldX, float worldY)
        {
            int cx = (int)MathF.Round(worldX / CELL_SIZE);
            int cy = (int)MathF.Round(worldY / CELL_SIZE);
            return (cx, cy);
        }

        // ─── Helpers ───────────────────────────────────────────────

        private static LocalCellState ParseState(string state) => state switch
        {
            "Free" => LocalCellState.Free,
            "Risky" => LocalCellState.Risky,
            "Blocked" => LocalCellState.Blocked,
            _ => LocalCellState.Unknown
        };

        private static string StateToString(LocalCellState state) => state switch
        {
            LocalCellState.Free => "Free",
            LocalCellState.Risky => "Risky",
            LocalCellState.Blocked => "Blocked",
            _ => "Unknown"
        };

        private void BackupCorrupted(string corruptedContent)
        {
            try
            {
                string backupDir = Path.Combine(_directoryPath, "backup");
                Directory.CreateDirectory(backupDir);
                string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                string backupPath = Path.Combine(backupDir, $"local_navigation_map_corrupted_{timestamp}.json");
                File.WriteAllText(backupPath, corruptedContent);
                _log($"[LocalMap] Corrupted map file. Backup created: {backupPath}");
            }
            catch (Exception ex)
            {
                _log($"[LocalMap] Failed to create backup: {ex.Message}");
            }
        }
    }
}
