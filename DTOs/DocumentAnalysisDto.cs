namespace nexthire_api.DTOs;

public sealed class DocumentAnalysisEnvelopeDto
{
    public bool Cached { get; set; }
    public DocumentAnalysisDto? Analysis { get; set; }
}

public sealed class DocumentAnalysisDto
{
    public string? Summary { get; set; }
    public string? Language { get; set; }
    public string? DocType { get; set; }
    public IReadOnlyList<string>? KeyPoints { get; set; }
    public IReadOnlyList<string>? Warnings { get; set; }
    public int ExtractedTextChars { get; set; }
    public DateTimeOffset? CreatedAtUtc { get; set; }
}

