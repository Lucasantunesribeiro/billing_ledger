namespace BillingLedger.Contracts.Ledger;

public sealed record LedgerEntryCreatedV1
{
    public int SchemaVersion { get; init; } = 1;
    public Guid EventId { get; init; } = Guid.NewGuid();
    public Guid CorrelationId { get; init; }
    public required Guid LedgerEntryId { get; init; }
    public required Guid InvoiceId { get; init; }
    public required string Type { get; init; }  // Debit | Credit
    public required decimal Amount { get; init; }
    public required DateTime OccurredAt { get; init; }
}
