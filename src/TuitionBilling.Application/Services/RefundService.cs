using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TuitionBilling.Application.Abstractions;
using TuitionBilling.Application.Common;
using TuitionBilling.Application.Contracts;
using TuitionBilling.Domain.Payments;

namespace TuitionBilling.Application.Services;

/// <summary>
/// Возвраты оформляет бухгалтер: отчисление, перевод на другую программу,
/// ошибочный платёж. Вернуть можно только с успешного платежа и только то,
/// что ещё не возвращено.
/// </summary>
public sealed class RefundService
{
    private readonly IBillingDbContext _db;
    private readonly IPaymentGatewayClient _gateway;
    private readonly LedgerService _ledger;
    private readonly ILogger<RefundService> _logger;

    public RefundService(
        IBillingDbContext db,
        IPaymentGatewayClient gateway,
        LedgerService ledger,
        ILogger<RefundService> logger)
    {
        _db = db;
        _gateway = gateway;
        _ledger = ledger;
        _logger = logger;
    }

    public async Task<RefundDto> CreateAsync(CreateRefundRequest request, CancellationToken cancellationToken)
    {
        var payment = await _db.Payments.FirstOrDefaultAsync(p => p.Id == request.PaymentId, cancellationToken)
                      ?? throw new NotFoundException("Платёж", request.PaymentId);

        if (payment.GatewayPaymentId is null)
        {
            throw new DomainConflictException("Платёж не дошёл до шлюза, возвращать нечего.");
        }

        var idempotencyKey = Guid.NewGuid().ToString();
        var refund = payment.StartRefund(request.Amount, request.Reason, idempotencyKey);
        _db.Refunds.Add(refund);
        await _db.SaveChangesAsync(cancellationToken);

        GatewayRefundView view;
        try
        {
            view = await _gateway.CreateRefundAsync(
                payment.GatewayPaymentId, refund.Amount, refund.Reason, idempotencyKey, cancellationToken);
        }
        catch (Exception ex)
        {
            // Возврат остаётся в статусе Pending: бухгалтер увидит его в списке,
            // а повторная попытка пойдёт с тем же ключом идемпотентности.
            _logger.LogError(ex, "Шлюз не принял возврат {RefundId}", refund.Id);
            throw new GatewayException("Шлюз не принял возврат, попробуйте позже.", ex);
        }

        var invoice = await _db.Invoices
                          .Include(i => i.InstallmentPlan)
                          .ThenInclude(p => p!.Items)
                          .FirstOrDefaultAsync(i => i.Id == payment.InvoiceId, cancellationToken)
                      ?? throw new NotFoundException("Начисление", payment.InvoiceId);

        var contract = await _db.Contracts.FirstAsync(c => c.Id == payment.ContractId, cancellationToken);

        refund.Complete(view.Id, DateTimeOffset.UtcNow);
        payment.RegisterRefund(refund.Amount);
        invoice.RegisterRefund(refund.Amount);
        await _ledger.PostRefundAsync(refund, payment, contract.Number, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        return ToDto(refund);
    }

    public async Task<IReadOnlyList<RefundDto>> ListAsync(Guid? paymentId, CancellationToken cancellationToken)
    {
        var query = _db.Refunds.AsNoTracking();
        if (paymentId is { } id)
        {
            query = query.Where(r => r.PaymentId == id);
        }

        var refunds = await query.OrderByDescending(r => r.CreatedAt).ToListAsync(cancellationToken);
        return refunds.Select(ToDto).ToList();
    }

    private static RefundDto ToDto(Refund refund) =>
        new(refund.Id, refund.PaymentId, refund.Amount, refund.Reason, refund.Status,
            refund.GatewayRefundId, refund.CreatedAt, refund.CompletedAt);
}
