using BillingLedger.Billing.Api.Domain.Aggregates;
using BillingLedger.Billing.Api.Domain.Repositories;
using BillingLedger.Contracts.Payments;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace BillingLedger.Billing.Api.Application.Consumers;

/// <summary>
/// Closes the payment SAGA: marks the Invoice as Paid.
/// The DomainEventToOutboxInterceptor atomically appends InvoicePaidV1
/// to infra.outbox_messages in the same SaveChangesAsync transaction.
///
/// Idempotency: if Invoice is already Paid, returns ACK silently.
/// Invoice.MarkAsPaid guards against invalid state transitions.
/// </summary>
public sealed class PaymentConfirmedConsumer(
    IInvoiceRepository repository,
    IUnitOfWork unitOfWork,
    ILogger<PaymentConfirmedConsumer> logger) : IConsumer<PaymentConfirmedV1>
{
    public async Task Consume(ConsumeContext<PaymentConfirmedV1> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        var invoice = await repository.GetByIdAsync(msg.InvoiceId, ct);
        if (invoice is null)
        {
            logger.LogWarning(
                "Invoice {InvoiceId} not found for PaymentConfirmedV1 {EventId} — discarding",
                msg.InvoiceId, msg.EventId);
            return; // ACK — may have been cancelled before payment cleared
        }

        if (invoice.Status == InvoiceStatus.Paid)
        {
            logger.LogInformation(
                "Idempotency guard: Invoice {InvoiceId} already Paid — " +
                "duplicate PaymentConfirmedV1 {EventId} discarded",
                msg.InvoiceId, msg.EventId);
            return; // Silent ACK — never DLQ
        }

        invoice.MarkAsPaid(msg.CorrelationId);

        // Atomic write: Invoice status + InvoicePaidV1 outbox message in one transaction
        await unitOfWork.SaveChangesAsync(ct);

        logger.LogInformation(
            "Invoice {InvoiceId} marked as Paid. InvoicePaidV1 queued in outbox.",
            msg.InvoiceId);
    }
}
