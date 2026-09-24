using FluentValidation;
using TuitionBilling.Application.Contracts;

namespace TuitionBilling.Application.Validation;

// Валидаторы вызываются руками в контроллере. Автоматическая интеграция через
// FluentValidation.AspNetCore больше не рекомендуется самими авторами: конвейер
// валидации ASP.NET синхронный, и асинхронные правила в нём не работают.

public sealed class CreateStudentRequestValidator : AbstractValidator<CreateStudentRequest>
{
    public CreateStudentRequestValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200).WithMessage("Укажите ФИО обучающегося.");
        RuleFor(x => x.Email).NotEmpty().EmailAddress().WithMessage("Нужен корректный адрес электронной почты.");
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8).WithMessage("Пароль не короче восьми символов.");
        RuleFor(x => x.Phone).MaximumLength(20);
    }
}

public sealed class CreateContractRequestValidator : AbstractValidator<CreateContractRequest>
{
    public CreateContractRequestValidator()
    {
        RuleFor(x => x.StudentId).NotEmpty();
        RuleFor(x => x.Number).NotEmpty().MaximumLength(50);
        RuleFor(x => x.ProgramName).NotEmpty().MaximumLength(300);
        RuleFor(x => x.AdmissionYear).InclusiveBetween(2000, 2100);
        RuleFor(x => x.TotalAmount).GreaterThan(0m).LessThanOrEqualTo(10_000_000m);
        RuleFor(x => x.TotalAmount).Must(HasNoSubKopecks).WithMessage("Сумма договора точнее копейки.");
    }

    private static bool HasNoSubKopecks(decimal value) => decimal.Round(value, 2) == value;
}

public sealed class IssueInvoiceRequestValidator : AbstractValidator<IssueInvoiceRequest>
{
    public IssueInvoiceRequestValidator()
    {
        RuleFor(x => x.PeriodCode).NotEmpty().MaximumLength(20)
            .Matches(@"^\d{4}/\d{4}-[1-2]$").WithMessage("Период записывается как 2026/2027-1.");
        RuleFor(x => x.Amount).GreaterThan(0m);
        RuleFor(x => x.DueOn).GreaterThanOrEqualTo(x => x.IssuedOn).WithMessage("Срок оплаты раньше даты начисления.");
    }
}

public sealed class CreateInstallmentPlanRequestValidator : AbstractValidator<CreateInstallmentPlanRequest>
{
    public CreateInstallmentPlanRequestValidator()
    {
        RuleFor(x => x.PartCount).InclusiveBetween(2, 12);
        RuleFor(x => x.IntervalDays).InclusiveBetween(1, 180);
    }
}

public sealed class CreatePaymentRequestValidator : AbstractValidator<CreatePaymentRequest>
{
    public CreatePaymentRequestValidator()
    {
        RuleFor(x => x.InvoiceId).NotEmpty();
        RuleFor(x => x.Amount).GreaterThan(0m).LessThanOrEqualTo(1_000_000m);
        RuleFor(x => x.Amount).Must(v => decimal.Round(v, 2) == v).WithMessage("Сумма платежа точнее копейки.");
    }
}

public sealed class CreateRefundRequestValidator : AbstractValidator<CreateRefundRequest>
{
    public CreateRefundRequestValidator()
    {
        RuleFor(x => x.PaymentId).NotEmpty();
        RuleFor(x => x.Amount).GreaterThan(0m);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(300).WithMessage("Укажите основание возврата.");
    }
}
