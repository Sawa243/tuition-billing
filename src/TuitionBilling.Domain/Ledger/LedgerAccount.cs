using TuitionBilling.Domain.Common;

namespace TuitionBilling.Domain.Ledger;

public sealed class LedgerAccount : Entity
{
    private LedgerAccount()
    {
        Code = string.Empty;
        Name = string.Empty;
    }

    public LedgerAccount(string code, Guid? contractId = null)
    {
        Code = code;
        Name = ChartOfAccounts.NameOf(code);
        Kind = ChartOfAccounts.KindOf(code);
        ContractId = contractId;
    }

    public string Code { get; private set; }

    public string Name { get; private set; }

    public AccountKind Kind { get; private set; }

    /// <summary>Заполнен только у дебиторки: там счёт свой на каждый договор.</summary>
    public Guid? ContractId { get; private set; }

    /// <summary>
    /// Для активов и расходов сальдо считается как дебет минус кредит,
    /// для доходов и обязательств — наоборот.
    /// </summary>
    public decimal BalanceOf(decimal debitTotal, decimal creditTotal) =>
        Kind is AccountKind.Asset or AccountKind.Expense ? debitTotal - creditTotal : creditTotal - debitTotal;
}
