namespace BillingLedger.Contracts.Payments;

public sealed record PaymentConfirmedV1
{
    public int SchemaVersion { get; init; } = 1;
    public Guid EventId { get; init; } = Guid.NewGuid();
    public Guid CorrelationId { get; init; }
    public required Guid InvoiceId { get; init; }
    public required string ExternalPaymentId { get; init; }
    /// <summary>
    /// Valor efetivamente pago. Opcional apenas por compatibilidade com mensagens V1 antigas
    /// (sem o campo); o Billing so marca a fatura como paga se o valor bater com a fatura.
    /// </summary>
    public decimal? Amount { get; init; }
    /// <summary>Moeda ISO 4217 do pagamento, quando conhecida.</summary>
    public string? Currency { get; init; }
    public required DateTime ConfirmedAt { get; init; }
}
