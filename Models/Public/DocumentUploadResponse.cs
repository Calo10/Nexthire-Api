using System.Text.Json.Serialization;

namespace nexthire_api.Models.Public;

public class DocumentUploadResponse
{
    [JsonPropertyName("documentId")]
    public string? DocumentId { get; set; }

    // Kept optional for debugging/forward-compat; not stored in DB for apply flow.
    [JsonPropertyName("originalUrl")]
    public string? OriginalUrl { get; set; }
}

