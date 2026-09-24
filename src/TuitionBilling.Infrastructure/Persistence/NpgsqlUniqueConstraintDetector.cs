using Microsoft.EntityFrameworkCore;
using Npgsql;
using TuitionBilling.Application.Abstractions;

namespace TuitionBilling.Infrastructure.Persistence;

/// <summary>
/// 23505 — код нарушения уникального ограничения в PostgreSQL. На нём держится
/// вся идемпотентность: вместо «проверить и вставить» мы сразу вставляем и
/// смотрим, не проиграли ли гонку.
/// </summary>
public sealed class NpgsqlUniqueConstraintDetector : IUniqueConstraintDetector
{
    public bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
