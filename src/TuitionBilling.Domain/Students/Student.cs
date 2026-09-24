using TuitionBilling.Domain.Common;

namespace TuitionBilling.Domain.Students;

/// <summary>
/// Карточка обучающегося. Идентификатор совпадает с идентификатором учётной записи
/// в ASP.NET Core Identity — одна сущность на человека, без лишнего сопоставления.
/// </summary>
public sealed class Student : Entity
{
    private Student()
    {
        FullName = string.Empty;
        Email = string.Empty;
    }

    public Student(Guid id, string fullName, string email, string? phone = null)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            throw new DomainException("ФИО обучающегося обязательно.");
        }

        Id = id;
        FullName = fullName.Trim();
        Email = email.Trim();
        Phone = phone;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public string FullName { get; private set; }

    public string Email { get; private set; }

    public string? Phone { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public void Rename(string fullName) => FullName = fullName.Trim();
}
