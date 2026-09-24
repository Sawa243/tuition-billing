namespace TuitionBilling.Domain.UnitTests;

public class InvoiceTests
{
    [Fact]
    public void RegisterPayment_Partial_MovesToPartiallyPaid()
    {
        var invoice = TestData.Invoice(TestData.Contract());

        invoice.RegisterPayment(50_000m);

        Assert.Equal(InvoiceStatus.PartiallyPaid, invoice.Status);
        Assert.Equal(70_000m, invoice.Outstanding);
    }

    [Fact]
    public void RegisterPayment_Full_MovesToPaid()
    {
        var invoice = TestData.Invoice(TestData.Contract());

        invoice.RegisterPayment(120_000m);

        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal(0m, invoice.Outstanding);
    }

    [Fact]
    public void RegisterPayment_AboveOutstanding_Throws()
    {
        var invoice = TestData.Invoice(TestData.Contract());
        invoice.RegisterPayment(100_000m);

        Assert.Throws<DomainException>(() => invoice.RegisterPayment(20_000.01m));
    }

    [Fact]
    public void RegisterRefund_ReturnsInvoiceToUnpaidState()
    {
        var invoice = TestData.Invoice(TestData.Contract());
        invoice.RegisterPayment(120_000m);

        invoice.RegisterRefund(120_000m);

        Assert.Equal(InvoiceStatus.Issued, invoice.Status);
        Assert.Equal(120_000m, invoice.Outstanding);
        Assert.Equal(120_000m, invoice.RefundedAmount);
    }

    [Fact]
    public void RegisterRefund_AbovePaidAmount_Throws()
    {
        var invoice = TestData.Invoice(TestData.Contract());
        invoice.RegisterPayment(30_000m);

        Assert.Throws<DomainException>(() => invoice.RegisterRefund(30_000.01m));
    }

    [Fact]
    public void Cancel_WithPaymentRegistered_Throws()
    {
        var invoice = TestData.Invoice(TestData.Contract());
        invoice.RegisterPayment(1_000m);

        Assert.Throws<DomainException>(invoice.Cancel);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(31, true)]
    public void IsOverdue_DependsOnDueDateAndOutstanding(int daysFromIssue, bool expected)
    {
        var invoice = TestData.Invoice(TestData.Contract());

        Assert.Equal(expected, invoice.IsOverdue(TestData.Today.AddDays(daysFromIssue)));
    }

    [Fact]
    public void IsOverdue_WhenFullyPaid_IsFalse()
    {
        var invoice = TestData.Invoice(TestData.Contract());
        invoice.RegisterPayment(120_000m);

        Assert.False(invoice.IsOverdue(TestData.Today.AddDays(365)));
    }
}
