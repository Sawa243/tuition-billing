using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TuitionBilling.Application.Contracts;
using TuitionBilling.Application.Services;
using TuitionBilling.Infrastructure.Identity;

namespace TuitionBilling.Api.Controllers;

[Authorize(Roles = Roles.Accountant)]
public sealed class RefundsController : ApiControllerBase
{
    private readonly RefundService _refunds;

    public RefundsController(RefundService refunds)
    {
        _refunds = refunds;
    }

    [HttpGet]
    public async Task<IReadOnlyList<RefundDto>> List([FromQuery] Guid? paymentId, CancellationToken cancellationToken) =>
        await _refunds.ListAsync(paymentId, cancellationToken);

    /// <summary>Вернуть деньги по платежу: отчисление, перевод, ошибочная оплата.</summary>
    [HttpPost]
    public async Task<RefundDto> Create([FromBody] CreateRefundRequest request, CancellationToken cancellationToken)
    {
        await ValidateAsync(request, cancellationToken);
        return await _refunds.CreateAsync(request, cancellationToken);
    }
}

[Authorize(Roles = Roles.Accountant)]
public sealed class LedgerController : ApiControllerBase
{
    private readonly LedgerService _ledger;

    public LedgerController(LedgerService ledger)
    {
        _ledger = ledger;
    }

    /// <summary>Оборотная ведомость: сальдо по каждому счёту плана счетов.</summary>
    [HttpGet("accounts")]
    public async Task<IReadOnlyList<LedgerAccountBalanceDto>> Accounts(CancellationToken cancellationToken) =>
        await _ledger.GetBalancesAsync(cancellationToken);

    /// <summary>Журнал операций: каждая со своими проводками.</summary>
    [HttpGet("transactions")]
    public async Task<IReadOnlyList<LedgerTransactionDto>> Transactions([FromQuery] int take, CancellationToken cancellationToken) =>
        await _ledger.GetTransactionsAsync(take is > 0 and <= 200 ? take : 50, cancellationToken);
}

[Authorize(Roles = Roles.Accountant)]
public sealed class ReconciliationController : ApiControllerBase
{
    private readonly ReconciliationService _reconciliation;

    public ReconciliationController(ReconciliationService reconciliation)
    {
        _reconciliation = reconciliation;
    }

    [HttpGet]
    public async Task<IReadOnlyList<ReconciliationReportDto>> List([FromQuery] int take, CancellationToken cancellationToken) =>
        await _reconciliation.ListAsync(take is > 0 and <= 60 ? take : 14, cancellationToken);

    /// <summary>
    /// Прогнать сверку за дату вручную. Обычно это делает фоновая служба ночью,
    /// но бухгалтеру нужна возможность переспросить прямо сейчас.
    /// </summary>
    [HttpPost("run")]
    public async Task<ReconciliationReportDto> Run([FromQuery] DateOnly? date, CancellationToken cancellationToken) =>
        await _reconciliation.RunAsync(date ?? DateOnly.FromDateTime(DateTime.UtcNow), cancellationToken);
}
