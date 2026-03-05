using BillingLedger.Billing.Api.Infrastructure.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging.Abstractions;

namespace BillingLedger.Billing.Api.Infrastructure.Persistence;

/// <summary>
/// Design-time factory used by 'dotnet ef migrations' commands.
/// Targets the local Docker Compose Postgres instance.
/// </summary>
internal sealed class BillingDbContextFactory : IDesignTimeDbContextFactory<BillingDbContext>
{
    public BillingDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<BillingDbContext>();
        optionsBuilder.UseNpgsql(
            "Host=localhost;Port=5432;Database=billing_ledger;Username=billing_user;Password=billing_pass",
            npgsql => npgsql.MigrationsHistoryTable("__ef_migrations", "billing"));

        return new BillingDbContext(optionsBuilder.Options, new DomainEventToOutboxInterceptor());
    }
}
