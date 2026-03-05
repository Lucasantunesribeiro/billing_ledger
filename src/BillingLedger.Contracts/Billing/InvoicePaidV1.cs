namespace BillingLedger.Contracts.Billing;

public sealed record InvoicePaidV1
{
    public int SchemaVersion { get; init; } = 1;
    public Guid EventId { get; init; } = Guid.NewGuid();
    public Guid CorrelationId { get; init; }
    public required Guid InvoiceId { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    public required DateTime PaidAt { get; init; }
}
