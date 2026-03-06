using System.Text;
using BillingLedger.Billing.Api.Application.Consumers;
using BillingLedger.Billing.Api.Domain.Repositories;
using BillingLedger.Billing.Api.Infrastructure.Audit;
using BillingLedger.Billing.Api.Infrastructure.Messaging;
using BillingLedger.Billing.Api.Infrastructure.Observability;
using BillingLedger.Billing.Api.Infrastructure.Persistence;
using BillingLedger.Billing.Api.Infrastructure.Persistence.Interceptors;
using BillingLedger.Billing.Api.Infrastructure.Persistence.Repositories;
using BillingLedger.BuildingBlocks.Messaging;
using BillingLedger.BuildingBlocks.Observability;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ─── Serilog ─────────────────────────────────────────────────────────────────
builder.Host.UseSerilog((ctx, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

// ─── Controllers ─────────────────────────────────────────────────────────────
builder.Services.AddControllers();

// ─── Health Checks ───────────────────────────────────────────────────────────
builder.Services.AddHealthChecks();

// ─── API Docs ─────────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "BillingLedger API", Version = "v1" }));

// ─── ProblemDetails + Global Exception Handler ────────────────────────────────
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// ─── JWT Authentication ───────────────────────────────────────────────────────
var jwtSection = builder.Configuration.GetSection("Jwt");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidateAudience = true,
            ValidAudience = jwtSection["Audience"],
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtSection["SigningKey"]!))
        };
    });

// ─── Authorization Policies ───────────────────────────────────────────────────
builder.Services.AddAuthorization(opts =>
{
    opts.AddPolicy("Finance", p => p.RequireRole("Finance", "Admin"));
    opts.AddPolicy("Admin", p => p.RequireRole("Admin"));
    opts.AddPolicy("Support", p => p.RequireRole("Support", "Finance", "Admin"));
    opts.AddPolicy("ReadOnly", p => p.RequireAuthenticatedUser());
});

// ─── OpenTelemetry ────────────────────────────────────────────────────────────
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation());

// ─── Outbox Interceptor ───────────────────────────────────────────────────────
builder.Services.AddSingleton<DomainEventToOutboxInterceptor>();

// ─── Database ────────────────────────────────────────────────────────────────
// Production (ECS): ConnectionStrings__Postgres = partial string (host+port+db);
//   Database__User and Database__Password are injected from Secrets Manager.
// Development: full connection string in appsettings.Development.json.
builder.Services.AddDbContext<BillingDbContext>((sp, options) =>
{
    var connStr = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required");

    var dbUser = builder.Configuration["Database:User"];
    var dbPassword = builder.Configuration["Database:Password"];
    if (!string.IsNullOrEmpty(dbUser) && !string.IsNullOrEmpty(dbPassword))
        connStr = $"{connStr};Username={dbUser};Password={dbPassword}";

    options.UseNpgsql(connStr,
        npgsql => npgsql.MigrationsHistoryTable("__ef_migrations", "billing"));
});

// ─── Domain Services ─────────────────────────────────────────────────────────
builder.Services.AddScoped<IInvoiceRepository, InvoiceRepository>();
builder.Services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<BillingDbContext>());

// ─── Audit ────────────────────────────────────────────────────────────────────
builder.Services.AddScoped<IAuditService, AuditService>();

// ─── Messaging (MassTransit) ─────────────────────────────────────────────────
// Set Messaging:Transport=SQS in production (ECS environment variable).
// Tests and local dev use InMemory (default).
var transport = builder.Configuration["Messaging:Transport"] ?? "InMemory";
var billingApiQueue = builder.Configuration["Messaging:BillingApiQueueName"] ?? "bl-billing-api";

builder.Services.AddMassTransit(cfg =>
{
    cfg.AddConsumer<PaymentConfirmedConsumer>();

    if (transport == "SQS")
    {
        // AWS region + credentials are resolved from environment:
        //   AWS_DEFAULT_REGION env var (set by ECS) + ECS task role (IAM).
        // ConfigurationHostSettings accepts AmazonSQSConfig + AmazonSNSConfig if
        // explicit credentials are needed (e.g. local dev with named profile).
        // AWS credentials + region resolved from ECS task role + AWS_DEFAULT_REGION env var.
        // No explicit Host() call needed in MassTransit 9.x — the SDK default chain handles it.
        cfg.UsingAmazonSqs((ctx, config) =>
        {
            config.ReceiveEndpoint(billingApiQueue, ep =>
                ep.ConfigureConsumer<PaymentConfirmedConsumer>(ctx));
        });
    }
    else
    {
        cfg.UsingInMemory((ctx, config) => config.ConfigureEndpoints(ctx));
    }
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

app.MapHealthChecks("/health");
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

// Required for WebApplicationFactory<Program> in integration tests
public partial class Program { }
