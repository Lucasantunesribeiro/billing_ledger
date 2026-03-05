using BillingLedger.Contracts.Payments;
using BillingLedger.Payments.Worker.Application.Consumers;
using BillingLedger.Payments.Worker.Infrastructure.Persistence;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace BillingLedger.Payments.UnitTests.Consumers;

public sealed class PaymentReceivedConsumerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("payments_test")
        .WithUsername("payments_user")
        .WithPassword("payments_pass")
        .Build();

    private ServiceProvider _services = null!;
    private ITestHarness _harness = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<PaymentsDbContext>(options =>
            options.UseNpgsql(
                _postgres.GetConnectionString(),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations", "payments")));

        services.AddMassTransitTestHarness(cfg =>
            cfg.AddConsumer<PaymentReceivedConsumer>());

        _services = services.BuildServiceProvider();

        // Apply migrations before starting the harness
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.MigrateAsync();

        _harness = _services.GetRequiredService<ITestHarness>();
        await _harness.Start();
    }

    public async Task DisposeAsync()
    {
        await _harness.Stop();
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    // ─── Happy path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Consume_NewPayment_ShouldPersistPaymentAttempt()
    {
        var msg = BuildMessage("pix-new-001");

        await _harness.Bus.Publish(msg);
        await WaitUntilConsumedCount(1);

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var attempt = await db.PaymentAttempts
            .FirstOrDefaultAsync(p => p.ExternalPaymentId == msg.ExternalPaymentId);

        attempt.Should().NotBeNull("PaymentAttempt must be persisted to the payments schema");
        attempt!.InvoiceId.Should().Be(msg.InvoiceId);
        attempt.Provider.Should().Be("PIX");
        attempt.Amount.Should().Be(msg.Amount);
    }

    [Fact]
    public async Task Consume_NewPayment_ShouldPublishPaymentConfirmedV1()
    {
        var msg = BuildMessage("pix-confirmed-001");

        await _harness.Bus.Publish(msg);
        await WaitUntilConsumedCount(1);

        var published = _harness.Published
            .Select<PaymentConfirmedV1>()
            .Any(ctx => ctx.Context.Message.ExternalPaymentId == msg.ExternalPaymentId);

        published.Should().BeTrue("PaymentConfirmedV1 must be published after processing PaymentReceivedV1");
    }

    // ─── Idempotency ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Consume_DuplicatePayment_ShouldBeNoOp_NoExtraAttemptOrConfirmed()
    {
        var externalId = $"pix-dup-{Guid.NewGuid():N}";
        var first = BuildMessage(externalId);

        // First message — normal processing
        await _harness.Bus.Publish(first);
        await WaitUntilConsumedCount(1);

        // Second message — exact same ExternalPaymentId (idempotency key)
        var duplicate = first with { EventId = Guid.NewGuid() };
        await _harness.Bus.Publish(duplicate);
        await WaitUntilConsumedCount(2); // both ACK'd by the bus, no DLQ

        // DB must have exactly 1 PaymentAttempt
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var count = await db.PaymentAttempts
            .CountAsync(p => p.ExternalPaymentId == externalId);

        count.Should().Be(1, "duplicate webhook must be silently discarded — no extra row");

        // Bus must have published PaymentConfirmedV1 exactly once
        var confirmedCount = _harness.Published
            .Select<PaymentConfirmedV1>()
            .Count(ctx => ctx.Context.Message.ExternalPaymentId == externalId);

        confirmedCount.Should().Be(1, "only one PaymentConfirmedV1 must be published for a given ExternalPaymentId");
    }

    [Fact]
    public async Task Consume_DuplicatePayment_ShouldNotFaultOrGoToDlq()
    {
        var externalId = $"pix-dlq-{Guid.NewGuid():N}";
        var msg = BuildMessage(externalId);

        await _harness.Bus.Publish(msg);
        await WaitUntilConsumedCount(1);

        await _harness.Bus.Publish(msg with { EventId = Guid.NewGuid() });
        await WaitUntilConsumedCount(2);

        // No faulted messages — duplicate must be silently ACK'd
        var faulted = _harness.Published
            .Select<Fault<PaymentReceivedV1>>()
            .Any();

        faulted.Should().BeFalse("idempotent duplicate must never fault");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static PaymentReceivedV1 BuildMessage(string externalId) => new()
    {
        InvoiceId = Guid.NewGuid(),
        ExternalPaymentId = externalId,
        Provider = "PIX",
        Amount = 150m,
        ReceivedAt = DateTime.UtcNow,
        CorrelationId = Guid.NewGuid()
    };

    private async Task WaitUntilConsumedCount(int expected, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (_harness.Consumed.Select<PaymentReceivedV1>().Count() >= expected)
                return;
            await Task.Delay(50);
        }
        throw new TimeoutException(
            $"Expected {expected} consumed PaymentReceivedV1 messages, " +
            $"got {_harness.Consumed.Select<PaymentReceivedV1>().Count()}");
    }
}
