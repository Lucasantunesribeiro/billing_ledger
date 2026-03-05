using BillingLedger.Billing.Api.Domain.Aggregates;

namespace BillingLedger.Billing.Api.Domain.Repositories;

public record InvoiceFilter
{
    public InvoiceStatus? Status { get; init; }
    public Guid? CustomerId { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
}
