using System.Globalization;
using System.ClientModel;
using System.Text;
using System.Text.Json;
using nexthire_api.DTOs;
using nexthire_api.DTOs.Sourcing;
using nexthire_api.Repositories;
using nexthire_api.Repositories.Sourcing;
using OpenAI.Chat;

namespace nexthire_api.Services.Sourcing;

public class SourcingLeadFitScoringAgent : ISourcingLeadFitScoringAgent
{
    private const int MaxQualificationNotesLength = 4000;

    private readonly IOrganizationSettingsRepository _orgSettings;
    private readonly IJobRepository _jobs;
    private readonly IDocumentService _documents;
    private readonly IWhatsAppAiService _ai;
    private readonly ISourcingRepository _sourcing;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SourcingLeadFitScoringAgent> _logger;

    public SourcingLeadFitScoringAgent(
        IOrganizationSettingsRepository orgSettings,
        IJobRepository jobs,
        IDocumentService documents,
        IWhatsAppAiService ai,
        ISourcingRepository sourcing,
        IConfiguration configuration,
        ILogger<SourcingLeadFitScoringAgent> logger)
    {
        _orgSettings = orgSettings;
        _jobs = jobs;
        _documents = documents;
        _ai = ai;
        _sourcing = sourcing;
        _configuration = configuration;
        _logger = logger;
    }

