namespace PaymentGateway.Emulator.Infrastructure;

public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>Идентификатор магазина — логин для Basic-авторизации, как у настоящего провайдера.</summary>
    public string ShopId { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Адрес уведомлений задаётся в настройках магазина, а не в каждом платеже, —
    /// иначе кто угодно мог бы попросить слать уведомления себе.
    /// </summary>
    public string NotificationUrl { get; set; } = string.Empty;

    /// <summary>Комиссия провайдера в процентах. Нужна, чтобы реестр сверки был похож на настоящий.</summary>
    public decimal FeePercent { get; set; } = 2.8m;

    public int NotificationRetries { get; set; } = 3;

    public int NotificationRetryDelayMs { get; set; } = 500;
}
