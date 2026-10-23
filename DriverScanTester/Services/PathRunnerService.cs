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
        ReportAndGoBack,

        /// <summary>
        /// A confirmed unexpected map change invalidated the route's map invariant.
        /// The route is permanently dead for this execution; the coordinator must
        /// synchronize the post-transition position before any new action.
        /// </summary>
        MapTransition,
        Error
    }

    /// <summary>
    /// Runs a MovementSystem on a given set of waypoints.
    /// The configured steering mode is the default for waypoints without their own
    /// per-point override (KeyboardTurn/WAD by default); a waypoint's
    /// <see cref="Waypoint.SteeringMode"/> wins when set.
    /// Abstracts away the MovementSystem lifecycle so both the old MainViewModel
    /// and the new BotWorkflowCoordinator can use it.
    /// </summary>
    public class PathRunnerService
    {
        private readonly GameMemoryService _memoryService;
        private readonly Action<string> _log;
        private readonly bool _enableWaypointSpecialRecoveries;
        private readonly MovementSteeringMode _steeringMode;
        private MovementSystem? _movementSystem;

        /// <summary>
        /// Shared pause switch. When set, the path loop suspends in place (inputs
        /// released) without discarding the MovementSystem, so resume continues with
        /// the same waypoint queue. Null = never paused.
        /// </summary>
        public BotPauseController? PauseController { get; set; }

        public MovementSystem? CurrentMovement => _movementSystem;
        public WKeyCameraController? WCameraController { get; }
        public WaypointRecoveryExecutor? WaypointRecoveryExecutor { get; set; }
        public Action<string>? OnGameUnresponsiveDetected { get; set; }
        public PathRunStopReason LastStopReason { get; private set; } = PathRunStopReason.None;

        public PathRunnerService(
            GameMemoryService memoryService,
            Action<string> log,
            bool enableWaypointSpecialRecoveries = true,
            MovementSteeringMode steeringMode = MovementSteeringMode.KeyboardTurn,
            WKeyCameraController? wKeyCameraController = null)
        {
            _memoryService = memoryService;
            _log = log;
            _enableWaypointSpecialRecoveries = enableWaypointSpecialRecoveries;
            _steeringMode = steeringMode;
            WCameraController = wKeyCameraController;
        }

        /// <summary>
        /// Runs a set of waypoints until the path is completed or cancelled.
        /// For looped paths (exp loop), it runs indefinitely until cancellation.
        /// </summary>
        /// <param name="waypoints">The list of waypoints to follow.</param>
        /// <param name="loop">If true, the path loops continuously (for exp hunting).</param>
        /// <param name="token">Cancellation token to stop execution.</param>
        /// <param name="expectedMap">Route map invariant (route ProtectionMapNumber).
        /// 0 = no restriction (multi-map travel compatibility).</param>
        /// <param name="routeLabel">Optional route name used in [ExpMapGuard] diagnostics.</param>
        /// <returns>True if the path completed normally (non-loop); false if cancelled.</returns>
        public async Task<bool> RunPathAsync(
            List<Waypoint> waypoints,
            bool loop,
            CancellationToken token,
            int expectedMap = 0,
            string routeLabel = "")
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

            MovementSystem movementSystem = new MovementSystem(
                _memoryService,
                _log,
                targetX: waypoints[^1].X,
                targetY: waypoints[^1].Y,
                precision: MovementPrecision.Medium,
                customPath: waypoints,
                initialMode: initialMode,
                loopPath: loop,
                enableWaypointSpecialRecoveries: _enableWaypointSpecialRecoveries,
                steeringMode: _steeringMode,
                wKeyCameraController: WCameraController)
            {
                InternalRepotEnabled = false, // External coordinator handles repot
                WaypointRecoveryExecutor = WaypointRecoveryExecutor,
                MobGroupingPausePredicate = () => PauseController?.IsPaused == true,
                OnGameUnresponsiveDetected = reason => OnGameUnresponsiveDetected?.Invoke(reason),
                // Route map invariant: a route with an expected map must never keep
                // navigating old-map waypoints after the player leaves that map.
                ExpectedMap = expectedMap,
                RouteLabel = routeLabel
            };
            _movementSystem = movementSystem;

            _log($"[PathRunner] Started path with {waypoints.Count} points, loop={loop}.");

            try
            {
                _log("[PathRunner] Entering main loop.");
                bool pauseLogged = false;
                while (!token.IsCancellationRequested)
                {
                    // Pause checkpoint: hold the route in place. MovementSystem keeps its
                    // waypoint queue, so resume continues with the same waypoint.
                    if (PauseController?.IsPaused == true)
                    {
                        if (!pauseLogged)
                        {
                            _log("[PathRunner] Paused — holding route position.");
                            pauseLogged = true;
                        }
                        movementSystem?.CancelMobGrouping("bot paused");
                        movementSystem?.StopMoving();
                        movementSystem?.ReleaseCombatKeys();
                        await PauseController.WaitIfPausedAsync(token);
                        if (token.IsCancellationRequested) break;
                        _log("[PathRunner] Resumed — continuing route.");
                        pauseLogged = false;
                        continue;
                    }

                    await movementSystem.Update(token);

                    // A confirmed unexpected map change permanently invalidates the route.
                    // Abort before any recovery/combat/loot logic can act on old-map
                    // waypoints; the coordinator synchronizes the new position and runs
                    // the existing recovery workflow.
                    if (movementSystem.IsMapRouteInvalidated)
                    {
                        LastStopReason = PathRunStopReason.MapTransition;
                        _log($"[PathRunner] Route '{routeLabel}' invalidated — unexpected map " +
                             $"change confirmed (expected={movementSystem.ExpectedMap}, " +
                             $"observed={movementSystem.ConfirmedTransitionMap}); aborting path.");
                        return false;
                    }

                    // ReportAndGoBack teleport: the old route is dead. Abort immediately
                    // (no 10s ZoneBlock wait, no city-poll delay) so the coordinator can
                    // discard the stale Exp queue and start Repot from its first command.
                    if (movementSystem.IsReportAndGoBackRequested)
                    {
                        LastStopReason = PathRunStopReason.ReportAndGoBack;
                        _log("[PathRunner] ReportAndGoBack teleport requested — aborting path (coordinator will start Repot from scratch).");
                        return false;
                    }

                    if (movementSystem.IsWaypointRepotRequested)
                    {
                        LastStopReason = PathRunStopReason.WaypointRepotRequested;
                        _log("[WaypointRecovery] Workflow route stopped for waypoint Repot request.");
                        return false;
                    }

                    // Check for terminal stop AFTER update so standby mode keeps running.
                    if (movementSystem.IsGoalReached)
                    {
                        _log("[PathRunner] Path completed (goal reached terminal).");
                        LastStopReason = PathRunStopReason.Completed;
                        return true;
                    }

                    // Non-loop routes in workflow mode never set the internal goal flag
                    // (InternalRepotEnabled=false); entering final-waypoint standby IS the
                    // completion signal for them. Loop paths never keep standby active
                    // (they re-enqueue), so this only fires for non-loop routes.
                    if (!loop && movementSystem.IsFinalStandbyActive)
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
                    if (movementSystem.IsZoneBlocked &&
                        movementSystem.ZoneBlockedDuration.TotalMilliseconds >= BotConstants.Delays.ZoneBlockAbortMs)
                    {
                        LastStopReason = PathRunStopReason.ZoneBlocked;
                        _log($"[PathRunner] Zone-blocked for {movementSystem.ZoneBlockedDuration.TotalSeconds:F0}s — aborting path (player likely in the wrong zone).");
                        return false;
                    }

                    // City-stuck watchdog: in workflow mode the in-city stuck escalation
                    // signals a route that cannot complete. Abort so the coordinator retries
                    // from the city instead of idling for 10 minutes.
                    if (movementSystem.IsCityStuckFatal)
                    {
                        _log("[PathRunner] City-stuck escalation fired — aborting path (coordinator will retry from the city).");
                        LastStopReason = PathRunStopReason.CityStuck;
                        return false;
                    }

                    await (PauseController != null
                        ? PauseController.PausableDelayAsync(BotConstants.Delays.PathRunnerTickMs, token)
                        : Task.Delay(BotConstants.Delays.PathRunnerTickMs, token));
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
                movementSystem?.SaveLocalMap();
                movementSystem?.CancelMobGrouping("path runner stopped");
                movementSystem?.CancelWaypointSpecialRecovery();
                // Centralized input cleanup for every termination path (normal
                // completion, cancellation, exception, phase change): StopMoving
                // releases W/A/D, ReleaseCombatKeys releases the combat-owned
                // attack key (3) via the existing ownership flag. Both are
                // idempotent, so an EXP → Repot interruption while 3 is held
                // can never leak it into the Repot phase. The "[Key] 3 up"
                // release log therefore always precedes "[PathRunner] Path stopped."
                movementSystem?.ReleaseCombatKeys();
                movementSystem?.ReleaseCameraOwnership();
                movementSystem?.DisposeMobGroupingDetector();
                _log($"[PathRunner] final stop reason={LastStopReason}.");
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
            _movementSystem?.CancelMobGrouping("path runner stop requested");
            _movementSystem?.CancelWaypointSpecialRecovery();
            _movementSystem?.ReleaseCombatKeys();
            _movementSystem?.ReleaseCameraOwnership();
        }
    }
}
