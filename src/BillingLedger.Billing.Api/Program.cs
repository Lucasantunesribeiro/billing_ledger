using BillingLedger.Billing.Api.Application.Consumers;
using BillingLedger.Billing.Api.Domain.Repositories;
using BillingLedger.Billing.Api.Infrastructure.Messaging;
using BillingLedger.Billing.Api.Infrastructure.Observability;
using BillingLedger.Billing.Api.Infrastructure.Persistence;
using BillingLedger.Billing.Api.Infrastructure.Persistence.Interceptors;
using BillingLedger.Billing.Api.Infrastructure.Persistence.Repositories;
using BillingLedger.BuildingBlocks.Messaging;
using BillingLedger.BuildingBlocks.Observability;
using MassTransit;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ─── Controllers ─────────────────────────────────────────────────────────────
builder.Services.AddControllers();

// ─── API Docs ─────────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "BillingLedger API", Version = "v1" }));

// ─── ProblemDetails + Global Exception Handler ────────────────────────────────
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// ─── Outbox Interceptor ───────────────────────────────────────────────────────
builder.Services.AddSingleton<DomainEventToOutboxInterceptor>();

// ─── Database ────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<BillingDbContext>((sp, options) =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("Postgres"),
        npgsql => npgsql.MigrationsHistoryTable("__ef_migrations", "billing")));

// ─── Domain Services ─────────────────────────────────────────────────────────
builder.Services.AddScoped<IInvoiceRepository, InvoiceRepository>();
builder.Services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<BillingDbContext>());

// ─── Messaging (MassTransit) ─────────────────────────────────────────────────
// Production: replace UsingInMemory with UsingAmazonSqs in Milestone 3
builder.Services.AddMassTransit(cfg =>
{
    cfg.AddConsumer<PaymentConfirmedConsumer>();

    cfg.UsingInMemory((ctx, config) =>
    {
        config.ConfigureEndpoints(ctx);
    });
});

// IEventBus wraps MassTransit's IPublishEndpoint (in tests: replaced by FakeEventBus)
builder.Services.AddScoped<IEventBus, MassTransitEventBus>();

// ─── Outbox Dispatcher ────────────────────────────────────────────────────────
builder.Services.AddSingleton<OutboxDispatcherService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcherService>());

// ─── Build ────────────────────────────────────────────────────────────────────
var app = builder.Build();

// ─── Pipeline ─────────────────────────────────────────────────────────────────
app.UseExceptionHandler();

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseHttpsRedirection();
app.MapControllers();

app.Run();

// Required for WebApplicationFactory<Program> in integration tests
public partial class Program { }
