using BillingLedger.Ledger.Worker.Domain;
using Microsoft.EntityFrameworkCore;

namespace BillingLedger.Ledger.Worker.Infrastructure.Persistence;

public sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LedgerDbContext).Assembly);
    }
}
