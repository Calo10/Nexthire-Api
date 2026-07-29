using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs.Calendly;
using nexthire_api.Security;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api/calendly")]
[Authorize]
public class CalendlyController : ControllerBase
{
    private readonly ICalendlyService _calendly;

    public CalendlyController(ICalendlyService calendly)
    {
        _calendly = calendly;
    }

    /// <summary>Validate stored Calendly credentials and return the connected user profile.</summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(CalendlyConnectionStatusDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<CalendlyConnectionStatusDto>> GetMe(CancellationToken cancellationToken)
    {
        var orgId = ClaimUtils.RequireOrgId(User);
        var status = await _calendly.GetConnectionStatusAsync(orgId, cancellationToken);
        return Ok(status);
    }

    /// <summary>List active Calendly scheduled events for the connected user (week/month range).</summary>
    [HttpGet("scheduled-events")]
    [ProducesResponseType(typeof(CalendlyScheduledEventsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CalendlyScheduledEventsResponse>> ListScheduledEvents(
        [FromQuery] DateTimeOffset? minStart,
        [FromQuery] DateTimeOffset? maxStart,
        CancellationToken cancellationToken)
    {
        var orgId = ClaimUtils.RequireOrgId(User);
        try
        {
            var result = await _calendly.ListScheduledEventsAsync(orgId, minStart, maxStart, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>List active event types for the connected Calendly user.</summary>
    [HttpGet("event-types")]
    [ProducesResponseType(typeof(IReadOnlyList<CalendlyEventTypeDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<CalendlyEventTypeDto>>> ListEventTypes(
        CancellationToken cancellationToken)
    {
        var orgId = ClaimUtils.RequireOrgId(User);
        try
        {
            return Ok(await _calendly.ListEventTypesAsync(orgId, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>List available booking slots for an event type (max ~7 day window).</summary>
    [HttpGet("available-times")]
    [ProducesResponseType(typeof(IReadOnlyList<CalendlyAvailableTimeDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<CalendlyAvailableTimeDto>>> ListAvailableTimes(
        [FromQuery] string eventType,
        [FromQuery] DateTimeOffset startTime,
        [FromQuery] DateTimeOffset endTime,
        CancellationToken cancellationToken)
    {
        var orgId = ClaimUtils.RequireOrgId(User);
        try
        {
            var result = await _calendly.ListAvailableTimesAsync(orgId, eventType, startTime, endTime, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>List busy times from Calendly-connected calendars (max ~7 day window).</summary>
    [HttpGet("busy-times")]
    [ProducesResponseType(typeof(IReadOnlyList<CalendlyBusyTimeDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<CalendlyBusyTimeDto>>> ListBusyTimes(
        [FromQuery] DateTimeOffset startTime,
        [FromQuery] DateTimeOffset endTime,
        CancellationToken cancellationToken)
    {
        var orgId = ClaimUtils.RequireOrgId(User);
        try
        {
            var result = await _calendly.ListBusyTimesAsync(orgId, startTime, endTime, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Book a meeting via Calendly Scheduling API (create invitee). Requires paid Calendly plan.</summary>
    [HttpPost("invitees")]
    [ProducesResponseType(typeof(CalendlyCreateInviteeResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CalendlyCreateInviteeResponse>> CreateInvitee(
        [FromBody] CalendlyCreateInviteeRequest request,
        CancellationToken cancellationToken)
    {
        var orgId = ClaimUtils.RequireOrgId(User);
        try
        {
            var result = await _calendly.CreateInviteeAsync(orgId, request, cancellationToken);
            return StatusCode(StatusCodes.Status201Created, result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

}
