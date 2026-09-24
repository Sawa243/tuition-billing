namespace TuitionBilling.Domain.Payments;

/// <summary>
/// Уведомление от шлюза — это вход автомата, а не команда, поэтому на него
/// есть три разумных ответа, и только один из них меняет состояние.
/// </summary>
public enum PaymentTransitionOutcome
{
    /// <summary>Статус изменён — дальше нужно провести деньги по журналу.</summary>
    Applied = 1,

    /// <summary>Платёж уже в этом статусе: повторная доставка того же события.</summary>
    AlreadyInThatState = 2,

    /// <summary>Переход назад или после конечного статуса — молча игнорируем.</summary>
    Ignored = 3
}
