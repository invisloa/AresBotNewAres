// <copyright file="BotConstants.cs" company="DriverScanTester">
//     Copyright (c) DriverScanTester. All rights reserved.
// </copyright>

using System;

namespace DriverScanTester
{
    // Buckerty blade BuckertyBlade 2530



    /// <summary>
    /// Central repository for all hardcoded bot constants — camera values, speeds,
    /// delays, thresholds, keyboard codes, scan coordinates, etc.
    ///
    /// Change any value here and it takes effect everywhere it's referenced.
    /// Grouped by category for easy navigation.
    /// </summary>
    public static class BotConstants
    {
        // ════════════════════════════════════════════════════════════════
        //  CAMERA — distances (game units), angles, vertical, bearing
        // ════════════════════════════════════════════════════════════════
        public static class Camera
        {
            /// <summary>Default camera distance lock for waypoints and movement.</summary>
            public const short DefaultDistanceLock = 17020;

            /// <summary>Default camera vertical lock value, matching the sell-view vertical (16310).</summary>
            public const short DefaultVerticalLock = 16310;

            /// <summary>Very low (closest) camera distance used during combat retarget very-low-search phase.</summary>
            public const short CombatRetargetVeryLowDistance = 16880;

            /// <summary>Lower camera distance used during combat retarget low-search phase.</summary>
            public const short CombatRetargetLowDistance = 16910;

            /// <summary>Mid camera distance used during combat retarget mid-search phase.</summary>
            public const short CombatRetargetMidDistance = 16950;

            /// <summary>Minimum absolute circular difference (game-angle units) to allow a camera update (deadband).</summary>
            public const float DeadbandGameUnits = 2.0f;

            /// <summary>If circular diff exceeds this threshold, update immediately (bypass cooldown).</summary>
            public const float ForceUpdateGameUnits = 8.0f;

            /// <summary>Minimum absolute circular difference (radians) to allow a camera update (deadband). Equivalent to the old 2-game-unit threshold (≈ 2.8°) when the legacy full spin was 257 game units.</summary>
            public const float DeadbandRadians = 0.0489f;

            /// <summary>If circular diff exceeds this threshold, update immediately (bypass cooldown). Equivalent to the old 8-game-unit threshold (≈ 11.2°) when the legacy full spin was 257 game units.</summary>
            public const float ForceUpdateRadians = 0.1956f;

            /// <summary>Minimum interval in ms between camera updates for small/medium changes.</summary>
            public const double MinUpdateIntervalMs = 200.0;

            /// <summary>Base freeze distance for heading lock near waypoints. Actual = max(reachThreshold*2, this).</summary>
            public const float HeadingFreezeDistanceBase = 10.0f;
        }

        // ════════════════════════════════════════════════════════════════
        //  CAMERA ANGLE — bit-patterns of the exact float radians stored at
        //  [Ares.exe + 0x4704B0] + 0x1A8 for each cardinal direction.
        //  These are the ONLY authoritative target values for writing the
        //  camera angle via the int bit-pattern helper.
        //  Source: exact measurement of full 4-byte float at +0x1A8.
        //  Reading upper-2-bytes (+0x1AA) or partial-byte values like 16585
        //  is incorrect — those are only fragments of the 32-bit float and
        //  introduce multi-degree error and float corruption.
        // ════════════════════════════════════════════════════════════════
        public static class CameraAngleBits
        {
            /// <summary>0° (North) — 0x40C90FDB = 2π, normalised to 0°.</summary>
            public const int North_0Deg   = 1086918619;

            /// <summary>90° (East) — 0x40FB53D1 = 2π + π/2, normalised to 90°.</summary>
            public const int East_90Deg   = 1090212817;

            /// <summary>180° (South) — 0x4116CBE4 = 2π + π, normalised to 180°.</summary>
            public const int South_180Deg = 1092013028;

            /// <summary>270° (West) — 0x412FEDDF = 2π + 1.5π, normalised to 270°.</summary>
            public const int West_270Deg  = 1093660127;

            /// <summary>360° (full turn / North again) — 0x41490FDB = 4π, normalised to 360°.</summary>
            public const int North_360Deg = 1095307227;
        }

