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
        public int MinHpPotions { get; set; } = 10;

        /// <summary>Minimum mana potions before repot is needed.</summary>
        public int MinManaPotions { get; set; } = 10;

        /// <summary>Weight ratio threshold (current/max) above which repot is needed.</summary>
        public float MaxWeightRatio { get; set; } = 0.85f;

        /// <summary>If HP is at or below this value, repot is triggered.</summary>
        public int MinHp { get; set; } = 0;

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

            if (snapshot.Hp <= MinHp)
            {
                _log($"[RepotDetector] HP is {snapshot.Hp} <= {MinHp}");
                return true;
            }

            return false;
        }
    }
}
