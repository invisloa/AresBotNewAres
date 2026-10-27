using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DriverScanTester.Models;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Loads, saves, lists and validates BotProfiles stored as JSON files
    /// in the SavedBotProfiles/ directory. This is the single persistence
    /// service for profiles: listing, loading, saving and validating.
    /// </summary>
    public class BotProfileLoader
    {
        private static readonly string PROFILE_DIR = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "SavedBotProfiles"));
        private static readonly string SAVE_DIR = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "SavedPaths"));
        private readonly Action<string> _log;

        public BotProfileLoader(Action<string> log)
        {
            _log = log;
            if (!Directory.Exists(PROFILE_DIR))
                Directory.CreateDirectory(PROFILE_DIR);
        }

        /// <summary>
        /// Returns file names (without extension) of all profiles in the profile directory.
        /// </summary>
        public List<string> ListProfiles()
        {
            var result = new List<string>();
            if (!Directory.Exists(PROFILE_DIR))
                return result;

            foreach (var f in Directory.GetFiles(PROFILE_DIR, "*.json"))
            {
                result.Add(Path.GetFileNameWithoutExtension(f));
            }
            return result;
        }

        /// <summary>
        /// Loads a profile by name (with or without .json extension).
        /// Only the current flow schema (BotProfile.FlowSteps) is supported: a profile
        /// without flow steps is rejected with an explicit diagnostic instead of being
        /// guessed at. Saving the profile writes the same flow schema.
        /// </summary>
        public BotProfile? LoadProfile(string profileName)
        {
            if (!profileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                profileName += ".json";

            string path = Path.Combine(PROFILE_DIR, profileName);
            if (!File.Exists(path))
            {
                _log($"[BotProfileLoader] Profile not found: {path}");
                return null;
            }

            try
            {
                string json = File.ReadAllText(path);
                var profile = JsonSerializer.Deserialize<BotProfile>(json);
                if (profile == null)
                {
                    _log($"[BotProfileLoader] Failed to deserialize profile: {path}");
                    return null;
                }

                if (profile.FlowSteps == null || profile.FlowSteps.Count == 0)
                {
                    _log($"[BotProfileLoader] Profile '{profile.Name}' has no flow steps and cannot be used. " +
                         "Saving a profile always writes the flow schema, so this file is not a supported profile.");
                    return null;
                }

                _log($"[BotProfileLoader] Loaded profile '{profile.Name}' with {profile.FlowSteps.Count} flow steps.");
                return profile;
            }
            catch (Exception ex)
            {
                _log($"[BotProfileLoader] Error loading profile '{profileName}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Saves a profile to disk. The file name is derived from profile.Name.
        /// Returns true when the profile was written successfully, false otherwise.
        /// Saving always writes the new flow schema.
        /// </summary>
        public bool SaveProfile(BotProfile profile)
        {
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                _log("[BotProfileLoader] Cannot save profile without a Name.");
                return false;
            }

            string fileName = profile.Name.Trim();
            if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                fileName += ".json";

            string path = Path.Combine(PROFILE_DIR, fileName);

            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(profile, options);
                File.WriteAllText(path, json);
                _log($"[BotProfileLoader] Saved profile '{profile.Name}' to {path}");
                return true;
            }
            catch (Exception ex)
            {
                _log($"[BotProfileLoader] Error saving profile: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Validates a profile and returns a list of error messages.
        /// Returns an empty list if the profile is valid.
        /// Every detected error is reported in one pass rather than stopping
        /// at the first error.
        /// </summary>
        public List<string> ValidateProfile(BotProfile profile)
        {
            var errors = new List<string>();

            if (profile == null)
            {
                errors.Add("Profile is null.");
                return errors;
            }

            // --- General ---
            if (string.IsNullOrWhiteSpace(profile.Name))
                errors.Add("Profile name is empty.");

            // --- Flow ---
            if (profile.FlowSteps == null || profile.FlowSteps.Count == 0)
            {
                errors.Add("Flow: no steps configured. Add at least one step.");
            }
            else
            {
                for (int s = 0; s < profile.FlowSteps.Count; s++)
                {
                    var step = profile.FlowSteps[s];
                    string stepLabel = $"Flow step {s + 1}";

                    if (step == null)
                    {
                        errors.Add($"{stepLabel}: step is missing.");
                        continue;
                    }

                    if (!Enum.IsDefined(typeof(BotFlowStepType), step.Type))
                        errors.Add($"{stepLabel}: type '{step.Type}' is not a defined value.");

                    switch (step.Type)
                    {
                        case BotFlowStepType.Path:
                            if (step.Routes != null && step.Routes.Count > 0)
                            {
                                for (int r = 0; r < step.Routes.Count; r++)
                                {
                                    var route = step.Routes[r];
                                    string routeLabel = $"{stepLabel} route {r + 1}";
                                    ValidatePathStep(errors, route, routeLabel);
                                }
                            }
                            else
                            {
                                ValidatePathReference(errors, step.PathFile, step.StartDelayMs, stepLabel);
                            }
                            switch (step.CompletionMode)
                            {
                                case TravelRouteCompletionMode.FinalWaypoint:
                                    if (step.ExpectedDestinationMapNumber != 0)
                                        errors.Add($"{stepLabel}: destination map must be 0 when finishing at the last waypoint.");
                                    break;
                                case TravelRouteCompletionMode.ExpectedMapReached:
                                    if (step.ExpectedDestinationMapNumber <= 0)
                                        errors.Add($"{stepLabel}: expected destination map must be greater than 0.");
                                    break;
                                default:
                                    errors.Add($"{stepLabel}: completion mode '{step.CompletionMode}' is not a defined value.");
                                    break;
                            }
                            break;

                        case BotFlowStepType.ExpLoop:
                            if (step.Routes != null && step.Routes.Count > 0)
                            {
                                for (int r = 0; r < step.Routes.Count; r++)
                                {
                                    var route = step.Routes[r];
                                    string routeLabel = $"{stepLabel} route {r + 1}";
                                    ValidatePathStep(errors, route, routeLabel);
                                }
                            }
                            else
                            {
                                ValidatePathReference(errors, step.PathFile, step.StartDelayMs, stepLabel);
                            }
                            break;

                        case BotFlowStepType.Operation:
                            if (string.IsNullOrWhiteSpace(step.OperationName))
                            {
                                errors.Add($"{stepLabel}: operation name is empty.");
                            }
                            else if (!BotOperations.IsKnown(step.OperationName))
                            {
                                errors.Add($"{stepLabel}: operation '{step.OperationName}' is not a known operation.");
                            }
                            break;

                        case BotFlowStepType.Repot:
                            if (step.RepotPaths == null || step.RepotPaths.Count == 0)
                            {
                                errors.Add($"{stepLabel}: repot step has no paths to the repot location. Add at least one.");
                            }
                            else
                            {
                                for (int r = 0; r < step.RepotPaths.Count; r++)
                                {
                                    var repotStep = step.RepotPaths[r];
                                    string repotLabel = $"{stepLabel} repot path {r + 1}";
                                    if (repotStep == null)
                                    {
                                        errors.Add($"{repotLabel}: path is missing.");
                                        continue;
                                    }
                                    ValidatePathStep(errors, repotStep, repotLabel);
                                }
                            }
                            break;
                    }
                }

                // Guard: the ExpLoop is the looping hunting route. Reusing one of the
                // flow Path steps here makes the bot run the travel route forever
                // instead of hunting the camp. Route groups are checked too.
                var expLoopStep = profile.FlowSteps.FirstOrDefault(s => s != null && s.Type == BotFlowStepType.ExpLoop);
                if (expLoopStep != null)
                {
                    var expLoopFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(expLoopStep.PathFile))
                        expLoopFiles.Add(NormalizePathFileName(expLoopStep.PathFile));
                    foreach (var r in expLoopStep.Routes ?? new List<BotRouteStep>())
                    {
                        if (r != null && !string.IsNullOrWhiteSpace(r.PathFile))
                            expLoopFiles.Add(NormalizePathFileName(r.PathFile));
                    }

                    if (expLoopFiles.Count > 0)
                    {
                        foreach (var step in profile.FlowSteps)
                        {
                            if (step == null || step.Type != BotFlowStepType.Path)
                                continue;

                            var pathFiles = new List<string>();
                            if (!string.IsNullOrWhiteSpace(step.PathFile))
                                pathFiles.Add(step.PathFile);
                            foreach (var r in step.Routes ?? new List<BotRouteStep>())
                            {
                                if (r != null && !string.IsNullOrWhiteSpace(r.PathFile))
                                    pathFiles.Add(r.PathFile);
                            }

                            foreach (var pathFile in pathFiles)
                            {
                                if (expLoopFiles.Contains(NormalizePathFileName(pathFile)))
                                {
                                    errors.Add($"Flow: ExpLoop path '{pathFile}' is also used as a Path step. The ExpLoop must be the looping hunting path, not a travel route.");
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            // --- Repot thresholds / weight / retries ---
            if (profile.MinHpPotions < 0)
                errors.Add("MinHpPotions is negative.");
            if (profile.MinManaPotions < 0)
                errors.Add("MinManaPotions is negative.");
            if (profile.MinHp < 0)
                errors.Add("MinHp is negative.");
            if (profile.MinMana < 0)
                errors.Add("MinMana is negative.");
            if (profile.MaxWeightRatio <= 0 || profile.MaxWeightRatio > 1f)
                errors.Add("MaxWeightRatio should be between 0 and 1.");
            if (profile.MaxTeleportRetries < 0)
                errors.Add("MaxTeleportRetries is negative.");

            // --- Start position check / protection ---
            if (profile.EnableStartPositionCheck && profile.ProtectionMapNumber <= 0)
                errors.Add("Start position check: protected map number must be greater than 0. Set the map the player must be on after the start teleport.");
            if (profile.StartPositionTolerance < 0)
                errors.Add("Start position check: start position tolerance cannot be negative.");

            // Per-step starting maps (map-based startup detection). 0 = unspecified and valid;
            // a negative value is always a configuration error.
            if (profile.FlowSteps != null)
            {
                for (int s = 0; s < profile.FlowSteps.Count; s++)
                {
                    var step = profile.FlowSteps[s];
                    if (step == null) continue;
                    if (step.StartMapNumber < 0)
                        errors.Add($"Flow step {s + 1}: starting map number cannot be negative (0 means unspecified).");
                }
            }

            // Per-route start checks: every route with the start check enabled must
            // carry its own protected map and a nonnegative tolerance.
            if (profile.FlowSteps != null)
            {
                for (int s = 0; s < profile.FlowSteps.Count; s++)
                {
                    var step = profile.FlowSteps[s];
                    if (step == null) continue;

                    var routes = step.Type == BotFlowStepType.Repot
                        ? step.RepotPaths ?? new List<BotRouteStep>()
                        : step.Routes ?? new List<BotRouteStep>();

                    for (int r = 0; r < routes.Count; r++)
                    {
                        var route = routes[r];
                        if (route == null || !route.StartCheckEnabled) continue;

                        string routeLabel = $"Flow step {s + 1} route '{route.PathFile}'";
                        if (route.ProtectionMapNumber <= 0)
                            errors.Add($"{routeLabel}: start check enabled but protected map is 0. Set the map the player must be on after the start teleport (own per-route data).");
                        if (route.StartPositionTolerance < 0)
                            errors.Add($"{routeLabel}: start position tolerance cannot be negative.");
                    }
                }
            }

            return errors;
        }

        /// <summary>
        /// Validates a path reference for a flow step: non-empty PathFile that exists in
        /// SavedPaths, and a nonnegative delay.
        /// </summary>
        private void ValidatePathReference(List<string> errors, string pathFile, int startDelayMs, string displayName)
        {
            if (string.IsNullOrWhiteSpace(pathFile))
            {
                errors.Add($"{displayName}: path is empty.");
            }
            else if (!SegmentFileExists(pathFile))
            {
                errors.Add($"{displayName}: path '{pathFile}' was not found in SavedPaths.");
            }

            if (startDelayMs < 0)
                errors.Add($"{displayName}: wait time cannot be negative.");
        }

        /// <summary>
        /// Validates one BotRouteStep: non-null step, non-empty PathFile, the referenced
        /// path existing in SavedPaths (with or without .json), and a nonnegative delay.
        /// </summary>
        private void ValidatePathStep(List<string> errors, BotRouteStep step, string displayName)
        {
            if (step == null)
            {
                errors.Add($"{displayName}: path is missing.");
                return;
            }

            if (string.IsNullOrWhiteSpace(step.PathFile))
            {
                errors.Add($"{displayName}: path is empty.");
            }
            else if (!SegmentFileExists(step.PathFile))
            {
                errors.Add($"{displayName}: path '{step.PathFile}' was not found in SavedPaths.");
            }

            if (step.StartDelayMs < 0)
                errors.Add($"{displayName}: wait time cannot be negative.");

            // Optional map-based startup metadata: 0 = unspecified (valid), negative
            // values are invalid.
            if (step.StartMapNumber < 0)
                errors.Add($"{displayName}: starting map number cannot be negative (0 means unspecified).");
        }

        /// <summary>
        /// Returns true when a path file exists in SavedPaths.
        /// Path names may be stored with or without the .json extension.
        /// </summary>
        private static bool SegmentFileExists(string segmentFileName)
        {
            string fileName = segmentFileName;
            if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                fileName += ".json";
            return File.Exists(Path.Combine(SAVE_DIR, fileName));
        }

        /// <summary>
        /// Normalizes a saved-path reference to a comparable form (file name
        /// without the .json extension) so two references can be compared even
        /// when one was stored with the extension and the other without.
        /// </summary>
        private static string NormalizePathFileName(string pathFileName)
        {
            string name = pathFileName.Trim();
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - ".json".Length);
            return name;
        }
    }
}