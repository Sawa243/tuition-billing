using TuitionBilling.Domain.Common;

namespace TuitionBilling.Domain.Contracts;

/// <summary>
/// Договор об оказании платных образовательных услуг. Корень агрегата:
/// начисления заводятся только через него, чтобы нельзя было выставить счёт
/// по расторгнутому договору.
/// </summary>
public sealed class Contract : Entity
{
    private readonly List<Invoice> _invoices = new();

    private Contract()
    {
        Number = string.Empty;
        ProgramName = string.Empty;
    }

    public Contract(string number, Guid studentId, string programName, int admissionYear, decimal totalAmount, DateOnly signedOn)
    {
        if (string.IsNullOrWhiteSpace(number))
        {
            throw new DomainException("Номер договора обязателен.");
        }

        if (string.IsNullOrWhiteSpace(programName))
        {
            throw new DomainException("Направление подготовки обязательно.");
        }

        Number = number.Trim();
        StudentId = studentId;
        ProgramName = programName.Trim();
        AdmissionYear = admissionYear;
        TotalAmount = Money.EnsurePositive(totalAmount, "Сумма договора");
        SignedOn = signedOn;
        Status = ContractStatus.Active;
    }

    public string Number { get; private set; }

    public Guid StudentId { get; private set; }

    public string ProgramName { get; private set; }

    public int AdmissionYear { get; private set; }

    /// <summary>Полная стоимость обучения по договору.</summary>
    public decimal TotalAmount { get; private set; }

    public DateOnly SignedOn { get; private set; }

    public ContractStatus Status { get; private set; }

    public IReadOnlyList<Invoice> Invoices => _invoices;

    public decimal IssuedAmount => _invoices.Where(i => i.Status != InvoiceStatus.Canceled).Sum(i => i.Amount);

    public Invoice IssueInvoice(string periodCode, decimal amount, DateOnly issuedOn, DateOnly dueOn)
    {
        if (Status != ContractStatus.Active)
        {
            throw new DomainException($"Договор {Number} в статусе {Status}, выставить начисление нельзя.");
        }

        if (_invoices.Any(i => i.PeriodCode == periodCode && i.Status != InvoiceStatus.Canceled))
        {
            throw new DomainException($"По договору {Number} за период {periodCode} начисление уже есть.");
        }

        var value = Money.EnsurePositive(amount, "Сумма начисления");
        if (IssuedAmount + value > TotalAmount)
        {
            throw new DomainException(
                $"Начисления по договору {Number} превысят его сумму: уже начислено {IssuedAmount}, добавляем {value}, всего по договору {TotalAmount}.");
        }

        var invoice = new Invoice(Id, periodCode, value, issuedOn, dueOn);
        _invoices.Add(invoice);
        return invoice;
    }

    public void Suspend()
    {
        if (Status == ContractStatus.Terminated)
        {
            throw new DomainException("Расторгнутый договор приостановить нельзя.");
        }

        Status = ContractStatus.Suspended;
    }

    public void Restore()
    {
        if (Status == ContractStatus.Terminated)
        {
            throw new DomainException("Расторгнутый договор восстановить нельзя.");
        }

        Status = ContractStatus.Active;
    }

    public void Terminate() => Status = ContractStatus.Terminated;
}
