using BillingLedger.Payments.Worker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BillingLedger.Payments.Worker.Infrastructure.Persistence.Mappings;

internal sealed class PaymentAttemptConfiguration : IEntityTypeConfiguration<PaymentAttempt>
{
    public void Configure(EntityTypeBuilder<PaymentAttempt> builder)
    {
        builder.ToTable("payment_attempts", "payments");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(p => p.InvoiceId)
            .HasColumnName("invoice_id")
            .IsRequired();

        builder.Property(p => p.ExternalPaymentId)
            .HasColumnName("external_payment_id")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(p => p.Provider)
            .HasColumnName("provider")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(p => p.Amount)
            .HasColumnName("amount")
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(p => p.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(p => p.ReceivedAt)
            .HasColumnName("received_at")
            .IsRequired();

        builder.Property(p => p.ConfirmedAt)
            .HasColumnName("confirmed_at");

        builder.Property(p => p.CorrelationId)
            .HasColumnName("correlation_id");

        // ─── Idempotency key ─────────────────────────────────────────────────
        // Safety net: unique index prevents double-spend even under race conditions.
        // Pre-check in consumer handles the normal (non-concurrent) case.
        builder.HasIndex(p => new { p.Provider, p.ExternalPaymentId })
            .IsUnique()
            .HasDatabaseName("uq_payment_attempts_provider_external_id");

        builder.HasIndex(p => p.InvoiceId)
            .HasDatabaseName("ix_payment_attempts_invoice_id");
    }
}
