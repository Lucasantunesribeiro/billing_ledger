using BillingLedger.SharedKernel.Primitives;

namespace BillingLedger.Ledger.Worker.Domain;

/// <summary>
/// Immutable ledger record — once written, never mutated.
/// EventId is the idempotency key: the unique index uq_ledger_entries_event_id
/// guarantees at-most-once write even under concurrent re-delivery.
/// </summary>
public sealed class LedgerEntry
{
    public Guid Id { get; private set; }
    public Guid InvoiceId { get; private set; }
    public LedgerEntryType Type { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public Guid EventId { get; private set; }    // idempotency key
    public Guid CorrelationId { get; private set; }
    public DateTime RecordedAt { get; private set; }

    // EF Core
#pragma warning disable CS8618
    private LedgerEntry() { }
#pragma warning restore CS8618

    public static LedgerEntry Create(
        Guid invoiceId,
        LedgerEntryType type,
        decimal amount,
        string currency,
        Guid eventId,
        Guid correlationId)
    {
        // O sinal do lancamento e dado por Type (Debit/Credit); Amount e sempre positivo.
        if (amount <= 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Ledger amount must be greater than zero.");
        // Coluna numeric(18,2): mais casas seriam arredondadas silenciosamente pelo PostgreSQL.
        if (!Money.HasValidScale(amount))
            throw new ArgumentException(
                $"Ledger amount must have at most {Money.Scale} decimal places.", nameof(amount));
        if (eventId == Guid.Empty)
            throw new ArgumentException("EventId (idempotency key) is required.", nameof(eventId));

        // Reaproveita a validacao/normalizacao de moeda ISO 4217 do Money.
        var money = Money.Of(amount, currency);

        return new LedgerEntry
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoiceId,
            Type = type,
            Amount = money.Amount,
            Currency = money.Currency,
            EventId = eventId,
            CorrelationId = correlationId,
            RecordedAt = DateTime.UtcNow
        };
    }

    /// <summary>Valor com sinal para apuracao de saldo: Credit soma, Debit subtrai.</summary>
    public decimal SignedAmount => Type == LedgerEntryType.Credit ? Amount : -Amount;

    /// <summary>
    /// Saldo (Credit - Debit) de um conjunto de lancamentos da mesma moeda.
    /// Soma exata em decimal; rejeita mistura de moedas.
    /// </summary>
    public static decimal Balance(IEnumerable<LedgerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        string? currency = null;
        var balance = 0m;
        foreach (var entry in entries)
        {
            currency ??= entry.Currency;
            if (entry.Currency != currency)
                throw new InvalidOperationException(
                    $"Cannot compute balance across currencies: {currency} vs {entry.Currency}");
            balance += entry.SignedAmount;
        }
        return balance;
    }
}
