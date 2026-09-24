using TuitionBilling.Domain.Ledger;

namespace TuitionBilling.Application.Contracts;

public sealed record LedgerAccountBalanceDto(
    Guid AccountId,
    string Code,
    string Name,
    AccountKind Kind,
    string? ContractNumber,
    decimal DebitTotal,
    decimal CreditTotal,
    decimal Balance);

public sealed record LedgerEntryDto(string AccountCode, string AccountName, EntrySide Side, decimal Amount);

public sealed record LedgerTransactionDto(
    Guid Id,
    LedgerTransactionKind Kind,
    string Reference,
    string Description,
    DateTimeOffset OccurredAt,
    decimal Total,
    IReadOnlyList<LedgerEntryDto> Entries);

public sealed record ReconciliationIssueDto(string Kind, string GatewayPaymentId, string Detail, bool Repaired);

public sealed record ReconciliationReportDto(
    Guid Id,
    DateOnly Date,
    DateTimeOffset RunAt,
    int GatewayOperations,
    int LocalPayments,
    int Matched,
    int Discrepancies,
    int AutoRepaired,
    decimal GatewayTotal,
    decimal LocalTotal,
    decimal LedgerDrift,
    IReadOnlyList<ReconciliationIssueDto> Issues);
