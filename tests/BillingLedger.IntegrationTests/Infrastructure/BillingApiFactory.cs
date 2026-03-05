using BillingLedger.Billing.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace BillingLedger.IntegrationTests.Infrastructure;

public class BillingApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("billing_test")
        .WithUsername("billing_user")
        .WithPassword("billing_pass")
        .Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Replace real DbContext with test one pointing to Testcontainers Postgres
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<BillingDbContext>));
            if (descriptor is not null) services.Remove(descriptor);

            services.AddDbContext<BillingDbContext>(options =>
                options.UseNpgsql(
                    _postgres.GetConnectionString(),
                    npgsql => npgsql.MigrationsHistoryTable("__ef_migrations", "billing")));
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
