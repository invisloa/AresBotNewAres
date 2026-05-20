using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KsefSystem.Domain;
using KsefSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace KsefSystem.Services
{
    public class QuarantineService
    {
        private readonly AppDbContext _context;

        public QuarantineService(AppDbContext context)
        {
            _context = context;
        }

        public async Task<List<KsefQuarantine>> GetQuarantinedInvoicesAsync()
        {
            return await _context.KsefQuarantine
                .Where(q => q.Status == "QUARANTINED")
                .OrderByDescending(q => q.CreatedAt)
                .ToListAsync();
        }

        public async Task ResolveQuarantineAsync(int quarantineId, string resolutionAction)
        {
            var item = await _context.KsefQuarantine.FindAsync(quarantineId);
            if (item == null) return;

            // In a real system, 'resolutionAction' might be 'WaitCorrection', 'ForceAccept', or 'Cancel'.
            if (resolutionAction == "ForceAccept")
            {
                // Force Accept creates an Invoice even if math was weird,
                // OR creates a corrective invoice.
                // For this example, we map what we can.

                var invoice = new Invoice
                {
                    TenantId = item.TenantId,
                    InvoiceNumber = "MANUAL-" + item.KsefReferenceNumber.Substring(0, 8),
                    VendorId = "MANUAL",
                    NetTotal = 0,
                    VatTotal = 0,
                    GrossTotal = 0,
                    Status = "DRAFT"
                };

                _context.Invoices.Add(invoice);
                item.Status = "PROCESSED";
                item.ValidationErrors += " [MANUALLY RESOLVED]";
            }
            else if (resolutionAction == "Reject")
            {
                item.Status = "REJECTED";
            }

            await _context.SaveChangesAsync();
        }
    }
}
