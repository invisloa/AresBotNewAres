using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Independent movement-input experiments. These are diagnostic scenarios only —
    /// none of them changes the production movement controller.
    /// </summary>
    public enum MovementDiagnosticScenario
    {
        ManualKeyboardBaseline = 0,
        KeybdEventContinuousW = 1,
        KeybdEventRepeatedW = 2,
        SendInputVirtualKey = 3,
        SendInputScanCode = 4,
        KeybdEventPulsedW = 5,
        KeybdEventKeyboardSteering = 6,
        KeyboardAndMouseFastTurning = 7,
        DirectCameraMemorySteering = 8,
        ExistingMovementSystemOnlyMove = 9
    }

    public enum MovementDiagnosticInputMode
    {
        None,
        KeybdEvent,
        SendInputVirtualKey,
        SendInputScanCode,
        ExistingMovementSystem
    }

    public enum MovementDiagnosticPhase
    {
        Idle,
        Countdown,
        Running,
        CleaningUp,
        Observing,
        Completed,
        Stopped,
        Failed
    }

    public enum MovementDiagnosticOutcome
    {
        NotRun,
        Completed,
        Stopped,
        Failed
    }

    public enum MovementDiagnosticKey
    {
        W,
        A,
        D
    }

    /// <summary>
    /// User-provided observation of what the other game client rendered. The diagnostic
    /// never invents these values.
    /// </summary>
    public enum MovementRemoteObservationRating
    {
        NotRecorded = 0,
        SmoothMovement = 1,
        AnimationStutteringOnly = 2,
        PositionJumping = 3,
        BothAnimationAndPosition = 4,
        CouldNotDetermine = 5
    }

    public sealed class MovementDiagnosticObservationChoice
    {
        public MovementDiagnosticObservationChoice(
            MovementRemoteObservationRating rating,
            string label)
        {
            Rating = rating;
            Label = label;
        }

        public MovementRemoteObservationRating Rating { get; }
        public string Label { get; }
    }

    public sealed class MovementDiagnosticScenarioInfo
    {
        public MovementDiagnosticScenarioInfo(
            MovementDiagnosticScenario scenario,
            string label,
            string inputMethodDescription,
            bool requiresAdvancedMode = false)
        {
            Scenario = scenario;
            Label = label;
            InputMethodDescription = inputMethodDescription;
            RequiresAdvancedMode = requiresAdvancedMode;
        }

        public MovementDiagnosticScenario Scenario { get; }
        public string Label { get; }
        public string InputMethodDescription { get; }
        public bool RequiresAdvancedMode { get; }
        public string DisplayText => $"TEST {(int)Scenario}: {Label}";
    }

    public static class MovementDiagnosticScenarioCatalog
    {
        private static readonly MovementDiagnosticScenarioInfo[] _all =
        {
            new(
                MovementDiagnosticScenario.ManualKeyboardBaseline,
                "Manual Keyboard — Baseline",
                "No synthetic input. Manual physical W/A/D only; memory is recorded."),
            new(
                MovementDiagnosticScenario.KeybdEventContinuousW,
                "keybd_event — Continuous W",
                "Existing GameInput.keybd_event(byte vk, byte scan). One W down, hold, one W up."),
            new(
                MovementDiagnosticScenario.KeybdEventRepeatedW,
                "keybd_event — Repeated W",
                "Existing GameInput.keybd_event W down, repeated W-down events at a configured interval, then one W up."),
            new(
                MovementDiagnosticScenario.SendInputVirtualKey,
                "SendInput — Virtual Key",
                "Win32 SendInput with a virtual-key KEYBDINPUT (no scancode flag)."),
            new(
                MovementDiagnosticScenario.SendInputScanCode,
                "SendInput — Scan Code",
                "Win32 SendInput with KEYEVENTF_SCANCODE and the project's existing scancode constants."),
            new(
                MovementDiagnosticScenario.KeybdEventPulsedW,
                "keybd_event — Pulsed W",
                "keybd_event alternating bounded W-down / W-up intervals (default 200 ms / 100 ms)."),
            new(
                MovementDiagnosticScenario.KeybdEventKeyboardSteering,
                "keybd_event — W + A/D Steering",
                "keybd_event W+A for one stage, all keys released, then W+D. No camera memory writes."),
            new(
                MovementDiagnosticScenario.KeyboardAndMouseFastTurning,
                "Keyboard + Mouse Fast Turning",
                "keybd_event W+A/D plus the existing game-client edge cursor assistance used by KeyboardTurn."),
            new(
                MovementDiagnosticScenario.DirectCameraMemorySteering,
                "DirectCamera — Memory Steering",
                "One bounded GameMemoryService.SetCameraAngle write plus readback, then keybd_event W. Advanced test.",
                requiresAdvancedMode: true),
            new(
                MovementDiagnosticScenario.ExistingMovementSystemOnlyMove,
                "Existing MovementSystem — OnlyMove",
                "The unmodified MovementSystem in OnlyMove mode, KeyboardTurn steering, one explicitly configured safe waypoint.")
        };

        public static IReadOnlyList<MovementDiagnosticScenarioInfo> All => _all;

        public static MovementDiagnosticScenarioInfo Get(MovementDiagnosticScenario scenario)
            => _all[(int)scenario];

        public static string GetDisplayText(MovementDiagnosticScenario scenario)
            => Get(scenario).DisplayText;

        public static string GetInputMethodDescription(MovementDiagnosticScenario scenario)
            => Get(scenario).InputMethodDescription;

        public static MovementDiagnosticInputMode GetPrimaryInputMode(MovementDiagnosticScenario scenario)
            => scenario switch
            {
                MovementDiagnosticScenario.ManualKeyboardBaseline => MovementDiagnosticInputMode.None,
                MovementDiagnosticScenario.SendInputVirtualKey => MovementDiagnosticInputMode.SendInputVirtualKey,
                MovementDiagnosticScenario.SendInputScanCode => MovementDiagnosticInputMode.SendInputScanCode,
                MovementDiagnosticScenario.ExistingMovementSystemOnlyMove => MovementDiagnosticInputMode.ExistingMovementSystem,
                _ => MovementDiagnosticInputMode.KeybdEvent
            };

        public static string GetObservationLabel(MovementRemoteObservationRating rating)
            => rating switch
            {
                MovementRemoteObservationRating.SmoothMovement => "Smooth movement",
                MovementRemoteObservationRating.AnimationStutteringOnly => "Stuttering animation only",
                MovementRemoteObservationRating.PositionJumping => "Position jumping / rubber-banding",
                MovementRemoteObservationRating.BothAnimationAndPosition => "Both animation and position affected",
                MovementRemoteObservationRating.CouldNotDetermine => "Could not determine",
                _ => "Not recorded"
            };
    }

    public sealed class MovementDiagnosticSettings
    {
        /// <summary>Scenario body duration in milliseconds (the manual baseline defaults to 15 s in the UI).</summary>
        public int DurationMs { get; set; } = 6000;
        public int CountdownMs { get; set; } = 3000;
        public int RepeatIntervalMs { get; set; } = 150;
        public int SampleIntervalMs { get; set; } = 100;
        public int PulseDownMs { get; set; } = 200;
        public int PulseUpMs { get; set; } = 100;
        public int FocusValidationTimeoutMs { get; set; } = 1500;

        /// <summary>Required for TEST 8. The UI enables this only through the advanced toggle.</summary>
        public bool AdvancedModeEnabled { get; set; }

        /// <summary>Required for TEST 9. No default waypoint is invented.</summary>
        public float? SafeWaypointX { get; set; }
        public float? SafeWaypointY { get; set; }
        public float MaxSafeWaypointDistance { get; set; } = 30f;

        public bool AutoStartRecoveryObservation { get; set; } = true;
        public int[] RecoveryCheckpointsSeconds { get; set; } = { 15, 30, 60, 120, 300 };
        public int MaxObservationMinutes { get; set; } = 30;

        public MovementDiagnosticSettings Clone() => new()
        {
            DurationMs = DurationMs,
            CountdownMs = CountdownMs,
            RepeatIntervalMs = RepeatIntervalMs,
            SampleIntervalMs = SampleIntervalMs,
            PulseDownMs = PulseDownMs,
            PulseUpMs = PulseUpMs,
            FocusValidationTimeoutMs = FocusValidationTimeoutMs,
            AdvancedModeEnabled = AdvancedModeEnabled,
            SafeWaypointX = SafeWaypointX,
            SafeWaypointY = SafeWaypointY,
            MaxSafeWaypointDistance = MaxSafeWaypointDistance,
            AutoStartRecoveryObservation = AutoStartRecoveryObservation,
            RecoveryCheckpointsSeconds = (RecoveryCheckpointsSeconds ?? Array.Empty<int>()).ToArray(),
            MaxObservationMinutes = MaxObservationMinutes
        };
    }

    public sealed class MovementDiagnosticEventEntry
    {
        public long Sequence { get; set; }
        public DateTime UtcTimestamp { get; set; }
        public double ElapsedMs { get; set; }
        public string EventType { get; set; } = "";
        public string Message { get; set; } = "";
        public string? InputBackend { get; set; }
        public string? VirtualKey { get; set; }
        public string? ScanCode { get; set; }
        public bool? ApiSucceeded { get; set; }
        public uint? InsertedCount { get; set; }
        public int? Win32Error { get; set; }
        public double? DurationMs { get; set; }
        public double? ActualIntervalMs { get; set; }
        public string? Details { get; set; }
    }

    public sealed class MovementDiagnosticSample
    {
        public long Sequence { get; set; }
        public DateTime UtcTimestamp { get; set; }
        public double ElapsedMs { get; set; }
        public string Phase { get; set; } = "";
        public bool PlayerPointerReadSucceeded { get; set; }
        public bool PositionReadSucceeded { get; set; }
        public float PlayerX { get; set; }
        public float PlayerY { get; set; }
        public bool MapReadSucceeded { get; set; }
        public int Map { get; set; }
        public float CameraAngleRadians { get; set; }
        public float CameraAngleDegrees { get; set; }
        public byte CurrentAction { get; set; }
        public int Animation1 { get; set; }
        public double ReadDurationMs { get; set; }
    }

    public sealed class MovementDiagnosticObserverRecord
    {
        public DateTime UtcTimestamp { get; set; }
        public double ElapsedMs { get; set; }
        public string Checkpoint { get; set; } = "Manual";
        public MovementRemoteObservationRating Rating { get; set; }
        public bool PersistedAfterSyntheticInputStopped { get; set; }
        public bool ObservedDuringSubsequentManualMovement { get; set; }
        public string Notes { get; set; } = "";
    }

    public sealed class MovementDiagnosticSession
    {
        public string SessionId { get; set; } = "";
        public DateTime StartedUtc { get; set; }
        public DateTime? EndedUtc { get; set; }
        public MovementDiagnosticScenario Scenario { get; set; }
        public string ScenarioLabel { get; set; } = "";
        public string InputMethodDescription { get; set; } = "";
        public MovementDiagnosticInputMode PrimaryInputMode { get; set; }
        public MovementDiagnosticSettings Settings { get; set; } = new();

        public uint GameProcessId { get; set; }
        public string? GameWindowHandle { get; set; }
        public string? ClientVersion { get; set; } = "Unknown";
        public bool FocusValidated { get; set; }
        public int InitialMap { get; set; }
        public bool InitialPositionReadSucceeded { get; set; }
        public float InitialX { get; set; }
        public float InitialY { get; set; }
        public bool FinalPositionReadSucceeded { get; set; }
        public float FinalX { get; set; }
        public float FinalY { get; set; }

        public string? SteeringModeUsed { get; set; }
        public bool DirectCameraRecoveryInvoked { get; set; }
        public List<string> MovementRecoveryEvents { get; set; } = new();
        public int CameraWriteCount { get; set; }
        public bool CameraReadbackVerified { get; set; }
        public float? CameraAngleBefore { get; set; }
        public float? CameraAngleAfter { get; set; }

        public int TotalKeyDownEvents { get; set; }
        public int TotalKeyUpEvents { get; set; }
        public int TotalRepeatEvents { get; set; }
        public int InputFailureCount { get; set; }
        public int ReadFailureCount { get; set; }
        public bool ReadFailureEventRecorded { get; set; }
        public int PulseTransitions { get; set; }
        public int StageCount { get; set; }
        public bool InputCleanupComplete { get; set; } = true;
        public string? CleanupDetail { get; set; }

        public DateTime? InputInjectionStoppedUtc { get; set; }
        public double? InputInjectionStoppedElapsedMs { get; set; }
        public double? MovementDurationMs { get; set; }
        public DateTime? ObservationStartedUtc { get; set; }
        public DateTime? RecoveryObservedUtc { get; set; }
        public double? RecoveryObservedElapsedMs { get; set; }
        public double? RecoveryDurationMs { get; set; }

        public MovementDiagnosticOutcome Outcome { get; set; } = MovementDiagnosticOutcome.NotRun;
        public string? FailureReason { get; set; }
        public string? OutputDirectory { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public Stopwatch Stopwatch { get; set; } = new();

        public List<MovementDiagnosticEventEntry> Events { get; set; } = new();
        public List<MovementDiagnosticSample> Samples { get; set; } = new();
        public List<MovementDiagnosticObserverRecord> Observations { get; set; } = new();

        public MovementDiagnosticObserverRecord? LatestObservation =>
            Observations.Count == 0 ? null : Observations[^1];

        public MovementDiagnosticSession CloneForReport(bool includeSamplesAndEvents)
        {
            var clone = (MovementDiagnosticSession)MemberwiseClone();
            clone.Settings = Settings.Clone();
            clone.Events = includeSamplesAndEvents ? Events.ToList() : new List<MovementDiagnosticEventEntry>();
            clone.Samples = includeSamplesAndEvents ? Samples.ToList() : new List<MovementDiagnosticSample>();
            clone.Observations = Observations.ToList();
            clone.MovementRecoveryEvents = MovementRecoveryEvents.ToList();
            clone.Stopwatch = new Stopwatch();
            return clone;
        }
    }

    public sealed class MovementDiagnosticSessionSummary
    {
        public string SessionId { get; set; } = "";
        public string Scenario { get; set; } = "";
        public string InputMechanism { get; set; } = "";
        public double? MovementDurationMs { get; set; }
        public string RemoteObservation { get; set; } = "Unknown";
        public string PersistenceAfterStop { get; set; } = "Unknown";
        public double? RecoveryDurationMs { get; set; }
        public string Outcome { get; set; } = "";
    }

    public sealed class MovementDiagnosticStatus
    {
        public MovementDiagnosticPhase Phase { get; set; } = MovementDiagnosticPhase.Idle;
        public MovementDiagnosticScenario? Scenario { get; set; }
        public string ScenarioDisplay { get; set; } = "";
        public string Message { get; set; } = "Idle.";
        public string InputMethodDescription { get; set; } = "";
        public string SessionId { get; set; } = "";
        public TimeSpan Elapsed { get; set; }
        public TimeSpan? CountdownRemaining { get; set; }
        public TimeSpan? HoldElapsed { get; set; }
        public TimeSpan? HoldTarget { get; set; }
        public string HoldLabel { get; set; } = "";
        public string LastCheckpoint { get; set; } = "";
        public string LastResult { get; set; } = "No test run yet.";
        public string? OutputDirectory { get; set; }
        public DateTime? InputInjectionStoppedUtc { get; set; }
        public double? RecoveryDurationMs { get; set; }
        public bool IsTestActive { get; set; }
        public bool IsObservationActive { get; set; }
        public bool HasCurrentSession { get; set; }
        public bool HasUnreleasedKeys { get; set; }
    }

    public readonly record struct MovementDiagnosticStartResult(bool Accepted, string Message)
    {
        public static MovementDiagnosticStartResult Ok(string message) => new(true, message);
        public static MovementDiagnosticStartResult Reject(string message) => new(false, message);
    }

    public readonly record struct MovementDiagnosticMarkResult(bool Accepted, string Message, double? RecoveryDurationMs)
    {
        public static MovementDiagnosticMarkResult Ok(string message, double durationMs)
            => new(true, message, durationMs);
        public static MovementDiagnosticMarkResult Reject(string message)
            => new(false, message, null);
    }
}
