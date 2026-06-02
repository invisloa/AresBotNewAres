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
    /// in the SavedBotProfiles/ directory.
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
        /// Handles legacy profiles by auto-converting RepotToExpPath/ExpLoopPath
        /// into a single HuntDefinition named "Default".
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

                // Backward compatibility: convert legacy RepotToExpPath/ExpLoopPath into HuntDefinitions
                if ((profile.HuntDefinitions == null || profile.HuntDefinitions.Count == 0)
                    && (!string.IsNullOrWhiteSpace(profile.RepotToExpPath) || !string.IsNullOrWhiteSpace(profile.ExpLoopPath)))
                {
                    profile.HuntDefinitions = new List<HuntDefinition>
                    {
                        new HuntDefinition
                        {
                            Name = "Default",
                            RepotToExpPath = profile.RepotToExpPath ?? "",
                            ExpLoopPath = profile.ExpLoopPath ?? ""
                        }
                    };
                    profile.DefaultHuntName = "Default";
                    _log($"[BotProfileLoader] Legacy profile converted: created HuntDefinition 'Default' from RepotToExpPath/ExpLoopPath.");
                }

                _log($"[BotProfileLoader] Loaded profile '{profile.Name}' ({profile.StartRoutes.Count} start routes, {profile.HuntDefinitions?.Count ?? 0} hunt(s)).");
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
        /// Validates HuntDefinitions as the primary source for phase 2+3 paths,
        /// but also handles legacy profiles with RepotToExpPath/ExpLoopPath.
        /// </summary>
        public List<string> ValidateProfile(BotProfile profile)
        {
            var errors = new List<string>();

            if (profile == null)
            {
                errors.Add("Profile is null.");
                return errors;
            }

            if (string.IsNullOrWhiteSpace(profile.Name))
                errors.Add("Profile Name is empty.");

            if (profile.StartRoutes == null || profile.StartRoutes.Count == 0)
                errors.Add("Profile has no StartRoutes defined.");
            else
            {
                for (int i = 0; i < profile.StartRoutes.Count; i++)
                {
                    var route = profile.StartRoutes[i];
                    if (string.IsNullOrWhiteSpace(route.Name))
                        errors.Add($"StartRoutes[{i}]: Name is empty.");
                    if (route.Area == null)
                        errors.Add($"StartRoutes[{i}] '{route.Name}': Area is null.");
                    if (string.IsNullOrWhiteSpace(route.PathFile))
                        errors.Add($"StartRoutes[{i}] '{route.Name}': PathFile is empty.");
                    else
                    {
                        string segPath = Path.Combine(SAVE_DIR, route.PathFile);
                        if (!segPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                            segPath += ".json";
                        if (!File.Exists(segPath))
                            errors.Add($"StartRoutes[{i}] '{route.Name}': PathFile '{route.PathFile}' not found in SavedPaths/.");
                    }
                }
            }

            // --- Validate HuntDefinitions (new format, preferred) ---
            bool hasHuntDefinitions = profile.HuntDefinitions != null && profile.HuntDefinitions.Count > 0;

            if (hasHuntDefinitions)
            {
                // Check for duplicate names
                var duplicateNames = profile.HuntDefinitions
                    .GroupBy(h => h.Name)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .ToList();
                foreach (var dup in duplicateNames)
                    errors.Add($"HuntDefinitions: Duplicate hunt name '{dup}'.");

                for (int i = 0; i < profile.HuntDefinitions.Count; i++)
                {
                    var hunt = profile.HuntDefinitions[i];
                    if (string.IsNullOrWhiteSpace(hunt.Name))
                        errors.Add($"HuntDefinitions[{i}]: Name is empty.");
                    if (string.IsNullOrWhiteSpace(hunt.RepotToExpPath))
                        errors.Add($"HuntDefinitions[{i}] '{hunt.Name}': RepotToExpPath is empty.");
                    else
                    {
                        string path = hunt.RepotToExpPath;
                        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                            path += ".json";
                        string fullPath = Path.Combine(SAVE_DIR, path);
                        if (!File.Exists(fullPath))
                            errors.Add($"HuntDefinitions[{i}] '{hunt.Name}': RepotToExpPath '{hunt.RepotToExpPath}' not found in SavedPaths/.");
                    }
                    if (string.IsNullOrWhiteSpace(hunt.ExpLoopPath))
                        errors.Add($"HuntDefinitions[{i}] '{hunt.Name}': ExpLoopPath is empty.");
                    else
                    {
                        string path = hunt.ExpLoopPath;
                        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                            path += ".json";
                        string fullPath = Path.Combine(SAVE_DIR, path);
                        if (!File.Exists(fullPath))
                            errors.Add($"HuntDefinitions[{i}] '{hunt.Name}': ExpLoopPath '{hunt.ExpLoopPath}' not found in SavedPaths/.");
                    }
                }
            }
            else
            {
                // --- Legacy fallback: validate RepotToExpPath / ExpLoopPath ---
                bool hasLegacyRepot = !string.IsNullOrWhiteSpace(profile.RepotToExpPath);
                bool hasLegacyExp = !string.IsNullOrWhiteSpace(profile.ExpLoopPath);

                if (!hasLegacyRepot && !hasLegacyExp)
                {
                    errors.Add("Profile has no HuntDefinitions and no legacy RepotToExpPath/ExpLoopPath. Add at least one hunt.");
                }
                else
                {
                    _log("[BotProfileLoader] WARNING: Profile uses legacy RepotToExpPath/ExpLoopPath. Consider migrating to HuntDefinitions.");

                    if (string.IsNullOrWhiteSpace(profile.RepotToExpPath))
                        errors.Add("RepotToExpPath is empty (legacy fallback).");
                    else
                    {
                        string path = profile.RepotToExpPath;
                        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                            path += ".json";
                        string fullPath = Path.Combine(SAVE_DIR, path);
                        if (!File.Exists(fullPath))
                            errors.Add($"RepotToExpPath '{profile.RepotToExpPath}' not found in SavedPaths/ (legacy fallback).");
                    }

                    if (string.IsNullOrWhiteSpace(profile.ExpLoopPath))
                        errors.Add("ExpLoopPath is empty (legacy fallback).");
                    else
                    {
                        string path = profile.ExpLoopPath;
                        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                            path += ".json";
                        string fullPath = Path.Combine(SAVE_DIR, path);
                        if (!File.Exists(fullPath))
                            errors.Add($"ExpLoopPath '{profile.ExpLoopPath}' not found in SavedPaths/ (legacy fallback).");
                    }
                }
            }

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
    }
}
