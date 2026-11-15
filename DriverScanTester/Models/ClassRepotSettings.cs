namespace DriverScanTester.Models
{
    /// <summary>Repot thresholds and purchase targets for one player class.</summary>
    public class ClassRepotSettings
    {
        public int MinHpPotions { get; set; } = BotConstants.Repot.DefaultMinHpPotions;
        public int MinManaPotions { get; set; } = BotConstants.Repot.DefaultMinManaPotions;
        public float MaxWeightRatio { get; set; } = BotConstants.Repot.DefaultMaxWeightRatio;
        public int MinHp { get; set; } = BotConstants.Repot.DefaultMinHp;
        public int MinMana { get; set; } = BotConstants.Repot.DefaultMinMana;
        public int HpBuyTarget { get; set; } = BotConstants.Repot.HpBuyTarget;
        public int ManaBuyTarget { get; set; } = BotConstants.Repot.ManaBuyTarget;
        public int RedBuyTarget { get; set; } = BotConstants.Repot.RedBuyTarget;
        public int WhiteBuyTarget { get; set; } = BotConstants.Repot.WhiteBuyTarget;
    }
}
