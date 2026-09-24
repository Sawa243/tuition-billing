namespace TuitionBilling.Application.Persistence;

/// <summary>
/// Сообщение исходящего ящика. Пишется в одной транзакции с бизнес-изменением,
/// а отправляется уже фоновой службой: иначе «сохранили в базу и сходили по HTTP»
/// разъедется при первом же падении между этими двумя шагами.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Kind { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ProcessedAt { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    public string? LastError { get; set; }

    /// <summary>Сообщение, которое не удалось доставить за отведённое число попыток.</summary>
    public bool IsDead { get; set; }
}

/// <summary>
/// Журнал входящих уведомлений шлюза. Нужен и для защиты от повторной доставки,
/// и просто как аудит: по нему видно, что именно присылал провайдер.
/// </summary>
public sealed class WebhookEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Ключ дедупликации: событие плюс операция плюс статус.</summary>
    public string DeduplicationKey { get; set; } = string.Empty;

    public string Event { get; set; } = string.Empty;

    public string GatewayPaymentId { get; set; } = string.Empty;

    public string ReportedStatus { get; set; } = string.Empty;

    /// <summary>Статус, который вернул шлюз на обратный запрос. Верим именно ему.</summary>
    public string? VerifiedStatus { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public string RawBody { get; set; } = string.Empty;

    public string? SourceIp { get; set; }

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Ключ идемпотентности создания платежа. Защита строится на уникальном индексе:
/// «сначала проверить, потом вставить» гонку не закрывает, два запроса пройдут
/// проверку одновременно.
/// </summary>
public sealed class PaymentIdempotencyKey
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Key { get; set; } = string.Empty;

    /// <summary>Отпечаток запроса: тот же ключ с другими данными — это ошибка, а не повтор.</summary>
    public string RequestHash { get; set; } = string.Empty;

    public Guid PaymentId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ReconciliationReport
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateOnly Date { get; set; }

    public DateTimeOffset RunAt { get; set; } = DateTimeOffset.UtcNow;

    public int GatewayOperations { get; set; }

    public int LocalPayments { get; set; }

    public int Matched { get; set; }

    public int Discrepancies { get; set; }

    public int AutoRepaired { get; set; }

    public decimal GatewayTotal { get; set; }

    public decimal LocalTotal { get; set; }

    /// <summary>Расхождение витрины и журнала проводок: в норме всегда ноль.</summary>
    public decimal LedgerDrift { get; set; }

    public string Details { get; set; } = "[]";
}
