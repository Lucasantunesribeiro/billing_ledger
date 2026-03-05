using BillingLedger.Billing.Api.Domain.Aggregates;

namespace BillingLedger.Billing.Api.Application.Queries;

public record InvoiceResponse(
    Guid Id,
    Guid CustomerId,
    decimal Amount,
    string Currency,
    string Status,
    string ExternalReference,
    DateTime DueDate,
    DateTime CreatedAt,
    DateTime? IssuedAt,
    DateTime? PaidAt,
    DateTime? CancelledAt)
{
    public static InvoiceResponse From(Invoice invoice) => new(
        invoice.Id.Value,
        invoice.CustomerId,
        invoice.Amount.Amount,
        invoice.Amount.Currency,
        invoice.Status.ToString(),
        invoice.ExternalReference,
        invoice.DueDate,
        invoice.CreatedAt,
        invoice.IssuedAt,
        invoice.PaidAt,
        invoice.CancelledAt);
}
