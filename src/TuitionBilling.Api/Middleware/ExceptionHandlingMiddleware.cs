using Microsoft.AspNetCore.Mvc;
using TuitionBilling.Application.Common;
using TuitionBilling.Domain.Common;

namespace TuitionBilling.Api.Middleware;

/// <summary>
/// Одно место, где исключения превращаются в ответы. Нарушение бизнес-правила —
/// это 409, а не 500: клиент сделал осмысленный запрос, просто сейчас так нельзя.
/// Текст ошибки отдаём как есть, потому что он писался для человека; наружу
/// не уходит ни стек, ни внутренности исключения.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            var problem = Translate(ex, context);
            if (problem.Status >= StatusCodes.Status500InternalServerError)
            {
                _logger.LogError(ex, "Необработанная ошибка при {Method} {Path}", context.Request.Method, context.Request.Path);
            }
            else
            {
                _logger.LogInformation("{Method} {Path} отклонён: {Detail}", context.Request.Method, context.Request.Path, problem.Detail);
            }

            if (context.Response.HasStarted)
            {
                throw;
            }

            context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(problem);
        }
    }

    private static ProblemDetails Translate(Exception exception, HttpContext context)
    {
        var (status, title, detail) = exception switch
        {
            NotFoundException => (StatusCodes.Status404NotFound, "Не найдено", exception.Message),
            AccessDeniedException => (StatusCodes.Status403Forbidden, "Доступ запрещён", exception.Message),
            IdempotencyConflictException => (StatusCodes.Status409Conflict, "Конфликт ключа идемпотентности", exception.Message),
            DomainConflictException => (StatusCodes.Status409Conflict, "Действие недоступно", exception.Message),
            DomainException => (StatusCodes.Status422UnprocessableEntity, "Нарушено правило учёта", exception.Message),
            GatewayException => (StatusCodes.Status502BadGateway, "Платёжный шлюз недоступен", exception.Message),
            _ => (StatusCodes.Status500InternalServerError, "Внутренняя ошибка", "Не удалось обработать запрос.")
        };

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = context.Request.Path
        };

        if (exception is Application.Common.ValidationException validation)
        {
            problem.Status = StatusCodes.Status400BadRequest;
            problem.Title = "Запрос не прошёл проверку";
            problem.Extensions["errors"] = validation.Errors;
        }

        return problem;
    }
}
