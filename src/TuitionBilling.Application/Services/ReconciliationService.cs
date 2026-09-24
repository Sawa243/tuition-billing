using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TuitionBilling.Application.Abstractions;
using TuitionBilling.Application.Contracts;
using TuitionBilling.Application.Persistence;
using TuitionBilling.Domain.Contracts;
using TuitionBilling.Domain.Payments;

namespace TuitionBilling.Application.Services;

/// <summary>
/// Суточная сверка с реестром шлюза.
///
/// Сопоставление идёт строго по идентификатору операции провайдера. По паре
/// «сумма и дата» сверять нельзя: время у сторон расходится, а одинаковых сумм
/// в вузе сколько угодно — все первокурсники платят один и тот же семестр.
///
/// Заодно проверяется внутренняя целостность: сальдо дебиторки по журналу
/// проводок должно совпадать с суммой непогашенных начислений. Расхождение
/// означает потерянную проводку и требует разбирательства.
/// </summary>
public sealed class ReconciliationService
{
    private readonly IBillingDbContext _db;
    private readonly IPaymentGatewayClient _gateway;
    private readonly PaymentSynchronizer _synchronizer;
    private readonly LedgerService _ledger;
    private readonly ILogger<ReconciliationService> _logger;

    public ReconciliationService(
        IBillingDbContext db,
        IPaymentGatewayClient gateway,
        PaymentSynchronizer synchronizer,
        LedgerService ledger,
        ILogger<ReconciliationService> logger)
    {
        _db = db;
        _gateway = gateway;
        _synchronizer = synchronizer;
        _ledger = ledger;
        _logger = logger;
    }

    public async Task<ReconciliationReportDto> RunAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var registry = await _gateway.GetRegistryAsync(date, cancellationToken);
        var from = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = from.AddDays(1);

        var local = await _db.Payments
            .Where(p => p.GatewayPaymentId != null && p.CreatedAt >= from && p.CreatedAt < to)
            .ToListAsync(cancellationToken);

        var issues = new List<ReconciliationIssueDto>();
        var matched = 0;
        var repaired = 0;

        foreach (var row in registry)
        {
            var payment = local.FirstOrDefault(p => p.GatewayPaymentId == row.PaymentId);
            if (payment is null)
            {
                issues.Add(new ReconciliationIssueDto("Нет у нас", row.PaymentId,
                    $"Шлюз знает операцию на {row.Amount:N2} ₽ в статусе {row.Status}, а у нас её нет.", false));
                continue;
            }

            if (payment.Amount != row.Amount)
            {
                issues.Add(new ReconciliationIssueDto("Расходится сумма", row.PaymentId,
                    $"У нас {payment.Amount:N2} ₽, у шлюза {row.Amount:N2} ₽.", false));
                continue;
            }

            if (payment.Status != row.Status)
            {
                // Классическая причина — потерянное уведомление. Статус подтягивается
                // обратным запросом, то есть тем же безопасным путём, что и всегда.
                var before = payment.Status;
                var (outcome, status) = await _synchronizer.SyncAsync(payment, cancellationToken);
                var wasRepaired = outcome == PaymentTransitionOutcome.Applied;
                if (wasRepaired)
                {
                    repaired++;
                }

                issues.Add(new ReconciliationIssueDto("Расходится статус", row.PaymentId,
                    $"У нас было {before}, у шлюза {row.Status}; после обратного запроса стало {status}.", wasRepaired));
                continue;
            }

            matched++;
        }

        var registryIds = registry.Select(r => r.PaymentId).ToHashSet();
        foreach (var orphan in local.Where(p => !registryIds.Contains(p.GatewayPaymentId!)))
        {
            issues.Add(new ReconciliationIssueDto("Нет у шлюза", orphan.GatewayPaymentId!,
                $"Платёж {orphan.Id} на {orphan.Amount:N2} ₽ в статусе {orphan.Status} не попал в реестр.", false));
        }

