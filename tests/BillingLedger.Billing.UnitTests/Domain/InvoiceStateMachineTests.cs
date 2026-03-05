using BillingLedger.Billing.Api.Domain.Aggregates;
using BillingLedger.Billing.Api.Domain.Events;
using BillingLedger.SharedKernel.Primitives;
using FluentAssertions;

namespace BillingLedger.Billing.UnitTests.Domain;

public class InvoiceStateMachineTests
{
    private static readonly Guid _customerId = Guid.NewGuid();
    private static readonly Money _amount = Money.Of(150.00m, "BRL");
    private static readonly DateTime _dueDate = DateTime.UtcNow.AddDays(30);
    private static readonly Guid _correlationId = Guid.NewGuid();

    private static Invoice CreateDraftInvoice() =>
        Invoice.Create(_customerId, _amount, _dueDate, "INV-2026-0001", _correlationId);

    // ─── CREATE ──────────────────────────────────────────────────────────────

    [Fact]
    public void Create_ShouldReturnInvoiceInDraftStatus()
    {
        var invoice = CreateDraftInvoice();

        invoice.Status.Should().Be(InvoiceStatus.Draft);
        invoice.CustomerId.Should().Be(_customerId);
        invoice.Amount.Should().Be(_amount);
        invoice.DueDate.Should().BeCloseTo(_dueDate, TimeSpan.FromSeconds(1));
        invoice.IssuedAt.Should().BeNull();
        invoice.PaidAt.Should().BeNull();
        invoice.CancelledAt.Should().BeNull();
    }

    [Fact]
    public void Create_ShouldRaiseInvoiceCreatedDomainEvent()
    {
        var invoice = CreateDraftInvoice();

        invoice.DomainEvents.Should().ContainSingle()
            .Which.Should().BeOfType<InvoiceCreatedDomainEvent>();
    }

    // ─── ISSUE ───────────────────────────────────────────────────────────────

