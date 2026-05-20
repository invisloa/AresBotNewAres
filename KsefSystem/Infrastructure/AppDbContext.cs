using Microsoft.EntityFrameworkCore;
using KsefSystem.Domain;

namespace KsefSystem.Infrastructure
{
    public class AppDbContext : DbContext
    {
        public DbSet<KsefQuarantine> KsefQuarantine { get; set; }
        public DbSet<Invoice> Invoices { get; set; }

        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // --- KsefQuarantine Mapping ---
            modelBuilder.Entity<KsefQuarantine>(entity =>
            {
                entity.ToTable("ksef_import_quarantine"); // Exact Table Name

                entity.HasKey(e => e.Id);
                entity.Property(e => e.KsefReferenceNumber).IsRequired().HasMaxLength(100);
                entity.HasIndex(e => e.KsefReferenceNumber).IsUnique();

                entity.Property(e => e.RawXml).IsRequired().HasColumnType("text");

                // Map the ValidationErrors string property to the PostgreSQL 'jsonb' type
                entity.Property(e => e.ValidationErrors)
                    .HasColumnType("jsonb");

                entity.Property(e => e.Status).HasDefaultValue("NEW").HasMaxLength(20);
                entity.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");
                entity.Property(e => e.UpdatedAt).HasDefaultValueSql("NOW()");
            });

            // --- Invoice Mapping ---
            modelBuilder.Entity<Invoice>(entity =>
            {
                entity.ToTable("invoices"); // Exact Table Name

                entity.HasKey(e => e.Id);
                entity.Property(e => e.InvoiceNumber).IsRequired().HasMaxLength(50);
                entity.Property(e => e.VendorId).IsRequired().HasMaxLength(50);

                // Precision 18, 2
                entity.Property(e => e.NetTotal).HasPrecision(18, 2);
                entity.Property(e => e.VatTotal).HasPrecision(18, 2);
                entity.Property(e => e.GrossTotal).HasPrecision(18, 2);

                entity.Property(e => e.Status).HasDefaultValue("DRAFT").HasMaxLength(20);
                entity.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");

                // We replicate the CHECK constraint in EF configuration
                // just so that EF knows about it (optional for runtime, useful for migrations)
                entity.ToTable(t => t.HasCheckConstraint("chk_invoices_math", "gross_total = net_total + vat_total"));
            });
        }
    }
}
