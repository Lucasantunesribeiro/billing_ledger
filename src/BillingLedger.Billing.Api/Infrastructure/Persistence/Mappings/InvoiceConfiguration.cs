using BillingLedger.Billing.Api.Domain.Aggregates;
using BillingLedger.SharedKernel.Primitives;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BillingLedger.Billing.Api.Infrastructure.Persistence.Mappings;

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("invoices", "billing");

        // DomainEvents lives only in-memory; never persisted
        builder.Ignore(i => i.DomainEvents);

        // Primary key — EntityId (readonly record struct) → Guid
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id)
            .HasConversion(id => id.Value, value => EntityId.From(value))
            .HasColumnName("id")
            .ValueGeneratedNever();

        // Money as a complex type (EF Core 8+) → two columns: amount, currency
        builder.ComplexProperty(i => i.Amount, money =>
        {
            money.Property(m => m.Amount)
                .HasColumnName("amount")
                .HasPrecision(18, 2)
                .IsRequired();

            money.Property(m => m.Currency)
                .HasColumnName("currency")
                .HasMaxLength(3)
                .IsRequired();
        });

        // Status stored as string for direct DB readability
        builder.Property(i => i.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(i => i.CustomerId)
            .HasColumnName("customer_id")
            .IsRequired();

        builder.Property(i => i.ExternalReference)
            .HasColumnName("external_reference")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(i => i.DueDate)
            .HasColumnName("due_date")
            .IsRequired();

        builder.Property(i => i.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(i => i.IssuedAt)
            .HasColumnName("issued_at");

        builder.Property(i => i.PaidAt)
            .HasColumnName("paid_at");

        builder.Property(i => i.CancelledAt)
            .HasColumnName("cancelled_at");

        // Indexes
        builder.HasIndex(i => i.CustomerId)
            .HasDatabaseName("ix_invoices_customer_id");

        builder.HasIndex(i => i.Status)
            .HasDatabaseName("ix_invoices_status");

        builder.HasIndex(i => i.ExternalReference)
            .IsUnique()
            .HasDatabaseName("uq_invoices_external_reference");
    }
}
