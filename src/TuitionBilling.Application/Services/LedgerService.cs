using Microsoft.EntityFrameworkCore;
using TuitionBilling.Application.Abstractions;
using TuitionBilling.Application.Contracts;
using TuitionBilling.Domain.Contracts;
using TuitionBilling.Domain.Ledger;
using TuitionBilling.Domain.Payments;

namespace TuitionBilling.Application.Services;

/// <summary>
/// Журнал проводок. Ни один метод не сохраняет изменения сам: операция должна
/// попасть в базу в той же транзакции, что и вызвавшее её событие, иначе
/// деньги и журнал разъедутся.
/// </summary>
public sealed class LedgerService
{
    private readonly IBillingDbContext _db;

    public LedgerService(IBillingDbContext db)
    {
        _db = db;
    }

    public async Task<LedgerAccount> EnsureAccountAsync(string code, Guid? contractId, CancellationToken cancellationToken)
    {
        var tracked = _db.LedgerAccounts.Local.FirstOrDefault(a => a.Code == code && a.ContractId == contractId);
        if (tracked is not null)
        {
            return tracked;
        }

        var existing = await _db.LedgerAccounts
            .FirstOrDefaultAsync(a => a.Code == code && a.ContractId == contractId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var account = new LedgerAccount(code, contractId);
        _db.LedgerAccounts.Add(account);
        return account;
    }

    public async Task<LedgerTransaction> PostInvoiceIssuedAsync(Invoice invoice, Contract contract, CancellationToken cancellationToken)
    {
        var receivable = await EnsureAccountAsync(ChartOfAccounts.StudentReceivable, contract.Id, cancellationToken);
        var revenue = await EnsureAccountAsync(ChartOfAccounts.TuitionRevenue, null, cancellationToken);

        return Post(LedgerTransaction
            .Draft(LedgerTransactionKind.InvoiceIssued, invoice.Id.ToString(), DateTimeOffset.UtcNow,
                $"Начислена оплата обучения по договору {contract.Number} за период {invoice.PeriodCode}")
            .Debit(receivable.Id, invoice.Amount)
            .Credit(revenue.Id, invoice.Amount));
    }

    public async Task<LedgerTransaction> PostPaymentSucceededAsync(Payment payment, string contractNumber, CancellationToken cancellationToken)
    {
        var clearing = await EnsureAccountAsync(ChartOfAccounts.GatewayClearing, null, cancellationToken);
        var receivable = await EnsureAccountAsync(ChartOfAccounts.StudentReceivable, payment.ContractId, cancellationToken);

        return Post(LedgerTransaction
            .Draft(LedgerTransactionKind.PaymentSucceeded, payment.Id.ToString(), payment.CapturedAt ?? DateTimeOffset.UtcNow,
                $"Оплата по договору {contractNumber}, операция шлюза {payment.GatewayPaymentId}")
            .Debit(clearing.Id, payment.Amount)
            .Credit(receivable.Id, payment.Amount));
    }

    public async Task<LedgerTransaction> PostRefundAsync(Refund refund, Payment payment, string contractNumber, CancellationToken cancellationToken)
    {
        var clearing = await EnsureAccountAsync(ChartOfAccounts.GatewayClearing, null, cancellationToken);
        var receivable = await EnsureAccountAsync(ChartOfAccounts.StudentReceivable, payment.ContractId, cancellationToken);

        return Post(LedgerTransaction
            .Draft(LedgerTransactionKind.RefundSucceeded, refund.Id.ToString(), refund.CompletedAt ?? DateTimeOffset.UtcNow,
                $"Возврат по договору {contractNumber}: {refund.Reason}")
            .Debit(receivable.Id, refund.Amount)
            .Credit(clearing.Id, refund.Amount));
    }

    /// <summary>
    /// Провайдер перечислил выручку за день на расчётный счёт, удержав комиссию.
    /// Здесь три проводки в одной операции — как раз тот случай, ради которого
    /// двойная запись не сводится к паре «дебет-кредит».
    /// </summary>
    public async Task<LedgerTransaction> PostSettlementAsync(DateOnly date, decimal gross, decimal fee, CancellationToken cancellationToken)
    {
        var clearing = await EnsureAccountAsync(ChartOfAccounts.GatewayClearing, null, cancellationToken);
        var bank = await EnsureAccountAsync(ChartOfAccounts.BankSettlement, null, cancellationToken);
        var feeAccount = await EnsureAccountAsync(ChartOfAccounts.GatewayFee, null, cancellationToken);

        return Post(LedgerTransaction
            .Draft(LedgerTransactionKind.GatewaySettlement, $"settlement:{date:yyyy-MM-dd}", DateTimeOffset.UtcNow,
                $"Перечисление выручки за {date:dd.MM.yyyy} за вычетом комиссии")
            .Debit(bank.Id, gross - fee)
            .Debit(feeAccount.Id, fee)
            .Credit(clearing.Id, gross));
    }

    public Task<bool> SettlementExistsAsync(DateOnly date, CancellationToken cancellationToken) =>
        _db.LedgerTransactions.AnyAsync(
            t => t.Kind == LedgerTransactionKind.GatewaySettlement && t.Reference == $"settlement:{date:yyyy-MM-dd}",
            cancellationToken);

    public async Task<IReadOnlyList<LedgerAccountBalanceDto>> GetBalancesAsync(CancellationToken cancellationToken)
    {
        var accounts = await _db.LedgerAccounts.AsNoTracking().ToListAsync(cancellationToken);
        var contracts = await _db.Contracts.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Number, cancellationToken);
        var totals = await _db.LedgerEntries.AsNoTracking()
            .GroupBy(e => e.AccountId)
            .Select(g => new
            {
                AccountId = g.Key,
                Debit = g.Where(e => e.Side == EntrySide.Debit).Sum(e => (decimal?)e.Amount) ?? 0m,
                Credit = g.Where(e => e.Side == EntrySide.Credit).Sum(e => (decimal?)e.Amount) ?? 0m
            })
            .ToListAsync(cancellationToken);

        return accounts
            .Select(account =>
            {
                var total = totals.FirstOrDefault(t => t.AccountId == account.Id);
                var debit = total?.Debit ?? 0m;
                var credit = total?.Credit ?? 0m;
                var contractNumber = account.ContractId is { } id && contracts.TryGetValue(id, out var number) ? number : null;
                return new LedgerAccountBalanceDto(account.Id, account.Code, account.Name, account.Kind, contractNumber,
                    debit, credit, account.BalanceOf(debit, credit));
            })
            .OrderBy(a => a.Code)
            .ThenBy(a => a.ContractNumber)
            .ToList();
    }

