namespace TuitionBilling.Domain.UnitTests;

public class LedgerTests
{
    private static readonly Guid Receivable = Guid.NewGuid();
    private static readonly Guid Revenue = Guid.NewGuid();
    private static readonly Guid Clearing = Guid.NewGuid();
    private static readonly Guid Fee = Guid.NewGuid();
    private static readonly Guid Bank = Guid.NewGuid();

    private static LedgerTransaction Draft(LedgerTransactionKind kind = LedgerTransactionKind.InvoiceIssued) =>
        LedgerTransaction.Draft(kind, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow, "Тестовая операция");

    [Fact]
    public void Post_BalancedTransaction_Succeeds()
    {
        var transaction = Draft()
            .Debit(Receivable, 120_000m)
            .Credit(Revenue, 120_000m)
            .Post();

        Assert.True(transaction.IsPosted);
        Assert.Equal(transaction.DebitTotal, transaction.CreditTotal);
    }

    [Fact]
    public void Post_UnbalancedTransaction_Throws()
    {
        var transaction = Draft()
            .Debit(Receivable, 120_000m)
            .Credit(Revenue, 119_000m);

        Assert.Throws<DomainException>(() => transaction.Post());
    }

    [Fact]
    public void Post_SingleEntry_Throws()
    {
        var transaction = Draft().Debit(Receivable, 100m);

        Assert.Throws<DomainException>(() => transaction.Post());
    }

    [Fact]
    public void Post_SplitAcrossThreeAccounts_Succeeds()
    {
        var transaction = Draft(LedgerTransactionKind.GatewaySettlement)
            .Debit(Bank, 39_600m)
            .Debit(Fee, 400m)
            .Credit(Clearing, 40_000m)
            .Post();

        Assert.Equal(40_000m, transaction.DebitTotal);
        Assert.Equal(3, transaction.Entries.Count);
    }

    [Fact]
    public void Debit_AfterPosting_Throws()
    {
        var transaction = Draft().Debit(Receivable, 100m).Credit(Revenue, 100m).Post();

        Assert.Throws<DomainException>(() => transaction.Debit(Receivable, 1m));
    }

    [Fact]
    public void Debit_WithNonPositiveAmount_Throws()
    {
        Assert.Throws<DomainException>(() => Draft().Debit(Receivable, 0m));
    }

    [Fact]
    public void Reverse_MirrorsEveryEntry()
    {
        var original = Draft()
            .Debit(Receivable, 120_000m)
            .Credit(Revenue, 120_000m)
            .Post();

        var reversal = original.Reverse(DateTimeOffset.UtcNow, "Ошибочное начисление");

        Assert.Equal(LedgerTransactionKind.Reversal, reversal.Kind);
        Assert.Equal(original.Id.ToString(), reversal.Reference);
        Assert.Equal(EntrySide.Credit, reversal.Entries.Single(e => e.AccountId == Receivable).Side);
        Assert.Equal(EntrySide.Debit, reversal.Entries.Single(e => e.AccountId == Revenue).Side);
        Assert.Equal(0m, original.Entries.Concat(reversal.Entries).Sum(e => e.Signed));
    }

    [Fact]
    public void Reverse_UnpostedDraft_Throws()
    {
        var draft = Draft().Debit(Receivable, 100m).Credit(Revenue, 100m);

        Assert.Throws<DomainException>(() => draft.Reverse(DateTimeOffset.UtcNow, "Сторно черновика"));
    }

    [Theory]
    [InlineData(ChartOfAccounts.StudentReceivable, 120_000, 40_000, 80_000)]
    [InlineData(ChartOfAccounts.TuitionRevenue, 0, 120_000, 120_000)]
    public void BalanceOf_DependsOnAccountKind(string code, decimal debit, decimal credit, decimal expected)
    {
        var account = new LedgerAccount(code);

        Assert.Equal(expected, account.BalanceOf(debit, credit));
    }

    [Fact]
    public void LedgerAccount_TakesNameAndKindFromChartOfAccounts()
    {
        var account = new LedgerAccount(ChartOfAccounts.GatewayFee);

        Assert.Equal(AccountKind.Expense, account.Kind);
        Assert.Equal("Комиссия платёжного провайдера", account.Name);
    }
}
