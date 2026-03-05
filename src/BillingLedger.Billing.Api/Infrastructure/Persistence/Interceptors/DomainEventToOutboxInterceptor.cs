using System.Text.Json;
using BillingLedger.Billing.Api.Domain.Events;
using BillingLedger.BuildingBlocks.Outbox;
using BillingLedger.Contracts.Billing;
using BillingLedger.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BillingLedger.Billing.Api.Infrastructure.Persistence.Interceptors;

public sealed class DomainEventToOutboxInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        AddOutboxMessages(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        AddOutboxMessages(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void AddOutboxMessages(DbContext? context)
    {
        if (context is null) return;

        var aggregates = context.ChangeTracker
            .Entries<AggregateRoot>()
            .Where(e => e.Entity.DomainEvents.Count > 0)
            .Select(e => e.Entity)
            .ToList();

        var outboxMessages = aggregates
            .SelectMany(a => a.DomainEvents)
            .Select(ToOutboxMessage)
            .OfType<OutboxMessage>()
            .ToList();

        if (outboxMessages.Count > 0)
            context.Set<OutboxMessage>().AddRange(outboxMessages);

        foreach (var aggregate in aggregates)
            aggregate.ClearDomainEvents();
    }

    private static OutboxMessage? ToOutboxMessage(DomainEvent domainEvent) =>
        domainEvent switch
        {
            InvoiceIssuedDomainEvent e => new OutboxMessage
            {
                Type = typeof(InvoiceIssuedV1).AssemblyQualifiedName!,
                Payload = JsonSerializer.Serialize(new InvoiceIssuedV1
                {
                    InvoiceId = e.InvoiceId,
                    CustomerId = e.CustomerId,
                    Amount = e.Amount.Amount,
                    Currency = e.Amount.Currency,
                    DueDate = e.DueDate,
                    IssuedAt = e.IssuedAt,
                    CorrelationId = e.CorrelationId
                }),
                CorrelationId = e.CorrelationId,
                OccurredAt = e.OccurredAt
            },
            InvoicePaidDomainEvent e => new OutboxMessage
            {
                Type = typeof(InvoicePaidV1).AssemblyQualifiedName!,
                Payload = JsonSerializer.Serialize(new InvoicePaidV1
                {
                    InvoiceId = e.InvoiceId,
                    Amount = e.Amount.Amount,
                    Currency = e.Amount.Currency,
                    PaidAt = e.PaidAt,
                    CorrelationId = e.CorrelationId
                }),
                CorrelationId = e.CorrelationId,
                OccurredAt = e.OccurredAt
            },
            InvoiceOverdueDomainEvent e => new OutboxMessage
            {
                Type = typeof(InvoiceOverdueV1).AssemblyQualifiedName!,
                Payload = JsonSerializer.Serialize(new InvoiceOverdueV1
                {
                    InvoiceId = e.InvoiceId,
                    OverdueAt = e.OverdueAt,
                    CorrelationId = e.CorrelationId
                }),
                CorrelationId = e.CorrelationId,
                OccurredAt = e.OccurredAt
            },
            // InvoiceCreatedDomainEvent and InvoiceCancelledDomainEvent have no integration event
            _ => null
        };
}
