namespace BillingLedger.SharedKernel.Primitives;

public readonly record struct Money(decimal Amount, string Currency)
{
    public static Money Of(decimal amount, string currency)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        return new Money(amount, currency.ToUpperInvariant());
    }

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    private void EnsureSameCurrency(Money other)
    {
        if (Currency != other.Currency)
            throw new InvalidOperationException($"Currency mismatch: {Currency} vs {other.Currency}");
    }

    public override string ToString() => $"{Amount:F2} {Currency}";
}
