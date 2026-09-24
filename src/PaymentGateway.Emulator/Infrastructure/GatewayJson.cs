using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaymentGateway.Emulator.Infrastructure;

/// <summary>
/// snake_case и отсутствие null-полей — ровно то, как выглядит ответ настоящего
/// платёжного API. Один набор настроек и на эндпоинты, и на отправку уведомлений.
/// </summary>
public static class GatewayJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
