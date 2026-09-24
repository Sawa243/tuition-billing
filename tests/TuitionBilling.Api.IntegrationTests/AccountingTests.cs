using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TuitionBilling.Domain.Ledger;

namespace TuitionBilling.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class AccountingTests
{
    private readonly TestApplication _app;

    public AccountingTests(TestApplication app)
    {
        _app = app;
    }

    [Fact]
    public async Task Refund_ReturnsMoneyAndPostsMirroredEntries()
    {
        var accountant = await TestData.AccountantAsync(_app);
        var (studentId, _, student) = await TestData.StudentAsync(_app);
        var (_, invoiceId) = await TestData.ContractWithInvoiceAsync(accountant, studentId);

        var (paymentId, gatewayId, _) = await TestData.StartPaymentAsync(student, invoiceId, 60_000m);
        await TestData.PayOnGatewayAsync(_app, gatewayId);
        await TestData.SendWebhookAsync(_app.Anonymous(), gatewayId, "succeeded", "payment.succeeded");

        var response = await accountant.PostAsJsonAsync("/api/refunds", new
        {
            paymentId,
            amount = 20_000m,
            reason = "Отчисление по собственному желанию"
        });

        response.EnsureSuccessStatusCode();
        var refund = await response.Content.ReadFromJsonAsync<JsonElement>(TestApplication.Json);
        Assert.Equal("Succeeded", refund.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(refund.GetProperty("gatewayRefundId").GetString()));

        var invoices = await student.GetFromJsonAsync<JsonElement>("/api/invoices", TestApplication.Json);
        var invoice = invoices.EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == invoiceId);
        Assert.Equal(40_000m, invoice.GetProperty("paidAmount").GetDecimal());
        Assert.Equal(20_000m, invoice.GetProperty("refundedAmount").GetDecimal());
        Assert.Equal(80_000m, invoice.GetProperty("outstanding").GetDecimal());

