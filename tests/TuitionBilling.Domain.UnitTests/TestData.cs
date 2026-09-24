namespace TuitionBilling.Domain.UnitTests;

internal static class TestData
{
    public static readonly DateOnly Today = new(2026, 9, 1);

    public static Contract Contract(decimal total = 480_000m) =>
        new("ДГ-2026-0417", Guid.NewGuid(), "02.03.03 Математическое обеспечение и администрирование информационных систем", 2026, total, Today);

    public static Invoice Invoice(Contract contract, decimal amount = 120_000m, string period = "2026/2027-1") =>
        contract.IssueInvoice(period, amount, Today, Today.AddDays(30));
}
