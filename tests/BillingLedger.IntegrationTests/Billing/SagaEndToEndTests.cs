using BillingLedger.Billing.Api.Application.Consumers;
using BillingLedger.Billing.Api.Domain.Aggregates;
using BillingLedger.Billing.Api.Domain.Repositories;
using BillingLedger.Billing.Api.Infrastructure.Messaging;
using BillingLedger.Billing.Api.Infrastructure.Persistence;
using BillingLedger.Billing.Api.Infrastructure.Persistence.Interceptors;
using BillingLedger.Billing.Api.Infrastructure.Persistence.Repositories;
using BillingLedger.BuildingBlocks.Messaging;
using BillingLedger.Contracts.Payments;
using BillingLedger.Ledger.Worker.Application.Consumers;
using BillingLedger.Ledger.Worker.Domain;
using BillingLedger.Ledger.Worker.Infrastructure.Persistence;
using BillingLedger.Payments.Worker.Application.Consumers;
using BillingLedger.Payments.Worker.Infrastructure.Persistence;
using BillingLedger.SharedKernel.Primitives;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace BillingLedger.IntegrationTests.Billing;

/// <summary>
/// Proves the full payment SAGA end-to-end on real Postgres:
///   Invoice (Draft→Issued) → DEBIT ledger entry
///   → PaymentReceivedV1 → PaymentAttempt created → PaymentConfirmedV1
///   → Invoice (Issued→Paid) → InvoicePaidV1 outbox
///   → CREDIT ledger entry
///   → Net balance = 0
///
/// All four consumers share a single MassTransit in-memory test harness.
/// Three DbContexts (billing/payments/ledger) share one Postgres container.
/// The OutboxDispatcherService is invoked manually (ProcessBatchAsync) to
/// control exactly when outbox messages are dispatched.
/// </summary>
public sealed class SagaEndToEndTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("saga_test")
        .WithUsername("saga_user")
        .WithPassword("saga_pass")
        .Build();

    private ServiceProvider _services = null!;
    private ITestHarness _harness = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var conn = _postgres.GetConnectionString();

        var services = new ServiceCollection();
        services.AddLogging();

        // ── Billing DbContext ──────────────────────────────────────────────
        services.AddSingleton<DomainEventToOutboxInterceptor>();
        services.AddDbContext<BillingDbContext>((sp, opt) =>
            opt.UseNpgsql(conn, n => n.MigrationsHistoryTable("__ef_migrations", "billing")));
        services.AddScoped<IInvoiceRepository, InvoiceRepository>();
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<BillingDbContext>());

        // ── Payments DbContext ─────────────────────────────────────────────
        services.AddDbContext<PaymentsDbContext>(opt =>
            opt.UseNpgsql(conn, n => n.MigrationsHistoryTable("__ef_migrations", "payments")));

        // ── Ledger DbContext ───────────────────────────────────────────────
        services.AddDbContext<LedgerDbContext>(opt =>
            opt.UseNpgsql(conn, n => n.MigrationsHistoryTable("__ef_migrations", "ledger")));

        // ── OutboxDispatcher (singleton — resolves its own scopes internally)
        services.AddSingleton<OutboxDispatcherService>();

        // ── MassTransit test harness with ALL four consumers ───────────────
        services.AddMassTransitTestHarness(cfg =>
        {
            cfg.AddConsumer<PaymentReceivedConsumer>();   // Payments BC
            cfg.AddConsumer<PaymentConfirmedConsumer>();  // Billing BC
            cfg.AddConsumer<InvoiceIssuedConsumer>();     // Ledger BC
            cfg.AddConsumer<InvoicePaidConsumer>();       // Ledger BC
        });

        // IEventBus → MassTransit bus (used by OutboxDispatcherService)
        services.AddScoped<IEventBus>(sp =>
            new MassTransitEventBus(sp.GetRequiredService<IBus>()));

        _services = services.BuildServiceProvider();

        // Apply all three migrations on the same Postgres instance
        await using var s1 = _services.CreateAsyncScope();
        await s1.ServiceProvider.GetRequiredService<BillingDbContext>().Database.MigrateAsync();

        await using var s2 = _services.CreateAsyncScope();
        await s2.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.MigrateAsync();

        await using var s3 = _services.CreateAsyncScope();
        await s3.ServiceProvider.GetRequiredService<LedgerDbContext>().Database.MigrateAsync();

        _harness = _services.GetRequiredService<ITestHarness>();
        await _harness.Start();
    }

    public async Task DisposeAsync()
    {
        await _harness.Stop();
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    // ─── Master SAGA test ────────────────────────────────────────────────────

    [Fact]
    public async Task FullSaga_Issue_Webhook_Paid_LedgerBalance_Zero()
    {
        var correlationId = Guid.NewGuid();
        var externalPaymentId = $"pix-saga-{Guid.NewGuid():N}";

        // ── 1. Create Invoice (Draft) ─────────────────────────────────────
        await using var scope1 = _services.CreateAsyncScope();
        var repo1 = scope1.ServiceProvider.GetRequiredService<IInvoiceRepository>();
        var uow1 = scope1.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var invoice = Invoice.Create(
            Guid.NewGuid(),
            Money.Of(300m, "BRL"),
            DateTime.UtcNow.AddDays(30),
            $"SAGA-{Guid.NewGuid():N}",
            correlationId);
        await repo1.AddAsync(invoice, CancellationToken.None);
        await uow1.SaveChangesAsync(CancellationToken.None);
        var invoiceId = invoice.Id.Value;

        // ── 2. Issue Invoice → InvoiceIssuedV1 lands in Outbox ───────────
        await using var scope2 = _services.CreateAsyncScope();
        var repo2 = scope2.ServiceProvider.GetRequiredService<IInvoiceRepository>();
        var uow2 = scope2.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var inv = await repo2.GetByIdAsync(invoiceId, CancellationToken.None);
        inv!.Issue(correlationId);
        await uow2.SaveChangesAsync(CancellationToken.None);

        // ── 3. Dispatch Outbox → InvoiceIssuedV1 published to MassTransit bus
        var dispatcher = _services.GetRequiredService<OutboxDispatcherService>();
        await dispatcher.ProcessBatchAsync(CancellationToken.None);

        // ── 4. Wait: InvoiceIssuedConsumer → DEBIT LedgerEntry ────────────
        await WaitForAsync(async () =>
        {
            await using var s = _services.CreateAsyncScope();
            var db = s.ServiceProvider.GetRequiredService<LedgerDbContext>();
            return await db.LedgerEntries
                .AnyAsync(e => e.InvoiceId == invoiceId && e.Type == LedgerEntryType.Debit);
        }, "DEBIT LedgerEntry to be created after InvoiceIssuedV1");

        // ── 5. Webhook → PaymentReceivedV1 → bus → PaymentReceivedConsumer ─
        await _harness.Bus.Publish(new PaymentReceivedV1
        {
            InvoiceId = invoiceId,
            ExternalPaymentId = externalPaymentId,
            Provider = "PIX",
            Amount = 300m,
            ReceivedAt = DateTime.UtcNow,
            CorrelationId = correlationId
        });

        // ── 6. Wait: PaymentConfirmedConsumer → Invoice.MarkAsPaid() ──────
        await WaitForAsync(async () =>
        {
            await using var s = _services.CreateAsyncScope();
            var db = s.ServiceProvider.GetRequiredService<BillingDbContext>();
            var i = await db.Invoices.FirstOrDefaultAsync(x => x.Id == EntityId.From(invoiceId));
            return i?.Status == InvoiceStatus.Paid;
        }, "Invoice to become Paid after PaymentConfirmedV1");

        // ── 7. Dispatch Outbox again → InvoicePaidV1 published ────────────
        await dispatcher.ProcessBatchAsync(CancellationToken.None);

        // ── 8. Wait: InvoicePaidConsumer → CREDIT LedgerEntry ─────────────
        await WaitForAsync(async () =>
        {
            await using var s = _services.CreateAsyncScope();
            var db = s.ServiceProvider.GetRequiredService<LedgerDbContext>();
            return await db.LedgerEntries
                .AnyAsync(e => e.InvoiceId == invoiceId && e.Type == LedgerEntryType.Credit);
        }, "CREDIT LedgerEntry to be created after InvoicePaidV1");

        // ── 9. Final assertions ───────────────────────────────────────────
        await using var assertScope = _services.CreateAsyncScope();
        var billingDb = assertScope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var ledgerDb = assertScope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        var paymentsDb = assertScope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        var finalInvoice = await billingDb.Invoices
            .FirstAsync(i => i.Id == EntityId.From(invoiceId));
        finalInvoice.Status.Should().Be(InvoiceStatus.Paid,
            "invoice must be Paid after PaymentConfirmedV1 is processed");

        var attempt = await paymentsDb.PaymentAttempts
            .FirstOrDefaultAsync(p => p.ExternalPaymentId == externalPaymentId);
        attempt.Should().NotBeNull("PaymentAttempt must be persisted");

        var entries = await ledgerDb.LedgerEntries
            .Where(e => e.InvoiceId == invoiceId)
            .ToListAsync();

        entries.Should().HaveCount(2, "exactly one DEBIT and one CREDIT entry per invoice");
        entries.Should().Contain(e => e.Type == LedgerEntryType.Debit, "DEBIT entry must exist");
        entries.Should().Contain(e => e.Type == LedgerEntryType.Credit, "CREDIT entry must exist");
        entries.Should().AllSatisfy(e => e.Amount.Should().Be(300m));
        entries.Should().AllSatisfy(e => e.Currency.Should().Be("BRL"));

        var balance = entries
            .Sum(e => e.Type == LedgerEntryType.Credit ? e.Amount : -e.Amount);
        balance.Should().Be(0m, "CREDIT − DEBIT = 0 when payment exactly matches invoice amount");
    }

    // ─── Idempotency: duplicate PaymentConfirmedV1 ───────────────────────────

    [Fact]
    public async Task PaymentConfirmedConsumer_WhenInvoiceAlreadyPaid_ShouldBeNoOp()
    {
        var correlationId = Guid.NewGuid();

        // Create + issue + manually set to Paid via MarkAsPaid
        await using var scope = _services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IInvoiceRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var invoice = Invoice.Create(
            Guid.NewGuid(), Money.Of(100m, "BRL"),
            DateTime.UtcNow.AddDays(30), $"IDEM-{Guid.NewGuid():N}", correlationId);
        await repo.AddAsync(invoice, CancellationToken.None);
        invoice.Issue(correlationId);
        invoice.MarkAsPaid(correlationId);
        await uow.SaveChangesAsync(CancellationToken.None);

        var invoiceId = invoice.Id.Value;

        // Publish PaymentConfirmedV1 twice
        var msg = new PaymentConfirmedV1
        {
            InvoiceId = invoiceId,
            ExternalPaymentId = $"pix-idem-{invoiceId}",
            ConfirmedAt = DateTime.UtcNow,
            CorrelationId = correlationId
        };

        await _harness.Bus.Publish(msg);
        await _harness.Bus.Publish(msg with { EventId = Guid.NewGuid() });

        // Wait for both to be consumed
        await WaitUntilConsumedCount<PaymentConfirmedV1>(2);

        // Invoice must still be Paid — no extra state change
        await using var assertScope = _services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var result = await db.Invoices.FirstAsync(i => i.Id == EntityId.From(invoiceId));
        result.Status.Should().Be(InvoiceStatus.Paid);

        // No fault published
        _harness.Published.Select<Fault<PaymentConfirmedV1>>()
            .Should().BeEmpty("duplicate PaymentConfirmedV1 must never fault");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static async Task WaitForAsync(
        Func<Task<bool>> condition,
        string description,
        int timeoutMs = 12000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Timed out waiting for: {description}");
    }

    private async Task WaitUntilConsumedCount<T>(int expected, int timeoutMs = 8000)
        where T : class
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (_harness.Consumed.Select<T>().Count() >= expected) return;
            await Task.Delay(50);
        }
        throw new TimeoutException(
            $"Expected {expected} consumed {typeof(T).Name}, " +
            $"got {_harness.Consumed.Select<T>().Count()}");
    }
}
