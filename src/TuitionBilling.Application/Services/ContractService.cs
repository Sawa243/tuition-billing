using Microsoft.EntityFrameworkCore;
using TuitionBilling.Application.Abstractions;
using TuitionBilling.Application.Common;
using TuitionBilling.Application.Contracts;
using TuitionBilling.Domain.Contracts;
using TuitionBilling.Domain.Ledger;

namespace TuitionBilling.Application.Services;

/// <summary>
/// Сценарии бухгалтера: договоры, семестровые начисления и рассрочка.
/// </summary>
public sealed class ContractService
{
    private readonly IBillingDbContext _db;
    private readonly LedgerService _ledger;

    public ContractService(IBillingDbContext db, LedgerService ledger)
    {
        _db = db;
        _ledger = ledger;
    }

    public async Task<ContractDto> CreateAsync(CreateContractRequest request, CancellationToken cancellationToken)
    {
        var student = await _db.Students.FirstOrDefaultAsync(s => s.Id == request.StudentId, cancellationToken)
                      ?? throw new NotFoundException("Обучающийся", request.StudentId);

        if (await _db.Contracts.AnyAsync(c => c.Number == request.Number, cancellationToken))
        {
            throw new DomainConflictException($"Договор с номером {request.Number} уже заведён.");
        }

        var contract = new Contract(request.Number, student.Id, request.ProgramName, request.AdmissionYear,
            request.TotalAmount, request.SignedOn);

        _db.Contracts.Add(contract);
        await _db.SaveChangesAsync(cancellationToken);

        return await GetAsync(contract.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<ContractDto>> ListAsync(Guid? studentId, CancellationToken cancellationToken)
    {
        var query = _db.Contracts.AsNoTracking();
        if (studentId is { } id)
        {
            query = query.Where(c => c.StudentId == id);
        }

        var contracts = await query.OrderBy(c => c.Number).ToListAsync(cancellationToken);
        var result = new List<ContractDto>(contracts.Count);
        foreach (var contract in contracts)
        {
            result.Add(await BuildAsync(contract, cancellationToken));
        }

        return result;
    }

    public async Task<ContractDto> GetAsync(Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await _db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contractId, cancellationToken)
                       ?? throw new NotFoundException("Договор", contractId);

        return await BuildAsync(contract, cancellationToken);
    }

    public async Task<InvoiceDto> IssueInvoiceAsync(Guid contractId, IssueInvoiceRequest request, CancellationToken cancellationToken)
    {
        var contract = await _db.Contracts
                           .Include(c => c.Invoices)
                           .FirstOrDefaultAsync(c => c.Id == contractId, cancellationToken)
                       ?? throw new NotFoundException("Договор", contractId);

        var invoice = contract.IssueInvoice(request.PeriodCode, request.Amount, request.IssuedOn, request.DueOn);

        // Add обязателен. Идентификатор сущность выдаёт себе сама в конструкторе,
        // а EF Core решает «новое или существующее» по тому, пуст ли ключ. Без
        // явного Add начисление, добавленное в коллекцию уже отслеживаемого
        // договора, уедет в базу как UPDATE несуществующей строки.
        _db.Invoices.Add(invoice);

        // Начисление и проводка по нему уходят в базу одной транзакцией.
        await _ledger.PostInvoiceIssuedAsync(invoice, contract, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        return await BuildInvoiceAsync(invoice.Id, cancellationToken);
    }

    public async Task<InvoiceDto> SplitIntoInstallmentsAsync(
        Guid invoiceId,
        CreateInstallmentPlanRequest request,
        CancellationToken cancellationToken)
    {
        var invoice = await _db.Invoices
                          .Include(i => i.InstallmentPlan)
                          .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken)
                      ?? throw new NotFoundException("Начисление", invoiceId);

        if (invoice.InstallmentPlan is not null)
        {
            throw new DomainConflictException("По этому начислению рассрочка уже оформлена.");
        }

        var plan = invoice.SplitIntoInstallments(request.PartCount, request.FirstDueOn, request.IntervalDays);
        _db.InstallmentPlans.Add(plan);
        await _db.SaveChangesAsync(cancellationToken);

        return await BuildInvoiceAsync(invoice.Id, cancellationToken);
    }

    /// <summary>
    /// Отмена ошибочного начисления. Проводка не удаляется и не правится —
    /// ставится сторно, иначе журнал разойдётся с витриной на ближайшей же сверке.
    /// </summary>
    public async Task<InvoiceDto> CancelInvoiceAsync(Guid invoiceId, string reason, CancellationToken cancellationToken)
    {
        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken)
                      ?? throw new NotFoundException("Начисление", invoiceId);

        invoice.Cancel();

        var reference = invoice.Id.ToString();
        var issued = await _db.LedgerTransactions
            .Include(t => t.Entries)
            .FirstOrDefaultAsync(t => t.Kind == LedgerTransactionKind.InvoiceIssued && t.Reference == reference, cancellationToken);

        if (issued is not null)
        {
            _db.LedgerTransactions.Add(issued.Reverse(DateTimeOffset.UtcNow,
                $"Сторно начисления {invoice.PeriodCode}: {reason}"));
        }

        await _db.SaveChangesAsync(cancellationToken);
        return await BuildInvoiceAsync(invoice.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<InvoiceDto>> ListInvoicesAsync(Guid? studentId, bool onlyUnpaid, CancellationToken cancellationToken)
    {
        var query = from invoice in _db.Invoices.AsNoTracking()
                    join contract in _db.Contracts.AsNoTracking() on invoice.ContractId equals contract.Id
                    where studentId == null || contract.StudentId == studentId
                    orderby invoice.DueOn
                    select new { invoice, contract.Number };

        var rows = await query.ToListAsync(cancellationToken);
        if (onlyUnpaid)
        {
            rows = rows.Where(r => r.invoice.Outstanding > 0m && r.invoice.Status != InvoiceStatus.Canceled).ToList();
        }

        var plans = await LoadPlansAsync(rows.Select(r => r.invoice.Id).ToList(), cancellationToken);
        return rows.Select(r => ToDto(r.invoice, r.Number, plans)).ToList();
    }

    public async Task<InvoiceDto> BuildInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        var row = await (from invoice in _db.Invoices.AsNoTracking()
                         join contract in _db.Contracts.AsNoTracking() on invoice.ContractId equals contract.Id
                         where invoice.Id == invoiceId
                         select new { invoice, contract.Number }).FirstOrDefaultAsync(cancellationToken)
                  ?? throw new NotFoundException("Начисление", invoiceId);

        var plans = await LoadPlansAsync(new List<Guid> { invoiceId }, cancellationToken);
        return ToDto(row.invoice, row.Number, plans);
    }

    private async Task<ContractDto> BuildAsync(Contract contract, CancellationToken cancellationToken)
    {
        var studentName = await _db.Students.AsNoTracking()
            .Where(s => s.Id == contract.StudentId)
            .Select(s => s.FullName)
            .FirstOrDefaultAsync(cancellationToken) ?? "—";

        var invoices = await _db.Invoices.AsNoTracking()
            .Where(i => i.ContractId == contract.Id)
            .OrderBy(i => i.PeriodCode)
            .ToListAsync(cancellationToken);

        var plans = await LoadPlansAsync(invoices.Select(i => i.Id).ToList(), cancellationToken);
        var dtos = invoices.Select(i => ToDto(i, contract.Number, plans)).ToList();

        return new ContractDto(
            contract.Id,
            contract.Number,
            contract.StudentId,
            studentName,
            contract.ProgramName,
            contract.AdmissionYear,
            contract.TotalAmount,
            dtos.Where(i => i.Status != InvoiceStatus.Canceled).Sum(i => i.Amount),
            dtos.Where(i => i.Status != InvoiceStatus.Canceled).Sum(i => i.Outstanding),
            contract.Status,
            contract.SignedOn,
            dtos);
    }

    private async Task<Dictionary<Guid, List<InstallmentItemDto>>> LoadPlansAsync(
        List<Guid> invoiceIds,
        CancellationToken cancellationToken)
    {
        if (invoiceIds.Count == 0)
        {
            return new Dictionary<Guid, List<InstallmentItemDto>>();
        }

        var plans = await _db.InstallmentPlans.AsNoTracking()
            .Where(p => invoiceIds.Contains(p.InvoiceId))
            .Select(p => new { p.Id, p.InvoiceId })
            .ToListAsync(cancellationToken);

        var planIds = plans.Select(p => p.Id).ToList();
        var items = await _db.InstallmentItems.AsNoTracking()
            .Where(i => planIds.Contains(i.PlanId))
            .OrderBy(i => i.Number)
            .ToListAsync(cancellationToken);

        return plans.ToDictionary(
            p => p.InvoiceId,
            p => items.Where(i => i.PlanId == p.Id)
                .Select(i => new InstallmentItemDto(i.Number, i.Amount, i.PaidAmount, i.DueOn, i.IsPaid))
                .ToList());
    }

    private static InvoiceDto ToDto(Invoice invoice, string contractNumber, Dictionary<Guid, List<InstallmentItemDto>> plans) =>
        new(invoice.Id,
            invoice.ContractId,
            contractNumber,
            invoice.PeriodCode,
            invoice.Amount,
            invoice.PaidAmount,
            invoice.RefundedAmount,
            invoice.Outstanding,
            invoice.IssuedOn,
            invoice.DueOn,
            invoice.Status,
            invoice.IsOverdue(DateOnly.FromDateTime(DateTime.UtcNow)),
            plans.TryGetValue(invoice.Id, out var items) ? items : Array.Empty<InstallmentItemDto>());
}
