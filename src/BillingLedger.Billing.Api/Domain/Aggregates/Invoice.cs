using BillingLedger.Billing.Api.Domain.Events;
using BillingLedger.SharedKernel.Domain;
using BillingLedger.SharedKernel.Primitives;

namespace BillingLedger.Billing.Api.Domain.Aggregates;

public sealed class Invoice : AggregateRoot
{
    // ─── Identity & Descriptors ──────────────────────────────────────────────

    public EntityId Id { get; private set; }
    public Guid CustomerId { get; private set; }
    public Money Amount { get; private set; }
    public string ExternalReference { get; private set; } = string.Empty;

    // ─── State Machine ───────────────────────────────────────────────────────

    public InvoiceStatus Status { get; private set; }

    // ─── Temporal Markers ────────────────────────────────────────────────────

    public DateTime DueDate { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? IssuedAt { get; private set; }
    public DateTime? PaidAt { get; private set; }
    public DateTime? CancelledAt { get; private set; }

    // ─── EF Core parameterless constructor ───────────────────────────────────
#pragma warning disable CS8618
    private Invoice() { }
#pragma warning restore CS8618

    // ─── Factory ─────────────────────────────────────────────────────────────

    public static Invoice Create(
        Guid customerId,
        Money amount,
        DateTime dueDate,
        string externalReference,
        Guid correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalReference);

        var invoice = new Invoice
        {
            Id = EntityId.New(),
            CustomerId = customerId,
            Amount = amount,
            DueDate = dueDate,
            ExternalReference = externalReference,
            Status = InvoiceStatus.Draft,
            CreatedAt = DateTime.UtcNow
        };

        invoice.RaiseDomainEvent(new InvoiceCreatedDomainEvent
        {
            InvoiceId = invoice.Id.Value,
            CorrelationId = correlationId
        });

        return invoice;
    }

    // ─── Business Intentions ─────────────────────────────────────────────────

    /// <summary>Transitions the invoice from Draft to Issued, publishing an InvoiceIssued event.</summary>
    public void Issue(Guid correlationId)
    {
        EnsureStatus(InvoiceStatus.Draft, nameof(Issue));

        Status = InvoiceStatus.Issued;
        IssuedAt = DateTime.UtcNow;

        RaiseDomainEvent(new InvoiceIssuedDomainEvent
        {
            InvoiceId = Id.Value,
            CustomerId = CustomerId,
            Amount = Amount,
            DueDate = DueDate,
            IssuedAt = IssuedAt.Value,
            CorrelationId = correlationId
        });
    }

    /// <summary>
    /// Marks the invoice as Paid. Valid from Issued or Overdue.
    /// Idempotent: if already Paid, callers should check before invoking.
    /// </summary>
    public void MarkAsPaid(Guid correlationId)
    {
        if (Status is not (InvoiceStatus.Issued or InvoiceStatus.Overdue))
            throw new InvalidOperationException(
                $"Cannot mark invoice as Paid in status {Status}. Allowed: Issued, Overdue.");

        Status = InvoiceStatus.Paid;
        PaidAt = DateTime.UtcNow;

        RaiseDomainEvent(new InvoicePaidDomainEvent
        {
            InvoiceId = Id.Value,
            PaidAt = PaidAt.Value,
            CorrelationId = correlationId
        });
    }

    /// <summary>
    /// Marks the invoice as Overdue. Valid only from Issued.
    /// Triggered by the scheduled overdue-check job.
    /// </summary>
    public void MarkAsOverdue(Guid correlationId)
    {
        EnsureStatus(InvoiceStatus.Issued, nameof(MarkAsOverdue));

        Status = InvoiceStatus.Overdue;

        RaiseDomainEvent(new InvoiceOverdueDomainEvent
        {
            InvoiceId = Id.Value,
            OverdueAt = DateTime.UtcNow,
            CorrelationId = correlationId
        });
    }

    /// <summary>Cancels the invoice. Valid from Draft, Issued, or Overdue.</summary>
    public void Cancel(Guid correlationId)
    {
        if (Status is InvoiceStatus.Paid or InvoiceStatus.Cancelled)
            throw new InvalidOperationException(
                $"Cannot cancel invoice in status {Status}. Paid and Cancelled invoices are terminal states.");

        Status = InvoiceStatus.Cancelled;
        CancelledAt = DateTime.UtcNow;

        RaiseDomainEvent(new InvoiceCancelledDomainEvent
        {
            InvoiceId = Id.Value,
            CorrelationId = correlationId
        });
    }

    // ─── Guard ───────────────────────────────────────────────────────────────

    private void EnsureStatus(InvoiceStatus required, string operation)
    {
        if (Status != required)
            throw new InvalidOperationException(
                $"Cannot perform '{operation}' on invoice in status {Status}. Required status: {required}.");
    }
}
