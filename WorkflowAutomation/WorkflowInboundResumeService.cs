using System.Text.Json;
using nexthire_api.WorkflowAutomation.Conversations;
using nexthire_api.WorkflowAutomation.Repositories;
using Nexa.WorkflowAutomation.Engine;
using Nexa.WorkflowAutomation.Enums;
using Nexa.WorkflowAutomation.Models.Events;
using Nexa.WorkflowAutomation.Models.Executions;
using Nexa.WorkflowAutomation.Serialization;

namespace nexthire_api.WorkflowAutomation;

/// <summary>
/// Resumes Waiting executions (or starts <c>whatsapp.received</c> / <c>email.received</c> triggers)
/// from product inbound channels.
/// </summary>
public interface IWorkflowInboundResumeService
{
    Task TryResumeWhatsAppAsync(
        Guid organizationId,
        string fromPhone,
        string? body,
        string? providerMessageId,
        CancellationToken cancellationToken = default);

    Task TryResumeEmailAsync(
        Guid organizationId,
        string fromEmail,
        string? body,
        string? providerMessageId,
        CancellationToken cancellationToken = default);
}

public sealed class WorkflowInboundResumeService : IWorkflowInboundResumeService
{
    private readonly SqlWorkflowExecutionRepository _executions;
    private readonly IWorkflowEngine _engine;
    private readonly IConversationInterviewService _interviews;
    private readonly IWorkflowTriggerService _triggers;
    private readonly ILogger<WorkflowInboundResumeService> _logger;

    public WorkflowInboundResumeService(
        SqlWorkflowExecutionRepository executions,
        IWorkflowEngine engine,
        IConversationInterviewService interviews,
        IWorkflowTriggerService triggers,
        ILogger<WorkflowInboundResumeService> logger)
    {
        _executions = executions;
        _engine = engine;
        _interviews = interviews;
        _triggers = triggers;
        _logger = logger;
    }

    public async Task TryResumeWhatsAppAsync(
        Guid organizationId,
        string fromPhone,
        string? body,
        string? providerMessageId,
        CancellationToken cancellationToken = default)
    {
        var from = WhatsAppTenantResolver.NormalizePhone(fromPhone);
        if (string.IsNullOrWhiteSpace(from))
            return;

        try
        {
            var interview = await _interviews.TryProcessReplyAsync(organizationId, from, body, cancellationToken);
            if (interview.Handled)
            {
                if (interview.Completed)
                    await _interviews.ResumeCompletedInterviewAsync(interview, cancellationToken);
                return;
            }

            var match = await _executions.FindWaitingByOrganizationAndPhoneAsync(
                organizationId, from, eventType: null, cancellationToken);
            if (match is null)
            {
                await CandidateWorkflowDispatch.TryAsync(
                    _triggers,
                    _logger,
                    organizationId,
                    CandidateWorkflowVariables.WhatsAppReceivedTrigger,
                    "WhatsApp",
                    from,
                    new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Candidate.Phone"] = WorkflowJsonSerializer.SerializeToElement(from),
                        ["Conversation.LastMessage"] = WorkflowJsonSerializer.SerializeToElement(body ?? string.Empty),
                    },
                    cancellationToken);
                return;
            }

            var (execution, waitingEventType, resumeCorrelation) = match.Value;
            var eventType = string.IsNullOrWhiteSpace(waitingEventType)
                ? WorkflowReplyWait.WhatsAppReplyEvent
                : waitingEventType;

            var receiptExternalKey = string.IsNullOrWhiteSpace(providerMessageId)
                ? $"{organizationId:D}:{Guid.NewGuid():N}"
                : $"{organizationId:D}:{providerMessageId}";

            var workflowEvent = new WorkflowEvent
            {
                Id = WorkflowEventIdFactory.FromExternalKey("whatsapp.inbound", receiptExternalKey),
                OrganizationId = execution.OrganizationId,
                WorkflowExecutionId = execution.Id,
                EventType = eventType,
                CorrelationId = resumeCorrelation,
                EntityType = execution.EntityType,
                EntityId = execution.EntityId,
                OccurredAt = DateTimeOffset.UtcNow,
                Payload = WorkflowJsonSerializer.SerializeToElement(new
                {
                    messageId = providerMessageId,
                    conversationId = from,
                    organizationId,
                    from,
                    body,
                    channel = "whatsapp",
                }),
            };

            var resumed = await _engine.ResumeFromEventAsync(
                new ResumeWorkflowFromEventRequest
                {
                    ExecutionId = execution.Id,
                    Event = workflowEvent,
                },
                cancellationToken);

            var guard = 0;
            while (resumed.Status is WorkflowExecutionStatus.Pending or WorkflowExecutionStatus.Running)
            {
                if (++guard > 50)
                    break;
                resumed = await _engine.ExecuteCurrentStepAsync(
                    resumed.OrganizationId,
                    resumed.Id,
                    cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "WhatsApp workflow inbound resume failed. Org={OrganizationId} From={From}",
                organizationId,
                from);
        }
    }

