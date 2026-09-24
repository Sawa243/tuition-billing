using PaymentGateway.Emulator.Contracts;

namespace PaymentGateway.Emulator.Infrastructure;

public static class GatewayMapper
{
    public static PaymentDto ToDto(this GatewayPayment payment, string? confirmationUrl)
    {
        var confirmation = payment.Status is GatewayPaymentStatus.Pending
            ? new ConfirmationDto("redirect", payment.ReturnUrl, confirmationUrl)
            : null;

        var cancellation = payment.CancellationReason is null
            ? null
            : new CancellationDetailsDto(payment.CancellationParty ?? "gateway", payment.CancellationReason);

        var succeeded = payment.Status == GatewayPaymentStatus.Succeeded;

        return new PaymentDto(
            payment.Id,
            payment.Status,
            AmountDto.Of(payment.Amount),
            succeeded ? AmountDto.Of(payment.Amount - payment.Fee) : null,
            succeeded,
            succeeded && payment.RefundedAmount < payment.Amount,
            payment.Description,
            confirmation,
            cancellation,
            payment.CreatedAt,
            payment.CapturedAt,
            payment.Metadata.Count == 0 ? null : payment.Metadata);
    }

    public static RefundDto ToDto(this GatewayRefund refund) =>
        new(refund.Id, refund.PaymentId, refund.Status, AmountDto.Of(refund.Amount), refund.Description, refund.CreatedAt);

    public static ReceiptDto ToDto(this GatewayReceipt receipt) =>
        new(receipt.Id, receipt.Type, "succeeded", receipt.PaymentId, receipt.Items, receipt.RegisteredAt, receipt.FiscalDocumentNumber);

    public static string ConfirmationUrlFor(HttpRequest request, string paymentId) =>
        $"{request.Scheme}://{request.Host}/checkout/{paymentId}";
}
