using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TuitionBilling.Application.Abstractions;
using TuitionBilling.Application.Persistence;
using TuitionBilling.Domain.Contracts;
using TuitionBilling.Domain.Ledger;
using TuitionBilling.Domain.Payments;
using TuitionBilling.Domain.Students;
using TuitionBilling.Infrastructure.Identity;

namespace TuitionBilling.Infrastructure.Persistence;

public sealed class BillingDbContext
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>, IBillingDbContext
{
    public BillingDbContext(DbContextOptions<BillingDbContext> options) : base(options)
    {
    }

    public DbSet<Student> Students => Set<Student>();

    public DbSet<Contract> Contracts => Set<Contract>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<InstallmentPlan> InstallmentPlans => Set<InstallmentPlan>();

    public DbSet<InstallmentItem> InstallmentItems => Set<InstallmentItem>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<Refund> Refunds => Set<Refund>();

    public DbSet<LedgerAccount> LedgerAccounts => Set<LedgerAccount>();

    public DbSet<LedgerTransaction> LedgerTransactions => Set<LedgerTransaction>();

    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();

    public DbSet<PaymentIdempotencyKey> PaymentIdempotencyKeys => Set<PaymentIdempotencyKey>();

    public DbSet<ReconciliationReport> ReconciliationReports => Set<ReconciliationReport>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(BillingDbContext).Assembly);
        UseSnakeCaseNames(builder);
    }

    /// <summary>
    /// В PostgreSQL всё, что не взято в кавычки, приводится к нижнему регистру,
    /// поэтому имена вида PeriodCode пришлось бы всюду цитировать. Переводим
    /// таблицы, столбцы и индексы в snake_case — так схему читает и тот, кто
    /// полезет в базу psql'ем мимо приложения.
    /// </summary>
    private static void UseSnakeCaseNames(ModelBuilder builder)
    {
        foreach (var entity in builder.Model.GetEntityTypes())
        {
            entity.SetTableName(ToSnakeCase(entity.GetTableName()));

            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.GetColumnName()));
            }

            foreach (var key in entity.GetKeys())
            {
                key.SetName(ToSnakeCase(key.GetName()));
            }

            foreach (var index in entity.GetIndexes())
            {
                index.SetDatabaseName(ToSnakeCase(index.GetDatabaseName()));
            }

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                foreignKey.SetConstraintName(ToSnakeCase(foreignKey.GetConstraintName()));
            }
        }
    }

    private static string? ToSnakeCase(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var result = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var current = name[i];

            // Подчёркивание ставится на границе слова, а не перед каждой заглавной,
            // иначе аббревиатуры рассыпаются: IX_Payments превратился бы в i_x_payments.
            var startsWord = char.IsUpper(current)
                             && i > 0
                             && name[i - 1] != '_'
                             && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])));

            if (startsWord)
            {
                result.Append('_');
            }

            result.Append(char.ToLowerInvariant(current));
        }

        return result.ToString();
    }
}
