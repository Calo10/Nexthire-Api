using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace nexthire_api.DTOs;

/// <summary>Multipart body for POST /api/marketing/meta/creative-image (Swagger-compatible).</summary>
public class UploadMetaCreativeImageForm
{
    [Required]
    public IFormFile File { get; set; } = default!;
}

/// <summary>Body for POST /api/marketing/meta/creative-image/demo — AI preview from job copy.</summary>
public class GenerateMetaCreativePreviewRequest
{
    [Required]
    public Guid JobId { get; set; }
}

/// <summary>Generated ad image for UI preview (not uploaded to Meta until the client calls creative-image).</summary>
public class GenerateMetaCreativePreviewResponse
{
    public string ImageBase64 { get; set; } = string.Empty;
    public string ContentType { get; set; } = "image/png";
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>How this size maps to typical Meta feed / square placements.</summary>
    public string MetaCreativeHint { get; set; } = string.Empty;

    /// <summary>Prompt passed to the image model (after LLM refinement).</summary>
    public string ImagePrompt { get; set; } = string.Empty;
}

public class UploadMetaImageResponse
{
    public string ImageHash { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;

    /// <summary>Same hash as <see cref="ImageHash"/> for clients expecting snake_case.</summary>
    [JsonPropertyName("image_hash")]
    public string ImageHashSnake => ImageHash;
}

public class CreateMetaCampaignRequest
{
    [Required]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    public Guid JobId { get; set; }

    [Required]
    [MaxLength(250)]
    public string CampaignName { get; set; } = string.Empty;

    [Required]
    public string Objective { get; set; } = "OUTCOME_TRAFFIC";

    [Range(1, int.MaxValue)]
    public int DailyBudget { get; set; }

    [Required]
    [MaxLength(10)]
    public string Country { get; set; } = string.Empty;

    [Range(13, 65)]
    public int AgeMin { get; set; }

    [Range(13, 65)]
    public int AgeMax { get; set; }

    [Required]
    [MinLength(1)]
    public string[] Platforms { get; set; } = Array.Empty<string>();

    [Required]
    [RegularExpression("^(whatsapp|job_post_url)$")]
    public string DestinationType { get; set; } = string.Empty;

    [Required]
    public string DestinationUrl { get; set; } = string.Empty;

    public string? WhatsappMessage { get; set; }

    [Required]
    public string AdText { get; set; } = string.Empty;

    [Required]
    public string CtaType { get; set; } = "LEARN_MORE";

    [Required]
    [MaxLength(200)]
    public string ImageHash { get; set; } = string.Empty;

    /// <summary>Ignored for creation — objects are always PAUSED in Meta.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Status { get; set; }
}

public class CreateMetaCampaignResponse
{
    public string CampaignId { get; set; } = string.Empty;
    public string AdSetId { get; set; } = string.Empty;
    public string CreativeId { get; set; } = string.Empty;
    public string AdId { get; set; } = string.Empty;
    public string AdAccountId { get; set; } = string.Empty;
    public string AdsManagerUrl { get; set; } = string.Empty;
}
