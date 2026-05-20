using System;

namespace KsefSystem.Domain
{
    // The "Clean" Invoice Entity - Maps to `invoices` table
    public class Invoice
    {
        public int Id { get; set; }
        public int TenantId { get; set; }
        public string InvoiceNumber { get; set; } = string.Empty;
        public string VendorId { get; set; } = string.Empty;

        // Strict Decimal Precision
        public decimal NetTotal { get; set; }
        public decimal VatTotal { get; set; }
        public decimal GrossTotal { get; set; }

        public string Status { get; set; } = "DRAFT";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // In a real app, this would be computed or validated by the domain logic
        public bool IsMathValid => GrossTotal == NetTotal + VatTotal;
    }
}
