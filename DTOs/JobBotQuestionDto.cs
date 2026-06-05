using System.ComponentModel.DataAnnotations;

namespace nexthire_api.DTOs;

public class JobBotQuestionDto
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public string QuestionKey { get; set; } = string.Empty;
    public string QuestionText { get; set; } = string.Empty;
    public string AnswerType { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsRequired { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class CreateJobBotQuestionRequestDto
{
    [Required]
    [StringLength(80, MinimumLength = 2)]
    public string QuestionKey { get; set; } = string.Empty;

    [Required]
    [StringLength(2000, MinimumLength = 2)]
    public string QuestionText { get; set; } = string.Empty;

    [Required]
    [StringLength(40)]
    public string AnswerType { get; set; } = string.Empty;

    public int? SortOrder { get; set; }
    public bool? IsRequired { get; set; }
    public bool? IsActive { get; set; }
}

public class UpdateJobBotQuestionRequestDto
{
    [Required]
    [StringLength(2000, MinimumLength = 2)]
    public string QuestionText { get; set; } = string.Empty;

    [Required]
    [StringLength(40)]
    public string AnswerType { get; set; } = string.Empty;

    public int? SortOrder { get; set; }
    public bool? IsRequired { get; set; }
    public bool? IsActive { get; set; }
}

public static class JobBotQuestionAnswerTypes
{
    public const string Text = "text";
    public const string Number = "number";
    public const string YesNo = "yes_no";
    public const string File = "file";

    public static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        Text,
        Number,
        YesNo,
        File
    };

    public static string? NormalizeOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim().ToLowerInvariant();
        return Allowed.Contains(normalized) ? normalized : null;
    }
}
