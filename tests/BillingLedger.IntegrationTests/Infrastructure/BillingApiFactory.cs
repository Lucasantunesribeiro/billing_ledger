using BillingLedger.Billing.Api.Infrastructure.Messaging;
using BillingLedger.Billing.Api.Infrastructure.Persistence;
using BillingLedger.BuildingBlocks.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace BillingLedger.IntegrationTests.Infrastructure;

public class BillingApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("billing_test")
        .WithUsername("billing_user")
        .WithPassword("billing_pass")
        .Build();

    public FakeEventBus EventBus { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Replace real DbContext with test one pointing to Testcontainers Postgres
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<BillingDbContext>));
            if (descriptor is not null) services.Remove(descriptor);

            services.AddDbContext<BillingDbContext>((sp, options) =>
                options.UseNpgsql(
                    _postgres.GetConnectionString(),
                    npgsql => npgsql.MigrationsHistoryTable("__ef_migrations", "billing")));

            // Replace real IEventBus with FakeEventBus
            services.RemoveAll<IEventBus>();
            services.AddSingleton<IEventBus>(EventBus);

            // Prevent background services (OutboxDispatcher) from auto-running during tests
            // The dispatcher is still resolvable by concrete type for manual test invocation
            var hostedServices = services
                .Where(d => d.ServiceType == typeof(IHostedService))
                .ToList();
            foreach (var svc in hostedServices) services.Remove(svc);

            // Re-register OutboxDispatcherService as singleton (not hosted) so tests can resolve it
            services.AddSingleton<OutboxDispatcherService>();
        });
    }

    public async Task InitializeAsync()
    {
        // Container must start BEFORE ConfigureWebHost references GetConnectionString()
        await _postgres.StartAsync();

        // Trigger host build + apply migrations
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        await db.Database.MigrateAsync();
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