        // ════════════════════════════════════════════════════════════════
        //  CARDINAL DIRECTIONS — exact float baselines for the math helper
        //  that converts a bearing (degrees from North) into a camera-angle
        //  radian float. N is the EXACT value at +0x1A8 for 0° (i.e. 2π).
        //  E/S/W are derived using Math.PI — they line up with the
        //  bit-patterns in CameraAngleBits above.
        // ════════════════════════════════════════════════════════════════
        public static class CardinalDirections
        {
            /// <summary>North baseline in game camera radians (exact 2π, derived from CameraAngleBits.North_0Deg).</summary>
            public static readonly float N = BitConverter.Int32BitsToSingle(CameraAngleBits.North_0Deg);

            /// <summary>East direction in game camera radians (N + π/2).</summary>
            public static readonly float E = N + (float)(Math.PI / 2);

            /// <summary>South direction in game camera radians (N + π).</summary>
            public static readonly float S = N + (float)Math.PI;

            /// <summary>West direction in game camera radians (N + 1.5π) — 270°, NOT 240°.</summary>
            public static readonly float W = N + (float)(Math.PI * 1.5);
        }

        // ════════════════════════════════════════════════════════════════
        //  SPEED & POTIONS — attack-speed threshold, potion key delays
        // ════════════════════════════════════════════════════════════════
        public static class SpeedPotion
        {
            /// <summary>Attack speed value at which bot drinks speed potions (key 7 + key 8).</summary>
            public const short AttackSpeedThreshold = 16384;

            /// <summary>Interval in seconds between attack-speed checks.</summary>
            public const double CheckIntervalSeconds = 5.0;

            /// <summary>Delay in ms after pressing potion key 7 before pressing key 8.</summary>
            public const int PostPotionDelayMs = 500;
        }

        // ════════════════════════════════════════════════════════════════
        //  COMBAT — attack values, idle timeout, stuck thresholds
        // ════════════════════════════════════════════════════════════════
        public static class Combat
        {
            /// <summary>Animation1 value below which the player is considered attacked (knight).</summary>
            public const int KnightAttackedMin = 17300;

            /// <summary>Animation1 value above which the player is considered attacked (knight).</summary>
            public const int KnightAttackedMax = 17400;

            /// <summary>Seconds of idle (action=0) before declaring the mob dead/stuck.</summary>
            public const double IdleTimeoutSeconds = 0.5;

            /// <summary>Minimum interval in seconds between TAB presses while in move+attack mode.</summary>
            public const double MoveModeTabIntervalSeconds = 0.8;

            /// <summary>Default distance at which the bot disengages attack from a waypoint.</summary>
            public const short DefaultAttackDisengageDistance = 60;

            /// <summary>
            /// Maximum target ID value considered a valid mob/NPC.
            /// Values above this threshold are player characters and should be skipped.
            /// Ported from AresTrainerV3_oldBot (isMobSelected &lt; 8300000 = mob, > 8300000 = player).
            /// </summary>
            public const int MaxMobTargetId = 8300000;
        }

        // ════════════════════════════════════════════════════════════════
        //  MOVEMENT — ghost waypoints, stuck, thresholds, camera filters
        // ════════════════════════════════════════════════════════════════
        public static class Movement
        {
            /// <summary>Distance tolerance for matching a waypoint as a "ghost" (duplicate).</summary>
            public const float GhostMatchEpsilon = 0.35f;

            /// <summary>Minimum reach threshold for ghost waypoints (overrides precision).</summary>
            public const float GhostReachThreshold = 7.0f;

            /// <summary>Sentinel value meaning "no bearing set".</summary>
            public const float UnsetBearing = -999f;

            /// <summary>Grace period in seconds after pressing W during which stuck detection is ignored.</summary>
            public const double StuckGraceAfterStartSeconds = 1.25;

            /// <summary>Extra distance added to waypoint reach threshold for near-target stuck ignore.</summary>
            public const float NearTargetStuckIgnoreExtra = 1.0f;

            /// <summary>Soft radius around the final waypoint for early goal completion.</summary>
            public const float FinalGoalSoftRadius = 3.0f;

            /// <summary>Seconds of stall inside FinalGoalSoftRadius before declaring goal reached.</summary>
            public const double FinalGoalStallTime = 2.0;

            /// <summary>Size of local navigation map cells in game tiles.</summary>
            public const float LocalMapCellSize = 1.0f;

            /// <summary>Rate-limit interval in ms for ForceStartMoving calls.</summary>
            public const double ForceStartMinIntervalMs = 700.0;

            /// <summary>Maximum tick-to-tick displacement before resetting progress tracker (position jump guard).</summary>
            public const float MaxReasonableTickMovement = 8.0f;

