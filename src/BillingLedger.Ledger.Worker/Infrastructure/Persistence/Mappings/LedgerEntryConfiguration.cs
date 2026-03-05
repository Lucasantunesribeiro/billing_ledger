using BillingLedger.Ledger.Worker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BillingLedger.Ledger.Worker.Infrastructure.Persistence.Mappings;

internal sealed class LedgerEntryConfiguration : IEntityTypeConfiguration<LedgerEntry>
{
    public void Configure(EntityTypeBuilder<LedgerEntry> builder)
    {
        builder.ToTable("ledger_entries", "ledger");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(e => e.InvoiceId)
            .HasColumnName("invoice_id")
            .IsRequired();

        builder.Property(e => e.Type)
            .HasColumnName("type")
            .HasConversion<string>()
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(e => e.Amount)
            .HasColumnName("amount")
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(e => e.Currency)
            .HasColumnName("currency")
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(e => e.EventId)
            .HasColumnName("event_id")
            .IsRequired();

        builder.Property(e => e.CorrelationId)
            .HasColumnName("correlation_id");

        builder.Property(e => e.RecordedAt)
            .HasColumnName("recorded_at")
            .IsRequired();

        // ─── Idempotency key ─────────────────────────────────────────────────
        // Each integration event has a unique EventId. Re-delivery of the same
        // message carries the same EventId → DB rejects the duplicate write.
        builder.HasIndex(e => e.EventId)
            .IsUnique()
            .HasDatabaseName("uq_ledger_entries_event_id");

        builder.HasIndex(e => e.InvoiceId)
            .HasDatabaseName("ix_ledger_entries_invoice_id");
    }
}
