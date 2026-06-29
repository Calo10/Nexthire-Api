using nexthire_api.DTOs.Sourcing;

namespace nexthire_api.Services.Sourcing;

public interface ISourcingLeadFitScoringAgent
{
    /// <summary>
    /// When org fit scoring is enabled, evaluates the lead against its job post and
    /// persists <c>fit_score</c> and <c>qualification_notes</c> on the sourcing lead.
    /// </summary>
    Task ScoreLeadIfEnabledAsync(
        Guid orgId,
        SourcingLeadDetailDto lead,
        string? resumeSummary = null,
        CancellationToken cancellationToken = default);

    /// <summary>Starts resume analysis for fit scoring; may run in parallel with other apply work.</summary>
    Task<string?> FetchResumeSummaryAsync(
        Guid orgId,
        string? resumeDocumentId,
        CancellationToken cancellationToken = default);
}
