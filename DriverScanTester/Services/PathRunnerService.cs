using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DriverScanTester.Services
{
    public enum PathRunStopReason
    {
        None,
        Completed,
        Cancelled,
        ZoneBlocked,
        CityStuck,
        WaypointRepotRequested,
        Error
    }

    /// <summary>
    /// Runs a MovementSystem on a given set of waypoints.
    /// Abstracts away the MovementSystem lifecycle so both the old MainViewModel
    /// and the new BotWorkflowCoordinator can use it.
    /// </summary>
    public class PathRunnerService
    {
        private readonly GameMemoryService _memoryService;
        private readonly Action<string> _log;
        private readonly bool _enableWaypointSpecialRecoveries;
        private MovementSystem? _movementSystem;

        public MovementSystem? CurrentMovement => _movementSystem;
        public WaypointRecoveryExecutor? WaypointRecoveryExecutor { get; set; }
        public PathRunStopReason LastStopReason { get; private set; } = PathRunStopReason.None;

        public PathRunnerService(
            GameMemoryService memoryService,
            Action<string> log,
            bool enableWaypointSpecialRecoveries = true)
        {
            _memoryService = memoryService;
            _log = log;
            _enableWaypointSpecialRecoveries = enableWaypointSpecialRecoveries;
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
            LastStopReason = PathRunStopReason.None;
            if (waypoints == null || waypoints.Count == 0)
            {
                _log("[PathRunner] No waypoints provided.");
                LastStopReason = PathRunStopReason.Error;
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
                loopPath: loop,
                enableWaypointSpecialRecoveries: _enableWaypointSpecialRecoveries)
            {
                InternalRepotEnabled = false, // External coordinator handles repot
                WaypointRecoveryExecutor = WaypointRecoveryExecutor
            };

            _log($"[PathRunner] Started path with {waypoints.Count} points, loop={loop}.");

            try
            {
                _log("[PathRunner] Entering main loop.");
                while (!token.IsCancellationRequested)
                {
                    await _movementSystem.Update(token);

                    if (_movementSystem.IsWaypointRepotRequested)
                    {
                        LastStopReason = PathRunStopReason.WaypointRepotRequested;
                        _log("[WaypointRecovery] Workflow route stopped for waypoint Repot request.");
                        return false;
                    }

                    // Check for terminal stop AFTER update so standby mode keeps running.
                    if (_movementSystem.IsGoalReached)
                    {
                        _log("[PathRunner] Path completed (goal reached terminal).");
                        LastStopReason = PathRunStopReason.Completed;
                        return true;
                    }

                    // Non-loop routes in workflow mode never set the internal goal flag
                    // (InternalRepotEnabled=false); entering final-waypoint standby IS the
                    // completion signal for them. Loop paths never keep standby active
                    // (they re-enqueue), so this only fires for non-loop routes.
                    if (!loop && _movementSystem.IsFinalStandbyActive)
                    {
                        _log("[PathRunner] Path completed (final waypoint standby).");
                        LastStopReason = PathRunStopReason.Completed;
                        return true;
                    }

                    // Zone-block watchdog: when the player is in a zone the current waypoints
                    // forbid (e.g. teleported to the city while the route expects the
                    // wilderness), the movement system stops and would idle here forever.
                    // Abort after a sustained block so the coordinator can restart from the
                    // city (go repot → travel back out).
                    if (_movementSystem.IsZoneBlocked &&
                        _movementSystem.ZoneBlockedDuration.TotalMilliseconds >= BotConstants.Delays.ZoneBlockAbortMs)
                    {
                        LastStopReason = PathRunStopReason.ZoneBlocked;
                        _log($"[PathRunner] Zone-blocked for {_movementSystem.ZoneBlockedDuration.TotalSeconds:F0}s — aborting path (player likely in the wrong zone).");
                        return false;
                    }

                    // City-stuck watchdog: in workflow mode the in-city stuck escalation
                    // signals a route that cannot complete. Abort so the coordinator retries
                    // from the city instead of idling for 10 minutes.
                    if (_movementSystem.IsCityStuckFatal)
                    {
                        _log("[PathRunner] City-stuck escalation fired — aborting path (coordinator will retry from the city).");
                        LastStopReason = PathRunStopReason.CityStuck;
                        return false;
                    }

                    await Task.Delay(BotConstants.Delays.PathRunnerTickMs, token);
                }
                _log("[PathRunner] Loop exited due to cancellation request.");
                LastStopReason = PathRunStopReason.Cancelled;
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
                LastStopReason = PathRunStopReason.Cancelled;
            }
            catch (Exception ex)
            {
                _log($"[PathRunner] Error: {ex.Message}");
                LastStopReason = PathRunStopReason.Error;
            }
            finally
            {
                // Persist any pending stuck-cell data before stopping
                _movementSystem?.SaveLocalMap();
                _movementSystem?.CancelWaypointSpecialRecovery();
                // Centralized input cleanup for every termination path (normal
                // completion, cancellation, exception, phase change): StopMoving
                // releases W/A/D, ReleaseCombatKeys releases the combat-owned
                // attack key (3) via the existing ownership flag. Both are
                // idempotent, so an EXP → Repot interruption while 3 is held
                // can never leak it into the Repot phase. The "[Key] 3 up"
                // release log therefore always precedes "[PathRunner] Path stopped."
                _movementSystem?.ReleaseCombatKeys();
                _movementSystem?.StopMoving();
                _log("[PathRunner] Path stopped.");
            }

            return false;
        }

        /// <summary>
        /// Stops the current movement immediately and persists any pending navigation data.
        /// Also releases combat-owned held keys (attack skill 3) through the existing
        /// ownership mechanism, so an externally requested stop can never leave the
        /// attack key held for the next workflow phase. Idempotent.
        /// </summary>
        public void Stop()
        {
            _movementSystem?.SaveLocalMap();
            _movementSystem?.CancelWaypointSpecialRecovery();
            _movementSystem?.ReleaseCombatKeys();
            _movementSystem?.StopMoving();
        }
    }
}
