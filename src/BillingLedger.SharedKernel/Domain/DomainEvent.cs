namespace BillingLedger.SharedKernel.Domain;

public abstract record DomainEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
    public Guid CorrelationId { get; init; }
}
