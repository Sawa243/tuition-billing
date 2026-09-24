namespace TuitionBilling.Domain.UnitTests;

public class InstallmentPlanTests
{
    [Theory]
    [InlineData(120_000, 3)]
    [InlineData(100_000, 3)]
    [InlineData(99_999.99, 7)]
    [InlineData(1_000.01, 12)]
    public void SplitIntoInstallments_PartsAlwaysSumUpToInvoiceAmount(decimal amount, int parts)
    {
        var invoice = TestData.Invoice(TestData.Contract(), amount);

        var plan = invoice.SplitIntoInstallments(parts, TestData.Today.AddDays(30), 30);

        Assert.Equal(parts, plan.Items.Count);
        Assert.Equal(amount, plan.Items.Sum(i => i.Amount));
    }

    [Fact]
    public void SplitIntoInstallments_RemainderGoesToTheLastPart()
    {
        var invoice = TestData.Invoice(TestData.Contract(), 100_000m);

        var plan = invoice.SplitIntoInstallments(3, TestData.Today.AddDays(30), 30);

        Assert.Equal(33_333.33m, plan.Items[0].Amount);
        Assert.Equal(33_333.33m, plan.Items[1].Amount);
        Assert.Equal(33_333.34m, plan.Items[2].Amount);
    }

    [Fact]
    public void SplitIntoInstallments_ShiftsInvoiceDueDateToTheLastPart()
    {
        var invoice = TestData.Invoice(TestData.Contract());

        var plan = invoice.SplitIntoInstallments(4, TestData.Today.AddDays(30), 30);

        Assert.Equal(plan.Items[^1].DueOn, invoice.DueOn);
    }

    [Fact]
    public void SplitIntoInstallments_AfterPayment_Throws()
    {
        var invoice = TestData.Invoice(TestData.Contract());
        invoice.RegisterPayment(1_000m);

        Assert.Throws<DomainException>(() => invoice.SplitIntoInstallments(3, TestData.Today.AddDays(30), 30));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(13)]
    public void SplitIntoInstallments_WithUnsupportedPartCount_Throws(int parts)
    {
        var invoice = TestData.Invoice(TestData.Contract());

        Assert.Throws<DomainException>(() => invoice.SplitIntoInstallments(parts, TestData.Today.AddDays(30), 30));
    }

    [Fact]
    public void RegisterPayment_FillsInstallmentsInOrder()
    {
        var invoice = TestData.Invoice(TestData.Contract(), 120_000m);
        var plan = invoice.SplitIntoInstallments(3, TestData.Today.AddDays(30), 30);

        invoice.RegisterPayment(50_000m);

        Assert.True(plan.Items[0].IsPaid);
        Assert.Equal(10_000m, plan.Items[1].PaidAmount);
        Assert.Equal(0m, plan.Items[2].PaidAmount);
    }

    [Fact]
    public void RegisterRefund_UnwindsInstallmentsFromTheEnd()
    {
        var invoice = TestData.Invoice(TestData.Contract(), 120_000m);
        var plan = invoice.SplitIntoInstallments(3, TestData.Today.AddDays(30), 30);
        invoice.RegisterPayment(120_000m);

        invoice.RegisterRefund(40_000m);

        Assert.True(plan.Items[0].IsPaid);
        Assert.True(plan.Items[1].IsPaid);
        Assert.Equal(0m, plan.Items[2].PaidAmount);
    }
}
