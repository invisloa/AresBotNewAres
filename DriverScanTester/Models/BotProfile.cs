using System.Collections.Generic;

namespace DriverScanTester.Models
{
    /// <summary>
    /// A bot profile is a linear FLOW of mixed steps that the bot cycles through:
    /// Repot steps, Path steps (walk a saved segment), Operation steps (run a named
    /// custom operation such as talking to an NPC), and an ExpLoop step (the looping
    /// hunting path that runs until the repot conditions are met).
    ///
    /// Steps are executed top to bottom and wrap around at the end, so the flow cycles
    /// indefinitely. Any step type can be placed anywhere, so a flow can look like:
    ///   Repot → Operation (talk to NPC) → Path (go to exp) → Operation → ExpLoop
    ///
    /// The profile itself does NOT contain waypoint data — only references to
    /// SavedPaths/*.json files and named operations.
    /// </summary>
    public class BotProfile
    {
        /// <summary>Display name for this profile.</summary>
        public string Name { get; set; } = "NewProfile";

        /// <summary>
        /// The ordered flow of steps executed by the workflow. Empty flows are invalid.
        /// </summary>
        public List<BotFlowStep> FlowSteps { get; set; } = new();

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

        // --- Start position check (protection) ---
        /// <summary>
        /// When true, the workflow verifies the player is standing on the profile's
        /// start position (<see cref="StartPositionX"/>/<see cref="StartPositionY"/>)
        /// before the flow starts. If the player is somewhere else, the bot uses the
        /// town teleport scroll, waits for the game to settle (~10 s), then verifies
        /// that the current map matches <see cref="ProtectionMapNumber"/> and that the
        /// player is back on the start position. If the verification fails the bot
        /// stops instead of starting the flow.
        /// </summary>
        public bool EnableStartPositionCheck { get; set; } = false;

        /// <summary>Expected X coordinate of the player when the bot starts.</summary>
        public int StartPositionX { get; set; } = 0;

        /// <summary>Expected Y coordinate of the player when the bot starts.</summary>
        public int StartPositionY { get; set; } = 0;

        /// <summary>
        /// The map number the player must be on after the start teleport (the
        /// "protection" map, e.g. the town map). 0 disables the map check.
        /// </summary>
        public int ProtectionMapNumber { get; set; } = 0;

        /// <summary>
        /// Tolerance in game tiles for the start-position comparison. After a teleport
        /// the game only refreshes the player position memory once the player moves, so
        /// the bot nudges the player a few steps and then accepts any position within
        /// this distance of the start coordinates (default 5 tiles).
        /// </summary>
        public int StartPositionTolerance { get; set; } = 5;

        // --- Loot priority mode ---
        /// <summary>
        /// When true, looting outranks combat and waypoint movement during the ExpLoop step.
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