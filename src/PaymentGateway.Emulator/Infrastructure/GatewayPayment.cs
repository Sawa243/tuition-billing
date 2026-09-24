namespace PaymentGateway.Emulator.Infrastructure;

public sealed class GatewayPayment
{
    public required string Id { get; init; }

    public required decimal Amount { get; init; }

    public required decimal Fee { get; init; }

    /// <summary>false — двухстадийная схема: сначала холд, потом подтверждение списания.</summary>
    public required bool AutoCapture { get; init; }

    public string? Description { get; init; }

    public string? ReturnUrl { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new();

    public required DateTimeOffset CreatedAt { get; init; }

    public string Status { get; set; } = Contracts.GatewayPaymentStatus.Pending;

    public DateTimeOffset? CapturedAt { get; set; }

    public decimal RefundedAmount { get; set; }

    public string? CancellationParty { get; set; }

    public string? CancellationReason { get; set; }
}

public sealed class GatewayRefund
{
    public required string Id { get; init; }

    public required string PaymentId { get; init; }

    public required decimal Amount { get; init; }

    public string? Description { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public string Status { get; set; } = "succeeded";
}

public sealed class GatewayReceipt
{
    public required string Id { get; init; }

    public required string PaymentId { get; init; }

    public required string Type { get; init; }

    public required IReadOnlyList<Contracts.ReceiptItemDto> Items { get; init; }

    public required DateTimeOffset RegisteredAt { get; init; }

    public required string FiscalDocumentNumber { get; init; }
}
