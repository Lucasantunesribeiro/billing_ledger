using BillingLedger.Billing.Api.Domain.Audit;
using BillingLedger.Billing.Api.Infrastructure.Persistence;

namespace BillingLedger.Billing.Api.Infrastructure.Audit;

/// <summary>
/// Adds an AuditLog entry to the current BillingDbContext session.
/// The entry is persisted atomically when the controller calls SaveChangesAsync.
/// </summary>
public sealed class AuditService(BillingDbContext db) : IAuditService
{
    public void Record(string actorUserId, string action, string resourceType, string resourceId)
        => db.AuditLogs.Add(AuditLog.Record(actorUserId, action, resourceType, resourceId));
}
