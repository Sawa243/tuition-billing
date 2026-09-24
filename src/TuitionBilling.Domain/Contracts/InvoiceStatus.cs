namespace TuitionBilling.Domain.Contracts;

public enum InvoiceStatus
{
    Issued = 1,
    PartiallyPaid = 2,
    Paid = 3,
    Canceled = 4
}
