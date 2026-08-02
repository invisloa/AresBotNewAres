using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace DriverScanTester.Models
{
    /// <summary>
    /// A bot profile describes segment file names and repot thresholds.
    /// It does NOT contain waypoint data — only references to SavedPaths/*.json files.
    /// The profile has exactly one City → Repot route step; every hunt defines its own
    /// Repot → Outside City, Outside City → Exp Spot and Exp Loop route steps.
    /// </summary>
    public class BotProfile
    {
        /// <summary>Display name for this profile.</summary>
        public string Name { get; set; } = "NewProfile";

        /// <summary>The single City → Repot route step (stage 1 of the workflow).</summary>
        public BotRouteStep CityToRepot { get; set; } = new();

        /// <summary>
        /// List of hunt/exp definitions. Each hunt defines the route stages 2-4 of the workflow.
        /// </summary>
        public List<HuntDefinition> HuntDefinitions { get; set; } = new();

        /// <summary>
        /// Name of the default hunt from HuntDefinitions (used when no explicit hunt is selected).
        /// </summary>
        public string DefaultHuntName { get; set; } = "";

        /// <summary>
        /// Returns the default HuntDefinition based on DefaultHuntName, or the first one if not found.
        /// </summary>
        [JsonIgnore]
        public HuntDefinition? DefaultHunt =>
            HuntDefinitions.FirstOrDefault(h => h.Name == DefaultHuntName)
            ?? HuntDefinitions.FirstOrDefault();

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

        // --- Potion buy targets (override RepotSystem defaults) ---
        /// <summary>Target count for HP potions (added to ItemCount1 base).</summary>
        public int HpBuyTarget { get; set; } = BotConstants.Repot.HpBuyTarget;
        /// <summary>Target count for Mana potions (added to ItemCount1 base).</summary>
        public int ManaBuyTarget { get; set; } = BotConstants.Repot.ManaBuyTarget;
        /// <summary>Target count for Red potions (added to ItemCount1 base).</summary>
        public int RedBuyTarget { get; set; } = BotConstants.Repot.RedBuyTarget;
        /// <summary>Target count for White potions (added to ItemCount1 base).</summary>
        public int WhiteBuyTarget { get; set; } = BotConstants.Repot.WhiteBuyTarget;

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
}
