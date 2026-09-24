namespace PaymentGateway.Emulator.Contracts;

/// <summary>
/// Чек по 54-ФЗ. Кассу применяет провайдер, но состав услуги присылает магазин:
/// одной суммой чек пробить нельзя, нужна расшифровка по позициям.
/// </summary>
public sealed record ReceiptItemDto(
    string Description,
    string Quantity,
    AmountDto Amount,
    int VatCode,
    string PaymentSubject,
    string PaymentMode);

public sealed record CustomerDto(string? Email, string? Phone, string? FullName);

public sealed record CreateReceiptRequest(
    string Type,
    string PaymentId,
    bool Send,
    CustomerDto Customer,
    IReadOnlyList<ReceiptItemDto> Items);

public sealed record ReceiptDto(
    string Id,
    string Type,
    string Status,
    string PaymentId,
    IReadOnlyList<ReceiptItemDto> Items,
    DateTimeOffset RegisteredAt,
    string FiscalDocumentNumber);