    [Fact]
    public void Issue_FromDraft_ShouldTransitionToIssued()
    {
        var invoice = CreateDraftInvoice();
        invoice.ClearDomainEvents();

        invoice.Issue(_correlationId);

        invoice.Status.Should().Be(InvoiceStatus.Issued);
        invoice.IssuedAt.Should().NotBeNull();
        invoice.IssuedAt!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Issue_FromDraft_ShouldRaiseInvoiceIssuedDomainEvent()
    {
        var invoice = CreateDraftInvoice();
        invoice.ClearDomainEvents();

        invoice.Issue(_correlationId);

        var evt = invoice.DomainEvents.Should().ContainSingle()
            .Which.Should().BeOfType<InvoiceIssuedDomainEvent>().Subject;

        evt.InvoiceId.Should().Be(invoice.Id.Value);
        evt.CustomerId.Should().Be(_customerId);
        evt.Amount.Should().Be(_amount);
        evt.CorrelationId.Should().Be(_correlationId);
    }

    [Theory]
    [InlineData(InvoiceStatus.Issued)]
    [InlineData(InvoiceStatus.Paid)]
    [InlineData(InvoiceStatus.Overdue)]
    [InlineData(InvoiceStatus.Cancelled)]
    public void Issue_FromNonDraftStatus_ShouldThrowInvalidOperationException(InvoiceStatus invalidStatus)
    {
        var invoice = CreateDraftInvoice();
        ForceStatus(invoice, invalidStatus);

        var act = () => invoice.Issue(_correlationId);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{invalidStatus}*");
    }

    // ─── MARK AS PAID ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(InvoiceStatus.Issued)]
    [InlineData(InvoiceStatus.Overdue)]
    public void MarkAsPaid_FromIssuedOrOverdue_ShouldTransitionToPaid(InvoiceStatus fromStatus)
    {
        var invoice = CreateDraftInvoice();
        ForceStatus(invoice, fromStatus);

        invoice.MarkAsPaid(_correlationId);

        invoice.Status.Should().Be(InvoiceStatus.Paid);
        invoice.PaidAt.Should().NotBeNull();
        invoice.PaidAt!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void MarkAsPaid_ShouldRaiseInvoicePaidDomainEvent()
    {
        var invoice = CreateDraftInvoice();
        invoice.Issue(_correlationId);
        invoice.ClearDomainEvents();

        invoice.MarkAsPaid(_correlationId);

        var evt = invoice.DomainEvents.Should().ContainSingle()
            .Which.Should().BeOfType<InvoicePaidDomainEvent>().Subject;

        evt.InvoiceId.Should().Be(invoice.Id.Value);
        evt.CorrelationId.Should().Be(_correlationId);
    }

    [Theory]
    [InlineData(InvoiceStatus.Draft)]
    [InlineData(InvoiceStatus.Cancelled)]
    [InlineData(InvoiceStatus.Paid)]
    public void MarkAsPaid_FromInvalidStatus_ShouldThrowInvalidOperationException(InvoiceStatus invalidStatus)
    {
        var invoice = CreateDraftInvoice();
        ForceStatus(invoice, invalidStatus);

        var act = () => invoice.MarkAsPaid(_correlationId);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{invalidStatus}*");
    }

    // ─── MARK AS OVERDUE ─────────────────────────────────────────────────────

    [Fact]
    public void MarkAsOverdue_FromIssued_ShouldTransitionToOverdue()
    {
        var invoice = CreateDraftInvoice();
        ForceStatus(invoice, InvoiceStatus.Issued);

        invoice.MarkAsOverdue(_correlationId);

        invoice.Status.Should().Be(InvoiceStatus.Overdue);
    }

    [Fact]
    public void MarkAsOverdue_ShouldRaiseInvoiceOverdueDomainEvent()
    {
        var invoice = CreateDraftInvoice();
        invoice.Issue(_correlationId);
        invoice.ClearDomainEvents();

        invoice.MarkAsOverdue(_correlationId);

        invoice.DomainEvents.Should().ContainSingle()
            .Which.Should().BeOfType<InvoiceOverdueDomainEvent>();
    }

    [Theory]
    [InlineData(InvoiceStatus.Draft)]
    [InlineData(InvoiceStatus.Paid)]
    [InlineData(InvoiceStatus.Cancelled)]
    [InlineData(InvoiceStatus.Overdue)]
    public void MarkAsOverdue_FromInvalidStatus_ShouldThrowInvalidOperationException(InvoiceStatus invalidStatus)
    {
        var invoice = CreateDraftInvoice();
        ForceStatus(invoice, invalidStatus);

        var act = () => invoice.MarkAsOverdue(_correlationId);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{invalidStatus}*");
    }

    // ─── CANCEL ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(InvoiceStatus.Draft)]
    [InlineData(InvoiceStatus.Issued)]
    [InlineData(InvoiceStatus.Overdue)]
    public void Cancel_FromCancellableStatus_ShouldTransitionToCancelled(InvoiceStatus fromStatus)
    {
        var invoice = CreateDraftInvoice();
        ForceStatus(invoice, fromStatus);

        invoice.Cancel(_correlationId);

        invoice.Status.Should().Be(InvoiceStatus.Cancelled);
        invoice.CancelledAt.Should().NotBeNull();
    }

    [Fact]
    public void Cancel_ShouldRaiseInvoiceCancelledDomainEvent()
    {
        var invoice = CreateDraftInvoice();
        invoice.ClearDomainEvents();

        invoice.Cancel(_correlationId);

        invoice.DomainEvents.Should().ContainSingle()
            .Which.Should().BeOfType<InvoiceCancelledDomainEvent>();
    }

    [Theory]
    [InlineData(InvoiceStatus.Paid)]
    [InlineData(InvoiceStatus.Cancelled)]
    public void Cancel_FromPaidOrCancelled_ShouldThrowInvalidOperationException(InvoiceStatus invalidStatus)
    {
        var invoice = CreateDraftInvoice();
        ForceStatus(invoice, invalidStatus);

        var act = () => invoice.Cancel(_correlationId);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{invalidStatus}*");
    }

    // ─── DOMAIN EVENT ISOLATION ───────────────────────────────────────────────

    [Fact]
    public void ClearDomainEvents_ShouldRemoveAllEvents()
    {
        var invoice = CreateDraftInvoice();
        invoice.DomainEvents.Should().NotBeEmpty();

        invoice.ClearDomainEvents();

        invoice.DomainEvents.Should().BeEmpty();
    }

    // ─── HELPER ──────────────────────────────────────────────────────────────

    /// Forces a specific status bypassing business rules for test setup.
    private static void ForceStatus(Invoice invoice, InvoiceStatus status)
    {
        // Use the public API where possible; only bypass for setup of "unreachable" states.
        switch (status)
        {
            case InvoiceStatus.Draft:
                break; // initial state
            case InvoiceStatus.Issued:
                if (invoice.Status == InvoiceStatus.Draft) invoice.Issue(Guid.NewGuid());
                break;
            case InvoiceStatus.Overdue:
                if (invoice.Status == InvoiceStatus.Draft) invoice.Issue(Guid.NewGuid());
                if (invoice.Status == InvoiceStatus.Issued) invoice.MarkAsOverdue(Guid.NewGuid());
                break;
            case InvoiceStatus.Paid:
                if (invoice.Status == InvoiceStatus.Draft) invoice.Issue(Guid.NewGuid());
                if (invoice.Status is InvoiceStatus.Issued or InvoiceStatus.Overdue) invoice.MarkAsPaid(Guid.NewGuid());
                break;
            case InvoiceStatus.Cancelled:
                if (invoice.Status is InvoiceStatus.Draft or InvoiceStatus.Issued) invoice.Cancel(Guid.NewGuid());
                break;
        }
        invoice.ClearDomainEvents();
    }
}
