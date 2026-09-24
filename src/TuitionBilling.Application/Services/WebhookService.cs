using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TuitionBilling.Application.Abstractions;
using TuitionBilling.Application.Persistence;
using TuitionBilling.Domain.Payments;

namespace TuitionBilling.Application.Services;

public sealed record GatewayNotification(string Event, string GatewayPaymentId, string ReportedStatus);

public enum WebhookOutcome
{
    Processed,
    Duplicate,
    UnknownPayment,
    NothingChanged
}

/// <summary>
/// Приём уведомлений платёжного шлюза.
///
/// Главное правило одно: телу уведомления не верим. У провайдеров вроде ЮKassa
/// подписи у вебхука нет вообще, так что прислать его может кто угодно, кто знает
/// адрес. Уведомление — лишь повод сходить к шлюзу и спросить настоящий статус;
/// деньги зачисляются только по ответу на этот запрос.
///
/// От повторной доставки защищает сам конечный автомат платежа, а не таблица
/// обработанных событий. Так вышло не сразу: в первой версии стоял уникальный
/// индекс по ключу «событие + операция + статус», и тест на подделку показал
/// дыру — поддельное уведомление занимало ключ, после чего настоящее отбрасывалось
/// как дубль, и оплата не зачислялась. Теперь таблица событий — чистый журнал
/// для аудита, а решение принимает переход состояния.
/// </summary>
public sealed class WebhookService
{
    private readonly IBillingDbContext _db;
    private readonly PaymentSynchronizer _synchronizer;
    private readonly ILogger<WebhookService> _logger;

    public WebhookService(IBillingDbContext db, PaymentSynchronizer synchronizer, ILogger<WebhookService> logger)
    {
        _db = db;
        _synchronizer = synchronizer;
        _logger = logger;
    }

    public async Task<WebhookOutcome> HandleAsync(
        GatewayNotification notification,
        string rawBody,
        string? sourceIp,
        CancellationToken cancellationToken)
    {
        var record = new WebhookEvent
        {
            DeduplicationKey = $"{notification.Event}:{notification.GatewayPaymentId}:{notification.ReportedStatus}",
            Event = notification.Event,
            GatewayPaymentId = notification.GatewayPaymentId,
            ReportedStatus = notification.ReportedStatus,
            RawBody = rawBody.Length > 4000 ? rawBody[..4000] : rawBody,
            SourceIp = sourceIp
        };

        _db.WebhookEvents.Add(record);

        var payment = await _db.Payments
            .FirstOrDefaultAsync(p => p.GatewayPaymentId == notification.GatewayPaymentId, cancellationToken);

        if (payment is null)
        {
            _logger.LogWarning("Уведомление по неизвестной операции {GatewayPaymentId} с адреса {Ip}",
                notification.GatewayPaymentId, sourceIp);
            record.Outcome = nameof(WebhookOutcome.UnknownPayment);
            await _db.SaveChangesAsync(cancellationToken);
            return WebhookOutcome.UnknownPayment;
        }

        // Конечный статус уже не изменится ничем, поэтому дёргать шлюз незачем.
        // Это и есть дешёвая защита от повторной доставки: провайдер шлёт
        // уведомление, пока не получит 200, и такие повторы — обычное дело.
        if (payment.Status.IsTerminal() && payment.Status.ToString().Equals(Normalize(notification.ReportedStatus), StringComparison.OrdinalIgnoreCase))
        {
            record.VerifiedStatus = payment.Status.ToString();
            record.Outcome = nameof(WebhookOutcome.Duplicate);
            await _db.SaveChangesAsync(cancellationToken);
            return WebhookOutcome.Duplicate;
        }

        var (outcome, status) = await _synchronizer.SyncAsync(payment, cancellationToken);

        record.VerifiedStatus = status.ToString();
        record.Outcome = outcome.ToString();
        await _db.SaveChangesAsync(cancellationToken);

        if (outcome == PaymentTransitionOutcome.Applied)
        {
            _logger.LogInformation("Платёж {PaymentId} переведён в {Status} по уведомлению", payment.Id, status);
            return WebhookOutcome.Processed;
        }

        // Сюда попадает и поддельное уведомление: шлюз на обратный запрос
        // ответил, что платёж всё ещё не оплачен, и статус не изменился.
        _logger.LogInformation("Уведомление {Event} по платежу {PaymentId} ничего не изменило: {Outcome}",
            notification.Event, payment.Id, outcome);
        return WebhookOutcome.NothingChanged;
    }

    private static string Normalize(string gatewayStatus) => gatewayStatus switch
    {
        "waiting_for_capture" => nameof(PaymentStatus.WaitingForCapture),
        _ => gatewayStatus
    };
}
