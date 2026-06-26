namespace nexthire_api.Helpers;

public static class OrganizationLogoValidator
{
    private const int MaxLogoBytes = 512 * 1024;

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/jpg",
        "image/webp"
    };

    public static string? ValidateAndNormalize(string? logoBase64, string? logoContentType, bool removeLogo)
    {
        if (removeLogo)
            return null;

        if (string.IsNullOrWhiteSpace(logoBase64))
            return null;

        var trimmed = logoBase64.Trim();
        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = trimmed.IndexOf(',');
            if (comma < 0)
                throw new ArgumentException("logoBase64 data URI is invalid.");

            trimmed = trimmed[(comma + 1)..].Trim();
        }

        if (string.IsNullOrWhiteSpace(trimmed))
            throw new ArgumentException("logoBase64 is required when logoContentType is provided.");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(trimmed);
        }
        catch (FormatException)
        {
            throw new ArgumentException("logoBase64 is not valid base64.");
        }

        if (bytes.Length == 0)
            throw new ArgumentException("logoBase64 cannot be empty.");

        if (bytes.Length > MaxLogoBytes)
            throw new ArgumentException($"logoBase64 exceeds the maximum size of {MaxLogoBytes / 1024} KB.");

        if (string.IsNullOrWhiteSpace(logoContentType))
            throw new ArgumentException("logoContentType is required when logoBase64 is provided.");

        var normalizedContentType = logoContentType.Trim().ToLowerInvariant();
        if (normalizedContentType == "image/jpg")
            normalizedContentType = "image/jpeg";

        if (!AllowedContentTypes.Contains(normalizedContentType))
            throw new ArgumentException("logoContentType must be image/png, image/jpeg, or image/webp.");

        return trimmed;
    }
}
