namespace BillingLedger.Billing.Api.Application.Commands;

public sealed record WebhookPaymentRequest(
    Guid InvoiceId,
    string ExternalPaymentId,
    string Provider,
    decimal Amount);
