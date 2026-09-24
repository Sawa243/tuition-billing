using PaymentGateway.Emulator.Contracts;

namespace PaymentGateway.Emulator.Infrastructure;

/// <summary>
/// Смена состояния платежа на стороне шлюза плюс постановка уведомления в очередь.
/// Вынесено отдельно, чтобы и страница оплаты, и API дёргали один и тот же код.
/// </summary>
public sealed class PaymentFlow
{
    private readonly NotificationQueue _queue;

    public PaymentFlow(NotificationQueue queue)
    {
        _queue = queue;
    }

    public void Pay(GatewayPayment payment)
    {
        if (payment.AutoCapture)
        {
            Capture(payment);
            return;
        }

        payment.Status = GatewayPaymentStatus.WaitingForCapture;
        Notify(payment, "payment.waiting_for_capture");
    }

    public void Capture(GatewayPayment payment)
    {
        payment.Status = GatewayPaymentStatus.Succeeded;
        payment.CapturedAt = DateTimeOffset.UtcNow;
        Notify(payment, "payment.succeeded");
    }

    public void Cancel(GatewayPayment payment, string party, string reason)
    {
        payment.Status = GatewayPaymentStatus.Canceled;
        payment.CancellationParty = party;
        payment.CancellationReason = reason;
        Notify(payment, "payment.canceled");
    }

    private void Notify(GatewayPayment payment, string eventName) =>
        _queue.Enqueue(new NotificationDto("notification", eventName, payment.ToDto(null)));
}
