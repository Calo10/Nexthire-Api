using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace nexthire_api.WorkflowAutomation;

/// <summary>
/// Fire-and-forget workflow dispatch. Failures are logged and never thrown to callers.
/// </summary>
public static class CandidateWorkflowDispatch
{
    public static async Task TryAsync(
        IWorkflowTriggerService triggers,
        ILogger logger,
        Guid organizationId,
        string triggerType,
        string entityType,
        string entityId,
        IReadOnlyDictionary<string, JsonElement>? variables,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(triggerType) || string.IsNullOrWhiteSpace(entityId))
            return;

        try
        {
            await triggers.DispatchAsync(
                organizationId,
                triggerType,
                entityType,
                entityId,
                variables,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Workflow trigger {TriggerType} failed for {EntityType} {EntityId}. Org={OrganizationId}",
                triggerType,
                entityType,
                entityId,
                organizationId);
        }
    }
}
