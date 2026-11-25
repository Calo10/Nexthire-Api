using nexthire_api.Services;
using Nexa.WorkflowAutomation.Contracts.Providers;

namespace nexthire_api.WorkflowAutomation.Providers;

/// <summary>
/// Returns the org Calendly scheduling URL when connected; full invitee booking is V2.
/// </summary>
public sealed class WorkflowScheduleMeetingService : ICalendarService
{
    private readonly ICalendlyService _calendly;

    public WorkflowScheduleMeetingService(ICalendlyService calendly)
    {
        _calendly = calendly;
    }

    public async Task<MeetingScheduleResult> ScheduleAsync(
        MeetingScheduleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var status = await _calendly.GetConnectionStatusAsync(request.OrganizationId, cancellationToken);
            if (!status.Ready || string.IsNullOrWhiteSpace(status.SchedulingUrl))
            {
                return new MeetingScheduleResult
                {
                    Success = false,
                    ErrorCode = "calendly.not_ready",
                    ErrorMessage = status.Error ?? "Calendly is not connected for this organization.",
                };
            }

            return new MeetingScheduleResult
            {
                Success = true,
                SchedulingUrl = status.SchedulingUrl,
                ExternalReferenceId = status.SchedulingUrl,
            };
        }
        catch (Exception ex)
        {
            return new MeetingScheduleResult
            {
                Success = false,
                ErrorCode = "calendly.failed",
                ErrorMessage = ex.Message,
            };
        }
    }
}
