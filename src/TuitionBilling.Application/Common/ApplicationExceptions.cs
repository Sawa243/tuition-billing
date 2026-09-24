namespace TuitionBilling.Application.Common;

public sealed class NotFoundException : Exception
{
    public NotFoundException(string what, object key) : base($"{what} {key} не найден.")
    {
    }
}

/// <summary>Объект существует, но обратился к нему не тот, кому он принадлежит.</summary>
public sealed class AccessDeniedException : Exception
{
    public AccessDeniedException(string message) : base(message)
    {
    }
}

/// <summary>Действие противоречит текущему состоянию данных.</summary>
public sealed class DomainConflictException : Exception
{
    public DomainConflictException(string message) : base(message)
    {
    }
}

/// <summary>Тот же ключ идемпотентности пришёл с другими данными.</summary>
public sealed class IdempotencyConflictException : Exception
{
    public IdempotencyConflictException(string key)
        : base($"Ключ идемпотентности {key} уже использован с другими параметрами запроса.")
    {
    }
}

public sealed class GatewayException : Exception
{
    public GatewayException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

public sealed class ValidationException : Exception
{
    public ValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("Запрос не прошёл проверку.")
    {
        Errors = errors;
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}
