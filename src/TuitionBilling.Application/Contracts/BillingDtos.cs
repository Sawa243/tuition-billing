using TuitionBilling.Domain.Contracts;
using TuitionBilling.Domain.Payments;

namespace TuitionBilling.Application.Contracts;

public sealed record StudentDto(Guid Id, string FullName, string Email, string? Phone);

public sealed record CreateStudentRequest(string FullName, string Email, string? Phone, string Password);

public sealed record CreateContractRequest(
    Guid StudentId,
    string Number,
    string ProgramName,
    int AdmissionYear,
    decimal TotalAmount,
    DateOnly SignedOn);

public sealed record ContractDto(
    Guid Id,
    string Number,
    Guid StudentId,
    string StudentName,
    string ProgramName,
    int AdmissionYear,
    decimal TotalAmount,
    decimal IssuedAmount,
    decimal OutstandingAmount,
    ContractStatus Status,
    DateOnly SignedOn,
    IReadOnlyList<InvoiceDto> Invoices);

public sealed record IssueInvoiceRequest(string PeriodCode, decimal Amount, DateOnly IssuedOn, DateOnly DueOn);

public sealed record CreateInstallmentPlanRequest(int PartCount, DateOnly FirstDueOn, int IntervalDays);

public sealed record InstallmentItemDto(int Number, decimal Amount, decimal PaidAmount, DateOnly DueOn, bool IsPaid);

public sealed record InvoiceDto(
    Guid Id,
    Guid ContractId,
    string ContractNumber,
    string PeriodCode,
    decimal Amount,
    decimal PaidAmount,
    decimal RefundedAmount,
    decimal Outstanding,
    DateOnly IssuedOn,
    DateOnly DueOn,
    InvoiceStatus Status,
    bool IsOverdue,
    IReadOnlyList<InstallmentItemDto> Installments);

public sealed record CreatePaymentRequest(Guid InvoiceId, decimal Amount);

public sealed record CreatePaymentResult(Guid PaymentId, string ConfirmationUrl, PaymentStatus Status, bool WasAlreadyCreated);

public sealed record PaymentDto(
    Guid Id,
    Guid InvoiceId,
    string PeriodCode,
    string ContractNumber,
    string StudentName,
    decimal Amount,
    decimal RefundedAmount,
    PaymentStatus Status,
    string? CancellationReason,
    string? GatewayPaymentId,
    string? ConfirmationUrl,
    string? FiscalDocumentNumber,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CapturedAt);

public sealed record CreateRefundRequest(Guid PaymentId, decimal Amount, string Reason);

public sealed record RefundDto(
    Guid Id,
    Guid PaymentId,
    decimal Amount,
    string Reason,
    RefundStatus Status,
    string? GatewayRefundId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>Печатная квитанция об оплате — наш документ, а не фискальный чек.</summary>
public sealed record ReceiptDto(
    Guid PaymentId,
    string OrganizationName,
    string StudentName,
    string ContractNumber,
    string ProgramName,
    string PeriodCode,
    decimal Amount,
    DateTimeOffset PaidAt,
    string? FiscalDocumentNumber,
    string? GatewayPaymentId);
