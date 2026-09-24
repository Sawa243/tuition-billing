namespace TuitionBilling.Application.Common;

public sealed class BillingOptions
{
    public const string SectionName = "Billing";

    /// <summary>Внешний адрес биллинга: на него шлюз возвращает плательщика после оплаты.</summary>
    public string PublicUrl { get; set; } = "http://localhost:5080";

    public string OrganizationName { get; set; } = "Университет «Синергия»";

    /// <summary>
    /// Код ставки НДС для чека. Для образовательных услуг это «без НДС»
    /// (подпункт 14 пункта 2 статьи 149 Налогового кодекса).
    /// </summary>
    public int ReceiptVatCode { get; set; } = 1;

    /// <summary>Сколько раз исходящий ящик пробует доставить сообщение, прежде чем признать его мёртвым.</summary>
    public int OutboxMaxAttempts { get; set; } = 5;

    public int OutboxPollSeconds { get; set; } = 5;

    /// <summary>
    /// Адреса, с которых принимаются уведомления шлюза. Пусто — проверка выключена,
    /// так можно только в разработке.
    /// </summary>
    public string[] WebhookAllowedIps { get; set; } = Array.Empty<string>();
}
