using System.Collections.Generic;

namespace DriverScanTester.Models
{
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
    ///   Path    → PathFile, StartDelayMs, CompletionMode, ExpectedDestinationMapNumber
    ///   Repot   → RepotPaths (cycled on each repot)
    ///   Operation → OperationName
    ///   ExpLoop → PathFile, StartDelayMs
    /// </summary>
    public sealed class BotFlowStep
    {
        /// <summary>The kind of work this step performs.</summary>
        public BotFlowStepType Type { get; set; } = BotFlowStepType.Path;

        // ── Path / ExpLoop ──
        /// <summary>Filename of the saved segment in SavedPaths (with or without .json).</summary>
        public string PathFile { get; set; } = "";

        /// <summary>Delay in milliseconds to wait before this step starts. Zero means no wait.</summary>
        public int StartDelayMs { get; set; }

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