using TuitionBilling.Domain.Common;

namespace TuitionBilling.Domain.Contracts;

public sealed class InstallmentItem : Entity
{
    private InstallmentItem()
    {
    }

    internal InstallmentItem(Guid planId, int number, decimal amount, DateOnly dueOn)
    {
        PlanId = planId;
        Number = number;
        Amount = Money.EnsurePositive(amount, $"Сумма части {number}");
        DueOn = dueOn;
    }

    public Guid PlanId { get; private set; }

    public int Number { get; private set; }

    public decimal Amount { get; private set; }

    public decimal PaidAmount { get; private set; }

    public DateOnly DueOn { get; private set; }

    public bool IsPaid => PaidAmount >= Amount;

    public decimal Outstanding => Amount - PaidAmount;

    /// <summary>Гасит часть платежа и возвращает, сколько именно ушло в эту часть.</summary>
    internal decimal Apply(decimal amount)
    {
        var applied = Math.Min(amount, Outstanding);
        PaidAmount += applied;
        return applied;
    }

    internal decimal Withdraw(decimal amount)
    {
        var withdrawn = Math.Min(amount, PaidAmount);
        PaidAmount -= withdrawn;
        return withdrawn;
    }
}
