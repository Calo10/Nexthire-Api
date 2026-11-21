using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace nexthire_api.WorkflowAutomation;

/// <summary>
/// Polls for delay/timeout executions and resumes them in-process.
/// </summary>
public sealed class WorkflowDelayResumeHostedService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WorkflowDelayResumeHostedService> _logger;

    public WorkflowDelayResumeHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<WorkflowDelayResumeHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<WorkflowDelayResumeService>();
                await service.ResumeDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Workflow delay resume poll failed.");
            }
        }
    }
}
