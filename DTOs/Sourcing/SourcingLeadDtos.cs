using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace nexthire_api.DTOs.Sourcing;

public class SourcingLeadListItemDto
{
    public Guid Id { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? JobId { get; set; }
    public string SourceTypeCode { get; set; } = string.Empty;
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? ResumeUrl { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Availability { get; set; }
    public decimal? FitScore { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class SourcingLeadDetailDto : SourcingLeadListItemDto
{
    public string? DesiredRole { get; set; }
    public string? CurrentRole { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ZipCode { get; set; }
    public string? Country { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public int? ExperienceYears { get; set; }
    public string? EnglishLevel { get; set; }
    public string? SpanishLevel { get; set; }
    public bool? HasTransportation { get; set; }
    public bool? WillingToRelocate { get; set; }
    public string? QualificationNotes { get; set; }
    public string? DynamicAnswersJson { get; set; }
    public string? RawPayloadJson { get; set; }
    public DateTimeOffset? ContactedAt { get; set; }
    public Guid? ConvertedCandidateId { get; set; }
    public Guid? ConvertedApplicationId { get; set; }
    public DateTimeOffset? ConvertedAt { get; set; }

    public SourcingContextDto? Campaign { get; set; }
    public SourcingContextDto? Job { get; set; }
    public SourcingSourceTypeSummaryDto? SourceType { get; set; }
}

public class SourcingContextDto
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public string? Title { get; set; }
}

public class SourcingSourceTypeSummaryDto
{
    public string Code { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? DisplayName { get; set; }
}

public class CreateSourcingLeadRequestDto
{
    public Guid? CampaignId { get; set; }
    public Guid? JobId { get; set; }

    [Required]
    [StringLength(64)]
    public string SourceTypeCode { get; set; } = string.Empty;

    [StringLength(160)]
    public string? FirstName { get; set; }

    [StringLength(240)]
    public string? LastName { get; set; }

    [StringLength(320)]
    public string? FullName { get; set; }

    [StringLength(640)]
    public string? Email { get; set; }

    [StringLength(80)]
    public string? Phone { get; set; }

    [StringLength(240)]
    public string? DesiredRole { get; set; }

    [StringLength(240)]
    public string? CurrentRole { get; set; }

    [StringLength(160)]
    public string? City { get; set; }

    [StringLength(80)]
    public string? State { get; set; }

    [StringLength(32)]
    public string? ZipCode { get; set; }

    [StringLength(120)]
    public string? Country { get; set; }

    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }

    [StringLength(80)]
    public string? Availability { get; set; }

    public int? ExperienceYears { get; set; }

    [StringLength(40)]
    public string? EnglishLevel { get; set; }

    [StringLength(40)]
    public string? SpanishLevel { get; set; }

    public bool? HasTransportation { get; set; }
    public bool? WillingToRelocate { get; set; }

    public decimal? FitScore { get; set; }

    public string? QualificationNotes { get; set; }
    public string? ResumeUrl { get; set; }
    public string? DynamicAnswersJson { get; set; }
    public string? RawPayloadJson { get; set; }
}

public class CreateSourcingLeadFormRequestDto
{
    public Guid? OrgId { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? JobId { get; set; }

    [Required]
    [StringLength(64)]
    public string SourceTypeCode { get; set; } = string.Empty;

    [StringLength(160)]
    public string? FirstName { get; set; }

    [StringLength(240)]
    public string? LastName { get; set; }

    [StringLength(320)]
    public string? FullName { get; set; }

    [StringLength(640)]
    public string? Email { get; set; }

    [StringLength(80)]
    public string? Phone { get; set; }

    [StringLength(240)]
    public string? DesiredRole { get; set; }

    [StringLength(240)]
    public string? CurrentRole { get; set; }

    [StringLength(160)]
    public string? City { get; set; }

    [StringLength(80)]
    public string? State { get; set; }

    [StringLength(32)]
    public string? ZipCode { get; set; }

    [StringLength(120)]
    public string? Country { get; set; }

    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }

    [StringLength(80)]
    public string? Availability { get; set; }

    public int? ExperienceYears { get; set; }

    [StringLength(40)]
    public string? EnglishLevel { get; set; }

    [StringLength(40)]
    public string? SpanishLevel { get; set; }

    public bool? HasTransportation { get; set; }
    public bool? WillingToRelocate { get; set; }

    public decimal? FitScore { get; set; }

    public string? QualificationNotes { get; set; }
    public string? ResumeUrl { get; set; }
    public string? RawPayloadJson { get; set; }

    [Required]
    public IFormFile Resume { get; set; } = default!;
}

public class UpdateSourcingLeadRequestDto
{
    public Guid? CampaignId { get; set; }
    public Guid? JobId { get; set; }

    [StringLength(64)]
    public string? SourceTypeCode { get; set; }

    [StringLength(160)]
    public string? FirstName { get; set; }

    [StringLength(240)]
    public string? LastName { get; set; }

    [StringLength(320)]
    public string? FullName { get; set; }

    [StringLength(640)]
    public string? Email { get; set; }

    [StringLength(80)]
    public string? Phone { get; set; }

    [StringLength(240)]
    public string? DesiredRole { get; set; }

    [StringLength(240)]
    public string? CurrentRole { get; set; }

    [StringLength(160)]
    public string? City { get; set; }

    [StringLength(80)]
    public string? State { get; set; }

    [StringLength(32)]
    public string? ZipCode { get; set; }

    [StringLength(120)]
    public string? Country { get; set; }

    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }

    [StringLength(80)]
    public string? Availability { get; set; }

    public int? ExperienceYears { get; set; }

    [StringLength(40)]
    public string? EnglishLevel { get; set; }

    [StringLength(40)]
    public string? SpanishLevel { get; set; }

    public bool? HasTransportation { get; set; }
    public bool? WillingToRelocate { get; set; }

    public decimal? FitScore { get; set; }

    public string? QualificationNotes { get; set; }
    public string? ResumeUrl { get; set; }
    public string? DynamicAnswersJson { get; set; }
    public string? RawPayloadJson { get; set; }
}

public class PatchSourcingLeadStatusRequestDto
{
    [Required]
    [StringLength(40)]
    public string Status { get; set; } = string.Empty;

    public string? Notes { get; set; }
}

public class ConvertLeadToCandidateResponseDto
{
    public Guid LeadId { get; set; }
    public Guid CandidateId { get; set; }
    public Guid? ApplicationId { get; set; }
    public bool CandidateCreated { get; set; }
    public bool ApplicationCreated { get; set; }
}
