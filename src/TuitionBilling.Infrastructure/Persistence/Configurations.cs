using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TuitionBilling.Application.Persistence;
using TuitionBilling.Domain.Contracts;
using TuitionBilling.Domain.Ledger;
using TuitionBilling.Domain.Payments;
using TuitionBilling.Domain.Students;

namespace TuitionBilling.Infrastructure.Persistence;

// Деньги везде numeric(14,2). Тип money в PostgreSQL не используется намеренно:
// его не рекомендует сама документация Postgres — он зависит от локали и
// хранит фиксированные два знака независимо от валюты.
internal static class Money
{
    public const string ColumnType = "numeric(14,2)";
}

public sealed class StudentConfiguration : IEntityTypeConfiguration<Student>
{
    public void Configure(EntityTypeBuilder<Student> builder)
    {
        builder.ToTable("students");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.FullName).HasMaxLength(200).IsRequired();
        builder.Property(s => s.Email).HasMaxLength(256).IsRequired();
        builder.Property(s => s.Phone).HasMaxLength(20);
        builder.HasIndex(s => s.Email).IsUnique();
    }
}

public sealed class ContractConfiguration : IEntityTypeConfiguration<Contract>
{
    public void Configure(EntityTypeBuilder<Contract> builder)
    {
        builder.ToTable("contracts");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Number).HasMaxLength(50).IsRequired();
        builder.Property(c => c.ProgramName).HasMaxLength(300).IsRequired();
        builder.Property(c => c.TotalAmount).HasColumnType(Money.ColumnType);
        builder.Property(c => c.Status).HasConversion<int>();
        builder.HasIndex(c => c.Number).IsUnique();
        builder.HasIndex(c => c.StudentId);

        builder.HasMany(c => c.Invoices)
            .WithOne()
            .HasForeignKey(i => i.ContractId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(Contract.Invoices))!.SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("invoices");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.PeriodCode).HasMaxLength(20).IsRequired();
        builder.Property(i => i.Amount).HasColumnType(Money.ColumnType);
        builder.Property(i => i.PaidAmount).HasColumnType(Money.ColumnType);
        builder.Property(i => i.RefundedAmount).HasColumnType(Money.ColumnType);
        builder.Property(i => i.Status).HasConversion<int>();
        builder.Ignore(i => i.Outstanding);
        builder.HasIndex(i => new { i.ContractId, i.PeriodCode });

        // Оптимистическая блокировка на системном столбце xmin: уведомление шлюза
        // и возврат плательщика на сайт нередко приходят одновременно.
        builder.Property<uint>("xmin").IsRowVersion();

        builder.HasOne(i => i.InstallmentPlan)
            .WithOne()
            .HasForeignKey<InstallmentPlan>(p => p.InvoiceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(Invoice.InstallmentPlan))!.SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class InstallmentPlanConfiguration : IEntityTypeConfiguration<InstallmentPlan>
{
    public void Configure(EntityTypeBuilder<InstallmentPlan> builder)
    {
        builder.ToTable("installment_plans");
        builder.HasKey(p => p.Id);
        builder.HasIndex(p => p.InvoiceId).IsUnique();

        builder.HasMany(p => p.Items)
            .WithOne()
            .HasForeignKey(i => i.PlanId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(InstallmentPlan.Items))!.SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class InstallmentItemConfiguration : IEntityTypeConfiguration<InstallmentItem>
{
    public void Configure(EntityTypeBuilder<InstallmentItem> builder)
    {
        builder.ToTable("installment_items");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Amount).HasColumnType(Money.ColumnType);
        builder.Property(i => i.PaidAmount).HasColumnType(Money.ColumnType);
        builder.Ignore(i => i.IsPaid);
        builder.Ignore(i => i.Outstanding);
        builder.HasIndex(i => new { i.PlanId, i.Number }).IsUnique();
    }
}

public sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Amount).HasColumnType(Money.ColumnType);
        builder.Property(p => p.RefundedAmount).HasColumnType(Money.ColumnType);
        builder.Property(p => p.Status).HasConversion<int>();
        builder.Property(p => p.IdempotencyKey).HasMaxLength(64).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(300);
        builder.Property(p => p.GatewayPaymentId).HasMaxLength(64);
        builder.Property(p => p.ConfirmationUrl).HasMaxLength(500);
        builder.Property(p => p.CancellationReason).HasMaxLength(200);
        builder.Property(p => p.GatewayReceiptId).HasMaxLength(64);
        builder.Property(p => p.FiscalDocumentNumber).HasMaxLength(32);
        builder.Ignore(p => p.RefundableAmount);
        builder.Property<uint>("xmin").IsRowVersion();

        builder.HasIndex(p => p.GatewayPaymentId).IsUnique();
        builder.HasIndex(p => p.InvoiceId);
        builder.HasIndex(p => new { p.StudentId, p.CreatedAt });
    }
}

public sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> builder)
    {
        builder.ToTable("refunds");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Amount).HasColumnType(Money.ColumnType);
        builder.Property(r => r.Status).HasConversion<int>();
        builder.Property(r => r.Reason).HasMaxLength(300).IsRequired();
        builder.Property(r => r.IdempotencyKey).HasMaxLength(64).IsRequired();
        builder.Property(r => r.GatewayRefundId).HasMaxLength(64);
        builder.HasIndex(r => r.PaymentId);
    }
}

public sealed class LedgerAccountConfiguration : IEntityTypeConfiguration<LedgerAccount>
{
    public void Configure(EntityTypeBuilder<LedgerAccount> builder)
    {
        builder.ToTable("ledger_accounts");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Code).HasMaxLength(10).IsRequired();
        builder.Property(a => a.Name).HasMaxLength(200).IsRequired();
        builder.Property(a => a.Kind).HasConversion<int>();

        // Дебиторка ведётся субсчётом на каждый договор, поэтому уникальна пара.
        builder.HasIndex(a => new { a.Code, a.ContractId }).IsUnique();
    }
}

public sealed class LedgerTransactionConfiguration : IEntityTypeConfiguration<LedgerTransaction>
{
    public void Configure(EntityTypeBuilder<LedgerTransaction> builder)
    {
        builder.ToTable("ledger_transactions");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Kind).HasConversion<int>();
        builder.Property(t => t.Reference).HasMaxLength(100).IsRequired();
        builder.Property(t => t.Description).HasMaxLength(300).IsRequired();
        builder.Ignore(t => t.DebitTotal);
        builder.Ignore(t => t.CreditTotal);
        builder.HasIndex(t => new { t.Kind, t.Reference });
        builder.HasIndex(t => t.OccurredAt);

        builder.HasMany(t => t.Entries)
            .WithOne()
            .HasForeignKey(e => e.TransactionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Metadata.FindNavigation(nameof(LedgerTransaction.Entries))!.SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class LedgerEntryConfiguration : IEntityTypeConfiguration<LedgerEntry>
{
    public void Configure(EntityTypeBuilder<LedgerEntry> builder)
    {
        builder.ToTable("ledger_entries");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Amount).HasColumnType(Money.ColumnType);
        builder.Property(e => e.Side).HasConversion<int>();
        builder.Ignore(e => e.Signed);
        builder.HasIndex(e => e.AccountId);
        builder.HasIndex(e => e.TransactionId);
    }
}

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Kind).HasMaxLength(50).IsRequired();
        builder.Property(m => m.Payload).IsRequired();
        builder.Property(m => m.LastError).HasMaxLength(1000);

        // Индекс под выборку фоновой службы: только неотправленные, по времени попытки.
        builder.HasIndex(m => new { m.ProcessedAt, m.IsDead, m.NextAttemptAt });
    }
}

public sealed class WebhookEventConfiguration : IEntityTypeConfiguration<WebhookEvent>
{
    public void Configure(EntityTypeBuilder<WebhookEvent> builder)
    {
        builder.ToTable("webhook_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.DeduplicationKey).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Event).HasMaxLength(50).IsRequired();
        builder.Property(e => e.GatewayPaymentId).HasMaxLength(64).IsRequired();
        builder.Property(e => e.ReportedStatus).HasMaxLength(30).IsRequired();
        builder.Property(e => e.VerifiedStatus).HasMaxLength(30);
        builder.Property(e => e.Outcome).HasMaxLength(30);
        builder.Property(e => e.RawBody).HasMaxLength(4000);
        builder.Property(e => e.SourceIp).HasMaxLength(64);

        // Индекс не уникальный: таблица — журнал аудита, а не механизм защиты.
        // От повторной доставки защищает конечный автомат платежа.
        builder.HasIndex(e => e.DeduplicationKey);
        builder.HasIndex(e => e.ReceivedAt);
    }
}

public sealed class PaymentIdempotencyKeyConfiguration : IEntityTypeConfiguration<PaymentIdempotencyKey>
{
    public void Configure(EntityTypeBuilder<PaymentIdempotencyKey> builder)
    {
        builder.ToTable("payment_idempotency_keys");
        builder.HasKey(k => k.Id);
        builder.Property(k => k.Key).HasMaxLength(64).IsRequired();
        builder.Property(k => k.RequestHash).HasMaxLength(64).IsRequired();

        // Вся идемпотентность создания платежа держится на этом индексе.
        builder.HasIndex(k => k.Key).IsUnique();
    }
}

public sealed class ReconciliationReportConfiguration : IEntityTypeConfiguration<ReconciliationReport>
{
    public void Configure(EntityTypeBuilder<ReconciliationReport> builder)
    {
        builder.ToTable("reconciliation_reports");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.GatewayTotal).HasColumnType(Money.ColumnType);
        builder.Property(r => r.LocalTotal).HasColumnType(Money.ColumnType);
        builder.Property(r => r.LedgerDrift).HasColumnType(Money.ColumnType);
        builder.Property(r => r.Details).HasColumnType("jsonb");
        builder.HasIndex(r => r.Date);
    }
}
