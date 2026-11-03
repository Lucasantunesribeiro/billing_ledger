using System.Globalization;
using BillingLedger.Payments.Worker.Domain;
using FluentAssertions;

namespace BillingLedger.Payments.UnitTests.Domain;

public class PaymentAttemptTests
{
    private static PaymentAttempt Create(decimal amount) =>
        PaymentAttempt.Create(Guid.NewGuid(), "pix-1", "PIX", amount, Guid.NewGuid());

    [Theory]
    [InlineData("0")]
    [InlineData("-0.01")]
    public void Create_WithNonPositiveAmount_ShouldThrow(string raw)
    {
        var act = () => Create(decimal.Parse(raw, CultureInfo.InvariantCulture));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_WithMoreThanTwoDecimals_ShouldThrowInsteadOfRounding()
    {
        var act = () => Create(150.005m);

        act.Should().Throw<ArgumentException>().WithMessage("*decimal places*");
    }

    [Fact]
    public void Create_ShouldKeepExactDecimalAmount()
    {
        Create(150.35m).Amount.Should().Be(150.35m);
    }
}
