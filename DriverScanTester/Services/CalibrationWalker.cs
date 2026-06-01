// <copyright file="CalibrationWalker.cs" company="DriverScanTester">
//     Copyright (c) DriverScanTester. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DriverScanTester.Services;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Result of one auto-measurement pass (A → forward for a fixed time).
    /// </summary>
    public sealed class CalibrationSample
    {
        /// <summary>True if the measurement completed cleanly (bot actually moved).</summary>
        public bool Success { get; init; }

        /// <summary>What we asked for: requested bearing in degrees (0=N, 90=E, ...).</summary>
        public float RequestedBearingDeg { get; init; }

        /// <summary>What actually happened: bearing computed from start→end world position.
        /// This is the **ground truth** that does not depend on the bot's bearing math.</summary>
        public float ActualBearingDeg { get; init; }

        /// <summary>Average game angle observed while the bot was moving (in 0x1aa units).</summary>
        public float AvgGameAngle { get; init; }

        /// <summary>Number of in-game samples averaged.</summary>
        public int SampleCount { get; init; }

        /// <summary>World distance the bot actually covered during the measurement.</summary>
        public float DistanceTravelled { get; init; }

        /// <summary>Free-form note (e.g. "timeout: bot stuck after 0.4 units").</summary>
        public string Note { get; init; } = "";
    }

    /// <summary>
    /// Drives the character forward for a fixed time window while the bot is
    /// "facing" a chosen bearing, and collects (bearing, game_angle) samples
    /// along the way. The resulting samples are used to fit a fresh bearing
    /// calibration.
    ///
    /// Crucial property: the bearing we record for each sample is computed
    /// from the actual world-position delta (start vs end), NOT from the
    /// bearing we asked the bot to face. This means the measurement is
    /// correct even if the existing calibration is wildly off — the bot will
    /// face the wrong way, walk in the wrong direction, and we will record
    /// "the bot walked at bearing X° while the camera was at game_angle Y"
    /// which is a valid calibration point.
    /// </summary>
    public sealed class CalibrationWalker
    {
        private readonly GameMemoryService _memory;
        private readonly Action<string> _log;

        public CalibrationWalker(GameMemoryService memory, Action<string> log)
        {
            _memory = memory;
            _log = log;
        }

        /// <summary>
        /// Make the bot face <paramref name="requestedBearingDeg"/> (using whatever
        /// calibration is currently active — even a bad one), hold W for
        /// <paramref name="walkDurationMs"/>, sample (position, game_angle)
        /// throughout, and return one <see cref="CalibrationSample"/>.
        ///
        /// Pre-conditions: the bot is standing still on flat ground and the
        /// game window is focused (W must reach the game).
        /// </summary>
        public async Task<CalibrationSample> MeasureAsync(
            float requestedBearingDeg,
            int walkDurationMs = 1500,
            int sampleIntervalMs = 50,
            CancellationToken token = default)
        {
            // 1. Make sure W is released (in case caller left it down).
            GameInput.keybd_event(GameInput.VK_W, GameInput.SCAN_W, (uint)GameInput.KEYEVENTF_KEYUP, 0);
            await Task.Delay(100, token);

            // 2. Face the requested bearing.
            short gameAngle = GeometryUtils.ConvertBearingToGameAngle(
                requestedBearingDeg, lastSetGameAngle: 0, hasLastGameAngle: false);
            _memory.SetCameraAngle(gameAngle);
            await Task.Delay(150, token); // let the camera settle

            // 3. Snapshot the start position.
            var (sx, sy, startOk) = _memory.GetPlayerPosition();
            if (!startOk)
            {
                return new CalibrationSample
                {
                    Success = false,
                    RequestedBearingDeg = requestedBearingDeg,
                    Note = "Could not read start position."
                };
            }

            // 4. Press W (key down, no keyup).
            GameInput.keybd_event(GameInput.VK_W, GameInput.SCAN_W, 0, 0);

            // 5. Sample position + game_angle for walkDurationMs.
            var angles = new List<short>(walkDurationMs / sampleIntervalMs + 4);
            int elapsed = 0;
            try
            {
                while (elapsed < walkDurationMs)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }
                    var (x, y, ok) = _memory.GetPlayerPosition();
                    if (ok)
                    {
                        angles.Add(_memory.GetCameraAngle());
                    }
                    await Task.Delay(sampleIntervalMs, token);
                    elapsed += sampleIntervalMs;
                }
            }
            finally
            {
                // 6. Always release W.
                GameInput.keybd_event(GameInput.VK_W, GameInput.SCAN_W, (uint)GameInput.KEYEVENTF_KEYUP, 0);
            }

            // 7. Snapshot end position.
            var (ex, ey, endOk) = _memory.GetPlayerPosition();
            if (!endOk || angles.Count == 0)
            {
                return new CalibrationSample
                {
                    Success = false,
                    RequestedBearingDeg = requestedBearingDeg,
                    SampleCount = angles.Count,
                    Note = "Could not read end position or got no samples."
                };
            }

            // 8. Compute actual bearing from start→end (this is the ground truth).
            float dx = ex - sx;
            float dy = ey - sy;
            float distance = (float)Math.Sqrt(dx * dx + dy * dy);
            float actualBearingDeg = GeometryUtils.NormalizeBearingDeg(
                GeometryUtils.RadToDeg((float)Math.Atan2(dx, dy)));

            // 9. Average the angle samples. Drop the first and last 10% to
            //    reduce noise from the start/stop transitions.
            int trim = Math.Max(1, angles.Count / 10);
            long sum = 0;
            int count = 0;
            for (int i = trim; i < angles.Count - trim; i++)
            {
                sum += angles[i];
                count++;
            }
            float avgGameAngle = count > 0 ? (float)sum / count : angles[angles.Count / 2];

            bool success = distance >= 1.0f && count >= 5;
            string note = success
                ? ""
                : (distance < 1.0f
                    ? $"bot did not move (Δdist={distance:F2}); perhaps stuck on obstacle or W did not reach the game"
                    : "too few samples");

            return new CalibrationSample
            {
                Success = success,
                RequestedBearingDeg = requestedBearingDeg,
                ActualBearingDeg = actualBearingDeg,
                AvgGameAngle = avgGameAngle,
                SampleCount = count,
                DistanceTravelled = distance,
                Note = note,
            };
        }
    }
}
