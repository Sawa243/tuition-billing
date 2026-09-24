using PaymentGateway.Emulator.Contracts;
using PaymentGateway.Emulator.Infrastructure;

namespace PaymentGateway.Emulator.Endpoints;

public static class PaymentEndpoints
{
    public static void MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v3").WithTags("Платежи");

        group.MapPost("/payments", (HttpContext context, CreatePaymentRequest request, PaymentStore store) =>
        {
            var key = context.Request.Headers["Idempotence-Key"].ToString();
            if (string.IsNullOrWhiteSpace(key))
            {
                return MissingKey();
            }

            if (request.Amount.ToDecimal() <= 0m)
            {
                return Results.BadRequest(new ErrorDto("error", "invalid_request", "Сумма платежа должна быть больше нуля."));
            }

            var result = store.CreatePayment(key, request);
            if (result.IsConflict)
            {
                return KeyConflict();
            }

            var payment = result.Value!;
            return Results.Ok(payment.ToDto(GatewayMapper.ConfirmationUrlFor(context.Request, payment.Id)));
        })
        .WithSummary("Создать платёж");

        group.MapGet("/payments/{id}", (HttpContext context, string id, PaymentStore store) =>
        {
            var payment = store.Find(id);
            return payment is null
                ? NotFound(id)
                : Results.Ok(payment.ToDto(GatewayMapper.ConfirmationUrlFor(context.Request, payment.Id)));
        })
        .WithSummary("Запросить статус платежа")
        .WithDescription("Этим запросом магазин проверяет подлинность уведомления: телу уведомления верить нельзя.");

        group.MapPost("/payments/{id}/capture", (string id, PaymentStore store, PaymentFlow flow) =>
        {
            var payment = store.Find(id);
            if (payment is null)
            {
                return NotFound(id);
            }

            if (payment.Status != GatewayPaymentStatus.WaitingForCapture)
            {
                return InvalidState(payment.Status);
            }

            flow.Capture(payment);
            return Results.Ok(payment.ToDto(null));
        })
        .WithSummary("Подтвердить списание захолдированных денег");

        group.MapPost("/payments/{id}/cancel", (string id, PaymentStore store, PaymentFlow flow) =>
        {
            var payment = store.Find(id);
            if (payment is null)
            {
                return NotFound(id);
            }

            if (payment.Status is GatewayPaymentStatus.Succeeded or GatewayPaymentStatus.Canceled)
            {
                return InvalidState(payment.Status);
            }

            flow.Cancel(payment, "merchant", "canceled_by_merchant");
            return Results.Ok(payment.ToDto(null));
        })
        .WithSummary("Отменить платёж");

        group.MapPost("/refunds", (CreateRefundRequest request, HttpContext context, PaymentStore store) =>
        {
            var key = context.Request.Headers["Idempotence-Key"].ToString();
            if (string.IsNullOrWhiteSpace(key))
            {
                return MissingKey();
            }

            var payment = store.Find(request.PaymentId);
            if (payment is null)
            {
                return NotFound(request.PaymentId);
            }

            if (payment.Status != GatewayPaymentStatus.Succeeded)
            {
                return InvalidState(payment.Status);
            }

            var amount = request.Amount.ToDecimal();
            if (amount <= 0m || store.RefundedTotal(payment.Id) + amount > payment.Amount)
            {
                return Results.BadRequest(new ErrorDto("error", "invalid_request", "Сумма возврата больше остатка по платежу."));
            }

            var result = store.CreateRefund(key, request);
            if (result.IsConflict)
            {
                return KeyConflict();
            }

            payment.RefundedAmount = store.RefundedTotal(payment.Id);
            return Results.Ok(result.Value!.ToDto());
        })
        .WithSummary("Вернуть деньги по платежу");

        group.MapGet("/refunds/{id}", (string id, PaymentStore store) =>
        {
            var refund = store.FindRefund(id);
            return refund is null ? NotFound(id) : Results.Ok(refund.ToDto());
        })
        .WithSummary("Запросить статус возврата");

        group.MapGet("/registry", (DateOnly? date, PaymentStore store) =>
        {
            var day = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var rows = store.PaymentsOn(day)
                .Select(p => new RegistryRowDto(p.Id, p.Status, AmountDto.Of(p.Amount), AmountDto.Of(p.Fee), p.CreatedAt, p.CapturedAt))
                .ToList();

            return Results.Ok(new RegistryDto(day, rows));
        })
        .WithSummary("Реестр операций за сутки")
        .WithDescription("По нему магазин делает суточную сверку: сопоставление идёт по идентификатору операции, а не по сумме и дате.");
    }

    private static IResult MissingKey() =>
        Results.BadRequest(new ErrorDto("error", "missing_idempotence_key", "Не передан заголовок Idempotence-Key."));

    private static IResult KeyConflict() =>
        Results.Json(
            new ErrorDto("error", "idempotence_key_conflict", "Ключ идемпотентности уже использован с другими данными."),
            statusCode: StatusCodes.Status409Conflict);

    private static IResult NotFound(string id) =>
        Results.NotFound(new ErrorDto("error", "not_found", $"Операция {id} не найдена."));

    private static IResult InvalidState(string status) =>
        Results.Json(
            new ErrorDto("error", "invalid_state", $"Операция в статусе {status}, действие недоступно."),
            statusCode: StatusCodes.Status409Conflict);
}
