namespace DriverScanTester.Models
{
    /// <summary>
    /// Defines a single hunt/exp spot with three route stages:
    /// Repot → Outside City, Outside City → Exp Spot and the Exp Loop.
    /// This ties the three stages together as one inseparable definition,
    /// so you cannot accidentally pair paths from different spots.
    /// </summary>
    public class HuntDefinition
    {
        /// <summary>Human-readable name, e.g. "Wilki", "Szkielety", "Minotaur".</summary>
        public string Name { get; set; } = "";

        /// <summary>Route from the repot NPC to a defined position outside the city (stage 2).</summary>
        public BotRouteStep RepotToCityExit { get; set; } = new();

        /// <summary>Route from outside the city to the exp/hunting spot (stage 3).</summary>
        public BotRouteStep CityExitToExp { get; set; } = new();

        /// <summary>The looping hunting route (stage 4).</summary>
        public BotRouteStep ExpLoop { get; set; } = new();
    }
}
