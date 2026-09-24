using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using TuitionBilling.Infrastructure.Identity;
using ValidationException = TuitionBilling.Application.Common.ValidationException;

namespace TuitionBilling.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    protected Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw new InvalidOperationException("В токене нет идентификатора пользователя.");

    protected bool IsAccountant => User.IsInRole(Roles.Accountant);

    /// <summary>
    /// Область видимости данных: бухгалтер видит всех, обучающийся — только себя.
    /// Ограничение ставится здесь, а не в запросе с клиента, поэтому подставить
    /// чужой идентификатор в адресную строку бесполезно.
    /// </summary>
    protected Guid? StudentScope => IsAccountant ? null : CurrentUserId;

    protected async Task ValidateAsync<T>(T request, CancellationToken cancellationToken)
    {
        var validator = HttpContext.RequestServices.GetService<IValidator<T>>();
        if (validator is null)
        {
            return;
        }

        var result = await validator.ValidateAsync(request, cancellationToken);
        if (result.IsValid)
        {
            return;
        }

        throw new ValidationException(result.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));
    }
}
