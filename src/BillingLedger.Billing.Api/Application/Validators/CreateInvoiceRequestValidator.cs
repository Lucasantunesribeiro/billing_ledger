using BillingLedger.Billing.Api.Application.Commands;
using BillingLedger.SharedKernel.Primitives;
using FluentValidation;

namespace BillingLedger.Billing.Api.Application.Validators;

public sealed class CreateInvoiceRequestValidator : AbstractValidator<CreateInvoiceRequest>
{
    public CreateInvoiceRequestValidator()
    {
        RuleFor(x => x.CustomerId)
            .NotEmpty().WithMessage("CustomerId is required.");

        // Sem arredondamento silencioso: numeric(18,2) arredondaria no banco e o evento
        // publicado divergiria do valor persistido (ver ADR 0004).
        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("Amount must be greater than zero.")
            .LessThanOrEqualTo(999_999_999).WithMessage("Amount exceeds maximum allowed value.")
            .Must(Money.HasValidScale).WithMessage("Amount must have at most 2 decimal places.");

        RuleFor(x => x.Currency)
            .NotEmpty().WithMessage("Currency is required.")
            .Length(3).WithMessage("Currency must be exactly 3 characters.")
            .Matches(@"^[A-Z]{3}$").WithMessage("Currency must be a 3-letter ISO 4217 code (e.g. BRL, USD, EUR).");

        RuleFor(x => x.DueDate)
            .GreaterThan(DateTime.UtcNow).WithMessage("DueDate must be in the future.");

        RuleFor(x => x.ExternalReference)
            .MaximumLength(100).WithMessage("ExternalReference must not exceed 100 characters.")
            .When(x => x.ExternalReference is not null);
    }
}
