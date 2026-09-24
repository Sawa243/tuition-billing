namespace TuitionBilling.Domain.UnitTests;

public class ContractTests
{
    [Fact]
    public void IssueInvoice_OnActiveContract_AddsInvoice()
    {
        var contract = TestData.Contract();

        var invoice = TestData.Invoice(contract);

        Assert.Single(contract.Invoices);
        Assert.Equal(InvoiceStatus.Issued, invoice.Status);
        Assert.Equal(120_000m, contract.IssuedAmount);
    }

    [Fact]
    public void IssueInvoice_OnTerminatedContract_Throws()
    {
        var contract = TestData.Contract();
        contract.Terminate();

        Assert.Throws<DomainException>(() => TestData.Invoice(contract));
    }

    [Fact]
    public void IssueInvoice_ForSamePeriodTwice_Throws()
    {
        var contract = TestData.Contract();
        TestData.Invoice(contract);

        Assert.Throws<DomainException>(() => TestData.Invoice(contract));
    }

    [Fact]
    public void IssueInvoice_AboveContractTotal_Throws()
    {
        var contract = TestData.Contract(total: 200_000m);
        TestData.Invoice(contract, 120_000m, "2026/2027-1");

        Assert.Throws<DomainException>(() => TestData.Invoice(contract, 120_000m, "2026/2027-2"));
    }

    [Fact]
    public void IssueInvoice_AfterCancelingPreviousOne_UsesPeriodAgain()
    {
        var contract = TestData.Contract();
        var first = TestData.Invoice(contract);
        first.Cancel();

        var second = TestData.Invoice(contract, 130_000m);

        Assert.Equal(InvoiceStatus.Issued, second.Status);
        Assert.Equal(130_000m, contract.IssuedAmount);
    }

    [Fact]
    public void Restore_AfterTermination_Throws()
    {
        var contract = TestData.Contract();
        contract.Terminate();

        Assert.Throws<DomainException>(contract.Restore);
    }
}