            /// <summary>Minimum effective reach threshold for Exact-precision waypoints.</summary>
            public const float ExactMinThreshold = 1.5f;

            /// <summary>Minimum reach threshold before camera heading freeze kicks in.</summary>
            public const float HeadingFreezeReachBase = 10.0f;

            /// <summary>Default MovementPrecision for waypoints when not specified.</summary>
            public const int DefaultPrecision = 12; // Medium = 12

            /// <summary>Maximum distance from a route segment for resync; beyond this, fallback to nearest waypoint.</summary>
            public const float RouteResyncMaxSegmentDistance = 8.0f;

            /// <summary>Distance threshold for considering player "near" a waypoint (used in non-loop final segment guard).</summary>
            public const float RouteResyncNearWaypointDistance = 3.0f;

            /// <summary>Segment-progress threshold above which the bot skips to the next segment (t >= this → skip).</summary>
            public const float RouteResyncVeryCloseToSegmentEndT = 0.85f;

            /// <summary>Distance epsilon for segment tie-breaking: if two segments have dist within this value, prefer the one closer to current target.</summary>
            public const float RouteResyncTieDistanceEpsilon = 1.0f;
        }

        // ════════════════════════════════════════════════════════════════
        //  WAYPOINT REACH THRESHOLDS — per precision level (game tiles)
        // ════════════════════════════════════════════════════════════════
        public static class WaypointThresholds
        {
            /// <summary>Distance at which an Exact-precision waypoint is considered reached.</summary>
            public const float Exact = 1.25f;

            /// <summary>Distance at which an Accurate-precision waypoint is considered reached.</summary>
            public const float Accurate = 2.0f;

            /// <summary>Distance at which a Medium-precision waypoint is considered reached.</summary>
            public const float Medium = 5.0f;

            /// <summary>Distance at which a High-precision waypoint is considered reached.</summary>
            public const float High = 8.0f;
        }

        // ════════════════════════════════════════════════════════════════
        //  GAME MEMORY OFFSETS — addresses for reading/writing game data
        // ════════════════════════════════════════════════════════════════
        public static class MemoryOffsets
        {
            // Player structure
            public const ulong PlayerPtr = 0x471C88;
            public const ulong MobSelectedPtr2 = 0x3F4D4C;
            public const ulong MobSelectedSub2 = 0x9D;
            public const ulong MobSelected2 = 0x60;
            public const ulong X = 0x144;
            public const ulong Y = 0xEE8;
            public const ulong Hp = 0x168;
            public const ulong MaxHp = 0x16C;
            public const ulong Mana = 0xC58;
            public const ulong MaxMp = 0xC5C;
            public const ulong IsInCity = 0x5F4;
            public const ulong MapNumber = 0x5F0;
            public const ulong CurrentMap = 0x5F8;
            public const ulong CurrentWeight = 0xC70;
            public const ulong MaxWeight = 0xC74;
            public const ulong RunSpeed = 0x150A;
            public const ulong SkillSpeed = 0xACA;
            public const ulong Animation1 = 0x3ba;
            public const ulong Animation2 = 0x3be;
            public const ulong InventorySlotHPCount = 0xF20;
            public const ulong ManaPotionsCount = 0xF40;
            public const ulong WhitePotionsCount = 0xF60;
            public const ulong RedPotionsCount = 0xF80;
            public const ulong InventoryFirstSlotSellValue = 0x191A;
            public const ulong FirstSellSlotPtr = 0xF1A;
            public const ulong TargetSelected = 0x60;
            public const ulong AttackSpeed1 = 0x47A;
            public const ulong AttackSpeed2 = 0x47E;
            public const ulong CurrentAction = 0x3B0;

            // Camera structure
            public const ulong CameraPtr = 0x4704B0;
            public const ulong CameraDistance = 0x1a6;
            // Horizontal camera yaw (full 32-bit float) lives at +0x1A8.
            // Do NOT read it from +0x1AA — that is only the upper 16 bits of
            // the same float and produces partial/corrupt values.
            public const ulong CameraAngle = 0x1A8;
            // Legacy/placeholder — the true vertical pitch address is not
            // verified in the new bot. Kept as a distinct value to avoid
            // accidentally aliasing the horizontal yaw.
            public const ulong CameraVerticalAngle = 0x1B0;

