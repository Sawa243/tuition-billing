using Microsoft.EntityFrameworkCore;

namespace TuitionBilling.Application.Abstractions;

/// <summary>
/// Нарушение уникального индекса приходит от драйвера базы в его собственном виде,
/// а прикладной слой про драйвер знать не должен. Распознаёт его реализация
/// в Infrastructure, здесь — только вопрос «это была гонка по уникальному ключу?».
/// </summary>
public interface IUniqueConstraintDetector
{
    bool IsUniqueViolation(DbUpdateException exception);
}
