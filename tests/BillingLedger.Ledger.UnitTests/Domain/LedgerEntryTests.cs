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
}
