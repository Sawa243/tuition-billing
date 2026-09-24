using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TuitionBilling.Application.Abstractions;
using TuitionBilling.Application.Persistence;
using TuitionBilling.Domain.Contracts;
using TuitionBilling.Domain.Payments;

namespace TuitionBilling.Application.Services;

public sealed record ReceiptOutboxPayload(Guid PaymentId);

/// <summary>
/// Единственное место, где платёж меняет статус. И уведомление от шлюза,
/// и возврат плательщика на сайт, и суточная сверка приходят сюда, поэтому
/// правило «верить только обратному запросу статуса» соблюдается везде сразу.
/// </summary>
public sealed class PaymentSynchronizer
{
    private readonly IBillingDbContext _db;
    private readonly IPaymentGatewayClient _gateway;
    private readonly LedgerService _ledger;
    private readonly ILogger<PaymentSynchronizer> _logger;

    public PaymentSynchronizer(
        IBillingDbContext db,
        IPaymentGatewayClient gateway,
        LedgerService ledger,
        ILogger<PaymentSynchronizer> logger)
    {
        _db = db;
        _gateway = gateway;
        _ledger = ledger;
        _logger = logger;
    }

    /// <summary>
    /// Подтягивает настоящий статус платежа у шлюза и проводит последствия.
    /// Возвращает итог перехода, чтобы вызывающий понимал, был ли это дубль.
    /// </summary>
    public async Task<(PaymentTransitionOutcome Outcome, PaymentStatus Status)> SyncAsync(
        Payment payment,
        CancellationToken cancellationToken)
    {
        if (payment.GatewayPaymentId is null)
        {
            return (PaymentTransitionOutcome.Ignored, payment.Status);
        }

        var view = await _gateway.GetPaymentAsync(payment.GatewayPaymentId, cancellationToken);
        if (view is null)
        {
            _logger.LogWarning("Шлюз не знает операции {GatewayPaymentId} — платёж {PaymentId} оставлен как есть",
                payment.GatewayPaymentId, payment.Id);
            return (PaymentTransitionOutcome.Ignored, payment.Status);
        }

        return await ApplyAsync(payment, view.Status, view.CapturedAt ?? DateTimeOffset.UtcNow, view.CancellationReason, cancellationToken);
    }

    public async Task<(PaymentTransitionOutcome Outcome, PaymentStatus Status)> ApplyAsync(
        Payment payment,
        PaymentStatus status,
        DateTimeOffset occurredAt,
        string? cancellationReason,
        CancellationToken cancellationToken)
    {
        var outcome = payment.ApplyStatus(status, occurredAt, cancellationReason);
        if (outcome != PaymentTransitionOutcome.Applied)
        {
            return (outcome, payment.Status);
        }

        if (payment.Status == PaymentStatus.Succeeded)
        {
            await RegisterSuccessAsync(payment, cancellationToken);
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Уведомление и возврат плательщика на сайт часто приходят одновременно.
            // Проиграть эту гонку не страшно: победитель уже сделал ровно то же самое.
            _logger.LogInformation("Платёж {PaymentId} в этот момент обновил кто-то ещё", payment.Id);
            return (PaymentTransitionOutcome.AlreadyInThatState, status);
        }

        return (outcome, payment.Status);
    }

    private async Task RegisterSuccessAsync(Payment payment, CancellationToken cancellationToken)
    {
        // ThenInclude обязателен: без частей рассрочки распределять оплату не по чему,
        // и график молча остался бы неоплаченным при оплаченном начислении.
        var invoice = await _db.Invoices
                          .Include(i => i.InstallmentPlan)
                          .ThenInclude(p => p!.Items)
                          .FirstOrDefaultAsync(i => i.Id == payment.InvoiceId, cancellationToken)
                      ?? throw new InvalidOperationException($"Начисление {payment.InvoiceId} пропало.");

        var contract = await _db.Contracts.FirstOrDefaultAsync(c => c.Id == payment.ContractId, cancellationToken)
                       ?? throw new InvalidOperationException($"Договор {payment.ContractId} пропал.");

        invoice.RegisterPayment(payment.Amount);
        await _ledger.PostPaymentSucceededAsync(payment, contract.Number, cancellationToken);

        // Чек по 54-ФЗ уходит через исходящий ящик: HTTP-запрос к кассе провайдера
        // нельзя выполнить в одной транзакции с записью в базу.
        _db.OutboxMessages.Add(new OutboxMessage
        {
            Kind = OutboxKinds.RegisterReceipt,
            Payload = JsonSerializer.Serialize(new ReceiptOutboxPayload(payment.Id)),
            NextAttemptAt = DateTimeOffset.UtcNow
        });
    }
}

public static class OutboxKinds
{
    public const string RegisterReceipt = "receipt.register";
}
