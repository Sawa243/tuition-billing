using System.Threading.Channels;
using Microsoft.Extensions.Options;
using PaymentGateway.Emulator.Contracts;

namespace PaymentGateway.Emulator.Infrastructure;

public sealed class NotificationQueue
{
    private readonly Channel<NotificationDto> _channel = Channel.CreateUnbounded<NotificationDto>();

    public void Enqueue(NotificationDto notification) => _channel.Writer.TryWrite(notification);

    public IAsyncEnumerable<NotificationDto> ReadAllAsync(CancellationToken token) => _channel.Reader.ReadAllAsync(token);
}

/// <summary>
/// Рассылка уведомлений. Подписи у них намеренно нет — ровно как у ЮKassa:
/// получатель обязан перезапросить статус платежа, а не верить телу запроса.
/// Если магазин ответил не 2xx, повторяем несколько раз.
/// </summary>
public sealed class NotificationSender : BackgroundService
{
    private readonly NotificationQueue _queue;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly ILogger<NotificationSender> _logger;

    public NotificationSender(
        NotificationQueue queue,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<GatewayOptions> options,
        ILogger<NotificationSender> logger)
    {
        _queue = queue;
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var notification in _queue.ReadAllAsync(stoppingToken))
        {
            await DeliverAsync(notification, stoppingToken);
        }
    }

    private async Task DeliverAsync(NotificationDto notification, CancellationToken token)
    {
        var options = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(options.NotificationUrl))
        {
            _logger.LogWarning("Адрес уведомлений не настроен, событие {Event} отброшено", notification.Event);
            return;
        }

        var client = _httpClientFactory.CreateClient("notifications");
        for (var attempt = 1; attempt <= options.NotificationRetries; attempt++)
        {
            try
            {
                var response = await client.PostAsJsonAsync(options.NotificationUrl, notification, token);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Уведомление {Event} по {PaymentId} доставлено с попытки {Attempt}",
                        notification.Event, notification.Object.Id, attempt);
                    return;
                }

                _logger.LogWarning("Магазин ответил {Status} на уведомление {Event}", (int)response.StatusCode, notification.Event);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Не удалось доставить уведомление {Event}, попытка {Attempt}", notification.Event, attempt);
            }

            await Task.Delay(options.NotificationRetryDelayMs * attempt, token);
        }

        _logger.LogError("Уведомление {Event} по {PaymentId} не доставлено", notification.Event, notification.Object.Id);
    }
}
