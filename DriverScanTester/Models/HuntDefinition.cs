namespace DriverScanTester.Models
{
    /// <summary>
    /// Defines a single hunt/exp spot — the pair of phase 2 (move to exp) and phase 3 (exp loop).
    /// This ties RepotToExpPath and ExpLoopPath together as one inseparable definition,
    /// so you cannot accidentally pair a move-to-exp path from one spot with an exp loop from another.
    /// </summary>
    public class HuntDefinition
    {
        /// <summary>Human-readable name, e.g. "Wilki", "Szkielety", "Minotaur".</summary>
        public string Name { get; set; } = "";

        /// <summary>Filename of the segment from repot NPC to the hunting area (phase 2).</summary>
        public string RepotToExpPath { get; set; } = "";

        /// <summary>Filename of the exp loop segment (phase 3).</summary>
        public string ExpLoopPath { get; set; } = "";
    }
}
