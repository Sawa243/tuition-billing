namespace TuitionBilling.Domain.Ledger;

public enum LedgerTransactionKind
{
    InvoiceIssued = 1,
    PaymentSucceeded = 2,
    RefundSucceeded = 3,
    GatewaySettlement = 4,
    Reversal = 5
}
