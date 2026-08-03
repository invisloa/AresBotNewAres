namespace DriverScanTester.Models
{
    /// <summary>
    /// Defines a single hunt/exp spot: the ordered chain of travel routes from the repot
    /// location to the EXP position, plus the looping exp route. This ties the journey
    /// together as one inseparable definition, so you cannot accidentally pair routes
    /// from different spots.
    /// </summary>
    public class HuntDefinition
    {
        /// <summary>Human-readable name, e.g. "Wilki", "Szkielety", "Minotaur".</summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// Ordered travel routes representing the complete journey from the repot location
        /// to the EXP position. Supports any positive number of entries (ordinary routes
        /// and/or portal routes that complete when their expected destination map is reached).
        /// </summary>
        public List<TravelRouteStep> TravelToExpRoutes { get; set; } = new();

        /// <summary>The looping hunting route.</summary>
        public BotRouteStep ExpLoop { get; set; } = new();
    }
}
