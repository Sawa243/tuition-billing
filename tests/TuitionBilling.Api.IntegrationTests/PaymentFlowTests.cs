using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TuitionBilling.Domain.Ledger;

namespace TuitionBilling.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class PaymentFlowTests
{
    private readonly TestApplication _app;

    public PaymentFlowTests(TestApplication app)
    {
        _app = app;
    }

    [Fact]
    public async Task Payment_FromCreationToReceipt_CreditsInvoiceAndBalancesLedger()
    {
        var accountant = await TestData.AccountantAsync(_app);
        var (studentId, _, student) = await TestData.StudentAsync(_app);
        var (contractId, invoiceId) = await TestData.ContractWithInvoiceAsync(accountant, studentId);

        var (paymentId, gatewayId, confirmationUrl) = await TestData.StartPaymentAsync(student, invoiceId, 45_000m);
        Assert.Contains("/checkout/", confirmationUrl);

        await TestData.PayOnGatewayAsync(_app, gatewayId);
        var webhook = await TestData.SendWebhookAsync(_app.Anonymous(), gatewayId, "succeeded", "payment.succeeded");
        Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);

        var payment = await student.GetFromJsonAsync<JsonElement>($"/api/payments/{paymentId}", TestApplication.Json);
        Assert.Equal("Succeeded", payment.GetProperty("status").GetString());

        var invoice = await FindInvoiceAsync(student, invoiceId);
        Assert.Equal(45_000m, invoice.GetProperty("paidAmount").GetDecimal());
        Assert.Equal(75_000m, invoice.GetProperty("outstanding").GetDecimal());
        Assert.Equal("PartiallyPaid", invoice.GetProperty("status").GetString());

        // Начисление и оплата должны дать ровно две сошедшиеся операции.
        var entries = await _app.ScopedAsync(db => db.LedgerEntries
            .Where(e => db.LedgerTransactions
                .Where(t => t.Reference == paymentId.ToString() || t.Reference == invoiceId.ToString())
                .Select(t => t.Id)
                .Contains(e.TransactionId))
            .ToListAsync());

        Assert.Equal(4, entries.Count);
        Assert.Equal(
            entries.Where(e => e.Side == EntrySide.Debit).Sum(e => e.Amount),
            entries.Where(e => e.Side == EntrySide.Credit).Sum(e => e.Amount));

        // Чек по 54-ФЗ уходит через исходящий ящик, поэтому появляется не мгновенно.
        var fiscal = await WaitForAsync(async () =>
        {
            var current = await student.GetFromJsonAsync<JsonElement>($"/api/payments/{paymentId}", TestApplication.Json);
            var number = current.GetProperty("fiscalDocumentNumber").GetString();
            return string.IsNullOrEmpty(number) ? null : number;
        });

        Assert.False(string.IsNullOrWhiteSpace(fiscal));