            // UI / Window
            public const ulong BaseNormalM = 0x471C88;
            public const ulong UiWindowM = 0x471CA8;
            public const int ShopWindow2M = 0x94;
            public const int ShopWindow1 = 0x100;
            public const int InventoryOpen = 0xE8;
            public const int SellerWindow2M = 0xac;
            public const int InventoryWindow2M = 0x60;
            public const int StorageWindow2M = 0x94;
            public const int ShopWindow2 = 0xd8;
            public const int SellerWindow1 = 0xc0;
            public const int StorageWindow1 = 0xc0;
            public const int StorageWindow2 = 0xd8;
            public const int InventoryWindow1 = 0xc0;
            public const int InventoryWindow2 = 0xd8;
            public const int InventoryCurrentTab = 0x110;
            public const int InventoryCurrentTabM = 0x2AD2EC;
            public const ulong InventoryTabSelectedPtr = 0x4CAEE8;
            public const int InventoryTabSelectedSub1 = 0x9;
            public const int InventoryTabSelectedSub2 = 0x36B;
            public const int SellWindow = 0xc0;
            public const ulong SellWindowM = 0x2AD308;
            public const ulong SellConfirmWindowPtr = 0x471C98;
            public const int DeleteWindow = 0x138c;
            public const int SellItemSelected = 0x12e;
            public const int SlotHP = 0xbb2;
            public const int SlotManna = 0xbce;
            public const int SlotRedPot = 0xbea;
            public const int SlotWhitePot = 0xc06;
            public const int SlotFirstSell = 0xF1A;
            public const int SlotFirstStorageValue = 0x116;
            public const ulong InventoryTestPtr = 0x242CB5;
            public const ulong InventoryTestSub = 0xF0;

            // Loot
            public const ulong CurrentItemHighlightedType = 0x8C9Fd0;
            public const int PositionX = 0x23c;
            public const int PositionY = 0x244;

            // Heal/Mana
            public const ulong HealManaBasePtr2 = 0x8A3DA8;
            public const ulong HealManaOffset2 = 0xC58;
            public const ulong HealManaBasePtr1 = 0x5C48CB;
            public const ulong HealManaOffset1 = 0x2C8;

            // Mouseover / NPC highlight
            public const ulong IsNpcMousePointedPtr = 0x471C84;
            public const ulong IsNpcMousePointed = 0x7C;

            // Seller mouseover (S_IsSellerPointed)
            // Pointer chain: [Ares.exe + <IsSellerPointedPtr>] + <IsSellerPointed>
            // Expected value when the mouse is pointing at the seller/NPC: 143850200.
            public const ulong IsSellerPointedPtr = 0x4704A8;
            public const ulong IsSellerPointed = 0xC;

            // Camera vertical "lock" used by the sell view (16-bit value, e.g. 16310).
            // Distinct from the float vertical angle at +0x1B0 (CameraVerticalAngle).
            public const ulong CameraVerticalLockOffset = 0x1BE;
        }

        // ════════════════════════════════════════════════════════════════
        //  GAME MAGIC VALUES — item IDs, potion counts, sell exclusion
        // ════════════════════════════════════════════════════════════════
        public static class GameMagicValues
        {
            /// <summary>Base item count value for potion counting.</summary>
            public const int ItemCount1 = 16777217;

            /// <summary>Item count value for mana potions.</summary>
            public const int MannaPotionsCountValue = 16777257;

            /// <summary>Item count value for white potions.</summary>
            public const int WhitePotionsCountValue = 16777222;

            /// <summary>Item count value for red potions.</summary>
            public const int RedPotionsCountValue = 16777222;

            /// <summary>Item types that should NOT be sold. (0 = empty slot)</summary>
            public static readonly int[] ItemsNotForSale = { 0, 246, 247, 1092, 1093, 1094, 1095, 3093 };

            /// <summary>Value of L_LootSelectedItem1 (read as clong / 32-bit) when a loot item is under the mouse cursor.
            /// Updated from 16344 (16-bit) to 99448280 (32-bit) to 138876240 per user observation.</summary>
            public const int LootMouseOverValue = 138876240;

            /// <summary>Item type identifier for SOD items (no longer readable — kept for reference).</summary>
            public const int Sod = -13799;

            /// <summary>Item type identifier for SOP items (no longer readable — kept for reference).</summary>
            public const int Sop = 32627;

            /// <summary>Item types that are event/snowman items (not to be sold).</summary>
            public static readonly int[] ItemValuesEventSnowman = { 9220, 9261, 9262, 9263, 9264, 9265, 9266, 9267 };

