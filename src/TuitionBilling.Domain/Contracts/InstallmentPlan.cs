using TuitionBilling.Domain.Common;

namespace TuitionBilling.Domain.Contracts;

/// <summary>
/// Рассрочка: начисление разбивается на равные части с собственными сроками.
/// Остаток от деления копеек уходит в последнюю часть, чтобы сумма частей
/// в точности совпадала с суммой начисления.
/// </summary>
public sealed class InstallmentPlan : Entity
{
    private readonly List<InstallmentItem> _items = new();

    private InstallmentPlan()
    {
    }

    internal InstallmentPlan(Guid invoiceId, decimal totalAmount, int partCount, DateOnly firstDueOn, int intervalDays)
    {
        if (partCount < 2 || partCount > 12)
        {
            throw new DomainException("Рассрочка оформляется от 2 до 12 частей.");
        }

        if (intervalDays < 1)
        {
            throw new DomainException("Интервал между платежами должен быть хотя бы день.");
        }

        InvoiceId = invoiceId;
        CreatedAt = DateTimeOffset.UtcNow;

        var part = Math.Floor(totalAmount / partCount * 100m) / 100m;
        var distributed = 0m;
        for (var number = 1; number < partCount; number++)
        {
            _items.Add(new InstallmentItem(Id, number, part, firstDueOn.AddDays(intervalDays * (number - 1))));
            distributed += part;
        }

        _items.Add(new InstallmentItem(Id, partCount, totalAmount - distributed, firstDueOn.AddDays(intervalDays * (partCount - 1))));
    }

    public Guid InvoiceId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    // Возвращается сам список, а не его копия: иначе EF Core не увидит здесь
    // навигационное свойство и не сохранит части рассрочки.
    public IReadOnlyList<InstallmentItem> Items => _items;

    internal void Allocate(decimal amount)
    {
        var rest = amount;
        foreach (var item in _items.OrderBy(i => i.Number))
        {
            if (rest <= 0m)
            {
                break;
            }

            rest -= item.Apply(rest);
        }
    }

    internal void Deallocate(decimal amount)
    {
        var rest = amount;
        foreach (var item in _items.OrderByDescending(i => i.Number))
        {
            if (rest <= 0m)
            {
                break;
            }

            rest -= item.Withdraw(rest);
        }
    }
}
