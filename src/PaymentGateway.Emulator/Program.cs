using Microsoft.Extensions.Options;
using PaymentGateway.Emulator.Endpoints;
using PaymentGateway.Emulator.Infrastructure;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) => configuration.ReadFrom.Configuration(context.Configuration));

builder.Services.AddOptions<GatewayOptions>()
    .Bind(builder.Configuration.GetSection(GatewayOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.ShopId) && !string.IsNullOrWhiteSpace(o.SecretKey),
        "Не заданы Gateway:ShopId и Gateway:SecretKey. Секреты берутся из переменных окружения или dotnet user-secrets.")
    .ValidateOnStart();

builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<GatewayOptions>>().Value);
builder.Services.AddSingleton<PaymentStore>();
builder.Services.AddSingleton<NotificationQueue>();
builder.Services.AddSingleton<PaymentFlow>();
builder.Services.AddHostedService<NotificationSender>();
builder.Services.AddHttpClient("notifications", client => client.Timeout = TimeSpan.FromSeconds(10));

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = GatewayJson.Options.PropertyNamingPolicy;
    options.SerializerOptions.DictionaryKeyPolicy = GatewayJson.Options.DictionaryKeyPolicy;
    options.SerializerOptions.DefaultIgnoreCondition = GatewayJson.Options.DefaultIgnoreCondition;
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => options.SwaggerDoc("v1", new()
{
    Title = "Учебный эмулятор платёжного шлюза",
    Version = "v1",
    Description = "Повторяет модель ЮKassa: Basic-авторизация магазина, заголовок Idempotence-Key, "
                  + "статусы платежа и уведомления без подписи."
}));

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseMiddleware<ShopAuthenticationMiddleware>();
app.UseSwagger();
app.UseSwaggerUI();

app.MapPaymentEndpoints();
app.MapReceiptEndpoints();
app.MapCheckoutEndpoints();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();

app.Run();

// Метка сборки для интеграционных тестов, см. такую же в биллинге.
namespace PaymentGateway.Emulator
{
    public sealed class GatewayEmulatorMarker;
}
