using BillingLedger.Contracts.Billing;
using BillingLedger.Ledger.Worker.Application.Consumers;
using BillingLedger.Ledger.Worker.Domain;
using BillingLedger.Ledger.Worker.Infrastructure.Persistence;
using System.Data.Common;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace BillingLedger.Ledger.UnitTests.Consumers;

/// <summary>
/// Idempotencia por EventId contra PostgreSQL real (Testcontainers): reentrega do mesmo
/// evento nao duplica lancamento, nem em sequencia nem em concorrencia (indice unico).
/// </summary>
public sealed class LedgerConsumerIdempotencyTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("ledger_test")
        .WithUsername("ledger_user")
        .WithPassword("ledger_pass")
        .Build();

    private ServiceProvider _services = null!;
    private ITestHarness _harness = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<LedgerDbContext>(options =>
            options.UseNpgsql(
                _postgres.GetConnectionString(),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations", "ledger")));

        services.AddMassTransitTestHarness(cfg =>
        {
            cfg.AddConsumer<InvoiceIssuedConsumer>();
            cfg.AddConsumer<InvoicePaidConsumer>();
        });

        _services = services.BuildServiceProvider();

        await using (var scope = _services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database.MigrateAsync();

        _harness = _services.GetRequiredService<ITestHarness>();
        await _harness.Start();
    }

    public async Task DisposeAsync()
    {
        await _harness.Stop();
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task InvoiceIssued_ConsumedTwiceWithSameEventId_ShouldCreateSingleDebit()
    {
        var msg = Issued(1234.56m);

        await _harness.Bus.Publish(msg);
        await WaitUntilConsumed<InvoiceIssuedV1>(1);
        await _harness.Bus.Publish(msg); // reentrega identica (mesmo EventId)
        await WaitUntilConsumed<InvoiceIssuedV1>(2);

        var entries = await EntriesFor(msg.InvoiceId);
        entries.Should().ContainSingle();
        entries[0].Type.Should().Be(LedgerEntryType.Debit);
        entries[0].Amount.Should().Be(1234.56m);
        entries[0].Currency.Should().Be("BRL");
        entries[0].EventId.Should().Be(msg.EventId);
        _harness.Published.Select<Fault<InvoiceIssuedV1>>().Any().Should().BeFalse("duplicata e ACK silencioso");
    }

    [Fact]
    public async Task InvoicePaid_ConsumedTwiceWithSameEventId_ShouldCreateSingleCredit()
    {
        var msg = new InvoicePaidV1
        {
            InvoiceId = Guid.NewGuid(),
            Amount = 0.10m,
            Currency = "BRL",
            PaidAt = DateTime.UtcNow,
            CorrelationId = Guid.NewGuid()
        };

        await _harness.Bus.Publish(msg);
        await WaitUntilConsumed<InvoicePaidV1>(1);
        await _harness.Bus.Publish(msg);
        await WaitUntilConsumed<InvoicePaidV1>(2);

        var entries = await EntriesFor(msg.InvoiceId);
        entries.Should().ContainSingle().Which.Type.Should().Be(LedgerEntryType.Credit);
    }

    [Fact]
    public async Task InvoiceIssued_ConcurrentRedeliveries_ShouldCreateSingleEntryAndNeverFault()
    {
        var msg = Issued(99.99m);

        // Publica 10 copias de uma vez. O harness pode serializar a entrega, entao a corrida
        // real (pre-checagem x commit concorrente) e forcada de forma deterministica no teste
        // InvoiceIssued_WhenConcurrentInstanceCommitsBetweenPreCheckAndSave_... abaixo.
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => _harness.Bus.Publish(msg)));
        await WaitUntilConsumed<InvoiceIssuedV1>(10);

        (await EntriesFor(msg.InvoiceId)).Should().ContainSingle();
        _harness.Published.Select<Fault<InvoiceIssuedV1>>().Any().Should().BeFalse();
    }

    [Fact]
    public async Task InvoiceIssued_WhenConcurrentInstanceCommitsBetweenPreCheckAndSave_ShouldSwallowUniqueViolation()
    {
        var msg = Issued(42.42m);

        // Simula outra instancia do worker gravando o mesmo EventId logo apos a pre-checagem
        // (AnyAsync) deste consumidor: forca o caminho do catch de SQLSTATE 23505.
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations", "ledger"))
            .AddInterceptors(new CommitDuplicateAfterFirstQuery(_postgres.GetConnectionString(), msg))
            .Options;
        await using var db = new LedgerDbContext(options);
        var consumer = new InvoiceIssuedConsumer(db, NullLogger<InvoiceIssuedConsumer>.Instance);
        var context = Substitute.For<ConsumeContext<InvoiceIssuedV1>>();
        context.Message.Returns(msg);
        context.CancellationToken.Returns(CancellationToken.None);

        var act = () => consumer.Consume(context);

        await act.Should().NotThrowAsync("violacao do indice unico = duplicata ja processada");
        (await EntriesFor(msg.InvoiceId)).Should().ContainSingle();
    }

    private sealed class CommitDuplicateAfterFirstQuery(string connectionString, InvoiceIssuedV1 msg)
        : DbCommandInterceptor
    {
        private int _fired;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                var options = new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).Options;
                await using var other = new LedgerDbContext(options);
                other.LedgerEntries.Add(LedgerEntry.Create(msg.InvoiceId, LedgerEntryType.Debit, msg.Amount,
                    msg.Currency, msg.EventId, msg.CorrelationId));
                await other.SaveChangesAsync(cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task DistinctEventsForSameInvoice_ShouldEachCreateEntry_AndBalanceToZero()
    {
        var issued = Issued(300.00m);
        var paid = new InvoicePaidV1
        {
            InvoiceId = issued.InvoiceId,
            Amount = 300.00m,
            Currency = "BRL",
            PaidAt = DateTime.UtcNow,
            CorrelationId = issued.CorrelationId
        };

        await _harness.Bus.Publish(issued);
        await _harness.Bus.Publish(paid);
        await WaitUntilConsumed<InvoiceIssuedV1>(1);
        await WaitUntilConsumed<InvoicePaidV1>(1);

        var entries = await EntriesFor(issued.InvoiceId);
        entries.Should().HaveCount(2);
        LedgerEntry.Balance(entries).Should().Be(0m);
    }

    [Fact]
    public async Task Amount_ShouldRoundTripThroughNumericColumnWithoutLoss()
    {
        var msg = Issued(9_999_999_999_999_999.99m); // limite de numeric(18,2)

        await _harness.Bus.Publish(msg);
        await WaitUntilConsumed<InvoiceIssuedV1>(1);

        (await EntriesFor(msg.InvoiceId)).Single().Amount.Should().Be(9_999_999_999_999_999.99m);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static InvoiceIssuedV1 Issued(decimal amount) => new()
    {
        InvoiceId = Guid.NewGuid(),
        CustomerId = Guid.NewGuid(),
        Amount = amount,
        Currency = "BRL",
        DueDate = DateTime.UtcNow.AddDays(30),
        IssuedAt = DateTime.UtcNow,
        CorrelationId = Guid.NewGuid()
    };

    private async Task<List<LedgerEntry>> EntriesFor(Guid invoiceId)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        return await db.LedgerEntries.AsNoTracking().Where(e => e.InvoiceId == invoiceId).ToListAsync();
    }

    private async Task WaitUntilConsumed<T>(int expected, int timeoutMs = 15000) where T : class
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (_harness.Consumed.Select<T>().Count() >= expected)
                return;
            await Task.Delay(50);
        }
        throw new TimeoutException(
            $"Expected {expected} consumed {typeof(T).Name}, got {_harness.Consumed.Select<T>().Count()}");
    }
}
