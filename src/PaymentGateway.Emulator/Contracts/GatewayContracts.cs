using System.Globalization;

namespace PaymentGateway.Emulator.Contracts;

/// <summary>
/// Формат повторяет API ЮKassa: snake_case, суммы строкой с двумя знаками.
/// Сумма строкой — не каприз: так провайдер не даёт клиенту прислать число
/// с плавающей точкой и потерять копейку ещё до разбора запроса.
/// </summary>
public sealed record AmountDto(string Value, string Currency)
{
    public static AmountDto Of(decimal amount) => new(amount.ToString("F2", CultureInfo.InvariantCulture), "RUB");

    public decimal ToDecimal() => decimal.Parse(Value, NumberStyles.Number, CultureInfo.InvariantCulture);
}

public sealed record ConfirmationDto(string Type, string? ReturnUrl = null, string? ConfirmationUrl = null);

public sealed record CancellationDetailsDto(string Party, string Reason);

public sealed record CreatePaymentRequest(
    AmountDto Amount,
    ConfirmationDto Confirmation,
    string? Description,
    bool? Capture,
    Dictionary<string, string>? Metadata);

public sealed record CreateRefundRequest(string PaymentId, AmountDto Amount, string? Description);

public sealed record PaymentDto(
    string Id,
    string Status,
    AmountDto Amount,
    AmountDto? IncomeAmount,
    bool Paid,
    bool Refundable,
    string? Description,
    ConfirmationDto? Confirmation,
    CancellationDetailsDto? CancellationDetails,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CapturedAt,
    Dictionary<string, string>? Metadata);

public sealed record RefundDto(
    string Id,
    string PaymentId,
    string Status,
    AmountDto Amount,
    string? Description,
    DateTimeOffset CreatedAt);

public sealed record NotificationDto(string Type, string Event, PaymentDto Object);

public sealed record RegistryRowDto(
    string PaymentId,
    string Status,
    AmountDto Amount,
    AmountDto Fee,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CapturedAt);

public sealed record RegistryDto(DateOnly Date, IReadOnlyList<RegistryRowDto> Items);

public sealed record ErrorDto(string Type, string Code, string Description);

public static class GatewayPaymentStatus
{
    public const string Pending = "pending";
    public const string WaitingForCapture = "waiting_for_capture";
    public const string Succeeded = "succeeded";
    public const string Canceled = "canceled";
}
