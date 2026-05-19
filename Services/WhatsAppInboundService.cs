using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using nexthire_api.DTOs;
using nexthire_api.DTOs.Sourcing;
using nexthire_api.Helpers;
using nexthire_api.Repositories;
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
    private readonly IConfiguration _configuration;
    private readonly IPipelineRepository _pipeline;
    private readonly ISourcingService _sourcing;
    private readonly ILogger<WhatsAppInboundService> _logger;

    public WhatsAppInboundService(
        IWhatsAppInboundRepository repository,
        IWhatsAppAiService aiService,
        INexaMessengerWhatsAppClient messengerClient,
        IConfiguration configuration,
        IPipelineRepository pipeline,
        ISourcingService sourcing,
        ILogger<WhatsAppInboundService> logger)
    {
        _repository = repository;
        _aiService = aiService;
        _messengerClient = messengerClient;
        _configuration = configuration;
        _pipeline = pipeline;
        _sourcing = sourcing;
        _logger = logger;
    }

    public async Task ProcessInboundMessageAsync(WhatsAppInboundMessageDto inbound, CancellationToken cancellationToken)
    {
        var rawTenant = inbound.TenantId;
        inbound.TenantId = WhatsAppTenantResolver.Resolve(_configuration, inbound.TenantId);
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

        var botConfig = await _repository.GetBotConfigAsync(inbound.TenantId);
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
                "El identificador de la vacante no es válido. Debe ser un GUID con solo números y letras A a F (hexadecimal), como en el enlace del aviso. Si copiaste el texto, revisa que no falte ni sobre ningún carácter.",
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

    public async Task<WhatsAppCandidateConversationDto?> GetConversationByCandidateAsync(string tenantId, Guid candidateId)
    {
        var conversation = await _repository.GetConversationByCandidateAsync(tenantId, candidateId);
        if (conversation == null)
            return null;

        var messages = await _repository.GetConversationMessagesAsync(conversation.Id, tenantId);
        return new WhatsAppCandidateConversationDto
        {
            Conversation = conversation,
            Messages = messages
        };
    }

    public Task<IReadOnlyList<WhatsAppConversationMessageDto>> GetConversationMessagesAsync(Guid conversationId, string tenantId)
    {
        return _repository.GetConversationMessagesAsync(conversationId, tenantId);
    }

    public async Task<SendWhatsAppMessageResponseDto> SendMessageAsync(
        Guid conversationId,
        SendWhatsAppMessageRequestDto request,
        CancellationToken cancellationToken)
    {
        var context = await _repository.GetConversationSendContextAsync(conversationId, request.TenantId);
        if (context == null)
            throw new KeyNotFoundException("Conversation not found.");

        var providerMessageId = await _messengerClient.SendMessageAsync(
            request.TenantId,
            context.PhoneNumber,
            request.Body,
            cancellationToken);

        var messageId = await _repository.SaveOutboundMessageForInboxAsync(
            conversationId,
            request.TenantId,
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

        var providerMessageId = await _messengerClient.SendMessageAsync(
            request.TenantId,
            normalizedTo,
            request.Body,
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
        string[] keys = ["human", "person", "agent", "recruiter", "asesor", "humano"];
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

            var outboundProviderMessageId = await _messengerClient.SendMessageAsync(
                inbound.TenantId,
                context.ContactPhone,
                reply,
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
                _logger.LogInformation(
                    "[WhatsAppBot] Repeated apply phrase mid-flow; re-prompt step {Step}",
                    session.Step);
                await SendAndPersistBotReplyAsync(
                    inbound,
                    context,
                    RepeatApplyStepPrompt(session.Step),
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
        if (!Guid.TryParse(inbound.TenantId.Trim(), out var orgId))
        {
            _logger.LogWarning(
                "WhatsApp tenantId is not a GUID; cannot start job application flow. TenantId: {TenantId}",
                inbound.TenantId);
            await SendAndPersistBotReplyAsync(
                inbound,
                context,
                "No pudimos iniciar tu postulación en este momento. Si el problema continúa, pide hablar con un reclutador.",
                cancellationToken);
            return true;
        }

        var jobOk = await _pipeline.JobExistsInOrgAsync(orgId, jobPostId);
        _logger.LogInformation(
            "[WhatsAppBot] Start flow: OrgId={OrgId} JobPostId={JobPostId} JobExistsInOrg={JobOk}",
            orgId,
            jobPostId,
            jobOk);
        if (!jobOk)
        {
            await SendAndPersistBotReplyAsync(
                inbound,
                context,
                "No encontramos esa vacante. Verifica el enlace del aviso e inténtalo de nuevo.",
                cancellationToken);
            return true;
        }

        var session = new WhatsAppApplySessionState { Step = WhatsAppApplySessionSteps.Name };
        await _repository.UpdateConversationApplyStateAsync(
            context.ConversationId,
            inbound.TenantId,
            jobPostId,
            SerializeApplySession(session),
            cancellationToken);

        await SendAndPersistBotReplyAsync(
            inbound,
            context,
            "¡Hola! Veo que quieres aplicar a esta vacante. Para continuar, ¿cuál es tu nombre completo?",
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
        if (!Guid.TryParse(inbound.TenantId.Trim(), out var orgId))
            return false;

        if (string.Equals(session.Step, WhatsAppApplySessionSteps.Name, StringComparison.OrdinalIgnoreCase))
        {
            var name = inbound.Body?.Trim() ?? string.Empty;
            if (name.Length < 2)
            {
                await SendAndPersistBotReplyAsync(
                    inbound,
                    context,
                    "Por favor escribe tu nombre completo (al menos 2 caracteres).",
                    cancellationToken);
                return true;
            }

            session.FullName = name;
            session.Step = WhatsAppApplySessionSteps.English;
            await PersistApplySessionAsync(context, inbound.TenantId, jobPostId, session, cancellationToken);

            await SendAndPersistBotReplyAsync(
                inbound,
                context,
                "Gracias. ¿Cuál es tu nivel de inglés? (por ejemplo: básico, intermedio, avanzado, nativo)",
                cancellationToken);
            return true;
        }

        if (string.Equals(session.Step, WhatsAppApplySessionSteps.English, StringComparison.OrdinalIgnoreCase))
        {
            var level = inbound.Body?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(level))
            {
                await SendAndPersistBotReplyAsync(
                    inbound,
                    context,
                    "Indica tu nivel de inglés en una breve frase.",
                    cancellationToken);
                return true;
            }

            session.EnglishLevel = level;
            session.Step = WhatsAppApplySessionSteps.Experience;
            await PersistApplySessionAsync(context, inbound.TenantId, jobPostId, session, cancellationToken);

            await SendAndPersistBotReplyAsync(
                inbound,
                context,
                "¿Cuántos años de experiencia tienes en roles similares? Responde con un número (ejemplo: 5).",
                cancellationToken);
            return true;
        }

        if (string.Equals(session.Step, WhatsAppApplySessionSteps.Experience, StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseExperienceYears(inbound.Body, out var years))
            {
                await SendAndPersistBotReplyAsync(
                    inbound,
                    context,
                    "Indica tus años de experiencia con un número (ejemplo: 5).",
                    cancellationToken);
                return true;
            }

            session.ExperienceYears = years;
            session.Step = WhatsAppApplySessionSteps.Resume;
            await PersistApplySessionAsync(context, inbound.TenantId, jobPostId, session, cancellationToken);

            await SendAndPersistBotReplyAsync(
                inbound,
                context,
                "Por último, envía tu currículum como documento (PDF o Word) por aquí, o pega un enlace al archivo.",
                cancellationToken);
            return true;
        }

        if (string.Equals(session.Step, WhatsAppApplySessionSteps.Resume, StringComparison.OrdinalIgnoreCase))
        {
            var resumeUrl = TryGetResumeUrl(inbound);
            if (string.IsNullOrWhiteSpace(resumeUrl))
            {
                await SendAndPersistBotReplyAsync(
                    inbound,
                    context,
                    "Necesitamos tu CV: envía un archivo (PDF o Word) o un enlace que empiece con http.",
                    cancellationToken);
                return true;
            }

            session.ResumeUrl = resumeUrl;
            session.Step = WhatsAppApplySessionSteps.Done;

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

                await _sourcing.CreateLeadAsync(
                    orgId,
                    new CreateSourcingLeadRequestDto
                    {
                        JobId = jobPostId,
                        SourceTypeCode = WhatsAppJobApplyIntentParser.SourceTypeWhatsApp,
                        FullName = session.FullName,
                        Phone = context.ContactPhone,
                        EnglishLevel = session.EnglishLevel,
                        ExperienceYears = session.ExperienceYears,
                        ResumeUrl = resumeUrl,
                        RawPayloadJson = payload
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to create sourcing lead from WhatsApp apply flow. OrgId: {OrgId}, JobId: {JobId}, ConversationId: {ConversationId}",
                    orgId,
                    jobPostId,
                    context.ConversationId);
                session.Step = WhatsAppApplySessionSteps.Resume;
                await SendAndPersistBotReplyAsync(
                    inbound,
                    context,
                    "Hubo un problema al guardar tu postulación. Por favor intenta de nuevo en unos minutos.",
                    cancellationToken);
                return true;
            }

            await _repository.UpdateConversationApplyStateAsync(
                context.ConversationId,
                inbound.TenantId,
                jobPostId,
                SerializeApplySession(session),
                cancellationToken);

            await SendAndPersistBotReplyAsync(
                inbound,
                context,
                "¡Listo! Registramos tu postulación. Un reclutador revisará tu información pronto.",
                cancellationToken);

            _logger.LogInformation(
                "WhatsApp job application flow completed and lead created. OrgId: {OrgId}, JobId: {JobId}, ConversationId: {ConversationId}",
                orgId,
                jobPostId,
                context.ConversationId);

            return true;
        }

        return false;
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

    private static bool TryParseExperienceYears(string? text, out int years)
    {
        years = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var m = Regex.Match(text.Trim(), @"\d+");
        if (!m.Success)
            return false;

        if (!int.TryParse(m.Value, out years))
            return false;

        return years is >= 0 and <= 60;
    }

    private static string RepeatApplyStepPrompt(string step) =>
        step switch
        {
            WhatsAppApplySessionSteps.Name =>
                "Seguimos con tu postulación. ¿Cuál es tu nombre completo?",
            WhatsAppApplySessionSteps.English =>
                "Seguimos con tu postulación. ¿Cuál es tu nivel de inglés? (básico, intermedio, avanzado, nativo, etc.)",
            WhatsAppApplySessionSteps.Experience =>
                "Seguimos con tu postulación. ¿Cuántos años de experiencia tienes? (solo un número)",
            WhatsAppApplySessionSteps.Resume =>
                "Seguimos con tu postulación. Envía tu currículum como archivo o pega un enlace.",
            _ => "Seguimos con tu postulación."
        };

    private static string? TryGetResumeUrl(WhatsAppInboundMessageDto inbound)
    {
        foreach (var item in inbound.Media ?? new List<WhatsAppInboundMediaDto>())
        {
            if (!string.IsNullOrWhiteSpace(item.Url))
                return item.Url.Trim();
        }

        var body = inbound.Body?.Trim();
        if (!string.IsNullOrEmpty(body)
            && body.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return body;

        return null;
    }

}
