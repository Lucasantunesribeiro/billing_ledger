using System.Globalization;
using BillingLedger.Billing.Api.Application.Commands;
using BillingLedger.Billing.Api.Application.Validators;
using FluentAssertions;

namespace BillingLedger.Billing.UnitTests.Application;

public class MoneyValidationTests
{
    private readonly CreateInvoiceRequestValidator _invoiceValidator = new();
    private readonly WebhookPaymentRequestValidator _webhookValidator = new();

    private static CreateInvoiceRequest Invoice(decimal amount) =>
        new(Guid.NewGuid(), amount, "BRL", DateTime.UtcNow.AddDays(10), null);

    private static WebhookPaymentRequest Webhook(decimal amount, string? currency = null) =>
        new(Guid.NewGuid(), "pix-1", "PIX", amount, currency);

    [Theory]
    [InlineData("0")]
    [InlineData("-0.01")]
    [InlineData("10.001")]
    [InlineData("999999999.01")]
    public void CreateInvoice_WithInvalidAmount_ShouldFail(string raw)
    {
        var result = _invoiceValidator.Validate(Invoice(decimal.Parse(raw, CultureInfo.InvariantCulture)));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateInvoiceRequest.Amount));
    }

    [Theory]
    [InlineData("0.01")]
    [InlineData("10.10")]
    [InlineData("999999999")]
    public void CreateInvoice_WithValidAmount_ShouldPass(string raw)
    {
        var result = _invoiceValidator.Validate(Invoice(decimal.Parse(raw, CultureInfo.InvariantCulture)));

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1.005")]
    public void Webhook_WithInvalidAmount_ShouldFail(string raw)
    {
        var result = _webhookValidator.Validate(Webhook(decimal.Parse(raw, CultureInfo.InvariantCulture)));

        result.Errors.Should().Contain(e => e.PropertyName == nameof(WebhookPaymentRequest.Amount));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("BRL", true)]
    [InlineData("brl", false)]
    [InlineData("R$", false)]
    public void Webhook_Currency_IsOptionalButMustBeIso4217WhenPresent(string? currency, bool valid)
    {
        var result = _webhookValidator.Validate(Webhook(150m, currency));

        result.IsValid.Should().Be(valid);
    }
}
