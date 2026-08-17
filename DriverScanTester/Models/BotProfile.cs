using System.Collections.Generic;

namespace DriverScanTester.Models
{
    /// <summary>
    /// A bot profile describes one complete EXP destination as a three-stage workflow:
    ///   1. City → Repot (a list of paths — each repot trip uses the next path in the list),
    ///   2. Go to EXP (an ordered chain of one or more paths, each finishing at its
    ///      last waypoint or when the expected destination map is reached),
    ///   3. EXP Path (one looping path used while hunting).
    /// It does NOT contain waypoint data — only references to SavedPaths/*.json files.
    /// One profile represents exactly one EXP destination; a new EXP location means a new profile.
    /// </summary>
    public class BotProfile
    {
        /// <summary>Display name for this profile.</summary>
        public string Name { get; set; } = "NewProfile";

        // --- Stage 1: REPOT ---
        /// <summary>
        /// Ordered list of paths from the city/player starting position to the repot
        /// location. The bot cycles through this list: every repot trip walks to the
        /// repot using the next path in the list, wrapping back to the first one after
        /// the last, so the repot route is rotated across repots.
        /// </summary>
        public List<BotRouteStep> CityToRepotPaths { get; set; } = new();

        // --- Stage 2: GO TO EXP ---
        /// <summary>
        /// Ordered list of travel paths from the repot location to the EXP position.
        /// Each path completes independently by its final waypoint (FinalWaypoint) or
        /// when the expected destination map is reached (ExpectedMapReached).
        /// </summary>
        public List<TravelRouteStep> TravelToExpRoutes { get; set; } = new();

        // --- Stage 3: EXP PATH ---
        /// <summary>The single looping hunting path; stops when the repot conditions are met.</summary>
        public BotRouteStep ExpLoop { get; set; } = new();

        // --- Custom operations ---
        /// <summary>
        /// Ordered list of built-in custom operation names (see BotOperations) to run
        /// once the player has arrived at the EXP map, before the EXP loop starts.
        /// E.g. talking to an NPC that grants access to the hunting area.
        /// Empty entries are ignored; unknown names fail the workflow, never silently skip.
        /// </summary>
        public List<string> PreExpOperations { get; set; } = new();

        // --- Repot thresholds (override RepotDetectorService defaults) ---
        /// <summary>Minimum HP potions before repot is needed.</summary>
        public int MinHpPotions { get; set; } = BotConstants.Repot.DefaultMinHpPotions;
        /// <summary>Minimum mana potions before repot is needed.</summary>
        public int MinManaPotions { get; set; } = BotConstants.Repot.DefaultMinManaPotions;
        /// <summary>Weight ratio (current/max) above which repot is triggered.</summary>
        public float MaxWeightRatio { get; set; } = BotConstants.Repot.DefaultMaxWeightRatio;
        /// <summary>HP floor: the heal bot drinks an HP potion when HP drops below this.
        /// Repot is only triggered at/below this value when HP potions are exhausted.</summary>
        public int MinHp { get; set; } = BotConstants.Repot.DefaultMinHp;
        /// <summary>Mana floor: the heal bot drinks a mana potion when mana drops below this.
        /// Repot is only triggered at/below this value when mana potions are exhausted.</summary>
        public int MinMana { get; set; } = BotConstants.Repot.DefaultMinMana;

        // --- Potion buy targets (override RepotSystem defaults) ---
        /// <summary>Target count for HP potions (buy up to this many).</summary>
        public int HpBuyTarget { get; set; } = BotConstants.Repot.HpBuyTarget;
        /// <summary>Target count for Mana potions (buy up to this many).</summary>
        public int ManaBuyTarget { get; set; } = BotConstants.Repot.ManaBuyTarget;
        /// <summary>Target count for Red potions (buy up to this many).</summary>
        public int RedBuyTarget { get; set; } = BotConstants.Repot.RedBuyTarget;
        /// <summary>Target count for White potions (buy up to this many).</summary>
        public int WhiteBuyTarget { get; set; } = BotConstants.Repot.WhiteBuyTarget;

        // --- Workflow options ---
        /// <summary>If true, skip actual RepotSystem.Repot() and just log "dry run".</summary>
        public bool DryRunRepot { get; set; } = false;

        // --- Loot priority mode ---
        /// <summary>
        /// When true, this profile is a loot-priority profile: looting outranks combat
        /// and waypoint movement. The bot scans for ground loot even while a mob is
        /// selected / being attacked; as soon as a loot item is found the bot goes to
        /// loot it, and while the bot walks to loot it performs NO attack actions and
        /// NO movement toward the next waypoint. Looting all items is the top priority.
        /// </summary>
        public bool LootPriority { get; set; } = false;

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
}
