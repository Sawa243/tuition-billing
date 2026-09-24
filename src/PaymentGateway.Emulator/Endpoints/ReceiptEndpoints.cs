using PaymentGateway.Emulator.Contracts;
using PaymentGateway.Emulator.Infrastructure;

namespace PaymentGateway.Emulator.Endpoints;

public static class ReceiptEndpoints
{
    public static void MapReceiptEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v3").WithTags("Чеки");

        group.MapPost("/receipts", (CreateReceiptRequest request, HttpContext context, PaymentStore store) =>
        {
            var key = context.Request.Headers["Idempotence-Key"].ToString();
            if (string.IsNullOrWhiteSpace(key))
            {
                return Results.BadRequest(new ErrorDto("error", "missing_idempotence_key", "Не передан заголовок Idempotence-Key."));
            }

            var payment = store.Find(request.PaymentId);
            if (payment is null)
            {
                return Results.NotFound(new ErrorDto("error", "not_found", $"Платёж {request.PaymentId} не найден."));
            }

            if (payment.Status != GatewayPaymentStatus.Succeeded)
            {
                return Results.Json(
                    new ErrorDto("error", "invalid_state", $"Чек по платежу в статусе {payment.Status} не пробивается."),
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (request.Items.Count == 0)
            {
                return Results.BadRequest(new ErrorDto("error", "invalid_request", "В чеке должна быть хотя бы одна позиция."));
            }

            var itemsTotal = request.Items.Sum(i => i.Amount.ToDecimal() * decimal.Parse(i.Quantity, System.Globalization.CultureInfo.InvariantCulture));
            if (Math.Round(itemsTotal, 2) != payment.Amount)
            {
                return Results.BadRequest(new ErrorDto("error", "invalid_request",
                    $"Сумма позиций чека {itemsTotal:F2} не совпадает с суммой платежа {payment.Amount:F2}."));
            }

            var result = store.CreateReceipt(key, request);
            if (result.IsConflict)
            {
                return Results.Json(
                    new ErrorDto("error", "idempotence_key_conflict", "Ключ идемпотентности уже использован с другими данными."),
                    statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Ok(result.Value!.ToDto());
        })
        .WithSummary("Зарегистрировать чек в кассе провайдера")
        .WithDescription("Кассовый чек по 54-ФЗ пробивает провайдер, состав услуги и ставку НДС передаёт магазин.");

        group.MapGet("/receipts/{id}", (string id, PaymentStore store) =>
        {
            var receipt = store.FindReceipt(id);
            return receipt is null
                ? Results.NotFound(new ErrorDto("error", "not_found", $"Чек {id} не найден."))
                : Results.Ok(receipt.ToDto());
        })
        .WithSummary("Запросить чек");
    }
}
