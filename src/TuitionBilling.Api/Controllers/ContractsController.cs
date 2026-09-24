using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using TuitionBilling.Application.Contracts;
using TuitionBilling.Application.Services;
using TuitionBilling.Domain.Students;
using TuitionBilling.Infrastructure.Identity;
using TuitionBilling.Infrastructure.Persistence;

namespace TuitionBilling.Api.Controllers;

[Authorize]
public sealed class ContractsController : ApiControllerBase
{
    private readonly ContractService _contracts;

    public ContractsController(ContractService contracts)
    {
        _contracts = contracts;
    }

    /// <summary>Список договоров. Обучающийся видит только свои.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<ContractDto>> List(CancellationToken cancellationToken) =>
        await _contracts.ListAsync(StudentScope, cancellationToken);

    [HttpGet("{id:guid}")]
    public async Task<ContractDto> Get(Guid id, CancellationToken cancellationToken)
    {
        var contract = await _contracts.GetAsync(id, cancellationToken);
        if (StudentScope is { } studentId && contract.StudentId != studentId)
        {
            throw new Application.Common.AccessDeniedException("Договор оформлен на другого обучающегося.");
        }

        return contract;
    }

    [HttpPost]
    [Authorize(Roles = Roles.Accountant)]
    public async Task<ActionResult<ContractDto>> Create([FromBody] CreateContractRequest request, CancellationToken cancellationToken)
    {
        await ValidateAsync(request, cancellationToken);
        var contract = await _contracts.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = contract.Id }, contract);
    }

    /// <summary>Выставить начисление за учебный период.</summary>
    [HttpPost("{id:guid}/invoices")]
    [Authorize(Roles = Roles.Accountant)]
    public async Task<InvoiceDto> IssueInvoice(Guid id, [FromBody] IssueInvoiceRequest request, CancellationToken cancellationToken)
    {
        await ValidateAsync(request, cancellationToken);
        return await _contracts.IssueInvoiceAsync(id, request, cancellationToken);
    }
}

public sealed record CancelInvoiceRequest(string Reason);

[Authorize]
public sealed class InvoicesController : ApiControllerBase
{
    private readonly ContractService _contracts;

    public InvoicesController(ContractService contracts)
    {
        _contracts = contracts;
    }

    /// <summary>Начисления. По умолчанию все, с onlyUnpaid=true — только с остатком к оплате.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<InvoiceDto>> List([FromQuery] bool onlyUnpaid, CancellationToken cancellationToken) =>
        await _contracts.ListInvoicesAsync(StudentScope, onlyUnpaid, cancellationToken);

    [HttpPost("{id:guid}/installments")]
    [Authorize(Roles = Roles.Accountant)]
    public async Task<InvoiceDto> SplitIntoInstallments(
        Guid id,
        [FromBody] CreateInstallmentPlanRequest request,
        CancellationToken cancellationToken)
    {
        await ValidateAsync(request, cancellationToken);
        return await _contracts.SplitIntoInstallmentsAsync(id, request, cancellationToken);
    }

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Roles = Roles.Accountant)]
    public async Task<InvoiceDto> Cancel(Guid id, [FromBody] CancelInvoiceRequest request, CancellationToken cancellationToken) =>
        await _contracts.CancelInvoiceAsync(id, request.Reason, cancellationToken);
}

[Authorize(Roles = Roles.Accountant)]
public sealed class StudentsController : ApiControllerBase
{
    private readonly BillingDbContext _db;
    private readonly UserManager<ApplicationUser> _users;

    public StudentsController(BillingDbContext db, UserManager<ApplicationUser> users)
    {
        _db = db;
        _users = users;
    }

    [HttpGet]
    public async Task<IReadOnlyList<StudentDto>> List(CancellationToken cancellationToken)
    {
        var students = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(_db.Students.OrderBy(s => s.FullName), cancellationToken);

        return students.Select(s => new StudentDto(s.Id, s.FullName, s.Email, s.Phone)).ToList();
    }

    /// <summary>Завести обучающегося: создаётся и учётная запись, и карточка.</summary>
    [HttpPost]
    public async Task<ActionResult<StudentDto>> Create([FromBody] CreateStudentRequest request, CancellationToken cancellationToken)
    {
        await ValidateAsync(request, cancellationToken);

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = request.Email,
            Email = request.Email,
            FullName = request.FullName,
            EmailConfirmed = true
        };

        var created = await _users.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            return BadRequest(new { detail = string.Join("; ", created.Errors.Select(e => e.Description)) });
        }

        await _users.AddToRoleAsync(user, Roles.Student);

        var student = new Student(user.Id, request.FullName, request.Email, request.Phone);
        _db.Students.Add(student);
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new StudentDto(student.Id, student.FullName, student.Email, student.Phone));
    }
}
