using BillingLedger.Payments.Worker.Application.Consumers;
using BillingLedger.Payments.Worker.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

// ─── Database ────────────────────────────────────────────────────────────────
// Production (ECS): ConnectionStrings__Postgres = partial string;
//   Database__User and Database__Password are injected from Secrets Manager.
builder.Services.AddDbContext<PaymentsDbContext>(options =>
{
    var connStr = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required");

    var dbUser = builder.Configuration["Database:User"];
    var dbPassword = builder.Configuration["Database:Password"];
    if (!string.IsNullOrEmpty(dbUser) && !string.IsNullOrEmpty(dbPassword))
        connStr = $"{connStr};Username={dbUser};Password={dbPassword}";

    options.UseNpgsql(connStr,
        npgsql => npgsql.MigrationsHistoryTable("__ef_migrations", "payments"));
});

// ─── Messaging (MassTransit) ─────────────────────────────────────────────────
// Local dev: Messaging:Transport=SQS + Messaging:LocalStackServiceUrl=http://localhost:4566
// Production: Messaging:Transport=SQS; credentials from ECS task role.
var transport     = builder.Configuration["Messaging:Transport"] ?? "InMemory";
var queueName     = builder.Configuration["Messaging:QueueName"] ?? "bl-payments-worker";
var localStackUrl = builder.Configuration["Messaging:LocalStackServiceUrl"];

builder.Services.AddMassTransit(cfg =>
{
    cfg.AddConsumer<PaymentReceivedConsumer>();

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
            }

            config.ReceiveEndpoint(queueName, ep =>
                ep.ConfigureConsumer<PaymentReceivedConsumer>(ctx));
        });
    }
    else
    {
        cfg.UsingInMemory((ctx, config) => config.ConfigureEndpoints(ctx));
    }
});

var host = builder.Build();
host.Run();
