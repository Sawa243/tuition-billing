using Microsoft.AspNetCore.Identity;

namespace TuitionBilling.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string FullName { get; set; } = string.Empty;
}

public static class Roles
{
    /// <summary>Обучающийся: видит только свои договоры, начисления и платежи.</summary>
    public const string Student = "student";

    /// <summary>Бухгалтер: заводит договоры и начисления, оформляет возвраты, смотрит журнал и сверку.</summary>
    public const string Accountant = "accountant";

    public static readonly string[] All = { Student, Accountant };
}
