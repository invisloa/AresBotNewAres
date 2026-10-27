using System;
using System.Collections.Generic;
using DriverScanTester.Models;

namespace DriverScanTester.Services
{
    /// <summary>Outcome of one map-based startup action selection attempt.</summary>
    public enum BotStartupSelectionStatus
    {
        /// <summary>An action (and route) matching the observed map was selected.</summary>
        Selected,

        /// <summary>The profile declares starting maps but no action matches the observed map.</summary>
        NoMatchingAction,

        /// <summary>The profile declares no starting map at all (map-based startup impossible).</summary>
        NoStartupMapConfigured,

        /// <summary>The input is unusable (no profile/flow or an invalid observed map).</summary>
        InvalidInput
    }

    /// <summary>
    /// Result of the map-based startup action selection: which flow step (and, for grouped
    /// Path / ExpLoop / Repot steps, which route of that step) must be executed first, plus
    /// the reason for the decision so it can be logged verbatim.
    /// </summary>
    public sealed class BotStartupSelection
    {
        public BotStartupSelection(
            BotStartupSelectionStatus status,
            int flowIndex,
            int routeIndex,
            int startMapNumber,
            BotFlowStep? step,
            string reason)
        {
            Status = status;
            FlowIndex = flowIndex;
            RouteIndex = routeIndex;
            StartMapNumber = startMapNumber;
            Step = step;
            Reason = reason;
        }

        /// <summary>Outcome of the selection.</summary>
        public BotStartupSelectionStatus Status { get; }

        /// <summary>Zero-based index of the selected flow step, or -1 when nothing was selected.</summary>
        public int FlowIndex { get; }

        /// <summary>
        /// Zero-based route index inside the selected step's route pool, or -1 when the
        /// selected step has no route (Operation step / nothing selected).
        /// </summary>
        public int RouteIndex { get; }

        /// <summary>The starting map number that produced the match (0 when nothing was selected).</summary>
        public int StartMapNumber { get; }

        /// <summary>The selected flow step (null when nothing was selected).</summary>
        public BotFlowStep? Step { get; }

        /// <summary>Human-readable reason for the decision (logged verbatim).</summary>
        public string Reason { get; }

        /// <summary>True when an action was selected.</summary>
        public bool HasSelection => Status == BotStartupSelectionStatus.Selected;
    }

