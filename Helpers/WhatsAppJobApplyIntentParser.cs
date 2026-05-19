using System.Text.RegularExpressions;

namespace nexthire_api.Helpers;

public static partial class WhatsAppJobApplyIntentParser
{
    public const string SourceTypeWhatsApp = "whatsapp";

    /// <summary>
    /// True when the message contains the apply phrase and a non-whitespace token after "job post"
    /// (e.g. "I want to apply to job post {id}"). The token may still not be a valid GUID.
    /// </summary>
    public static bool LooksLikeApplyJobPostMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;
        return ApplyJobPostPhraseRegex().IsMatch(body.Trim());
    }

    /// <summary>
    /// Parses the job post id after "apply to job post". The id must be a valid GUID (hex only per segment).
    /// </summary>
    public static bool TryParseJobPostId(string? body, out Guid jobId)
    {
        jobId = default;
        if (string.IsNullOrWhiteSpace(body))
            return false;

        var m = ApplyJobPostPhraseRegex().Match(body.Trim());
        if (!m.Success)
            return false;

        var token = m.Groups[1].Value.Trim().TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '"', '\'');
        return Guid.TryParse(token, out jobId);
    }

    /// <summary>
    /// Matches "… apply to job post &lt;token&gt;" and captures the token (validated separately with Guid.TryParse).
    /// </summary>
    [GeneratedRegex(
        @"\bapply\s+to\s+job\s+post\s+(\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApplyJobPostPhraseRegex();
}
