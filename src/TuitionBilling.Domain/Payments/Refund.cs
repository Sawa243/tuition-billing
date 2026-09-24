using TuitionBilling.Domain.Common;

namespace TuitionBilling.Domain.Payments;

public sealed class Refund : Entity
{
    private Refund()
    {
        Reason = string.Empty;
        IdempotencyKey = string.Empty;
    }

    internal Refund(Guid paymentId, decimal amount, string reason, string idempotencyKey)
    {
        PaymentId = paymentId;
        Amount = amount;
        Reason = reason;
        IdempotencyKey = idempotencyKey;
        Status = RefundStatus.Pending;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid PaymentId { get; private set; }

    public decimal Amount { get; private set; }

    public string Reason { get; private set; }

    public string IdempotencyKey { get; private set; }

    public RefundStatus Status { get; private set; }

    public string? GatewayRefundId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public void Complete(string gatewayRefundId, DateTimeOffset occurredAt)
    {
        if (Status != RefundStatus.Pending)
        {
            throw new DomainException($"Возврат уже в статусе {Status}.");
        }

        GatewayRefundId = gatewayRefundId;
        Status = RefundStatus.Succeeded;
        CompletedAt = occurredAt;
    }

    public void Cancel(DateTimeOffset occurredAt)
    {
        if (Status != RefundStatus.Pending)
        {
            throw new DomainException($"Возврат уже в статусе {Status}.");
        }

        Status = RefundStatus.Canceled;
        CompletedAt = occurredAt;
    }
}
