namespace BillingLedger.Billing.Api.Infrastructure.Audit;

/// <summary>
/// Stages an audit log entry in the current EF Core session.
/// The entry is committed atomically when SaveChangesAsync is called.
/// </summary>
public interface IAuditService
{
    void Record(string actorUserId, string action, string resourceType, string resourceId);
}
