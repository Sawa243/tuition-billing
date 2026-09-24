using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using TuitionBilling.Api.Auth;
using TuitionBilling.Infrastructure.Identity;

namespace TuitionBilling.Api.Controllers;

public sealed record LoginRequest(string Email, string Password);

public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt, string FullName, IReadOnlyList<string> Roles);

public sealed class AuthController : ApiControllerBase
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly JwtTokenService _tokens;
    private readonly ILogger<AuthController> _logger;

    public AuthController(UserManager<ApplicationUser> users, JwtTokenService tokens, ILogger<AuthController> logger)
    {
        _users = users;
        _tokens = tokens;
        _logger = logger;
    }

    /// <summary>Вход по почте и паролю. В ответ — токен, с которым ходят все остальные запросы.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var user = await _users.FindByEmailAsync(request.Email);

        // Ответ одинаков и когда нет такого пользователя, и когда пароль не тот:
        // иначе по разнице ответов перебирают существующие адреса.
        if (user is null || !await _users.CheckPasswordAsync(user, request.Password))
        {
            _logger.LogWarning("Неудачный вход для {Email}", request.Email);
            return Unauthorized(new { detail = "Неверная почта или пароль." });
        }

        var roles = await _users.GetRolesAsync(user);
        var (token, expiresAt) = _tokens.Issue(user, roles);

        return Ok(new LoginResponse(token, expiresAt, user.FullName, roles.ToList()));
    }

    /// <summary>Кто я: имя, роли и идентификатор. Клиент по этому решает, какой кабинет показать.</summary>
    [HttpGet("me")]
    [Authorize]
    public IActionResult Me() => Ok(new
    {
        id = CurrentUserId,
        fullName = User.FindFirst("full_name")?.Value ?? string.Empty,
        roles = User.FindAll(System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToList()
    });
}