            /// <summary>Size of each inventory/storage slot in bytes.</summary>
            public const int InventorySlotSize = 0x20;

            /// <summary>Number of inventory slots per tab.</summary>
            public const int SlotsPerInventoryTab = 36;

            /// <summary>Total inventory slots (both tabs).</summary>
            public const int TotalInventorySlots = 72;

            /// <summary>Total storage slots.</summary>
            public const int TotalStorageSlots = 98;
        }

        // ════════════════════════════════════════════════════════════════
        //  HEAL / MANA — key codes and thresholds
        // ════════════════════════════════════════════════════════════════
        public static class HealMana
        {
            /// <summary>Virtual-key code for key '1' (HP potion).</summary>
            public const int Vk1 = 0x31;

            /// <summary>Virtual-key code for key '2' (MP potion).</summary>
            public const int Vk2 = 0x32;

            /// <summary>Scan code for key '1'.</summary>
            public const byte ScanCode1 = 0x02;

            /// <summary>Scan code for key '2'.</summary>
            public const byte ScanCode2 = 0x03;

            /// <summary>HP threshold below which key '1' is pressed.</summary>
            public const short HpThreshold = 666;

            /// <summary>MP threshold below which key '2' is pressed.</summary>
            public const short MpThreshold = 150    ;

            /// <summary>Delay in ms between key down and key up events.</summary>
            public const int KeyPressDelayMs = 50;
        }

        // ════════════════════════════════════════════════════════════════
        //  KEYBOARD — virtual-key codes, scan codes, event flags
        // ════════════════════════════════════════════════════════════════
        public static class Keyboard
        {
            /// <summary>KEYEVENTF_KEYUP flag for keybd_event.</summary>
            public const int KeyEventKeyUp = 0x0002;

            // W key
            public const byte VkW = 0x57;
            public const byte ScanW = 0x11;

            // TAB key
            public const byte VkTab = 0x09;
            public const byte ScanTab = 0x0F;

            // Key 3 (attack skill)
            public const byte Vk3 = 0x33;
            public const byte Scan3 = 0x04;

            // Key 7 (potion 1)
            public const byte Vk7 = 0x37;
            public const byte Scan7 = 0x08;

            // Key 8 (potion 2)
            public const byte Vk8 = 0x38;
            public const byte Scan8 = 0x09;

            // Key 6 (town teleport)
            public const byte Vk6 = 0x36;
            public const byte Scan6 = 0x07;

            // A key (strafe left)
            public const byte VkA = 0x41;
            public const byte ScanA = 0x1E;

            // D key (strafe right)
            public const byte VkD = 0x44;
            public const byte ScanD = 0x20;

            // Escape key
            public const byte VkEscape = 0x1B;
            public const byte ScanEscape = 0x01;

            // Space key
            public const byte VkSpace = 0x20;
            public const byte ScanSpace = 0x39;

            /// <summary>Delay in ms between key down and key up in PressKey().</summary>
            public const int PressKeyGapMs = 20;
        }

        // ════════════════════════════════════════════════════════════════
        //  DELAYS / TIMEOUTS — all sleep and delay values in ms
        // ════════════════════════════════════════════════════════════════
        public static class Delays
        {
            // ── Combat delays ──
            /// <summary>Delay after pressing TAB for target selection.</summary>
            public const int TabRetargetMs = 100;

            /// <summary>Delay after pressing TAB while in combat retarget.</summary>
            public const int CombatRetargetTabMs = 100;

            /// <summary>Short wait during combat attack loop.</summary>
            public const int CombatAttackWaitMs = 30;

            /// <summary>Brief delay after potions used in combat.</summary>
            public const int PotionsUsedMs = 50;

            /// <summary>Delay before attempting combat retarget TAB press.</summary>
            public const int PreTabWaitMs = 50;

            /// <summary>Delay after setting camera during retarget before TAB.</summary>
            public const int PostCameraRetargetMs = 100;

            // ── Movement delays ──
            /// <summary>W key release gap in ms during ForceStartMoving.</summary>
            public const int ForceStartReleaseGapMs = 25;

            /// <summary>Main loop delay between ticks in BotWorkflowCoordinator.</summary>
            public const int WorkflowMainLoopMs = 50;

            /// <summary>Main loop delay between ticks in PathRunnerService.</summary>
            public const int PathRunnerTickMs = 100;

