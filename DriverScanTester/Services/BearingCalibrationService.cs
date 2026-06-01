// <copyright file="BearingCalibrationService.cs" company="DriverScanTester">
//     Copyright (c) DriverScanTester. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DriverScanTester.Services;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Owns the runtime-mutable bearing calibration table.
    ///
    /// The bot needs to convert a bearing in degrees (0=N, 90=E, 180=S, 270=W)
    /// into a raw game-angle value (16-bit short stored at [Ares+0x4704B0]+0x1aa).
    /// The exact mapping between the two was hand-measured once and hardcoded as
    /// <see cref="BotConstants.BearingCalibration"/>. Different game builds,
    /// camera vertical angles, or aspect ratios can shift those values, leaving
    /// the bot slightly off-target.
    ///
    /// This service exposes the same 13-point table at runtime, allows it to be
    /// re-measured and overridden, and persists overrides to a JSON file so they
    /// survive restarts.
    ///
    /// Default values come from <see cref="BotConstants.BearingCalibration"/> and
    /// are restored by <see cref="ResetToDefaults"/>.
    /// </summary>
    public static class BearingCalibrationService
    {
        /// <summary>Number of points in the calibration table (0°, 30°, ..., 360°).</summary>
        public const int PointCount = 13;

        private const string DefaultFileName = "camera_calibration.json";

        private static readonly float[] _bearingDeg = new float[PointCount]
        {
            0f, 30f, 60f, 90f, 120f, 150f, 180f, 210f, 240f, 270f, 300f, 330f, 360f
        };

        // Lock guarding all reads/writes of _gameAngles and _fullSpinGameUnits. The bot
        // tick loop and the UI thread both touch this state.
        private static readonly object _lock = new object();
        private static float[] _gameAngles = null!;
        private static float _fullSpinGameUnits;

        /// <summary>Event raised after the calibration table is changed in any way
        /// (defaults loaded, file loaded, runtime override applied).</summary>
        public static event EventHandler? Changed;

        static BearingCalibrationService()
        {
            ResetToDefaults();
        }

        /// <summary>Default JSON file path used by Save/Load if no path is provided.
        /// Lives next to the .exe (same convention as shop.json, mage.json, etc.).</summary>
        public static string DefaultFilePath
        {
            get
            {
                string baseDir = AppContext.BaseDirectory;
                return Path.Combine(baseDir, DefaultFileName);
            }
        }

        /// <summary>True if the current values differ from the hardcoded defaults.</summary>
        public static bool IsOverridden { get; private set; }

        /// <summary>Full-spin circumference in game-angle units (N2 - N at 360°).</summary>
        public static float FullSpinGameUnits
        {
            get { lock (_lock) return _fullSpinGameUnits; }
        }

        /// <summary>Snapshot of the 13-point table (bearing-deg + game-angle, in degree order).</summary>
        public static IReadOnlyList<CalibrationPoint> Points
        {
            get
            {
                lock (_lock)
                {
                    var snapshot = new CalibrationPoint[PointCount];
                    for (int i = 0; i < PointCount; i++)
                    {
                        snapshot[i] = new CalibrationPoint(_bearingDeg[i], _gameAngles[i]);
                    }
                    return snapshot;
                }
            }
        }

        /// <summary>
        /// Returns a fresh array of <see cref="GeometryUtils.BearingCalibrationPoint"/>
        /// reflecting the current state. Used by <see cref="GeometryUtils"/> when
        /// converting bearings.
        /// </summary>
        internal static GeometryUtils.BearingCalibrationPoint[] BuildGeometryTable()
        {
            lock (_lock)
            {
                var arr = new GeometryUtils.BearingCalibrationPoint[PointCount];
                for (int i = 0; i < PointCount; i++)
                {
                    arr[i] = new GeometryUtils.BearingCalibrationPoint(_bearingDeg[i], _gameAngles[i]);
                }
                return arr;
            }
        }

        /// <summary>Restores the hardcoded defaults from <see cref="BotConstants.BearingCalibration"/>.</summary>
        public static void ResetToDefaults()
        {
            lock (_lock)
            {
                _gameAngles = new float[PointCount]
                {
                    BotConstants.BearingCalibration.North,
                    BotConstants.BearingCalibration.Deg30,
                    BotConstants.BearingCalibration.Deg60,
                    BotConstants.BearingCalibration.East,
                    BotConstants.BearingCalibration.Deg120,
                    BotConstants.BearingCalibration.Deg150,
                    BotConstants.BearingCalibration.South,
                    BotConstants.BearingCalibration.Deg210,
                    BotConstants.BearingCalibration.Deg240,
                    BotConstants.BearingCalibration.West,
                    BotConstants.BearingCalibration.Deg300,
                    BotConstants.BearingCalibration.Deg330,
                    BotConstants.BearingCalibration.NorthFullCircle
                };
                _fullSpinGameUnits = BotConstants.BearingCalibration.FullSpinGameUnits;
                IsOverridden = false;
            }
            RaiseChanged();
        }

        /// <summary>
        /// Overrides the four cardinal points measured by the user (N=0°, E=90°, S=180°, W=270°).
        /// Recomputes the 9 intermediate points by linear interpolation between adjacent cardinals,
        /// and sets the 360° (N2) point to <paramref name="north"/> + <paramref name="fullSpinGameUnits"/>.
        /// If <paramref name="fullSpinGameUnits"/> is &lt;= 0, it is estimated as 4×(E-N).
        /// </summary>
        public static void SetCardinalMeasured(
            float north,
            float east,
            float south,
            float west,
            float fullSpinGameUnits = -1f)
        {
            if (fullSpinGameUnits <= 0f)
            {
                fullSpinGameUnits = 4f * (east - north);
            }

            var newAngles = new float[PointCount];
            newAngles[0]  = north;                          // 0°
            newAngles[1]  = Lerp(north, east, 1f/3f);      // 30°
            newAngles[2]  = Lerp(north, east, 2f/3f);      // 60°
            newAngles[3]  = east;                           // 90°
            newAngles[4]  = Lerp(east, south, 1f/3f);      // 120°
            newAngles[5]  = Lerp(east, south, 2f/3f);      // 150°
            newAngles[6]  = south;                          // 180°
            newAngles[7]  = Lerp(south, west, 1f/3f);      // 210°
            newAngles[8]  = Lerp(south, west, 2f/3f);      // 240°
            newAngles[9]  = west;                           // 270°
            float northAtFullCircle = north + fullSpinGameUnits;
            newAngles[10] = Lerp(west, northAtFullCircle, 1f/3f); // 300°
            newAngles[11] = Lerp(west, northAtFullCircle, 2f/3f); // 330°
            newAngles[12] = northAtFullCircle;                    // 360°

            lock (_lock)
            {
                _gameAngles = newAngles;
                _fullSpinGameUnits = fullSpinGameUnits;
                IsOverridden = true;
            }
            RaiseChanged();
        }

        /// <summary>
        /// Replaces a single point in the table. Use after <see cref="SetCardinalMeasured"/>
        /// if a measured value disagrees with the linear interpolation. Allocates a new
        /// backing array to stay safe with concurrent readers on the bot tick thread.
        /// </summary>
        public static void SetPoint(int index, float gameAngle)
        {
            if (index < 0 || index >= PointCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            lock (_lock)
            {
                var copy = (float[])_gameAngles.Clone();
                copy[index] = gameAngle;
                _gameAngles = copy;
                if (index == 0 || index == PointCount - 1)
                {
                    _fullSpinGameUnits = _gameAngles[PointCount - 1] - _gameAngles[0];
                }
                IsOverridden = true;
            }
            RaiseChanged();
        }

        /// <summary>
        /// Replaces the entire 13-point table atomically (one Changed event) and
        /// recomputes the full-spin circumference from the first and last point.
        /// </summary>
        public static void ReplaceAll(float[] newGameAngles)
        {
            if (newGameAngles == null || newGameAngles.Length != PointCount)
            {
                throw new ArgumentException($"Expected array of length {PointCount}.", nameof(newGameAngles));
            }

            var copy = new float[PointCount];
            Array.Copy(newGameAngles, copy, PointCount);

            lock (_lock)
            {
                _gameAngles = copy;
                _fullSpinGameUnits = _gameAngles[PointCount - 1] - _gameAngles[0];
                IsOverridden = true;
            }
            RaiseChanged();
        }

        /// <summary>
        /// Output of <see cref="FitLinear"/>: the linear model
        /// <c>game_angle ≈ scale * bearing_deg + offset</c>, plus per-point
        /// residuals and the full-spin circumference derived from the slope.
        /// </summary>
        public sealed class LinearFit
        {
            public float Scale { get; init; }       // game-units per degree
            public float Offset { get; init; }      // game-angle at bearing = 0°
            public float FullSpin { get; init; }    // scale * 360
            public float MeanResidual { get; init; }
            public int PointCount { get; init; }
        }

        /// <summary>
        /// Fits a linear model <c>game_angle = scale * bearing_deg + offset</c> to
        /// the supplied (bearing, game_angle) pairs using ordinary least squares,
        /// and applies it as a 13-point table via <see cref="SetCardinalMeasured"/>
        /// (with intermediate points filled by linear interpolation).
        ///
        /// The model assumes the bearing→game-angle mapping is approximately
        /// affine. This is true within a 360° turn on the same game build.
        /// </summary>
        /// <param name="bearingsDeg">Bearings in degrees (0=N, 90=E, 180=S, 270=W).
        /// Will be normalised to [0, 360). Need at least 2 distinct values.</param>
        /// <param name="gameAngles">Observed game-angle raw values matching the bearings.</param>
        /// <param name="fit">On success, the fitted model.</param>
        public static bool FitLinear(
            IReadOnlyList<float> bearingsDeg,
            IReadOnlyList<float> gameAngles,
            out LinearFit fit)
        {
            fit = default!;
            if (bearingsDeg == null || gameAngles == null
                || bearingsDeg.Count != gameAngles.Count
                || bearingsDeg.Count < 2)
            {
                return false;
            }

            int n = bearingsDeg.Count;

            // Drop pairs whose bearing is degenerate (duplicate).
            // At least 2 unique bearings are required.
            double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;
            int kept = 0;
            for (int i = 0; i < n; i++)
            {
                float b = NormalizeBearing(bearingsDeg[i]);
                float y = gameAngles[i];
                if (float.IsNaN(b) || float.IsNaN(y) || float.IsInfinity(b) || float.IsInfinity(y))
                {
                    continue;
                }
                sumX += b;
                sumY += y;
                sumXX += b * b;
                sumXY += b * y;
                kept++;
            }

            if (kept < 2)
            {
                return false;
            }

            double denom = kept * sumXX - sumX * sumX;
            if (Math.Abs(denom) < 1e-9)
            {
                // All bearings equal — can't fit a line.
                return false;
            }

            double scale = (kept * sumXY - sumX * sumY) / denom;
            double offset = (sumY - scale * sumX) / kept;

            // Mean absolute residual (in game-units).
            double sumAbsResid = 0;
            for (int i = 0; i < n; i++)
            {
                float b = NormalizeBearing(bearingsDeg[i]);
                float y = gameAngles[i];
                if (float.IsNaN(b) || float.IsNaN(y)) continue;
                double predicted = scale * b + offset;
                sumAbsResid += Math.Abs(predicted - y);
            }
            float meanResid = (float)(sumAbsResid / kept);

            float scaleF = (float)scale;
            float offsetF = (float)offset;
            float fullSpin = scaleF * 360f;

            // Apply as 4-point cardinal fit, but the explicit-cardinals form
            // (SetCardinalMeasured) only accepts 0/90/180/270 — which is exactly
            // what we want here, so we re-derive N/E/S/W from the fit.
            float n0 = offsetF;                        // bearing 0°
            float n90 = offsetF + scaleF * 90f;        // bearing 90°
            float n180 = offsetF + scaleF * 180f;      // bearing 180°
            float n270 = offsetF + scaleF * 270f;      // bearing 270°

            SetCardinalMeasured(n0, n90, n180, n270, fullSpin);

            fit = new LinearFit
            {
                Scale = scaleF,
                Offset = offsetF,
                FullSpin = fullSpin,
                MeanResidual = meanResid,
                PointCount = kept,
            };
            return true;
        }

        private static float NormalizeBearing(float deg)
        {
            while (deg < 0f) deg += 360f;
            while (deg >= 360f) deg -= 360f;
            return deg;
        }

        /// <summary>Saves the current calibration to a JSON file. Default path is
        /// <see cref="DefaultFilePath"/>.</summary>
        public static void SaveToFile(string? path = null)
        {
            path ??= DefaultFilePath;

            float[] angles;
            float fullSpin;
            lock (_lock)
            {
                angles = (float[])_gameAngles.Clone();
                fullSpin = _fullSpinGameUnits;
            }

            var dto = new PersistedDto { GameAngles = angles, FullSpinGameUnits = fullSpin };
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(dto, options);
            File.WriteAllText(path, json);
        }

        /// <summary>Loads calibration from a JSON file. If <paramref name="path"/> is null,
        /// <see cref="DefaultFilePath"/> is used. If the file does not exist or is invalid,
        /// defaults are kept and the method returns false.</summary>
        public static bool LoadFromFile(string? path = null)
        {
            path ??= DefaultFilePath;
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                string json = File.ReadAllText(path);
                var dto = JsonSerializer.Deserialize<PersistedDto>(json);
                if (dto == null || dto.GameAngles == null || dto.GameAngles.Length != PointCount)
                {
                    return false;
                }

                var newAngles = new float[PointCount];
                Array.Copy(dto.GameAngles, newAngles, PointCount);
                float newFullSpin = dto.FullSpinGameUnits > 0f
                    ? dto.FullSpinGameUnits
                    : (newAngles[PointCount - 1] - newAngles[0]);

                lock (_lock)
                {
                    _gameAngles = newAngles;
                    _fullSpinGameUnits = newFullSpin;
                    IsOverridden = true;
                }
                RaiseChanged();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static void RaiseChanged() => Changed?.Invoke(null, EventArgs.Empty);

        /// <summary>Plain-data snapshot of one calibration point. Bearing in degrees, game-angle raw.</summary>
        public readonly struct CalibrationPoint
        {
            public float BearingDeg { get; }
            public float GameAngle { get; }

            public CalibrationPoint(float bearingDeg, float gameAngle)
            {
                BearingDeg = bearingDeg;
                GameAngle = gameAngle;
            }
        }

        private sealed class PersistedDto
        {
            public float[] GameAngles { get; set; } = Array.Empty<float>();
            public float FullSpinGameUnits { get; set; }
        }
    }
}
