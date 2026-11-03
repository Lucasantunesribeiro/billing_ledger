using BillingLedger.SharedKernel.Primitives;

namespace BillingLedger.Payments.Worker.Domain;

public sealed class PaymentAttempt
{
    public Guid Id { get; private set; }
    public Guid InvoiceId { get; private set; }
    public string ExternalPaymentId { get; private set; } = string.Empty;
    public string Provider { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public PaymentAttemptStatus Status { get; private set; }
    public DateTime ReceivedAt { get; private set; }
    public DateTime? ConfirmedAt { get; private set; }
    public Guid CorrelationId { get; private set; }

    // EF Core
#pragma warning disable CS8618
    private PaymentAttempt() { }
#pragma warning restore CS8618

    public static PaymentAttempt Create(
        Guid invoiceId,
        string externalPaymentId,
        string provider,
        decimal amount,
        Guid correlationId)
    {
        // Ultima barreira antes do numeric(18,2): valor nao positivo ou com mais de 2 casas
        // seria gravado (e arredondado) e repassado no PaymentConfirmedV1 (ver ADR 0004).
        if (amount <= 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Payment amount must be greater than zero.");
        if (!Money.HasValidScale(amount))
            throw new ArgumentException(
                $"Payment amount must have at most {Money.Scale} decimal places.", nameof(amount));

        return new PaymentAttempt
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoiceId,
            ExternalPaymentId = externalPaymentId,
            Provider = provider,
            Amount = amount,
            Status = PaymentAttemptStatus.Pending,
            ReceivedAt = DateTime.UtcNow,
            CorrelationId = correlationId
        };
    }
}
