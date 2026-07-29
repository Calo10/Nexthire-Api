using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using nexthire_api.DTOs.Calendly;
using nexthire_api.Options;
using nexthire_api.Repositories.Sourcing;

namespace nexthire_api.Services;

public interface ICalendlyService
{
    Task<CalendlyConnectionStatusDto> GetConnectionStatusAsync(Guid orgId, CancellationToken cancellationToken = default);

    Task<CalendlyScheduledEventsResponse> ListScheduledEventsAsync(
        Guid orgId,
        DateTimeOffset? minStart = null,
        DateTimeOffset? maxStart = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalendlyEventTypeDto>> ListEventTypesAsync(
        Guid orgId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalendlyAvailableTimeDto>> ListAvailableTimesAsync(
        Guid orgId,
        string eventTypeUri,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalendlyBusyTimeDto>> ListBusyTimesAsync(
        Guid orgId,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        CancellationToken cancellationToken = default);

    Task<CalendlyCreateInviteeResponse> CreateInviteeAsync(
        Guid orgId,
        CalendlyCreateInviteeRequest request,
        CancellationToken cancellationToken = default);
}

public class CalendlyService : ICalendlyService
{
    public const string SourceTypeCode = "calendly";
    public const string HttpClientName = "Calendly";

    private readonly ISourcingRepository _sourcing;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CalendlyService> _logger;

    public CalendlyService(
        ISourcingRepository sourcing,
        IHttpClientFactory httpClientFactory,
        ILogger<CalendlyService> logger)
    {
        _sourcing = sourcing;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<CalendlyConnectionStatusDto> GetConnectionStatusAsync(
        Guid orgId,
        CancellationToken cancellationToken = default)
    {
        var (config, connected, active) = await TryLoadConfigAsync(orgId, cancellationToken);
        if (config == null || string.IsNullOrWhiteSpace(config.PersonalAccessToken))
        {
            return new CalendlyConnectionStatusDto
            {
                Connected = connected,
                Active = active,
                Ready = false,
                Error = connected
                    ? "Calendly personal access token is missing."
                    : "Calendly is not connected."
            };
        }

        try
        {
            var me = await GetCurrentUserAsync(config.PersonalAccessToken.Trim(), cancellationToken);
            var schedulingUrl = FirstNonEmpty(config.SchedulingUrl, me.SchedulingUrl);
            return new CalendlyConnectionStatusDto
            {
                Connected = connected,
                Active = active,
                Ready = connected && active,
                Email = me.Email,
                Name = me.Name,
                SchedulingUrl = schedulingUrl,
                Timezone = me.Timezone,
                UserUri = me.Uri,
                OrganizationUri = me.CurrentOrganization
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to validate Calendly token for org {OrgId}", orgId);
            return new CalendlyConnectionStatusDto
            {
                Connected = connected,
                Active = active,
                Ready = false,
                Error = ex.Message
            };
        }
    }

    public async Task<CalendlyScheduledEventsResponse> ListScheduledEventsAsync(
        Guid orgId,
        DateTimeOffset? minStart = null,
        DateTimeOffset? maxStart = null,
        CancellationToken cancellationToken = default)
    {
        var (token, config, me) = await RequireReadyAsync(orgId, cancellationToken);

        var min = (minStart ?? DateTimeOffset.UtcNow.AddDays(-7)).UtcDateTime;
        var max = (maxStart ?? DateTimeOffset.UtcNow.AddDays(21)).UtcDateTime;

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"scheduled_events?user={Uri.EscapeDataString(me.Uri!)}" +
            $"&min_start_time={Uri.EscapeDataString(FormatCalendlyUtc(min))}" +
            $"&max_start_time={Uri.EscapeDataString(FormatCalendlyUtc(max))}" +
            "&status=active&count=100");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ParseCalendlyError(payload, (int)response.StatusCode));

        using var doc = JsonDocument.Parse(payload);
        var items = new List<CalendlyScheduledEventDto>();
        if (doc.RootElement.TryGetProperty("collection", out var collection) &&
            collection.ValueKind == JsonValueKind.Array)
        {
            foreach (var ev in collection.EnumerateArray())
                items.Add(MapScheduledEvent(ev));
        }

        foreach (var item in items.Take(40))
        {
            try
            {
                await EnrichInviteesAsync(client, token, item, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not load invitees for Calendly event {Uri}", item.Uri);
            }
        }

        return new CalendlyScheduledEventsResponse
        {
            SchedulingUrl = FirstNonEmpty(config.SchedulingUrl, me.SchedulingUrl),
            HostEmail = me.Email,
            HostName = me.Name,
            Items = items.OrderBy(x => x.StartTime).ToList()
        };
    }

    public async Task<IReadOnlyList<CalendlyEventTypeDto>> ListEventTypesAsync(
        Guid orgId,
        CancellationToken cancellationToken = default)
    {
        var (token, _, me) = await RequireReadyAsync(orgId, cancellationToken);
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"event_types?user={Uri.EscapeDataString(me.Uri!)}&active=true&count=100");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ParseCalendlyError(payload, (int)response.StatusCode));

        using var doc = JsonDocument.Parse(payload);
        var items = new List<CalendlyEventTypeDto>();
        if (doc.RootElement.TryGetProperty("collection", out var collection) &&
            collection.ValueKind == JsonValueKind.Array)
        {
            foreach (var et in collection.EnumerateArray())
            {
                var uri = et.TryGetProperty("uri", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(uri)) continue;
                items.Add(new CalendlyEventTypeDto
                {
                    Uri = uri,
                    Name = et.TryGetProperty("name", out var n) ? n.GetString() ?? "Event" : "Event",
                    Active = !et.TryGetProperty("active", out var a) || a.ValueKind != JsonValueKind.False,
                    DurationMinutes = et.TryGetProperty("duration", out var d) && d.TryGetInt32(out var mins) ? mins : 30,
                    SchedulingUrl = et.TryGetProperty("scheduling_url", out var su) ? su.GetString() : null,
                    Kind = et.TryGetProperty("kind", out var k) ? k.GetString() : null,
                    Color = et.TryGetProperty("color", out var c) ? c.GetString() : null
                });
            }
        }

        return items.OrderBy(x => x.Name).ToList();
    }

    public async Task<IReadOnlyList<CalendlyAvailableTimeDto>> ListAvailableTimesAsync(
        Guid orgId,
        string eventTypeUri,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(eventTypeUri))
            throw new InvalidOperationException("eventType is required.");

        // Calendly rejects start_time in the past with opaque "supplied parameters are invalid".
        // Also requires end > start and a window strictly within 7 days.
        var (startUtc, endUtc) = NormalizeAvailabilityWindow(startTime, endTime);

        var (token, _, _) = await RequireReadyAsync(orgId, cancellationToken);
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var url =
            $"event_type_available_times?event_type={Uri.EscapeDataString(eventTypeUri.Trim())}" +
            $"&start_time={Uri.EscapeDataString(FormatCalendlyUtc(startUtc))}" +
            $"&end_time={Uri.EscapeDataString(FormatCalendlyUtc(endUtc))}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ParseCalendlyError(payload, (int)response.StatusCode));

        using var doc = JsonDocument.Parse(payload);
        var items = new List<CalendlyAvailableTimeDto>();
        if (doc.RootElement.TryGetProperty("collection", out var collection) &&
            collection.ValueKind == JsonValueKind.Array)
        {
            foreach (var slot in collection.EnumerateArray())
            {
                if (!slot.TryGetProperty("start_time", out var st) || !st.TryGetDateTimeOffset(out var start))
                    continue;
                items.Add(new CalendlyAvailableTimeDto
                {
                    Status = slot.TryGetProperty("status", out var status) ? status.GetString() ?? "available" : "available",
                    StartTime = start,
                    SchedulingUrl = slot.TryGetProperty("scheduling_url", out var su) ? su.GetString() : null,
                    InviteesRemaining = slot.TryGetProperty("invitees_remaining", out var ir) && ir.TryGetInt32(out var rem)
                        ? rem
                        : null
                });
            }
        }

        return items.OrderBy(x => x.StartTime).ToList();
    }

    public async Task<IReadOnlyList<CalendlyBusyTimeDto>> ListBusyTimesAsync(
        Guid orgId,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        CancellationToken cancellationToken = default)
    {
        if (endTime <= startTime)
            throw new InvalidOperationException("endTime must be after startTime.");

        var startUtc = startTime.UtcDateTime;
        var endUtc = endTime.UtcDateTime;
        // Busy-times window is also capped near 7 days; keep under that limit.
        if (endUtc - startUtc > TimeSpan.FromDays(6.9))
            endUtc = startUtc.AddDays(6).AddHours(23);

        var (token, _, me) = await RequireReadyAsync(orgId, cancellationToken);
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var url =
            $"user_busy_times?user={Uri.EscapeDataString(me.Uri!)}" +
            $"&start_time={Uri.EscapeDataString(FormatCalendlyUtc(startUtc))}" +
            $"&end_time={Uri.EscapeDataString(FormatCalendlyUtc(endUtc))}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ParseCalendlyError(payload, (int)response.StatusCode));

        using var doc = JsonDocument.Parse(payload);
        var items = new List<CalendlyBusyTimeDto>();
        if (doc.RootElement.TryGetProperty("collection", out var collection) &&
            collection.ValueKind == JsonValueKind.Array)
        {
            foreach (var busy in collection.EnumerateArray())
            {
                if (!busy.TryGetProperty("start_time", out var st) || !st.TryGetDateTimeOffset(out var start))
                    continue;
                if (!busy.TryGetProperty("end_time", out var et) || !et.TryGetDateTimeOffset(out var end))
                    continue;
                items.Add(new CalendlyBusyTimeDto
                {
                    Type = busy.TryGetProperty("type", out var t) ? t.GetString() ?? "busy" : "busy",
                    StartTime = start,
                    EndTime = end
                });
            }
        }

        return items.OrderBy(x => x.StartTime).ToList();
    }

    public async Task<CalendlyCreateInviteeResponse> CreateInviteeAsync(
        Guid orgId,
        CalendlyCreateInviteeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new InvalidOperationException("Request body is required.");
        if (string.IsNullOrWhiteSpace(request.EventTypeUri))
            throw new InvalidOperationException("eventTypeUri is required.");
        if (string.IsNullOrWhiteSpace(request.InviteeName))
            throw new InvalidOperationException("inviteeName is required.");
        if (string.IsNullOrWhiteSpace(request.InviteeEmail))
            throw new InvalidOperationException("inviteeEmail is required.");

        var (token, _, me) = await RequireReadyAsync(orgId, cancellationToken);
        var timezone = FirstNonEmpty(request.Timezone, me.Timezone, "UTC")!;

        var body = new Dictionary<string, object?>
        {
            ["event_type"] = request.EventTypeUri.Trim(),
            ["start_time"] = FormatCalendlyUtc(request.StartTime.UtcDateTime),
            ["invitee"] = new Dictionary<string, object?>
            {
                ["name"] = request.InviteeName.Trim(),
                ["email"] = request.InviteeEmail.Trim(),
                ["timezone"] = timezone
            }
        };

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "invitees")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(httpRequest, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ParseCalendlyError(payload, (int)response.StatusCode));

        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("resource", out var resource))
            throw new InvalidOperationException("Unexpected Calendly create invitee response.");

