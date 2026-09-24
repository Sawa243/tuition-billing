using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TuitionBilling.Application.Abstractions;
using TuitionBilling.Application.Common;
using TuitionBilling.Application.Persistence;
using TuitionBilling.Application.Services;

namespace TuitionBilling.Infrastructure.BackgroundJobs;

/// <summary>
/// Разбор исходящего ящика.
///
/// Зачем он вообще нужен: нельзя одной транзакцией и записать платёж в базу,
/// и сходить по HTTP в кассу провайдера. Упадём между этими шагами — либо
/// деньги зачтены без чека, либо чек пробит по несуществующему платежу.
/// Поэтому в транзакции пишется только строка задания, а доставку берёт на себя
/// эта служба. Гарантия получается «хотя бы один раз», и поэтому запрос уходит
/// с ключом идемпотентности — повтор не пробьёт второй чек.
/// </summary>
public sealed class OutboxDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BillingOptions _options;
    private readonly ILogger<OutboxDispatcher> _logger;

    public OutboxDispatcher(
        IServiceScopeFactory scopeFactory,
        IOptions<BillingOptions> options,
        ILogger<OutboxDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.OutboxPollSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Разбор исходящего ящика сорвался");
            }
        }
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IBillingDbContext>();
        var gateway = scope.ServiceProvider.GetRequiredService<IPaymentGatewayClient>();

        var now = DateTimeOffset.UtcNow;
        var messages = await db.OutboxMessages
            .Where(m => m.ProcessedAt == null && !m.IsDead && (m.NextAttemptAt == null || m.NextAttemptAt <= now))
            .OrderBy(m => m.CreatedAt)
            .Take(20)
            .ToListAsync(cancellationToken);

        foreach (var message in messages)
        {
            try
            {
                await HandleAsync(db, gateway, message, cancellationToken);
                message.ProcessedAt = DateTimeOffset.UtcNow;
                message.LastError = null;
            }
            catch (Exception ex)
            {
                message.Attempts++;
                message.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;

                if (message.Attempts >= _options.OutboxMaxAttempts)
                {
                    message.IsDead = true;
                    _logger.LogError(ex, "Сообщение {MessageId} признано недоставляемым после {Attempts} попыток",
                        message.Id, message.Attempts);
                }
                else
                {
                    // Выдержка растёт с каждой попыткой: шлюз мог просто прилечь.
                    message.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Pow(2, message.Attempts) * 5);
                    _logger.LogWarning(ex, "Попытка {Attempt} по сообщению {MessageId} не удалась",
                        message.Attempts, message.Id);
                }
            }
        }

        if (messages.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task HandleAsync(
        IBillingDbContext db,
        IPaymentGatewayClient gateway,
        OutboxMessage message,
        CancellationToken cancellationToken)
    {
        switch (message.Kind)
        {
            case OutboxKinds.RegisterReceipt:
                await RegisterReceiptAsync(db, gateway, message, _options, cancellationToken);
                return;
            default:
                throw new InvalidOperationException($"Неизвестный вид сообщения: {message.Kind}");
        }
    }

    private static async Task RegisterReceiptAsync(
        IBillingDbContext db,
        IPaymentGatewayClient gateway,
        OutboxMessage message,
        BillingOptions options,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<ReceiptOutboxPayload>(message.Payload)
                      ?? throw new InvalidOperationException("Пустая нагрузка сообщения.");

        var payment = await db.Payments.FirstOrDefaultAsync(p => p.Id == payload.PaymentId, cancellationToken)
                      ?? throw new InvalidOperationException($"Платёж {payload.PaymentId} не найден.");

        if (payment.FiscalDocumentNumber is not null || payment.GatewayPaymentId is null)
        {
            return;
        }

        var invoice = await db.Invoices.AsNoTracking().FirstAsync(i => i.Id == payment.InvoiceId, cancellationToken);
        var contract = await db.Contracts.AsNoTracking().FirstAsync(c => c.Id == payment.ContractId, cancellationToken);
        var student = await db.Students.AsNoTracking().FirstAsync(s => s.Id == payment.StudentId, cancellationToken);

        var request = new GatewayReceiptRequest(
            payment.GatewayPaymentId,
            student.Email,
            new[]
            {
                // Ставка по умолчанию — «без НДС»: образовательные услуги освобождены
                // подпунктом 14 пункта 2 статьи 149 Налогового кодекса. Вынесена
                // в настройку, потому что у платных курсов вне лицензии ставка другая.
                new GatewayReceiptItem(
                    $"Образовательные услуги по договору {contract.Number}, период {invoice.PeriodCode}",
                    payment.Amount,
                    options.ReceiptVatCode)
            });

        // Ключ идемпотентности — идентификатор самого сообщения: сколько бы раз
        // служба ни повторила доставку, чек у провайдера останется один.
        var receipt = await gateway.CreateReceiptAsync(request, message.Id.ToString(), cancellationToken);
        payment.AttachReceipt(receipt.Id, receipt.FiscalDocumentNumber, DateTimeOffset.UtcNow);
    }
}
