using System;

namespace DriverScanTester.Models
{
    public sealed class AutoBuffEntry
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public int VirtualKey { get; set; }
    }
}
