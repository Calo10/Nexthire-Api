namespace nexthire_api.Services;

/// <summary>
/// Polls every 60 seconds for due Core HR report schedules and runs them through <see cref="IHrReportsService"/>
/// (one DI scope per poll). Disabled in the Testing environment. Duplicate runs are prevented by claiming the schedule
/// (next_run_at moves forward atomically) and by a 5-minute recent-execution check.
/// </summary>
public sealed class ReportScheduleHostedService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<ReportScheduleHostedService> _logger;

    public ReportScheduleHostedService(
        IServiceScopeFactory scopeFactory,
        IHostEnvironment environment,
        ILogger<ReportScheduleHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _environment = environment;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_environment.IsEnvironment("Testing"))
        {
            _logger.LogInformation("Report schedule polling is disabled in the Testing environment.");
            return;
        }

        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var reports = scope.ServiceProvider.GetRequiredService<IHrReportsService>();
                    var processed = await reports.ProcessDueSchedulesAsync(DateTimeOffset.UtcNow, stoppingToken);
                    if (processed > 0)
                        _logger.LogInformation("Processed {Count} due report schedule(s).", processed);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Schema may not be ready yet at startup, or the DB may be briefly unavailable: retry next tick.
                    _logger.LogError(ex, "Report schedule poll failed.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down.
        }
    }
}
