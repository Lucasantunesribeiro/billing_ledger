using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BillingLedger.Payments.Worker.Infrastructure.Persistence;

/// <summary>Design-time factory for 'dotnet ef migrations' commands.</summary>
internal sealed class PaymentsDbContextFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    public PaymentsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5433;Database=billing_ledger;Username=billing_user;Password=billing_pass",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations", "payments"))
            .Options;

        return new PaymentsDbContext(options);
    }
}
