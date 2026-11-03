using BillingLedger.Billing.Api.Application.Commands;
using BillingLedger.SharedKernel.Primitives;
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
            .LessThanOrEqualTo(999_999_999).WithMessage("Amount exceeds maximum allowed value.")
            .Must(Money.HasValidScale).WithMessage("Amount must have at most 2 decimal places.");

        // Opcional por compatibilidade com provedores que nao enviam moeda; quando ausente,
        // o Billing assume a moeda da fatura. Quando presente, precisa bater com a fatura.
        RuleFor(x => x.Currency)
            .Matches(@"^[A-Z]{3}$").WithMessage("Currency must be a 3-letter ISO 4217 code (e.g. BRL, USD, EUR).")
            .When(x => x.Currency is not null);
    }
}
