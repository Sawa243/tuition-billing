using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TuitionBilling.Application.Common;
using TuitionBilling.Api.Middleware;
using TuitionBilling.Application.Services;

namespace TuitionBilling.Api.Controllers;

[ApiController]
[Route("api/webhooks")]
[AllowAnonymous]
public sealed class WebhooksController : ControllerBase
{
    private readonly WebhookService _webhooks;
    private readonly BillingOptions _options;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(WebhookService webhooks, IOptions<BillingOptions> options, ILogger<WebhooksController> logger)
    {
        _webhooks = webhooks;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Приём уведомлений платёжного шлюза.
    ///
    /// Подписи у уведомления нет — так устроены и настоящие провайдеры, например
    /// ЮKassa. Поэтому защита стоит в два слоя: список разрешённых адресов
    /// отправителя и обязательный обратный запрос статуса платежа. Тело запроса
    /// служит только сигналом «сходи проверь», зачисление идёт по ответу шлюза.
    ///
    /// Отвечаем 200 почти всегда, включая дубликаты: любой другой код провайдер
    /// считает неудачей и будет повторять доставку сутки.
    /// </summary>
    [HttpPost("gateway")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Gateway(CancellationToken cancellationToken)
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        if (!IpAllowList.IsAllowed(remoteIp, _options.WebhookAllowedIps))
        {
            _logger.LogWarning("Уведомление с неразрешённого адреса {Ip} отклонено", remoteIp);
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync(cancellationToken);

        GatewayNotification notification;
        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;
            var payload = root.GetProperty("object");
            notification = new GatewayNotification(
                root.GetProperty("event").GetString() ?? string.Empty,
                payload.GetProperty("id").GetString() ?? string.Empty,
                payload.GetProperty("status").GetString() ?? string.Empty);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Не удалось разобрать уведомление с адреса {Ip}", remoteIp);
            return BadRequest(new { detail = "Тело уведомления не разобрано." });
        }

        var outcome = await _webhooks.HandleAsync(notification, rawBody, remoteIp?.ToString(), cancellationToken);
        return Ok(new { outcome = outcome.ToString() });
    }

}
