using nexthire_api.DTOs.CoreHr;
using nexthire_api.Repositories;

namespace nexthire_api.Services;

// A domain event that may produce onboarding notifications. DedupeKey is a stable key for the event;
// the pipeline appends channel and recipient role to it.
public sealed record OnboardingNotificationEvent(
    Guid OrganizationId,
    Guid OnboardingId,
    string TriggerCode,
    string DedupeKey,
    Guid? TaskId = null);

/// <summary>
/// Records onboarding notification attempts (nh_hr_onboarding_notifications) according to the org's
/// notification settings. Attempts are always recorded as Queued; real delivery only happens when
/// <c>Onboarding:SendNotifications</c> is explicitly <c>true</c> in configuration, so development
/// environments and seed data never email anyone by accident.
/// </summary>
public interface IOnboardingNotificationService
{
    /// <summary>True only when <c>Onboarding:SendNotifications=true</c>.</summary>
    bool SendingEnabled { get; }

    /// <summary>Fire-and-forget: never throws and never blocks the caller.</summary>
    void Enqueue(OnboardingNotificationEvent notificationEvent);

    /// <summary>Processes one event synchronously. Returns the number of new (non-duplicate) attempts recorded.</summary>
    Task<int> ProcessAsync(OnboardingNotificationEvent notificationEvent, CancellationToken cancellationToken = default);
}

public static class OnboardingNotificationStatuses
{
    public const string Queued = "Queued";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
}

public static class OnboardingNotificationKeys
{
    /// <summary>
    /// Key for events that can legitimately repeat (status changes): identical events inside the same UTC minute
    /// (double clicks, retries) collapse, later ones do not.
    /// </summary>
    public static string ForEvent(string triggerCode, Guid entityId, DateTime? utcNow = null) =>
        $"{triggerCode}:{entityId:N}:{(utcNow ?? DateTime.UtcNow):yyyyMMddHHmm}";

    /// <summary>Key for one-time facts (e.g. a task being assigned to a specific user).</summary>
    public static string ForTask(string triggerCode, Guid taskId, Guid? userId = null) =>
        userId.HasValue ? $"{triggerCode}:{taskId:N}:{userId.Value:N}" : $"{triggerCode}:{taskId:N}";
}

