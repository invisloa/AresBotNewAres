using System;

namespace KsefSystem.Domain
{
    // The "Staging" Entity - Maps to `ksef_import_quarantine` table
    public class KsefQuarantine
    {
        public int Id { get; set; }
        public int TenantId { get; set; }
        public string KsefReferenceNumber { get; set; } = string.Empty; // Unique KSeF ID

        // Use TEXT/String for raw XML storage in C#, mapped to TEXT in Postgres
        public string RawXml { get; set; } = string.Empty;

        // JSONB Column
        // We use string for simplicity in EF Core, but mapped to jsonb
        // Alternatively, we could use JsonDocument, but string is easier to serialize/deserialize manually
        public string? ValidationErrors { get; set; }

        public string Status { get; set; } = "NEW"; // NEW, QUARANTINED, PROCESSED

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
