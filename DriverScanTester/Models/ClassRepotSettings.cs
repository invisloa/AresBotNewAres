namespace DriverScanTester.Models
{
    /// <summary>Repot thresholds and purchase targets for one player class.</summary>
    public class ClassRepotSettings
    {
        public int MinHpPotions { get; set; } = BotConstants.Repot.DefaultMinHpPotions;
        public int MinManaPotions { get; set; } = BotConstants.Repot.DefaultMinManaPotions;
        public float MaxWeightRatio { get; set; } = BotConstants.Repot.DefaultMaxWeightRatio;

        /// <summary>
        /// Optional exact weight limit for this class, in the same units as
        /// <c>GameSnapshot.CurrentWeight</c>. When greater than 0 it replaces
        /// <see cref="MaxWeightRatio"/> and the repot fires as soon as the current weight
        /// reaches this value. 0 keeps the percentage behaviour. Only class-specific
        /// settings support an exact value; shared defaults always use the percentage.
        /// </summary>
        public int MaxWeightValue { get; set; } = 0;

        public int MinHp { get; set; } = BotConstants.Repot.DefaultMinHp;
        public int MinMana { get; set; } = BotConstants.Repot.DefaultMinMana;
        public int HpBuyTarget { get; set; } = BotConstants.Repot.HpBuyTarget;
        public int ManaBuyTarget { get; set; } = BotConstants.Repot.ManaBuyTarget;
        public int RedBuyTarget { get; set; } = BotConstants.Repot.RedBuyTarget;
        public int WhiteBuyTarget { get; set; } = BotConstants.Repot.WhiteBuyTarget;
    }
}
