namespace nexthire_api.DTOs.Sourcing;

public class SourcingDashboardDto
{
    public int CandidatesCapturedToday { get; set; }
    public int TotalLeads { get; set; }
    public int ActiveCampaigns { get; set; }
    public decimal? CostPerCandidate { get; set; }
    public decimal? ConversionRate { get; set; }
    public IReadOnlyList<LeadCountBySourceDto> LeadsBySource { get; set; } = Array.Empty<LeadCountBySourceDto>();
    public IReadOnlyList<SourcingLeadSummaryDto> RecentLeads { get; set; } = Array.Empty<SourcingLeadSummaryDto>();
}

public class LeadCountBySourceDto
{
    public string SourceTypeCode { get; set; } = string.Empty;
    public string? SourceTypeName { get; set; }
    public int Count { get; set; }
}

public class SourcingLeadSummaryDto
{
    public Guid Id { get; set; }
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string Status { get; set; } = string.Empty;
    public string SourceTypeCode { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