    /// <summary>
    /// Map-based startup action selection. On an explicit workflow start the player's
    /// current map is compared against the profile's configured starting maps:
    ///
    ///   1. Flow steps are inspected in their ORIGINAL configured order.
    ///   2. For every action the candidate routes are inspected in their declared order:
    ///        • Repot            → RepotPaths (alternative repot routes)
    ///        • Path  / ExpLoop  → Routes, or the single PathFile fallback
    ///        • Operation        → only when the step declares its own starting map
    ///   3. The FIRST action that contains a route whose starting map equals the observed
    ///      map wins; inside that action the FIRST such route wins. A later action that
    ///      happens to be physically closer never overrides an earlier matching action —
    ///      proximity is only used later, by the existing MovementSystem route resync.
    ///
    /// Route-level <see cref="BotRouteStep.StartMapNumber"/> takes precedence; when a route
    /// does not declare one, the step-level <see cref="BotFlowStep.StartMapNumber"/> is the
    /// default. 0 means "unspecified" and never matches a map.
    ///
    /// The selector is pure: it neither reads the game nor changes any workflow state.
    /// </summary>
    public static class BotStartupActionSelector
    {
        /// <summary>
        /// True when at least one flow step / route declares a starting map number. A profile
        /// without any starting-map metadata cannot be started by map and is rejected explicitly;
        /// starting maps are never invented.
        /// </summary>
        public static bool HasConfiguredStartupMaps(BotProfile? profile)
        {
            var steps = profile?.FlowSteps;
            if (steps == null)
                return false;

            foreach (var step in steps)
            {
                if (step == null)
                    continue;

                if (step.StartMapNumber > 0)
                    return true;

                var routes = GetCandidateRoutes(step);
                if (routes == null)
                    continue;

                foreach (var route in routes)
                {
                    if (route != null && route.StartMapNumber > 0)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Selects the first flow action (and the first route inside it) whose starting map
        /// equals <paramref name="mapNumber"/>.
        /// </summary>
        public static BotStartupSelection Select(BotProfile? profile, int mapNumber)
        {
            if (mapNumber <= 0)
            {
                return new BotStartupSelection(
                    BotStartupSelectionStatus.InvalidInput,
                    -1, -1, 0, null,
                    $"Observed map {mapNumber} is not a valid map number (the map read must be a positive number).");
            }

            var steps = profile?.FlowSteps;
            if (steps == null || steps.Count == 0)
            {
                return new BotStartupSelection(
                    BotStartupSelectionStatus.InvalidInput,
                    -1, -1, 0, null,
                    "The profile has no flow steps, so no starting action can be selected.");
            }

            if (!HasConfiguredStartupMaps(profile))
            {
                return new BotStartupSelection(
                    BotStartupSelectionStatus.NoStartupMapConfigured,
                    -1, -1, 0, null,
                    "No flow step or route declares a starting map number.");
            }

            for (int stepIndex = 0; stepIndex < steps.Count; stepIndex++)
            {
                BotFlowStep? step = steps[stepIndex];
                if (step == null)
                    continue;

                if (step.Type == BotFlowStepType.Operation)
                {
                    // An operation is only eligible when it explicitly declares the map it is
                    // meant to run on; the operation NAME is never used as a location hint.
                    if (step.StartMapNumber != mapNumber)
                        continue;
                    if (string.IsNullOrWhiteSpace(step.OperationName))
                        continue;
                    if (!BotOperations.IsKnown(step.OperationName))
                        continue;

                    return new BotStartupSelection(
                        BotStartupSelectionStatus.Selected,
                        stepIndex,
                        -1,
                        mapNumber,
                        step,
                        $"Flow step {stepIndex + 1} (Operation '{step.OperationName}') declares starting map {mapNumber}.");
                }

                var routes = GetCandidateRoutes(step);
                if (routes == null || routes.Count == 0)
                    continue;

                for (int routeIndex = 0; routeIndex < routes.Count; routeIndex++)
                {
                    BotRouteStep? route = routes[routeIndex];
                    if (route == null || string.IsNullOrWhiteSpace(route.PathFile))
                        continue;

                    int routeStartMap = ResolveStartMap(route.StartMapNumber, step.StartMapNumber);
                    if (routeStartMap != mapNumber)
                        continue;

                    string kind = step.Type switch
                    {
                        BotFlowStepType.Repot => "repot path",
                        BotFlowStepType.ExpLoop => "ExpLoop route",
                        _ => "route"
                    };

                    return new BotStartupSelection(
                        BotStartupSelectionStatus.Selected,
                        stepIndex,
                        routeIndex,
                        routeStartMap,
                        step,
                        $"Flow step {stepIndex + 1} ({step.Type}) {kind} {routeIndex + 1} '{route.PathFile}' starts on map {routeStartMap}.");
                }
            }

            return new BotStartupSelection(
                BotStartupSelectionStatus.NoMatchingAction,
                -1, -1, 0, null,
                $"No configured action starts on map {mapNumber}. Configured starting maps: {DescribeConfiguredMaps(steps)}.");
        }

        /// <summary>
        /// Effective starting map of a route: the route's own value wins, otherwise the
        /// owning step's value is the default. Negative/garbage values are treated as
        /// unspecified (0).
        /// </summary>
        public static int ResolveStartMap(int routeStartMapNumber, int stepStartMapNumber)
        {
            if (routeStartMapNumber > 0)
                return routeStartMapNumber;
            return stepStartMapNumber > 0 ? stepStartMapNumber : 0;
        }

        /// <summary>
        /// Route candidates of one flow step in their declared order: RepotPaths for a Repot
        /// step, Routes for a Path / ExpLoop step, and the single PathFile fallback (with the
        /// step's starting map preserved) when a Path / ExpLoop step has no route group.
        /// Returns null for step types without routes.
        /// </summary>
        public static List<BotRouteStep>? GetCandidateRoutes(BotFlowStep step)
        {
            if (step == null)
                return null;

            return step.Type switch
            {
                BotFlowStepType.Repot => step.RepotPaths,
                BotFlowStepType.Path or BotFlowStepType.ExpLoop => GetRoutePool(step),
                _ => null
            };
        }

        /// <summary>
        /// Effective route pool of a Path / ExpLoop step: the configured route group, or the
        /// single route built from <see cref="BotFlowStep.PathFile"/> /
        /// <see cref="BotFlowStep.StartDelayMs"/> / <see cref="BotFlowStep.StartMapNumber"/>.
        /// Returns null when no route is configured.
        /// </summary>
        public static List<BotRouteStep>? GetRoutePool(BotFlowStep step)
        {
            if (step == null)
                return null;

            if (step.Routes != null && step.Routes.Count > 0)
                return step.Routes;

            if (string.IsNullOrWhiteSpace(step.PathFile))
                return null;

            return new List<BotRouteStep>
            {
                new BotRouteStep
                {
                    PathFile = step.PathFile,
                    StartDelayMs = step.StartDelayMs,
                    // The single-route fallback must not silently lose the step's
                    // starting-map metadata.
                    StartMapNumber = step.StartMapNumber
                }
            };
        }

        /// <summary>Comma-separated list of the configured starting maps, for diagnostics.</summary>
        private static string DescribeConfiguredMaps(List<BotFlowStep> steps)
        {
            var maps = new SortedSet<int>();
            foreach (var step in steps)
            {
                if (step == null)
                    continue;

                if (step.StartMapNumber > 0)
                    maps.Add(step.StartMapNumber);

                var routes = GetCandidateRoutes(step);
                if (routes == null)
                    continue;

                foreach (var route in routes)
                {
                    if (route != null && route.StartMapNumber > 0)
                        maps.Add(route.StartMapNumber);
                }
            }

            return maps.Count == 0
                ? "none"
                : string.Join(", ", maps);
        }
    }
}