public class OnboardingNotificationService : IOnboardingNotificationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OnboardingNotificationService> _logger;

    public OnboardingNotificationService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<OnboardingNotificationService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public bool SendingEnabled => _configuration.GetValue<bool>("Onboarding:SendNotifications");

    public void Enqueue(OnboardingNotificationEvent notificationEvent)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ProcessAsync(notificationEvent, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Onboarding notification {Trigger} for {OnboardingId} failed.",
                    notificationEvent.TriggerCode, notificationEvent.OnboardingId);
            }
        });
    }

    public async Task<int> ProcessAsync(OnboardingNotificationEvent evt, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IOnboardingRepository>();

        var setting = await repo.GetNotificationSettingAsync(evt.OrganizationId, evt.TriggerCode, cancellationToken);
        if (setting is not { IsEnabled: true }) return 0;

        var roles = setting.Recipients.Where(r => OnboardingRecipientRoles.All.Contains(r)).ToList();
        var channels = new List<string>();
        if (setting.ChannelEmail) channels.Add("Email");
        if (setting.ChannelWhatsapp) channels.Add("WhatsApp");
        if (roles.Count == 0 || channels.Count == 0) return 0;

        var recipients = await repo.ResolveNotificationRecipientsAsync(
            evt.OrganizationId, evt.OnboardingId, evt.TaskId, roles, cancellationToken);
        if (recipients.Count == 0) return 0;

        var sendEnabled = SendingEnabled;
        string? employeeName = null;
        var recorded = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var recipient in recipients)
        {
            foreach (var channel in channels)
            {
                // The same person can hold several roles (e.g. owner and assignee): notify once per channel.
                var identity = (recipient.Email ?? recipient.Role) + "|" + channel;
                if (!seen.Add(identity)) continue;

                var key = $"{evt.DedupeKey}:{channel}:{recipient.Role}";
                var isEmail = channel == "Email";
                var skipReason = isEmail && string.IsNullOrWhiteSpace(recipient.Email) ? "No email address on file." : null;

                var inserted = await repo.RecordNotificationAttemptAsync(
                    evt.OrganizationId, evt.OnboardingId, evt.TriggerCode, key, channel,
                    isEmail ? recipient.Email : recipient.Name,
                    skipReason != null ? OnboardingNotificationStatuses.Skipped : OnboardingNotificationStatuses.Queued,
                    skipReason, cancellationToken);
                if (!inserted) continue; // duplicate: already handled
                recorded++;

                if (skipReason != null || !sendEnabled) continue;

                if (!isEmail)
                {
                    await repo.UpdateNotificationAttemptAsync(evt.OrganizationId, key, OnboardingNotificationStatuses.Skipped,
                        "WhatsApp delivery is not available for onboarding notifications yet.", cancellationToken);
                    continue;
                }

                try
                {
                    employeeName ??= (await repo.GetHeaderAsync(evt.OrganizationId, evt.OnboardingId, cancellationToken))?.EmployeeName ?? "an employee";
                    var (subject, text) = BuildMessage(evt.TriggerCode, employeeName);
                    var email = scope.ServiceProvider.GetRequiredService<IEmailService>();
                    await email.SendEmailAsync(recipient.Email!, subject, text, $"<p>{System.Net.WebUtility.HtmlEncode(text)}</p>", cancellationToken);
                    await repo.UpdateNotificationAttemptAsync(evt.OrganizationId, key, OnboardingNotificationStatuses.Sent, null, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Sending onboarding email {Trigger} failed.", evt.TriggerCode);
                    await repo.UpdateNotificationAttemptAsync(evt.OrganizationId, key, OnboardingNotificationStatuses.Failed, ex.Message, cancellationToken);
                }
            }
        }

        return recorded;
    }

    /// <summary>Plain-text subject/body for a trigger.</summary>
    public static (string Subject, string Text) BuildMessage(string triggerCode, string employeeName)
    {
        return triggerCode switch
        {
            OnboardingTriggers.OnboardingCreated => ($"Onboarding created: {employeeName}", $"An onboarding was created for {employeeName}."),
            OnboardingTriggers.OnboardingStarted => ($"Onboarding started: {employeeName}", $"The onboarding for {employeeName} has started."),
            OnboardingTriggers.OnboardingInReview => ($"Onboarding ready for review: {employeeName}", $"All required tasks for {employeeName} are complete and ready for review."),
            OnboardingTriggers.OnboardingCompleted => ($"Onboarding completed: {employeeName}", $"The onboarding for {employeeName} is complete."),
            OnboardingTriggers.OnboardingCancelled => ($"Onboarding cancelled: {employeeName}", $"The onboarding for {employeeName} was cancelled."),
            OnboardingTriggers.TaskAssigned => ($"New onboarding task: {employeeName}", $"You have a new onboarding task for {employeeName}."),
            OnboardingTriggers.TaskDueSoon => ($"Onboarding task due soon: {employeeName}", $"An onboarding task for {employeeName} is due soon."),
            OnboardingTriggers.TaskOverdue => ($"Onboarding task overdue: {employeeName}", $"An onboarding task for {employeeName} is overdue."),
            OnboardingTriggers.TaskApprovalRequested => ($"Approval needed: {employeeName}", $"An onboarding task for {employeeName} needs your approval."),
            OnboardingTriggers.TaskApproved => ($"Task approved: {employeeName}", $"Your onboarding task for {employeeName} was approved."),
            OnboardingTriggers.TaskRejected => ($"Task needs changes: {employeeName}", $"Your onboarding task for {employeeName} was rejected and needs changes."),
            _ => ($"Onboarding update: {employeeName}", $"There is an update on the onboarding for {employeeName}.")
        };
    }
}
