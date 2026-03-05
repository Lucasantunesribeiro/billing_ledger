namespace BillingLedger.Billing.Api.Domain.Aggregates;

public enum InvoiceStatus
{
    Draft,
    Issued,
    Paid,
    Overdue,
    Cancelled
}
