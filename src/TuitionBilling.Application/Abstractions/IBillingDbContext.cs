using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using TuitionBilling.Application.Persistence;
using TuitionBilling.Domain.Contracts;
using TuitionBilling.Domain.Ledger;
using TuitionBilling.Domain.Payments;
using TuitionBilling.Domain.Students;

namespace TuitionBilling.Application.Abstractions;

/// <summary>
/// Прикладной слой видит базу через этот интерфейс, а не через конкретный
/// DbContext: сценарии остаются в Application, а EF Core со всей его
/// конфигурацией — в Infrastructure.
/// </summary>
public interface IBillingDbContext
{
    DbSet<Student> Students { get; }

    DbSet<Contract> Contracts { get; }

    DbSet<Invoice> Invoices { get; }

    DbSet<InstallmentPlan> InstallmentPlans { get; }

    DbSet<InstallmentItem> InstallmentItems { get; }

    DbSet<Payment> Payments { get; }

    DbSet<Refund> Refunds { get; }

    DbSet<LedgerAccount> LedgerAccounts { get; }

    DbSet<LedgerTransaction> LedgerTransactions { get; }

    DbSet<LedgerEntry> LedgerEntries { get; }

    DbSet<OutboxMessage> OutboxMessages { get; }

    DbSet<WebhookEvent> WebhookEvents { get; }

    DbSet<PaymentIdempotencyKey> PaymentIdempotencyKeys { get; }

    DbSet<ReconciliationReport> ReconciliationReports { get; }

    DatabaseFacade Database { get; }

    EntityEntry Entry(object entity);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
