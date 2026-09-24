using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TuitionBilling.Api.Middleware;

namespace TuitionBilling.Api.IntegrationTests;

/// <summary>
/// Проверки безопасности, которые задание называет отдельным видом тестирования:
/// подделка уведомления, повтор платежа по тому же ключу и попытка зайти чужой ролью.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class SecurityTests
{
    private readonly TestApplication _app;

    public SecurityTests(TestApplication app)
    {
        _app = app;
    }

    [Fact]
    public async Task ForgedNotification_DoesNotCreditThePayment()
    {
        var accountant = await TestData.AccountantAsync(_app);
        var (studentId, _, student) = await TestData.StudentAsync(_app);
        var (_, invoiceId) = await TestData.ContractWithInvoiceAsync(accountant, studentId);
        var (paymentId, gatewayId, _) = await TestData.StartPaymentAsync(student, invoiceId, 50_000m);

        // На шлюзе никто не платил, а уведомление говорит «succeeded».
        var response = await TestData.SendWebhookAsync(_app.Anonymous(), gatewayId, "succeeded", "payment.succeeded");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payment = await student.GetFromJsonAsync<JsonElement>($"/api/payments/{paymentId}", TestApplication.Json);
        Assert.Equal("Pending", payment.GetProperty("status").GetString());

        var invoices = await student.GetFromJsonAsync<JsonElement>("/api/invoices", TestApplication.Json);
        var invoice = invoices.EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == invoiceId);
        Assert.Equal(0m, invoice.GetProperty("paidAmount").GetDecimal());
    }

    // Регрессия на найденную дыру: раньше поддельное уведомление занимало ключ
    // дедупликации, и настоящее отбрасывалось как повтор — оплата не зачислялась.
    [Fact]
    public async Task ForgedNotification_DoesNotBlockTheGenuineOne()
    {
        var accountant = await TestData.AccountantAsync(_app);
        var (studentId, _, student) = await TestData.StudentAsync(_app);
        var (_, invoiceId) = await TestData.ContractWithInvoiceAsync(accountant, studentId);
        var (paymentId, gatewayId, _) = await TestData.StartPaymentAsync(student, invoiceId, 50_000m);

        await TestData.SendWebhookAsync(_app.Anonymous(), gatewayId, "succeeded", "payment.succeeded");

        await TestData.PayOnGatewayAsync(_app, gatewayId);
        await TestData.SendWebhookAsync(_app.Anonymous(), gatewayId, "succeeded", "payment.succeeded");

        var payment = await student.GetFromJsonAsync<JsonElement>($"/api/payments/{paymentId}", TestApplication.Json);
        Assert.Equal("Succeeded", payment.GetProperty("status").GetString());
    }

    [Fact]
    public async Task RepeatedNotification_IsAcceptedAndChangesNothing()
    {
        var accountant = await TestData.AccountantAsync(_app);
        var (studentId, _, student) = await TestData.StudentAsync(_app);
        var (_, invoiceId) = await TestData.ContractWithInvoiceAsync(accountant, studentId);
        var (paymentId, gatewayId, _) = await TestData.StartPaymentAsync(student, invoiceId, 20_000m);

        await TestData.PayOnGatewayAsync(_app, gatewayId);
        await TestData.SendWebhookAsync(_app.Anonymous(), gatewayId, "succeeded", "payment.succeeded");

        // Провайдер повторяет доставку, пока не получит 200. Повтор обязан быть безопасным.
        for (var i = 0; i < 3; i++)
        {
            var repeat = await TestData.SendWebhookAsync(_app.Anonymous(), gatewayId, "succeeded", "payment.succeeded");
            Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        }

        var invoices = await student.GetFromJsonAsync<JsonElement>("/api/invoices", TestApplication.Json);
        var invoice = invoices.EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == invoiceId);
        Assert.Equal(20_000m, invoice.GetProperty("paidAmount").GetDecimal());

        var payment = await student.GetFromJsonAsync<JsonElement>($"/api/payments/{paymentId}", TestApplication.Json);
        Assert.Equal("Succeeded", payment.GetProperty("status").GetString());
    }

    [Fact]
    public async Task NotificationAboutUnknownPayment_IsAcknowledgedWithoutError()
    {
        var response = await TestData.SendWebhookAsync(_app.Anonymous(), "pay_does_not_exist", "succeeded", "payment.succeeded");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestApplication.Json);
        Assert.Equal("UnknownPayment", body.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task MalformedNotification_IsRejected()
    {
        var response = await _app.Anonymous().PostAsync("/api/webhooks/gateway",
            new StringContent("{\"garbage\":true}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Student_CannotSeeAnotherStudentsPayment()
    {
        var accountant = await TestData.AccountantAsync(_app);
        var (ownerId, _, owner) = await TestData.StudentAsync(_app, "Первый Студент");
        var (_, _, stranger) = await TestData.StudentAsync(_app, "Второй Студент");
        var (_, invoiceId) = await TestData.ContractWithInvoiceAsync(accountant, ownerId);
        var (paymentId, _, _) = await TestData.StartPaymentAsync(owner, invoiceId, 5_000m);

        var response = await stranger.GetAsync($"/api/payments/{paymentId}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Student_CannotPayForAnotherStudentsInvoice()
    {
        var accountant = await TestData.AccountantAsync(_app);
        var (ownerId, _, _) = await TestData.StudentAsync(_app);
        var (_, _, stranger) = await TestData.StudentAsync(_app);
        var (_, invoiceId) = await TestData.ContractWithInvoiceAsync(accountant, ownerId);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
        {
            Content = JsonContent.Create(new { invoiceId, amount = 1_000m })
        };
        request.Headers.Add("Idempotence-Key", Guid.NewGuid().ToString());

        var response = await stranger.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/ledger/accounts")]
    [InlineData("/api/ledger/transactions")]
    [InlineData("/api/reconciliation")]
    [InlineData("/api/refunds")]
    [InlineData("/api/students")]
    public async Task Student_CannotReachAccountantEndpoints(string path)
    {
        var (_, _, student) = await TestData.StudentAsync(_app);

        var response = await student.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Accountant_CannotCreatePaymentsForStudents()
    {
        var accountant = await TestData.AccountantAsync(_app);
        var (studentId, _, _) = await TestData.StudentAsync(_app);
        var (_, invoiceId) = await TestData.ContractWithInvoiceAsync(accountant, studentId);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
        {
            Content = JsonContent.Create(new { invoiceId, amount = 1_000m })
        };
        request.Headers.Add("Idempotence-Key", Guid.NewGuid().ToString());

        var response = await accountant.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/invoices")]
    [InlineData("/api/contracts")]
    [InlineData("/api/payments")]
    public async Task Anonymous_IsNotLetIn(string path)
    {
        var response = await _app.Anonymous().GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithWrongPassword_IsRejected()
    {
        var (_, email, _) = await TestData.StudentAsync(_app);

        var response = await _app.Anonymous().PostAsJsonAsync("/api/auth/login", new { email, password = "wrong-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("185.71.76.1", "185.71.76.0/27", true)]
    [InlineData("185.71.76.33", "185.71.76.0/27", false)]
    [InlineData("77.75.156.11", "77.75.156.11", true)]
    [InlineData("77.75.156.12", "77.75.156.11", false)]
    [InlineData("77.75.154.200", "77.75.154.128/25", true)]
    [InlineData("77.75.154.100", "77.75.154.128/25", false)]
    public void IpAllowList_MatchesProviderRanges(string address, string range, bool expected)
    {
        Assert.Equal(expected, IpAllowList.Matches(System.Net.IPAddress.Parse(address), range));
    }

    [Fact]
    public void IpAllowList_WithEmptyListLetsEveryoneThrough()
    {
        Assert.True(IpAllowList.IsAllowed(System.Net.IPAddress.Parse("1.2.3.4"), Array.Empty<string>()));
        Assert.False(IpAllowList.IsAllowed(null, new[] { "185.71.76.0/27" }));
    }
}
