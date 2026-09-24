using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TuitionBilling.Application.Services;
using TuitionBilling.Domain.Students;
using TuitionBilling.Infrastructure.Identity;

namespace TuitionBilling.Infrastructure.Persistence;

/// <summary>
/// Демонстрационные данные: без них пустую систему не показать ни проверяющему,
/// ни нагрузочному тесту. Пароли демо-учёток берутся из конфигурации, а не из кода.
/// </summary>
public sealed class DatabaseSeeder
{
    private readonly BillingDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly RoleManager<IdentityRole<Guid>> _roles;
    private readonly LedgerService _ledger;
    private readonly ILogger<DatabaseSeeder> _logger;

    public DatabaseSeeder(
        BillingDbContext db,
        UserManager<ApplicationUser> users,
        RoleManager<IdentityRole<Guid>> roles,
        LedgerService ledger,
        ILogger<DatabaseSeeder> logger)
    {
        _db = db;
        _users = users;
        _roles = roles;
        _ledger = ledger;
        _logger = logger;
    }

    public async Task SeedAsync(string accountantPassword, string studentPassword, CancellationToken cancellationToken)
    {
        foreach (var role in Roles.All)
        {
            if (!await _roles.RoleExistsAsync(role))
            {
                await _roles.CreateAsync(new IdentityRole<Guid>(role));
            }
        }

        await EnsureAccountantAsync("buh@synergy.local", "Петрова Мария Сергеевна", accountantPassword);

        var ivanov = await EnsureStudentAsync("ivanov@synergy.local", "Иванов Иван Иванович", studentPassword);
        var petrov = await EnsureStudentAsync("petrov@synergy.local", "Петров Алексей Дмитриевич", studentPassword);

        await EnsureContractAsync(ivanov, "ДГ-2026-0417", "02.03.03 Математическое обеспечение и администрирование информационных систем",
            480_000m, cancellationToken);
        await EnsureContractAsync(petrov, "ДГ-2026-0418", "09.03.03 Прикладная информатика",
            420_000m, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Демонстрационные данные готовы");
    }

    private async Task EnsureAccountantAsync(string email, string fullName, string password)
    {
        if (await _users.FindByEmailAsync(email) is not null)
        {
            return;
        }

        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = email, Email = email, FullName = fullName, EmailConfirmed = true };
        var result = await _users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Не удалось создать бухгалтера: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        }

        await _users.AddToRoleAsync(user, Roles.Accountant);
    }

    private async Task<Student> EnsureStudentAsync(string email, string fullName, string password)
    {
        var existing = await _db.Students.FirstOrDefaultAsync(s => s.Email == email);
        if (existing is not null)
        {
            return existing;
        }

        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = email, Email = email, FullName = fullName, EmailConfirmed = true };
        var result = await _users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Не удалось создать обучающегося: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        }

        await _users.AddToRoleAsync(user, Roles.Student);

        var student = new Student(user.Id, fullName, email);
        _db.Students.Add(student);
        await _db.SaveChangesAsync();
        return student;
    }

    private async Task EnsureContractAsync(
        Student student,
        string number,
        string program,
        decimal total,
        CancellationToken cancellationToken)
    {
        if (await _db.Contracts.AnyAsync(c => c.Number == number, cancellationToken))
        {
            return;
        }

        var signedOn = new DateOnly(2026, 8, 25);
        var contract = new Domain.Contracts.Contract(number, student.Id, program, 2026, total, signedOn);
        _db.Contracts.Add(contract);

        var invoice = contract.IssueInvoice("2026/2027-1", total / 4m, signedOn, signedOn.AddDays(30));
        await _ledger.PostInvoiceIssuedAsync(invoice, contract, cancellationToken);
    }
}
