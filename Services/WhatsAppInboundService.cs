using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using nexthire_api.DTOs;
using nexthire_api.DTOs.Sourcing;
using nexthire_api.Exceptions;
using nexthire_api.Helpers;
using nexthire_api.Repositories;
using nexthire_api.Options;
using nexthire_api.Services.Sourcing;
using OpenAI.Chat;

namespace nexthire_api.Services;

public class WhatsAppInboundService : IWhatsAppInboundService
{
    private static readonly JsonSerializerOptions ApplySessionJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IWhatsAppInboundRepository _repository;
    private readonly IWhatsAppAiService _aiService;
    private readonly INexaMessengerWhatsAppClient _messengerClient;
    private readonly ITwilioOrgCredentialsResolver _twilioCredentials;
    private readonly IConfiguration _configuration;
    private readonly IPipelineRepository _pipeline;
    private readonly ISourcingService _sourcing;
    private readonly IJobBotQuestionRepository _jobBotQuestions;
    private readonly IWhatsAppApplyResumeService _applyResume;
    private readonly ICandidateRepository _candidates;
    private readonly IUserRepository _users;
    private readonly INexaClient _nexaClient;
    private readonly INexaAccessTokenResolver _nexaAccessTokens;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WhatsAppInboundService> _logger;

    public WhatsAppInboundService(
        IWhatsAppInboundRepository repository,
        IWhatsAppAiService aiService,
        INexaMessengerWhatsAppClient messengerClient,
        ITwilioOrgCredentialsResolver twilioCredentials,
        IConfiguration configuration,
        IPipelineRepository pipeline,
        ISourcingService sourcing,
        IJobBotQuestionRepository jobBotQuestions,
        IWhatsAppApplyResumeService applyResume,
        ICandidateRepository candidates,
        IUserRepository users,
        INexaClient nexaClient,
        INexaAccessTokenResolver nexaAccessTokens,
        IHttpClientFactory httpClientFactory,
        ILogger<WhatsAppInboundService> logger)
    {
        _repository = repository;
        _aiService = aiService;
        _messengerClient = messengerClient;
        _twilioCredentials = twilioCredentials;
        _configuration = configuration;
        _pipeline = pipeline;
        _sourcing = sourcing;
        _jobBotQuestions = jobBotQuestions;
        _applyResume = applyResume;
        _candidates = candidates;
        _users = users;
        _nexaClient = nexaClient;
        _nexaAccessTokens = nexaAccessTokens;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task ProcessInboundMessageAsync(WhatsAppInboundMessageDto inbound, CancellationToken cancellationToken)
    {
        WhatsAppInboundMediaHelper.EnsureMediaPopulated(inbound);
        inbound.Body ??= string.Empty;

        var rawTenant = inbound.TenantId;
        inbound.TenantId = await _repository.ResolveInboundTenantAsync(
            rawTenant,
            _configuration,
            cancellationToken);
        if (!string.Equals(rawTenant, inbound.TenantId, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "[WhatsAppBot] Tenant remapped Raw={RawTenantId} Resolved={ResolvedTenantId}",
                rawTenant,
                inbound.TenantId);
        }

        var normalizedPhoneNumber = WhatsAppPhoneNormalizer.NormalizeForConversation(inbound.From);
        if (inbound.ReceivedAtUtc == default)
            inbound.ReceivedAtUtc = DateTimeOffset.UtcNow;

        var saveResult = await _repository.SaveInboundMessageAsync(
            inbound,
            normalizedPhoneNumber,
            cancellationToken);

        if (!saveResult.MessageInserted)
        {
            _logger.LogInformation(
                "[WhatsAppBot] STOP: duplicate ProviderMessageId (no bot). TenantId={TenantId} MsgId={ProviderMessageId}",
                inbound.TenantId,
                inbound.ProviderMessageId);
            return;
        }

        var context = saveResult.Context;
        _logger.LogInformation(
            "[WhatsAppBot] Message saved. ConversationId={ConversationId} TenantId={TenantId} ContactPhone={ContactPhone}",
            context.ConversationId,
            inbound.TenantId,
            context.ContactPhone);

        if (!await _repository.IsConversationBotEnabledAsync(context.ConversationId, cancellationToken))
        {
            _logger.LogInformation(
                "[WhatsAppBot] STOP: recruiter took over (bot_enabled=0). ConversationId={ConversationId}",
                context.ConversationId);
            return;
        }

        var botConfig = await _repository.GetBotConfigAsync(inbound.TenantId, rawTenant);
        if (botConfig == null || !botConfig.IsEnabled)
        {
            _logger.LogWarning(
                "[WhatsAppBot] STOP: bot disabled or no whatsapp_bot_config row. TenantId={TenantId} ConversationId={ConversationId} ConfigNull={ConfigNull} IsEnabled={IsEnabled}",
                inbound.TenantId,
                context.ConversationId,
                botConfig == null,
                botConfig?.IsEnabled);
            return;
        }

        _logger.LogInformation("[WhatsAppBot] Bot config OK. TenantId={TenantId} IsEnabled=true", inbound.TenantId);

        if (NeedsHuman(inbound.Body, out var humanKeyword))
        {
            _logger.LogWarning(
                "[WhatsAppBot] STOP: needs-human keyword matched ({Keyword}). ConversationId={ConversationId}",
                humanKeyword,
                context.ConversationId);
            await _repository.MarkNeedsHumanAsync(context.ConversationId, cancellationToken);
            return;
        }

        var applyStateRow = await _repository.GetConversationApplyStateAsync(
            context.ConversationId,
            inbound.TenantId,
            cancellationToken);
        if (applyStateRow == null)
        {
            _logger.LogWarning(
                "[WhatsAppBot] GetConversationApplyState returned NULL (unexpected). Using empty state. ConversationId={ConversationId} TenantId={TenantId}",
                context.ConversationId,
                inbound.TenantId);
        }

        var applyState = applyStateRow ?? new WhatsAppConversationApplyStateDto();
        var looksApply = WhatsAppJobApplyIntentParser.LooksLikeApplyJobPostMessage(inbound.Body);
        var parsedOk = WhatsAppJobApplyIntentParser.TryParseJobPostId(inbound.Body, out var parsedJobId);
        _logger.LogInformation(
            "[WhatsAppBot] Apply intent: LooksLikeApply={LooksApply} TryParseJobId={ParsedOk} JobId={JobId} DbJobId={DbJobId} SessionJsonLen={SessionLen}",
            looksApply,
            parsedOk,
            parsedOk ? parsedJobId : null,
            applyState.JobId,
            applyState.BotApplySessionJson?.Length ?? 0);

        if (await TryHandleJobApplicationFlowAsync(inbound, context, applyState, cancellationToken))
        {
            _logger.LogInformation(
                "[WhatsAppBot] Job-application flow handled message. ConversationId={ConversationId}",
                context.ConversationId);
            return;
        }

        if (looksApply && !parsedOk)
        {
            _logger.LogInformation(
                "[WhatsAppBot] Invalid job GUID in apply phrase; sending error reply. ConversationId={ConversationId}",
                context.ConversationId);
            await SendAndPersistBotReplyAsync(
                inbound,
                context,
                WhatsAppBotMessages.InvalidJobId(JobLanguageCodes.Spanish),
                cancellationToken);
            return;
        }

        _logger.LogInformation(
            "[WhatsAppBot] Falling through to Azure OpenAI. ConversationId={ConversationId} HistoryLimit={HistoryLimit}",
            context.ConversationId,
            Math.Clamp(botConfig.HistoryMessageLimit, 2, 30));

        var historyLimit = Math.Clamp(botConfig.HistoryMessageLimit, 2, 30);
        var history = await _repository.GetRecentMessagesAsync(context.ConversationId, historyLimit);

        var systemPrompt = BuildSystemPrompt(botConfig.SystemPrompt);
        var aiMessages = BuildAiMessages(history);

        var aiResponse = await _aiService.GenerateReplyAsync(
            systemPrompt,
            aiMessages,
            botConfig.BotModel,
            cancellationToken);

        await SendAndPersistBotReplyAsync(inbound, context, aiResponse, cancellationToken);

        _logger.LogInformation(
            "[WhatsAppBot] AI reply sent. TenantId={TenantId} ConversationId={ConversationId}",
            inbound.TenantId,
            context.ConversationId);
    }

    public Task<IReadOnlyList<WhatsAppConversationListItemDto>> GetConversationsAsync(string tenantId, string? status)
    {
        return _repository.GetConversationsAsync(tenantId, status);
    }

    public Task<bool> MarkConversationReadAsync(Guid conversationId, string tenantId, CancellationToken cancellationToken = default)
    {
        return _repository.MarkConversationReadAsync(conversationId, tenantId, cancellationToken);
    }

    public async Task<WhatsAppCandidateConversationDto?> GetConversationByCandidateAsync(string tenantId, Guid candidateId)
    {
        var conversation = await _repository.GetConversationByCandidateAsync(tenantId, candidateId);
        if (conversation == null)
            return null;

        var messages = await GetConversationMessagesAsync(conversation.Id, tenantId);
        return new WhatsAppCandidateConversationDto
        {
            Conversation = conversation,
            Messages = messages
        };
    }

    public async Task<IReadOnlyList<WhatsAppConversationMessageDto>> GetConversationMessagesAsync(Guid conversationId, string tenantId)
    {
        var messages = (await _repository.GetConversationMessagesAsync(conversationId, tenantId)).ToList();
        await RefreshOutboundDeliveryAsync(tenantId, messages, CancellationToken.None);
        return messages;
    }

    public async Task<SendWhatsAppMessageResponseDto> SendMessageAsync(
        Guid conversationId,
        SendWhatsAppMessageRequestDto request,
        CancellationToken cancellationToken)
    {
        var context = await _repository.GetConversationSendContextAsync(conversationId, request.TenantId);
        if (context == null)
            throw new KeyNotFoundException("Conversation not found.");

        await EnsureCustomerCareWindowAsync(conversationId, cancellationToken);
        var twilio = await ResolveTwilioCredentialsAsync(request.TenantId, cancellationToken);

        var providerMessageId = await _messengerClient.SendMessageAsync(
            request.TenantId,
            context.PhoneNumber,
            request.Body,
            twilio,
            cancellationToken);

        var messageId = await _repository.SaveOutboundMessageForInboxAsync(
            conversationId,
            string.IsNullOrWhiteSpace(context.TenantId) ? request.TenantId : context.TenantId,
            "twilio",
            context.BusinessPhoneNumber,
            context.PhoneNumber,
            request.Body,
            providerMessageId,
            cancellationToken);

        return new SendWhatsAppMessageResponseDto
        {
            MessageId = messageId,
            Status = "sent"
        };
    }

    public async Task<SendWhatsAppMessageResponseDto> SendDirectMessageAsync(
        SendDirectWhatsAppMessageRequestDto request,
        CancellationToken cancellationToken)
    {
        var normalizedTo = WhatsAppPhoneNormalizer.NormalizeForConversation(request.To);
        var context = await _repository.EnsureConversationForDirectSendAsync(
            request.TenantId,
            normalizedTo,
            request.CandidateId,
            cancellationToken);

        await EnsureCustomerCareWindowAsync(context.Id, cancellationToken);
        var twilio = await ResolveTwilioCredentialsAsync(request.TenantId, cancellationToken);

        var providerMessageId = await _messengerClient.SendMessageAsync(
            request.TenantId,
            normalizedTo,
            request.Body,
            twilio,
            cancellationToken);

        var messageId = await _repository.SaveOutboundMessageForInboxAsync(
            context.Id,
            request.TenantId,
            "twilio",
            context.BusinessPhoneNumber,
            normalizedTo,
            request.Body,
            providerMessageId,
            cancellationToken);

        return new SendWhatsAppMessageResponseDto
        {
            MessageId = messageId,
            Status = "sent"
        };
    }

    public async Task<SendWhatsAppIntroductionResponseDto> SendIntroductionAsync(
        Guid orgId,
        Guid nexaUserId,
        Guid candidateId,
        CancellationToken cancellationToken)
    {
        if (orgId == Guid.Empty || nexaUserId == Guid.Empty)
            throw new UnauthorizedAccessException("Missing or invalid organization or user identity.");
        if (candidateId == Guid.Empty)
            throw new WhatsAppIntroductionException("candidateId is required.");

        // Candidate, Twilio connection, and conversation are scoped to the authenticated org only.
        var candidate = await _candidates.GetByIdAsync(orgId, candidateId);
        if (candidate == null)
            throw new WhatsAppIntroductionException("Candidate was not found.", 404);

        var phone = WhatsAppPhoneNormalizer.NormalizeForConversation(candidate.Phone);
        if (string.IsNullOrWhiteSpace(phone))
            throw new WhatsAppIntroductionException("Candidate phone is not available.");

        var recruiterName = await ResolveRecruiterNameAsync(orgId, nexaUserId, cancellationToken);
        var candidateName = await ResolveCandidateNameAsync(orgId, nexaUserId, candidate, cancellationToken);
        var organizationName = await ResolveOrganizationNameAsync(orgId, nexaUserId, cancellationToken);
        return await SendContentTemplateAsync(
            orgId,
            candidate,
            phone,
            recruiterName,
            candidateName,
            organizationName,
            static twilio => WhatsAppIntroductionRules.RequireContentSid(twilio.DefaultWhatsAppContentSid),
            WhatsAppIntroductionHistoryText.Format,
            "introduction",
            cancellationToken);
    }

    public async Task<SendWhatsAppIntroductionResponseDto> SendFollowUpAsync(
        Guid orgId,
        Guid nexaUserId,
        Guid candidateId,
        CancellationToken cancellationToken)
    {
        if (orgId == Guid.Empty || nexaUserId == Guid.Empty)
            throw new UnauthorizedAccessException("Missing or invalid organization or user identity.");
        if (candidateId == Guid.Empty)
            throw new WhatsAppIntroductionException("candidateId is required.");

        var candidate = await _candidates.GetByIdAsync(orgId, candidateId);
        if (candidate == null)
            throw new WhatsAppIntroductionException("Candidate was not found.", 404);

        var phone = WhatsAppPhoneNormalizer.NormalizeForConversation(candidate.Phone);
        if (string.IsNullOrWhiteSpace(phone))
            throw new WhatsAppIntroductionException("Candidate phone is not available.");

        var recruiterName = await ResolveRecruiterNameAsync(orgId, nexaUserId, cancellationToken);
        var candidateName = await ResolveCandidateNameAsync(orgId, nexaUserId, candidate, cancellationToken);
        var organizationName = await ResolveOrganizationNameAsync(orgId, nexaUserId, cancellationToken);
        return await SendContentTemplateAsync(
            orgId,
            candidate,
            phone,
            recruiterName,
            candidateName,
            organizationName,
            twilio =>
            {
                if (string.IsNullOrWhiteSpace(twilio.FollowUpWhatsAppContentSid))
                    throw new WhatsAppIntroductionException("Twilio FollowUpWhatsAppContentSid is not configured for this organization.");
                return twilio.FollowUpWhatsAppContentSid.Trim();
            },
            WhatsAppFollowUpHistoryText.Format,
            "follow-up",
            cancellationToken);
    }

    private async Task<SendWhatsAppIntroductionResponseDto> SendContentTemplateAsync(
        Guid orgId,
        CandidateDto candidate,
        string phone,
        string recruiterName,
        string candidateName,
        string organizationName,
        Func<TwilioOrgCredentials, string> resolveContentSid,
        Func<string, string, string, string> history,
        string purpose,
        CancellationToken cancellationToken)
    {
        var variables = WhatsAppIntroductionRules.BuildVariables(
            recruiterName,
            candidateName,
            organizationName);

        TwilioOrgCredentials twilio;
        try
        {
            twilio = await ResolveTwilioCredentialsAsync(orgId.ToString("D"), cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "WhatsApp {Purpose} Twilio configuration is unavailable. OrgId={OrgId}", purpose, orgId);
            throw new WhatsAppIntroductionException(WhatsAppIntroductionRules.SafeTwilioConfigurationMessage(ex.Message));
        }

        var contentSid = resolveContentSid(twilio);
        var historyText = history(recruiterName, candidateName, organizationName);
        var tenantId = orgId.ToString("D");
        var context = await _repository.EnsureConversationForDirectSendAsync(
            tenantId,
            phone,
            candidate.Id,
            cancellationToken);

        string? providerMessageId;
        try
        {
            providerMessageId = await _messengerClient.SendTemplateMessageAsync(
                tenantId,
                phone,
                contentSid,
                variables,
                twilio,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "WhatsApp {Purpose} send failed. OrgId={OrgId} CandidateId={CandidateId} ContentSid={ContentSid}",
                purpose,
                orgId,
                candidate.Id,
                contentSid);
            throw new WhatsAppIntroductionException("Failed to send WhatsApp template.", 502);
        }

        var messageId = await _repository.SaveOutboundMessageForInboxAsync(
            context.Id,
            tenantId,
            "twilio",
            context.BusinessPhoneNumber,
            phone,
            historyText,
            providerMessageId,
            cancellationToken);

        return new SendWhatsAppIntroductionResponseDto
        {
            MessageId = messageId,
            Status = "sent",
            Body = historyText
        };
    }

    public Task<bool> UpdateConversationAsync(
        Guid conversationId,
        string tenantId,
        UpdateWhatsAppConversationRequestDto request,
        CancellationToken cancellationToken)
    {
        return _repository.UpdateConversationAsync(conversationId, tenantId, request, cancellationToken);
    }

    private static string BuildSystemPrompt(string? configuredPrompt)
    {
        var basePrompt = string.IsNullOrWhiteSpace(configuredPrompt)
            ? "You are the NextHire WhatsApp recruiting assistant."
            : configuredPrompt.Trim();

        return $"""
                {basePrompt}

                Guardrails:
                - Ask one question at a time.
                - Stay strictly on recruiting and hiring topics.
                - Keep answers short and practical.
                - Never invent job details, salary, requirements, or process steps.
                - If information is unavailable, say so and ask a clarifying recruiting question.
                - If the user asks for a human recruiter, respond briefly and indicate handoff.
                """;
    }

    private static List<ChatMessage> BuildAiMessages(IReadOnlyList<WhatsAppMessageHistoryItemDto> history)
    {
        var messages = new List<ChatMessage>(history.Count);
        foreach (var item in history)
        {
            if (string.IsNullOrWhiteSpace(item.Body))
                continue;

            if (string.Equals(item.Direction, "outbound", StringComparison.OrdinalIgnoreCase))
                messages.Add(new AssistantChatMessage(item.Body));
            else
                messages.Add(new UserChatMessage(item.Body));
        }

        return messages;
    }

    private static bool NeedsHuman(string? text, out string? matchedKeyword)
    {
        matchedKeyword = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var value = text.ToLowerInvariant();
        string[] keys =
        [
            "human",
            "person",
            "agent",
            "recruiter",
            "reclutador",
            "reclutadora",
            "asesor",
            "asesora",
            "humano",
            "humana",
            "agente",
            "operador",
            "operadora"
        ];
        foreach (var k in keys)
        {
            if (!value.Contains(k, StringComparison.Ordinal))
                continue;
            matchedKeyword = k;
            return true;
        }

        return false;
    }

    private async Task SendAndPersistBotReplyAsync(
        WhatsAppInboundMessageDto inbound,
        WhatsAppConversationContext context,
        string reply,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation(
                "[WhatsAppBot] Sending outbound via messenger. TenantId={TenantId} To={To} ReplyLen={Len}",
                inbound.TenantId,
                context.ContactPhone,
                reply.Length);

            var twilio = await ResolveTwilioCredentialsAsync(inbound.TenantId, cancellationToken);

            var outboundProviderMessageId = await _messengerClient.SendMessageAsync(
                inbound.TenantId,
                context.ContactPhone,
                reply,
                twilio,
                cancellationToken);

            await _repository.SaveOutboundMessageAsync(
                context.ConversationId,
                inbound.TenantId,
                inbound.Provider,
                context.BusinessPhone,
                context.ContactPhone,
                reply,
                outboundProviderMessageId,
                cancellationToken);

            _logger.LogInformation(
                "[WhatsAppBot] Outbound saved. ConversationId={ConversationId} ProviderMsgId={ProviderMsgId}",
                context.ConversationId,
                outboundProviderMessageId ?? "(null)");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[WhatsAppBot] FAILED to send/persist outbound. TenantId={TenantId} ConversationId={ConversationId} To={To}",
                inbound.TenantId,
                context.ConversationId,
                context.ContactPhone);
            throw;
        }
    }

    private async Task<bool> TryHandleJobApplicationFlowAsync(
        WhatsAppInboundMessageDto inbound,
        WhatsAppConversationContext context,
        WhatsAppConversationApplyStateDto dbState,
        CancellationToken cancellationToken)
    {
        var session = DeserializeApplySession(dbState.BotApplySessionJson);
        var jobId = dbState.JobId;

        if (WhatsAppJobApplyIntentParser.TryParseJobPostId(inbound.Body, out var parsedJobId))
        {
            var isMidFlow = WhatsAppApplySessionSteps.IsCollecting(session.Step);
            var shouldStartNewFlow = isMidFlow
                ? !jobId.HasValue || parsedJobId != jobId.Value
                : !string.Equals(session.Step, WhatsAppApplySessionSteps.Done, StringComparison.OrdinalIgnoreCase)
                  || !jobId.HasValue
                  || parsedJobId != jobId.Value;

            _logger.LogInformation(
                "[WhatsAppBot] Parsed job post id={ParsedJobId} Step={Step} IsMidFlow={Mid} ShouldStartNew={StartNew} DbJobId={DbJobId}",
                parsedJobId,
                session.Step,
                isMidFlow,
                shouldStartNewFlow,
                jobId);

            if (!shouldStartNewFlow && isMidFlow && jobId.HasValue && parsedJobId == jobId.Value)
            {
                var jobContext = await _pipeline.GetJobBotContextAsync(jobId.Value);
                var questions = jobContext == null
                    ? []
                    : await LoadActiveQuestionsAsync(jobContext.OrgId, jobId.Value, cancellationToken);
                var prompt = await GetCurrentQuestionPromptAsync(session, questions, cancellationToken);
                _logger.LogInformation(
                    "[WhatsAppBot] Repeated apply phrase mid-flow; re-prompt step {Step}",
                    session.Step);
                await SendAndPersistBotReplyAsync(
                    inbound,
                    context,
                    prompt,
                    cancellationToken);

                return true;
            }

            if (shouldStartNewFlow)
                return await StartJobApplicationFlowAsync(inbound, context, parsedJobId, cancellationToken);
        }

        if (!WhatsAppApplySessionSteps.IsCollecting(session.Step))
        {
            _logger.LogInformation(
                "[WhatsAppBot] Flow not collecting (step={Step}); not handling here.",
                session.Step);
            return false;
        }

        if (!jobId.HasValue)
        {
            _logger.LogWarning(
                "[WhatsAppBot] Apply flow step={Step} but no job_id in DB. ConversationId={ConversationId}",
                session.Step,
                context.ConversationId);
            return false;
        }

        if (string.IsNullOrWhiteSpace(session.Language))
            session.Language = JobLanguageCodes.Spanish;

        return await AdvanceJobApplicationFlowAsync(
            inbound,
            context,
            jobId.Value,
            session,
            cancellationToken);
    }

    private async Task<bool> StartJobApplicationFlowAsync(
        WhatsAppInboundMessageDto inbound,
        WhatsAppConversationContext context,
        Guid jobPostId,
        CancellationToken cancellationToken)
    {
        var jobContext = await _pipeline.GetJobBotContextAsync(jobPostId);
        _logger.LogInformation(
            "[WhatsAppBot] Start flow: TenantId={TenantId} JobPostId={JobPostId} JobOrgId={JobOrgId} Language={Language}",
            inbound.TenantId,
            jobPostId,
            jobContext?.OrgId,
            jobContext?.Language);
        if (jobContext == null)
        {
            _logger.LogWarning(
                "[WhatsAppBot] Job not found in database. JobPostId={JobPostId} TenantId={TenantId}",
                jobPostId,
                inbound.TenantId);
            await SendAndPersistBotReplyAsync(
                inbound,
                context,
                WhatsAppBotMessages.JobNotFound(JobLanguageCodes.Spanish),
                cancellationToken);
            return true;
        }

        if (Guid.TryParse(inbound.TenantId.Trim(), out var tenantOrgId) && jobContext.OrgId != tenantOrgId)
        {
            _logger.LogWarning(
                "[WhatsAppBot] Job org {JobOrgId} differs from resolved tenant org {TenantOrgId}; apply flow continues using job org.",
                jobContext.OrgId,
                tenantOrgId);
        }

        var language = WhatsAppBotMessages.NormalizeLanguage(jobContext.Language);
        var session = new WhatsAppApplySessionState
        {
            Step = WhatsAppApplySessionSteps.FixedFields,
            CurrentFixedFieldIndex = 0,
            Language = language
        };

        await _repository.UpdateConversationApplyStateAsync(
            context.ConversationId,
            inbound.TenantId,
            jobPostId,
            SerializeApplySession(session),
            cancellationToken);

        var firstFieldKey = WhatsAppApplyFixedFieldKeys.Order[0];
        await SendAndPersistBotReplyAsync(
            inbound,
            context,
            WhatsAppApplyFixedFields.GetPrompt(firstFieldKey, isFirstQuestion: true, language),
            cancellationToken);

        _logger.LogInformation(
            "[WhatsAppBot] Job application flow started. TenantId={TenantId} ConversationId={ConversationId} JobId={JobId}",
            inbound.TenantId,
            context.ConversationId,
            jobPostId);

        return true;
    }

    private async Task<bool> AdvanceJobApplicationFlowAsync(
        WhatsAppInboundMessageDto inbound,
        WhatsAppConversationContext context,
        Guid jobPostId,
        WhatsAppApplySessionState session,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(inbound.TenantId.Trim(), out var tenantOrgId))
            tenantOrgId = Guid.Empty;

        var jobOrgId = await _pipeline.GetJobOrgIdAsync(jobPostId);
        if (!jobOrgId.HasValue)
            return false;

        var orgId = jobOrgId.Value;
        if (tenantOrgId != Guid.Empty && orgId != tenantOrgId)
        {
            _logger.LogWarning(
                "[WhatsAppBot] Apply step {Step}: using job org {JobOrgId} (tenant org {TenantOrgId}).",
                session.Step,
                orgId,
                tenantOrgId);
        }

        if (string.Equals(session.Step, WhatsAppApplySessionSteps.FixedFields, StringComparison.OrdinalIgnoreCase))
        {
            return await AdvanceFixedFieldsAsync(
                inbound,
                context,
                jobPostId,
                orgId,
                session,
                cancellationToken);
        }

        if (string.Equals(session.Step, WhatsAppApplySessionSteps.CustomQuestions, StringComparison.OrdinalIgnoreCase))
        {
            return await AdvanceCustomQuestionsAsync(
                inbound,
                context,
                jobPostId,
                orgId,
                session,
                cancellationToken);
        }

        return false;
    }

    private async Task<bool> AdvanceFixedFieldsAsync(
        WhatsAppInboundMessageDto inbound,
        WhatsAppConversationContext context,
        Guid jobPostId,
        Guid orgId,
        WhatsAppApplySessionState session,
        CancellationToken cancellationToken)
    {
        var fieldKey = WhatsAppApplyFixedFields.GetCurrentFieldKey(session);
        if (fieldKey == null)
            return false;

        if (!WhatsAppApplyResponseValidator.TryValidateFixedField(
                fieldKey,
                inbound.Body,
                session.Language,
                out var value,
                out var validationError))
        {
            await SendAndPersistBotReplyAsync(
                inbound,
                context,
                validationError!,
                cancellationToken);
            return true;
        }

        WhatsAppApplyFixedFields.SetFieldValue(session, fieldKey, value!);
        session.CurrentFixedFieldIndex++;

        if (session.CurrentFixedFieldIndex < WhatsAppApplyFixedFieldKeys.Order.Length)
        {
            await PersistApplySessionAsync(context, inbound.TenantId, jobPostId, session, cancellationToken);
            var nextFieldKey = WhatsAppApplyFixedFieldKeys.Order[session.CurrentFixedFieldIndex];
            await SendAndPersistBotReplyAsync(
                inbound,
                context,
                WhatsAppApplyFixedFields.GetPrompt(nextFieldKey, isFirstQuestion: false, session.Language),
                cancellationToken);
            return true;
        }

        var questions = await LoadActiveQuestionsAsync(orgId, jobPostId, cancellationToken);
        if (questions.Count == 0)
            return await CompleteApplicationAsync(inbound, context, jobPostId, orgId, session, questions, cancellationToken);

        session.Step = WhatsAppApplySessionSteps.CustomQuestions;
        session.CurrentQuestionIndex = 0;
        await PersistApplySessionAsync(context, inbound.TenantId, jobPostId, session, cancellationToken);
        await SendAndPersistBotReplyAsync(
            inbound,
            context,
            questions[0].QuestionText.Trim(),
            cancellationToken);
        return true;
    }

    private async Task<bool> AdvanceCustomQuestionsAsync(
        WhatsAppInboundMessageDto inbound,
        WhatsAppConversationContext context,
        Guid jobPostId,
        Guid orgId,
        WhatsAppApplySessionState session,
        CancellationToken cancellationToken)
    {
        var questions = await LoadActiveQuestionsAsync(orgId, jobPostId, cancellationToken);
        if (questions.Count == 0)
            return await CompleteApplicationAsync(inbound, context, jobPostId, orgId, session, questions, cancellationToken);

        if (session.CurrentQuestionIndex >= questions.Count)
        {
            _logger.LogWarning(
                "[WhatsAppBot] Session question index {Index} out of range ({Count}). ConversationId={ConversationId}",
                session.CurrentQuestionIndex,
                questions.Count,
                context.ConversationId);
            return false;
        }

        var currentQuestion = questions[session.CurrentQuestionIndex];
        if (!WhatsAppApplyResponseValidator.TryValidateAnswer(
                currentQuestion,
                inbound,
                session.Language,
                out var answerValue,
                out var validationError))
        {
            await SendAndPersistBotReplyAsync(
                inbound,
                context,
                validationError!,
                cancellationToken);
            return true;
        }

        if (string.Equals(currentQuestion.AnswerType, JobBotQuestionAnswerTypes.File, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                answerValue = await _applyResume.UploadResumeFromInboundAsync(orgId, inbound, cancellationToken);
            }
            catch (Exception ex)
            {
                var failureCode = WhatsAppResumeUploadDiagnostics.ClassifyUploadFailure(ex);
                _logger.LogError(
                    ex,
                    "[WhatsAppResume] FAILED OrgId={OrgId} JobId={JobId} ConversationId={ConversationId} FailureCode={FailureCode} Media={MediaDiagnostics} Message={Message}",
                    orgId,
                    jobPostId,
                    context.ConversationId,
                    failureCode,
                    WhatsAppResumeUploadDiagnostics.DescribeInboundMedia(inbound),
                    ex.Message);

                var reply = WhatsAppBotMessages.ResumeUploadError(session.Language, ex);
                await SendAndPersistBotReplyAsync(
                    inbound,
                    context,
                    reply,
                    cancellationToken);
                return true;
            }
        }

        session.Answers.Add(new WhatsAppApplySessionAnswer
        {
            QuestionId = currentQuestion.Id,
            Key = currentQuestion.QuestionKey,
            Label = currentQuestion.QuestionText,
            Value = answerValue!,
            AnsweredAtUtc = DateTimeOffset.UtcNow
        });
        session.CurrentQuestionIndex++;

        if (session.CurrentQuestionIndex < questions.Count)
        {
            await PersistApplySessionAsync(context, inbound.TenantId, jobPostId, session, cancellationToken);
            await SendAndPersistBotReplyAsync(
                inbound,
                context,
                questions[session.CurrentQuestionIndex].QuestionText.Trim(),
                cancellationToken);
            return true;
        }

        return await CompleteApplicationAsync(inbound, context, jobPostId, orgId, session, questions, cancellationToken);
    }

    private async Task<bool> CompleteApplicationAsync(
        WhatsAppInboundMessageDto inbound,
        WhatsAppConversationContext context,
        Guid jobPostId,
        Guid orgId,
        WhatsAppApplySessionState session,
        IReadOnlyList<JobBotQuestionDto> questions,
        CancellationToken cancellationToken)
    {
        var previousStep = session.Step;
        var dynamicAnswersJson = session.Answers.Count > 0
            ? WhatsAppDynamicApplyHelper.BuildDynamicAnswersJson(session.Answers)
            : null;
        var (_, resumeUrl) = WhatsAppDynamicApplyHelper.ExtractLeadFieldsFromAnswers(session.Answers, questions);
        try
        {
            resumeUrl = await _applyResume.EnsureResumeDocumentIdAsync(orgId, resumeUrl, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to resolve resume document id before creating sourcing lead. OrgId={OrgId} JobId={JobId}",
                orgId,
                jobPostId);
            resumeUrl = null;
        }

        WhatsAppApplyFixedFields.SyncFullName(session);

        try
        {
            var payload = JsonSerializer.Serialize(
                new
                {
                    whatsappConversationId = context.ConversationId,
                    channel = "whatsapp",
                    collectedAtUtc = DateTimeOffset.UtcNow
                },
                ApplySessionJsonOptions);

            var resumeSummaryTask = _sourcing.PrefetchResumeSummaryAsync(orgId, resumeUrl, cancellationToken);

            var lead = await _sourcing.CreateLeadAsync(
                orgId,
                new CreateSourcingLeadRequestDto
                {
                    JobId = jobPostId,
                    SourceTypeCode = WhatsAppJobApplyIntentParser.SourceTypeWhatsApp,
                    FirstName = session.FirstName,
                    LastName = session.LastName,
                    FullName = session.FullName,
                    Email = session.Email,
                    Phone = context.ContactPhone,
                    ResumeUrl = resumeUrl,
                    DynamicAnswersJson = dynamicAnswersJson,
                    RawPayloadJson = payload
                });

            await _sourcing.ScoreLeadFitAsync(orgId, lead, resumeSummaryTask, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to create sourcing lead from WhatsApp apply flow. OrgId: {OrgId}, JobId: {JobId}, ConversationId: {ConversationId}",
                orgId,
                jobPostId,
                context.ConversationId);

            if (string.Equals(previousStep, WhatsAppApplySessionSteps.CustomQuestions, StringComparison.OrdinalIgnoreCase)
                && session.Answers.Count > 0)
            {
                session.Answers.RemoveAt(session.Answers.Count - 1);
                session.CurrentQuestionIndex = session.Answers.Count;
            }

            session.Step = previousStep;
            await PersistApplySessionAsync(context, inbound.TenantId, jobPostId, session, cancellationToken);
            await SendAndPersistBotReplyAsync(
                inbound,
                context,
                WhatsAppBotMessages.SaveApplicationError(session.Language),
                cancellationToken);
            return true;
        }

        session.Step = WhatsAppApplySessionSteps.Done;
        await _repository.UpdateConversationApplyStateAsync(
            context.ConversationId,
            inbound.TenantId,
            jobPostId,
            SerializeApplySession(session),
            cancellationToken);

        await SendAndPersistBotReplyAsync(
            inbound,
            context,
            WhatsAppBotMessages.ApplicationComplete(session.Language),
            cancellationToken);

        _logger.LogInformation(
            "WhatsApp job application flow completed and lead created. OrgId: {OrgId}, JobId: {JobId}, ConversationId: {ConversationId}",
            orgId,
            jobPostId,
            context.ConversationId);

        return true;
    }

    private async Task<IReadOnlyList<JobBotQuestionDto>> LoadActiveQuestionsAsync(
        Guid orgId,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var questions = await _jobBotQuestions.ListByJobAsync(orgId, jobId, includeInactive: false);
        return questions
            .OrderBy(q => q.SortOrder)
            .ThenBy(q => q.CreatedAt)
            .ThenBy(q => q.Id)
            .ToList();
    }

    private Task<string> GetCurrentQuestionPromptAsync(
        WhatsAppApplySessionState session,
        IReadOnlyList<JobBotQuestionDto> questions,
        CancellationToken cancellationToken)
    {
        if (string.Equals(session.Step, WhatsAppApplySessionSteps.FixedFields, StringComparison.OrdinalIgnoreCase))
        {
            var fieldKey = WhatsAppApplyFixedFields.GetCurrentFieldKey(session)
                           ?? WhatsAppApplyFixedFieldKeys.FirstName;
            return Task.FromResult(WhatsAppApplyFixedFields.GetRepeatPrompt(fieldKey, session.Language));
        }

        if (questions.Count == 0)
            return Task.FromResult(WhatsAppBotMessages.ContinueApplication(session.Language));

        var index = Math.Clamp(session.CurrentQuestionIndex, 0, questions.Count - 1);
        return Task.FromResult(
            WhatsAppBotMessages.ContinueWithQuestion(session.Language, questions[index].QuestionText));
    }

    private Task PersistApplySessionAsync(
        WhatsAppConversationContext context,
        string tenantId,
        Guid jobPostId,
        WhatsAppApplySessionState session,
        CancellationToken cancellationToken)
    {
        return _repository.UpdateConversationApplyStateAsync(
            context.ConversationId,
            tenantId,
            jobPostId,
            SerializeApplySession(session),
            cancellationToken);
    }

    private static WhatsAppApplySessionState DeserializeApplySession(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new WhatsAppApplySessionState();

        try
        {
            return JsonSerializer.Deserialize<WhatsAppApplySessionState>(json, ApplySessionJsonOptions)
                   ?? new WhatsAppApplySessionState();
        }
        catch
        {
            return new WhatsAppApplySessionState();
        }
    }

    private static string SerializeApplySession(WhatsAppApplySessionState session) =>
        JsonSerializer.Serialize(session, ApplySessionJsonOptions);

    private async Task<string> ResolveRecruiterNameAsync(
        Guid orgId,
        Guid nexaUserId,
        CancellationToken cancellationToken)
    {
        var local = await _users.GetByNexaUserIdAsync(orgId, nexaUserId);
        var displayName = WhatsAppIntroductionRules.PersonNameOrNull(local?.DisplayName, local?.Email);
        if (!string.IsNullOrWhiteSpace(displayName))
            return displayName;

        var localName = WhatsAppIntroductionRules.PersonNameOrNull(
            local == null ? null : WhatsAppIntroductionRules.JoinPersonName(local.FirstName, local.LastName),
            local?.Email);
        if (!string.IsNullOrWhiteSpace(localName))
            return localName;

        var nexaName = await TryNexaPersonNameAsync(
            orgId,
            nexaUserId,
            member => member.UserId == nexaUserId,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(nexaName))
            return nexaName;

        throw new WhatsAppIntroductionException("Recruiter name is not available.");
    }

    private async Task<string> ResolveCandidateNameAsync(
        Guid orgId,
        Guid nexaUserId,
        CandidateDto candidate,
        CancellationToken cancellationToken)
    {
        var storedName = WhatsAppIntroductionRules.PersonNameOrNull(
            WhatsAppIntroductionRules.JoinPersonName(candidate.FirstName, candidate.LastName),
            candidate.Email);
        if (!string.IsNullOrWhiteSpace(storedName))
            return storedName;

        if (!string.IsNullOrWhiteSpace(candidate.Email))
        {
            var user = await _users.GetByEmailAsync(orgId, candidate.Email);
            var userName = WhatsAppIntroductionRules.PersonNameOrNull(
                user == null ? null : WhatsAppIntroductionRules.JoinPersonName(user.FirstName, user.LastName),
                user?.Email ?? candidate.Email);
            if (!string.IsNullOrWhiteSpace(userName))
                return userName;

            var nexaName = await TryNexaPersonNameAsync(
                orgId,
                nexaUserId,
                member => string.Equals(member.Email, candidate.Email, StringComparison.OrdinalIgnoreCase),
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(nexaName))
                return nexaName;
        }

        var fallback = WhatsAppIntroductionRules.JoinPersonName(candidate.FirstName, candidate.LastName);
        if (!string.IsNullOrWhiteSpace(fallback))
            return fallback;

        throw new WhatsAppIntroductionException("Candidate name is not available.");
    }

    private async Task<string?> TryNexaPersonNameAsync(
        Guid orgId,
        Guid nexaUserId,
        Func<NexaOrgMemberDto, bool> match,
        CancellationToken cancellationToken)
    {
        var token = await _nexaAccessTokens.GetValidAccessTokenAsync(
            nexaUserId.ToString(),
            cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
            return null;

        try
        {
            var members = await _nexaClient.ListOrgMembersAsync(orgId, token, cancellationToken);
            var member = members.FirstOrDefault(match);
            return WhatsAppIntroductionRules.PersonNameOrNull(member?.FullName, member?.Email);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "WhatsApp introduction could not load a person name from Nexa members. OrgId={OrgId}",
                orgId);
            return null;
        }
    }

    private async Task<string> ResolveOrganizationNameAsync(
        Guid orgId,
        Guid nexaUserId,
        CancellationToken cancellationToken)
    {
        var token = await _nexaAccessTokens.GetValidAccessTokenAsync(
            nexaUserId.ToString(),
            cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning(
                "WhatsApp introduction organization lookup skipped because the Nexa access token is missing. OrgId={OrgId}",
                orgId);
            throw new WhatsAppIntroductionException("Organization name is not available.");
        }

        try
        {
            var organization = await _nexaClient.GetOrganizationAsync(orgId, token, cancellationToken);
            if (string.IsNullOrWhiteSpace(organization?.Name))
                throw new WhatsAppIntroductionException("Organization name is not available.");

            return organization.Name.Trim();
        }
        catch (WhatsAppIntroductionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "WhatsApp introduction organization lookup failed. OrgId={OrgId}", orgId);
            throw new WhatsAppIntroductionException("Organization name is not available.");
        }
    }

    private async Task RefreshOutboundDeliveryAsync(
        string tenantId,
        List<WhatsAppConversationMessageDto> messages,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = messages
            .Where(message => WhatsAppDeliveryRules.NeedsRefresh(
                message.Direction,
                message.ProviderMessageId,
                message.DeliveryStatus,
                message.DeliveryCheckedAtUtc,
                now))
            .Take(8)
            .ToList();
        if (pending.Count == 0)
            return;

        TwilioOrgCredentials credentials;
        try
        {
            credentials = await ResolveTwilioCredentialsAsync(tenantId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "WhatsApp delivery status was not refreshed because Twilio credentials are unavailable.");
            return;
        }

        await Task.WhenAll(pending.Select(message => RefreshOneDeliveryAsync(credentials, message, cancellationToken)));
    }

    private async Task RefreshOneDeliveryAsync(
        TwilioOrgCredentials credentials,
        WhatsAppConversationMessageDto message,
        CancellationToken cancellationToken)
    {
        var sid = message.ProviderMessageId!.Trim();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));

            using var client = _httpClientFactory.CreateClient("Twilio");
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.twilio.com/2010-04-01/Accounts/{Uri.EscapeDataString(credentials.AccountSid)}/Messages/{Uri.EscapeDataString(sid)}.json");
            var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{credentials.AccountSid}:{credentials.AuthToken}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

            using var response = await client.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode || !WhatsAppDeliveryRules.TryReadTwilioStatus(body, out var status, out var errorCode))
            {
                await _repository.TouchMessageDeliveryCheckAsync(message.Id, cancellationToken);
                return;
            }

            message.DeliveryStatus = status;
            message.DeliveryErrorCode = errorCode;
            message.DeliveryCheckedAtUtc = DateTimeOffset.UtcNow;
            await _repository.UpdateMessageDeliveryAsync(message.Id, status, errorCode, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await _repository.TouchMessageDeliveryCheckAsync(message.Id, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "WhatsApp delivery lookup failed. MessageId={MessageId}", message.Id);
            try
            {
                await _repository.TouchMessageDeliveryCheckAsync(message.Id, cancellationToken);
            }
            catch (Exception touchEx) when (touchEx is not OperationCanceledException)
            {
                _logger.LogWarning(touchEx, "WhatsApp delivery check timestamp was not saved. MessageId={MessageId}", message.Id);
            }
        }
    }

    private async Task EnsureCustomerCareWindowAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var lastInboundAt = await _repository.GetLastInboundAtUtcAsync(conversationId, cancellationToken);
        if (!WhatsAppCustomerCareWindow.IsOpen(lastInboundAt, DateTimeOffset.UtcNow))
            throw new WhatsAppCustomerCareWindowException();
    }

    private async Task<TwilioOrgCredentials> ResolveTwilioCredentialsAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        var orgId = await ResolveOrgIdFromTenantAsync(tenantId, cancellationToken);
        return await _twilioCredentials.ResolveAsync(orgId, cancellationToken);
    }

    private async Task<Guid> ResolveOrgIdFromTenantAsync(string tenantId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException("tenantId is required to resolve Twilio credentials.");

        var trimmed = tenantId.Trim();
        if (Guid.TryParse(trimmed, out var orgId))
            return orgId;

        var fromDb = await _repository.LookupOrgIdByMessengerTenantAsync(trimmed, cancellationToken);
        if (fromDb.HasValue)
            return fromDb.Value;

        throw new InvalidOperationException(
            $"Cannot resolve organization from tenant '{trimmed}'. Use org GUID or configure whatsapp_tenant_mappings.");
    }
}
