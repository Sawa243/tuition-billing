using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PaymentGateway.Emulator;
using TuitionBilling.Api;
using TuitionBilling.Application.Abstractions;
using TuitionBilling.Infrastructure.Gateway;
using TuitionBilling.Infrastructure.Persistence;

namespace TuitionBilling.Api.IntegrationTests;

/// <summary>
/// Поднимает оба сервиса разом: биллинг и эмулятор шлюза. Биллинг ходит в шлюз
/// не по сети, а через обработчик тестового сервера, поэтому портов не нужно,
/// зато работает настоящий HTTP-конвейер обоих приложений.
///
/// База — настоящий PostgreSQL (tuition_billing_tests), а не SQLite: проверять
/// numeric, уникальные индексы и xmin имеет смысл только на той СУБД, которая
/// стоит в бою.
/// </summary>
public sealed class TestApplication : IAsyncLifetime
{
    public const string TestConnectionString =
        "Host=localhost;Port=55432;Database=tuition_billing_tests;Username=billing;Password=billing_local_dev";

    private WebApplicationFactory<GatewayEmulatorMarker> _gateway = null!;
    private WebApplicationFactory<BillingApiMarker> _billing = null!;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public HttpClient Gateway { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public IServiceProvider Services => _billing.Services;

    public Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_DB_CONNECTION") ?? TestConnectionString;

        _gateway = new WebApplicationFactory<GatewayEmulatorMarker>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(Environments.Production);
                builder.UseSetting("Gateway:ShopId", "test-shop");
                builder.UseSetting("Gateway:SecretKey", "test-secret");
                // Уведомления в тестах не рассылаем: их отправляет сам тест,
                // иначе эмулятору пришлось бы достучаться до биллинга по сети.
                builder.UseSetting("Gateway:NotificationUrl", string.Empty);
                builder.UseSetting("Gateway:FeePercent", "2.8");
            });

        // Без этого клиент пойдёт по редиректу со страницы оплаты на адрес
        // биллинга и упрётся в 404 внутри тестового сервера шлюза.
        Gateway = _gateway.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Gateway.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String("test-shop:test-secret"u8.ToArray()));

        _billing = new WebApplicationFactory<BillingApiMarker>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(Environments.Production);
                builder.UseSetting("ConnectionStrings:Billing", connectionString);
                builder.UseSetting("Jwt:SigningKey", "integration-tests-signing-key-0123456789");
                builder.UseSetting("Gateway:ShopId", "test-shop");
                builder.UseSetting("Gateway:SecretKey", "test-secret");
                builder.UseSetting("Gateway:BaseUrl", "http://gateway.test");
                builder.UseSetting("Billing:PublicUrl", "http://billing.test");
                builder.UseSetting("Billing:OutboxPollSeconds", "1");
                builder.UseSetting("Seed:Enabled", "false");
                builder.UseSetting("Reconciliation:Enabled", "false");
                builder.UseSetting("Database:MigrateOnStartup", "true");

                builder.ConfigureTestServices(services =>
                {
                    // Последняя регистрация первичного обработчика побеждает,
                    // поэтому запросы к шлюзу уходят в его тестовый сервер.
                    services.AddHttpClient<IPaymentGatewayClient, PaymentGatewayHttpClient>()
                        .ConfigurePrimaryHttpMessageHandler(() => _gateway.Server.CreateHandler());
                });
            });

        Client = _billing.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        Gateway.Dispose();
        await _billing.DisposeAsync();
        await _gateway.DisposeAsync();
    }

    public async Task<T> ScopedAsync<T>(Func<BillingDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        return await action(db);
    }

    /// <summary>Клиент с токеном конкретного пользователя.</summary>
    public async Task<HttpClient> SignInAsync(string email, string password)
    {
        var response = await Client.PostAsJsonAsync("/api/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        var token = payload.GetProperty("token").GetString();

        var client = _billing.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public HttpClient Anonymous() => _billing.CreateClient();
}

[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<TestApplication>
{
    public const string Name = "integration";
}
