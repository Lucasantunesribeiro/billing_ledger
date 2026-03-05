using BillingLedger.Ledger.Worker.Application.Consumers;
using BillingLedger.Ledger.Worker.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

// ─── Database ────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<LedgerDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("Postgres"),
        npgsql => npgsql.MigrationsHistoryTable("__ef_migrations", "ledger")));

// ─── Messaging (MassTransit) ─────────────────────────────────────────────────
// Production: replace UsingInMemory with UsingAmazonSqs in Milestone 3
builder.Services.AddMassTransit(cfg =>
{
    cfg.AddConsumer<InvoiceIssuedConsumer>();
    cfg.AddConsumer<InvoicePaidConsumer>();

    cfg.UsingInMemory((ctx, config) =>
    {
        config.ConfigureEndpoints(ctx);
    });
});

var host = builder.Build();
host.Run();
