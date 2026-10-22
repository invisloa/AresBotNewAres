using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Writes the independent diagnostic session artefacts. The report distinguishes
    /// local memory samples from user-provided remote-client observations and never
    /// labels local coordinates as server positions.
    /// </summary>
    public sealed class MovementDiagnosticReportWriter
    {
        private readonly Action<string> _log;
        private readonly string _rootDirectory;
        private readonly object _writeLock = new();

        public MovementDiagnosticReportWriter(Action<string> log, string? rootDirectory = null)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _rootDirectory = rootDirectory ?? Path.Combine(
                BotFileLogger.LogsDirectory,
                "MovementSyncDiagnostics");
        }

        public string RootDirectory => _rootDirectory;

        public string GetSessionDirectory(string sessionId)
            => Path.Combine(_rootDirectory, sessionId);

        public string WriteReport(
            MovementDiagnosticSession session,
            IReadOnlyList<MovementDiagnosticSessionSummary> history)
        {
            lock (_writeLock)
            {
                string directory = GetSessionDirectory(session.SessionId);
                try
                {
                    Directory.CreateDirectory(directory);
                    WriteEventsCsv(Path.Combine(directory, "events.csv"), session);
                    WriteSamplesCsv(Path.Combine(directory, "samples.csv"), session);
                    WriteSessionJson(Path.Combine(directory, "session.json"), session, history);
                    WriteTextReport(Path.Combine(directory, "report.txt"), session, history);
                    return directory;
                }
                catch (Exception ex)
                {
                    _log($"[MovementDiag] Report write failed for {session.SessionId}: {ex.Message}");
                    return session.OutputDirectory ?? directory;
                }
            }
        }

        public static MovementDiagnosticSessionSummary BuildSummary(MovementDiagnosticSession session)
        {
            MovementDiagnosticObserverRecord? latest = session.LatestObservation;
            return new MovementDiagnosticSessionSummary
            {
                SessionId = session.SessionId,
                Scenario = MovementDiagnosticScenarioCatalog.GetDisplayText(session.Scenario),
                InputMechanism = session.InputMethodDescription,
                MovementDurationMs = session.MovementDurationMs,
                RemoteObservation = latest == null
                    ? "Unknown"
                    : MovementDiagnosticScenarioCatalog.GetObservationLabel(latest.Rating),
                PersistenceAfterStop = latest == null
                    ? "Unknown"
                    : latest.PersistedAfterSyntheticInputStopped ? "Yes (reported)" : "No (reported)",
                RecoveryDurationMs = session.RecoveryDurationMs,
                Outcome = session.Outcome.ToString()
            };
        }

        private static void WriteEventsCsv(string path, MovementDiagnosticSession session)
        {
            var sb = new StringBuilder();
            sb.AppendLine("sequence,utc_timestamp,elapsed_ms,session_id,scenario,event_type,message,input_backend,virtual_key,scan_code,api_succeeded,inserted_count,win32_error,duration_ms,actual_interval_ms,details");
            foreach (MovementDiagnosticEventEntry ev in session.Events.OrderBy(e => e.Sequence))
            {
                sb.Append(ev.Sequence).Append(',')
                  .Append(Csv(ev.UtcTimestamp.ToString("O", CultureInfo.InvariantCulture))).Append(',')
                  .Append(F(ev.ElapsedMs)).Append(',')
                  .Append(Csv(session.SessionId)).Append(',')
                  .Append(Csv(MovementDiagnosticScenarioCatalog.GetDisplayText(session.Scenario))).Append(',')
                  .Append(Csv(ev.EventType)).Append(',')
                  .Append(Csv(ev.Message)).Append(',')
                  .Append(Csv(ev.InputBackend)).Append(',')
                  .Append(Csv(ev.VirtualKey)).Append(',')
                  .Append(Csv(ev.ScanCode)).Append(',')
                  .Append(ev.ApiSucceeded.HasValue ? (ev.ApiSucceeded.Value ? "true" : "false") : "").Append(',')
                  .Append(ev.InsertedCount?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
                  .Append(ev.Win32Error?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
                  .Append(ev.DurationMs.HasValue ? F(ev.DurationMs.Value) : "").Append(',')
                  .Append(ev.ActualIntervalMs.HasValue ? F(ev.ActualIntervalMs.Value) : "").Append(',')
                  .Append(Csv(ev.Details)).AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static void WriteSamplesCsv(string path, MovementDiagnosticSession session)
        {
            var sb = new StringBuilder();
            sb.AppendLine("sequence,utc_timestamp,elapsed_ms,phase,player_pointer_read,position_read,x,y,map_read,map,camera_angle_degrees,camera_angle_radians,current_action,animation1,read_duration_ms");
            foreach (MovementDiagnosticSample sample in session.Samples.OrderBy(s => s.Sequence))
            {
                sb.Append(sample.Sequence).Append(',')
                  .Append(Csv(sample.UtcTimestamp.ToString("O", CultureInfo.InvariantCulture))).Append(',')
                  .Append(F(sample.ElapsedMs)).Append(',')
                  .Append(Csv(sample.Phase)).Append(',')
                  .Append(B(sample.PlayerPointerReadSucceeded)).Append(',')
                  .Append(B(sample.PositionReadSucceeded)).Append(',')
                  .Append(sample.PositionReadSucceeded ? F(sample.PlayerX) : "").Append(',')
                  .Append(sample.PositionReadSucceeded ? F(sample.PlayerY) : "").Append(',')
                  .Append(B(sample.MapReadSucceeded)).Append(',')
                  .Append(sample.MapReadSucceeded ? sample.Map.ToString(CultureInfo.InvariantCulture) : "").Append(',')
                  .Append(F(sample.CameraAngleDegrees)).Append(',')
                  .Append(F(sample.CameraAngleRadians)).Append(',')
                  .Append(sample.CurrentAction.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(sample.Animation1.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(F(sample.ReadDurationMs)).AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static void WriteSessionJson(
            string path,
            MovementDiagnosticSession session,
            IReadOnlyList<MovementDiagnosticSessionSummary> history)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            };
            var document = new
            {
                schemaVersion = 1,
                generatedUtc = DateTime.UtcNow,
                session,
                comparisonHistory = history
            };
            File.WriteAllText(path, JsonSerializer.Serialize(document, options), new UTF8Encoding(false));
        }

        private static void WriteTextReport(
            string path,
            MovementDiagnosticSession session,
            IReadOnlyList<MovementDiagnosticSessionSummary> history)
        {
            var sb = new StringBuilder();
            sb.AppendLine("MOVEMENT SYNCHRONIZATION DIAGNOSTIC");
            sb.AppendLine("===================================");
            sb.AppendLine($"Session:              {session.SessionId}");
            sb.AppendLine($"Scenario:             {MovementDiagnosticScenarioCatalog.GetDisplayText(session.Scenario)}");
            sb.AppendLine($"Input mechanism:      {session.InputMethodDescription}");
            sb.AppendLine($"Started (UTC):        {session.StartedUtc:O}");
            sb.AppendLine($"Ended (UTC):          {(session.EndedUtc.HasValue ? session.EndedUtc.Value.ToString("O") : "not finished")}");
            sb.AppendLine($"Outcome:              {session.Outcome}");
            sb.AppendLine($"Game PID:             {session.GameProcessId}");
            sb.AppendLine($"Game window:          {session.GameWindowHandle ?? "Unknown"}");
            sb.AppendLine($"Client version:       {session.ClientVersion ?? "Unknown"}");
            sb.AppendLine($"Focus validated:      {session.FocusValidated}");
            sb.AppendLine($"Steering mode used:   {session.SteeringModeUsed ?? "N/A"}");
            sb.AppendLine($"Direct-camera recovery invoked: {session.DirectCameraRecoveryInvoked}");
            sb.AppendLine($"Input failures:       {session.InputFailureCount}");
            sb.AppendLine($"Sample read failures: {session.ReadFailureCount}");
            sb.AppendLine($"Key down / up / repeat: {session.TotalKeyDownEvents} / {session.TotalKeyUpEvents} / {session.TotalRepeatEvents}");
            sb.AppendLine($"Input cleanup:        {(session.InputCleanupComplete ? "complete" : "INCOMPLETE")} {session.CleanupDetail}");
            sb.AppendLine($"Input stopped (UTC):  {(session.InputInjectionStoppedUtc.HasValue ? session.InputInjectionStoppedUtc.Value.ToString("O") : "not recorded")}");
            sb.AppendLine($"Movement duration:    {FormatMs(session.MovementDurationMs)}");
            sb.AppendLine($"Manual recovery mark: {(session.RecoveryObservedUtc.HasValue ? session.RecoveryObservedUtc.Value.ToString("O") : "not recorded")}");
            sb.AppendLine($"Recovery duration:    {FormatMs(session.RecoveryDurationMs)}");
            sb.AppendLine($"Report directory:     {session.OutputDirectory ?? "unknown"}");
            sb.AppendLine();
            sb.AppendLine("LOCAL SAMPLES — NOT SERVER POSITIONS");
            sb.AppendLine("------------------------------------");
            AppendPositionSummary(sb, "Initial", session.InitialPositionReadSucceeded, session.InitialMap, session.InitialX, session.InitialY);
            AppendPositionSummary(sb, "Final", session.FinalPositionReadSucceeded, null, session.FinalX, session.FinalY);
            sb.AppendLine("X/Y are read from local game memory via the existing GameMemoryService.GetPlayerStateSnapshot().");
            sb.AppendLine("They are not authoritative server positions. Current action and animation1 are local memory values,");
            sb.AppendLine("not proof of what another client renders.");
            sb.AppendLine();

            sb.AppendLine("OBSERVER RECORDS (user-provided, authoritative only for the reported visual observation)");
            sb.AppendLine("--------------------------------------------------------------------------------------");
            if (session.Observations.Count == 0)
            {
                sb.AppendLine("No observer records were saved. The remote-client appearance is Unknown.");
            }
            else
            {
                foreach (MovementDiagnosticObserverRecord observation in session.Observations)
                {
                    sb.AppendLine($"{observation.UtcTimestamp:O} | elapsed={FormatMs(observation.ElapsedMs)} | checkpoint={observation.Checkpoint}");
                    sb.AppendLine($"  Rating: {MovementDiagnosticScenarioCatalog.GetObservationLabel(observation.Rating)}");
                    sb.AppendLine($"  Persisted after synthetic input stopped: {observation.PersistedAfterSyntheticInputStopped}");
                    sb.AppendLine($"  Observed during subsequent manual movement: {observation.ObservedDuringSubsequentManualMovement}");
                    if (!string.IsNullOrWhiteSpace(observation.Notes))
                        sb.AppendLine($"  Notes: {observation.Notes}");
                }
            }
            sb.AppendLine();

            sb.AppendLine("RECOVERY TIMELINE");
            sb.AppendLine("-----------------");
            foreach (MovementDiagnosticEventEntry ev in session.Events
                         .Where(e => e.EventType.StartsWith("RECOVERY_", StringComparison.Ordinal) ||
                                     e.EventType == "INPUT_INJECTION_STOPPED" ||
                                     e.EventType == "POST_TEST_OBSERVATION_STARTED" ||
                                     e.EventType == "POST_TEST_OBSERVATION_STOPPED")
                         .OrderBy(e => e.Sequence))
            {
                sb.AppendLine($"{ev.UtcTimestamp:O} | +{FormatMs(ev.ElapsedMs)} | {ev.EventType} | {ev.Message}");
            }
            if (session.Events.All(e => e.EventType != "RECOVERY_"))
                sb.AppendLine("Recovery was not manually observed or marked (Unknown).");
            sb.AppendLine();

            sb.AppendLine("SCENARIO COMPARISON — THIS DIAGNOSTIC SESSION HISTORY");
            sb.AppendLine("-----------------------------------------------------");
            sb.AppendLine("Scenario | Input mechanism | Movement duration | Remote observation | Persistence after stop | Recovery time");
            foreach (MovementDiagnosticSessionSummary row in history)
            {
                sb.AppendLine(string.Join(" | ", new[]
                {
                    row.Scenario,
                    row.InputMechanism,
                    FormatMs(row.MovementDurationMs),
                    row.RemoteObservation,
                    row.PersistenceAfterStop,
                    FormatMs(row.RecoveryDurationMs)
                }));
            }
            sb.AppendLine();

            sb.AppendLine("OBSERVED CORRELATIONS / LIMITATIONS");
            sb.AppendLine("-----------------------------------");
            foreach (string line in BuildCorrelationLines(history))
                sb.AppendLine(line);
            sb.AppendLine();
            sb.AppendLine("No server-side synchronization measurement is available to this application. Local memory samples");
            sb.AppendLine("cannot prove server state or another client's rendering. Any conclusion requires the observer records above.");
            sb.AppendLine("keybd_event returns no insertion count, so its failures cannot be detected per call; foreground validation");
            sb.AppendLine("and the elevation fields in GAME_CONTEXT are the available safeguards. SendInput insertion counts and");
            sb.AppendLine("Win32 errors are recorded explicitly.");
            sb.AppendLine("If observer records are missing or ambiguous, the cause remains undetermined.");

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static IEnumerable<string> BuildCorrelationLines(
            IReadOnlyList<MovementDiagnosticSessionSummary> history)
        {
            var usable = history
                .Where(h => h.RemoteObservation != "Unknown" &&
                            h.RemoteObservation != MovementDiagnosticScenarioCatalog.GetObservationLabel(
                                MovementRemoteObservationRating.NotRecorded) &&
                            h.RemoteObservation != MovementDiagnosticScenarioCatalog.GetObservationLabel(
                                MovementRemoteObservationRating.CouldNotDetermine))
                .ToList();
            var undetermined = history.Count(h => h.RemoteObservation ==
                MovementDiagnosticScenarioCatalog.GetObservationLabel(MovementRemoteObservationRating.CouldNotDetermine));
            bool anyAbnormal = usable.Any(IsAbnormal);
            bool anySmooth = usable.Any(h => h.RemoteObservation ==
                MovementDiagnosticScenarioCatalog.GetObservationLabel(MovementRemoteObservationRating.SmoothMovement));

            if (usable.Count == 0)
            {
                yield return undetermined > 0
                    ? "- Observer records exist but are all 'Could not determine'. The cause remains undetermined."
                    : "- Insufficient observer data: the cause remains undetermined.";
                yield break;
            }

            if (anyAbnormal && anySmooth)
            {
                yield return "- Observer reports differ between scenarios. Compare the mechanisms above to isolate the input";
                yield return "  method that first correlates with the abnormal remote appearance.";
            }
            else if (anyAbnormal)
            {
                yield return "- Abnormal remote appearance was reported, but no comparable smooth scenario was recorded.";
            }
            else if (anySmooth)
            {
                yield return "- Only smooth movement was reported so far; keep testing the remaining input mechanisms.";
            }

            var abnormalSessions = usable.Where(IsAbnormal).ToList();
            var persisted = abnormalSessions
                .Where(h => h.PersistenceAfterStop == "Yes (reported)")
                .ToList();
            if (persisted.Count == 0)
            {
                yield return "- No observer record confirms persistence after synthetic input stopped.";
            }
            else
            {
                foreach (var row in persisted)
                {
                    string recovery = row.RecoveryDurationMs.HasValue
                        ? $"{FormatMs(row.RecoveryDurationMs)} after confirmed input stop"
                        : "not yet recovered/marked";
                    yield return $"- Persistence after input stop was reported for {row.Scenario}; recovery: {recovery}.";
                }
            }

            var repeated = usable.FirstOrDefault(h => h.Scenario.Contains("Repeated W", StringComparison.Ordinal));
            var continuous = usable.FirstOrDefault(h => h.Scenario.Contains("Continuous W", StringComparison.Ordinal));
            if (repeated != null && continuous != null)
            {
                yield return IsAbnormal(repeated) != IsAbnormal(continuous)
                    ? "- Repeated and continuous keybd_event W produced different observer reports."
                    : "- Repeated and continuous keybd_event W produced the same observer category.";
            }

            var keybd = usable.FirstOrDefault(h => h.Scenario.Contains("keybd_event — Continuous W", StringComparison.Ordinal));
            var sendInput = usable.FirstOrDefault(h => h.Scenario.Contains("SendInput", StringComparison.Ordinal));
            if (keybd != null && sendInput != null)
            {
                yield return IsAbnormal(keybd) != IsAbnormal(sendInput)
                    ? "- keybd_event and SendInput produced different observer reports."
                    : "- keybd_event and SendInput produced the same observer category.";
            }

            var directCamera = usable.FirstOrDefault(h => h.Scenario.Contains("DirectCamera", StringComparison.Ordinal));
            if (directCamera != null)
            {
                yield return IsAbnormal(directCamera)
                    ? "- Abnormal movement was reported after DirectCamera memory steering."
                    : "- No abnormal movement was reported for the DirectCamera memory-steering test.";
            }

            var production = usable.FirstOrDefault(h => h.Scenario.Contains("OnlyMove", StringComparison.Ordinal));
            if (production != null)
            {
                yield return IsAbnormal(production)
                    ? "- Abnormal movement was reported with the existing MovementSystem OnlyMove controller."
                    : "- No abnormal movement was reported with the existing MovementSystem OnlyMove controller.";
            }
        }

        private static bool IsAbnormal(MovementDiagnosticSessionSummary summary)
            => summary.RemoteObservation == MovementDiagnosticScenarioCatalog.GetObservationLabel(
                   MovementRemoteObservationRating.AnimationStutteringOnly) ||
               summary.RemoteObservation == MovementDiagnosticScenarioCatalog.GetObservationLabel(
                   MovementRemoteObservationRating.PositionJumping) ||
               summary.RemoteObservation == MovementDiagnosticScenarioCatalog.GetObservationLabel(
                   MovementRemoteObservationRating.BothAnimationAndPosition);

        private static void AppendPositionSummary(
            StringBuilder sb,
            string label,
            bool positionRead,
            int? map,
            float x,
            float y)
        {
            sb.AppendLine(positionRead
                ? $"{label}: local X={x:F1} Y={y:F1} Map={(map.HasValue ? map.Value.ToString(CultureInfo.InvariantCulture) : "see samples")}"
                : $"{label}: local position read unavailable");
        }

        private static string Csv(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            bool quote = value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n');
            string escaped = value.Replace("\"", "\"\"");
            return quote ? $"\"{escaped}\"" : escaped;
        }

        private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        private static string B(bool value) => value ? "true" : "false";

        private static string FormatMs(double? ms)
            => ms.HasValue ? $"{ms.Value:F0} ms ({TimeSpan.FromMilliseconds(ms.Value):mm\\:ss\\.fff})" : "Unknown";
    }
}
