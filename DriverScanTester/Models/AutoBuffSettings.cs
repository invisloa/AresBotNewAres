using System.Collections.Generic;

namespace DriverScanTester.Models
{
    public sealed class AutoBuffSettings
    {
        public bool Enabled { get; set; }
        public List<AutoBuffEntry> Buffs { get; set; } = new();
    }
}
