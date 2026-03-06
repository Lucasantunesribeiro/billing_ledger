using BillingLedger.Billing.Api.Domain.Aggregates;
using BillingLedger.Billing.Api.Domain.Audit;
using BillingLedger.Billing.Api.Domain.Repositories;
using BillingLedger.Billing.Api.Infrastructure.Persistence.Interceptors;
using BillingLedger.BuildingBlocks.Outbox;
using BillingLedger.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace BillingLedger.Billing.Api.Infrastructure.Persistence;

public class BillingDbContext(
    DbContextOptions<BillingDbContext> options,
    DomainEventToOutboxInterceptor interceptor)
    : DbContext(options), IUnitOfWork
{
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.AddInterceptors(interceptor);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // DomainEvent and its subtypes are in-memory only — never persisted
        modelBuilder.Ignore<DomainEvent>();

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BillingDbContext).Assembly);
    }
}
