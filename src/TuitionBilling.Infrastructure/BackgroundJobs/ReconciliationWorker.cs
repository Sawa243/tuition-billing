using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TuitionBilling.Application.Abstractions;
using TuitionBilling.Application.Services;

namespace TuitionBilling.Infrastructure.BackgroundJobs;

public sealed class ReconciliationOptions
{
    public const string SectionName = "Reconciliation";

    public bool Enabled { get; set; } = true;

    /// <summary>Час по UTC, после которого сверка за вчера считается положенной.</summary>
    public int RunAfterHourUtc { get; set; } = 1;

    public int CheckIntervalMinutes { get; set; } = 15;
}

/// <summary>
/// Суточная сверка запускается сама. Проверка идёт по факту, а не по расписанию:
/// «за вчера отчёта нет и час уже наступил» — значит пора. Так сверка не потеряется,
/// если сервис в нужную минуту был выключен.
/// </summary>
public sealed class ReconciliationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ReconciliationOptions _options;
    private readonly ILogger<ReconciliationWorker> _logger;

    public ReconciliationWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<ReconciliationOptions> options,
        ILogger<ReconciliationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Автоматическая сверка выключена настройкой");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, _options.CheckIntervalMinutes)));
        do
        {
            try
            {
                await RunIfDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Суточная сверка сорвалась");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunIfDueAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now.Hour < _options.RunAfterHourUtc)
        {
            return;
        }

        var yesterday = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-1);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IBillingDbContext>();

        if (await db.ReconciliationReports.AnyAsync(r => r.Date == yesterday, cancellationToken))
        {
            return;
        }

        var reconciliation = scope.ServiceProvider.GetRequiredService<ReconciliationService>();
        await reconciliation.RunAsync(yesterday, cancellationToken);
    }
}
