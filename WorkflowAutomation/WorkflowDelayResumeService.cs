using Microsoft.Extensions.Logging;
using Nexa.WorkflowAutomation.Engine;
using Nexa.WorkflowAutomation.Enums;
using Nexa.WorkflowAutomation.Exceptions;

namespace nexthire_api.WorkflowAutomation;

/// <summary>
/// Resumes Waiting executions whose <c>resumeAt</c> has elapsed (delays and event timeouts).
/// </summary>
public sealed class WorkflowDelayResumeService
{
    public const int DefaultLimit = 50;

    private readonly IWorkflowDueExecutionLookup _due;
    private readonly IWorkflowEngine _engine;
    private readonly ILogger<WorkflowDelayResumeService> _logger;

    public WorkflowDelayResumeService(
        IWorkflowDueExecutionLookup due,
        IWorkflowEngine engine,
        ILogger<WorkflowDelayResumeService> logger)
    {
        _due = due;
        _engine = engine;
        _logger = logger;
    }

    public async Task<int> ResumeDueAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var due = await _due.ListDueAsync(now, DefaultLimit, cancellationToken);
        var resumed = 0;

        foreach (var execution in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var current = await _engine.ResumeFromTimeoutAsync(
                    execution.OrganizationId,
                    execution.Id,
                    now,
                    cancellationToken);

                var guard = 0;
                while (current.Status is WorkflowExecutionStatus.Pending or WorkflowExecutionStatus.Running)
                {
                    if (++guard > 50)
                    {
                        _logger.LogWarning(
                            "Delay resume exceeded step iteration guard. ExecutionId={ExecutionId}",
                            current.Id);
                        break;
                    }

                    current = await _engine.ExecuteCurrentStepAsync(
                        current.OrganizationId,
                        current.Id,
                        cancellationToken);
                }

                resumed++;
                _logger.LogInformation(
                    "Resumed due workflow execution. ExecutionId={ExecutionId}, Status={Status}",
                    current.Id,
                    current.Status);
            }
            catch (WorkflowInvalidStateException ex)
            {
                _logger.LogDebug(
                    ex,
                    "Skipping due execution that is no longer Waiting. ExecutionId={ExecutionId}",
                    execution.Id);
            }
            catch (WorkflowExecutionNotFoundException)
            {
                _logger.LogDebug("Due execution disappeared before resume. ExecutionId={ExecutionId}", execution.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resume due workflow execution {ExecutionId}", execution.Id);
            }
        }

        return resumed;
    }
}
