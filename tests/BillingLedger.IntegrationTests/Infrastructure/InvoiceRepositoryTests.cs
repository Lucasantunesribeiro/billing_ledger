using BillingLedger.Billing.Api.Domain.Aggregates;
using BillingLedger.Billing.Api.Infrastructure.Persistence;
using BillingLedger.Billing.Api.Infrastructure.Persistence.Repositories;
using BillingLedger.SharedKernel.Primitives;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace BillingLedger.IntegrationTests.Infrastructure;

public class InvoiceRepositoryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("billing_test")
        .WithUsername("billing_user")
        .WithPassword("billing_pass")
        .Build();

    private BillingDbContext _ctx = null!;
    private InvoiceRepository _repo = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var options = new DbContextOptionsBuilder<BillingDbContext>()
            .UseNpgsql(
                _postgres.GetConnectionString(),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations", "billing"))
            .Options;

        _ctx = new BillingDbContext(options, new BillingLedger.Billing.Api.Infrastructure.Persistence.Interceptors.DomainEventToOutboxInterceptor());
        await _ctx.Database.MigrateAsync();
        _repo = new InvoiceRepository(_ctx);
    }

    public async Task DisposeAsync()
    {
        await _ctx.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task AddAsync_ShouldPersistInvoiceToDatabase()
    {
        var invoice = Invoice.Create(
            Guid.NewGuid(),
            Money.Of(250.00m, "BRL"),
            DateTime.UtcNow.AddDays(30),
            "INV-2026-TEST-001",
            Guid.NewGuid());

        await _repo.AddAsync(invoice);
        await _ctx.SaveChangesAsync();

        _ctx.ChangeTracker.Clear(); // Evict cache to force DB read

        var saved = await _repo.GetByIdAsync(invoice.Id.Value);

        saved.Should().NotBeNull();
        saved!.Id.Should().Be(invoice.Id);
        saved.CustomerId.Should().Be(invoice.CustomerId);
        saved.Amount.Should().Be(Money.Of(250.00m, "BRL"));
        saved.Status.Should().Be(InvoiceStatus.Draft);
        saved.ExternalReference.Should().Be("INV-2026-TEST-001");
    }

    [Fact]
    public async Task GetByIdAsync_WithNonExistentId_ShouldReturnNull()
    {
        var result = await _repo.GetByIdAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_WithStatusFilter_ShouldReturnOnlyMatchingInvoices()
    {
        var customerId = Guid.NewGuid();
        var draft = Invoice.Create(customerId, Money.Of(100m, "BRL"), DateTime.UtcNow.AddDays(10), "INV-FILTER-A", Guid.NewGuid());
        var issued = Invoice.Create(customerId, Money.Of(200m, "BRL"), DateTime.UtcNow.AddDays(20), "INV-FILTER-B", Guid.NewGuid());
        issued.Issue(Guid.NewGuid());

        await _repo.AddAsync(draft);
        await _repo.AddAsync(issued);
        await _ctx.SaveChangesAsync();

        var filter = new BillingLedger.Billing.Api.Domain.Repositories.InvoiceFilter { Status = InvoiceStatus.Issued };
        var results = await _repo.GetAsync(filter);

        results.Should().Contain(i => i.Id == issued.Id);
        results.Should().NotContain(i => i.Id == draft.Id);
    }

    [Fact]
    public async Task State_DraftToIssued_ShouldPersistCorrectStatus()
    {
        var invoice = Invoice.Create(
            Guid.NewGuid(), Money.Of(500m, "BRL"), DateTime.UtcNow.AddDays(15), "INV-STATE-001", Guid.NewGuid());

        await _repo.AddAsync(invoice);
        await _ctx.SaveChangesAsync();

        // Reload, issue, save again
        _ctx.ChangeTracker.Clear();
        var loaded = await _repo.GetByIdAsync(invoice.Id.Value);
        loaded!.Issue(Guid.NewGuid());
        await _ctx.SaveChangesAsync();

        _ctx.ChangeTracker.Clear();
        var updated = await _repo.GetByIdAsync(invoice.Id.Value);

        updated!.Status.Should().Be(InvoiceStatus.Issued);
        updated.IssuedAt.Should().NotBeNull();
    }
}
