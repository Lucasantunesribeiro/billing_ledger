using BillingLedger.Billing.Api.Domain.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BillingLedger.Billing.Api.Infrastructure.Persistence.Mappings;

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs", "infra");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(a => a.ActorUserId).HasColumnName("actor_user_id").HasMaxLength(256).IsRequired();
        builder.Property(a => a.Action).HasColumnName("action").HasMaxLength(100).IsRequired();
        builder.Property(a => a.ResourceType).HasColumnName("resource_type").HasMaxLength(100).IsRequired();
        builder.Property(a => a.ResourceId).HasColumnName("resource_id").HasMaxLength(100).IsRequired();
        builder.Property(a => a.OccurredAt).HasColumnName("occurred_at").IsRequired();

        builder.HasIndex(a => new { a.ResourceType, a.ResourceId })
            .HasDatabaseName("ix_audit_logs_resource");

        builder.HasIndex(a => a.OccurredAt)
            .HasDatabaseName("ix_audit_logs_occurred_at");
    }
}
