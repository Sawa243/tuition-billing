using TuitionBilling.Domain.Common;

namespace TuitionBilling.Domain.Ledger;

/// <summary>
/// Проводка. Создаётся только вместе с операцией и после этого не меняется:
/// ошибка исправляется обратной операцией, а не правкой строки.
/// </summary>
public sealed class LedgerEntry : Entity
{
    private LedgerEntry()
    {
    }

    internal LedgerEntry(Guid transactionId, Guid accountId, EntrySide side, decimal amount)
    {
        TransactionId = transactionId;
        AccountId = accountId;
        Side = side;
        Amount = Money.EnsurePositive(amount, "Сумма проводки");
    }

    public Guid TransactionId { get; private set; }

    public Guid AccountId { get; private set; }

    public EntrySide Side { get; private set; }

    public decimal Amount { get; private set; }

    public decimal Signed => Side == EntrySide.Debit ? Amount : -Amount;
}
