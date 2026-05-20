namespace DriverScanTester.Models
{
    /// <summary>
    /// Represents a rectangular area on a specific map.
    /// Used to determine which city-start route matches the player's current position.
    /// </summary>
    public class StartArea
    {
        /// <summary>Map number where this area is valid.</summary>
        public int MapNumber { get; set; }

        public float MinX { get; set; }
        public float MaxX { get; set; }
        public float MinY { get; set; }
        public float MaxY { get; set; }

        public StartArea() { }

        public StartArea(int mapNumber, float minX, float maxX, float minY, float maxY)
        {
            MapNumber = mapNumber;
            MinX = minX;
            MaxX = maxX;
            MinY = minY;
            MaxY = maxY;
        }

        /// <summary>
        /// Returns true if the given snapshot's position falls within this area.
        /// </summary>
        public bool Contains(GameSnapshot snapshot)
        {
            return snapshot.MapNumber == MapNumber
                && snapshot.X >= MinX && snapshot.X <= MaxX
                && snapshot.Y >= MinY && snapshot.Y <= MaxY;
        }

        public override string ToString()
            => $"Map={MapNumber} X=[{MinX}-{MaxX}] Y=[{MinY}-{MaxY}]";
    }
}
