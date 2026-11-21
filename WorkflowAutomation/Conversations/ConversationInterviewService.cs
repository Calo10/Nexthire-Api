using System.Text.Json;
using nexthire_api.Services;
using nexthire_api.WorkflowAutomation.Providers;
using nexthire_api.WorkflowAutomation.Repositories;
using Microsoft.Extensions.Logging;
using Nexa.WorkflowAutomation.Contracts.Persistence;
using Nexa.WorkflowAutomation.Contracts.Providers;
using Nexa.WorkflowAutomation.Engine;
using Nexa.WorkflowAutomation.Enums;
using Nexa.WorkflowAutomation.Models.Events;
using Nexa.WorkflowAutomation.Models.Executions;
using Nexa.WorkflowAutomation.Models.Variables;
using Nexa.WorkflowAutomation.Serialization;
using WorkflowEmail = Nexa.WorkflowAutomation.Contracts.Providers.IEmailService;

namespace nexthire_api.WorkflowAutomation.Conversations;

public interface IConversationInterviewService
{
    Task<StartInterviewResult> StartAsync(
        StartInterviewRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles an inbound message for an active interview when one exists.
    /// Does not resume the workflow until all questions are answered.
    /// </summary>
    Task<ProcessInterviewReplyResult> TryProcessReplyAsync(
        Guid organizationId,
        string recipient,
        string? body,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// After interview completion: resumes Waiting workflow and continues pending steps.
    /// </summary>
    Task<WorkflowExecution> ResumeCompletedInterviewAsync(
        ProcessInterviewReplyResult completion,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Interview orchestrator for <c>conversation.askQuestions</c>.
/// Messenger owns message history; this service only tracks interview state.
/// </summary>
public sealed class ConversationInterviewService : IConversationInterviewService
{
    private readonly IInterviewSessionRepository _sessions;
    private readonly IWhatsAppMessagingService _whatsApp;
    private readonly WorkflowEmail _email;
    private readonly IWorkflowExecutionRepository _executions;
    private readonly SqlWorkflowExecutionRepository _executionLookup;
    private readonly IWorkflowEngine _engine;
    private readonly ILogger<ConversationInterviewService> _logger;

    public ConversationInterviewService(
        IInterviewSessionRepository sessions,
        IWhatsAppMessagingService whatsApp,
        WorkflowEmail email,
        IWorkflowExecutionRepository executions,
        SqlWorkflowExecutionRepository executionLookup,
        IWorkflowEngine engine,
        ILogger<ConversationInterviewService> logger)
    {
        _sessions = sessions;
        _whatsApp = whatsApp;
        _email = email;
        _executions = executions;
        _executionLookup = executionLookup;
        _engine = engine;
        _logger = logger;
    }

    public async Task<StartInterviewResult> StartAsync(
        StartInterviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Recipient);
        ArgumentNullException.ThrowIfNull(request.Questions);

        if (request.Questions.Count == 0)
        {
            return new StartInterviewResult
            {
                Success = false,
                ErrorCode = "interview.questions.required",
                ErrorMessage = "At least one question is required."
            };
        }

        var channel = request.Channel.Trim().ToLowerInvariant();
        if (channel is not ("whatsapp" or "email"))
        {
            return new StartInterviewResult
            {
                Success = false,
                ErrorCode = "interview.channel.unsupported",
                ErrorMessage = $"Channel '{request.Channel}' is not supported. Use 'whatsapp' or 'email'."
            };
        }

        var recipient = NormalizeRecipient(channel, request.Recipient);
        var conversationId = channel == "whatsapp"
            ? WhatsAppTenantResolver.BuildConversationId(request.OrganizationId, recipient)
            : EmailTenantResolver.BuildConversationId(request.OrganizationId, recipient);
        var now = DateTimeOffset.UtcNow;

        var existing = await _sessions.GetByConversationIdAsync(conversationId, cancellationToken);
        if (existing is not null &&
            string.Equals(existing.Status, InterviewSessionStatus.Active, StringComparison.OrdinalIgnoreCase))
        {
            return new StartInterviewResult
            {
                Success = false,
                ErrorCode = "interview.already.active",
                ErrorMessage = $"An active interview already exists for conversation '{conversationId}'."
            };
        }

        var session = new InterviewSession
        {
            ConversationId = conversationId,
            OrganizationId = request.OrganizationId,
            WorkflowExecutionId = request.WorkflowExecutionId,
            Channel = channel,
            Recipient = recipient,
            QuestionIndex = 0,
            Answers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Questions = request.Questions.ToList(),
            CompletionEventType = string.IsNullOrWhiteSpace(request.CompletionEventType)
                ? "conversation.completed"
                : request.CompletionEventType.Trim(),
            Status = InterviewSessionStatus.Active,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now
        };

        await _sessions.UpsertAsync(session, cancellationToken);

        if (!string.IsNullOrWhiteSpace(request.IntroMessage))
        {
            var intro = await SendMessageAsync(
                channel,
                request.OrganizationId,
                recipient,
                conversationId,
                request.IntroMessage.Trim(),
                cancellationToken);
            if (!intro.Success)
            {
                await CancelSessionAsync(session, cancellationToken);
                return new StartInterviewResult
                {
                    Success = false,
                    ErrorCode = intro.ErrorCode ?? "interview.send.failed",
                    ErrorMessage = intro.ErrorMessage ?? "Failed to send intro message."
                };
            }
        }

        var firstQuestion = request.Questions[0].Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(firstQuestion))
        {
            await CancelSessionAsync(session, cancellationToken);
            return new StartInterviewResult
            {
                Success = false,
                ErrorCode = "interview.question.text.required",
                ErrorMessage = "First question text is required."
            };
        }

        var firstSend = await SendMessageAsync(
            channel,
            request.OrganizationId,
            recipient,
            conversationId,
            firstQuestion,
            cancellationToken);
        if (!firstSend.Success)
        {
            await CancelSessionAsync(session, cancellationToken);
            return new StartInterviewResult
            {
                Success = false,
                ErrorCode = firstSend.ErrorCode ?? "interview.send.failed",
                ErrorMessage = firstSend.ErrorMessage ?? "Failed to send first question."
            };
        }

        _logger.LogInformation(
            "Interview started. ConversationId={ConversationId}, ExecutionId={ExecutionId}, Questions={Count}",
            conversationId,
            request.WorkflowExecutionId,
            request.Questions.Count);

        return new StartInterviewResult
        {
            Success = true,
            ConversationId = conversationId
        };
    }

    public async Task<ProcessInterviewReplyResult> TryProcessReplyAsync(
        Guid organizationId,
        string recipient,
        string? body,
        CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty || string.IsNullOrWhiteSpace(recipient))
            return new ProcessInterviewReplyResult { Handled = false };

        var isEmail = recipient.Contains('@');
        InterviewSession? session;
        if (isEmail)
        {
            var normalizedEmail = EmailTenantResolver.NormalizeEmail(recipient);
            if (string.IsNullOrWhiteSpace(normalizedEmail))
                return new ProcessInterviewReplyResult { Handled = false };

            var conversationId = EmailTenantResolver.BuildConversationId(organizationId, normalizedEmail);
            session = await _sessions.GetActiveByConversationIdAsync(conversationId, cancellationToken)
                ?? await _sessions.GetActiveByOrganizationAndRecipientAsync(
                    organizationId,
                    normalizedEmail,
                    cancellationToken);

            if (session is null)
            {
                var legacy = await _sessions.GetActiveByConversationIdAsync(normalizedEmail, cancellationToken);
                if (legacy is not null && legacy.OrganizationId == organizationId)
                    session = legacy;
            }
        }
        else
        {
            var normalizedPhone = WhatsAppTenantResolver.NormalizePhone(recipient);
            if (string.IsNullOrWhiteSpace(normalizedPhone))
                return new ProcessInterviewReplyResult { Handled = false };

            var conversationId = WhatsAppTenantResolver.BuildConversationId(organizationId, normalizedPhone);

            session = await _sessions.GetActiveByConversationIdAsync(conversationId, cancellationToken)
                ?? await _sessions.GetActiveByOrganizationAndRecipientAsync(
                    organizationId,
                    normalizedPhone,
                    cancellationToken);

            // Legacy sessions created before multi-tenant conversation ids (phone-only PK).
            if (session is null)
            {
                var legacy = await _sessions.GetActiveByConversationIdAsync(normalizedPhone, cancellationToken);
                if (legacy is not null && legacy.OrganizationId == organizationId)
                    session = legacy;
            }
        }

        if (session is null)
            return new ProcessInterviewReplyResult { Handled = false };

        if (session.OrganizationId != organizationId)
        {
            _logger.LogWarning(
                "Interview session organization mismatch. ConversationId={ConversationId}, ExpectedOrg={Expected}, SessionOrg={Actual}",
                session.ConversationId,
                organizationId,
                session.OrganizationId);
            return new ProcessInterviewReplyResult { Handled = false };
        }

        if (session.QuestionIndex < 0 || session.QuestionIndex >= session.Questions.Count)
        {
            _logger.LogWarning(
                "Interview session has invalid questionIndex. ConversationId={ConversationId}, Index={Index}",
                session.ConversationId,
                session.QuestionIndex);
            return new ProcessInterviewReplyResult { Handled = false };
        }

        var current = session.Questions[session.QuestionIndex];
        var expectedIndex = session.QuestionIndex;
        var answerText = body ?? string.Empty;
        session.Answers[current.Id] = answerText;
        session.QuestionIndex += 1;
        session.UpdatedAt = DateTimeOffset.UtcNow;

        if (session.QuestionIndex < session.Questions.Count)
        {
            var advanced = await _sessions.TryAdvanceAsync(session, expectedIndex, cancellationToken);
            if (!advanced)
            {
                _logger.LogWarning(
                    "Interview reply lost race (stale questionIndex). ConversationId={ConversationId}, ExpectedIndex={Index}",
                    session.ConversationId,
                    expectedIndex);
                return new ProcessInterviewReplyResult
                {
                    Handled = true,
                    Completed = false,
                    OrganizationId = session.OrganizationId,
                    Recipient = session.Recipient,
                    ConversationId = session.ConversationId,
                    WorkflowExecutionId = session.WorkflowExecutionId
                };
            }

            var next = session.Questions[session.QuestionIndex];
            var nextText = next.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(nextText))
            {
                var send = await SendMessageAsync(
                    session.Channel,
                    session.OrganizationId,
                    session.Recipient,
                    session.ConversationId,
                    nextText,
                    cancellationToken);
                if (!send.Success)
                {
                    _logger.LogError(
                        "Failed to send next interview question. ConversationId={ConversationId}, Error={Error}",
                        session.ConversationId,
                        send.ErrorMessage);
                }
            }

            return new ProcessInterviewReplyResult
            {
                Handled = true,
                Completed = false,
                OrganizationId = session.OrganizationId,
                Recipient = session.Recipient,
                ConversationId = session.ConversationId,
                WorkflowExecutionId = session.WorkflowExecutionId
            };
        }

        session.Status = InterviewSessionStatus.Completed;
        var completed = await _sessions.TryAdvanceAsync(session, expectedIndex, cancellationToken);
        if (!completed)
        {
            _logger.LogWarning(
                "Interview completion lost race. ConversationId={ConversationId}, ExpectedIndex={Index}",
                session.ConversationId,
                expectedIndex);
            return new ProcessInterviewReplyResult
            {
                Handled = true,
                Completed = false,
                OrganizationId = session.OrganizationId,
                Recipient = session.Recipient,
                ConversationId = session.ConversationId,
                WorkflowExecutionId = session.WorkflowExecutionId
            };
        }

        await CopyAnswersToExecutionContextAsync(session, cancellationToken);

        _logger.LogInformation(
            "Interview completed. ConversationId={ConversationId}, ExecutionId={ExecutionId}",
            session.ConversationId,
            session.WorkflowExecutionId);

        var answersByVariable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var question in session.Questions)
        {
            if (string.IsNullOrWhiteSpace(question.Variable))
                continue;
            if (session.Answers.TryGetValue(question.Id, out var answer))
                answersByVariable[question.Variable.Trim()] = answer;
        }

