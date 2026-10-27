using System.Collections.Generic;

namespace DriverScanTester.Models
{
    /// <summary>
    /// Determines how a travel route (a leg of the Repot → EXP journey) is considered complete.
    /// </summary>
    public enum TravelRouteCompletionMode
    {
        /// <summary>
        /// Execute the path normally and complete when the final waypoint is reached
        /// (MovementSystem.IsGoalReached). ExpectedDestinationMapNumber must be 0.
        /// </summary>
        FinalWaypoint = 0,

        /// <summary>
        /// Follow the path, but complete as soon as the configured nonzero destination map
        /// is stably detected — the final waypoint does not need to be reached. Used for
        /// portal routes that teleport the player to another map mid-path.
        /// </summary>
        ExpectedMapReached = 1
    }

    /// <summary>
    /// The kind of work a single flow step performs. A profile's flow is a linear,
    /// ordered list of these steps that the bot cycles through; any step type can be
    /// placed anywhere in the flow, so repot / paths / custom operations / hunting can
    /// be freely mixed.
    /// </summary>
    public enum BotFlowStepType
    {
        /// <summary>Walk a saved path segment once (non-looping).</summary>
        Path = 0,

        /// <summary>
        /// Perform the full repot routine: teleport to the city if needed, walk to the
        /// repot point (using the next path from <see cref="BotFlowStep.RepotPaths"/> on
        /// each repot), then sell items and buy potions.
        /// </summary>
        Repot = 1,

        /// <summary>Run a named built-in custom operation (see BotOperations).</summary>
        Operation = 2,

        /// <summary>
        /// Run the looping hunting path until the repot conditions are met (or the player
        /// is detected in the city). When this step ends the flow simply advances to the
        /// next step (wrapping around at the end); the Repot step returns the bot to the
        /// city and refills.
        /// </summary>
        ExpLoop = 3
    }

    /// <summary>
    /// One step of a profile's linear flow. Which fields are used depends on
    /// <see cref="Type"/>:
    ///   Path      → Routes (route group, cycled) or single PathFile, StartDelayMs,
    ///               CompletionMode, ExpectedDestinationMapNumber
    ///   Repot     → RepotPaths (cycled on each repot)
    ///   Operation → OperationName
    ///   ExpLoop   → Routes (route group, cycled) or single PathFile, StartDelayMs
    /// </summary>
    public sealed class BotFlowStep
    {
        /// <summary>The kind of work this step performs.</summary>
        public BotFlowStepType Type { get; set; } = BotFlowStepType.Path;

        /// <summary>
        /// Optional map number the player must be standing on for this action to be chosen by
        /// the map-based startup detection (see <see cref="Services.BotStartupActionSelector"/>).
        /// The bot searches the flow in its configured order on an explicit start and uses the
        /// FIRST action whose starting map matches the observed player map.
        ///
        /// For Path / ExpLoop / Repot steps this value is the DEFAULT for the step's routes:
        /// a route with its own <see cref="BotRouteStep.StartMapNumber"/> wins, a route without
        /// one inherits this value. For an Operation step it makes the operation eligible for
        /// the startup detection; an operation with no starting map configured is never
        /// selected by name/location resemblance.
        ///
        /// 0 means unspecified (no map match). It is deliberately NOT the same as
        /// <see cref="BotRouteStep.ProtectionMapNumber"/> (start-protection/route-validation
        /// map) nor as <see cref="ExpectedDestinationMapNumber"/> (the map reached AFTER a
        /// travel route).
        /// </summary>
        public int StartMapNumber { get; set; } = 0;

        // ── Path / ExpLoop ──
        /// <summary>
        /// Filename of the saved segment in SavedPaths (with or without .json).
        /// Used as the single route when <see cref="Routes"/> is empty.
        /// </summary>
        public string PathFile { get; set; } = "";

        /// <summary>
        /// Delay in milliseconds to wait before this step starts. Zero means no wait.
        /// Used as the single-route delay when <see cref="Routes"/> is empty.
        /// </summary>
        public int StartDelayMs { get; set; }

        // ── Path / ExpLoop route group ──
        /// <summary>
        /// The pool of alternative routes for this step. Each time the flow executes this
        /// step, the NEXT route in the list is used (wrapping around), so the step's route
        /// group rotates one route per flow cycle: e.g. repot1 → repot2 → repot3 → repot1 ...
        /// Together with other grouped steps (e.g. an ExpLoop group exp1/exp2/exp3) the
        /// rotation stays in lockstep, so cycle 1 uses repot1 + exp1, cycle 2 uses
        /// repot2 + exp2, and so on. When empty, the step falls back to the single route
        /// from <see cref="PathFile"/> and <see cref="StartDelayMs"/>.
        /// </summary>
        public List<BotRouteStep> Routes { get; set; } = new();

        // ── Path only ──
        /// <summary>How a Path step completes: final waypoint or expected destination map.</summary>
        public TravelRouteCompletionMode CompletionMode { get; set; }
            = TravelRouteCompletionMode.FinalWaypoint;

        /// <summary>Destination map number for <see cref="TravelRouteCompletionMode.ExpectedMapReached"/>.</summary>
        public int ExpectedDestinationMapNumber { get; set; }

        // ── Operation only ──
        /// <summary>Name of the built-in custom operation to run (see BotOperations).</summary>
        public string OperationName { get; set; } = "";

        // ── Repot only ──
        /// <summary>
        /// The pool of paths to the repot location. On every repot trip the bot uses the
        /// next path in this list, wrapping around, so the repot route is rotated.
        /// </summary>
        public List<BotRouteStep> RepotPaths { get; set; } = new();
    }
}