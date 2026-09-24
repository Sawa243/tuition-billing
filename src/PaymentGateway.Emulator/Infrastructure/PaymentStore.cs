using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using PaymentGateway.Emulator.Contracts;

namespace PaymentGateway.Emulator.Infrastructure;

/// <summary>
/// Хранилище эмулятора — в памяти процесса. Базу сюда ставить незачем:
/// эмулятор изображает чужую систему, а не хранит наши деньги.
/// </summary>
public sealed class PaymentStore
{
    private readonly ConcurrentDictionary<string, GatewayPayment> _payments = new();
    private readonly ConcurrentDictionary<string, GatewayRefund> _refunds = new();
    private readonly ConcurrentDictionary<string, GatewayReceipt> _receipts = new();
    private readonly ConcurrentDictionary<string, IdempotencyRecord> _idempotency = new();
    private readonly GatewayOptions _options;

    public PaymentStore(GatewayOptions options)
    {
        _options = options;
    }

    private sealed record IdempotencyRecord(string RequestHash, string ResourceId);

    public GatewayPayment? Find(string id) => _payments.TryGetValue(id, out var payment) ? payment : null;

    public GatewayRefund? FindRefund(string id) => _refunds.TryGetValue(id, out var refund) ? refund : null;

    public GatewayReceipt? FindReceipt(string id) => _receipts.TryGetValue(id, out var receipt) ? receipt : null;

    public IReadOnlyList<GatewayPayment> PaymentsOn(DateOnly date) =>
        _payments.Values.Where(p => DateOnly.FromDateTime(p.CreatedAt.UtcDateTime) == date)
            .OrderBy(p => p.CreatedAt)
            .ToList();

    public decimal RefundedTotal(string paymentId) =>
        _refunds.Values.Where(r => r.PaymentId == paymentId && r.Status == "succeeded").Sum(r => r.Amount);

    public IdempotentResult<GatewayPayment> CreatePayment(string idempotenceKey, CreatePaymentRequest request)
    {
        var amount = request.Amount.ToDecimal();
        var candidate = new GatewayPayment
        {
            Id = NewId("pay"),
            Amount = amount,
            Fee = Math.Round(amount * _options.FeePercent / 100m, 2, MidpointRounding.AwayFromZero),
            AutoCapture = request.Capture ?? true,
            Description = request.Description,
            ReturnUrl = request.Confirmation.ReturnUrl,
            Metadata = request.Metadata ?? new Dictionary<string, string>(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        return Remember(idempotenceKey, Hash(request), candidate.Id, () => _payments[candidate.Id] = candidate, id => _payments[id]);
    }

    public IdempotentResult<GatewayRefund> CreateRefund(string idempotenceKey, CreateRefundRequest request)
    {
        var candidate = new GatewayRefund
        {
            Id = NewId("ref"),
            PaymentId = request.PaymentId,
            Amount = request.Amount.ToDecimal(),
            Description = request.Description,
            CreatedAt = DateTimeOffset.UtcNow
        };

        return Remember(idempotenceKey, Hash(request), candidate.Id, () => _refunds[candidate.Id] = candidate, id => _refunds[id]);
    }

    public IdempotentResult<GatewayReceipt> CreateReceipt(string idempotenceKey, CreateReceiptRequest request)
    {
        var candidate = new GatewayReceipt
        {
            Id = NewId("rec"),
            PaymentId = request.PaymentId,
            Type = request.Type,
            Items = request.Items,
            RegisteredAt = DateTimeOffset.UtcNow,
            FiscalDocumentNumber = Random.Shared.Next(100000, 999999).ToString()
        };

        return Remember(idempotenceKey, Hash(request), candidate.Id, () => _receipts[candidate.Id] = candidate, id => _receipts[id]);
    }

    /// <summary>
    /// Повтор с тем же ключом возвращает первый результат, повтор с тем же ключом
    /// но другим телом — ошибка. Так это устроено у провайдеров, и так же
    /// защищён наш собственный эндпоинт создания платежа.
    /// </summary>
    private IdempotentResult<T> Remember<T>(string key, string hash, string newId, Action persist, Func<string, T> load)
        where T : class
    {
        var record = _idempotency.GetOrAdd(key, _ =>
        {
            persist();
            return new IdempotencyRecord(hash, newId);
        });

        if (record.ResourceId == newId)
        {
            return IdempotentResult<T>.Created(load(record.ResourceId));
        }

        return record.RequestHash == hash
            ? IdempotentResult<T>.Replayed(load(record.ResourceId))
            : IdempotentResult<T>.Conflict();
    }

    private static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    private static string Hash(object request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(request))));
}

public sealed record IdempotentResult<T>(T? Value, bool IsReplay, bool IsConflict)
    where T : class
{
    public static IdempotentResult<T> Created(T value) => new(value, false, false);

    public static IdempotentResult<T> Replayed(T value) => new(value, true, false);

    public static IdempotentResult<T> Conflict() => new(null, false, true);
}
