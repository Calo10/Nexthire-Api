using Nexa.WorkflowAutomation.Contracts.Providers;

namespace nexthire_api.WorkflowAutomation.Providers;

/// <summary>
/// Records a call request as a workflow output. NextHire has no dialer; the step succeeds
/// so published templates that include Call can finish instead of crashing.
/// </summary>
public sealed class WorkflowCallService : ICallService
{
    private readonly ILogger<WorkflowCallService> _logger;

    public WorkflowCallService(ILogger<WorkflowCallService> logger)
    {
        _logger = logger;
    }

    public Task<CallOperationResult> CreateAsync(CallOperationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var callId = Guid.NewGuid().ToString("D");
        _logger.LogInformation(
            "Workflow recorded call {CallId} for org {OrganizationId} to {Phone}",
            callId,
            request.OrganizationId,
            request.PhoneNumber);
        return Task.FromResult(new CallOperationResult { CallId = callId });
    }
}
