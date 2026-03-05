using BillingLedger.SharedKernel.Domain;
using BillingLedger.SharedKernel.Primitives;

namespace BillingLedger.Billing.Api.Domain.Events;

/// <summary>Internal domain event raised when a new Invoice is created as Draft.</summary>
public sealed record InvoiceCreatedDomainEvent : DomainEvent
{
    public required Guid InvoiceId { get; init; }
}

/// <summary>Internal domain event raised when an Invoice is issued (Draft → Issued).</summary>
public sealed record InvoiceIssuedDomainEvent : DomainEvent
{
    public required Guid InvoiceId { get; init; }
    public required Guid CustomerId { get; init; }
    public required Money Amount { get; init; }
    public required DateTime DueDate { get; init; }
    public required DateTime IssuedAt { get; init; }
}

/// <summary>Internal domain event raised when an Invoice is paid (Issued|Overdue → Paid).</summary>
public sealed record InvoicePaidDomainEvent : DomainEvent
{
    public required Guid InvoiceId { get; init; }
    public required Money Amount { get; init; }
    public required DateTime PaidAt { get; init; }
}

/// <summary>Internal domain event raised when an Invoice is cancelled.</summary>
public sealed record InvoiceCancelledDomainEvent : DomainEvent
{
    public required Guid InvoiceId { get; init; }
}

/// <summary>Internal domain event raised when an issued Invoice becomes overdue (job-triggered).</summary>
public sealed record InvoiceOverdueDomainEvent : DomainEvent
{
    public required Guid InvoiceId { get; init; }
    public required DateTime OverdueAt { get; init; }
}