        return new CalendlyCreateInviteeResponse
        {
            InviteeUri = resource.TryGetProperty("uri", out var uri) ? uri.GetString() : null,
            InviteeEmail = resource.TryGetProperty("email", out var email) ? email.GetString() : request.InviteeEmail,
            InviteeName = resource.TryGetProperty("name", out var name) ? name.GetString() : request.InviteeName,
            EventUri = resource.TryGetProperty("event", out var ev) ? ev.GetString() : null,
            CancelUrl = resource.TryGetProperty("cancel_url", out var cu) ? cu.GetString() : null,
            RescheduleUrl = resource.TryGetProperty("reschedule_url", out var ru) ? ru.GetString() : null,
            Status = resource.TryGetProperty("status", out var st) ? st.GetString() : "active"
        };
    }

    private async Task<(string Token, CalendlySourceConnectionConfig Config, CalendlyMeResource Me)> RequireReadyAsync(
        Guid orgId,
        CancellationToken cancellationToken)
    {
        var (config, connected, active) = await TryLoadConfigAsync(orgId, cancellationToken);
        if (!connected || !active || config == null || string.IsNullOrWhiteSpace(config.PersonalAccessToken))
            throw new InvalidOperationException("Calendly is not connected and active for this organization.");

        var token = config.PersonalAccessToken.Trim();
        var me = await GetCurrentUserAsync(token, cancellationToken);
        if (string.IsNullOrWhiteSpace(me.Uri))
            throw new InvalidOperationException("Calendly did not return a user URI.");
        return (token, config, me);
    }

    private async Task<(CalendlySourceConnectionConfig? Config, bool Connected, bool Active)> TryLoadConfigAsync(
        Guid orgId,
        CancellationToken cancellationToken)
    {
        var rows = await _sourcing.GetSourceConnectionsAsync(orgId);
        var row = rows.FirstOrDefault(r =>
            string.Equals(r.SourceTypeCode, SourceTypeCode, StringComparison.OrdinalIgnoreCase));
        if (row == null)
            return (null, false, false);

        var config = string.IsNullOrWhiteSpace(row.ConfigJson) ? null : ParseConfig(row.ConfigJson);
        return (config, row.IsConnected, row.IsActive);
    }

    private async Task<CalendlyMeResource> GetCurrentUserAsync(string token, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, "users/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ParseCalendlyError(payload, (int)response.StatusCode));

        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("resource", out var resource))
            throw new InvalidOperationException("Unexpected Calendly /users/me response.");

        return new CalendlyMeResource
        {
            Uri = resource.TryGetProperty("uri", out var uri) ? uri.GetString() : null,
            Name = resource.TryGetProperty("name", out var name) ? name.GetString() : null,
            Email = resource.TryGetProperty("email", out var email) ? email.GetString() : null,
            SchedulingUrl = resource.TryGetProperty("scheduling_url", out var su) ? su.GetString() : null,
            Timezone = resource.TryGetProperty("timezone", out var tz) ? tz.GetString() : null,
            CurrentOrganization = resource.TryGetProperty("current_organization", out var org)
                ? org.GetString()
                : null
        };
    }

    private static async Task EnrichInviteesAsync(
        HttpClient client,
        string token,
        CalendlyScheduledEventDto item,
        CancellationToken cancellationToken)
    {
        var uuid = ExtractUuidFromUri(item.Uri);
        if (string.IsNullOrWhiteSpace(uuid)) return;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"scheduled_events/{uuid}/invitees?count=20");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return;

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("collection", out var collection) ||
            collection.ValueKind != JsonValueKind.Array)
            return;

        var emails = new List<string>();
        var names = new List<string>();
        foreach (var inv in collection.EnumerateArray())
        {
            if (inv.TryGetProperty("email", out var e) && !string.IsNullOrWhiteSpace(e.GetString()))
                emails.Add(e.GetString()!);
            if (inv.TryGetProperty("name", out var n) && !string.IsNullOrWhiteSpace(n.GetString()))
                names.Add(n.GetString()!);
        }

        item.InviteeEmails = emails;
        item.InviteeNames = names;
    }

    private static CalendlyScheduledEventDto MapScheduledEvent(JsonElement ev)
    {
        var location = TryGetLocation(ev);
        return new CalendlyScheduledEventDto
        {
            Uri = ev.TryGetProperty("uri", out var uri) ? uri.GetString() ?? string.Empty : string.Empty,
            Name = ev.TryGetProperty("name", out var name) ? name.GetString() ?? "Meeting" : "Meeting",
            Status = ev.TryGetProperty("status", out var status) ? status.GetString() ?? "active" : "active",
            StartTime = ev.TryGetProperty("start_time", out var start) && start.TryGetDateTimeOffset(out var st)
                ? st
                : DateTimeOffset.MinValue,
            EndTime = ev.TryGetProperty("end_time", out var end) && end.TryGetDateTimeOffset(out var et)
                ? et
                : DateTimeOffset.MinValue,
            Location = location.JoinUrl ?? location.Location,
            MeetingUrl = location.JoinUrl,
            EventType = ev.TryGetProperty("event_type", out var etype) ? etype.GetString() : null
        };
    }

    private static (string? Location, string? JoinUrl) TryGetLocation(JsonElement ev)
    {
        if (!ev.TryGetProperty("location", out var loc) || loc.ValueKind != JsonValueKind.Object)
            return (null, null);

        string? location = null;
        string? joinUrl = null;
        if (loc.TryGetProperty("location", out var l) && l.ValueKind == JsonValueKind.String)
            location = l.GetString();
        if (loc.TryGetProperty("join_url", out var j) && j.ValueKind == JsonValueKind.String)
            joinUrl = j.GetString();
        if (string.IsNullOrWhiteSpace(location) && loc.TryGetProperty("type", out var t))
            location = t.GetString();
        return (location, joinUrl);
    }

    private static string? ExtractUuidFromUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        var parts = uri.TrimEnd('/').Split('/');
        return parts.Length == 0 ? null : parts[^1];
    }

    /// <summary>
    /// Calendly docs use 6 fractional digits (e.g. 2020-01-02T03:04:05.678123Z).
    /// start_time must be in the future; window must be under 7 days.
    /// </summary>
    private static (DateTime StartUtc, DateTime EndUtc) NormalizeAvailabilityWindow(
        DateTimeOffset startTime,
        DateTimeOffset endTime)
    {
        // At least ~2 minutes ahead so Calendly never sees a past start_time.
        var minStart = DateTime.UtcNow.AddMinutes(2);
        minStart = new DateTime(
            minStart.Year, minStart.Month, minStart.Day,
            minStart.Hour, minStart.Minute, 0, DateTimeKind.Utc);

        var startUtc = startTime.UtcDateTime;
        startUtc = new DateTime(
            startUtc.Year, startUtc.Month, startUtc.Day,
            startUtc.Hour, startUtc.Minute, 0, DateTimeKind.Utc);
        if (startUtc < minStart) startUtc = minStart;

        var endUtc = endTime.UtcDateTime;
        if (endUtc <= startUtc)
            endUtc = startUtc.AddDays(6).AddHours(23);

        // Keep strictly under 7 days (Calendly rejects some exact-7-day windows).
        var maxEnd = startUtc.AddDays(6).AddHours(23);
        if (endUtc > maxEnd) endUtc = maxEnd;

        return (startUtc, endUtc);
    }

    private static string FormatCalendlyUtc(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'");

    private static CalendlySourceConnectionConfig ParseConfig(string json)
    {
        try
        {
            var cfg = JsonSerializer.Deserialize<CalendlySourceConnectionConfig>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new CalendlySourceConnectionConfig();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (string.IsNullOrWhiteSpace(cfg.PersonalAccessToken))
            {
                cfg.PersonalAccessToken =
                    TryGetString(root, "PersonalAccessToken")
                    ?? TryGetString(root, "personalAccessToken")
                    ?? TryGetString(root, "AccessToken")
                    ?? TryGetString(root, "api_key")
                    ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(cfg.SchedulingUrl))
            {
                cfg.SchedulingUrl =
                    TryGetString(root, "SchedulingUrl")
                    ?? TryGetString(root, "schedulingUrl")
                    ?? TryGetString(root, "scheduling_url");
            }
            if (string.IsNullOrWhiteSpace(cfg.WebhookSigningKey))
            {
                cfg.WebhookSigningKey =
                    TryGetString(root, "WebhookSigningKey")
                    ?? TryGetString(root, "webhookSigningKey")
                    ?? TryGetString(root, "webhook_signing_key");
            }
            return cfg;
        }
        catch
        {
            return new CalendlySourceConnectionConfig();
        }
    }

    private static string? TryGetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string ParseCalendlyError(string payload, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                return msg.GetString() ?? $"Calendly error ({status}).";
            if (doc.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                return title.GetString() ?? $"Calendly error ({status}).";
            if (doc.RootElement.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in details.EnumerateArray())
                {
                    if (d.TryGetProperty("message", out var dm) && dm.ValueKind == JsonValueKind.String)
                        return dm.GetString() ?? $"Calendly error ({status}).";
                }
            }
        }
        catch
        {
            // ignore
        }

        return string.IsNullOrWhiteSpace(payload)
            ? $"Calendly error ({status})."
            : $"Calendly error ({status}): {payload.Trim()[..Math.Min(180, payload.Trim().Length)]}";
    }

    private sealed class CalendlyMeResource
    {
        public string? Uri { get; init; }
        public string? Name { get; init; }
        public string? Email { get; init; }
        public string? SchedulingUrl { get; init; }
        public string? Timezone { get; init; }
        public string? CurrentOrganization { get; init; }
    }
}
