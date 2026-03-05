using BillingLedger.Contracts.Payments;
using BillingLedger.Payments.Worker.Domain;
using BillingLedger.Payments.Worker.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BillingLedger.Payments.Worker.Application.Consumers;

/// <summary>
/// Consumes PaymentReceivedV1 events and confirms payments.
///
/// Idempotency strategy (two layers):
///   1. Pre-check (AnyAsync) — handles the common case with no DB round-trip wasted.
///   2. Catch DbUpdateException / UniqueViolation (SqlState 23505) — handles the race
///      where two concurrent workers both pass the pre-check before either writes.
/// Both layers result in a silent no-op: the message is ACK'd, never sent to DLQ.
/// </summary>
public sealed class PaymentReceivedConsumer(
    PaymentsDbContext db,
    ILogger<PaymentReceivedConsumer> logger) : IConsumer<PaymentReceivedV1>
{
    public async Task Consume(ConsumeContext<PaymentReceivedV1> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        // Layer 1: pre-check — fast path for the non-concurrent case
        var alreadyProcessed = await db.PaymentAttempts
            .AnyAsync(p => p.Provider == msg.Provider
                        && p.ExternalPaymentId == msg.ExternalPaymentId, ct);

        if (alreadyProcessed)
        {
            logger.LogInformation(
                "Idempotency guard: duplicate PaymentReceivedV1 discarded. " +
                "Provider={Provider} ExternalPaymentId={ExternalPaymentId} EventId={EventId}",
                msg.Provider, msg.ExternalPaymentId, msg.EventId);
            return; // ACK — do NOT throw, never goes to DLQ
        }

        var attempt = PaymentAttempt.Create(
            msg.InvoiceId,
            msg.ExternalPaymentId,
            msg.Provider,
            msg.Amount,
            msg.CorrelationId);

        db.PaymentAttempts.Add(attempt);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Layer 2: race-condition catch — concurrent instance already committed
            logger.LogInformation(
                "Idempotency constraint catch (race): PaymentReceivedV1 already processed. " +
                "Provider={Provider} ExternalPaymentId={ExternalPaymentId}",
                msg.Provider, msg.ExternalPaymentId);
            return; // ACK — silent discard
        }

        // Publish downstream event (direct via MassTransit bus, no Outbox needed here
        // because MassTransit's consume context guarantees at-least-once with retry)
        await context.Publish(new PaymentConfirmedV1
        {
            InvoiceId = attempt.InvoiceId,
            ExternalPaymentId = attempt.ExternalPaymentId,
            ConfirmedAt = DateTime.UtcNow,
            CorrelationId = msg.CorrelationId
        }, ct);

        logger.LogInformation(
            "Payment confirmed. InvoiceId={InvoiceId} Provider={Provider} ExternalPaymentId={ExternalPaymentId}",
            attempt.InvoiceId, attempt.Provider, attempt.ExternalPaymentId);
    }
}
