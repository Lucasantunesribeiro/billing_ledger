using BillingLedger.Billing.Api.Application.Commands;
using FluentValidation;

namespace BillingLedger.Billing.Api.Application.Validators;

public sealed class WebhookPaymentRequestValidator : AbstractValidator<WebhookPaymentRequest>
{
    public WebhookPaymentRequestValidator()
    {
        RuleFor(x => x.InvoiceId)
            .NotEmpty().WithMessage("InvoiceId is required.");

        RuleFor(x => x.ExternalPaymentId)
            .NotEmpty().WithMessage("ExternalPaymentId is required.")
            .MaximumLength(200).WithMessage("ExternalPaymentId must not exceed 200 characters.");

        RuleFor(x => x.Provider)
            .NotEmpty().WithMessage("Provider is required.")
            .MaximumLength(50).WithMessage("Provider must not exceed 50 characters.")
            .Matches(@"^[A-Za-z0-9_\-]+$").WithMessage("Provider contains invalid characters.");

        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("Amount must be positive.")
            .LessThanOrEqualTo(999_999_999).WithMessage("Amount exceeds maximum allowed value.");
    }
}
