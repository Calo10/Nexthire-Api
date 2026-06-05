using System.Text.Json;
using nexthire_api.DTOs;

namespace nexthire_api.Helpers;

public static class WhatsAppDynamicApplyHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly HashSet<string> FullNameKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "full_name",
        "nombre",
        "nombre_completo",
        "name"
    };

    private static readonly HashSet<string> ResumeKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "resume",
        "cv",
        "curriculum",
        "curriculum_vitae"
    };

    public static string BuildDynamicAnswersJson(IReadOnlyList<WhatsAppApplySessionAnswer> answers)
    {
        var payload = new
        {
            version = 1,
            answers = answers.Select(a => new
            {
                questionId = a.QuestionId,
                key = a.Key,
                label = a.Label,
                value = a.Value,
                answeredAtUtc = a.AnsweredAtUtc
            })
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    public static (string? FullName, string? ResumeUrl) ExtractLeadFieldsFromAnswers(
        IReadOnlyList<WhatsAppApplySessionAnswer> answers,
        IReadOnlyList<JobBotQuestionDto> questions)
    {
        string? fullName = null;
        string? resumeUrl = null;
        var questionById = questions.ToDictionary(q => q.Id);

        foreach (var answer in answers)
        {
            if (!questionById.TryGetValue(answer.QuestionId, out var question))
                continue;

            if (fullName == null && FullNameKeys.Contains(question.QuestionKey))
                fullName = answer.Value;

            if (resumeUrl == null
                && (ResumeKeys.Contains(question.QuestionKey)
                    || string.Equals(question.AnswerType, JobBotQuestionAnswerTypes.File, StringComparison.OrdinalIgnoreCase)))
            {
                resumeUrl = answer.Value;
            }
        }

        return (fullName, resumeUrl);
    }

    public static string? TryGetFileAnswerUrl(WhatsAppInboundMessageDto inbound)
    {
        WhatsAppInboundMediaHelper.EnsureMediaPopulated(inbound);

        foreach (var item in inbound.Media ?? [])
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

    public static string FormatQuestionPrompt(JobBotQuestionDto question, bool isFirstQuestion)
    {
        if (isFirstQuestion)
        {
            return $"¡Hola! Veo que quieres aplicar a esta vacante. {question.QuestionText.Trim()}";
        }

        return question.QuestionText.Trim();
    }
}