        return new ProcessInterviewReplyResult
        {
            Handled = true,
            Completed = true,
            OrganizationId = session.OrganizationId,
            Recipient = session.Recipient,
            ConversationId = session.ConversationId,
            WorkflowExecutionId = session.WorkflowExecutionId,
            CompletionEventType = session.CompletionEventType,
            Answers = answersByVariable
        };
    }

    public async Task<WorkflowExecution> ResumeCompletedInterviewAsync(
        ProcessInterviewReplyResult completion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (!completion.Completed || string.IsNullOrWhiteSpace(completion.ConversationId))
        {
            throw new InvalidOperationException("Interview completion data is incomplete.");
        }

        if (completion.OrganizationId is null || completion.OrganizationId == Guid.Empty)
        {
            throw new InvalidOperationException("Interview completion OrganizationId is required.");
        }

        var organizationId = completion.OrganizationId.Value;
        var recipient = completion.Recipient?.Trim();

        (WorkflowExecution Execution, string WaitingEventType, string CorrelationId)? orgMatch = null;
        if (!string.IsNullOrWhiteSpace(recipient) && recipient.Contains('@'))
        {
            orgMatch = await _executionLookup.FindWaitingByOrganizationAndEmailAsync(
                organizationId,
                recipient,
                completion.CompletionEventType,
                cancellationToken);

            orgMatch ??= await _executionLookup.FindWaitingByOrganizationAndEmailAsync(
                organizationId,
                recipient,
                eventType: null,
                cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(recipient))
        {
            var phone = WhatsAppTenantResolver.NormalizePhone(recipient);
            if (!string.IsNullOrWhiteSpace(phone))
            {
                orgMatch = await _executionLookup.FindWaitingByOrganizationAndPhoneAsync(
                    organizationId,
                    phone,
                    completion.CompletionEventType,
                    cancellationToken);

                orgMatch ??= await _executionLookup.FindWaitingByOrganizationAndPhoneAsync(
                    organizationId,
                    phone,
                    eventType: null,
                    cancellationToken);
            }
        }

        (WorkflowExecution Execution, string WaitingEventType)? match = orgMatch is null
            ? null
            : (orgMatch.Value.Execution, orgMatch.Value.WaitingEventType);

        if (match is null)
        {
            match = await _executionLookup.FindWaitingByCorrelationAsync(
                completion.ConversationId,
                completion.CompletionEventType,
                cancellationToken,
                organizationId);

            match ??= await _executionLookup.FindWaitingByCorrelationAsync(
                completion.ConversationId,
                eventType: null,
                cancellationToken,
                organizationId);
        }

        if (match is null)
        {
            throw new InvalidOperationException(
                $"No waiting workflow for conversation '{completion.ConversationId}' in organization '{organizationId}'.");
        }

        var (waitingExecution, waitingEventType) = match.Value;
        if (waitingExecution.OrganizationId != organizationId)
        {
            throw new InvalidOperationException(
                $"Waiting workflow organization mismatch for conversation '{completion.ConversationId}'.");
        }

        var resumeCorrelation = orgMatch?.CorrelationId ?? completion.ConversationId;
        var eventType = string.IsNullOrWhiteSpace(completion.CompletionEventType)
            ? (string.IsNullOrWhiteSpace(waitingEventType) ? "conversation.completed" : waitingEventType)
            : completion.CompletionEventType;

        var eventPayload = WorkflowJsonSerializer.SerializeToElement(new
        {
            conversationId = completion.ConversationId,
            answers = completion.Answers
        });

        var workflowEvent = new WorkflowEvent
        {
            Id = WorkflowEventIdFactory.FromExternalKey(
                "conversation.completed",
                $"{organizationId:D}:{completion.ConversationId}"),
            OrganizationId = waitingExecution.OrganizationId,
            WorkflowExecutionId = waitingExecution.Id,
            EventType = eventType,
            CorrelationId = resumeCorrelation,
            EntityType = waitingExecution.EntityType,
            EntityId = waitingExecution.EntityId,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = eventPayload
        };

        var resumed = await _engine.ResumeFromEventAsync(
            new ResumeWorkflowFromEventRequest
            {
                ExecutionId = waitingExecution.Id,
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

        return resumed;
    }

    private async Task CopyAnswersToExecutionContextAsync(
        InterviewSession session,
        CancellationToken cancellationToken)
    {
        var execution = await _executions.GetByIdAsync(
            session.OrganizationId,
            session.WorkflowExecutionId,
            cancellationToken);

        if (execution is null)
        {
            _logger.LogWarning(
                "Cannot copy interview answers: execution {ExecutionId} not found.",
                session.WorkflowExecutionId);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var expectedRevision = execution.Revision;

        foreach (var question in session.Questions)
        {
            if (string.IsNullOrWhiteSpace(question.Variable))
                continue;

            if (!session.Answers.TryGetValue(question.Id, out var answer))
                continue;

            execution.Context.Variables.Set(
                question.Variable.Trim(),
                answer,
                WorkflowVariableScope.Execution,
                now);
        }

        execution.ConversationId ??= session.ConversationId;
        execution.Context.ConversationId ??= session.ConversationId;

        await _executions.UpdateAsync(execution, expectedRevision, cancellationToken);
    }

    private async Task CancelSessionAsync(InterviewSession session, CancellationToken cancellationToken)
    {
        session.Status = InterviewSessionStatus.Cancelled;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await _sessions.UpsertAsync(session, cancellationToken);
    }

    private async Task<(bool Success, string? ErrorCode, string? ErrorMessage)> SendMessageAsync(
        string channel,
        Guid organizationId,
        string recipient,
        string conversationId,
        string body,
        CancellationToken cancellationToken)
    {
        if (channel.Equals("email", StringComparison.OrdinalIgnoreCase))
        {
            var result = await _email.SendAsync(new EmailSendRequest
            {
                OrganizationId = organizationId,
                To = recipient,
                Subject = "NextHire",
                Body = body,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["correlationId"] = conversationId,
                    ["channel"] = "email"
                }
            }, cancellationToken);

            return (result.Success, result.ErrorCode, result.ErrorMessage);
        }

        var wa = await _whatsApp.SendAsync(new WhatsAppSendMessagingRequest
        {
            OrganizationId = organizationId,
            To = recipient,
            Body = body,
            CorrelationId = conversationId
        }, cancellationToken);

        return (wa.Success, wa.ErrorCode, wa.ErrorMessage);
    }

    private static string NormalizeRecipient(string channel, string recipient)
    {
        var trimmed = recipient.Trim();
        if (channel.Equals("email", StringComparison.OrdinalIgnoreCase))
            return EmailTenantResolver.NormalizeEmail(trimmed);

        if (trimmed.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["whatsapp:".Length..].Trim();

        return trimmed.StartsWith('+') ? trimmed : $"+{trimmed.TrimStart('+')}";
    }
}
