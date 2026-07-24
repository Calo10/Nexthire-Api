using System.ComponentModel.DataAnnotations;

namespace nexthire_api.DTOs.Sourcing;

public class SourcingCampaignListItemDto
{
    public Guid Id { get; set; }
    public Guid? JobId { get; set; }
    public string? JobTitle { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal? DailyBudget { get; set; }
    public decimal? TotalBudget { get; set; }
    public string Currency { get; set; } = string.Empty;
    public DateTimeOffset? StartDate { get; set; }
    public DateTimeOffset? EndDate { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Leads in <c>sourcing_leads</c> for this campaign's job (and/or attributed to this campaign).
    /// </summary>
    public int LeadsCount { get; set; }
}

public class SourcingCampaignDetailDto : SourcingCampaignListItemDto
{
    public string? LandingPageUrl { get; set; }
    public string? TrackingCode { get; set; }
    public string? ExternalCampaignId { get; set; }
    public string? ExternalAdAccountId { get; set; }
}

public class CreateSourcingCampaignRequestDto
{
    public Guid? JobId { get; set; }

    [Required]
    [StringLength(240)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(80)]
    public string Platform { get; set; } = string.Empty;

    [StringLength(40)]
    public string? Status { get; set; }

    public decimal? DailyBudget { get; set; }
    public decimal? TotalBudget { get; set; }

    [StringLength(8)]
    public string? Currency { get; set; }

    public DateTimeOffset? StartDate { get; set; }
    public DateTimeOffset? EndDate { get; set; }

    [StringLength(2000)]
    public string? LandingPageUrl { get; set; }

    [StringLength(120)]
    public string? TrackingCode { get; set; }

    [StringLength(240)]
    public string? ExternalCampaignId { get; set; }

    [StringLength(240)]
    public string? ExternalAdAccountId { get; set; }
}

public class UpdateSourcingCampaignRequestDto
{
    public Guid? JobId { get; set; }

    [StringLength(240)]
    public string? Name { get; set; }

    [StringLength(80)]
    public string? Platform { get; set; }

    public decimal? DailyBudget { get; set; }
    public decimal? TotalBudget { get; set; }

    [StringLength(8)]
    public string? Currency { get; set; }

    public DateTimeOffset? StartDate { get; set; }
    public DateTimeOffset? EndDate { get; set; }

    [StringLength(2000)]
    public string? LandingPageUrl { get; set; }

    [StringLength(120)]
    public string? TrackingCode { get; set; }

    [StringLength(240)]
    public string? ExternalCampaignId { get; set; }

    [StringLength(240)]
    public string? ExternalAdAccountId { get; set; }
}

public class PatchSourcingCampaignStatusRequestDto
{
    [Required]
    [StringLength(40)]
    public string Status { get; set; } = string.Empty;
}
