using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NBomber.Contracts.Stats;
using NBomber.CSharp;

// Нагрузочный прогон по работающей системе: биллинг и эмулятор шлюза должны быть
// подняты заранее. Сценарии намеренно разной тяжести — от чтения списка начислений
// до создания платежа, который внутри ходит по HTTP во внешний шлюз.
//
// Лицензия NBomber разрешает бесплатное академическое использование: прогон
// в рамках учебной работы, без коммерческой подписки.

var baseUrl = Environment.GetEnvironmentVariable("LOAD_BASE_URL") ?? "http://localhost:5080";
var accountantEmail = Environment.GetEnvironmentVariable("LOAD_ACCOUNTANT_EMAIL") ?? "buh@synergy.local";
var accountantPassword = Environment.GetEnvironmentVariable("LOAD_ACCOUNTANT_PASSWORD")
                         ?? throw new InvalidOperationException("Не задан LOAD_ACCOUNTANT_PASSWORD.");
var studentEmail = Environment.GetEnvironmentVariable("LOAD_STUDENT_EMAIL") ?? "ivanov@synergy.local";
var studentPassword = Environment.GetEnvironmentVariable("LOAD_STUDENT_PASSWORD")
                      ?? throw new InvalidOperationException("Не задан LOAD_STUDENT_PASSWORD.");

var seconds = Setting("LOAD_SECONDS", 30);

// Интенсивность вынесена в переменные окружения: одним и тем же прогоном
// снимается и обычная рабочая нагрузка, и поиск потолка.
var readRate = Setting("LOAD_READ_RATE", 120);
var historyRate = Setting("LOAD_HISTORY_RATE", 60);
var createRate = Setting("LOAD_CREATE_RATE", 25);
var webhookRate = Setting("LOAD_WEBHOOK_RATE", 60);

static int Setting(string name, int fallback) =>
    int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        ? value
        : fallback;
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

using var setup = new HttpClient { BaseAddress = new Uri(baseUrl) };

Console.WriteLine($"Готовлю данные на {baseUrl}");

var accountantToken = await LoginAsync(setup, accountantEmail, accountantPassword);
var studentToken = await LoginAsync(setup, studentEmail, studentPassword);
var studentId = await MeAsync(setup, studentToken);

// Отдельный договор под нагрузку: платежи по нему создаются тысячами,
// и портить ими демонстрационные данные незачем.
var invoiceId = await PrepareInvoiceAsync(setup, accountantToken, studentId);
Console.WriteLine($"Начисление под нагрузку: {invoiceId}");

var student = new HttpClient { BaseAddress = new Uri(baseUrl) };
student.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", studentToken);

var anonymous = new HttpClient { BaseAddress = new Uri(baseUrl) };

var invoices = Scenario.Create("invoices_read", async _ =>
    {
        var response = await student.GetAsync("/api/invoices");
        return response.IsSuccessStatusCode ? Response.Ok() : Response.Fail(statusCode: ((int)response.StatusCode).ToString());
    })
    .WithWarmUpDuration(TimeSpan.FromSeconds(5))
    .WithLoadSimulations(Simulation.Inject(rate: readRate, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(seconds)));

var history = Scenario.Create("payments_history", async _ =>
    {
        var response = await student.GetAsync("/api/payments?take=50");
        return response.IsSuccessStatusCode ? Response.Ok() : Response.Fail(statusCode: ((int)response.StatusCode).ToString());
    })
    .WithWarmUpDuration(TimeSpan.FromSeconds(5))
    .WithLoadSimulations(Simulation.Inject(rate: historyRate, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(seconds)));

// Самый тяжёлый сценарий: запись в базу плюс синхронный поход в шлюз.
var createPayment = Scenario.Create("payment_create", async _ =>
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
        {
            Content = JsonContent.Create(new { invoiceId, amount = 1.00m })
        };

        request.Headers.Add("Idempotence-Key", Guid.NewGuid().ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", studentToken);

        var response = await anonymous.SendAsync(request);
        return response.IsSuccessStatusCode ? Response.Ok() : Response.Fail(statusCode: ((int)response.StatusCode).ToString());
    })
    .WithWarmUpDuration(TimeSpan.FromSeconds(5))
    .WithLoadSimulations(Simulation.Inject(rate: createRate, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(seconds)));

// Уведомление по несуществующей операции: путь тот же, что у настоящего,
// включая запись в журнал событий, но чужие платежи прогон не портит.
var webhook = Scenario.Create("webhook_receive", async _ =>
    {
        var body = new
        {
            type = "notification",
            @event = "payment.succeeded",
            @object = new { id = $"pay_load_{Guid.NewGuid():N}", status = "succeeded", amount = new { value = "1.00", currency = "RUB" } }
        };

        var response = await anonymous.PostAsJsonAsync("/api/webhooks/gateway", body);
        return response.IsSuccessStatusCode ? Response.Ok() : Response.Fail(statusCode: ((int)response.StatusCode).ToString());
    })
    .WithWarmUpDuration(TimeSpan.FromSeconds(5))
    .WithLoadSimulations(Simulation.Inject(rate: webhookRate, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(seconds)));

NBomberRunner
    .RegisterScenarios(invoices, history, createPayment, webhook)
    .WithReportFolder("load-reports")
    .WithReportFileName("tuition-billing")
    .WithReportFormats(ReportFormat.Html, ReportFormat.Md, ReportFormat.Csv, ReportFormat.Txt)
    .Run();

student.Dispose();
anonymous.Dispose();

async Task<string> LoginAsync(HttpClient client, string email, string password)
{
    var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
    response.EnsureSuccessStatusCode();

    var payload = await response.Content.ReadFromJsonAsync<JsonElement>(json);
    return payload.GetProperty("token").GetString()!;
}

async Task<Guid> MeAsync(HttpClient client, string token)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    var response = await client.SendAsync(request);
    response.EnsureSuccessStatusCode();

    var payload = await response.Content.ReadFromJsonAsync<JsonElement>(json);
    return payload.GetProperty("id").GetGuid();
}

async Task<Guid> PrepareInvoiceAsync(HttpClient client, string token, Guid student)
{
    var suffix = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    using var contractRequest = new HttpRequestMessage(HttpMethod.Post, "/api/contracts")
    {
        Content = JsonContent.Create(new
        {
            studentId = student,
            number = $"LOAD-{suffix}",
            programName = "Нагрузочный прогон",
            admissionYear = 2026,
            totalAmount = 1_000_000m,
            signedOn = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd")
        })
    };

    contractRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    var contractResponse = await client.SendAsync(contractRequest);
    contractResponse.EnsureSuccessStatusCode();

    var contract = await contractResponse.Content.ReadFromJsonAsync<JsonElement>(json);
    var contractId = contract.GetProperty("id").GetGuid();

    using var invoiceRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/contracts/{contractId}/invoices")
    {
        Content = JsonContent.Create(new
        {
            periodCode = "2026/2027-2",
            amount = 1_000_000m,
            issuedOn = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
            dueOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)).ToString("yyyy-MM-dd")
        })
    };

    invoiceRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    var invoiceResponse = await client.SendAsync(invoiceRequest);
    invoiceResponse.EnsureSuccessStatusCode();

    var invoice = await invoiceResponse.Content.ReadFromJsonAsync<JsonElement>(json);
    return invoice.GetProperty("id").GetGuid();
}
