using System.Globalization;
using BillingLedger.Billing.Api.Application.Consumers;
using BillingLedger.Billing.Api.Domain.Aggregates;
using BillingLedger.Billing.Api.Domain.Repositories;
using BillingLedger.Contracts.Payments;
using BillingLedger.SharedKernel.Primitives;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BillingLedger.Billing.UnitTests.Application;

/// <summary>
/// Um pagamento so quita a fatura quando valor e moeda batem exatamente (decimal).
/// </summary>
public class PaymentConfirmedConsumerTests
{
    private readonly IInvoiceRepository _repository = Substitute.For<IInvoiceRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private Invoice IssuedInvoice(decimal amount = 300.00m, string currency = "BRL")
    {
        var invoice = Invoice.Create(Guid.NewGuid(), Money.Of(amount, currency), DateTime.UtcNow.AddDays(30),
            $"INV-{Guid.NewGuid():N}", Guid.NewGuid());
        invoice.Issue(Guid.NewGuid());
        _repository.GetByIdAsync(invoice.Id.Value, Arg.Any<CancellationToken>()).Returns(invoice);
        return invoice;
    }

    private async Task ConsumeAsync(PaymentConfirmedV1 message)
    {
        var context = Substitute.For<ConsumeContext<PaymentConfirmedV1>>();
        context.Message.Returns(message);
        context.CancellationToken.Returns(CancellationToken.None);

        var consumer = new PaymentConfirmedConsumer(_repository, _unitOfWork,
            NullLogger<PaymentConfirmedConsumer>.Instance);
        await consumer.Consume(context);
    }

    private static PaymentConfirmedV1 Confirmation(Guid invoiceId, decimal? amount, string? currency = "BRL") => new()
    {
        InvoiceId = invoiceId,
        ExternalPaymentId = $"pix-{Guid.NewGuid():N}",
        Amount = amount,
        Currency = currency,
        ConfirmedAt = DateTime.UtcNow,
        CorrelationId = Guid.NewGuid()
    };

    [Fact]
    public async Task ExactAmount_ShouldMarkInvoiceAsPaid()
    {
        var invoice = IssuedInvoice(300.00m);

        await ConsumeAsync(Confirmation(invoice.Id.Value, 300m));

        invoice.Status.Should().Be(InvoiceStatus.Paid);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("299.99")]
    [InlineData("300.01")]
    [InlineData("0.01")]
    public async Task DifferentAmount_ShouldNotMarkInvoiceAsPaid(string paid)
    {
        var invoice = IssuedInvoice(300.00m);

        await ConsumeAsync(Confirmation(invoice.Id.Value, decimal.Parse(paid, CultureInfo.InvariantCulture)));

        invoice.Status.Should().Be(InvoiceStatus.Issued, "pagamento parcial/excedente exige conciliacao");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DifferentCurrency_ShouldNotMarkInvoiceAsPaid()
    {
        var invoice = IssuedInvoice(300.00m, "BRL");

        await ConsumeAsync(Confirmation(invoice.Id.Value, 300m, "USD"));

        invoice.Status.Should().Be(InvoiceStatus.Issued);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingCurrency_ShouldAssumeInvoiceCurrency()
    {
        var invoice = IssuedInvoice(300.00m, "BRL");

        await ConsumeAsync(Confirmation(invoice.Id.Value, 300m, currency: null));

        invoice.Status.Should().Be(InvoiceStatus.Paid);
    }

    [Fact]
    public async Task LegacyMessageWithoutAmount_ShouldKeepPreviousBehaviour()
    {
        var invoice = IssuedInvoice(300.00m);

        await ConsumeAsync(Confirmation(invoice.Id.Value, amount: null, currency: null));

        invoice.Status.Should().Be(InvoiceStatus.Paid);
    }

    [Fact]
    public async Task AlreadyPaid_ShouldBeNoOp()
    {
        var invoice = IssuedInvoice(300.00m);
        invoice.MarkAsPaid(Guid.NewGuid());

        await ConsumeAsync(Confirmation(invoice.Id.Value, 300m));

        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