    public async Task<IReadOnlyList<LedgerTransactionDto>> GetTransactionsAsync(int take, CancellationToken cancellationToken)
    {
        var transactions = await _db.LedgerTransactions.AsNoTracking()
            .OrderByDescending(t => t.OccurredAt)
            .Take(take)
            .ToListAsync(cancellationToken);

        var ids = transactions.Select(t => t.Id).ToList();
        var entries = await _db.LedgerEntries.AsNoTracking()
            .Where(e => ids.Contains(e.TransactionId))
            .ToListAsync(cancellationToken);
        var accounts = await _db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, cancellationToken);

        return transactions
            .Select(transaction =>
            {
                // Дебет вперёд: так операцию читают в бухгалтерии.
                var own = entries
                    .Where(e => e.TransactionId == transaction.Id)
                    .OrderBy(e => e.Side)
                    .ToList();
                return new LedgerTransactionDto(
                    transaction.Id,
                    transaction.Kind,
                    transaction.Reference,
                    transaction.Description,
                    transaction.OccurredAt,
                    own.Where(e => e.Side == EntrySide.Debit).Sum(e => e.Amount),
                    own.Select(e => new LedgerEntryDto(
                            accounts[e.AccountId].Code,
                            accounts[e.AccountId].Name,
                            e.Side,
                            e.Amount))
                        .ToList());
            })
            .ToList();
    }

    /// <summary>
    /// Сальдо всей дебиторки по журналу. Суточная сверка сравнивает его с суммой
    /// непогашенных начислений: если числа разошлись, где-то потеряна проводка.
    /// </summary>
    public async Task<decimal> ReceivableBalanceAsync(CancellationToken cancellationToken)
    {
        var accountIds = await _db.LedgerAccounts.AsNoTracking()
            .Where(a => a.Code == ChartOfAccounts.StudentReceivable)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

        if (accountIds.Count == 0)
        {
            return 0m;
        }

        var entries = await _db.LedgerEntries.AsNoTracking()
            .Where(e => accountIds.Contains(e.AccountId))
            .Select(e => new { e.Side, e.Amount })
            .ToListAsync(cancellationToken);

        return entries.Where(e => e.Side == EntrySide.Debit).Sum(e => e.Amount)
               - entries.Where(e => e.Side == EntrySide.Credit).Sum(e => e.Amount);
    }

    private LedgerTransaction Post(LedgerTransaction draft)
    {
        var transaction = draft.Post();
        _db.LedgerTransactions.Add(transaction);
        return transaction;
    }
}
