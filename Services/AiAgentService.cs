using System.ClientModel;
using Azure;
using Azure.AI.OpenAI;
using nexthire_api.Agents;
using nexthire_api.DTOs;
using nexthire_api.DTOs.AiAgents;
using nexthire_api.DTOs.Meta;
using nexthire_api.DTOs.Sourcing;
using nexthire_api.Helpers;
using nexthire_api.Repositories;
using nexthire_api.Services.Sourcing;
using nexthire_api.WorkflowAutomation;
using Nexa.WorkflowAutomation.Serialization;
using OpenAI.Chat;
using System.Text.Json;

namespace nexthire_api.Services;

public class AiAgentService : IAiAgentService
{
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "active", "paused", "busy", "inactive",
    };

    private static readonly HashSet<string> ChatAgents = new(StringComparer.OrdinalIgnoreCase)
    {
        "sourcing",
        "campaign",
        "screening",
        "interview",
        "offer",
    };

    private readonly IOrgAgentRepository _agents;
    private readonly IAgentChatRepository _chat;
    private readonly ITokenUsageRepository _usage;
    private readonly ISourcingService _sourcing;
    private readonly IMetaAdsService _metaAds;
    private readonly IJobRepository _jobs;
    private readonly IApplicationsService _applications;
    private readonly IPipelineRepository _pipeline;
    private readonly ITemplatesRepository _templates;
    private readonly IEmailService _email;
    private readonly IWhatsAppInboundService _whatsApp;
    private readonly IWorkflowTriggerService _workflowTriggers;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AiAgentService> _logger;

    public AiAgentService(
        IOrgAgentRepository agents,
        IAgentChatRepository chat,
        ITokenUsageRepository usage,
        ISourcingService sourcing,
        IMetaAdsService metaAds,
        IJobRepository jobs,
        IApplicationsService applications,
        IPipelineRepository pipeline,
        ITemplatesRepository templates,
        IEmailService email,
        IWhatsAppInboundService whatsApp,
        IWorkflowTriggerService workflowTriggers,
        IConfiguration configuration,
        ILogger<AiAgentService> logger)
    {
        _agents = agents;
        _chat = chat;
        _usage = usage;
        _sourcing = sourcing;
        _metaAds = metaAds;
        _jobs = jobs;
        _applications = applications;
        _pipeline = pipeline;
        _templates = templates;
        _email = email;
        _whatsApp = whatsApp;
        _workflowTriggers = workflowTriggers;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AgentCatalogItemDto>> ListCatalogAsync(Guid orgId)
    {
        var hired = (await _agents.ListAsync(orgId)).ToDictionary(item => item.AgentId, StringComparer.OrdinalIgnoreCase);

        return AgentCatalog.All.Select(definition =>
        {
            hired.TryGetValue(definition.AgentId, out var record);
            return new AgentCatalogItemDto
            {
                AgentId = definition.AgentId,
                Availability = definition.Availability,
                Hireable = definition.Hireable,
                Hired = record is not null,
                HiredAt = record?.HiredAt,
            };
        }).ToList();
    }

    public Task<IReadOnlyList<HiredAgentDto>> ListHiredAsync(Guid orgId) =>
        _agents.ListAsync(orgId);

    public async Task<HiredAgentDto?> GetHiredAsync(Guid orgId, string agentId)
    {
        var id = AgentCatalog.NormalizeId(agentId);
        return await _agents.GetByAgentIdAsync(orgId, id);
    }

    public async Task<(HiredAgentDto Agent, bool Created)> HireAsync(Guid orgId, string agentId, Guid? hiredBy)
    {
        var definition = AgentCatalog.Require(agentId);
        if (!definition.Hireable)
            throw new ArgumentException("This agent is not available yet.");

        var existing = await _agents.GetByAgentIdAsync(orgId, definition.AgentId);
        if (existing is not null)
            return (existing, false);

        var created = await _agents.InsertAsync(orgId, definition.AgentId, hiredBy);
        if (definition.AgentId == "sourcing")
        {
            var context = new AgentContextDto();
            AriaSourcingPlaybook.PrepareSchedule(context, existing: null, DateTimeOffset.UtcNow);
            created = await _agents.UpdateContextAsync(orgId, definition.AgentId, context.ToJson()) ?? created;
        }
        else if (definition.AgentId == "campaign")
        {
            var context = new AgentContextDto { Channel = VeraCampaignPlaybook.PlatformMeta };
            VeraCampaignPlaybook.PrepareSchedule(context, existing: null, DateTimeOffset.UtcNow);
            created = await _agents.UpdateContextAsync(orgId, definition.AgentId, context.ToJson()) ?? created;
        }
        else if (definition.AgentId == "screening")
        {
            var context = new AgentContextDto
            {
                Channel = KaiScreeningPlaybook.ChannelEmail,
                MaxProcessed = KaiScreeningPlaybook.DefaultMaxProcessed,
                ResponseWaitHours = KaiScreeningPlaybook.DefaultResponseWaitHours,
            };
            KaiScreeningPlaybook.PrepareSchedule(context, existing: null, DateTimeOffset.UtcNow);
            created = await _agents.UpdateContextAsync(orgId, definition.AgentId, context.ToJson()) ?? created;
        }
        else if (definition.AgentId == "interview")
        {
            var context = new AgentContextDto
            {
                Channel = LenaInterviewPlaybook.ChannelEmail,
                MaxProcessed = LenaInterviewPlaybook.DefaultMaxProcessed,
                ResponseWaitHours = LenaInterviewPlaybook.DefaultResponseWaitHours,
            };
            LenaInterviewPlaybook.PrepareSchedule(context, existing: null, DateTimeOffset.UtcNow);
            created = await _agents.UpdateContextAsync(orgId, definition.AgentId, context.ToJson()) ?? created;
        }
        else if (definition.AgentId == "offer")
        {
            var context = new AgentContextDto
            {
                Channel = MiraOfferPlaybook.ChannelEmail,
                MaxProcessed = MiraOfferPlaybook.DefaultMaxProcessed,
                MaxOffersToSend = MiraOfferPlaybook.DefaultMaxProcessed,
                ResponseWaitHours = MiraOfferPlaybook.DefaultResponseWaitHours,
            };
            MiraOfferPlaybook.PrepareSchedule(context, existing: null, DateTimeOffset.UtcNow);
            created = await _agents.UpdateContextAsync(orgId, definition.AgentId, context.ToJson()) ?? created;
        }

        return (created, true);
    }

    public Task<bool> UnhireAsync(Guid orgId, string agentId)
    {
        var id = (agentId ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(id))
            throw new ArgumentException("Unknown agent.");
        return _agents.DeleteAsync(orgId, id);
    }

    public Task<HiredAgentDto?> SetStatusAsync(Guid orgId, string agentId, string status)
    {
        var id = AgentCatalog.NormalizeId(agentId);
        var normalized = (status ?? string.Empty).Trim().ToLowerInvariant();
        if (!AllowedStatuses.Contains(normalized))
            throw new ArgumentException("Invalid agent status.");

        return _agents.UpdateStatusAsync(orgId, id, normalized);
    }

    public async Task<HiredAgentDto?> UpdateContextAsync(Guid orgId, string agentId, AgentContextDto context)
    {
        var id = AgentCatalog.NormalizeId(agentId);
        var existing = await _agents.GetByAgentIdAsync(orgId, id);
        if (existing is null)
            return null;

        if (id == "sourcing")
            AriaSourcingPlaybook.PrepareSchedule(context, existing.Context, DateTimeOffset.UtcNow);
        else if (id == "campaign")
            VeraCampaignPlaybook.PrepareSchedule(context, existing.Context, DateTimeOffset.UtcNow);
        else if (id == "screening")
            KaiScreeningPlaybook.PrepareSchedule(context, existing.Context, DateTimeOffset.UtcNow);
        else if (id == "interview")
            LenaInterviewPlaybook.PrepareSchedule(context, existing.Context, DateTimeOffset.UtcNow);
        else if (id == "offer")
            MiraOfferPlaybook.PrepareSchedule(context, existing.Context, DateTimeOffset.UtcNow);

        return await _agents.UpdateContextAsync(orgId, id, context.ToJson());
    }

    public async Task<HiredAgentDto?> RunNowAsync(
        Guid orgId,
        string agentId,
        Guid nexaUserId,
        string? userEmail,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        var id = AgentCatalog.NormalizeId(agentId);
        var hired = await _agents.GetByAgentIdAsync(orgId, id);
        if (hired is null)
            return null;

        if (string.Equals(hired.Status, "paused", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Paused agents cannot run.");

        if (id == "sourcing")
            return await ExecuteAriaAsync(
                orgId,
                hired,
                nexaUserId,
                userEmail,
                displayName,
                scheduled: false,
                cancellationToken);

        if (id == "campaign")
            return await ExecuteVeraAsync(orgId, hired, scheduled: false, cancellationToken);

        if (id == "screening")
            return await ExecuteKaiAsync(
                orgId,
                hired,
                nexaUserId,
                scheduled: false,
                cancellationToken);

        if (id == "interview")
            return await ExecuteLenaAsync(
                orgId,
                hired,
                nexaUserId,
                scheduled: false,
                cancellationToken);

        if (id == "offer")
            return await ExecuteMiraAsync(
                orgId,
                hired,
                nexaUserId,
                scheduled: false,
                cancellationToken);

        _logger.LogInformation("AI agent run stub. OrgId={OrgId}, AgentId={AgentId}", orgId, id);
        var busy = await _agents.IncrementRunAsync(orgId, id);
        if (busy is null)
            return null;

        return await _agents.UpdateStatusAsync(
                   orgId,
                   id,
                   string.Equals(busy.Status, "paused", StringComparison.OrdinalIgnoreCase) ? "paused" : "active")
               ?? busy;
    }

    public async Task RunDueAgentsAsync(CancellationToken cancellationToken = default)
    {
        await RunDueForAgentAsync("sourcing", cancellationToken);
        await RunDueForAgentAsync("campaign", cancellationToken);
        await RunDueForAgentAsync("screening", cancellationToken);
        await RunDueForAgentAsync("interview", cancellationToken);
        await RunDueForAgentAsync("offer", cancellationToken);
    }

    public async Task<AgentTokenUsageDto> GetUsageAsync(
        Guid orgId,
        int days = 90,
        CancellationToken cancellationToken = default)
    {
        var window = Math.Clamp(days, 1, 90);
        var since = DateTimeOffset.UtcNow.AddDays(-window);
        var events = await _usage.ListDailyAsync(orgId, since, cancellationToken);
        return new AgentTokenUsageDto
        {
            Total = events.Sum(item => (long)item.Tokens),
            Events = events.ToList(),
        };
    }

    private async Task RunDueForAgentAsync(string agentId, CancellationToken cancellationToken)
    {
        var due = await _agents.ListActiveByAgentIdAsync(agentId);
        foreach (var agent in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = agent.Context;
            if (context is null || !AriaSourcingPlaybook.IsDue(context, DateTimeOffset.UtcNow))
                continue;

            try
            {
                if (agentId == "campaign")
                {
                    await ExecuteVeraAsync(agent.OrgId, agent, scheduled: true, cancellationToken);
                    continue;
                }

                var actor = agent.HiredBy ?? Guid.Empty;
                if (actor == Guid.Empty)
                {
                    _logger.LogWarning(
                        "Skipping scheduled {AgentId} for org {OrgId}: missing hired_by for conversions.",
                        agentId,
                        agent.OrgId);
                    continue;
                }

                if (agentId == "screening")
                {
                    await ExecuteKaiAsync(agent.OrgId, agent, actor, scheduled: true, cancellationToken);
                    continue;
                }

                if (agentId == "interview")
                {
                    await ExecuteLenaAsync(agent.OrgId, agent, actor, scheduled: true, cancellationToken);
                    continue;
                }

                if (agentId == "offer")
                {
                    await ExecuteMiraAsync(agent.OrgId, agent, actor, scheduled: true, cancellationToken);
                    continue;
                }

                await ExecuteAriaAsync(
                    agent.OrgId,
                    agent,
                    actor,
                    userEmail: null,
                    displayName: "Aria (scheduled)",
                    scheduled: true,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Scheduled {AgentId} run failed for org {OrgId}.", agentId, agent.OrgId);
            }
        }
    }

    private async Task<HiredAgentDto?> ExecuteAriaAsync(
        Guid orgId,
        HiredAgentDto hired,
        Guid nexaUserId,
        string? userEmail,
        string? displayName,
        bool scheduled,
        CancellationToken cancellationToken)
    {
        if (string.Equals(hired.Status, "paused", StringComparison.OrdinalIgnoreCase))
        {
            if (scheduled) return hired;
            throw new ArgumentException("Paused agents cannot run.");
        }

        var context = hired.Context ?? new AgentContextDto();
        AriaSourcingPlaybook.PrepareContext(context, hired.Context);

        var maxProcessed = AriaSourcingPlaybook.ClampMaxProcessed(context.MaxProcessed);
        var percent = AriaSourcingPlaybook.ClampAutoConversionPercent(context.AutoConversionPercent);
        var jobId = context.JobId;

        _logger.LogInformation(
            "Aria run started. OrgId={OrgId}, JobId={JobId}, MaxProcessed={MaxProcessed}, AutoConversionPercent={Percent}, Scheduled={Scheduled}",
            orgId,
            jobId,
            maxProcessed,
            percent,
            scheduled);

        await _agents.UpdateStatusAsync(orgId, hired.AgentId, "busy");

        var result = new AriaRunResult();
        try
        {
            var batch = await LoadProcessableLeadsAsync(orgId, jobId, maxProcessed);
            result.Scanned = batch.Count;

            var working = new List<SourcingLeadListItemDto>(batch.Count);
            foreach (var lead in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!lead.FitScore.HasValue && lead.JobId.HasValue)
                {
                    try
                    {
                        var detail = await _sourcing.GetLeadAsync(orgId, lead.Id);
                        if (detail is not null)
                        {
                            await _sourcing.ScoreLeadFitAsync(orgId, detail, cancellationToken: cancellationToken);
                            result.Scored++;
                            detail = await _sourcing.GetLeadAsync(orgId, lead.Id) ?? detail;
                            working.Add(detail);
                            continue;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        result.Errors++;
                        _logger.LogWarning(ex, "Aria failed to score lead {LeadId}", lead.Id);
                    }
                }

                working.Add(lead);
            }

            result.Quota = AriaSourcingPlaybook.ConversionQuota(working.Count, percent);
            var toConvert = AriaSourcingPlaybook.SelectForConversion(working, result.Quota);
            result.Skipped = Math.Max(0, working.Count - toConvert.Count);

            foreach (var lead in toConvert)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var converted = await _sourcing.ConvertLeadToCandidateAsync(
                        orgId,
                        lead.Id,
                        nexaUserId,
                        userEmail,
                        displayName);
                    if (converted is null)
                    {
                        result.Errors++;
                        continue;
                    }

                    result.Converted++;
                    result.ConvertedLeadIds.Add(converted.LeadId);
                    result.ConvertedCandidateIds.Add(converted.CandidateId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Errors++;
                    _logger.LogWarning(ex, "Aria failed to convert lead {LeadId}", lead.Id);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.Errors++;
            _logger.LogError(ex, "Aria run failed for org {OrgId}", orgId);
        }

        var now = DateTimeOffset.UtcNow;
        var runTokens = AriaSourcingPlaybook.EstimateTokens(result);
        AriaSourcingPlaybook.StampRun(context, now);
        AriaSourcingPlaybook.RecordReview(context, result, runTokens, now);
        context.MaxProcessed = maxProcessed;
        context.AutoConversionPercent = percent;
        context.Cadence = AriaSourcingPlaybook.NormalizeCadence(context.Cadence);

        await _agents.UpdateContextAsync(orgId, hired.AgentId, context.ToJson());

        if (runTokens > 0)
        {
            await _usage.RecordAsync(new TokenUsageEntry
            {
                OrgId = orgId,
                AgentId = hired.AgentId,
                Source = "run",
                EntityType = "aria",
                TotalTokens = (int)Math.Min(int.MaxValue, runTokens),
            }, cancellationToken);
        }

        var tokens = hired.Tokens + runTokens;
        var runs = hired.Runs + 1;
        var updated = await _agents.SetOperationalStatsAsync(orgId, hired.AgentId, runs, tokens, "active");

        _logger.LogInformation(
            "Aria run finished. OrgId={OrgId}, Summary={Summary}",
            orgId,
            context.LastSummary);

        if (jobId.HasValue && jobId.Value != Guid.Empty)
        {
            var variables = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["Job.Id"] = WorkflowJsonSerializer.SerializeToElement(jobId.Value.ToString("D")),
                ["Agent.Id"] = WorkflowJsonSerializer.SerializeToElement(hired.AgentId),
            };
            if (!string.IsNullOrWhiteSpace(context.LastSummary))
                variables["Workflow.Summary"] = WorkflowJsonSerializer.SerializeToElement(context.LastSummary);

            await CandidateWorkflowDispatch.TryAsync(
                _workflowTriggers,
                _logger,
                orgId,
                CandidateWorkflowVariables.SourcingCompletedTrigger,
                CandidateWorkflowVariables.JobEntityType,
                jobId.Value.ToString("D"),
                variables,
                cancellationToken);
        }

        if (updated is null)
            return await _agents.GetByAgentIdAsync(orgId, hired.AgentId);

        updated.Context = AgentContextDto.Parse(context.ToJson()) ?? context;
        return updated;
    }

    public async Task<AgentChatThreadDto?> ListChatAsync(
        Guid orgId,
        string agentId,
        CancellationToken cancellationToken = default)
    {
        var id = AgentCatalog.NormalizeId(agentId);
        if (!ChatAgents.Contains(id))
            throw new ArgumentException("This agent does not support chat yet.");

        var current = await _agents.GetByAgentIdAsync(orgId, id);
        if (current is null) return null;

        var messages = await _chat.ListAsync(orgId, id, 80, cancellationToken);
        return new AgentChatThreadDto { Messages = messages.ToList() };
    }

    public async Task<AgentChatReplyDto?> ChatAsync(
        Guid orgId,
        string agentId,
        AgentChatRequestDto request,
        Guid nexaUserId,
        string? userEmail,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        var id = AgentCatalog.NormalizeId(agentId);
        if (!ChatAgents.Contains(id))
            throw new ArgumentException("This agent does not support chat yet.");

        var current = await _agents.GetByAgentIdAsync(orgId, id);
        if (current is null) return null;
        if (string.Equals(current.Status, "paused", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Paused agents cannot chat.");

        var question = request.Messages.LastOrDefault(item =>
            string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content;
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("A user message is required.");

        var context = current.Context ?? new AgentContextDto();
        if (id == "campaign")
            VeraCampaignPlaybook.PrepareContext(context, current.Context);
        else if (id == "screening")
            KaiScreeningPlaybook.PrepareContext(context, current.Context);
        else if (id == "interview")
            LenaInterviewPlaybook.PrepareContext(context, current.Context);
        else if (id == "offer")
            MiraOfferPlaybook.PrepareContext(context, current.Context);
        else
            AriaSourcingPlaybook.PrepareContext(context, current.Context);

        var stored = await _chat.ListAsync(orgId, id, 80, cancellationToken);
        var userTurn = new AgentChatMessageDto
        {
            Role = "user",
            Content = question.Trim(),
            At = DateTimeOffset.UtcNow,
        };
        var thread = stored.Count > 0
            ? stored.Concat([userTurn]).ToList()
            : request.Messages.Count > 0
                ? request.Messages.ToList()
                : [userTurn];

        var language = request.Locale?.StartsWith("es", StringComparison.OrdinalIgnoreCase) == true
            ? "Spanish"
            : "English";
        var isVera = id == "campaign";
        var isKai = id == "screening";
        var isLena = id == "interview";
        var isMira = id == "offer";
        var systemPrompt = isVera
            ? VeraChatInterpreter.SystemPrompt(language)
            : isKai
                ? KaiChatInterpreter.SystemPrompt(language)
                : isLena
                    ? LenaChatInterpreter.SystemPrompt(language)
                    : isMira
                        ? MiraChatInterpreter.SystemPrompt(language)
                        : AriaChatInterpreter.SystemPrompt(language);
        var userPrompt = isVera
            ? VeraChatInterpreter.UserPrompt(context, thread)
            : isKai
                ? KaiChatInterpreter.UserPrompt(context, thread)
                : isLena
                    ? LenaChatInterpreter.UserPrompt(context, thread)
                    : isMira
                        ? MiraChatInterpreter.UserPrompt(context, thread)
                        : AriaChatInterpreter.UserPrompt(context, thread);

        var (content, tokensUsed) = await CompleteChatJsonAsync(
            orgId,
            id,
            systemPrompt,
            userPrompt,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException(
                isVera ? "Vera could not answer."
                : isKai ? "Kai could not answer."
                : isLena ? "Lena could not answer."
                : isMira ? "Mira could not answer."
                : "Aria could not answer.");

        var decision = isVera
            ? VeraChatInterpreter.Parse(content)
            : isKai
                ? KaiChatInterpreter.Parse(content)
                : isLena
                    ? LenaChatInterpreter.Parse(content)
                    : isMira
                        ? MiraChatInterpreter.Parse(content)
                        : AriaChatInterpreter.Parse(content);
        context.ChatTokens += tokensUsed;

        var wantsUpdate = isVera
            ? VeraChatInterpreter.WantsUpdate(decision.Action)
            : isKai
                ? KaiChatInterpreter.WantsUpdate(decision.Action)
                : isLena
                    ? LenaChatInterpreter.WantsUpdate(decision.Action)
                    : isMira
                        ? MiraChatInterpreter.WantsUpdate(decision.Action)
                        : AriaChatInterpreter.WantsUpdate(decision.Action);
        var wantsRun = isVera
            ? VeraChatInterpreter.WantsRun(decision.Action)
            : isKai
                ? KaiChatInterpreter.WantsRun(decision.Action)
                : isLena
                    ? LenaChatInterpreter.WantsRun(decision.Action)
                    : isMira
                        ? MiraChatInterpreter.WantsRun(decision.Action)
                        : AriaChatInterpreter.WantsRun(decision.Action);

        if (wantsUpdate)
        {
            if (isVera) VeraChatInterpreter.ApplyPatch(context, decision.Context);
            else if (isKai) KaiChatInterpreter.ApplyPatch(context, decision.Context);
            else if (isLena) LenaChatInterpreter.ApplyPatch(context, decision.Context);
            else if (isMira) MiraChatInterpreter.ApplyPatch(context, decision.Context);
            else AriaChatInterpreter.ApplyPatch(context, decision.Context);
        }

        HiredAgentDto? agent;
        if (wantsUpdate)
            agent = await UpdateContextAsync(orgId, id, context);
        else
        {
            await _agents.UpdateContextAsync(orgId, id, context.ToJson());
            agent = await _agents.GetByAgentIdAsync(orgId, id);
        }

        if (tokensUsed > 0)
        {
            await _usage.RecordAsync(new TokenUsageEntry
            {
                OrgId = orgId,
                AgentId = id,
                Source = "chat",
                EntityType = isVera ? "vera" : isKai ? "kai" : isLena ? "lena" : isMira ? "mira" : "aria",
                TotalTokens = tokensUsed,
            }, cancellationToken);
        }

        if (agent is not null && tokensUsed > 0)
        {
            agent = await _agents.SetOperationalStatsAsync(
                       orgId,
                       id,
                       agent.Runs,
                       agent.Tokens + tokensUsed,
                       string.Equals(agent.Status, "paused", StringComparison.OrdinalIgnoreCase) ? "paused" : "active")
                   ?? agent;
            if (agent is not null)
                agent.Context = context;
        }

        if (wantsRun)
            agent = await RunNowAsync(orgId, id, nexaUserId, userEmail, displayName, cancellationToken) ?? agent;

        var reply = decision.Reply.Trim();
        var messages = await PersistChatAsync(orgId, id, question.Trim(), reply, thread, cancellationToken);
        return new AgentChatReplyDto
        {
            Reply = reply,
            Action = decision.Action,
            Agent = agent,
            Messages = messages,
        };
    }

    private async Task<(string Content, int Tokens)> CompleteChatJsonAsync(
        Guid orgId,
        string agentId,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        var apiKey = _configuration["AzureOpenAI:ApiKey"]
            ?? _configuration["AzureOpenAI:ChatApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Azure OpenAI is not configured.");

        var deployment = _configuration["AzureOpenAI:ChatDeployment"]
            ?? _configuration["AzureOpenAI:Deployment"]
            ?? "gpt-4o-mini";

        var client = new AzureOpenAIClient(ResolveAzureOpenAiUri(), new AzureKeyCredential(apiKey));
        var chatClient = client.GetChatClient(deployment);

        try
        {
            var response = await chatClient.CompleteChatAsync(
                [
                    new SystemChatMessage(systemPrompt),
                    new UserChatMessage(userPrompt),
                ],
                new ChatCompletionOptions
                {
                    MaxOutputTokenCount = 700,
                    Temperature = 0.4f,
                    ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
                },
                cancellationToken);

            var text = response.Value.Content.FirstOrDefault()?.Text?.Trim() ?? string.Empty;
            var usage = response.Value.Usage;
            var tokens = (usage?.InputTokenCount ?? 0) + (usage?.OutputTokenCount ?? 0);
            if (tokens <= 0) tokens = Math.Max(40, text.Length / 4);
            return (text, tokens);
        }
        catch (ClientResultException ex) when (ex.Status == 401)
        {
            _logger.LogWarning(ex, "Aria chat rejected by Azure OpenAI for org {OrgId} agent {AgentId}", orgId, agentId);
            throw new InvalidOperationException("Azure OpenAI rejected the API key.");
        }
    }

    private Uri ResolveAzureOpenAiUri()
    {
        var chatResource = _configuration["AzureOpenAI:ChatResource"]?.Trim();
        if (!string.IsNullOrEmpty(chatResource))
            return new Uri(chatResource.TrimEnd('/') + "/");

        var endpoint = _configuration["AzureOpenAI:Endpoint"]?.Trim();
        if (!string.IsNullOrEmpty(endpoint))
        {
            var uri = new Uri(endpoint);
            return new Uri($"{uri.Scheme}://{uri.Authority}/");
        }

        var resource = _configuration["AzureOpenAI:Resource"]?.Trim();
        if (!string.IsNullOrEmpty(resource))
            return new Uri(resource.TrimEnd('/') + "/");

        throw new InvalidOperationException("Azure OpenAI is not configured.");
    }

    private async Task<List<AgentChatMessageDto>> PersistChatAsync(
        Guid orgId,
        string agentId,
        string question,
        string reply,
        IReadOnlyList<AgentChatMessageDto> thread,
        CancellationToken cancellationToken)
    {
        try
        {
            await _chat.AppendAsync(orgId, agentId, "user", question, cancellationToken);
            await _chat.AppendAsync(orgId, agentId, "assistant", reply, cancellationToken);
            var stored = await _chat.ListAsync(orgId, agentId, 80, cancellationToken);
            return stored.ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not persist chat for agent {AgentId} org {OrgId}.", agentId, orgId);
            return thread
                .Concat([
                    new AgentChatMessageDto
                    {
                        Role = "assistant",
                        Content = reply,
                        At = DateTimeOffset.UtcNow,
                    },
                ])
                .ToList();
        }
    }

    private async Task<HiredAgentDto?> ExecuteVeraAsync(
        Guid orgId,
        HiredAgentDto hired,
        bool scheduled,
        CancellationToken cancellationToken)
    {
        if (string.Equals(hired.Status, "paused", StringComparison.OrdinalIgnoreCase))
        {
            if (scheduled) return hired;
            throw new ArgumentException("Paused agents cannot run.");
        }

        var context = hired.Context ?? new AgentContextDto();
        VeraCampaignPlaybook.PrepareContext(context, hired.Context);

        if (context.JobId is not Guid jobId || jobId == Guid.Empty)
            throw new ArgumentException("Select a job in Vera Context before running.");

        var destinationUrl = VeraCampaignPlaybook.NormalizeDestinationUrl(context.CampaignDestinationUrl);
        if (string.IsNullOrWhiteSpace(destinationUrl))
            throw new ArgumentException("Add a destination URL (public job link) in Vera Context before running.");

        var platforms = VeraCampaignPlaybook.NormalizePlatforms(context.Channel);
        if (platforms.Contains(VeraCampaignPlaybook.PlatformMeta) && !await IsMetaAdsReadyAsync(orgId))
            throw new ArgumentException("Connect and activate Meta Ads in Sources before running Meta campaigns.");

        var job = await _jobs.GetByIdAsync(orgId, jobId)
            ?? throw new ArgumentException("The selected job was not found.");

        var now = DateTimeOffset.UtcNow;
        if (scheduled && !AriaSourcingPlaybook.IsDue(context, now))
            return hired;

        await _agents.UpdateStatusAsync(orgId, hired.AgentId, "busy");

        var result = new VeraRunResult();
        var stamped = new List<VeraCampaignDto>();
        try
        {
            var sweep = VeraCampaignPlaybook.Plan(context);
            result.Planned = sweep.ToCreate.Count + sweep.ToManage.Count;
            var budget = VeraCampaignPlaybook.ClampDailyBudget(context.CampaignDailyBudget);
            var desired = VeraCampaignPlaybook.NormalizeDesiredStatus(context.CampaignDesiredStatus);
            var country = VeraCampaignPlaybook.NormalizeCountryCode(context.CampaignCountryCode);
            var headline = VeraCampaignPlaybook.Headline(context, job.Title);
            var intro = VeraCampaignPlaybook.IntroText(context, job.Description);

            foreach (var item in sweep.ToCreate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Platform == VeraCampaignPlaybook.PlatformLinkedIn)
                {
                    result.Skipped++;
                    stamped.Add(VeraCampaignPlaybook.Stamp(
                        item.Platform,
                        Guid.Empty,
                        jobId,
                        VeraCampaignPlaybook.CampaignName(job.Title, item.Platform, now),
                        desired,
                        VeraCampaignPlaybook.ActionSkipped,
                        now));
                    continue;
                }

                try
                {
                    var localId = await CreateVeraMetaCampaignAsync(
                        orgId,
                        jobId,
                        job.Title,
                        headline,
                        intro,
                        destinationUrl,
                        country,
                        budget,
                        desired,
                        cancellationToken);
                    if (localId is null)
                    {
                        result.Errors++;
                        continue;
                    }

                    stamped.Add(VeraCampaignPlaybook.Stamp(
                        VeraCampaignPlaybook.PlatformMeta,
                        localId.Value,
                        jobId,
                        VeraCampaignPlaybook.CampaignName(job.Title, VeraCampaignPlaybook.PlatformMeta, now),
                        desired,
                        VeraCampaignPlaybook.ActionCreated,
                        now));
                    result.Created++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Errors++;
                    _logger.LogWarning(ex, "Vera failed to create Meta campaign for job {JobId}", jobId);
                }
            }

            foreach (var item in sweep.ToManage)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.ExistingCampaignId is not Guid campaignId || campaignId == Guid.Empty)
                    continue;

                if (item.Platform == VeraCampaignPlaybook.PlatformLinkedIn)
                {
                    result.Skipped++;
                    continue;
                }

                try
                {
                    if (desired == VeraCampaignPlaybook.StatusActive)
                        await _metaAds.ActivateMarketingCampaignAsync(orgId, campaignId.ToString("D"), cancellationToken);
                    else
                        await _metaAds.PauseMarketingCampaignAsync(orgId, campaignId.ToString("D"), cancellationToken);

                    stamped.Add(VeraCampaignPlaybook.Stamp(
                        VeraCampaignPlaybook.PlatformMeta,
                        campaignId,
                        jobId,
                        VeraCampaignPlaybook.CampaignName(job.Title, VeraCampaignPlaybook.PlatformMeta, now),
                        desired,
                        VeraCampaignPlaybook.ActionSynced,
                        now));
                    result.Managed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Errors++;
                    _logger.LogWarning(ex, "Vera failed to sync campaign {CampaignId}", campaignId);
                }
            }

            try
            {
                await _metaAds.SyncMarketingCampaignsToSourcingAsync(orgId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Vera could not sync marketing campaigns to sourcing for org {OrgId}", orgId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ArgumentException)
        {
            result.Errors++;
            _logger.LogError(ex, "Vera run failed for org {OrgId}", orgId);
        }

        VeraCampaignPlaybook.RememberCampaigns(context, stamped.Where(item => item.CampaignId != Guid.Empty));
        var runTokens = VeraCampaignPlaybook.EstimateTokens(result);
        AriaSourcingPlaybook.StampRun(context, now);
        VeraCampaignPlaybook.RecordReview(context, result, runTokens, now);
        context.Cadence = AriaSourcingPlaybook.NormalizeCadence(context.Cadence);

        await _agents.UpdateContextAsync(orgId, hired.AgentId, context.ToJson());

        if (runTokens > 0)
        {
            await _usage.RecordAsync(new TokenUsageEntry
            {
                OrgId = orgId,
                AgentId = hired.AgentId,
                Source = "run",
                EntityType = "vera",
                TotalTokens = (int)Math.Min(int.MaxValue, runTokens),
            }, cancellationToken);
        }

        var tokens = hired.Tokens + runTokens;
        var runs = hired.Runs + 1;
        var updated = await _agents.SetOperationalStatsAsync(orgId, hired.AgentId, runs, tokens, "active");

        _logger.LogInformation("Vera run finished. OrgId={OrgId}, Summary={Summary}", orgId, context.LastSummary);

        if (updated is null)
            return await _agents.GetByAgentIdAsync(orgId, hired.AgentId);

        updated.Context = AgentContextDto.Parse(context.ToJson()) ?? context;
        return updated;
    }

    private async Task<HiredAgentDto?> ExecuteKaiAsync(
        Guid orgId,
        HiredAgentDto hired,
        Guid nexaUserId,
        bool scheduled,
        CancellationToken cancellationToken)
    {
        if (string.Equals(hired.Status, "paused", StringComparison.OrdinalIgnoreCase))
        {
            if (scheduled) return hired;
            throw new ArgumentException("Paused agents cannot run.");
        }

        var context = hired.Context ?? new AgentContextDto();
        KaiScreeningPlaybook.PrepareContext(context, hired.Context);

        var channel = KaiScreeningPlaybook.NormalizeChannel(context.Channel);
        var maxProcessed = KaiScreeningPlaybook.ClampMaxProcessed(context.MaxProcessed);
        var waitHours = KaiScreeningPlaybook.ClampResponseWaitHours(context.ResponseWaitHours);
        context.Channel = channel;
        context.MaxProcessed = maxProcessed;
        context.ResponseWaitHours = waitHours;

        if (channel == KaiScreeningPlaybook.ChannelEmail &&
            KaiScreeningPlaybook.ParseTemplateId(context.TemplateId) is null)
        {
            throw new ArgumentException("Pick an email template in Context before running Kai.");
        }

        if (nexaUserId == Guid.Empty && channel == KaiScreeningPlaybook.ChannelWhatsApp)
            throw new ArgumentException("A recruiter identity is required to send WhatsApp screening messages.");

        var stageId = await ResolveKaiStageIdAsync(orgId, context.StageId);
        if (stageId is null)
            throw new ArgumentException(
                $"No pipeline stage named \"{KaiScreeningPlaybook.DefaultStageName}\" was found. Pick a stage in Context.");

        context.StageId = stageId;

        _logger.LogInformation(
            "Kai run started. OrgId={OrgId}, JobId={JobId}, StageId={StageId}, Channel={Channel}, Max={Max}, Scheduled={Scheduled}",
            orgId,
            context.JobId,
            stageId,
            channel,
            maxProcessed,
            scheduled);

        await _agents.UpdateStatusAsync(orgId, hired.AgentId, "busy");

        DTOs.TemplateDto? emailTemplate = null;
        if (channel == KaiScreeningPlaybook.ChannelEmail)
        {
            emailTemplate = await _templates.GetAsync(orgId, KaiScreeningPlaybook.ParseTemplateId(context.TemplateId)!.Value);
            if (emailTemplate is null || !emailTemplate.IsActive)
                throw new ArgumentException("The selected email template was not found or is inactive.");
            if (!string.Equals(emailTemplate.Channel, KaiScreeningPlaybook.ChannelEmail, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Pick an email channel template for Kai email outreach.");
        }

        var page = await _applications.GetPagedAsync(
            orgId,
            context.JobId,
            candidateId: null,
            stageId,
            status: null,
            from: null,
            to: null,
            search: null,
            page: 1,
            pageSize: Math.Max(maxProcessed * 3, 50));

        var apps = page.Items
            .Select(item => (
                item.ApplicationId,
                item.CandidateId,
                item.CandidateName,
                item.CandidateEmail,
                item.JobTitle,
                item.Status))
            .ToList();

        var now = DateTimeOffset.UtcNow;
        var sweep = KaiScreeningPlaybook.Plan(context, apps, now);
        var result = new KaiRunResult
        {
            Eligible = sweep.Eligible,
            Planned = sweep.ToContact.Count,
            Skipped = sweep.Skipped,
        };
        var stamped = new List<KaiOutreachDto>();

        foreach (var decision in sweep.ToContact)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (channel == KaiScreeningPlaybook.ChannelEmail)
                {
                    if (string.IsNullOrWhiteSpace(decision.CandidateEmail) ||
                        !decision.CandidateEmail.Contains('@'))
                    {
                        result.Skipped++;
                        continue;
                    }

                    var body = KaiScreeningPlaybook.RenderTemplate(
                        emailTemplate!.Body,
                        decision.CandidateName,
                        decision.JobTitle,
                        context.Notes);
                    var subject = KaiScreeningPlaybook.RenderTemplate(
                        string.IsNullOrWhiteSpace(emailTemplate.Subject)
                            ? $"Screening — {decision.JobTitle}"
                            : emailTemplate.Subject!,
                        decision.CandidateName,
                        decision.JobTitle,
                        context.Notes);
                    var html = $"<div style=\"font-family:sans-serif;white-space:pre-wrap\">{System.Net.WebUtility.HtmlEncode(body).Replace("\n", "<br/>")}</div>";
                    await _email.SendEmailAsync(decision.CandidateEmail.Trim(), subject, body, html, cancellationToken);
                }
                else
                {
                    if (decision.Action == KaiScreeningPlaybook.ActionFollowUp)
                        await _whatsApp.SendFollowUpAsync(orgId, nexaUserId, decision.CandidateId, cancellationToken);
                    else
                        await _whatsApp.SendIntroductionAsync(orgId, nexaUserId, decision.CandidateId, cancellationToken);
                }

                stamped.Add(KaiScreeningPlaybook.Stamp(
                    decision.CandidateId,
                    decision.ApplicationId,
                    channel,
                    decision.Action,
                    now));

                if (decision.Action == KaiScreeningPlaybook.ActionFollowUp)
                    result.FollowUps++;
                else
                    result.Intros++;

                var outreachVars = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Candidate.Id"] = WorkflowJsonSerializer.SerializeToElement(decision.CandidateId.ToString("D")),
                    ["Agent.Id"] = WorkflowJsonSerializer.SerializeToElement(hired.AgentId),
                };
                if (decision.ApplicationId != Guid.Empty)
                {
                    outreachVars["Application.Id"] =
                        WorkflowJsonSerializer.SerializeToElement(decision.ApplicationId.ToString("D"));
                }

                await CandidateWorkflowDispatch.TryAsync(
                    _workflowTriggers,
                    _logger,
                    orgId,
                    CandidateWorkflowVariables.ScreeningOutreachTrigger,
                    decision.ApplicationId != Guid.Empty
                        ? CandidateWorkflowVariables.ApplicationEntityType
                        : CandidateWorkflowVariables.CandidateEntityType,
                    decision.ApplicationId != Guid.Empty
                        ? decision.ApplicationId.ToString("D")
                        : decision.CandidateId.ToString("D"),
                    outreachVars,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Errors++;
                _logger.LogWarning(
                    ex,
                    "Kai failed to contact candidate {CandidateId} via {Channel}",
                    decision.CandidateId,
                    channel);
            }
        }

        KaiScreeningPlaybook.RememberOutreach(context, stamped);
        var runTokens = KaiScreeningPlaybook.EstimateTokens(result);
        AriaSourcingPlaybook.StampRun(context, now);
        KaiScreeningPlaybook.RecordReview(context, result, runTokens, now);
        context.Cadence = AriaSourcingPlaybook.NormalizeCadence(context.Cadence);

        await _agents.UpdateContextAsync(orgId, hired.AgentId, context.ToJson());

        if (runTokens > 0)
        {
            await _usage.RecordAsync(new TokenUsageEntry
            {
                OrgId = orgId,
                AgentId = hired.AgentId,
                Source = "run",
                EntityType = "kai",
                TotalTokens = (int)Math.Min(int.MaxValue, runTokens),
            }, cancellationToken);
        }

        var tokens = hired.Tokens + runTokens;
        var runs = hired.Runs + 1;
        var updated = await _agents.SetOperationalStatsAsync(orgId, hired.AgentId, runs, tokens, "active");

        _logger.LogInformation("Kai run finished. OrgId={OrgId}, Summary={Summary}", orgId, context.LastSummary);

        if (updated is null)
            return await _agents.GetByAgentIdAsync(orgId, hired.AgentId);

        updated.Context = AgentContextDto.Parse(context.ToJson()) ?? context;
        return updated;
    }

    private async Task<HiredAgentDto?> ExecuteLenaAsync(
        Guid orgId,
        HiredAgentDto hired,
        Guid nexaUserId,
        bool scheduled,
        CancellationToken cancellationToken)
    {
        if (string.Equals(hired.Status, "paused", StringComparison.OrdinalIgnoreCase))
        {
            if (scheduled) return hired;
            throw new ArgumentException("Paused agents cannot run.");
        }

        var context = hired.Context ?? new AgentContextDto();
        LenaInterviewPlaybook.PrepareContext(context, hired.Context);

        var channel = LenaInterviewPlaybook.NormalizeChannel(context.Channel);
        var maxProcessed = LenaInterviewPlaybook.ClampMaxProcessed(context.MaxProcessed);
        var waitHours = LenaInterviewPlaybook.ClampResponseWaitHours(context.ResponseWaitHours);
        context.Channel = channel;
        context.MaxProcessed = maxProcessed;
        context.ResponseWaitHours = waitHours;

        if (channel == LenaInterviewPlaybook.ChannelEmail &&
            LenaInterviewPlaybook.ParseTemplateId(context.TemplateId) is null)
        {
            throw new ArgumentException("Pick an email template in Context before running Lena.");
        }

        if (nexaUserId == Guid.Empty && channel == LenaInterviewPlaybook.ChannelWhatsApp)
            throw new ArgumentException("A recruiter identity is required to send WhatsApp interview messages.");

        var stageId = await ResolveStageIdAsync(orgId, context.StageId, LenaInterviewPlaybook.DefaultStageName);
        if (stageId is null)
            throw new ArgumentException(
                $"No pipeline stage named \"{LenaInterviewPlaybook.DefaultStageName}\" was found. Pick a stage in Context.");

        context.StageId = stageId;

        _logger.LogInformation(
            "Lena run started. OrgId={OrgId}, JobId={JobId}, StageId={StageId}, Channel={Channel}, Max={Max}, Scheduled={Scheduled}",
            orgId,
            context.JobId,
            stageId,
            channel,
            maxProcessed,
            scheduled);

        await _agents.UpdateStatusAsync(orgId, hired.AgentId, "busy");

        DTOs.TemplateDto? emailTemplate = null;
        if (channel == LenaInterviewPlaybook.ChannelEmail)
        {
            emailTemplate = await _templates.GetAsync(orgId, LenaInterviewPlaybook.ParseTemplateId(context.TemplateId)!.Value);
            if (emailTemplate is null || !emailTemplate.IsActive)
                throw new ArgumentException("The selected email template was not found or is inactive.");
            if (!string.Equals(emailTemplate.Channel, LenaInterviewPlaybook.ChannelEmail, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Pick an email channel template for Lena interview invites.");
        }

        var page = await _applications.GetPagedAsync(
            orgId,
            context.JobId,
            candidateId: null,
            stageId,
            status: null,
            from: null,
            to: null,
            search: null,
            page: 1,
            pageSize: Math.Max(maxProcessed * 3, 50));

        var apps = page.Items
            .Select(item => (
                item.ApplicationId,
                item.CandidateId,
                item.CandidateName,
                item.CandidateEmail,
                item.JobTitle,
                item.Status))
            .ToList();

        var now = DateTimeOffset.UtcNow;
        var sweep = LenaInterviewPlaybook.Plan(context, apps, now);
        var result = new LenaRunResult
        {
            Eligible = sweep.Eligible,
            Planned = sweep.ToContact.Count,
            Skipped = sweep.Skipped,
        };
        var stamped = new List<KaiOutreachDto>();

        foreach (var decision in sweep.ToContact)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (channel == LenaInterviewPlaybook.ChannelEmail)
                {
                    if (string.IsNullOrWhiteSpace(decision.CandidateEmail) ||
                        !decision.CandidateEmail.Contains('@'))
                    {
                        result.Skipped++;
                        continue;
                    }

                    var body = LenaInterviewPlaybook.RenderTemplate(
                        emailTemplate!.Body,
                        decision.CandidateName,
                        decision.JobTitle,
                        context.Notes);
                    var subject = LenaInterviewPlaybook.RenderTemplate(
                        string.IsNullOrWhiteSpace(emailTemplate.Subject)
                            ? $"Interview — {decision.JobTitle}"
                            : emailTemplate.Subject!,
                        decision.CandidateName,
                        decision.JobTitle,
                        context.Notes);
                    var html = $"<div style=\"font-family:sans-serif;white-space:pre-wrap\">{System.Net.WebUtility.HtmlEncode(body).Replace("\n", "<br/>")}</div>";
                    await _email.SendEmailAsync(decision.CandidateEmail.Trim(), subject, body, html, cancellationToken);
                }
                else
                {
                    if (decision.Action == LenaInterviewPlaybook.ActionReminder)
                        await _whatsApp.SendFollowUpAsync(orgId, nexaUserId, decision.CandidateId, cancellationToken);
                    else
                        await _whatsApp.SendIntroductionAsync(orgId, nexaUserId, decision.CandidateId, cancellationToken);
                }

                stamped.Add(LenaInterviewPlaybook.Stamp(
                    decision.CandidateId,
                    decision.ApplicationId,
                    channel,
                    decision.Action,
                    now));

                if (decision.Action == LenaInterviewPlaybook.ActionReminder)
                    result.Reminders++;
                else
                    result.Invites++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Errors++;
                _logger.LogWarning(
                    ex,
                    "Lena failed to contact candidate {CandidateId} via {Channel}",
                    decision.CandidateId,
                    channel);
            }
        }

        LenaInterviewPlaybook.RememberOutreach(context, stamped);
        var runTokens = LenaInterviewPlaybook.EstimateTokens(result);
        AriaSourcingPlaybook.StampRun(context, now);
        LenaInterviewPlaybook.RecordReview(context, result, runTokens, now);
        context.Cadence = AriaSourcingPlaybook.NormalizeCadence(context.Cadence);

        await _agents.UpdateContextAsync(orgId, hired.AgentId, context.ToJson());

        if (runTokens > 0)
        {
            await _usage.RecordAsync(new TokenUsageEntry
            {
                OrgId = orgId,
                AgentId = hired.AgentId,
                Source = "run",
                EntityType = "lena",
                TotalTokens = (int)Math.Min(int.MaxValue, runTokens),
            }, cancellationToken);
        }

        var tokens = hired.Tokens + runTokens;
        var runs = hired.Runs + 1;
        var updated = await _agents.SetOperationalStatsAsync(orgId, hired.AgentId, runs, tokens, "active");

        _logger.LogInformation("Lena run finished. OrgId={OrgId}, Summary={Summary}", orgId, context.LastSummary);

        if (updated is null)
            return await _agents.GetByAgentIdAsync(orgId, hired.AgentId);

        updated.Context = AgentContextDto.Parse(context.ToJson()) ?? context;
        return updated;
    }

    private async Task<HiredAgentDto?> ExecuteMiraAsync(
        Guid orgId,
        HiredAgentDto hired,
        Guid nexaUserId,
        bool scheduled,
        CancellationToken cancellationToken)
    {
        if (string.Equals(hired.Status, "paused", StringComparison.OrdinalIgnoreCase))
        {
            if (scheduled) return hired;
            throw new ArgumentException("Paused agents cannot run.");
        }

        var context = hired.Context ?? new AgentContextDto();
        MiraOfferPlaybook.PrepareContext(context, hired.Context);

        var channel = MiraOfferPlaybook.NormalizeChannel(context.Channel);
        var maxProcessed = MiraOfferPlaybook.ClampMaxProcessed(context.MaxProcessed);
        var waitHours = MiraOfferPlaybook.ClampResponseWaitHours(context.ResponseWaitHours);
        context.Channel = channel;
        context.MaxProcessed = maxProcessed;
        context.ResponseWaitHours = waitHours;

        if (channel == MiraOfferPlaybook.ChannelEmail &&
            MiraOfferPlaybook.ParseTemplateId(context.TemplateId) is null)
        {
            throw new ArgumentException("Pick an email template in Context before running Mira.");
        }

        if (nexaUserId == Guid.Empty && channel == MiraOfferPlaybook.ChannelWhatsApp)
            throw new ArgumentException("A recruiter identity is required to send WhatsApp offer messages.");

        var stageId = await ResolveStageIdAsync(orgId, context.StageId, MiraOfferPlaybook.DefaultStageName);
        if (stageId is null)
            throw new ArgumentException(
                $"No pipeline stage named \"{MiraOfferPlaybook.DefaultStageName}\" was found. Pick a stage in Context.");

        context.StageId = stageId;

        _logger.LogInformation(
            "Mira run started. OrgId={OrgId}, JobId={JobId}, StageId={StageId}, Channel={Channel}, Max={Max}, Scheduled={Scheduled}",
            orgId,
            context.JobId,
            stageId,
            channel,
            maxProcessed,
            scheduled);

        await _agents.UpdateStatusAsync(orgId, hired.AgentId, "busy");

        DTOs.TemplateDto? emailTemplate = null;
        if (channel == MiraOfferPlaybook.ChannelEmail)
        {
            emailTemplate = await _templates.GetAsync(orgId, MiraOfferPlaybook.ParseTemplateId(context.TemplateId)!.Value);
            if (emailTemplate is null || !emailTemplate.IsActive)
                throw new ArgumentException("The selected email template was not found or is inactive.");
            if (!string.Equals(emailTemplate.Channel, MiraOfferPlaybook.ChannelEmail, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Pick an email channel template for Mira offer messages.");
        }

        var page = await _applications.GetPagedAsync(
            orgId,
            context.JobId,
            candidateId: null,
            stageId,
            status: null,
            from: null,
            to: null,
            search: null,
            page: 1,
            pageSize: Math.Max(maxProcessed * 3, 50));

        var apps = page.Items
            .Select(item => (
                item.ApplicationId,
                item.CandidateId,
                item.CandidateName,
                item.CandidateEmail,
                item.JobTitle,
                item.Status))
            .ToList();

        var now = DateTimeOffset.UtcNow;
        var sweep = MiraOfferPlaybook.Plan(context, apps, now);
        var result = new MiraRunResult
        {
            Eligible = sweep.Eligible,
            Planned = sweep.ToContact.Count,
            Skipped = sweep.Skipped,
        };
        var stamped = new List<KaiOutreachDto>();

        foreach (var decision in sweep.ToContact)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (channel == MiraOfferPlaybook.ChannelEmail)
                {
                    if (string.IsNullOrWhiteSpace(decision.CandidateEmail) ||
                        !decision.CandidateEmail.Contains('@'))
                    {
                        result.Skipped++;
                        continue;
                    }

                    var body = MiraOfferPlaybook.RenderTemplate(
                        emailTemplate!.Body,
                        decision.CandidateName,
                        decision.JobTitle,
                        context.Notes);
                    var subject = MiraOfferPlaybook.RenderTemplate(
                        string.IsNullOrWhiteSpace(emailTemplate.Subject)
                            ? $"Offer — {decision.JobTitle}"
                            : emailTemplate.Subject!,
                        decision.CandidateName,
                        decision.JobTitle,
                        context.Notes);
                    var html = $"<div style=\"font-family:sans-serif;white-space:pre-wrap\">{System.Net.WebUtility.HtmlEncode(body).Replace("\n", "<br/>")}</div>";
                    await _email.SendEmailAsync(decision.CandidateEmail.Trim(), subject, body, html, cancellationToken);
                }
                else
                {
                    if (decision.Action == MiraOfferPlaybook.ActionFollowUp)
                        await _whatsApp.SendFollowUpAsync(orgId, nexaUserId, decision.CandidateId, cancellationToken);
                    else
                        await _whatsApp.SendIntroductionAsync(orgId, nexaUserId, decision.CandidateId, cancellationToken);
                }

                stamped.Add(MiraOfferPlaybook.Stamp(
                    decision.CandidateId,
                    decision.ApplicationId,
                    channel,
                    decision.Action,
                    now));

                if (decision.Action == MiraOfferPlaybook.ActionFollowUp)
                    result.FollowUps++;
                else
                    result.Offers++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Errors++;
                _logger.LogWarning(
                    ex,
                    "Mira failed to contact candidate {CandidateId} via {Channel}",
                    decision.CandidateId,
                    channel);
            }
        }

        MiraOfferPlaybook.RememberOutreach(context, stamped);
        var runTokens = MiraOfferPlaybook.EstimateTokens(result);
        AriaSourcingPlaybook.StampRun(context, now);
        MiraOfferPlaybook.RecordReview(context, result, runTokens, now);
        context.Cadence = AriaSourcingPlaybook.NormalizeCadence(context.Cadence);

        await _agents.UpdateContextAsync(orgId, hired.AgentId, context.ToJson());

        if (runTokens > 0)
        {
            await _usage.RecordAsync(new TokenUsageEntry
            {
                OrgId = orgId,
                AgentId = hired.AgentId,
                Source = "run",
                EntityType = "mira",
                TotalTokens = (int)Math.Min(int.MaxValue, runTokens),
            }, cancellationToken);
        }

        var tokens = hired.Tokens + runTokens;
        var runs = hired.Runs + 1;
        var updated = await _agents.SetOperationalStatsAsync(orgId, hired.AgentId, runs, tokens, "active");

        _logger.LogInformation("Mira run finished. OrgId={OrgId}, Summary={Summary}", orgId, context.LastSummary);

        if (updated is null)
            return await _agents.GetByAgentIdAsync(orgId, hired.AgentId);

        updated.Context = AgentContextDto.Parse(context.ToJson()) ?? context;
        return updated;
    }

    private Task<Guid?> ResolveKaiStageIdAsync(Guid orgId, Guid? stageId) =>
        ResolveStageIdAsync(orgId, stageId, KaiScreeningPlaybook.DefaultStageName);

    private async Task<Guid?> ResolveStageIdAsync(Guid orgId, Guid? stageId, string defaultStageName)
    {
        var stages = await _pipeline.EnsureDefaultStagesAsync(orgId);
        if (stageId is Guid id && id != Guid.Empty)
        {
            if (stages.Any(item => item.Id == id))
                return id;
        }

        var match = stages.FirstOrDefault(item =>
            string.Equals(item.Name, defaultStageName, StringComparison.OrdinalIgnoreCase));
        return match?.Id;
    }

    private async Task<Guid?> CreateVeraMetaCampaignAsync(
        Guid orgId,
        Guid jobId,
        string jobTitle,
        string headline,
        string intro,
        string destinationUrl,
        string countryCode,
        decimal dailyBudgetUsd,
        string desiredStatus,
        CancellationToken cancellationToken)
    {
        string? imageHash = null;
        string? imageBase64 = null;
        string? imageContentType = null;
        try
        {
            var preview = await _metaAds.GenerateCreativePreviewFromJobAsync(
                orgId,
                jobId,
                creativeMessage: intro,
                aiInstructions: headline,
                cancellationToken);
            imageBase64 = preview.ImageBase64;
            imageContentType = preview.ContentType;
            if (!string.IsNullOrWhiteSpace(imageBase64))
            {
                await using var stream = new MemoryStream(Convert.FromBase64String(imageBase64));
                var file = new MemoryFormFile(stream, "vera-creative.png", imageContentType ?? "image/png");
                var uploaded = await _metaAds.UploadCreativeImageAsync(orgId, file, cancellationToken);
                imageHash = uploaded.ImageHash;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Vera could not generate/upload Meta creative for job {JobId}.", jobId);
            return null;
        }

        if (string.IsNullOrWhiteSpace(imageHash))
            return null;

        var status = desiredStatus == VeraCampaignPlaybook.StatusActive ? "ACTIVE" : "PAUSED";
        var dailyBudgetCents = (long)Math.Round(dailyBudgetUsd * 100m, MidpointRounding.AwayFromZero);
        var campaignName = VeraCampaignPlaybook.CampaignName(jobTitle, VeraCampaignPlaybook.PlatformMeta, DateTimeOffset.UtcNow);
        var message = intro.Length > 500 ? intro[..500] : intro;

        var request = new CreateMetaCampaignRequest
        {
            TenantId = orgId.ToString("D"),
            JobId = jobId,
            DestinationType = "job_post_url",
            ImageBase64 = imageBase64,
            ImageContentType = imageContentType,
            Campaign = new MetaCampaignGraphPayload
            {
                Name = campaignName,
                Objective = "OUTCOME_TRAFFIC",
                Status = status,
                SpecialAdCategories = ["EMPLOYMENT"],
                SpecialAdCategoryCountry = [countryCode],
                IsAdsetBudgetSharingEnabled = false,
            },
            AdSet = new MetaAdSetGraphPayload
            {
                Name = $"{campaignName} · Ad set",
                DailyBudget = dailyBudgetCents,
                BillingEvent = "IMPRESSIONS",
                OptimizationGoal = "LINK_CLICKS",
                BidStrategy = "LOWEST_COST_WITHOUT_CAP",
                Status = status,
                Targeting = new MetaTargetingGraphPayload
                {
                    GeoLocations = new MetaGeoLocationsGraphPayload
                    {
                        Countries = [countryCode],
                    },
                    AgeMin = 18,
                    AgeMax = 65,
                    PublisherPlatforms = ["facebook", "instagram"],
                    TargetingAutomation = new MetaTargetingAutomationGraphPayload { AdvantageAudience = 0 },
                },
            },
            Creative = new MetaAdCreativeGraphPayload
            {
                Name = $"{campaignName} · Creative",
                ObjectStorySpec = new MetaObjectStorySpecGraphPayload
                {
                    LinkData = new MetaLinkDataGraphPayload
                    {
                        Message = message,
                        Name = headline.Length > 255 ? headline[..255] : headline,
                        Link = destinationUrl,
                        ImageHash = imageHash,
                        CallToAction = new MetaCallToActionGraphPayload { Type = "LEARN_MORE" },
                    },
                },
            },
            Ad = new MetaAdGraphPayload
            {
                Name = $"{campaignName} · Ad",
                Status = status,
            },
        };

        var created = await _metaAds.CreateCampaignAsync(orgId, request, cancellationToken);
        return created.LocalRecordId;
    }

    private async Task<bool> IsMetaAdsReadyAsync(Guid orgId)
    {
        var rows = await _sourcing.GetSourceConnectionsAsync(orgId);
        var row = rows.FirstOrDefault(item =>
            string.Equals(item.SourceTypeCode, "meta_ads", StringComparison.OrdinalIgnoreCase));
        return row is { IsConnected: true, IsActive: true } && !string.IsNullOrWhiteSpace(row.ConfigJson);
    }

    private async Task<List<SourcingLeadListItemDto>> LoadProcessableLeadsAsync(
        Guid orgId,
        Guid? jobId,
        int maxProcessed)
    {
        var collected = new List<SourcingLeadListItemDto>(maxProcessed);
        var seen = new HashSet<Guid>();

        foreach (var status in AriaSourcingPlaybook.ProcessableLeadStatuses)
        {
            if (collected.Count >= maxProcessed) break;

            var pageSize = Math.Min(100, maxProcessed - collected.Count);
            var page = await _sourcing.GetLeadsAsync(
                orgId,
                jobId,
                campaignId: null,
                sourceTypeCode: null,
                status: status,
                search: null,
                availability: null,
                minFitScore: null,
                page: 1,
                pageSize: pageSize,
                sort: "created_at",
                dir: "asc");

            foreach (var lead in page.Items)
            {
                if (!seen.Add(lead.Id)) continue;
                collected.Add(lead);
                if (collected.Count >= maxProcessed) break;
            }
        }

        return collected;
    }
}
