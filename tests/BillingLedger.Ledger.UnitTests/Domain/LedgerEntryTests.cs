using System.Reflection;
using BillingLedger.Ledger.Worker.Domain;
using FluentAssertions;

namespace BillingLedger.Ledger.UnitTests.Domain;

public class LedgerEntryTests
{
    [Fact]
    public void Create_ShouldCopyAllFieldsAndKeepEventIdAsIdempotencyKey()
    {
        var invoiceId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();

        var entry = LedgerEntry.Create(invoiceId, LedgerEntryType.Debit, 150.35m, "BRL", eventId, correlationId);

        entry.Id.Should().NotBeEmpty();
        entry.InvoiceId.Should().Be(invoiceId);
        entry.Type.Should().Be(LedgerEntryType.Debit);
        entry.Amount.Should().Be(150.35m);
        entry.Currency.Should().Be("BRL");
        entry.EventId.Should().Be(eventId);
        entry.CorrelationId.Should().Be(correlationId);
        entry.RecordedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Create_ShouldGenerateDistinctIdsForEachEntry()
    {
        var a = LedgerEntry.Create(Guid.NewGuid(), LedgerEntryType.Credit, 1m, "BRL", Guid.NewGuid(), Guid.NewGuid());
        var b = LedgerEntry.Create(Guid.NewGuid(), LedgerEntryType.Credit, 1m, "BRL", Guid.NewGuid(), Guid.NewGuid());

        a.Id.Should().NotBe(b.Id);
    }

    [Fact]
    public void Amount_ShouldBeDecimalNeverFloatingPoint()
    {
        typeof(LedgerEntry).GetProperty(nameof(LedgerEntry.Amount))!.PropertyType
            .Should().Be(typeof(decimal));
    }

    [Fact]
    public void Entry_ShouldBeImmutable_NoPublicSetters()
    {
        var publicSetters = typeof(LedgerEntry)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name);

        publicSetters.Should().BeEmpty("ledger entries are append-only and never mutated");
    }

    [Fact]
    public void Decimal_SumOfDebitAndCredit_ShouldBeExactlyZeroNet()
    {
        var debit = LedgerEntry.Create(Guid.NewGuid(), LedgerEntryType.Debit, 0.1m + 0.2m, "BRL", Guid.NewGuid(), Guid.NewGuid());
        var credit = LedgerEntry.Create(debit.InvoiceId, LedgerEntryType.Credit, 0.3m, "BRL", Guid.NewGuid(), Guid.NewGuid());

        (debit.Amount - credit.Amount).Should().Be(0m);
    }

    // ─── Guardas de valor/moeda ──────────────────────────────────────────────

    [Theory]
    [InlineData("0")]
    [InlineData("-0.01")]
    [InlineData("-100")]
    public void Create_WithNonPositiveAmount_ShouldThrow(string raw)
    {
        var amount = decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);

        var act = () => LedgerEntry.Create(Guid.NewGuid(), LedgerEntryType.Debit, amount, "BRL", Guid.NewGuid(), Guid.NewGuid());

        act.Should().Throw<ArgumentOutOfRangeException>("o sinal vem do Type; Amount e sempre positivo");
    }

    [Fact]
    public void Create_WithMoreThanTwoDecimals_ShouldThrowInsteadOfLettingPostgresRound()
    {
        var act = () => LedgerEntry.Create(Guid.NewGuid(), LedgerEntryType.Debit, 10.005m, "BRL", Guid.NewGuid(), Guid.NewGuid());

        act.Should().Throw<ArgumentException>().WithMessage("*decimal places*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("R$")]
    [InlineData("REAL")]
    public void Create_WithInvalidCurrency_ShouldThrow(string currency)
    {
        var act = () => LedgerEntry.Create(Guid.NewGuid(), LedgerEntryType.Debit, 1m, currency, Guid.NewGuid(), Guid.NewGuid());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_ShouldNormalizeCurrencyToUpperCase()
    {
        LedgerEntry.Create(Guid.NewGuid(), LedgerEntryType.Debit, 1m, "brl", Guid.NewGuid(), Guid.NewGuid())
            .Currency.Should().Be("BRL");
    }

    [Fact]
    public void Create_WithEmptyEventId_ShouldThrow()
    {
        var act = () => LedgerEntry.Create(Guid.NewGuid(), LedgerEntryType.Debit, 1m, "BRL", Guid.Empty, Guid.NewGuid());

        act.Should().Throw<ArgumentException>().WithMessage("*EventId*");
    }

    // ─── Saldo ───────────────────────────────────────────────────────────────

    [Fact]
    public void Balance_OfManyEntries_ShouldBeExact()
    {
        var invoiceId = Guid.NewGuid();
        // 10.000 debitos de 0,01 + 1 credito de 100,00 → saldo exatamente zero.
        var entries = Enumerable.Range(0, 10_000)
            .Select(_ => LedgerEntry.Create(invoiceId, LedgerEntryType.Debit, 0.01m, "BRL", Guid.NewGuid(), Guid.NewGuid()))
            .Append(LedgerEntry.Create(invoiceId, LedgerEntryType.Credit, 100.00m, "BRL", Guid.NewGuid(), Guid.NewGuid()))
            .ToList();

        LedgerEntry.Balance(entries).Should().Be(0m);
        LedgerEntry.Balance(entries.Take(10_000)).Should().Be(-100.00m);
    }

    [Fact]
    public void Balance_WithMixedCurrencies_ShouldThrow()
    {
        var entries = new[]
        {
            LedgerEntry.Create(Guid.NewGuid(), LedgerEntryType.Debit, 1m, "BRL", Guid.NewGuid(), Guid.NewGuid()),
            LedgerEntry.Create(Guid.NewGuid(), LedgerEntryType.Credit, 1m, "USD", Guid.NewGuid(), Guid.NewGuid())
        };

        var act = () => LedgerEntry.Balance(entries);

        act.Should().Throw<InvalidOperationException>();
    }
}
