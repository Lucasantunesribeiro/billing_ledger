namespace BillingLedger.Contracts.Billing;

public sealed record InvoiceOverdueV1
{
    public int SchemaVersion { get; init; } = 1;
    public Guid EventId { get; init; } = Guid.NewGuid();
    public Guid CorrelationId { get; init; }
    public required Guid InvoiceId { get; init; }
    public required DateTime OverdueAt { get; init; }
}
