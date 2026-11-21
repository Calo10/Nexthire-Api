using System.Text.Json;
using System.Text.Json.Serialization;
using nexthire_api.WorkflowAutomation.Conversations;
using nexthire_api.WorkflowAutomation.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexa.WorkflowAutomation.Engine;
using Nexa.WorkflowAutomation.Enums;
using Nexa.WorkflowAutomation.Exceptions;
using Nexa.WorkflowAutomation.Models.Events;
using Nexa.WorkflowAutomation.Models.Executions;
using Nexa.WorkflowAutomation.Serialization;

namespace nexthire_api.WorkflowAutomation.Controllers;

/// <summary>
/// Tenant inbound endpoint for Messenger's SendGrid Inbound Parse forwarder.
/// Same resume path as WhatsApp: interview first, then Waiting execution by org + prospect email.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/webhooks/email")]
public sealed class EmailInboundWebhookController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqlWorkflowExecutionRepository _executions;
    private readonly IWorkflowEngine _engine;
    private readonly IConversationInterviewService _interviews;
    private readonly IWorkflowTriggerService _triggers;
    private readonly ILogger<EmailInboundWebhookController> _logger;

    public EmailInboundWebhookController(
        SqlWorkflowExecutionRepository executions,
        IWorkflowEngine engine,
        IConversationInterviewService interviews,
        IWorkflowTriggerService triggers,
        ILogger<EmailInboundWebhookController> logger)
    {
        _executions = executions;
        _engine = engine;
        _interviews = interviews;
        _triggers = triggers;
        _logger = logger;
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        MessengerEmailInboundPayload? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<MessengerEmailInboundPayload>(
                Request.Body, JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Email inbound webhook JSON parse failed.");
            return BadRequest(new { message = "Invalid JSON payload." });
        }

        if (payload is null)
            return BadRequest(new { message = "Payload is required." });

        var from = EmailTenantResolver.NormalizeEmail(payload.From);
        if (string.IsNullOrWhiteSpace(from))
            return BadRequest(new { message = "from is required." });

        if (!EmailTenantResolver.TryResolveOrganizationId(payload.TenantId, out var organizationId))
        {
            _logger.LogWarning("Email inbound missing or invalid Messenger tenantId. To={To}", payload.To);
            return BadRequest(new { message = "tenantId is required and must be a valid organization id." });
        }

        var body = !string.IsNullOrWhiteSpace(payload.PlainText)
            ? payload.PlainText
            : payload.Html;

        var interview = await _interviews.TryProcessReplyAsync(
            organizationId,
            from,
            body,
            cancellationToken);
        if (interview.Handled)
        {
            if (!interview.Completed)
            {
                return Ok(new
                {
                    message = "Interview answer recorded; waiting for next reply.",
                    organizationId,
                    conversationId = interview.ConversationId,
                    executionId = interview.WorkflowExecutionId,
                    completed = false
                });
            }

            try
            {
                var resumedInterview = await _interviews.ResumeCompletedInterviewAsync(interview, cancellationToken);
                _logger.LogInformation(
                    "Email interview completed and workflow resumed. Org={OrganizationId}, From={From}, ExecutionId={ExecutionId}, Status={Status}",
                    organizationId,
                    from,
                    resumedInterview.Id,
                    resumedInterview.Status);

                return Ok(new
                {
                    message = "Interview completed; workflow resumed.",
                    organizationId,
                    conversationId = interview.ConversationId,
                    executionId = resumedInterview.Id,
                    status = resumedInterview.Status.ToString(),
                    completed = true
                });
            }
            catch (WorkflowInvalidStateException ex)
            {
                _logger.LogWarning(ex, "Email interview complete but could not resume execution {ExecutionId}",
                    interview.WorkflowExecutionId);
                return Conflict(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Email interview complete resume failed for Org={OrganizationId}, From={From}",
                    organizationId, from);
                return BadRequest(new { message = ex.Message });
            }
        }

        var match = await _executions.FindWaitingByOrganizationAndEmailAsync(
            organizationId,
            from,
            eventType: "email.reply.received",
            cancellationToken);

        match ??= await _executions.FindWaitingByOrganizationAndEmailAsync(
            organizationId,
            from,
            eventType: "email.received",
            cancellationToken);

        match ??= await _executions.FindWaitingByOrganizationAndEmailAsync(
            organizationId,
            from,
            eventType: null,
            cancellationToken);

        if (match is null)
        {
            await DispatchStartAsync(organizationId, from, payload, cancellationToken);
            _logger.LogInformation(
                "Email inbound: no Waiting workflow for Org={OrganizationId}, From={From}; start triggers dispatched",
                organizationId,
                from);
            return Ok(new { message = "No matching waiting workflow execution." });
        }

        var (execution, waitingEventType, resumeCorrelation) = match.Value;
        var eventType = string.IsNullOrWhiteSpace(waitingEventType)
            ? "email.reply.received"
            : waitingEventType;

        var eventPayload = WorkflowJsonSerializer.SerializeToElement(new
        {
            conversationId = from,
            organizationId,
            from,
            to = payload.To,
            subject = payload.Subject,
            body,
            channel = "email"
        });

        var receiptExternalKey = $"{organizationId:D}:{from}:{Guid.NewGuid():N}";

        var workflowEvent = new WorkflowEvent
        {
            Id = WorkflowEventIdFactory.FromExternalKey("email.inbound", receiptExternalKey),
            OrganizationId = execution.OrganizationId,
            WorkflowExecutionId = execution.Id,
            EventType = eventType,
            CorrelationId = resumeCorrelation,
            EntityType = execution.EntityType,
            EntityId = execution.EntityId,
            OccurredAt = payload.ReceivedAtUtc == default ? DateTimeOffset.UtcNow : payload.ReceivedAtUtc,
            Payload = eventPayload
        };

        try
        {
            var resumed = await _engine.ResumeFromEventAsync(
                new ResumeWorkflowFromEventRequest
                {
                    ExecutionId = execution.Id,
                    Event = workflowEvent
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

            _logger.LogInformation(
                "Email inbound resumed workflow. Org={OrganizationId}, From={From}, ExecutionId={ExecutionId}, Status={Status}",
                organizationId,
                from,
                resumed.Id,
                resumed.Status);

            return Ok(new
            {
                message = "Workflow resumed.",
                organizationId,
                conversationId = from,
                executionId = resumed.Id,
                status = resumed.Status.ToString()
            });
        }
        catch (WorkflowInvalidStateException ex)
        {
            _logger.LogWarning(ex, "Email inbound could not resume execution {ExecutionId}", execution.Id);
            return Conflict(new { message = ex.Message });
        }
        catch (WorkflowAutomationException ex)
        {
            _logger.LogError(ex, "Email inbound failed for Org={OrganizationId}, From={From}",
                organizationId, from);
            return BadRequest(new { message = ex.Message });
        }
    }

    private async Task DispatchStartAsync(
        Guid organizationId,
        string from,
        MessengerEmailInboundPayload payload,
        CancellationToken cancellationToken)
    {
        var entityId = Guid.NewGuid().ToString("D");
        var variables = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["Email.From"] = WorkflowJsonSerializer.SerializeToElement(from)
        };
        if (!string.IsNullOrWhiteSpace(payload.To))
            variables["Email.To"] = WorkflowJsonSerializer.SerializeToElement(payload.To);
        if (!string.IsNullOrWhiteSpace(payload.Subject))
            variables["Email.Subject"] = WorkflowJsonSerializer.SerializeToElement(payload.Subject);
        var body = !string.IsNullOrWhiteSpace(payload.PlainText) ? payload.PlainText : payload.Html;
        if (!string.IsNullOrWhiteSpace(body))
            variables["Email.Body"] = WorkflowJsonSerializer.SerializeToElement(body);

        try
        {
            await _triggers.DispatchAsync(
                organizationId,
                "email.received",
                "Email",
                entityId,
                variables,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Email inbound start trigger failed. Org={OrganizationId}, From={From}",
                organizationId, from);
        }
    }
}

public sealed class MessengerEmailInboundPayload
{
    [JsonPropertyName("tenantId")]
    public string? TenantId { get; set; }

    [JsonPropertyName("from")]
    public string? From { get; set; }

    [JsonPropertyName("to")]
    public string? To { get; set; }

    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    [JsonPropertyName("plainText")]
    public string? PlainText { get; set; }

    [JsonPropertyName("html")]
    public string? Html { get; set; }

    [JsonPropertyName("receivedAtUtc")]
    public DateTimeOffset ReceivedAtUtc { get; set; }
}
