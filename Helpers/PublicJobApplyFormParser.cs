using System.Text.Json;
using Microsoft.AspNetCore.Http;
using nexthire_api.DTOs;
using nexthire_api.Services;

namespace nexthire_api.Helpers;

public sealed class PublicJobApplyFormData
{
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public string? Source { get; init; }
    public string SourceTypeCode { get; init; } = "public_apply";
    public string? DynamicAnswersJson { get; init; }
    public IFormFile? Resume { get; init; }
    public IReadOnlyDictionary<string, IFormFile> AnswerFilesByQuestionId { get; init; }
        = new Dictionary<string, IFormFile>(StringComparer.OrdinalIgnoreCase);

    public string FullName => string.Join(' ',
        new[] { FirstName, LastName }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));
}

public static class PublicJobApplyFormParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static (PublicJobApplyFormData? Data, string? Error) Parse(IFormCollection form, IFormFileCollection files)
    {
        var firstName = GetFormValue(form, "FirstName", "firstName");
        var lastName = GetFormValue(form, "LastName", "lastName");
        var email = GetFormValue(form, "Email", "email");
        var phone = GetFormValue(form, "Phone", "phone");
        var source = GetFormValue(form, "Source", "source");
        var sourceTypeCode = GetFormValue(form, "SourceTypeCode", "sourceTypeCode") ?? "public_apply";
        var dynamicAnswersJson = GetFormJsonValue(form, "dynamicAnswersJson", "DynamicAnswersJson");

        if (string.IsNullOrWhiteSpace(firstName))
            return (null, "firstName is required.");
        if (string.IsNullOrWhiteSpace(lastName))
            return (null, "lastName is required.");
        if (string.IsNullOrWhiteSpace(email))
            return (null, "email is required.");

        var resume = FindResumeFile(files);
        var answerFiles = FindAnswerFiles(files);

        return (new PublicJobApplyFormData
        {
            FirstName = firstName.Trim(),
            LastName = lastName.Trim(),
            Email = email.Trim(),
            Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(),
            Source = string.IsNullOrWhiteSpace(source) ? null : source.Trim(),
            SourceTypeCode = sourceTypeCode.Trim(),
            DynamicAnswersJson = dynamicAnswersJson,
            Resume = resume,
            AnswerFilesByQuestionId = answerFiles
        }, null);
    }

    public static string? ValidateRequiredQuestionAnswers(
        IReadOnlyList<JobBotQuestionDto> questions,
        string? dynamicAnswersJson,
        IFormFile? resume,
        IReadOnlyDictionary<string, IFormFile> answerFilesByQuestionId)
    {
        var requiredQuestions = questions
            .Where(q => q.IsActive && q.IsRequired)
            .ToList();

        if (requiredQuestions.Count == 0)
            return null;

        Dictionary<Guid, string>? answersByQuestionId = null;
        if (!string.IsNullOrWhiteSpace(dynamicAnswersJson)
            && TryDeserializeDynamicAnswers(dynamicAnswersJson, out var payload)
            && payload?.Answers is { Count: > 0 })
        {
            answersByQuestionId = payload.Answers
                .Where(a => !string.IsNullOrWhiteSpace(a.Value))
                .ToDictionary(a => a.QuestionId, a => a.Value.Trim());
        }

        var hasResume = resume is { Length: > 0 };

        foreach (var question in requiredQuestions)
        {
            var isFileQuestion = string.Equals(
                question.AnswerType,
                JobBotQuestionAnswerTypes.File,
                StringComparison.OrdinalIgnoreCase);

            if (isFileQuestion)
            {
                if (hasResume)
                    continue;

                if (answerFilesByQuestionId.TryGetValue(question.Id.ToString(), out var answerFile)
                    && answerFile.Length > 0)
                    continue;

                if (answersByQuestionId?.ContainsKey(question.Id) == true)
                    continue;

                return "Resume or answerFile_{questionId} is required.";
            }

            if (answersByQuestionId?.ContainsKey(question.Id) == true)
                continue;

            return $"{question.QuestionKey} is required.";
        }

        return null;
    }

    public static async Task<(string? DynamicAnswersJson, string? ResumeDocumentId)> ProcessApplyFilesAsync(
        Guid orgId,
        string? dynamicAnswersJson,
        IReadOnlyList<JobBotQuestionDto> questions,
        IReadOnlyDictionary<string, IFormFile> answerFilesByQuestionId,
        IFormFile? resumeFile,
        IResumeDocumentsUploader resumeUploader,
        CancellationToken cancellationToken)
    {
        string? resumeDocumentId = null;
        PublicDynamicAnswersPayload? payload = null;

        if (!string.IsNullOrWhiteSpace(dynamicAnswersJson))
        {
            if (!TryDeserializeDynamicAnswers(dynamicAnswersJson, out payload))
                throw new ArgumentException("dynamicAnswersJson is not valid JSON.");
        }

        if (payload?.Answers is { Count: > 0 })
        {
            var questionById = questions.ToDictionary(q => q.Id);
            var updatedAnswers = new List<PublicDynamicAnswer>();

            foreach (var answer in payload.Answers)
            {
                if (!questionById.TryGetValue(answer.QuestionId, out var question))
                {
                    updatedAnswers.Add(answer);
                    continue;
                }

                var isFileQuestion = string.Equals(
                    question.AnswerType,
                    JobBotQuestionAnswerTypes.File,
                    StringComparison.OrdinalIgnoreCase);

                if (!isFileQuestion || !string.IsNullOrWhiteSpace(answer.Value))
                {
                    if (isFileQuestion && !string.IsNullOrWhiteSpace(answer.Value))
                        resumeDocumentId ??= answer.Value.Trim();

                    updatedAnswers.Add(answer);
                    continue;
                }

                if (answerFilesByQuestionId.TryGetValue(answer.QuestionId.ToString(), out var answerFile)
                    && answerFile.Length > 0)
                {
                    answer.Value = await resumeUploader.UploadResumeAsync(orgId, answerFile, cancellationToken);
                    resumeDocumentId ??= answer.Value;
                    updatedAnswers.Add(answer);
                    continue;
                }

                updatedAnswers.Add(answer);
            }

            foreach (var question in questions.Where(q =>
                         string.Equals(q.AnswerType, JobBotQuestionAnswerTypes.File, StringComparison.OrdinalIgnoreCase)))
            {
                var answer = updatedAnswers.FirstOrDefault(a => a.QuestionId == question.Id);
                if (answer is null || !string.IsNullOrWhiteSpace(answer.Value) || resumeFile is null)
                    continue;

                answer.Value = await resumeUploader.UploadResumeAsync(orgId, resumeFile, cancellationToken);
                resumeDocumentId ??= answer.Value;
            }

            dynamicAnswersJson = JsonSerializer.Serialize(
                new PublicDynamicAnswersPayload { Version = payload.Version, Answers = updatedAnswers },
                JsonOptions);
        }

        if (resumeFile is not null)
            resumeDocumentId ??= await resumeUploader.UploadResumeAsync(orgId, resumeFile, cancellationToken);

        return (dynamicAnswersJson, resumeDocumentId);
    }

    private static IFormFile? FindResumeFile(IFormFileCollection files)
    {
        foreach (var file in files)
        {
            if (file.Length <= 0)
                continue;

            if (string.Equals(file.Name, "Resume", StringComparison.OrdinalIgnoreCase)
                || string.Equals(file.Name, "resume", StringComparison.OrdinalIgnoreCase))
                return file;
        }

        return null;
    }

    private static Dictionary<string, IFormFile> FindAnswerFiles(IFormFileCollection files)
    {
        var map = new Dictionary<string, IFormFile>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            if (file.Length <= 0)
                continue;

            var name = file.Name;
            if (name.StartsWith("answerFile_", StringComparison.OrdinalIgnoreCase))
            {
                map[name["answerFile_".Length..]] = file;
                continue;
            }

            if (name.StartsWith("AnswerFile_", StringComparison.OrdinalIgnoreCase))
                map[name["AnswerFile_".Length..]] = file;
        }

        return map;
    }

    private static string? GetFormValue(IFormCollection form, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!form.TryGetValue(key, out var value))
                continue;

            var normalized = NormalizeFormFieldValue(value.ToString());
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }

        return null;
    }

    /// <summary>Never split on commas — JSON contains many.</summary>
    private static string? GetFormJsonValue(IFormCollection form, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!form.TryGetValue(key, out var value))
                continue;

            var raw = value.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            if (TryDeserializeDynamicAnswers(raw, out _))
                return raw;

            // Same field sent twice in multipart → two JSON blobs comma-joined
            var dupIdx = raw.IndexOf("}{", StringComparison.Ordinal);
            while (dupIdx > 0)
            {
                var candidate = raw[..(dupIdx + 1)].Trim();
                if (TryDeserializeDynamicAnswers(candidate, out _))
                    return candidate;
                dupIdx = raw.IndexOf("}{", dupIdx + 2, StringComparison.Ordinal);
            }
        }

        return null;
    }

    private static bool TryDeserializeDynamicAnswers(string json, out PublicDynamicAnswersPayload? payload)
    {
        payload = null;
        try
        {
            payload = JsonSerializer.Deserialize<PublicDynamicAnswersPayload>(json, JsonOptions);
            return payload?.Answers is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Duplicate scalar fields (email + Email) arrive comma-separated in multipart forms.
    /// </summary>
    internal static string? NormalizeFormFieldValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim();
        var comma = trimmed.IndexOf(',');
        if (comma > 0)
            trimmed = trimmed[..comma].Trim();

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private sealed class PublicDynamicAnswersPayload
    {
        public int Version { get; set; } = 1;
        public List<PublicDynamicAnswer> Answers { get; set; } = [];
    }

    private sealed class PublicDynamicAnswer
    {
        public Guid QuestionId { get; set; }
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public DateTimeOffset AnsweredAtUtc { get; set; }
    }
}
