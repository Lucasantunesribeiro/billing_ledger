using System.Text;
using System.Threading.RateLimiting;
using BillingLedger.Billing.Api.Application.Consumers;
using BillingLedger.Billing.Api.Application.Validators;
using BillingLedger.Billing.Api.Domain.Repositories;
using BillingLedger.Billing.Api.Infrastructure.Audit;
using BillingLedger.Billing.Api.Infrastructure.Messaging;
using BillingLedger.Billing.Api.Infrastructure.Observability;
using BillingLedger.Billing.Api.Infrastructure.Persistence;
using BillingLedger.Billing.Api.Infrastructure.Persistence.Interceptors;
using BillingLedger.Billing.Api.Infrastructure.Persistence.Repositories;
using BillingLedger.BuildingBlocks.Messaging;
using BillingLedger.BuildingBlocks.Observability;
using FluentValidation;
using FluentValidation.AspNetCore;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
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

// ─── FluentValidation ─────────────────────────────────────────────────────────
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<CreateInvoiceRequestValidator>();

// ─── Rate Limiting ────────────────────────────────────────────────────────────
// Global: 100 req/min per IP — sliding window partitioned by remote IP.
// "webhook" named policy: 20 req/min (tighter limit for the unauthenticated endpoint).
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetSlidingWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    options.AddPolicy<string>("webhook", ctx =>
        RateLimitPartition.GetSlidingWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await ctx.HttpContext.Response.WriteAsJsonAsync(new
        {
            type = "https://tools.ietf.org/html/rfc6585#section-4",
            title = "Too Many Requests",
            status = 429,
            detail = "Rate limit exceeded. Please slow down your requests."
        }, ct);
    };
});

// ─── Health Checks ───────────────────────────────────────────────────────────
builder.Services.AddHealthChecks();

// ─── API Docs ─────────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "BillingLedger API", Version = "v1" });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Insira o token JWT."
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

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
                Encoding.UTF8.GetBytes(jwtSection["SigningKey"]!)),
            // V3 FIX: Tokens are invalid exactly at expiry — no tolerance window.
            // Default ClockSkew=5min allows stolen tokens to be used post-expiry.
            ClockSkew = TimeSpan.Zero,
            // Pin to HS256 — reject tokens signed with unexpected algorithms (incl. "none").
            ValidAlgorithms = ["HS256"]
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
// Transport:
//   - "SQS"      → Amazon SQS/SNS (production ECS, or local dev via LocalStack)
//   - anything   → InMemory (integration tests only — requires MT license at runtime)
// Local dev: set Messaging:Transport=SQS + Messaging:LocalStackServiceUrl=http://localhost:4566
// Production: set Messaging:Transport=SQS; credentials from ECS task role.
var transport = builder.Configuration["Messaging:Transport"] ?? "InMemory";
var billingApiQueue = builder.Configuration["Messaging:BillingApiQueueName"] ?? "bl-billing-api";
var localStackUrl = builder.Configuration["Messaging:LocalStackServiceUrl"];

builder.Services.AddMassTransit(cfg =>
{
    cfg.AddConsumer<PaymentConfirmedConsumer>();

    if (transport == "SQS")
    {
        cfg.UsingAmazonSqs((ctx, config) =>
        {
            if (!string.IsNullOrEmpty(localStackUrl))
            {
                // LocalStack: explicit credentials + service URL override
                config.Host("us-east-1", h =>
                {
                    h.AccessKey("test");
                    h.SecretKey("test");
                    h.Config(new Amazon.SQS.AmazonSQSConfig { ServiceURL = localStackUrl });
                    h.Config(new Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceConfig
                    { ServiceURL = localStackUrl });
                });
            }
            // Production (ECS): no Host() call — AWS SDK default credential chain
            // resolves region (AWS_DEFAULT_REGION) + credentials (task role IAM).

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
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

// Required for WebApplicationFactory<Program> in integration tests
public partial class Program { }
