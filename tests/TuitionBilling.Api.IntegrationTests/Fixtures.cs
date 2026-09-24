using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using TuitionBilling.Domain.Students;
using TuitionBilling.Infrastructure.Identity;
using TuitionBilling.Infrastructure.Persistence;

namespace TuitionBilling.Api.IntegrationTests;

/// <summary>
/// Данные под каждый тест заводятся свои — с уникальной почтой и номером договора.
/// Так тесты не мешают друг другу и не требуют чистить базу между прогонами.
/// </summary>
public static class TestData
{
    public const string Password = "Test2026!pass";

    public static async Task<HttpClient> AccountantAsync(TestApplication app)
    {
        var email = $"acc-{Guid.NewGuid():N}@test.local";
        await CreateUserAsync(app, email, "Бухгалтер Тестовый", Roles.Accountant);
        return await app.SignInAsync(email, Password);
    }

    public static async Task<(Guid Id, string Email, HttpClient Client)> StudentAsync(TestApplication app, string? fullName = null)
    {
        var email = $"stu-{Guid.NewGuid():N}@test.local";
        var name = fullName ?? "Тестов Тест Тестович";
        var id = await CreateUserAsync(app, email, name, Roles.Student);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            db.Students.Add(new Student(id, name, email));
            await db.SaveChangesAsync();
        }

        return (id, email, await app.SignInAsync(email, Password));
    }

    private static async Task<Guid> CreateUserAsync(TestApplication app, string email, string fullName, string role)
    {
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        if (!await roles.RoleExistsAsync(role))
        {
            await roles.CreateAsync(new IdentityRole<Guid>(role));
        }

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            FullName = fullName,
            EmailConfirmed = true
        };

        var created = await users.CreateAsync(user, Password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        await users.AddToRoleAsync(user, role);

        return user.Id;
    }

    /// <summary>Договор плюс начисление на указанную сумму. Возвращает идентификаторы обоих.</summary>
    public static async Task<(Guid ContractId, Guid InvoiceId)> ContractWithInvoiceAsync(
        HttpClient accountant,
        Guid studentId,
        decimal total = 480_000m,
        decimal invoiceAmount = 120_000m,
        string period = "2026/2027-1")
    {
        var contractResponse = await accountant.PostAsJsonAsync("/api/contracts", new
        {
            studentId,
            number = $"ДГ-{Guid.NewGuid().ToString("N")[..10]}",
            programName = "02.03.03 Математическое обеспечение и администрирование информационных систем",
            admissionYear = 2026,
            totalAmount = total,
            signedOn = "2026-08-25"
        });

        contractResponse.EnsureSuccessStatusCode();
        var contract = await contractResponse.Content.ReadFromJsonAsync<JsonElement>(TestApplication.Json);
        var contractId = contract.GetProperty("id").GetGuid();

        var invoiceResponse = await accountant.PostAsJsonAsync($"/api/contracts/{contractId}/invoices", new
        {
            periodCode = period,
            amount = invoiceAmount,
            issuedOn = "2026-08-25",
            dueOn = "2026-09-25"
        });

        invoiceResponse.EnsureSuccessStatusCode();
        var invoice = await invoiceResponse.Content.ReadFromJsonAsync<JsonElement>(TestApplication.Json);

        return (contractId, invoice.GetProperty("id").GetGuid());
    }

    /// <summary>Создать платёж и вернуть его идентификаторы у нас и у шлюза.</summary>
    public static async Task<(Guid PaymentId, string GatewayId, string ConfirmationUrl)> StartPaymentAsync(
        HttpClient student,
        Guid invoiceId,
        decimal amount,
        string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
        {
            Content = JsonContent.Create(new { invoiceId, amount })
        };

        request.Headers.Add("Idempotence-Key", idempotencyKey ?? Guid.NewGuid().ToString());

        var response = await student.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var created = await response.Content.ReadFromJsonAsync<JsonElement>(TestApplication.Json);
        var paymentId = created.GetProperty("paymentId").GetGuid();

        var payment = await student.GetFromJsonAsync<JsonElement>($"/api/payments/{paymentId}", TestApplication.Json);

        return (paymentId,
            payment.GetProperty("gatewayPaymentId").GetString()!,
            created.GetProperty("confirmationUrl").GetString()!);
    }

    /// <summary>Оплатить на странице шлюза тестовой картой.</summary>
    public static async Task PayOnGatewayAsync(TestApplication app, string gatewayPaymentId, bool success = true)
    {
        var card = success ? "4111111111111111" : "4000000000000002";
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["card"] = card,
            ["expiry"] = "12/29",
            ["cvc"] = "123"
        });

        var response = await app.Gateway.PostAsync($"/checkout/{gatewayPaymentId}", form);
        Assert.True((int)response.StatusCode is 200 or 302, $"Страница оплаты ответила {(int)response.StatusCode}");
    }

    /// <summary>Уведомление от шлюза — ровно в том виде, в каком его шлёт провайдер.</summary>
    public static Task<HttpResponseMessage> SendWebhookAsync(
        HttpClient anonymous,
        string gatewayPaymentId,
        string status,
        string @event)
    {
        var body = new
        {
            type = "notification",
            @event,
            @object = new { id = gatewayPaymentId, status, amount = new { value = "1.00", currency = "RUB" } }
        };

        return anonymous.PostAsJsonAsync("/api/webhooks/gateway", body);
    }
}
