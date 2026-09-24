namespace TuitionBilling.Domain.UnitTests;

public class MoneyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void EnsurePositive_RejectsNonPositiveAmounts(decimal amount)
    {
        Assert.Throws<DomainException>(() => Money.EnsurePositive(amount, "Сумма"));
    }

    [Fact]
    public void EnsurePositive_RejectsFractionsOfKopeck()
    {
        Assert.Throws<DomainException>(() => Money.EnsurePositive(100.001m, "Сумма"));
    }

    [Fact]
    public void Round_UsesAccountingRoundingNotBankers()
    {
        Assert.Equal(0.13m, Money.Round(0.125m));
        Assert.Equal(0.15m, Money.Round(0.145m));
    }

    // Ради этого теста деньги и живут в decimal: в double такое равенство не сходится.
    [Fact]
    public void Decimal_KeepsExactSumOfTenPartsOfOneThird()
    {
        var total = 0m;
        for (var i = 0; i < 3; i++)
        {
            total += 0.1m;
        }

        Assert.Equal(0.3m, total);
    }
}
