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
        /// Profiles saved with the previous Hunt-based schema are migrated in memory:
        /// the default hunt (or the first hunt) becomes the profile's direct
        /// Go to EXP / EXP Path stages. Saving the profile writes only the new schema.
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

                MigrateLegacyHuntSchema(profile, json);

                _log($"[BotProfileLoader] Loaded profile '{profile.Name}'.");
                return profile;
            }
            catch (Exception ex)
            {
                _log($"[BotProfileLoader] Error loading profile '{profileName}': {ex.Message}");
                return null;
            }
        }

        // ──────────────────── Legacy migration (private) ────────────────────

        /// <summary>
        /// Private DTO for the old Hunt-based schema. Used only to read legacy
        /// profile JSON; the public BotProfile model never exposes Hunt properties.
        /// </summary>
        private sealed class LegacyHuntDto
        {
            public string Name { get; set; } = "";
            public List<TravelRouteStep> TravelToExpRoutes { get; set; } = new();
            public BotRouteStep ExpLoop { get; set; } = new();
            public BotRouteStep? RepotToCityExit { get; set; }
            public BotRouteStep? CityExitToExp { get; set; }
        }

        /// <summary>
        /// One-time in-memory migration for profiles saved with the Hunt-based schema.
        /// The hunt matching the old DefaultHuntName (or the first hunt when there is
        /// no match) is selected and its TravelToExpRoutes / ExpLoop are copied into the
        /// new direct profile properties. All other profile settings are preserved.
        /// </summary>
        private void MigrateLegacyHuntSchema(BotProfile profile, string json)
        {
            using var doc = JsonDocument.Parse(json);

            // New direct schema already present: nothing to migrate.
            if (doc.RootElement.TryGetProperty("TravelToExpRoutes", out var directRoutesElement) &&
                directRoutesElement.ValueKind == JsonValueKind.Array &&
                directRoutesElement.GetArrayLength() > 0)
            {
                return;
            }

            if (!doc.RootElement.TryGetProperty("HuntDefinitions", out var huntsElement) ||
                huntsElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var hunts = new List<LegacyHuntDto>();
            foreach (var huntElement in huntsElement.EnumerateArray())
            {
                var hunt = huntElement.Deserialize<LegacyHuntDto>();
                if (hunt != null)
                    hunts.Add(hunt);
            }
            if (hunts.Count == 0)
                return;

            // Select the hunt matching the old DefaultHuntName, otherwise the first one.
            string defaultHuntName = "";
            if (doc.RootElement.TryGetProperty("DefaultHuntName", out var defaultElement) &&
                defaultElement.ValueKind == JsonValueKind.String)
            {
                defaultHuntName = defaultElement.GetString() ?? "";
            }

            var selected = hunts.FirstOrDefault(h =>
                string.Equals(h.Name, defaultHuntName, StringComparison.OrdinalIgnoreCase))
                ?? hunts[0];

            // Newer legacy hunts carry TravelToExpRoutes directly; the older schema used
            // RepotToCityExit / CityExitToExp steps, converted here in that order into
            // FinalWaypoint travel paths, preserving PathFile and StartDelayMs.
            var routes = new List<TravelRouteStep>(selected.TravelToExpRoutes ?? new List<TravelRouteStep>());
            if (routes.Count == 0 && (selected.RepotToCityExit != null || selected.CityExitToExp != null))
            {
                foreach (var step in new[] { selected.RepotToCityExit, selected.CityExitToExp })
                {
                    if (step == null)
                        continue;

                    routes.Add(new TravelRouteStep
                    {
                        PathFile = step.PathFile ?? "",
                        StartDelayMs = step.StartDelayMs,
                        CompletionMode = TravelRouteCompletionMode.FinalWaypoint,
                        ExpectedDestinationMapNumber = 0
                    });
                }
            }

            profile.TravelToExpRoutes = routes;
            profile.ExpLoop = selected.ExpLoop ?? new BotRouteStep();
            if (profile.ExpLoop.PathFile == null)
                profile.ExpLoop.PathFile = "";

            _log($"[BotProfileLoader] Legacy profile '{profile.Name}': migrated hunt '{selected.Name}' into the profile's Go to EXP / EXP Path stages.");
            if (hunts.Count > 1)
                _log($"[BotProfileLoader] Legacy profile '{profile.Name}' contained {hunts.Count} hunts; only '{selected.Name}' was migrated because one profile now represents exactly one EXP destination.");
        }

        /// <summary>
        /// Saves a profile to disk. The file name is derived from profile.Name.
        /// Returns true when the profile was written successfully, false otherwise.
        /// Saving always writes the new direct schema.
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

            // --- Stage 1: REPOT ---
            if (profile.CityToRepot == null)
            {
                errors.Add("Stage 1 — Repot: path is missing. Configure the path to the repot location.");
            }
            else
            {
                ValidatePathStep(errors, profile.CityToRepot, "Stage 1 — Repot");
            }

            // --- Stage 2: GO TO EXP ---
            if (profile.TravelToExpRoutes == null)
            {
                errors.Add("Stage 2 — Go to EXP: no paths configured. Add at least one path.");
            }
            else if (profile.TravelToExpRoutes.Count == 0)
            {
                errors.Add("Stage 2 — Go to EXP: no paths configured. Add at least one path.");
            }
            else
            {
                for (int r = 0; r < profile.TravelToExpRoutes.Count; r++)
                {
                    var route = profile.TravelToExpRoutes[r];
                    string routeLabel = $"Go to EXP path {r + 1}";

                    if (route == null)
                    {
                        errors.Add($"{routeLabel}: path is missing.");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(route.PathFile))
                    {
                        errors.Add($"{routeLabel}: path is empty.");
                    }
                    else if (!SegmentFileExists(route.PathFile))
                    {
                        errors.Add($"{routeLabel}: path '{route.PathFile}' was not found in SavedPaths.");
                    }

                    if (route.StartDelayMs < 0)
                        errors.Add($"{routeLabel}: wait time cannot be negative.");

                    if (!Enum.IsDefined(typeof(TravelRouteCompletionMode), route.CompletionMode))
                        errors.Add($"{routeLabel}: completion mode '{route.CompletionMode}' is not a defined value.");

                    switch (route.CompletionMode)
                    {
                        case TravelRouteCompletionMode.FinalWaypoint:
                            if (route.ExpectedDestinationMapNumber != 0)
                                errors.Add($"{routeLabel}: destination map must be 0 when finishing at the last waypoint.");
                            break;
                        case TravelRouteCompletionMode.ExpectedMapReached:
                            if (route.ExpectedDestinationMapNumber <= 0)
                                errors.Add($"{routeLabel}: expected destination map must be greater than 0.");
                            break;
                    }
                }
            }

            // --- Stage 3: EXP PATH ---
            if (profile.ExpLoop == null)
            {
                errors.Add("Stage 3 — EXP Path: path is missing. Configure the looping EXP path.");
            }
            else
            {
                ValidatePathStep(errors, profile.ExpLoop, "Stage 3 — EXP Path");
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

            return errors;
        }

        /// <summary>
        /// Validates one path step: non-null step, non-empty PathFile, the referenced
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
    }
}
