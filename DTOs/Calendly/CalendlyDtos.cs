using System.ComponentModel.DataAnnotations;

namespace nexthire_api.DTOs.Calendly;

public class CalendlyConnectionStatusDto
{
    public bool Connected { get; set; }
    public bool Active { get; set; }
    public bool Ready { get; set; }
    public string? Email { get; set; }
    public string? Name { get; set; }
    public string? SchedulingUrl { get; set; }
    public string? Timezone { get; set; }
    public string? UserUri { get; set; }
    public string? OrganizationUri { get; set; }
    public string? Error { get; set; }
}

public class CalendlyScheduledEventDto
{
    public string Uri { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset EndTime { get; set; }
    public string? Location { get; set; }
    public string? MeetingUrl { get; set; }
    public string? EventType { get; set; }
    public IReadOnlyList<string> InviteeEmails { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> InviteeNames { get; set; } = Array.Empty<string>();
}

public class CalendlyScheduledEventsResponse
{
    public string? SchedulingUrl { get; set; }
    public string? HostEmail { get; set; }
    public string? HostName { get; set; }
    public IReadOnlyList<CalendlyScheduledEventDto> Items { get; set; } = Array.Empty<CalendlyScheduledEventDto>();
}

public class CalendlyEventTypeDto
{
    public string Uri { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Active { get; set; }
    public int DurationMinutes { get; set; }
    public string? SchedulingUrl { get; set; }
    public string? Kind { get; set; }
    public string? Color { get; set; }
}

public class CalendlyAvailableTimeDto
{
    public string Status { get; set; } = "available";
    public DateTimeOffset StartTime { get; set; }
    public string? SchedulingUrl { get; set; }
    public int? InviteesRemaining { get; set; }
}

public class CalendlyBusyTimeDto
{
    public string Type { get; set; } = string.Empty;
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset EndTime { get; set; }
}

public class CalendlyCreateInviteeRequest
{
    [Required]
    [StringLength(500)]
    public string EventTypeUri { get; set; } = string.Empty;

    [Required]
    public DateTimeOffset StartTime { get; set; }

    [Required]
    [StringLength(200)]
    public string InviteeName { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [StringLength(320)]
    public string InviteeEmail { get; set; } = string.Empty;

    [StringLength(100)]
    public string? Timezone { get; set; }
}

public class CalendlyCreateInviteeResponse
{
    public string? InviteeUri { get; set; }
    public string? InviteeEmail { get; set; }
    public string? InviteeName { get; set; }
    public string? EventUri { get; set; }
    public string? CancelUrl { get; set; }
    public string? RescheduleUrl { get; set; }
    public string? Status { get; set; }
}

public class CalendlyContactDto
{
    public string Uri { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Company { get; set; }
    public string? JobTitle { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public class CalendlyContactsResponse
{
    public IReadOnlyList<CalendlyContactDto> Items { get; set; } = Array.Empty<CalendlyContactDto>();
    public string? NextPageToken { get; set; }
}

public class CalendlySyncContactsResponse
{
    public int Fetched { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public IReadOnlyList<Guid> ContactIds { get; set; } = Array.Empty<Guid>();
}
