using DriverScanTester.Models;

namespace DriverScanTester.Services
{
    /// <summary>
    /// Why a repot (teleport to the city) is needed. The workflow needs the reason to
    /// tell a WEIGHT repot apart from a low-potion repot: only the weight repot may be
    /// preceded by the LOOT PRIORITY potion dump.
    /// </summary>
    public enum RepotReason
    {
        /// <summary>No repot condition is met.</summary>
        None = 0,

        /// <summary>The HP potion stock reached <see cref="RepotDetectorService.MinHpPotions"/>.</summary>
        LowHpPotions,

        /// <summary>The mana potion stock reached <see cref="RepotDetectorService.MinManaPotions"/>.</summary>
        LowManaPotions,

        /// <summary>The bag reached the weight limit (LOOT PRIORITY only).</summary>
        Weight,

        /// <summary>HP is low and no HP potions are left.</summary>
        LowHp,

        /// <summary>Mana is low and no mana potions are left.</summary>
        LowMana
    }

    /// <summary>
    /// Decides whether the player needs to repot based on a GameSnapshot.
    /// Thresholds are configurable.
    /// </summary>
    public class RepotDetectorService
    {
        private readonly Action<string> _log;

        /// <summary>Minimum HP potions before repot is needed.</summary>
        public int MinHpPotions { get; set; } = BotConstants.Repot.DefaultMinHpPotions;

        /// <summary>Minimum mana potions before repot is needed.</summary>
        public int MinManaPotions { get; set; } = BotConstants.Repot.DefaultMinManaPotions;

        /// <summary>Weight ratio threshold (current/max) above which repot is needed.</summary>
        public float MaxWeightRatio { get; set; } = BotConstants.Repot.DefaultMaxWeightRatio;

        /// <summary>
        /// Optional exact weight limit (same units as <c>GameSnapshot.CurrentWeight</c>).
        /// When greater than 0 it replaces <see cref="MaxWeightRatio"/>: the repot fires
        /// as soon as the current weight reaches this value. 0 means "use the ratio".
        /// Class-specific profile settings may set it; shared defaults never do.
        /// </summary>
        public int MaxWeightValue { get; set; } = 0;

        /// <summary>
        /// Whether the weight-ratio check participates in repot decisions. Only enabled
        /// when the profile uses LOOT PRIORITY — loot fills the inventory up to the
        /// weight limit, so a full bag means it is time to repot. Without loot priority
        /// the bot just exp's as the profile states and weight never forces a repot.
        /// </summary>
        public bool WeightRepotEnabled { get; set; } = true;

        /// <summary>
        /// HP floor. While HP potions are available the heal/mana bot drinks them to
        /// stay above this value; repot is only triggered at/below this value once the
        /// HP potion stock is exhausted (the potion-count check above already fires then).
        /// </summary>
        public int MinHp { get; set; } = BotConstants.Repot.DefaultMinHp;

        /// <summary>
        /// Mana floor. While mana potions are available the heal/mana bot drinks them to
        /// stay above this value; repot is only triggered at/below this value once the
        /// mana potion stock is exhausted (the potion-count check above already fires then).
        /// </summary>
        public int MinMana { get; set; } = BotConstants.Repot.DefaultMinMana;

        public RepotDetectorService(Action<string> log)
        {
            _log = log;
        }

        /// <summary>
        /// True when the HP potion stock is at/below the configured minimum —
        /// i.e. the repot (and the teleport back to the city) is needed because
        /// of low HP potions. Used to trigger the low-HP-potions screenshot.
        /// </summary>
        public bool IsLowHpPotions(GameSnapshot snapshot)
        {
            return snapshot.HpPotions <= MinHpPotions;
        }

        /// <summary>
        /// True when the bag reached the weight limit and the weight check is enabled
        /// (LOOT PRIORITY). Pure weight test: the potion and HP/mana conditions are NOT
        /// evaluated, so the workflow can react to the weight trigger on its own and run
        /// the potion dump before deciding to teleport.
        /// </summary>
        public bool IsWeightRepotNeeded(GameSnapshot snapshot)
        {
            return IsWeightLimitReached(snapshot.CurrentWeight, snapshot.MaxWeight);
        }

        /// <summary>
        /// Weight-only variant for re-reading the weight after a potion dump.
        /// An unreadable max weight (≤ 0) never triggers the ratio check.
        /// </summary>
        public bool IsWeightLimitReached(int currentWeight, int maxWeight)
        {
            if (!WeightRepotEnabled)
                return false;

            // An exact weight limit for the current class takes precedence over the
            // percentage, so it also works when the game reports no max weight.
            if (MaxWeightValue > 0)
                return currentWeight >= MaxWeightValue;

            if (maxWeight <= 0)
                return false;

            float ratio = (float)currentWeight / maxWeight;
            return ratio >= MaxWeightRatio;
        }

        /// <summary>
        /// Checks if the player needs to repot based on the snapshot.
        /// </summary>
        public bool NeedsRepot(GameSnapshot snapshot)
        {
            return Diagnose(snapshot) != RepotReason.None;
        }

        /// <summary>
        /// Returns the first repot condition that fires (in the same priority order the
        /// repot checks always used) and logs it. <see cref="RepotReason.None"/> means no
        /// repot is needed.
        /// </summary>
        public RepotReason Diagnose(GameSnapshot snapshot)
        {
            if (snapshot.HpPotions <= MinHpPotions)
            {
                _log($"[RepotDetector] Low HP potions: {snapshot.HpPotions} <= {MinHpPotions}");
                return RepotReason.LowHpPotions;
            }

            if (snapshot.ManaPotions <= MinManaPotions)
            {
                _log($"[RepotDetector] Low mana potions: {snapshot.ManaPotions} <= {MinManaPotions}");
                return RepotReason.LowManaPotions;
            }

            // The weight check only applies with loot priority enabled (the profile's
            // "Loot Priority" flag). Without loot priority the bot keeps exp'ing as the
            // profile states and a full bag never forces a repot.
            if (IsWeightRepotNeeded(snapshot))
            {
                if (MaxWeightValue > 0)
                {
                    _log($"[RepotDetector] Weight high: {snapshot.CurrentWeight} >= {MaxWeightValue} (exact class limit)");
                }
                else
                {
                    float ratio = (float)snapshot.CurrentWeight / snapshot.MaxWeight;
                    _log($"[RepotDetector] Weight high: {snapshot.CurrentWeight}/{snapshot.MaxWeight} ({ratio:P0} >= {MaxWeightRatio:P0})");
                }
                return RepotReason.Weight;
            }

            // Low current HP/Mana triggers a repot only when the corresponding potion
            // stock is exhausted. While potions are available the heal/mana bot restores
            // them automatically — teleporting home on a low-mana sample with 35 potions
            // in the inventory is exactly the false trigger we want to avoid.
            if (snapshot.HpPotions <= MinHpPotions && snapshot.Hp <= MinHp)
            {
                _log($"[RepotDetector] HP is {snapshot.Hp} <= {MinHp} and HP potions are low ({snapshot.HpPotions} <= {MinHpPotions})");
                return RepotReason.LowHp;
            }

            if (snapshot.ManaPotions <= MinManaPotions && snapshot.Mana <= MinMana)
            {
                _log($"[RepotDetector] Mana is {snapshot.Mana} <= {MinMana} and mana potions are low ({snapshot.ManaPotions} <= {MinManaPotions})");
                return RepotReason.LowMana;
            }

            return RepotReason.None;
        }
    }
}
