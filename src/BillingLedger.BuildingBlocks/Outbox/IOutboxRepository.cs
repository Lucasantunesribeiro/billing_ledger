namespace BillingLedger.BuildingBlocks.Outbox;

public interface IOutboxRepository
{
    Task AddAsync(OutboxMessage message, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken ct = default);
    Task MarkPublishedAsync(Guid id, CancellationToken ct = default);
    Task IncrementAttemptsAsync(Guid id, string error, CancellationToken ct = default);
}
