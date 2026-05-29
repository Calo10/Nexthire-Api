using System.Text.RegularExpressions;

namespace nexthire_api.Helpers;

public static partial class ResumeDocumentIdHelper
{
    private static readonly Regex GuidRegex = GuidInTextRegex();

    /// <summary>
    /// Resolves the documents-function id stored in <c>candidates.resume_url</c> / lead resume field.
    /// Accepts a raw GUID, or a URL containing <c>/documents/{id}</c>.
    /// </summary>
    public static bool TryResolve(string? raw, out Guid documentId)
    {
        documentId = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var value = raw.Trim().Trim('"');
        if (Guid.TryParse(value, out documentId))
            return true;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (!string.Equals(segments[i], "documents", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (Guid.TryParse(segments[i + 1], out documentId))
                    return true;
            }
        }

        var match = GuidRegex.Match(value);
        if (match.Success && Guid.TryParse(match.Value, out documentId))
            return true;

        return false;
    }

    [GeneratedRegex(
        @"[0-9a-fA-F]{8}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{12}",
        RegexOptions.Compiled)]
    private static partial Regex GuidInTextRegex();
}
