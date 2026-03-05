using BillingLedger.Payments.Worker.Application.Consumers;
using BillingLedger.Payments.Worker.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

// ─── Database ────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<PaymentsDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("Postgres"),
        npgsql => npgsql.MigrationsHistoryTable("__ef_migrations", "payments")));

// ─── Messaging (MassTransit) ─────────────────────────────────────────────────
// Production: replace UsingInMemory with UsingAmazonSqs in Milestone 3
builder.Services.AddMassTransit(cfg =>
{
    cfg.AddConsumer<PaymentReceivedConsumer>();

    cfg.UsingInMemory((ctx, config) =>
    {
        config.ConfigureEndpoints(ctx);
    });
});

var host = builder.Build();
host.Run();
