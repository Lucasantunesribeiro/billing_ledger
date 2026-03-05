namespace BillingLedger.BuildingBlocks.Outbox;

public class OutboxMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Type { get; init; }
    public required string Payload { get; init; }
    public Guid CorrelationId { get; init; }
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
    public DateTime? PublishedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}
