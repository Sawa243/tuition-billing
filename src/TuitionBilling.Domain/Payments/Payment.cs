using TuitionBilling.Domain.Common;

namespace TuitionBilling.Domain.Payments;

public sealed class Payment : Entity
{
    private static readonly Dictionary<PaymentStatus, PaymentStatus[]> Allowed = new()
    {
        [PaymentStatus.Pending] = new[] { PaymentStatus.WaitingForCapture, PaymentStatus.Succeeded, PaymentStatus.Canceled },
        [PaymentStatus.WaitingForCapture] = new[] { PaymentStatus.Succeeded, PaymentStatus.Canceled },
        [PaymentStatus.Succeeded] = Array.Empty<PaymentStatus>(),
        [PaymentStatus.Canceled] = Array.Empty<PaymentStatus>()
    };

    private Payment()
    {
        IdempotencyKey = string.Empty;
        Description = string.Empty;
    }

    public Payment(Guid invoiceId, Guid contractId, Guid studentId, decimal amount, string idempotencyKey, string description)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new DomainException("Ключ идемпотентности обязателен.");
        }

        InvoiceId = invoiceId;
        ContractId = contractId;
        StudentId = studentId;
        Amount = Money.EnsurePositive(amount, "Сумма платежа");
        IdempotencyKey = idempotencyKey;
        Description = description;
        Status = PaymentStatus.Pending;
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
    }

    public Guid InvoiceId { get; private set; }

    public Guid ContractId { get; private set; }

    public Guid StudentId { get; private set; }

    public decimal Amount { get; private set; }

    public decimal RefundedAmount { get; private set; }

    public PaymentStatus Status { get; private set; }

    /// <summary>Ключ, с которым платёж создавали. По нему же отсекаются дубли.</summary>
    public string IdempotencyKey { get; private set; }

    public string Description { get; private set; }

    public string? GatewayPaymentId { get; private set; }

    public string? ConfirmationUrl { get; private set; }

    public string? CancellationReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? CapturedAt { get; private set; }

    public string? GatewayReceiptId { get; private set; }

    /// <summary>Номер фискального документа: чек по 54-ФЗ пробивает касса провайдера.</summary>
    public string? FiscalDocumentNumber { get; private set; }

    public DateTimeOffset? ReceiptRegisteredAt { get; private set; }

    public decimal RefundableAmount => Status == PaymentStatus.Succeeded ? Amount - RefundedAmount : 0m;

    public void AttachToGateway(string gatewayPaymentId, string confirmationUrl)
    {
        if (!string.IsNullOrEmpty(GatewayPaymentId))
        {
            throw new DomainException("Платёж уже связан с операцией шлюза.");
        }

        GatewayPaymentId = gatewayPaymentId;
        ConfirmationUrl = confirmationUrl;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public PaymentTransitionOutcome ApplyStatus(PaymentStatus next, DateTimeOffset occurredAt, string? cancellationReason = null)
    {
        if (next == Status)
        {
            return PaymentTransitionOutcome.AlreadyInThatState;
        }

        if (!Allowed[Status].Contains(next))
        {
            return PaymentTransitionOutcome.Ignored;
        }

        Status = next;
        UpdatedAt = occurredAt;

        if (next == PaymentStatus.Succeeded)
        {
            CapturedAt = occurredAt;
        }

        if (next == PaymentStatus.Canceled)
        {
            CancellationReason = cancellationReason;
        }

        return PaymentTransitionOutcome.Applied;
    }

    public Refund StartRefund(decimal amount, string reason, string idempotencyKey)
    {
        if (Status != PaymentStatus.Succeeded)
        {
            throw new DomainException($"Возврат возможен только по успешному платежу, текущий статус {Status}.");
        }

        var value = Money.EnsurePositive(amount, "Сумма возврата");
        if (value > RefundableAmount)
        {
            throw new DomainException($"К возврату доступно {RefundableAmount}, запрошено {value}.");
        }

        return new Refund(Id, value, reason, idempotencyKey);
    }

    public void AttachReceipt(string gatewayReceiptId, string fiscalDocumentNumber, DateTimeOffset registeredAt)
    {
        GatewayReceiptId = gatewayReceiptId;
        FiscalDocumentNumber = fiscalDocumentNumber;
        ReceiptRegisteredAt = registeredAt;
    }

    public void RegisterRefund(decimal amount)
    {
        if (amount > RefundableAmount)
        {
            throw new DomainException($"К возврату доступно {RefundableAmount}, проводим {amount}.");
        }

        RefundedAmount += amount;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
