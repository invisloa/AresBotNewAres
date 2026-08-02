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
        /// Old profiles that are missing the new route steps deserialize fine
        /// (unknown properties are ignored); validation reports the missing stages.
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

                _log($"[BotProfileLoader] Loaded profile '{profile.Name}' ({profile.HuntDefinitions?.Count ?? 0} hunt(s)).");
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
        /// </summary>
        public void SaveProfile(BotProfile profile)
        {
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                _log("[BotProfileLoader] Cannot save profile without a Name.");
                return;
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
            }
            catch (Exception ex)
            {
                _log($"[BotProfileLoader] Error saving profile: {ex.Message}");
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
                errors.Add("Profile Name is empty.");

            // --- Stage 1: City → Repot (profile-level) ---
            if (profile.CityToRepot == null)
            {
                errors.Add("City → Repot: route step is missing. Configure stage 1 (City → Repot).");
            }
            else
            {
                ValidateRouteStep(errors, profile.CityToRepot, "City → Repot");
            }

            // --- Hunt definitions ---
            if (profile.HuntDefinitions == null || profile.HuntDefinitions.Count == 0)
            {
                errors.Add("Profile has no hunts. Add at least one hunt with all route stages configured.");
            }
            else
            {
                for (int i = 0; i < profile.HuntDefinitions.Count; i++)
                {
                    var hunt = profile.HuntDefinitions[i];
                    string huntLabel = string.IsNullOrWhiteSpace(hunt.Name) ? $"Hunt[{i}]" : $"Hunt '{hunt.Name}'";

                    if (string.IsNullOrWhiteSpace(hunt.Name))
                        errors.Add($"HuntDefinitions[{i}]: Name is empty.");

                    if (hunt.RepotToCityExit == null)
                        errors.Add($"{huntLabel}, Repot → Outside City: route step is missing.");
                    else
                        ValidateRouteStep(errors, hunt.RepotToCityExit, $"{huntLabel}, Repot → Outside City");

                    if (hunt.CityExitToExp == null)
                        errors.Add($"{huntLabel}, Outside City → Exp Spot: route step is missing.");
                    else
                        ValidateRouteStep(errors, hunt.CityExitToExp, $"{huntLabel}, Outside City → Exp Spot");

                    if (hunt.ExpLoop == null)
                        errors.Add($"{huntLabel}, Exp Loop: route step is missing.");
                    else
                        ValidateRouteStep(errors, hunt.ExpLoop, $"{huntLabel}, Exp Loop");
                }

                // Duplicate hunt names (ordinal case-insensitive)
                var duplicateNames = profile.HuntDefinitions
                    .GroupBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .ToList();
                foreach (var dup in duplicateNames)
                    errors.Add($"Duplicate hunt name '{dup}' (names are compared case-insensitively).");
            }

            // --- Default hunt ---
            if (string.IsNullOrWhiteSpace(profile.DefaultHuntName))
            {
                errors.Add("DefaultHuntName is empty. Set a default hunt.");
            }
            else if (profile.HuntDefinitions != null)
            {
                int defaultMatches = profile.HuntDefinitions.Count(h =>
                    string.Equals(h.Name, profile.DefaultHuntName, StringComparison.OrdinalIgnoreCase));
                if (defaultMatches != 1)
                    errors.Add($"DefaultHuntName '{profile.DefaultHuntName}' does not match exactly one hunt.");
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
        /// Validates one route step: non-null step, non-empty PathFile, the referenced
        /// segment existing in SavedPaths (with or without .json), and a nonnegative delay.
        /// </summary>
        private void ValidateRouteStep(List<string> errors, BotRouteStep step, string displayName)
        {
            if (step == null)
            {
                errors.Add($"{displayName}: route step is missing.");
                return;
            }

            if (string.IsNullOrWhiteSpace(step.PathFile))
            {
                errors.Add($"{displayName}: PathFile is empty.");
            }
            else if (!SegmentFileExists(step.PathFile))
            {
                errors.Add($"{displayName}: segment '{step.PathFile}' was not found in SavedPaths.");
            }

            if (step.StartDelayMs < 0)
                errors.Add($"{displayName}: StartDelayMs cannot be negative.");
        }

        /// <summary>
        /// Returns true when a segment file exists in SavedPaths.
        /// Segment names may be stored with or without the .json extension.
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
