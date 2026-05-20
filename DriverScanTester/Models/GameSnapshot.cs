namespace DriverScanTester.Models
{
    /// <summary>
    /// Immutable snapshot of the player's current game state,
    /// used by the workflow coordinator to make decisions.
    /// </summary>
    public class GameSnapshot
    {
        public float X { get; }
        public float Y { get; }
        public int MapNumber { get; }
        public int CurrentMap { get; }
        public bool IsInCity { get; }
        public int Hp { get; }
        public int Mana { get; }
        public int HpPotions { get; }
        public int ManaPotions { get; }
        public int CurrentWeight { get; }
        public int MaxWeight { get; }

        public GameSnapshot(
            float x, float y,
            int mapNumber,
            int currentMap,
            bool isInCity,
            int hp, int mana,
            int hpPotions, int manaPotions,
            int currentWeight, int maxWeight)
        {
            X = x;
            Y = y;
            MapNumber = mapNumber;
            CurrentMap = currentMap;
            IsInCity = isInCity;
            Hp = hp;
            Mana = mana;
            HpPotions = hpPotions;
            ManaPotions = manaPotions;
            CurrentWeight = currentWeight;
            MaxWeight = maxWeight;
        }
    }
}
