using DriverScanTester.Models;

namespace DriverScanTester.Services
{
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
        /// Checks if the player needs to repot based on the snapshot.
        /// </summary>
        public bool NeedsRepot(GameSnapshot snapshot)
        {
            if (snapshot.HpPotions <= MinHpPotions)
            {
                _log($"[RepotDetector] Low HP potions: {snapshot.HpPotions} <= {MinHpPotions}");
                return true;
            }

            if (snapshot.ManaPotions <= MinManaPotions)
            {
                _log($"[RepotDetector] Low mana potions: {snapshot.ManaPotions} <= {MinManaPotions}");
                return true;
            }

            if (snapshot.MaxWeight > 0)
            {
                float ratio = (float)snapshot.CurrentWeight / snapshot.MaxWeight;
                if (ratio >= MaxWeightRatio)
                {
                    _log($"[RepotDetector] Weight high: {snapshot.CurrentWeight}/{snapshot.MaxWeight} ({ratio:P0} >= {MaxWeightRatio:P0})");
                    return true;
                }
            }

            // Low current HP/Mana triggers a repot only when the corresponding potion
            // stock is exhausted. While potions are available the heal/mana bot restores
            // them automatically — teleporting home on a low-mana sample with 35 potions
            // in the inventory is exactly the false trigger we want to avoid.
            if (snapshot.HpPotions <= MinHpPotions && snapshot.Hp <= MinHp)
            {
                _log($"[RepotDetector] HP is {snapshot.Hp} <= {MinHp} and HP potions are low ({snapshot.HpPotions} <= {MinHpPotions})");
                return true;
            }

            if (snapshot.ManaPotions <= MinManaPotions && snapshot.Mana <= MinMana)
            {
                _log($"[RepotDetector] Mana is {snapshot.Mana} <= {MinMana} and mana potions are low ({snapshot.ManaPotions} <= {MinManaPotions})");
                return true;
            }

            return false;
        }
    }
}