        var drift = await LedgerDriftAsync(cancellationToken);
        if (drift != 0m)
        {
            issues.Add(new ReconciliationIssueDto("Разошёлся журнал", "—",
                $"Сальдо дебиторки по проводкам отличается от суммы непогашенных начислений на {drift:N2} ₽.", false));
        }

        await PostSettlementAsync(date, registry, local, from, to, cancellationToken);

        var report = new ReconciliationReport
        {
            Date = date,
            GatewayOperations = registry.Count,
            LocalPayments = local.Count,
            Matched = matched,
            Discrepancies = issues.Count,
            AutoRepaired = repaired,
            GatewayTotal = registry.Where(r => r.Status == PaymentStatus.Succeeded).Sum(r => r.Amount),
            LocalTotal = local.Where(p => p.Status == PaymentStatus.Succeeded).Sum(p => p.Amount),
            LedgerDrift = drift,
            Details = JsonSerializer.Serialize(issues)
        };

        _db.ReconciliationReports.Add(report);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Сверка за {Date}: сошлось {Matched}, расхождений {Issues}, починено {Repaired}",
            date, matched, issues.Count, repaired);

        return ToDto(report, issues);
    }

    public async Task<IReadOnlyList<ReconciliationReportDto>> ListAsync(int take, CancellationToken cancellationToken)
    {
        var reports = await _db.ReconciliationReports.AsNoTracking()
            .OrderByDescending(r => r.Date)
            .ThenByDescending(r => r.RunAt)
            .Take(take)
            .ToListAsync(cancellationToken);

        return reports
            .Select(r => ToDto(r, JsonSerializer.Deserialize<List<ReconciliationIssueDto>>(r.Details) ?? new List<ReconciliationIssueDto>()))
            .ToList();
    }

    private async Task<decimal> LedgerDriftAsync(CancellationToken cancellationToken)
    {
        var ledgerReceivable = await _ledger.ReceivableBalanceAsync(cancellationToken);
        var invoicesOutstanding = await _db.Invoices.AsNoTracking()
            .Where(i => i.Status != InvoiceStatus.Canceled)
            .SumAsync(i => (decimal?)(i.Amount - i.PaidAmount), cancellationToken) ?? 0m;

        return ledgerReceivable - invoicesOutstanding;
    }

    /// <summary>
    /// Провайдер переводит выручку за день на расчётный счёт за вычетом комиссии.
    /// Проводка ставится один раз на дату — повторный запуск сверки её не задвоит.
    /// </summary>
    private async Task PostSettlementAsync(
        DateOnly date,
        IReadOnlyList<GatewayRegistryRow> registry,
        List<Payment> local,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        if (await _ledger.SettlementExistsAsync(date, cancellationToken))
        {
            return;
        }

        var settled = registry
            .Where(r => r.Status == PaymentStatus.Succeeded)
            .Where(r => local.Any(p => p.GatewayPaymentId == r.PaymentId && p.Status == PaymentStatus.Succeeded))
            .ToList();

        if (settled.Count == 0)
        {
            return;
        }

        var refunded = await _db.Refunds.AsNoTracking()
            .Where(r => r.Status == RefundStatus.Succeeded && r.CompletedAt >= from && r.CompletedAt < to)
            .SumAsync(r => (decimal?)r.Amount, cancellationToken) ?? 0m;

        var gross = settled.Sum(r => r.Amount);
        var fee = settled.Sum(r => r.Fee);

        if (gross - refunded - fee <= 0m)
        {
            _logger.LogWarning("Перечисление за {Date} не проводится: возвраты съели всю выручку дня", date);
            return;
        }

        await _ledger.PostSettlementAsync(date, gross - refunded, fee, cancellationToken);
    }

    private static ReconciliationReportDto ToDto(ReconciliationReport report, IReadOnlyList<ReconciliationIssueDto> issues) =>
        new(report.Id, report.Date, report.RunAt, report.GatewayOperations, report.LocalPayments, report.Matched,
            report.Discrepancies, report.AutoRepaired, report.GatewayTotal, report.LocalTotal, report.LedgerDrift, issues);
}
