namespace BillingLedger.Billing.Api.Domain.Audit;

/// <summary>
/// Immutable audit trail record persisted atomically with every destructive action.
/// Stored in infra.audit_logs (shared infrastructure schema).
/// </summary>
public sealed class AuditLog
{
    public Guid Id { get; private set; }
    public string ActorUserId { get; private set; } = null!;
    public string Action { get; private set; } = null!;
    public string ResourceType { get; private set; } = null!;
    public string ResourceId { get; private set; } = null!;
    public DateTime OccurredAt { get; private set; }

    private AuditLog() { }

    public static AuditLog Record(
        string actorUserId,
        string action,
        string resourceType,
        string resourceId) => new()
        {
            Id = Guid.NewGuid(),
            ActorUserId = actorUserId,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            OccurredAt = DateTime.UtcNow
        };
}
