using System.Globalization;
using BillingLedger.SharedKernel.Primitives;
using FluentAssertions;

namespace BillingLedger.Billing.UnitTests.Domain;

/// <summary>
/// Regras de dinheiro da ADR 0004: decimal com escala 2, arredondamento explicito
/// (ToEven por padrao), soma exata e rateio sem perder/criar centavos.
/// </summary>
public class MoneyArithmeticTests
{
    // ─── Escala / validacao ──────────────────────────────────────────────────

    [Theory]
    [InlineData("10.005")]
    [InlineData("0.001")]
    [InlineData("1.999")]
    public void Of_WithMoreThanTwoDecimalPlaces_ShouldRejectInsteadOfRoundingSilently(string raw)
    {
        var amount = decimal.Parse(raw, CultureInfo.InvariantCulture);

        var act = () => Money.Of(amount, "BRL");

        act.Should().Throw<ArgumentException>().WithMessage("*decimal places*");
    }

    [Fact]
    public void Of_WithTrailingZerosBeyondScale_ShouldBeAccepted()
    {
        // 10.100m tem escala 3 na representacao, mas o valor so tem 2 casas significativas.
        var money = Money.Of(10.100m, "BRL");

        money.Amount.Should().Be(10.10m);
    }

    [Fact]
    public void Of_AboveNumeric18_2Capacity_ShouldThrow()
    {
        var act = () => Money.Of(Money.MaxAmount + 0.01m, "BRL");

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("BR")]
    [InlineData("BRLL")]
    [InlineData("R$1")]
    [InlineData("12A")]
    public void Of_WithInvalidIsoCurrency_ShouldThrow(string currency)
    {
        var act = () => Money.Of(10m, currency);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToString_ShouldUseInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("pt-BR");

            Money.Of(1234.5m, "BRL").ToString().Should().Be("1234.50 BRL");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // ─── Arredondamento explicito ────────────────────────────────────────────

    [Theory]
    [InlineData("0.125", "0.12")] // meio exato, digito anterior par → mantem
    [InlineData("0.135", "0.14")] // meio exato, digito anterior impar → sobe
    [InlineData("2.675", "2.68")] // em double seria 2.67499999...; em decimal e exato
    [InlineData("-0.125", "-0.12")]
    [InlineData("10.004", "10.00")]
    [InlineData("10.006", "10.01")]
    public void Round_Default_ShouldUseBankersRounding(string raw, string expected)
    {
        var rounded = Money.Round(decimal.Parse(raw, CultureInfo.InvariantCulture));

        rounded.Should().Be(decimal.Parse(expected, CultureInfo.InvariantCulture));
        Money.DefaultRounding.Should().Be(MidpointRounding.ToEven);
    }

    [Theory]
    [InlineData("0.125", "0.13")]
    [InlineData("0.135", "0.14")]
    [InlineData("-0.125", "-0.13")]
    public void Round_AwayFromZero_WhenRequestedExplicitly(string raw, string expected)
    {
        var rounded = Money.Round(decimal.Parse(raw, CultureInfo.InvariantCulture), MidpointRounding.AwayFromZero);

        rounded.Should().Be(decimal.Parse(expected, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Round_ThenOf_ShouldProduceValidMoneyFromComputedValue()
    {
        // 2% de juros sobre 99,99 = 1,9998 → precisa de arredondamento explicito.
        var interest = 99.99m * 0.02m;

        var act = () => Money.Of(interest, "BRL");
        act.Should().Throw<ArgumentException>();

        Money.Of(Money.Round(interest), "BRL").Amount.Should().Be(2.00m);
    }

    // ─── Soma de muitas entradas ─────────────────────────────────────────────

    [Fact]
    public void Sum_OfManyCents_ShouldBeExact()
    {
        var entries = Enumerable.Range(0, 100_000).Select(_ => Money.Of(0.01m, "BRL"));

        var total = Money.Sum(entries, "BRL");

        total.Should().Be(Money.Of(1_000m, "BRL"));
    }

    [Fact]
    public void Sum_OfManyTenCents_InDouble_WouldDrift_ButDecimalDoesNot()
    {
        const int n = 1_000_000;
        var asDouble = Enumerable.Repeat(0.1d, n).Sum();
        var asMoney = Money.Sum(Enumerable.Repeat(Money.Of(0.10m, "BRL"), n), "BRL");

        asDouble.Should().NotBe(100_000d, "ponto flutuante acumula erro de representacao");
        asMoney.Amount.Should().Be(100_000.00m);
    }

    [Fact]
    public void Sum_WithMixedCurrencies_ShouldThrow()
    {
        var values = new[] { Money.Of(1m, "BRL"), Money.Of(1m, "USD") };

        var act = () => Money.Sum(values, "BRL");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Sum_OfEmpty_ShouldBeZeroInCurrency()
    {
        Money.Sum([], "brl").Should().Be(Money.Of(0m, "BRL"));
    }

    // ─── Rateio / split com centavo residual ─────────────────────────────────

    [Fact]
    public void Allocate_100By3_ShouldGiveResidualCentToFirstPart()
    {
        var parts = Money.Of(100m, "BRL").Allocate(3);

        parts.Select(p => p.Amount).Should().Equal(33.34m, 33.33m, 33.33m);
        Money.Sum(parts, "BRL").Amount.Should().Be(100m);
        parts.Should().AllSatisfy(p => p.Currency.Should().Be("BRL"));
    }

    [Fact]
    public void Allocate_FiveCentsBy3_ShouldDistributeTwoResidualCents()
    {
        var parts = Money.Of(0.05m, "BRL").Allocate(3);

        parts.Select(p => p.Amount).Should().Equal(0.02m, 0.02m, 0.01m);
    }

    [Fact]
    public void Allocate_OneCentBy3_ShouldNotCreateMoney()
    {
        var parts = Money.Of(0.01m, "BRL").Allocate(3);

        parts.Select(p => p.Amount).Should().Equal(0.01m, 0m, 0m);
    }

    [Fact]
    public void Allocate_ByRatios_ShouldBeProportionalAndExact()
    {
        // 70/30 de 10,01 = 7,007 / 3,003 → 7,00 / 3,00 + 1 centavo residual na 1a parcela
        var parts = Money.Of(10.01m, "BRL").Allocate(70, 30);

        parts.Select(p => p.Amount).Should().Equal(7.01m, 3.00m);
    }

    [Fact]
    public void Allocate_WithZeroRatio_ShouldNeverReceiveResidual()
    {
        var parts = Money.Of(0.03m, "BRL").Allocate(0, 1, 1);

        parts.Select(p => p.Amount).Should().Equal(0m, 0.02m, 0.01m);
    }

    [Fact]
    public void Allocate_AnyAmountAndParts_ShouldAlwaysSumToOriginal()
    {
        var random = new Random(20261103);
        for (var i = 0; i < 2_000; i++)
        {
            var amount = random.Next(1, 10_000_000) / 100m;
            var partsCount = random.Next(1, 37);

            var parts = Money.Of(amount, "BRL").Allocate(partsCount);

            Money.Sum(parts, "BRL").Amount.Should().Be(amount);
            (parts.Max(p => p.Amount) - parts.Min(p => p.Amount)).Should().BeLessThanOrEqualTo(0.01m);
            parts.Should().AllSatisfy(p => Money.HasValidScale(p.Amount).Should().BeTrue());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Allocate_WithNonPositiveParts_ShouldThrow(int parts)
    {
        var act = () => Money.Of(10m, "BRL").Allocate(parts);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Allocate_WithAllZeroRatios_ShouldThrow()
    {
        var act = () => Money.Of(10m, "BRL").Allocate(0, 0);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Allocate_OnUnvalidatedNegativeMoney_ShouldThrow()
    {
        // O construtor do record nao valida; Allocate protege o algoritmo mesmo assim.
        var act = () => new Money(-10m, "BRL").Allocate(2);

        act.Should().Throw<InvalidOperationException>();
    }
}
