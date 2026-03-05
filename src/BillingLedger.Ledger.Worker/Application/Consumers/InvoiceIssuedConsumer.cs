using BillingLedger.Contracts.Billing;
using BillingLedger.Ledger.Worker.Domain;
using BillingLedger.Ledger.Worker.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BillingLedger.Ledger.Worker.Application.Consumers;

/// <summary>
/// Creates a DEBIT LedgerEntry when an invoice is issued.
/// Idempotency: unique index on event_id rejects duplicate events silently.
/// </summary>
public sealed class InvoiceIssuedConsumer(
    LedgerDbContext db,
    ILogger<InvoiceIssuedConsumer> logger) : IConsumer<InvoiceIssuedV1>
{
    public async Task Consume(ConsumeContext<InvoiceIssuedV1> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        // Pre-check (common case — avoids wasted write round-trip)
        if (await db.LedgerEntries.AnyAsync(e => e.EventId == msg.EventId, ct))
        {
            logger.LogInformation(
                "Duplicate InvoiceIssuedV1 discarded. EventId={EventId} InvoiceId={InvoiceId}",
                msg.EventId, msg.InvoiceId);
            return;
        }

        db.LedgerEntries.Add(LedgerEntry.Create(
            invoiceId: msg.InvoiceId,
            type: LedgerEntryType.Debit,
            amount: msg.Amount,
            currency: msg.Currency,
            eventId: msg.EventId,
            correlationId: msg.CorrelationId));

        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "DEBIT LedgerEntry created. InvoiceId={InvoiceId} Amount={Amount} {Currency}",
                msg.InvoiceId, msg.Amount, msg.Currency);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Race-condition guard: concurrent instance already committed
            logger.LogInformation(
                "Race-condition idempotency catch for InvoiceIssuedV1. EventId={EventId}",
                msg.EventId);
        }
    }
}
