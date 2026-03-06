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
// Local dev: Messaging:Transport=SQS + Messaging:LocalStackServiceUrl=http://localhost:4566
//   Both consumers share one queue (ledger-worker-queue) via LocalStack fanout.
// Production: separate queues per consumer via Messaging:InvoiceIssuedQueueName / InvoicePaidQueueName.
var transport = builder.Configuration["Messaging:Transport"] ?? "InMemory";
var invoiceIssuedQueue = builder.Configuration["Messaging:InvoiceIssuedQueueName"] ?? "bl-invoice-issued";
var invoicePaidQueue = builder.Configuration["Messaging:InvoicePaidQueueName"] ?? "bl-invoice-paid";
var localStackUrl = builder.Configuration["Messaging:LocalStackServiceUrl"];

builder.Services.AddMassTransit(cfg =>
{
    cfg.AddConsumer<InvoiceIssuedConsumer>();
    cfg.AddConsumer<InvoicePaidConsumer>();

    if (transport == "SQS")
    {
        cfg.UsingAmazonSqs((ctx, config) =>
        {
            if (!string.IsNullOrEmpty(localStackUrl))
            {
                config.Host("us-east-1", h =>
                {
                    h.AccessKey("test");
                    h.SecretKey("test");
                    h.Config(new Amazon.SQS.AmazonSQSConfig { ServiceURL = localStackUrl });
                    h.Config(new Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceConfig
                    { ServiceURL = localStackUrl });
                });

                // Local dev: both consumers share one queue (LocalStack creates one ledger queue)
                config.ReceiveEndpoint(invoiceIssuedQueue, ep =>
                {
                    ep.ConfigureConsumer<InvoiceIssuedConsumer>(ctx);
                    ep.ConfigureConsumer<InvoicePaidConsumer>(ctx);
                });
            }
            else
            {
                // Production: dedicated queue per consumer
                config.ReceiveEndpoint(invoiceIssuedQueue, ep =>
                    ep.ConfigureConsumer<InvoiceIssuedConsumer>(ctx));
                config.ReceiveEndpoint(invoicePaidQueue, ep =>
                    ep.ConfigureConsumer<InvoicePaidConsumer>(ctx));
            }
        });
    }
    else
    {
        cfg.UsingInMemory((ctx, config) => config.ConfigureEndpoints(ctx));
    }
});

var host = builder.Build();
host.Run();
