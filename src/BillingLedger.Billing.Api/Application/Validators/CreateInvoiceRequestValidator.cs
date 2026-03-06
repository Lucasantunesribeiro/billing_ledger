using BillingLedger.Billing.Api.Application.Commands;
using FluentValidation;

namespace BillingLedger.Billing.Api.Application.Validators;

public sealed class CreateInvoiceRequestValidator : AbstractValidator<CreateInvoiceRequest>
{
    public CreateInvoiceRequestValidator()
    {
        RuleFor(x => x.CustomerId)
            .NotEmpty().WithMessage("CustomerId is required.");

        RuleFor(x => x.Amount)
            .GreaterThanOrEqualTo(0).WithMessage("Amount must be non-negative.")
            .LessThanOrEqualTo(999_999_999).WithMessage("Amount exceeds maximum allowed value.");

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
