using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Runs a MovementSystem on a given set of waypoints.
    /// Abstracts away the MovementSystem lifecycle so both the old MainViewModel
    /// and the new BotWorkflowCoordinator can use it.
    /// </summary>
    public class PathRunnerService
    {
        private readonly GameMemoryService _memoryService;
        private readonly Action<string> _log;
        private MovementSystem? _movementSystem;

        public MovementSystem? CurrentMovement => _movementSystem;

        public PathRunnerService(GameMemoryService memoryService, Action<string> log)
        {
            _memoryService = memoryService;
            _log = log;
        }

        /// <summary>
        /// Runs a set of waypoints until the path is completed or cancelled.
        /// For looped paths (exp loop), it runs indefinitely until cancellation.
        /// </summary>
        /// <param name="waypoints">The list of waypoints to follow.</param>
        /// <param name="loop">If true, the path loops continuously (for exp hunting).</param>
        /// <param name="token">Cancellation token to stop execution.</param>
        /// <returns>True if the path completed normally (non-loop); false if cancelled.</returns>
        public async Task<bool> RunPathAsync(
            List<Waypoint> waypoints,
            bool loop,
            CancellationToken token)
        {
            if (waypoints == null || waypoints.Count == 0)
            {
                _log("[PathRunner] No waypoints provided.");
                return false;
            }

            // Determine initial mode from first waypoint
            var initialMode = waypoints[0].Mode;

            _movementSystem = new MovementSystem(
                _memoryService,
                _log,
                targetX: waypoints[^1].X,
                targetY: waypoints[^1].Y,
                precision: MovementPrecision.Medium,
                customPath: waypoints,
                initialMode: initialMode,
                loopPath: loop)
            {
                InternalRepotEnabled = false // External coordinator handles repot
            };

            _log($"[PathRunner] Started path with {waypoints.Count} points, loop={loop}.");

            try
            {
                _log("[PathRunner] Entering main loop.");
                while (!token.IsCancellationRequested)
                {
                    await _movementSystem.Update(token);

                    // Check for terminal stop AFTER update so standby mode keeps running.
                    if (_movementSystem.IsGoalReached)
                    {
                        _log("[PathRunner] Path completed (goal reached terminal).");
                        return true;
                    }

                    await Task.Delay(BotConstants.Delays.PathRunnerTickMs, token);
                }
                _log("[PathRunner] Loop exited due to cancellation request.");
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
            }
            catch (Exception ex)
            {
                _log($"[PathRunner] Error: {ex.Message}");
            }
            finally
            {
                // Persist any pending stuck-cell data before stopping
                _movementSystem?.SaveLocalMap();
                _movementSystem?.StopMoving();
                _log("[PathRunner] Path stopped.");
            }

            return false;
        }

        /// <summary>
        /// Stops the current movement immediately and persists any pending navigation data.
        /// </summary>
        public void Stop()
        {
            _movementSystem?.SaveLocalMap();
            _movementSystem?.StopMoving();
        }
    }
}
