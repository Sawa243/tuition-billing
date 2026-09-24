using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TuitionBilling.Application.Abstractions;
using TuitionBilling.Application.Common;
using TuitionBilling.Application.Contracts;
using TuitionBilling.Application.Persistence;
using TuitionBilling.Domain.Contracts;
using TuitionBilling.Domain.Payments;

namespace TuitionBilling.Application.Services;

public sealed class PaymentService
{
    private readonly IBillingDbContext _db;
    private readonly IPaymentGatewayClient _gateway;
    private readonly IUniqueConstraintDetector _uniqueViolations;
    private readonly PaymentSynchronizer _synchronizer;
    private readonly BillingOptions _options;
    private readonly ILogger<PaymentService> _logger;

    public PaymentService(
        IBillingDbContext db,
        IPaymentGatewayClient gateway,
        IUniqueConstraintDetector uniqueViolations,
        PaymentSynchronizer synchronizer,
        IOptions<BillingOptions> options,
        ILogger<PaymentService> logger)
    {
        _db = db;
        _gateway = gateway;
        _uniqueViolations = uniqueViolations;
        _synchronizer = synchronizer;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Создаёт платёж и отправляет плательщика на страницу шлюза.
    /// Идемпотентность держится на уникальном индексе по ключу: проверка
    /// «а нет ли уже такого ключа» гонку не закрывает — два запроса пройдут её разом.
    /// </summary>
    public async Task<CreatePaymentResult> CreateAsync(
        Guid studentId,
        CreatePaymentRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var requestHash = Hash(studentId, request);

        var existingKey = await _db.PaymentIdempotencyKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Key == idempotencyKey, cancellationToken);
        if (existingKey is not null)
        {
            return await ReplayAsync(existingKey, requestHash, idempotencyKey, cancellationToken);
        }

        var (invoice, contract) = await LoadPayableAsync(studentId, request, cancellationToken);

        var description = $"Оплата обучения по договору {contract.Number}, {invoice.PeriodCode}";
        var payment = new Payment(invoice.Id, contract.Id, studentId, request.Amount, idempotencyKey, description);

        _db.Payments.Add(payment);
        _db.PaymentIdempotencyKeys.Add(new PaymentIdempotencyKey
        {
            Key = idempotencyKey,
            RequestHash = requestHash,
            PaymentId = payment.Id
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (_uniqueViolations.IsUniqueViolation(ex))
        {
            // Второй одновременный запрос с тем же ключом: уникальный индекс не пустил.
            _logger.LogInformation("Повторное создание платежа по ключу {Key} отсечено индексом", idempotencyKey);

            // Контекст остался с неприменёнными вставками — снимаем их с учёта,
            // иначе следующий SaveChanges повторит тот же провалившийся запрос.
            foreach (var entry in ex.Entries)
            {
                entry.State = EntityState.Detached;
            }

            var winner = await _db.PaymentIdempotencyKeys.AsNoTracking()
                .FirstOrDefaultAsync(k => k.Key == idempotencyKey, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return await ReplayAsync(winner, requestHash, idempotencyKey, cancellationToken);
        }

        return await SendToGatewayAsync(payment, contract, invoice, idempotencyKey, false, cancellationToken);
    }

    public async Task<PaymentDto> GetAsync(Guid paymentId, Guid? studentId, CancellationToken cancellationToken)
    {
        var payment = await _db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken)
                      ?? throw new NotFoundException("Платёж", paymentId);

        if (studentId is { } id && payment.StudentId != id)
        {
            throw new AccessDeniedException("Платёж относится к другому обучающемуся.");
        }

        return (await BuildAsync(new List<Payment> { payment }, cancellationToken)).Single();
    }

    public async Task<IReadOnlyList<PaymentDto>> ListAsync(Guid? studentId, int take, CancellationToken cancellationToken)
    {
        var query = _db.Payments.AsNoTracking();
        if (studentId is { } id)
        {
            query = query.Where(p => p.StudentId == id);
        }

        var payments = await query.OrderByDescending(p => p.CreatedAt).Take(take).ToListAsync(cancellationToken);
        return await BuildAsync(payments, cancellationToken);
    }

    public async Task<ReceiptDto> GetReceiptAsync(Guid paymentId, Guid? studentId, CancellationToken cancellationToken)
    {
        var payment = await _db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken)
                      ?? throw new NotFoundException("Платёж", paymentId);

        if (studentId is { } id && payment.StudentId != id)
        {
            throw new AccessDeniedException("Платёж относится к другому обучающемуся.");
        }

        if (payment.Status != PaymentStatus.Succeeded)
        {
            throw new DomainConflictException("Квитанция выдаётся только по успешному платежу.");
        }

        var contract = await _db.Contracts.AsNoTracking().FirstAsync(c => c.Id == payment.ContractId, cancellationToken);
        var invoice = await _db.Invoices.AsNoTracking().FirstAsync(i => i.Id == payment.InvoiceId, cancellationToken);
        var student = await _db.Students.AsNoTracking().FirstAsync(s => s.Id == payment.StudentId, cancellationToken);

        return new ReceiptDto(
            payment.Id,
            _options.OrganizationName,
            student.FullName,
            contract.Number,
            contract.ProgramName,
            invoice.PeriodCode,
            payment.Amount,
            payment.CapturedAt ?? payment.UpdatedAt,
            payment.FiscalDocumentNumber,
            payment.GatewayPaymentId);
    }

    /// <summary>
    /// Плательщик вернулся со страницы шлюза. Состоянию в адресе строки запроса
    /// верить нельзя, поэтому статус перезапрашивается у шлюза.
    /// </summary>
    public async Task<PaymentDto?> HandleReturnAsync(string gatewayPaymentId, CancellationToken cancellationToken)
    {
        var payment = await _db.Payments.FirstOrDefaultAsync(p => p.GatewayPaymentId == gatewayPaymentId, cancellationToken);
        if (payment is null)
        {
            return null;
        }

        await _synchronizer.SyncAsync(payment, cancellationToken);
        return (await BuildAsync(new List<Payment> { payment }, cancellationToken)).Single();
    }

    private async Task<CreatePaymentResult> ReplayAsync(
        PaymentIdempotencyKey key,
        string requestHash,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (key.RequestHash != requestHash)
        {
            throw new IdempotencyConflictException(idempotencyKey);
        }

        var payment = await _db.Payments.FirstOrDefaultAsync(p => p.Id == key.PaymentId, cancellationToken)
                      ?? throw new NotFoundException("Платёж", key.PaymentId);

        if (payment.ConfirmationUrl is not null)
        {
            return new CreatePaymentResult(payment.Id, payment.ConfirmationUrl, payment.Status, true);
        }

        // Платёж создан, но до шлюза в прошлый раз не доехал — доводим до конца
        // тем же ключом: шлюз тоже идемпотентен и вернёт ту же операцию.
        var invoice = await _db.Invoices.FirstAsync(i => i.Id == payment.InvoiceId, cancellationToken);
        var contract = await _db.Contracts.FirstAsync(c => c.Id == payment.ContractId, cancellationToken);
        return await SendToGatewayAsync(payment, contract, invoice, idempotencyKey, true, cancellationToken);
    }

    private async Task<CreatePaymentResult> SendToGatewayAsync(
        Payment payment,
        Contract contract,
        Invoice invoice,
        string idempotencyKey,
        bool wasAlreadyCreated,
        CancellationToken cancellationToken)
    {
        var metadata = new Dictionary<string, string>
        {
            ["payment_id"] = payment.Id.ToString(),
            ["invoice_id"] = invoice.Id.ToString(),
            ["contract"] = contract.Number
        };

        var view = await _gateway.CreatePaymentAsync(
            payment.Amount,
            payment.Description,
            $"{_options.PublicUrl.TrimEnd('/')}/api/payments/return",
            metadata,
            idempotencyKey,
            cancellationToken);

        if (payment.GatewayPaymentId is null)
        {
            payment.AttachToGateway(view.Id, view.ConfirmationUrl ?? string.Empty);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return new CreatePaymentResult(payment.Id, payment.ConfirmationUrl ?? string.Empty, payment.Status, wasAlreadyCreated);
    }

    private async Task<(Invoice Invoice, Contract Contract)> LoadPayableAsync(
        Guid studentId,
        CreatePaymentRequest request,
        CancellationToken cancellationToken)
    {
        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == request.InvoiceId, cancellationToken)
                      ?? throw new NotFoundException("Начисление", request.InvoiceId);

        var contract = await _db.Contracts.FirstOrDefaultAsync(c => c.Id == invoice.ContractId, cancellationToken)
                       ?? throw new NotFoundException("Договор", invoice.ContractId);

        if (contract.StudentId != studentId)
        {
            throw new AccessDeniedException("Начисление выставлено по чужому договору.");
        }

        if (contract.Status != ContractStatus.Active)
        {
            throw new DomainConflictException($"Договор {contract.Number} в статусе {contract.Status}, оплата недоступна.");
        }

        if (invoice.Status == InvoiceStatus.Canceled)
        {
            throw new DomainConflictException("Начисление отменено.");
        }

        // Зарезервированные суммы ещё не оплаченных платежей тоже занимают остаток,
        // иначе студент двумя вкладками создаст два платежа на всю сумму.
        var pending = await _db.Payments
            .Where(p => p.InvoiceId == invoice.Id && p.Status != PaymentStatus.Canceled && p.Status != PaymentStatus.Succeeded)
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;

        var available = invoice.Outstanding - pending;
        if (request.Amount > available)
        {
            throw new DomainConflictException(
                $"К оплате доступно {available.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))} ₽, запрошено {request.Amount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))} ₽.");
        }

        return (invoice, contract);
    }

    private async Task<IReadOnlyList<PaymentDto>> BuildAsync(List<Payment> payments, CancellationToken cancellationToken)
    {
        if (payments.Count == 0)
        {
            return Array.Empty<PaymentDto>();
        }

        var invoiceIds = payments.Select(p => p.InvoiceId).Distinct().ToList();
        var contractIds = payments.Select(p => p.ContractId).Distinct().ToList();
        var studentIds = payments.Select(p => p.StudentId).Distinct().ToList();

        var invoices = await _db.Invoices.AsNoTracking().Where(i => invoiceIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.PeriodCode, cancellationToken);
        var contracts = await _db.Contracts.AsNoTracking().Where(c => contractIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Number, cancellationToken);
        var students = await _db.Students.AsNoTracking().Where(s => studentIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.FullName, cancellationToken);

        return payments.Select(p => new PaymentDto(
                p.Id,
                p.InvoiceId,
                invoices.GetValueOrDefault(p.InvoiceId, "—"),
                contracts.GetValueOrDefault(p.ContractId, "—"),
                students.GetValueOrDefault(p.StudentId, "—"),
                p.Amount,
                p.RefundedAmount,
                p.Status,
                p.CancellationReason,
                p.GatewayPaymentId,
                p.ConfirmationUrl,
                p.FiscalDocumentNumber,
                p.CreatedAt,
                p.CapturedAt))
            .ToList();
    }

    private static string Hash(Guid studentId, CreatePaymentRequest request)
    {
        var raw = $"{studentId:N}|{request.InvoiceId:N}|{request.Amount.ToString("F2", CultureInfo.InvariantCulture)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}
