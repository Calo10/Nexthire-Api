using Nexa.WorkflowAutomation.Models.Executions;

namespace nexthire_api.WorkflowAutomation;

/// <summary>
/// Cross-tenant lookup of delay/timeout executions that are due to resume.
/// </summary>
public interface IWorkflowDueExecutionLookup
{
    Task<IReadOnlyList<WorkflowExecution>> ListDueAsync(
        DateTimeOffset asOf,
        int limit,
        CancellationToken cancellationToken = default);
}
