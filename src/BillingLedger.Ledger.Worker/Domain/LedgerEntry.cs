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
        Guid correlationId) => new()
    {
        Id = Guid.NewGuid(),
        InvoiceId = invoiceId,
        Type = type,
        Amount = amount,
        Currency = currency,
        EventId = eventId,
        CorrelationId = correlationId,
        RecordedAt = DateTime.UtcNow
    };
}
