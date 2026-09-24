namespace TuitionBilling.Domain.Payments;

/// <summary>
/// Статусы повторяют модель ЮKassa, чтобы при переходе с эмулятора
/// на настоящего провайдера не пришлось переписывать автомат.
/// </summary>
public enum PaymentStatus
{
    Pending = 1,
    WaitingForCapture = 2,
    Succeeded = 3,
    Canceled = 4
}

public static class PaymentStatusExtensions
{
    public static bool IsTerminal(this PaymentStatus status) =>
        status is PaymentStatus.Succeeded or PaymentStatus.Canceled;
}