    public Task<string?> FetchResumeSummaryAsync(
        Guid orgId,
        string? resumeDocumentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resumeDocumentId)
            || !Guid.TryParse(resumeDocumentId.Trim(), out var documentId))
        {
            return Task.FromResult<string?>(null);
        }

        return FetchResumeSummaryByDocumentIdAsync(orgId, documentId, cancellationToken);
    }

    public async Task ScoreLeadIfEnabledAsync(
        Guid orgId,
        SourcingLeadDetailDto lead,
        string? resumeSummary = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!lead.JobId.HasValue)
            {
                _logger.LogDebug(
                    "Skipping fit scoring for lead {LeadId}: no job_id.",
                    lead.Id);
                return;
            }

            if (lead.FitScore.HasValue)
            {
                _logger.LogDebug(
                    "Skipping fit scoring for lead {LeadId}: fit_score already set.",
                    lead.Id);
                return;
            }

            var settings = await _orgSettings.GetAsync(orgId);
            if (settings is not { FitScoringEnabled: true })
                return;

            var job = await _jobs.GetByIdAsync(orgId, lead.JobId.Value);
            if (job is null)
            {
                _logger.LogWarning(
                    "Skipping fit scoring for lead {LeadId}: job {JobId} not found.",
                    lead.Id,
                    lead.JobId);
                return;
            }

            var resolvedResumeSummary = resumeSummary
                ?? await TryGetResumeSummaryAsync(orgId, lead, cancellationToken);
            var dynamicAnswers = FormatDynamicAnswers(lead.DynamicAnswersJson);
            var systemPrompt = BuildSystemPrompt(job.Language);
            var userPrompt = BuildUserPrompt(job, lead, dynamicAnswers, resolvedResumeSummary);

            var response = await _ai.GenerateReplyAsync(
                systemPrompt,
                [new UserChatMessage(userPrompt)],
                ResolveChatDeployment(),
                cancellationToken);

            if (!TryParseFitScoreResponse(response, out var fitScore, out var qualificationNotes))
            {
                _logger.LogWarning(
                    "Fit scoring returned unparseable response for lead {LeadId}. Response={Response}",
                    lead.Id,
                    TruncateForLog(response));
                return;
            }

            SourcingValidation.ValidateFitScore(fitScore);

            var updated = await _sourcing.UpdateLeadFitScoreAsync(
                orgId,
                lead.Id,
                fitScore,
                qualificationNotes);

            if (!updated)
            {
                _logger.LogWarning(
                    "Fit scoring could not update lead {LeadId} in database.",
                    lead.Id);
                return;
            }

            _logger.LogInformation(
                "Fit score computed for lead {LeadId}. JobId={JobId} FitScore={FitScore}",
                lead.Id,
                lead.JobId,
                fitScore);
        }
        catch (ClientResultException ex) when (ex.Status == 401)
        {
            _logger.LogWarning(
                ex,
                "Fit scoring: Azure OpenAI rejected the API key (401). " +
                "Set a valid AzureOpenAI:ApiKey in appsettings.Development.local.json " +
                "for the nexa-open-ai resource (Keys and Endpoint in Azure Portal). LeadId={LeadId}",
                lead.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Fit scoring failed for lead {LeadId} in org {OrgId}.",
                lead.Id,
                orgId);
        }
    }

    private async Task<string?> TryGetResumeSummaryAsync(
        Guid orgId,
        SourcingLeadDetailDto lead,
        CancellationToken cancellationToken)
    {
        var documentIdText = lead.ResumeUrl?.Trim();
        if (string.IsNullOrWhiteSpace(documentIdText)
            || !Guid.TryParse(documentIdText, out var documentId))
        {
            return null;
        }

        return await FetchResumeSummaryByDocumentIdAsync(orgId, documentId, lead.Id, cancellationToken);
    }

    private async Task<string?> FetchResumeSummaryByDocumentIdAsync(
        Guid orgId,
        Guid documentId,
        Guid? leadIdForLogs,
        CancellationToken cancellationToken)
    {
        try
        {
            var envelope = await _documents.GetResumeAnalysisAsync(orgId, documentId, cancellationToken);
            var analysis = envelope.Analysis;
            if (analysis is null)
                return null;

            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(analysis.Summary))
                builder.AppendLine(analysis.Summary.Trim());

            if (analysis.KeyPoints is { Count: > 0 })
            {
                builder.AppendLine("Puntos clave:");
                foreach (var point in analysis.KeyPoints.Take(12))
                {
                    if (!string.IsNullOrWhiteSpace(point))
                        builder.AppendLine($"- {point.Trim()}");
                }
            }

            var text = builder.ToString().Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Resume analysis unavailable for lead {LeadId}, document {DocumentId}.",
                leadIdForLogs,
                documentId);
            return null;
        }
    }

    private Task<string?> FetchResumeSummaryByDocumentIdAsync(
        Guid orgId,
        Guid documentId,
        CancellationToken cancellationToken)
        => FetchResumeSummaryByDocumentIdAsync(orgId, documentId, leadIdForLogs: null, cancellationToken);

    private static string BuildSystemPrompt(string? jobLanguage)
    {
        var spanish = !string.Equals(jobLanguage, JobLanguageCodes.English, StringComparison.OrdinalIgnoreCase);
        return spanish
            ? """
              Eres un reclutador experto. Evalúa qué tan bien encaja un candidato con una vacante usando la descripción del puesto, las respuestas del candidato y el resumen de su CV si está disponible.
              Responde ÚNICAMENTE con JSON válido, sin markdown ni texto adicional, con esta forma exacta:
              {"fitScore":<número entero de 0 a 100>,"qualificationNotes":"<máximo 2 oraciones concisas en español explicando fortalezas, brechas y recomendación>"}
              """
            : """
              You are an expert recruiter. Evaluate how well a candidate fits a job using the job description, the candidate's answers, and their resume summary when available.
              Reply ONLY with valid JSON, no markdown or extra text, in exactly this shape:
              {"fitScore":<integer from 0 to 100>,"qualificationNotes":"<at most 2 concise sentences in English explaining strengths, gaps, and recommendation>"}
              """;
    }

    private static string BuildUserPrompt(
        JobDto job,
        SourcingLeadDetailDto lead,
        string? dynamicAnswers,
        string? resumeSummary)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## Job post");
        builder.AppendLine($"Title: {job.Title}");
        if (!string.IsNullOrWhiteSpace(job.Department))
            builder.AppendLine($"Department: {job.Department}");
        if (!string.IsNullOrWhiteSpace(job.Location))
            builder.AppendLine($"Location: {job.Location}");
        if (!string.IsNullOrWhiteSpace(job.Description))
            builder.AppendLine($"Description: {job.Description.Trim()}");

        builder.AppendLine();
        builder.AppendLine("## Candidate");
        if (!string.IsNullOrWhiteSpace(lead.FullName))
            builder.AppendLine($"Name: {lead.FullName}");
        else if (!string.IsNullOrWhiteSpace(lead.FirstName) || !string.IsNullOrWhiteSpace(lead.LastName))
            builder.AppendLine($"Name: {lead.FirstName} {lead.LastName}".Trim());
        if (!string.IsNullOrWhiteSpace(lead.Email))
            builder.AppendLine($"Email: {lead.Email}");
        if (!string.IsNullOrWhiteSpace(lead.Phone))
            builder.AppendLine($"Phone: {lead.Phone}");

        if (!string.IsNullOrWhiteSpace(dynamicAnswers))
        {
            builder.AppendLine();
            builder.AppendLine("## Screening answers");
            builder.AppendLine(dynamicAnswers);
        }

        if (!string.IsNullOrWhiteSpace(resumeSummary))
        {
            builder.AppendLine();
            builder.AppendLine("## Resume summary");
            builder.AppendLine(resumeSummary);
        }

        return builder.ToString().Trim();
    }

    private static string? FormatDynamicAnswers(string? dynamicAnswersJson)
    {
        if (string.IsNullOrWhiteSpace(dynamicAnswersJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(dynamicAnswersJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("answers", out var answers)
                || answers.ValueKind != JsonValueKind.Array)
            {
                return dynamicAnswersJson.Trim();
            }

            var lines = new List<string>();
            foreach (var answer in answers.EnumerateArray())
            {
                var label = answer.TryGetProperty("label", out var labelNode)
                    ? labelNode.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(label)
                    && answer.TryGetProperty("key", out var keyNode))
                {
                    label = keyNode.GetString();
                }

                if (string.IsNullOrWhiteSpace(label))
                    continue;

                var value = answer.TryGetProperty("value", out var valueNode)
                    ? FormatAnswerValue(valueNode)
                    : null;

                if (string.IsNullOrWhiteSpace(value))
                    value = "(sin respuesta)";

                lines.Add($"- {label.Trim()}: {value}");
            }

            return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
        }
        catch
        {
            return dynamicAnswersJson.Trim();
        }
    }

    private static string? FormatAnswerValue(JsonElement valueNode)
    {
        return valueNode.ValueKind switch
        {
            JsonValueKind.String => valueNode.GetString(),
            JsonValueKind.Number => valueNode.GetRawText(),
            JsonValueKind.True => "yes",
            JsonValueKind.False => "no",
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => valueNode.GetRawText()
        };
    }

    private static bool TryParseFitScoreResponse(
        string content,
        out decimal fitScore,
        out string qualificationNotes)
    {
        fitScore = default;
        qualificationNotes = string.Empty;

        if (string.IsNullOrWhiteSpace(content))
            return false;

        var json = ExtractJsonObject(content);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("fitScore", out var scoreNode))
                return false;

            decimal parsedScore;
            if (scoreNode.ValueKind == JsonValueKind.Number)
            {
                if (!scoreNode.TryGetDecimal(out parsedScore))
                    return false;
            }
            else if (scoreNode.ValueKind == JsonValueKind.String)
            {
                if (!decimal.TryParse(scoreNode.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out parsedScore))
                    return false;
            }
            else
            {
                return false;
            }

            if (!root.TryGetProperty("qualificationNotes", out var notesNode))
                return false;

            var notes = notesNode.ValueKind == JsonValueKind.String
                ? notesNode.GetString()
                : notesNode.GetRawText();

            if (string.IsNullOrWhiteSpace(notes))
                return false;

            fitScore = Math.Round(parsedScore, 0, MidpointRounding.AwayFromZero);
            qualificationNotes = notes.Trim();
            if (qualificationNotes.Length > MaxQualificationNotesLength)
                qualificationNotes = qualificationNotes[..MaxQualificationNotesLength];

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ExtractJsonObject(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = trimmed.IndexOf('\n');
            if (firstNewLine >= 0)
                trimmed = trimmed[(firstNewLine + 1)..];
            var fenceEnd = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0)
                trimmed = trimmed[..fenceEnd];
            trimmed = trimmed.Trim();
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        return trimmed[start..(end + 1)];
    }

    private string? ResolveChatDeployment()
    {
        foreach (var configKey in new[] { "AzureOpenAI:ChatDeployment", "AzureOpenAI:Deployment" })
        {
            var value = _configuration[configKey]?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string TruncateForLog(string? value, int maxLength = 500)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value ?? string.Empty;

        return value[..maxLength] + "...";
    }
}
