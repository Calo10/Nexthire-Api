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
/// Tenant inbound endpoint for Messenger's existing Twilio WhatsApp webhook forwarder.
/// Does not replace <c>POST /api/webhooks/twilio/whatsapp</c> in nexa-messenger.
/// Correlation is always <c>OrganizationId + Phone</c> (Messenger <c>tenantId</c> = org Guid).
/// Also advances active <c>conversation.askQuestions</c> interviews before generic wait-resume.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/webhooks/whatsapp")]
public sealed class WhatsAppInboundWebhookController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqlWorkflowExecutionRepository _executions;
    private readonly IWorkflowEngine _engine;
    private readonly IConversationInterviewService _interviews;
    private readonly IWorkflowTriggerService _triggers;
    private readonly ILogger<WhatsAppInboundWebhookController> _logger;

    public WhatsAppInboundWebhookController(
        SqlWorkflowExecutionRepository executions,
        IWorkflowEngine engine,
        IConversationInterviewService interviews,
        IWorkflowTriggerService triggers,
        ILogger<WhatsAppInboundWebhookController> logger)
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
        MessengerWhatsAppInboundPayload? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<MessengerWhatsAppInboundPayload>(
                Request.Body, JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "WhatsApp inbound webhook JSON parse failed.");
            return BadRequest(new { message = "Invalid JSON payload." });
        }

        if (payload is null)
            return BadRequest(new { message = "Payload is required." });

        var from = WhatsAppTenantResolver.NormalizePhone(payload.From);
        if (string.IsNullOrWhiteSpace(from))
            return BadRequest(new { message = "from is required." });

        // Same tenant resolution as LinkedIn Ads / Messenger: tenantId = organization Guid.
        if (!WhatsAppTenantResolver.TryResolveOrganizationId(payload.TenantId, out var organizationId))
        {
            _logger.LogWarning(
                "WhatsApp inbound missing or invalid Messenger tenantId. ProviderMessageId={ProviderMessageId}",
                payload.ProviderMessageId);
            return BadRequest(new { message = "tenantId is required and must be a valid organization id." });
        }

        // Active askQuestions interviews take precedence over generic wait-resume.
        var interview = await _interviews.TryProcessReplyAsync(
            organizationId,
            from,
            payload.Body,
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
                var resumed = await _interviews.ResumeCompletedInterviewAsync(interview, cancellationToken);
                _logger.LogInformation(
                    "Interview completed and workflow resumed. Org={OrganizationId}, From={From}, ExecutionId={ExecutionId}, Status={Status}",
                    organizationId,
                    from,
                    resumed.Id,
                    resumed.Status);

                return Ok(new
                {
                    message = "Interview completed; workflow resumed.",
                    organizationId,
                    conversationId = interview.ConversationId,
                    executionId = resumed.Id,
                    status = resumed.Status.ToString(),
                    completed = true
                });
            }
            catch (WorkflowInvalidStateException ex)
            {
                _logger.LogWarning(ex, "Interview complete but could not resume execution {ExecutionId}",
                    interview.WorkflowExecutionId);
                return Conflict(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Interview complete resume failed for Org={OrganizationId}, From={From}",
                    organizationId, from);
                return BadRequest(new { message = ex.Message });
            }
        }

        // Multi-tenant correlation: OrganizationId + Phone (never phone alone).
        var match = await _executions.FindWaitingByOrganizationAndPhoneAsync(
            organizationId,
            from,
            eventType: null,
            cancellationToken);

        if (match is null)
        {
            await DispatchStartAsync(organizationId, from, payload, cancellationToken);
            _logger.LogInformation(
                "WhatsApp inbound: no Waiting workflow for Org={OrganizationId}, From={From}, ProviderMessageId={ProviderMessageId}; start triggers dispatched",
                organizationId,
                from,
                payload.ProviderMessageId);
            return Ok(new { message = "No matching waiting workflow execution." });
        }

        var (execution, waitingEventType, resumeCorrelation) = match.Value;
        var eventType = string.IsNullOrWhiteSpace(waitingEventType)
            ? "whatsapp.received"
            : waitingEventType;

        var eventPayload = WorkflowJsonSerializer.SerializeToElement(new
        {
            messageId = payload.ProviderMessageId,
            conversationId = from,
            organizationId,
            from,
            to = payload.To,
            body = payload.Body,
            profileName = payload.ProfileName,
            channel = payload.Channel ?? "whatsapp"
        });

        var receiptExternalKey = string.IsNullOrWhiteSpace(payload.ProviderMessageId)
            ? $"{organizationId:D}:{Guid.NewGuid():N}"
            : $"{organizationId:D}:{payload.ProviderMessageId}";

        var workflowEvent = new WorkflowEvent
        {
            Id = WorkflowEventIdFactory.FromExternalKey("whatsapp.inbound", receiptExternalKey),
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
                "WhatsApp inbound resumed workflow. Org={OrganizationId}, From={From}, ExecutionId={ExecutionId}, Status={Status}",
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
            _logger.LogWarning(ex, "WhatsApp inbound could not resume execution {ExecutionId}", execution.Id);
            return Conflict(new { message = ex.Message });
        }
        catch (WorkflowAutomationException ex)
        {
            _logger.LogError(ex, "WhatsApp inbound failed for Org={OrganizationId}, From={From}",
                organizationId, from);
            return BadRequest(new { message = ex.Message });
        }
    }

    private async Task DispatchStartAsync(
        Guid organizationId,
        string from,
        MessengerWhatsAppInboundPayload payload,
        CancellationToken cancellationToken)
    {
        var entityId = string.IsNullOrWhiteSpace(payload.ProviderMessageId)
            ? Guid.NewGuid().ToString("D")
            : payload.ProviderMessageId.Trim();
        var variables = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["WhatsApp.From"] = WorkflowJsonSerializer.SerializeToElement(from)
        };
        if (!string.IsNullOrWhiteSpace(payload.Body))
            variables["WhatsApp.Body"] = WorkflowJsonSerializer.SerializeToElement(payload.Body);
        if (!string.IsNullOrWhiteSpace(payload.ProviderMessageId))
            variables["WhatsApp.MessageId"] = WorkflowJsonSerializer.SerializeToElement(payload.ProviderMessageId);

        try
        {
            await _triggers.DispatchAsync(
                organizationId,
                "whatsapp.received",
                "WhatsApp",
                entityId,
                variables,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WhatsApp inbound start trigger failed. Org={OrganizationId}, From={From}",
                organizationId, from);
        }
    }

    private sealed class MessengerWhatsAppInboundPayload
    {
        [JsonPropertyName("tenantId")]
        public string? TenantId { get; set; }

        [JsonPropertyName("channel")]
        public string? Channel { get; set; }

        [JsonPropertyName("provider")]
        public string? Provider { get; set; }

        [JsonPropertyName("providerMessageId")]
        public string? ProviderMessageId { get; set; }

        [JsonPropertyName("from")]
        public string? From { get; set; }

        [JsonPropertyName("to")]
        public string? To { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("profileName")]
        public string? ProfileName { get; set; }

        [JsonPropertyName("receivedAtUtc")]
        public DateTimeOffset ReceivedAtUtc { get; set; }
    }
}
