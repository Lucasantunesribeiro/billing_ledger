using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BillingLedger.Ledger.Worker.Infrastructure.Persistence;

/// <summary>Design-time factory for 'dotnet ef migrations' commands.</summary>
internal sealed class LedgerDbContextFactory : IDesignTimeDbContextFactory<LedgerDbContext>
{
    public LedgerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=billing_ledger;Username=billing_user;Password=billing_pass",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations", "ledger"))
            .Options;

        return new LedgerDbContext(options);
    }
}
