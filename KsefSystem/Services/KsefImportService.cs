using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using KsefSystem.Domain;
using KsefSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace KsefSystem.Services
{
    public class KsefImportService
    {
        private readonly AppDbContext _context;

        public KsefImportService(AppDbContext context)
        {
            _context = context;
        }

        // --- TWO-PHASE IMPORT LOGIC ---

        public async Task ImportInvoiceAsync(string xml, int tenantId, string ksefReferenceNumber)
        {
            // 1. PHASE A: IMMEDIATE PERSISTENCE (Staging)
            // Save RAW data first. If this fails (e.g., DB down), we retry the whole job.
            // If it succeeds, we have a safe copy.

            var quarantineEntry = new KsefQuarantine
            {
                TenantId = tenantId,
                KsefReferenceNumber = ksefReferenceNumber,
                RawXml = xml,
                Status = "NEW",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.KsefQuarantine.Add(quarantineEntry);
            await _context.SaveChangesAsync();

            // 2. PHASE B: BUSINESS VALIDATION
            // Now we process the saved entry.
            // We use a try-catch block to catch parsing or logic errors.

            try
            {
                var validationErrors = new List<string>();

                // Simulated XML Parsing (Assume standard KSeF structure)
                var xDoc = XDocument.Parse(xml);
                var root = xDoc.Root;

                // Extract Fields with Safe Parsing
                string invoiceNumber = root?.Element("InvoiceNumber")?.Value ?? "UNKNOWN";
                string vendorId = root?.Element("VendorId")?.Value ?? "UNKNOWN";

                if (!decimal.TryParse(root?.Element("NetTotal")?.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal net))
                    validationErrors.Add("Invalid NetTotal format");

                if (!decimal.TryParse(root?.Element("TaxTotal")?.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal vat))
                    validationErrors.Add("Invalid TaxTotal format");

                if (!decimal.TryParse(root?.Element("GrossTotal")?.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal gross))
                    validationErrors.Add("Invalid GrossTotal format");

                // Strict Math Validation (The "Hard Constraint" Check)
                // DB has "CHECK (gross = net + vat)", so we must be exact.
                // However, floating point arithmetic in C# decimal is generally safe for equality
                // if values are truly equal. We use a very strict check here.
                if ((net + vat) != gross)
                {
                    validationErrors.Add($"Math Error: Net ({net}) + VAT ({vat}) != Gross ({gross})");
                }

                // Check Validation Result
                if (validationErrors.Count > 0)
                {
                    // PHASE D: FAILURE -> QUARANTINE
                    // Do NOT touch the main 'invoices' table.
                    // Update the quarantine entry with errors.

                    quarantineEntry.Status = "QUARANTINED";
                    quarantineEntry.ValidationErrors = JsonSerializer.Serialize(validationErrors);
                    quarantineEntry.UpdatedAt = DateTime.UtcNow;

                    Console.WriteLine($"[Import] Invoice {ksefReferenceNumber} QUARANTINED. Errors: {quarantineEntry.ValidationErrors}");
                }
                else
                {
                    // PHASE C: SUCCESS -> PRODUCTION
                    // Everything is valid. Insert into clean table.

                    var invoice = new Invoice
                    {
                        TenantId = tenantId,
                        InvoiceNumber = invoiceNumber,
                        VendorId = vendorId,
                        NetTotal = net,
                        VatTotal = vat,
                        GrossTotal = gross,
                        Status = "DRAFT",
                        CreatedAt = DateTime.UtcNow
                    };

                    _context.Invoices.Add(invoice);

                    // Mark quarantine as processed
                    quarantineEntry.Status = "PROCESSED";
                    quarantineEntry.UpdatedAt = DateTime.UtcNow;

                    Console.WriteLine($"[Import] Invoice {ksefReferenceNumber} PROCESSED successfully.");
                }

                // Commit the changes (Phase C or D)
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // CRITICAL ERROR HANDLING
                // If parsing crashes or DB insert fails, we still update the quarantine status to ERROR/REJECTED.

                // IMPORTANT: Clear the ChangeTracker to detach any invalid Invoice entity
                // that might be causing the exception (e.g., constraint violation).
                // We only want to save the update to the existing KsefQuarantine entity.
                _context.ChangeTracker.Clear();

                // Re-attach the quarantine entry to update it
                _context.KsefQuarantine.Attach(quarantineEntry);

                quarantineEntry.Status = "REJECTED";
                quarantineEntry.ValidationErrors = JsonSerializer.Serialize(new List<string> { $"System Error: {ex.Message}" });
                quarantineEntry.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();
                Console.WriteLine($"[Import] System Error processing {ksefReferenceNumber}: {ex.Message}");
            }
        }
    }
}