        var refundId = refund.GetProperty("id").GetGuid();
        var entries = await LoadEntriesAsync(refundId.ToString());
        Assert.Equal(2, entries.Count);
        Assert.Equal(
            entries.Where(e => e.Side == EntrySide.Debit).Sum(e => e.Amount),
            entries.Where(e => e.Side == EntrySide.Credit).Sum(e => e.Amount));
    }

    [Fact]
    public async Task Refund_AboveRemainingAmount_IsRejected()
    {
        var accountant = await TestData.AccountantAsync(_app);
        var (studentId, _, student) = await TestData.StudentAsync(_app);
        var (_, invoiceId) = await TestData.ContractWithInvoiceAsync(accountant, studentId);

        var (paymentId, gatewayId, _) = await TestData.StartPaymentAsync(student, invoiceId, 10_000m);
        await TestData.PayOnGatewayAsync(_app, gatewayId);
        await TestData.SendWebhookAsync(_app.Anonymous(), gatewayId, "succeeded", "payment.succeeded");

        var response = await accountant.PostAsJsonAsync("/api/refunds", new
        {
            paymentId,
            amount = 10_000.01m,
            reason = "Слишком много"
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Refund_OnUnpaidPayment_IsRejected()
    {
        var accountant = await TestData.AccountantAsync(_app);
        var (studentId, _, student) = await TestData.StudentAsync(_app);
        var (_, invoiceId) = await TestData.ContractWithInvoiceAsync(accountant, studentId);
        var (paymentId, _, _) = await TestData.StartPaymentAsync(student, invoiceId, 10_000m);

        var response = await accountant.PostAsJsonAsync("/api/refunds", new
        {
            paymentId,
            amount = 1_000m,
            reason = "Платёж ещё не прошёл"
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CancelingAnInvoice_PostsAReversalInsteadOfDeletingEntries()
    {
        var accountant = await TestData.AccountantAsync(_app);
        var (studentId, _, _) = await TestData.StudentAsync(_app);
        var (_, invoiceId) = await TestData.ContractWithInvoiceAsync(accountant, studentId, invoiceAmount: 70_000m);

        var response = await accountant.PostAsJsonAsync($"/api/invoices/{invoiceId}/cancel", new { reason = "Ошибка бухгалтера" });
        response.EnsureSuccessStatusCode();

        var invoice = await response.Content.ReadFromJsonAsync<JsonElement>(TestApplication.Json);
        Assert.Equal("Canceled", invoice.GetProperty("status").GetString());

        var issued = await LoadEntriesAsync(invoiceId.ToString());
        Assert.Equal(2, issued.Count);

        var transactionId = await _app.ScopedAsync(db => db.LedgerTransactions
            .Where(t => t.Reference == invoiceId.ToString() && t.Kind == LedgerTransactionKind.InvoiceIssued)
            .Select(t => t.Id)
            .FirstAsync());

        var reversal = await LoadEntriesAsync(transactionId.ToString());
        Assert.Equal(2, reversal.Count);

        // Исходная операция и сторно вместе дают ноль — история осталась полной.
        Assert.Equal(0m, issued.Concat(reversal).Sum(e => e.Signed));
    }

    [Fact]
    public async Task Reconciliation_MatchesGatewayRegistryAndFindsNoDrift()
    {
        var accountant = await TestData.AccountantAsync(_app);
        var (studentId, _, student) = await TestData.StudentAsync(_app);
        var (_, invoiceId) = await TestData.ContractWithInvoiceAsync(accountant, studentId);

        var (_, gatewayId, _) = await TestData.StartPaymentAsync(student, invoiceId, 25_000m);
        await TestData.PayOnGatewayAsync(_app, gatewayId);
        await TestData.SendWebhookAsync(_app.Anonymous(), gatewayId, "succeeded", "payment.succeeded");

        var response = await accountant.PostAsync("/api/reconciliation/run", null);
        response.EnsureSuccessStatusCode();

        var report = await response.Content.ReadFromJsonAsync<JsonElement>(TestApplication.Json);
        Assert.Equal(0m, report.GetProperty("ledgerDrift").GetDecimal());
        Assert.True(report.GetProperty("matched").GetInt32() >= 1);
    }

    // Уведомление может не дойти вовсе. Тогда расхождение находит суточная сверка
    // и чинит его обратным запросом статуса — тем же безопасным путём.
    [Fact]
    public async Task Reconciliation_RepairsAPaymentWhoseNotificationNeverArrived()
    {
        var accountant = await TestData.AccountantAsync(_app);
        var (studentId, _, student) = await TestData.StudentAsync(_app);
        var (_, invoiceId) = await TestData.ContractWithInvoiceAsync(accountant, studentId);

        var (paymentId, gatewayId, _) = await TestData.StartPaymentAsync(student, invoiceId, 35_000m);
        await TestData.PayOnGatewayAsync(_app, gatewayId);
        // Уведомление намеренно не отправляем — имитируем потерю.

        var before = await student.GetFromJsonAsync<JsonElement>($"/api/payments/{paymentId}", TestApplication.Json);
        Assert.Equal("Pending", before.GetProperty("status").GetString());

        var response = await accountant.PostAsync("/api/reconciliation/run", null);
        response.EnsureSuccessStatusCode();

        var report = await response.Content.ReadFromJsonAsync<JsonElement>(TestApplication.Json);
        Assert.True(report.GetProperty("autoRepaired").GetInt32() >= 1);

        var after = await student.GetFromJsonAsync<JsonElement>($"/api/payments/{paymentId}", TestApplication.Json);
        Assert.Equal("Succeeded", after.GetProperty("status").GetString());
    }

    [Fact]
    public async Task LedgerAccounts_KeepDebitEqualToCreditAcrossTheWholeBook()
    {
        var accountant = await TestData.AccountantAsync(_app);
        var (studentId, _, student) = await TestData.StudentAsync(_app);
        var (_, invoiceId) = await TestData.ContractWithInvoiceAsync(accountant, studentId);

        var (_, gatewayId, _) = await TestData.StartPaymentAsync(student, invoiceId, 15_000m);
        await TestData.PayOnGatewayAsync(_app, gatewayId);
        await TestData.SendWebhookAsync(_app.Anonymous(), gatewayId, "succeeded", "payment.succeeded");

        var accounts = await accountant.GetFromJsonAsync<JsonElement>("/api/ledger/accounts", TestApplication.Json);
        var debit = accounts.EnumerateArray().Sum(a => a.GetProperty("debitTotal").GetDecimal());
        var credit = accounts.EnumerateArray().Sum(a => a.GetProperty("creditTotal").GetDecimal());

        Assert.Equal(debit, credit);
    }

    [Fact]
    public async Task IssuingTwoInvoicesForTheSamePeriod_IsRejected()
    {
        var accountant = await TestData.AccountantAsync(_app);
        var (studentId, _, _) = await TestData.StudentAsync(_app);
        var (contractId, _) = await TestData.ContractWithInvoiceAsync(accountant, studentId);

        var response = await accountant.PostAsJsonAsync($"/api/contracts/{contractId}/invoices", new
        {
            periodCode = "2026/2027-1",
            amount = 50_000m,
            issuedOn = "2026-08-25",
            dueOn = "2026-09-25"
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task IssuingAnInvoiceWithABrokenPeriodCode_IsRejected()
    {
        var accountant = await TestData.AccountantAsync(_app);
        var (studentId, _, _) = await TestData.StudentAsync(_app);
        var (contractId, _) = await TestData.ContractWithInvoiceAsync(accountant, studentId);

        var response = await accountant.PostAsJsonAsync($"/api/contracts/{contractId}/invoices", new
        {
            periodCode = "осенний семестр",
            amount = 50_000m,
            issuedOn = "2026-08-25",
            dueOn = "2026-09-25"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private Task<List<LedgerEntry>> LoadEntriesAsync(string reference) =>
        _app.ScopedAsync(db => db.LedgerEntries
            .Where(e => db.LedgerTransactions.Where(t => t.Reference == reference).Select(t => t.Id).Contains(e.TransactionId))
            .ToListAsync());
}
