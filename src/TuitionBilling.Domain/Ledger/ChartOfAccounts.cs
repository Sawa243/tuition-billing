namespace TuitionBilling.Domain.Ledger;

/// <summary>
/// План счётов. Коды условные, учебные: полноценный план бюджетного учёта
/// сюда тащить незачем, а пять счетов закрывают весь оборот денег в системе.
/// </summary>
public static class ChartOfAccounts
{
    /// <summary>Дебиторская задолженность студента. Заводится отдельным субсчётом на каждый договор.</summary>
    public const string StudentReceivable = "1205";

    /// <summary>Деньги в пути: платёж прошёл, но провайдер ещё не перевёл их на расчётный счёт.</summary>
    public const string GatewayClearing = "1210";

    /// <summary>Расчётный счёт университета.</summary>
    public const string BankSettlement = "1010";

    /// <summary>Доходы от платных образовательных услуг.</summary>
    public const string TuitionRevenue = "4010";

    /// <summary>Комиссия платёжного провайдера.</summary>
    public const string GatewayFee = "5010";

    public static AccountKind KindOf(string code) => code switch
    {
        StudentReceivable or GatewayClearing or BankSettlement => AccountKind.Asset,
        TuitionRevenue => AccountKind.Revenue,
        GatewayFee => AccountKind.Expense,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Неизвестный код счёта.")
    };

    public static string NameOf(string code) => code switch
    {
        StudentReceivable => "Расчёты со студентами по оплате обучения",
        GatewayClearing => "Средства в пути у платёжного провайдера",
        BankSettlement => "Расчётный счёт",
        TuitionRevenue => "Доходы от платных образовательных услуг",
        GatewayFee => "Комиссия платёжного провайдера",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Неизвестный код счёта.")
    };
}
