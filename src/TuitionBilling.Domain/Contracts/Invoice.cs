using TuitionBilling.Domain.Common;

namespace TuitionBilling.Domain.Contracts;

/// <summary>
/// Начисление за учебный период. PaidAmount здесь — витрина: настоящая правда
/// про деньги лежит в журнале проводок, а суточная сверка ежедневно проверяет,
/// что витрина и журнал сходятся.
/// </summary>
public sealed class Invoice : Entity
{
    private Invoice()
    {
        PeriodCode = string.Empty;
    }

    internal Invoice(Guid contractId, string periodCode, decimal amount, DateOnly issuedOn, DateOnly dueOn)
    {
        if (string.IsNullOrWhiteSpace(periodCode))
        {
            throw new DomainException("Код периода обязателен.");
        }

        if (dueOn < issuedOn)
        {
            throw new DomainException("Срок оплаты не может быть раньше даты начисления.");
        }

        ContractId = contractId;
        PeriodCode = periodCode.Trim();
        Amount = amount;
        IssuedOn = issuedOn;
        DueOn = dueOn;
        Status = InvoiceStatus.Issued;
    }

    public Guid ContractId { get; private set; }

    /// <summary>Учебный период в виде "2026/2027-1" — год плюс номер семестра.</summary>
    public string PeriodCode { get; private set; }

    public decimal Amount { get; private set; }

    /// <summary>Оплачено за вычетом возвратов.</summary>
    public decimal PaidAmount { get; private set; }

    public decimal RefundedAmount { get; private set; }

    public DateOnly IssuedOn { get; private set; }

    public DateOnly DueOn { get; private set; }

    public InvoiceStatus Status { get; private set; }

    public InstallmentPlan? InstallmentPlan { get; private set; }

    public decimal Outstanding => Amount - PaidAmount;

    public bool IsOverdue(DateOnly today) => Outstanding > 0m && today > DueOn && Status != InvoiceStatus.Canceled;

    public InstallmentPlan SplitIntoInstallments(int partCount, DateOnly firstDueOn, int intervalDays)
    {
        if (Status == InvoiceStatus.Canceled)
        {
            throw new DomainException("Отменённое начисление нельзя разбить на рассрочку.");
        }

        if (PaidAmount > 0m)
        {
            throw new DomainException("Начисление уже частично оплачено, рассрочку оформлять поздно.");
        }

        InstallmentPlan = new InstallmentPlan(Id, Amount, partCount, firstDueOn, intervalDays);
        DueOn = InstallmentPlan.Items[^1].DueOn;
        return InstallmentPlan;
    }

    public void RegisterPayment(decimal amount)
    {
        if (Status == InvoiceStatus.Canceled)
        {
            throw new DomainException("Начисление отменено, зачесть оплату нельзя.");
        }

        var value = Money.EnsurePositive(amount, "Сумма оплаты");
        if (value > Outstanding)
        {
            throw new DomainException($"Переплата по начислению {PeriodCode}: к оплате {Outstanding}, пришло {value}.");
        }

        PaidAmount += value;
        InstallmentPlan?.Allocate(value);
        Status = Outstanding == 0m ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid;
    }

    public void RegisterRefund(decimal amount)
    {
        var value = Money.EnsurePositive(amount, "Сумма возврата");
        if (value > PaidAmount)
        {
            throw new DomainException($"Вернуть {value} нельзя: по начислению {PeriodCode} оплачено только {PaidAmount}.");
        }

        PaidAmount -= value;
        RefundedAmount += value;
        InstallmentPlan?.Deallocate(value);
        Status = PaidAmount == 0m ? InvoiceStatus.Issued : InvoiceStatus.PartiallyPaid;
    }

    public void Cancel()
    {
        if (PaidAmount > 0m)
        {
            throw new DomainException("По начислению есть оплата — сначала оформите возврат.");
        }

        Status = InvoiceStatus.Canceled;
    }
}
