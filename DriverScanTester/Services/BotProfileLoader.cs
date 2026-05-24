using System;
using System.Collections.Generic;
using System.IO;
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
        private const string PROFILE_DIR = "SavedBotProfiles";
        private const string SAVE_DIR = "SavedPaths";
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
        /// Returns null if the file does not exist or cannot be parsed.
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
                _log($"[BotProfileLoader] Loaded profile '{profile.Name}' ({profile.StartRoutes.Count} start routes).");
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

            if (string.IsNullOrWhiteSpace(profile.RepotToExpPath))
                errors.Add("RepotToExpPath is empty.");
            else
            {
                string path = profile.RepotToExpPath;
                if (!path.EndsWith(".json")) path += ".json";
                string fullPath = Path.Combine(SAVE_DIR, path);
                if (!File.Exists(fullPath))
                    errors.Add($"RepotToExpPath '{profile.RepotToExpPath}' not found in SavedPaths/.");
            }

            if (string.IsNullOrWhiteSpace(profile.ExpLoopPath))
                errors.Add("ExpLoopPath is empty.");
            else
            {
                string path = profile.ExpLoopPath;
                if (!path.EndsWith(".json")) path += ".json";
                string fullPath = Path.Combine(SAVE_DIR, path);
                if (!File.Exists(fullPath))
                    errors.Add($"ExpLoopPath '{profile.ExpLoopPath}' not found in SavedPaths/.");
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
