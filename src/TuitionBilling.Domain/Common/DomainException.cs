namespace TuitionBilling.Domain.Common;

/// <summary>
/// Нарушено бизнес-правило. Слой API превращает такое в 409/422, а не в 500.
/// </summary>
public sealed class DomainException : Exception
{
    public DomainException(string message) : base(message)
    {
    }
}