        var receipt = await student.GetFromJsonAsync<JsonElement>($"/api/payments/{paymentId}/receipt", TestApplication.Json);
        Assert.Equal(45_000m, receipt.GetProperty("amount").GetDecimal());
        Assert.Equal(fiscal, receipt.GetProperty("fiscalDocumentNumber").GetString());
        Assert.False(string.IsNullOrWhiteSpace(receipt.GetProperty("studentName").GetString()));
        Assert.NotEqual(Guid.Empty, contractId);
    }

    [Fact]
    public async Task CreatePayment_WithTheSameKey_ReturnsTheSamePaymentInsteadOfADuplicate()
    {
        var accountant = await TestData.AccountantAsync(_app);
        var (studentId, _, student) = await TestData.StudentAsync(_app);
        var (_, invoiceId) = await TestData.ContractWithInvoiceAsync(accountant, studentId);

        var key = Guid.NewGuid().ToString();
        var first = await TestData.StartPaymentAsync(student, invoiceId, 10_000m, key);
        var second = await TestData.StartPaymentAsync(student, invoiceId, 10_000m, key);

        Assert.Equal(first.PaymentId, second.PaymentId);
        Assert.Equal(first.GatewayId, second.GatewayId);

        var count = await _app.ScopedAsync(db => db.Payments.CountAsync(p => p.InvoiceId == invoiceId));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CreatePayment_SameKeyButDifferentAmount_IsRejected()
    {
        var accountant = await TestData.AccountantAsync(_app);
        var (studentId, _, student) = await TestData.StudentAsync(_app);
        var (_, invoiceId) = await TestData.ContractWithInvoiceAsync(accountant, studentId);

        var key = Guid.NewGuid().ToString();
        await TestData.StartPaymentAsync(student, invoiceId, 10_000m, key);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
        {
            Content = JsonContent.Create(new { invoiceId, amount = 20_000m })
        };
        request.Headers.Add("Idempotence-Key", key);

        var response = await student.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreatePayment_WithoutIdempotencyKey_IsRejected()
    {
        var accountant = await TestData.AccountantAsync(_app);
        var (studentId, _, student) = await TestData.StudentAsync(_app);
        var (_, invoiceId) = await TestData.ContractWithInvoiceAsync(accountant, studentId);

        var response = await student.PostAsJsonAsync("/api/payments", new { invoiceId, amount = 1_000m });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreatePayment_AboveOutstanding_IsRejected()
    {
        var accountant = await TestData.AccountantAsync(_app);
        var (studentId, _, student) = await TestData.StudentAsync(_app);
        var (_, invoiceId) = await TestData.ContractWithInvoiceAsync(accountant, studentId, invoiceAmount: 100_000m);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
        {
            Content = JsonContent.Create(new { invoiceId, amount = 100_000.01m })
        };
        request.Headers.Add("Idempotence-Key", Guid.NewGuid().ToString());

        var response = await student.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // Две вкладки, два платежа на всю сумму: если не учитывать уже созданные,
    // но ещё не оплаченные платежи, студент заплатит вдвое.
    [Fact]
    public async Task CreatePayment_WhenAnotherPaymentAlreadyHoldsTheAmount_IsRejected()
    {
        var accountant = await TestData.AccountantAsync(_app);
        var (studentId, _, student) = await TestData.StudentAsync(_app);
        var (_, invoiceId) = await TestData.ContractWithInvoiceAsync(accountant, studentId, invoiceAmount: 100_000m);

        await TestData.StartPaymentAsync(student, invoiceId, 100_000m);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
        {
            Content = JsonContent.Create(new { invoiceId, amount = 100_000m })
        };
        request.Headers.Add("Idempotence-Key", Guid.NewGuid().ToString());

        var response = await student.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task DeclinedCard_LeavesInvoiceUnpaid()
    {
        var accountant = await TestData.AccountantAsync(_app);
        var (studentId, _, student) = await TestData.StudentAsync(_app);
        var (_, invoiceId) = await TestData.ContractWithInvoiceAsync(accountant, studentId);

        var (paymentId, gatewayId, _) = await TestData.StartPaymentAsync(student, invoiceId, 30_000m);
        await TestData.PayOnGatewayAsync(_app, gatewayId, success: false);
        await TestData.SendWebhookAsync(_app.Anonymous(), gatewayId, "canceled", "payment.canceled");

        var payment = await student.GetFromJsonAsync<JsonElement>($"/api/payments/{paymentId}", TestApplication.Json);
        Assert.Equal("Canceled", payment.GetProperty("status").GetString());
        Assert.Equal("insufficient_funds", payment.GetProperty("cancellationReason").GetString());

        var invoice = await FindInvoiceAsync(student, invoiceId);
        Assert.Equal(0m, invoice.GetProperty("paidAmount").GetDecimal());
    }

    [Fact]
    public async Task Installments_SplitInvoiceAndAbsorbPaymentInOrder()
    {
        var accountant = await TestData.AccountantAsync(_app);
        var (studentId, _, student) = await TestData.StudentAsync(_app);
        var (_, invoiceId) = await TestData.ContractWithInvoiceAsync(accountant, studentId, invoiceAmount: 100_000m);

        var split = await accountant.PostAsJsonAsync($"/api/invoices/{invoiceId}/installments", new
        {
            partCount = 3,
            firstDueOn = "2026-09-10",
            intervalDays = 30
        });

        split.EnsureSuccessStatusCode();
        var plan = await split.Content.ReadFromJsonAsync<JsonElement>(TestApplication.Json);
        var parts = plan.GetProperty("installments").EnumerateArray().Select(p => p.GetProperty("amount").GetDecimal()).ToList();

        Assert.Equal(3, parts.Count);
        Assert.Equal(100_000m, parts.Sum());
        Assert.Equal(33_333.34m, parts[^1]);

        var (_, gatewayId, _) = await TestData.StartPaymentAsync(student, invoiceId, 40_000m);
        await TestData.PayOnGatewayAsync(_app, gatewayId);
        await TestData.SendWebhookAsync(_app.Anonymous(), gatewayId, "succeeded", "payment.succeeded");

        var invoice = await FindInvoiceAsync(student, invoiceId);
        var items = invoice.GetProperty("installments").EnumerateArray().ToList();

        Assert.True(items[0].GetProperty("isPaid").GetBoolean());
        Assert.Equal(6_666.67m, items[1].GetProperty("paidAmount").GetDecimal());
        Assert.Equal(0m, items[2].GetProperty("paidAmount").GetDecimal());
    }

    private static async Task<JsonElement> FindInvoiceAsync(HttpClient client, Guid invoiceId)
    {
        var invoices = await client.GetFromJsonAsync<JsonElement>("/api/invoices", TestApplication.Json);
        return invoices.EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == invoiceId);
    }

    private static async Task<string?> WaitForAsync(Func<Task<string?>> probe, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var value = await probe();
            if (value is not null)
            {
                return value;
            }

            await Task.Delay(500);
        }

        return null;
    }
}
