using BillingLedger.BuildingBlocks.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BillingLedger.Billing.Api.Infrastructure.Persistence.Mappings;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages", "infra");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(m => m.Type)
            .HasColumnName("type")
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(m => m.Payload)
            .HasColumnName("payload")
            .IsRequired();

        builder.Property(m => m.CorrelationId)
            .HasColumnName("correlation_id");

        builder.Property(m => m.OccurredAt)
            .HasColumnName("occurred_at")
            .IsRequired();

        builder.Property(m => m.PublishedAt)
            .HasColumnName("published_at");

        builder.Property(m => m.Attempts)
            .HasColumnName("attempts");

        builder.Property(m => m.LastError)
            .HasColumnName("last_error")
            .HasMaxLength(1000);

        builder.HasIndex(m => m.PublishedAt)
            .HasDatabaseName("ix_outbox_messages_published_at");
    }
}
