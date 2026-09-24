using TuitionBilling.Domain.Payments;

namespace TuitionBilling.Application.Abstractions;

public sealed record GatewayPaymentView(
    string Id,
    PaymentStatus Status,
    decimal Amount,
    decimal? IncomeAmount,
    string? ConfirmationUrl,
    string? CancellationReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CapturedAt);

public sealed record GatewayRefundView(string Id, string PaymentId, decimal Amount, string Status);

public sealed record GatewayReceiptView(string Id, string Status, string FiscalDocumentNumber);

public sealed record GatewayRegistryRow(
    string PaymentId,
    PaymentStatus Status,
    decimal Amount,
    decimal Fee,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CapturedAt);

public sealed record GatewayReceiptItem(string Description, decimal Amount, int VatCode);

public sealed record GatewayReceiptRequest(
    string GatewayPaymentId,
    string CustomerEmail,
    IReadOnlyList<GatewayReceiptItem> Items);

/// <summary>
/// Клиент платёжного шлюза. Реализация ходит по HTTP в эмулятор, но интерфейс
/// описан так, чтобы подменить его на настоящего провайдера можно было одним классом.
/// </summary>
public interface IPaymentGatewayClient
{
    Task<GatewayPaymentView> CreatePaymentAsync(
        decimal amount,
        string description,
        string returnUrl,
        IReadOnlyDictionary<string, string> metadata,
        string idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Обратный запрос статуса. Именно он, а не тело уведомления, служит
    /// источником правды: подписи у уведомлений нет.
    /// </summary>
    Task<GatewayPaymentView?> GetPaymentAsync(string gatewayPaymentId, CancellationToken cancellationToken);

    Task<GatewayRefundView> CreateRefundAsync(
        string gatewayPaymentId,
        decimal amount,
        string description,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<GatewayReceiptView> CreateReceiptAsync(
        GatewayReceiptRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<GatewayRegistryRow>> GetRegistryAsync(DateOnly date, CancellationToken cancellationToken);
}