            /// <summary>Delay when bot is in Failed state before re-checking.</summary>
            public const int FailedStateMs = 1000;

            /// <summary>Default phase transition delay.</summary>
            public const int DefaultPhaseMs = 100;

            // ── Repot delays ──
            /// <summary>Initial delay before opening shop.</summary>
            public const int OpenShopInitialMs = 1000;

            /// <summary>Delay between shop-open retry attempts.</summary>
            public const int OpenShopRetryMs = 500;

            /// <summary>Delay between potion buy operations.</summary>
            public const int BuyPotionIntervalMs = 1000;

            /// <summary>Delay after clicking to buy a potion stack.</summary>
            public const int PostBuyClickMs = 500;

            /// <summary>Delay between key presses during potion quantity input.</summary>
            public const int PotionQuantityKeyMs = 500;

            /// <summary>Delay before clicking OK in buy window.</summary>
            public const int PreBuyOkMs = 300;

            /// <summary>Delay after clicking OK in buy window.</summary>
            public const int PostBuyOkMs = 500;

            /// <summary>Delay between right-click and sell confirmation.</summary>
            public const int SellClickGapMs = 200;

            /// <summary>Delay before checking sell window again.</summary>
            public const int SellConfirmRetryMs = 100;

            /// <summary>Delay between sell confirmation clicks.</summary>
            public const int SellConfirmClickMs = 50;

            /// <summary>Delay for repot button/key press.</summary>
            public const int RepotKeyPressMs = 50;

            /// <summary>Delay after repot key press.</summary>
            public const int PostRepotKeyMs = 200;

            // ── Loot delays ──
            /// <summary>Loot system update interval.</summary>
            public const int LootUpdateMs = 10;

            /// <summary>Delay after left-click down during collection.</summary>
            public const int CollectClickHoldMs = 50;

            /// <summary>Delay after left-click up (wait for animation).</summary>
            public const int CollectAnimationMs = 500;

            /// <summary>Interval between spacebar presses for area loot.</summary>
            public const int LootSpacePressMs = 300;

            // ── Teleport delays ──
            /// <summary>Delay after pressing teleport key.</summary>
            public const int TeleportKeyDownMs = 50;

            /// <summary>Delay between teleport-wait loop iterations.</summary>
            public const int TeleportWaitIterationMs = 500;

            /// <summary>Number of teleport-wait iterations (~20 seconds total).</summary>
            public const int TeleportWaitIterations = 40;

            // ── Exp loop ──
            /// <summary>Interval in ms between repot condition checks during exp loop.</summary>
            public const int ExpLoopRepotCheckIntervalMs = 3000;

            // ── Misc ──
            /// <summary>Key press gap for heal/mana system.</summary>
            public const int HealManaKeyPressMs = 50;
        }

        // ════════════════════════════════════════════════════════════════
        //  TIMEOUTS — time-based thresholds in seconds
        // ════════════════════════════════════════════════════════════════
        public static class Timeouts
        {
            /// <summary>Teleport wait timeout before assuming arrival failed.</summary>
            public const double TeleportWaitSeconds = 15.0;

            /// <summary>Maximum age of a healthy bearing for reuse in reverse-diagonal recovery.</summary>
            public const double HealthyBearingMaxAgeSeconds = 5.0;


        }

        // ════════════════════════════════════════════════════════════════
        //  ACTION VALUES — game action byte constants for movement states
        // ════════════════════════════════════════════════════════════════
        public static class ActionValues
        {
            /// <summary>Action byte indicating the character is idle.</summary>
            public const byte Idle = 0;

            /// <summary>Action byte indicating idle/stuck state (knight without weapon).</summary>
            public const byte IdleOrStuck1 = 25;

            /// <summary>Action byte indicating idle/stuck state (general).</summary>
            public const byte IdleOrStuck2 = 1;

            /// <summary>Action byte indicating running (with weapon).</summary>
            public const byte RunningWeapon = 27;

            /// <summary>Action byte indicating running (without weapon).</summary>
            public const byte RunningNoWeapon = 3;

            /// <summary>Action byte indicating being hit (with weapon).</summary>
            public const byte HitWeapon = 28;

            /// <summary>Action byte indicating attacking (with weapon).</summary>
            public const byte Attacking = 39;

            /// <summary>Action byte indicating being hit (without weapon).</summary>
            public const byte HitNoWeapon = 4;

