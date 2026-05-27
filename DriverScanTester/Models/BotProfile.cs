using System.Collections.Generic;

namespace DriverScanTester.Models
{
    /// <summary>
    /// A bot profile describes segment file names, start areas, and repot thresholds.
    /// It does NOT contain waypoint data — only references to SavedPaths/*.json files.
    /// </summary>
    public class BotProfile
    {
        /// <summary>Display name for this profile.</summary>
        public string Name { get; set; } = "NewProfile";

        /// <summary>Map number of the city this profile is for (informational / validation).</summary>
        public int CityMapNumber { get; set; }

        /// <summary>
        /// One or more start-area -> city-to-repot path mappings.
        /// The first matching area (by current player position) will be used.
        /// </summary>
        public List<StartRoute> StartRoutes { get; set; } = new();

        /// <summary>Filename of the repot -> exp area segment (relative to SavedPaths/).</summary>
        public string RepotToExpPath { get; set; } = "";

        /// <summary>Filename of the exp loop segment (relative to SavedPaths/).</summary>
        public string ExpLoopPath { get; set; } = "";

        // --- Repot thresholds (override RepotDetectorService defaults) ---
        /// <summary>Minimum HP potions before repot is needed.</summary>
        public int MinHpPotions { get; set; } = BotConstants.Repot.DefaultMinHpPotions;
        /// <summary>Minimum mana potions before repot is needed.</summary>
        public int MinManaPotions { get; set; } = BotConstants.Repot.DefaultMinManaPotions;
        /// <summary>Weight ratio (current/max) above which repot is triggered.</summary>
        public float MaxWeightRatio { get; set; } = BotConstants.Repot.DefaultMaxWeightRatio;
        /// <summary>If HP is at or below this value, repot is triggered.</summary>
        public int MinHp { get; set; } = BotConstants.Repot.DefaultMinHp;
        /// <summary>If Mana is at or below this value, repot is triggered.</summary>
        public int MinMana { get; set; } = BotConstants.Repot.DefaultMinMana;

        // --- Workflow options ---
        /// <summary>If true, skip actual RepotSystem.Repot() and just log "dry run".</summary>
        public bool DryRunRepot { get; set; } = false;

        /// <summary>Virtual-key code for town teleport (default 0x36 = '6').</summary>
        public int TeleportKey { get; set; } = BotConstants.Workflow.DefaultTeleportKey;
        /// <summary>Scan code for teleport key (default 0x07 for '6').</summary>
        public int TeleportScanCode { get; set; } = BotConstants.Workflow.DefaultTeleportScanCode;

        /// <summary>Maximum teleport retries before giving up.</summary>
        public int MaxTeleportRetries { get; set; } = BotConstants.Repot.MaxTeleportRetries;

        // --- Window position offset ---
        // All hardcoded mouse coordinates assume the game client area is at screen position (0,0).
        // If your game window is elsewhere (e.g. second monitor, windowed mode), set these offsets.
        // Set to 0,0 to auto-detect from the actual window position via ClientToScreen.
        /// <summary>X offset of the game client area on screen (0 = auto-detect).</summary>
        public int WindowOffsetX { get; set; } = 0;
        /// <summary>Y offset of the game client area on screen (0 = auto-detect).</summary>
        public int WindowOffsetY { get; set; } = 0;
    }

    /// <summary>
    /// Maps a named start area (where the player can appear in the city)
    /// to a specific city->repot path segment file.
    /// </summary>
    public class StartRoute
    {
        /// <summary>Human-readable name, e.g. "StartA", "Temple".</summary>
        public string Name { get; set; } = "";

        /// <summary>The area on the city map where this start route applies.</summary>
        public StartArea Area { get; set; } = new();

        /// <summary>Filename of the segment from this start point to the repot NPC.</summary>
        public string PathFile { get; set; } = "";
    }
}
