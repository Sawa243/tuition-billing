using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TuitionBilling.Application.Common;
using TuitionBilling.Application.Contracts;
using TuitionBilling.Application.Services;
using TuitionBilling.Infrastructure.Identity;

namespace TuitionBilling.Api.Controllers;

[Authorize]
public sealed class PaymentsController : ApiControllerBase
{
    private readonly PaymentService _payments;
    private readonly BillingOptions _options;

    public PaymentsController(PaymentService payments, IOptions<BillingOptions> options)
    {
        _payments = payments;
        _options = options.Value;
    }

    /// <summary>
    /// Создать платёж по начислению. Ключ идемпотентности клиент передаёт
    /// в заголовке Idempotence-Key: с тем же ключом повтор вернёт тот же платёж,
    /// а не создаст второй. Двойной клик по кнопке «Оплатить» — самый частый случай.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = Roles.Student)]
    [ProducesResponseType(typeof(CreatePaymentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CreatePaymentResult>> Create(
        [FromBody] CreatePaymentRequest request,
        [FromHeader(Name = "Idempotence-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        await ValidateAsync(request, cancellationToken);

        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 64)
        {
            return BadRequest(new { detail = "Нужен заголовок Idempotence-Key длиной до 64 символов, обычно это UUID." });
        }

        var result = await _payments.CreateAsync(CurrentUserId, request, idempotencyKey, cancellationToken);
        return Ok(result);
    }

    [HttpGet]
    public async Task<IReadOnlyList<PaymentDto>> List([FromQuery] int take, CancellationToken cancellationToken) =>
        await _payments.ListAsync(StudentScope, take is > 0 and <= 200 ? take : 50, cancellationToken);

    [HttpGet("{id:guid}")]
    public async Task<PaymentDto> Get(Guid id, CancellationToken cancellationToken) =>
        await _payments.GetAsync(id, StudentScope, cancellationToken);

    /// <summary>Квитанция об оплате — печатная форма для обучающегося.</summary>
    [HttpGet("{id:guid}/receipt")]
    public async Task<ReceiptDto> Receipt(Guid id, CancellationToken cancellationToken) =>
        await _payments.GetReceiptAsync(id, StudentScope, cancellationToken);

    /// <summary>
    /// Сюда шлюз возвращает плательщика после оплаты. Ни статусу в адресе,
    /// ни самому факту возврата верить нельзя — статус перезапрашивается у шлюза,
    /// и только после этого пользователь уходит обратно в кабинет.
    /// </summary>
    [HttpGet("return")]
    [AllowAnonymous]
    public async Task<IActionResult> Return([FromQuery(Name = "payment_id")] string? paymentId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(paymentId))
        {
            return Redirect($"{_options.PublicUrl.TrimEnd('/')}/?payment=unknown");
        }

        var payment = await _payments.HandleReturnAsync(paymentId, cancellationToken);
        var status = payment?.Status.ToString().ToLowerInvariant() ?? "unknown";
        return Redirect($"{_options.PublicUrl.TrimEnd('/')}/?payment={status}");
    }
}
