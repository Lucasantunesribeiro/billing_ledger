namespace BillingLedger.Billing.Api.Application.Commands;

public record CreateInvoiceRequest(
    Guid CustomerId,
    decimal Amount,
    string Currency,
    DateTime DueDate,
    string? ExternalReference);