            /// <summary>Action byte indicating attacking (without weapon).</summary>
            public const byte AttackingNoWeapon = 7;
        }

        // ════════════════════════════════════════════════════════════════
        //  REVERSE-DIAGONAL RECOVERY
        // ════════════════════════════════════════════════════════════════
        public static class ReverseDiagonal
        {
            /// <summary>Duration per reverse-diagonal attempt in ms.</summary>
            public const int AttemptMs = 200;

            /// <summary>Maximum number of reverse-diagonal attempts before escalation.</summary>
            public const int MaxAttempts = 4;

            /// <summary>Minimum displacement (game tiles) to consider an attempt successful.</summary>
            public const float SuccessDisplacement = 0.60f;

            /// <summary>Minimum distance-to-target improvement (tiles) for success.</summary>
            public const float SuccessDistImprove = 0.50f;

            /// <summary>Bearing offsets applied sequentially to the base bearing.</summary>
            public static readonly float[] AttemptOffsets = { 135f, -135f, 150f, -150f };
        }

        // ════════════════════════════════════════════════════════════════
        //  OBSTACLE — hardcoded obstacle geometry
        // ════════════════════════════════════════════════════════════════
        public static class Obstacle
        {
            /// <summary>Center of the known obstacle (world coordinates).</summary>
            public static readonly (float X, float Y) Center = (4900, 5200);

            /// <summary>Half-size of the obstacle square in game tiles.</summary>
            public const float Size = 200;
        }

        // ════════════════════════════════════════════════════════════════
        //  REPOT — potion thresholds, buy quantities, detection
        // ════════════════════════════════════════════════════════════════
        public static class Repot
        {
            /// <summary>Minimum HP potion count before triggering repot (internal helper).</summary>
            public const int MinHpPotionsInternal = 5;

            /// <summary>Minimum mana potion count before triggering repot (internal helper).</summary>
            public const int MinManaPotionsInternal = 5;

            /// <summary>Default minimum HP potions for RepotDetectorService.</summary>
            public const int DefaultMinHpPotions = 10;

            /// <summary>Default minimum mana potions for RepotDetectorService.</summary>
            public const int DefaultMinManaPotions = 10;

            /// <summary>Default weight ratio threshold (current/max).</summary>
            public const float DefaultMaxWeightRatio = 0.85f;

            /// <summary>Default HP threshold for repot trigger.</summary>
            public const int DefaultMinHp = 500;

            /// <summary>Default Mana threshold for repot trigger.</summary>
            public const int DefaultMinMana = 100;

            /// <summary>Mana potion target count (added to ItemCount1 base).</summary>
            public const int ManaBuyTarget = 99;

            /// <summary>Red potion target count (added to ItemCount1 base).</summary>
            public const int RedBuyTarget = 3;

            /// <summary>White potion target count (added to ItemCount1 base).</summary>
            public const int WhiteBuyTarget = 3;

            /// <summary>HP potion target count (added to ItemCount1 base).</summary>
            public const int HpBuyTarget = 120;

            /// <summary>Maximum teleport retries before giving up.</summary>
            public const int MaxTeleportRetries = 3;

            /// <summary>Maximum retries for opening shop window.</summary>
            public const int OpenShopRetries = 10;
        }

        // ════════════════════════════════════════════════════════════════
        //  LOOT / PIXEL SCAN — screen coordinates and regions
        // ════════════════════════════════════════════════════════════════
        public static class Loot
        {
            /// <summary>Small scan region X range. Centered on the character
            /// (was previously shifted +50 to the right).</summary>
            public static readonly int[] SmallScanX = { 800, 1120 };

            /// <summary>Small scan region Y range. Centered on the character
            /// at old-screen-abs (964, 503).</summary>
            public static readonly int[] SmallScanY = { 378, 628 };

            /// <summary>Big scan region X range.</summary>
            public static readonly int[] BigScanX = { 550, 1360 };

            /// <summary>Big scan region Y range.</summary>
            public static readonly int[] BigScanY = { 290, 760 };

            /// <summary>Character exclude zone — min X. Centered on the character.</summary>
            public const int ExcludeXMin = 880;

            /// <summary>Character exclude zone — max X.</summary>
            public const int ExcludeXMax = 1040;

            /// <summary>Character exclude zone — min Y. Centered on the character
            /// at old-screen-abs Y=503.</summary>
            public const int ExcludeYMin = 418;

            /// <summary>Character exclude zone — max Y.</summary>
            public const int ExcludeYMax = 588;

