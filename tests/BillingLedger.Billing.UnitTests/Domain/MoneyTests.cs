using BillingLedger.SharedKernel.Primitives;
using FluentAssertions;

namespace BillingLedger.Billing.UnitTests.Domain;

public class MoneyTests
{
    [Fact]
    public void Of_WithValidAmount_ShouldCreateMoney()
    {
        var money = Money.Of(100m, "BRL");

        money.Amount.Should().Be(100m);
        money.Currency.Should().Be("BRL");
    }

    [Fact]
    public void Of_ShouldNormalizeCurrencyToUpperCase()
    {
        var money = Money.Of(50m, "brl");

        money.Currency.Should().Be("BRL");
    }

    [Fact]
    public void Of_WithNegativeAmount_ShouldThrowArgumentOutOfRangeException()
    {
        var act = () => Money.Of(-1m, "BRL");

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Of_WithZeroAmount_ShouldBeAllowed()
    {
        var money = Money.Of(0m, "BRL");

        money.Amount.Should().Be(0m);
    }

    [Fact]
    public void Of_WithNullOrEmptyCurrency_ShouldThrowArgumentException()
    {
        var actNull = () => Money.Of(10m, null!);
        var actEmpty = () => Money.Of(10m, "");
        var actWhitespace = () => Money.Of(10m, "  ");

        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
        actWhitespace.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Add_WithSameCurrency_ShouldReturnSum()
    {
        var a = Money.Of(100m, "BRL");
        var b = Money.Of(50m, "BRL");

        var result = a.Add(b);

        result.Amount.Should().Be(150m);
        result.Currency.Should().Be("BRL");
    }

    [Fact]
    public void Add_WithDifferentCurrencies_ShouldThrowInvalidOperationException()
    {
        var brl = Money.Of(100m, "BRL");
        var usd = Money.Of(50m, "USD");

        var act = () => brl.Add(usd);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*BRL*USD*");
    }

    [Fact]
    public void Equality_WithSameAmountAndCurrency_ShouldBeEqual()
    {
        var a = Money.Of(100m, "BRL");
        var b = Money.Of(100m, "BRL");

        a.Should().Be(b);
    }

    [Fact]
    public void Equality_WithDifferentAmounts_ShouldNotBeEqual()
    {
        var a = Money.Of(100m, "BRL");
        var b = Money.Of(200m, "BRL");

        a.Should().NotBe(b);
    }
}
