namespace TuitionBilling.Domain.Common;

/// <summary>
/// Система однова́лютная, поэтому отдельного типа "деньги" нет: везде decimal,
/// а здесь лежат общие правила обращения с суммами.
/// </summary>
public static class Money
{
    public const string Currency = "RUB";

    /// <summary>
    /// Округление до копеек. Именно AwayFromZero: так считает бухгалтерия,
    /// а не банковское округление к чётному, которое стоит в .NET по умолчанию.
    /// </summary>
    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    public static decimal EnsurePositive(decimal amount, string what)
    {
        if (amount <= 0m)
        {
            throw new DomainException($"{what}: сумма должна быть больше нуля, получено {amount}.");
        }

        var rounded = Round(amount);
        if (rounded != amount)
        {
            throw new DomainException($"{what}: сумма {amount} точнее копейки.");
        }

        return rounded;
    }
}