            /// <summary>Bitmap width for screen capture.</summary>
            public const int BitmapWidth = 1370;

            /// <summary>Bitmap height for screen capture.</summary>
            public const int BitmapHeight = 840;

            /// <summary>Unbug click X coordinate when position hasn't changed after collecting.</summary>
            public const int UnbugClickX = 900;

            /// <summary>Unbug click Y coordinate.</summary>
            public const int UnbugClickY = 600;

            /// <summary>Under-character scan start X.</summary>
            public const int UnderCharScanStartX = 930;

            /// <summary>Under-character scan end X.</summary>
            public const int UnderCharScanEndX = 980;

            /// <summary>Under-character scan step X.</summary>
            public const int UnderCharScanStepX = 5;

            /// <summary>Under-character scan start Y.</summary>
            public const int UnderCharScanStartY = 500;

            /// <summary>Under-character scan end Y.</summary>
            public const int UnderCharScanEndY = 545;

            /// <summary>Under-character scan step Y.</summary>
            public const int UnderCharScanStepY = 5;
        }

        // ════════════════════════════════════════════════════════════════
        //  UI / WINDOW — main window defaults, hotkeys
        // ════════════════════════════════════════════════════════════════
        public static class Ui
        {
            /// <summary>Default HP threshold in UI.</summary>
            public const int DefaultHpThreshold = 250;

            /// <summary>Hotkey virtual-key for pointer scan (F1).</summary>
            public const int PointerScanHotkeyVk = 0x70;

            /// <summary>Hotkey ID for pointer scan.</summary>
            public const int PointerScanHotkeyId = 9000;

            /// <summary>Window width for main bot window.</summary>
            public const int WindowWidth = 800;

            /// <summary>Window height for main bot window.</summary>
            public const int WindowHeight = 500;
        }

        // ════════════════════════════════════════════════════════════════
        //  BOT WORKFLOW — fallback path names, profile defaults
        // ════════════════════════════════════════════════════════════════
        public static class Workflow
        {
            /// <summary>Fallback city-to-repot path file name (no profile).</summary>
            public const string FallbackCityToRepot = "Kharon_StartA_ToRepot.json";

            /// <summary>Fallback repot-to-exp path file name (no profile).</summary>
            public const string FallbackRepotToExp = "Kharon_Repot_To_Wolves.json";

            /// <summary>Fallback exp-loop path file name (no profile).</summary>
            public const string FallbackExpLoop = "Kharon_Wolves_ExpLoop.json";

            /// <summary>Default teleport key virtual-key code ('6').</summary>
            public const int DefaultTeleportKey = 0x36;

            /// <summary>Default teleport key scan code.</summary>
            public const int DefaultTeleportScanCode = 0x07;
        }

        // ════════════════════════════════════════════════════════════════
        //  LOCK / SCAN PRIORITY — intervals per priority level
        // ════════════════════════════════════════════════════════════════
        public static class LockPriority
        {
            /// <summary>Scan interval in ms for Low priority.</summary>
            public const int LowScanInterval = 10_000;

            /// <summary>Scan interval in ms for Mid priority.</summary>
            public const int MidScanInterval = 1_000;

            /// <summary>Scan interval in ms for High priority.</summary>
            public const int HighScanInterval = 100;

            /// <summary>Lock write interval in ms for Low priority.</summary>
            public const int LowLockInterval = 10_000;

            /// <summary>Lock write interval in ms for Mid priority.</summary>
            public const int MidLockInterval = 100;

            /// <summary>Lock write interval in ms for High priority.</summary>
            public const int HighLockInterval = 1;
        }

        // ════════════════════════════════════════════════════════════════
        //  LOCAL NAVIGATION MAP — cell confidence thresholds
        // ════════════════════════════════════════════════════════════════
        public static class NavMap
        {
            /// <summary>Cell size in world units.</summary>
            public const float CellSize = 1.0f;

            /// <summary>Confidence threshold for marking a cell as Blocked (>= this).</summary>
            public const int BlockedConfidenceThreshold = 3;

            /// <summary>Maximum confidence value for Free cells.</summary>
            public const int MaxFreeConfidence = 10;

            /// <summary>Default confidence increment.</summary>
            public const int ConfidenceIncrement = 1;

            /// <summary>Confidence value for initial MarkBlocked.</summary>
            public const int MinBlockedConfidence = 3;
        }
    }
}
