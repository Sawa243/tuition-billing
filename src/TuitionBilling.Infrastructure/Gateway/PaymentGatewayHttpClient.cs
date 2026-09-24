using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TuitionBilling.Application.Abstractions;
using TuitionBilling.Application.Common;
using TuitionBilling.Domain.Payments;

namespace TuitionBilling.Infrastructure.Gateway;

public sealed class GatewayClientOptions
{
    public const string SectionName = "Gateway";

    public string BaseUrl { get; set; } = "http://localhost:5090";

    public string ShopId { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 15;
}

/// <summary>
/// Клиент платёжного шлюза. Формат запросов и ответов — как у ЮKassa: snake_case,
/// суммы строкой, ключ идемпотентности в заголовке Idempotence-Key.
/// </summary>
public sealed class PaymentGatewayHttpClient : IPaymentGatewayClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly ILogger<PaymentGatewayHttpClient> _logger;

    public PaymentGatewayHttpClient(HttpClient http, ILogger<PaymentGatewayHttpClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<GatewayPaymentView> CreatePaymentAsync(
        decimal amount,
        string description,
        string returnUrl,
        IReadOnlyDictionary<string, string> metadata,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var body = new
        {
            amount = Amount(amount),
            confirmation = new { type = "redirect", return_url = returnUrl },
            description,
            capture = true,
            metadata
        };

        var payment = await SendAsync<PaymentResponse>(HttpMethod.Post, "/v3/payments", body, idempotencyKey, cancellationToken);
        return ToView(payment);
    }

    public async Task<GatewayPaymentView?> GetPaymentAsync(string gatewayPaymentId, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync($"/v3/payments/{gatewayPaymentId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        var payment = await response.Content.ReadFromJsonAsync<PaymentResponse>(Json, cancellationToken)
                      ?? throw new GatewayException("Шлюз вернул пустой ответ на запрос статуса.");

        return ToView(payment);
    }

    public async Task<GatewayRefundView> CreateRefundAsync(
        string gatewayPaymentId,
        decimal amount,
        string description,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var body = new { payment_id = gatewayPaymentId, amount = Amount(amount), description };
        var refund = await SendAsync<RefundResponse>(HttpMethod.Post, "/v3/refunds", body, idempotencyKey, cancellationToken);
        return new GatewayRefundView(refund.Id, refund.PaymentId, ParseAmount(refund.Amount), refund.Status);
    }

    public async Task<GatewayReceiptView> CreateReceiptAsync(
        GatewayReceiptRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var body = new
        {
            type = "payment",
            payment_id = request.GatewayPaymentId,
            send = true,
            customer = new { email = request.CustomerEmail },
            items = request.Items.Select(i => new
            {
                description = i.Description,
                quantity = "1.00",
                amount = Amount(i.Amount),
                vat_code = i.VatCode,
                payment_subject = "service",
                payment_mode = "full_payment"
            })
        };

        var receipt = await SendAsync<ReceiptResponse>(HttpMethod.Post, "/v3/receipts", body, idempotencyKey, cancellationToken);
        return new GatewayReceiptView(receipt.Id, receipt.Status, receipt.FiscalDocumentNumber);
    }

    public async Task<IReadOnlyList<GatewayRegistryRow>> GetRegistryAsync(DateOnly date, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync($"/v3/registry?date={date:yyyy-MM-dd}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var registry = await response.Content.ReadFromJsonAsync<RegistryResponse>(Json, cancellationToken)
                       ?? throw new GatewayException("Шлюз вернул пустой реестр.");

        return registry.Items
            .Select(r => new GatewayRegistryRow(
                r.PaymentId,
                ParseStatus(r.Status),
                ParseAmount(r.Amount),
                ParseAmount(r.Fee),
                r.CreatedAt,
                r.CapturedAt))
            .ToList();
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        object body,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body, options: Json)
        };

        request.Headers.Add("Idempotence-Key", idempotencyKey);

        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
               ?? throw new GatewayException($"Шлюз вернул пустой ответ на {method} {path}.");
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogError("Шлюз ответил {Status}: {Body}", (int)response.StatusCode, text);
        throw new GatewayException($"Шлюз ответил {(int)response.StatusCode}: {text}");
    }

    private static object Amount(decimal value) =>
        new { value = value.ToString("F2", CultureInfo.InvariantCulture), currency = "RUB" };

    private static decimal ParseAmount(AmountResponse amount) =>
        decimal.Parse(amount.Value, NumberStyles.Number, CultureInfo.InvariantCulture);

    private static PaymentStatus ParseStatus(string status) => status switch
    {
        "pending" => PaymentStatus.Pending,
        "waiting_for_capture" => PaymentStatus.WaitingForCapture,
        "succeeded" => PaymentStatus.Succeeded,
        "canceled" => PaymentStatus.Canceled,
        _ => throw new GatewayException($"Шлюз прислал неизвестный статус {status}.")
    };

    private static GatewayPaymentView ToView(PaymentResponse payment) =>
        new(payment.Id,
            ParseStatus(payment.Status),
            ParseAmount(payment.Amount),
            payment.IncomeAmount is null ? null : ParseAmount(payment.IncomeAmount),
            payment.Confirmation?.ConfirmationUrl,
            payment.CancellationDetails?.Reason,
            payment.CreatedAt,
            payment.CapturedAt);

    private sealed record AmountResponse(string Value, string Currency);

    private sealed record ConfirmationResponse(string Type, string? ReturnUrl, string? ConfirmationUrl);

    private sealed record CancellationDetailsResponse(string Party, string Reason);

    private sealed record PaymentResponse(
        string Id,
        string Status,
        AmountResponse Amount,
        AmountResponse? IncomeAmount,
        ConfirmationResponse? Confirmation,
        CancellationDetailsResponse? CancellationDetails,
        DateTimeOffset CreatedAt,
        DateTimeOffset? CapturedAt);

    private sealed record RefundResponse(string Id, string PaymentId, string Status, AmountResponse Amount);

    private sealed record ReceiptResponse(string Id, string Status, string FiscalDocumentNumber);

    private sealed record RegistryRowResponse(
        string PaymentId,
        string Status,
        AmountResponse Amount,
        AmountResponse Fee,
        DateTimeOffset CreatedAt,
        DateTimeOffset? CapturedAt);

    private sealed record RegistryResponse(DateOnly Date, IReadOnlyList<RegistryRowResponse> Items);
}