    public async Task TryResumeEmailAsync(
        Guid organizationId,
        string fromEmail,
        string? body,
        string? providerMessageId,
        CancellationToken cancellationToken = default)
    {
        var from = EmailTenantResolver.NormalizeEmail(fromEmail);
        if (string.IsNullOrWhiteSpace(from))
            return;

        try
        {
            var interview = await _interviews.TryProcessReplyAsync(organizationId, from, body, cancellationToken);
            if (interview.Handled)
            {
                if (interview.Completed)
                    await _interviews.ResumeCompletedInterviewAsync(interview, cancellationToken);
                return;
            }

            var match = await _executions.FindWaitingByOrganizationAndEmailAsync(
                organizationId, from, eventType: null, cancellationToken);
            if (match is null)
            {
                await CandidateWorkflowDispatch.TryAsync(
                    _triggers,
                    _logger,
                    organizationId,
                    CandidateWorkflowVariables.EmailReceivedTrigger,
                    "Email",
                    from,
                    new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Candidate.Email"] = WorkflowJsonSerializer.SerializeToElement(from),
                        ["Conversation.LastMessage"] = WorkflowJsonSerializer.SerializeToElement(body ?? string.Empty),
                    },
                    cancellationToken);
                return;
            }

            var (execution, waitingEventType, resumeCorrelation) = match.Value;
            var conversationId = EmailTenantResolver.BuildConversationId(organizationId, from);
            var eventType = string.IsNullOrWhiteSpace(waitingEventType)
                ? WorkflowReplyWait.EmailReplyEvent
                : waitingEventType;

            var receiptExternalKey = string.IsNullOrWhiteSpace(providerMessageId)
                ? $"{organizationId:D}:{Guid.NewGuid():N}"
                : $"{organizationId:D}:{providerMessageId}";

            var workflowEvent = new WorkflowEvent
            {
                Id = WorkflowEventIdFactory.FromExternalKey("email.inbound", receiptExternalKey),
                OrganizationId = execution.OrganizationId,
                WorkflowExecutionId = execution.Id,
                EventType = eventType,
                CorrelationId = resumeCorrelation,
                EntityType = execution.EntityType,
                EntityId = execution.EntityId,
                OccurredAt = DateTimeOffset.UtcNow,
                Payload = WorkflowJsonSerializer.SerializeToElement(new
                {
                    messageId = providerMessageId,
                    conversationId,
                    organizationId,
                    from,
                    body,
                    channel = "email",
                }),
            };

            var resumed = await _engine.ResumeFromEventAsync(
                new ResumeWorkflowFromEventRequest
                {
                    ExecutionId = execution.Id,
                    Event = workflowEvent,
                },
                cancellationToken);

            var guard = 0;
            while (resumed.Status is WorkflowExecutionStatus.Pending or WorkflowExecutionStatus.Running)
            {
                if (++guard > 50)
                    break;
                resumed = await _engine.ExecuteCurrentStepAsync(
                    resumed.OrganizationId,
                    resumed.Id,
                    cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Email workflow inbound resume failed. Org={OrganizationId} From={From}",
                organizationId,
                from);
        }
    }
}
