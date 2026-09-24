using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using PaymentGateway.Emulator.Contracts;

namespace PaymentGateway.Emulator.Infrastructure;

/// <summary>
/// Basic-авторизация магазина: логин — идентификатор магазина, пароль — секретный ключ.
/// Настоящий провайдер защищает свой API точно так же.
/// </summary>
public sealed class ShopAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<GatewayOptions> _options;

    public ShopAuthenticationMiddleware(RequestDelegate next, IOptionsMonitor<GatewayOptions> options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/v3"))
        {
            await _next(context);
            return;
        }

        if (!IsAuthorized(context.Request.Headers.Authorization))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"gateway\"";
            await context.Response.WriteAsJsonAsync(
                new ErrorDto("error", "invalid_credentials", "Неверный идентификатор магазина или секретный ключ."));
            return;
        }

        await _next(context);
    }

    private bool IsAuthorized(string? header)
    {
        if (string.IsNullOrWhiteSpace(header) || !AuthenticationHeaderValue.TryParse(header, out var parsed))
        {
            return false;
        }

        if (!string.Equals(parsed.Scheme, "Basic", StringComparison.OrdinalIgnoreCase) || parsed.Parameter is null)
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parsed.Parameter));
        }
        catch (FormatException)
        {
            return false;
        }

        var separator = decoded.IndexOf(':');
        if (separator < 0)
        {
            return false;
        }

        var options = _options.CurrentValue;
        return decoded[..separator] == options.ShopId && decoded[(separator + 1)..] == options.SecretKey;
    }
}
