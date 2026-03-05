using BillingLedger.Contracts.Billing;
using BillingLedger.Ledger.Worker.Domain;
using BillingLedger.Ledger.Worker.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BillingLedger.Ledger.Worker.Application.Consumers;

/// <summary>
/// Creates a CREDIT LedgerEntry when an invoice is paid.
/// Together with the DEBIT from InvoiceIssuedConsumer, the net balance is zero.
/// Idempotency: unique index on event_id rejects duplicate events silently.
/// </summary>
public sealed class InvoicePaidConsumer(
    LedgerDbContext db,
    ILogger<InvoicePaidConsumer> logger) : IConsumer<InvoicePaidV1>
{
    public async Task Consume(ConsumeContext<InvoicePaidV1> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        // Pre-check (common case)
        if (await db.LedgerEntries.AnyAsync(e => e.EventId == msg.EventId, ct))
        {
            logger.LogInformation(
                "Duplicate InvoicePaidV1 discarded. EventId={EventId} InvoiceId={InvoiceId}",
                msg.EventId, msg.InvoiceId);
            return;
        }

        db.LedgerEntries.Add(LedgerEntry.Create(
            invoiceId: msg.InvoiceId,
            type: LedgerEntryType.Credit,
            amount: msg.Amount,
            currency: msg.Currency,
            eventId: msg.EventId,
            correlationId: msg.CorrelationId));

        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "CREDIT LedgerEntry created. InvoiceId={InvoiceId} Amount={Amount} {Currency}",
                msg.InvoiceId, msg.Amount, msg.Currency);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            logger.LogInformation(
                "Race-condition idempotency catch for InvoicePaidV1. EventId={EventId}",
                msg.EventId);
        }
    }
}
