using System.ComponentModel.DataAnnotations;

namespace nexthire_api.DTOs.Sourcing;

public class CreateSourcingTrackingEventRequestDto
{
    public Guid? CampaignId { get; set; }

    [StringLength(64)]
    public string? SourceTypeCode { get; set; }

    public Guid? LeadId { get; set; }
    public Guid? JobId { get; set; }

    [Required]
    [StringLength(64)]
    public string EventType { get; set; } = string.Empty;

    [StringLength(128)]
    public string? SessionId { get; set; }

    [StringLength(128)]
    public string? VisitorId { get; set; }

    [StringLength(64)]
    public string? IpAddress { get; set; }

    [StringLength(2000)]
    public string? UserAgent { get; set; }

    [StringLength(2000)]
    public string? Referrer { get; set; }

    public string? MetadataJson { get; set; }
}

public class SourcingTrackingEventCreatedDto
{
    public Guid Id { get; set; }
}
