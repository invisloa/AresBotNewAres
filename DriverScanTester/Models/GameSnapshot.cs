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

        /// <summary>White (movement-speed) potions — potion-slot count at the white-potion offset.</summary>
        public int WhitePotions { get; }

        /// <summary>Red (attack) potions — potion-slot count at the red-potion offset.</summary>
        public int RedPotions { get; }

        public int CurrentWeight { get; }
        public int MaxWeight { get; }

        public GameSnapshot(
            float x, float y,
            int mapNumber,
            int currentMap,
            bool isInCity,
            int hp, int mana,
            int hpPotions, int manaPotions,
            int currentWeight, int maxWeight,
            int whitePotions = 0, int redPotions = 0)
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
            WhitePotions = whitePotions;
            RedPotions = redPotions;
            CurrentWeight = currentWeight;
            MaxWeight = maxWeight;
        }
    }
}
