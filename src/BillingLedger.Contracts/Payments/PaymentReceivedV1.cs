namespace BillingLedger.Contracts.Payments;

public sealed record PaymentReceivedV1
{
    public int SchemaVersion { get; init; } = 1;
    public Guid EventId { get; init; } = Guid.NewGuid();
    public Guid CorrelationId { get; init; }
    public required Guid InvoiceId { get; init; }
    public required string ExternalPaymentId { get; init; }
    public required string Provider { get; init; }  // PIX | BOLETO | CARD
    public required decimal Amount { get; init; }
    public required DateTime ReceivedAt { get; init; }
}
