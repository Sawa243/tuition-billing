using TuitionBilling.Domain.Common;

namespace TuitionBilling.Domain.Ledger;

/// <summary>
/// Операция двойной записи: набор проводок, у которого дебет сходится с кредитом.
/// Собирается черновиком, затем проводится — после этого ничего добавить нельзя.
/// </summary>
public sealed class LedgerTransaction : Entity
{
    private readonly List<LedgerEntry> _entries = new();

    private LedgerTransaction()
    {
        Reference = string.Empty;
        Description = string.Empty;
    }

    private LedgerTransaction(LedgerTransactionKind kind, string reference, DateTimeOffset occurredAt, string description)
    {
        Kind = kind;
        Reference = reference;
        OccurredAt = occurredAt;
        Description = description;
    }

    public LedgerTransactionKind Kind { get; private set; }

    /// <summary>Идентификатор того, из-за чего появилась операция: начисления, платежа, возврата.</summary>
    public string Reference { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public string Description { get; private set; }

    public bool IsPosted { get; private set; }

    public IReadOnlyList<LedgerEntry> Entries => _entries;

    public decimal DebitTotal => _entries.Where(e => e.Side == EntrySide.Debit).Sum(e => e.Amount);

    public decimal CreditTotal => _entries.Where(e => e.Side == EntrySide.Credit).Sum(e => e.Amount);

    public static LedgerTransaction Draft(LedgerTransactionKind kind, string reference, DateTimeOffset occurredAt, string description) =>
        new(kind, reference, occurredAt, description);

    public LedgerTransaction Debit(Guid accountId, decimal amount) => Add(accountId, EntrySide.Debit, amount);

    public LedgerTransaction Credit(Guid accountId, decimal amount) => Add(accountId, EntrySide.Credit, amount);

    public LedgerTransaction Post()
    {
        if (_entries.Count < 2)
        {
            throw new DomainException("В операции должно быть минимум две проводки.");
        }

        if (DebitTotal != CreditTotal)
        {
            throw new DomainException($"Операция не сходится: дебет {DebitTotal}, кредит {CreditTotal}.");
        }

        IsPosted = true;
        return this;
    }

    /// <summary>
    /// Сторно: ошибочная операция не правится и не удаляется, вместо неё
    /// проводится зеркальная. История остаётся полной.
    /// </summary>
    public LedgerTransaction Reverse(DateTimeOffset occurredAt, string reason)
    {
        if (!IsPosted)
        {
            throw new DomainException("Сторнировать можно только проведённую операцию.");
        }

        var reversal = new LedgerTransaction(LedgerTransactionKind.Reversal, Id.ToString(), occurredAt, reason);
        foreach (var entry in _entries)
        {
            var side = entry.Side == EntrySide.Debit ? EntrySide.Credit : EntrySide.Debit;
            reversal.Add(entry.AccountId, side, entry.Amount);
        }

        return reversal.Post();
    }

    private LedgerTransaction Add(Guid accountId, EntrySide side, decimal amount)
    {
        if (IsPosted)
        {
            throw new DomainException("Операция уже проведена, проводки не добавляются.");
        }

        _entries.Add(new LedgerEntry(Id, accountId, side, amount));
        return this;
    }
}
