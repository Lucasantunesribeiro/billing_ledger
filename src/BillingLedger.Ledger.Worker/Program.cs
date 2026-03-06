using BillingLedger.Ledger.Worker.Application.Consumers;
using BillingLedger.Ledger.Worker.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

// ─── Database ────────────────────────────────────────────────────────────────
// Production (ECS): ConnectionStrings__Postgres = partial string;
//   Database__User and Database__Password are injected from Secrets Manager.
builder.Services.AddDbContext<LedgerDbContext>(options =>
{
    var connStr = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required");

    var dbUser = builder.Configuration["Database:User"];
    var dbPassword = builder.Configuration["Database:Password"];
    if (!string.IsNullOrEmpty(dbUser) && !string.IsNullOrEmpty(dbPassword))
        connStr = $"{connStr};Username={dbUser};Password={dbPassword}";

    options.UseNpgsql(connStr,
        npgsql => npgsql.MigrationsHistoryTable("__ef_migrations", "ledger"));
});

// ─── Messaging (MassTransit) ─────────────────────────────────────────────────
// Set Messaging:Transport=SQS in production (ECS environment variable).
// InvoiceIssuedConsumer and InvoicePaidConsumer get dedicated queues with DLQs.
var transport = builder.Configuration["Messaging:Transport"] ?? "InMemory";
var invoiceIssuedQueue = builder.Configuration["Messaging:InvoiceIssuedQueueName"] ?? "bl-invoice-issued";
var invoicePaidQueue = builder.Configuration["Messaging:InvoicePaidQueueName"] ?? "bl-invoice-paid";

builder.Services.AddMassTransit(cfg =>
{
    cfg.AddConsumer<InvoiceIssuedConsumer>();
    cfg.AddConsumer<InvoicePaidConsumer>();

    if (transport == "SQS")
    {
        cfg.UsingAmazonSqs((ctx, config) =>
        {
            config.ReceiveEndpoint(invoiceIssuedQueue, ep =>
                ep.ConfigureConsumer<InvoiceIssuedConsumer>(ctx));
            config.ReceiveEndpoint(invoicePaidQueue, ep =>
                ep.ConfigureConsumer<InvoicePaidConsumer>(ctx));
        });
    }
    else
    {
        cfg.UsingInMemory((ctx, config) => config.ConfigureEndpoints(ctx));
    }
});

var host = builder.Build();
host.Run();
