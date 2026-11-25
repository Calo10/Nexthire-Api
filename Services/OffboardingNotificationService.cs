using nexthire_api.DTOs.CoreHr;
using nexthire_api.Repositories;

namespace nexthire_api.Services;

// A domain event that may produce offboarding notifications. DedupeKey is a stable key for the event;
// the pipeline appends channel and recipient role to it.
public sealed record OffboardingNotificationEvent(
    Guid OrganizationId,
    Guid OffboardingId,
    string TriggerCode,
    string DedupeKey,
    Guid? TaskId = null);

/// <summary>
/// Records offboarding notification attempts (nh_hr_offboarding_notifications) according to the org's
/// notification settings. Attempts are always recorded as Queued; real delivery only happens when
/// <c>Offboarding:SendNotifications</c> is explicitly <c>true</c> in configuration, so development
/// environments and seed data never email anyone by accident.
/// The employee being offboarded is only ever a recipient when the offboarding was explicitly flagged
/// <c>notify_employee</c> AND the separation is not involuntary.
/// </summary>
public interface IOffboardingNotificationService
{
    /// <summary>True only when <c>Offboarding:SendNotifications=true</c>.</summary>
    bool SendingEnabled { get; }

    /// <summary>Fire-and-forget: never throws and never blocks the caller.</summary>
    void Enqueue(OffboardingNotificationEvent notificationEvent);

    /// <summary>Processes one event synchronously. Returns the number of new (non-duplicate) attempts recorded.</summary>
    Task<int> ProcessAsync(OffboardingNotificationEvent notificationEvent, CancellationToken cancellationToken = default);
}

public static class OffboardingNotificationStatuses
{
    public const string Queued = "Queued";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
}

public static class OffboardingNotificationKeys
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

public class OffboardingNotificationService : IOffboardingNotificationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OffboardingNotificationService> _logger;

    public OffboardingNotificationService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<OffboardingNotificationService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public bool SendingEnabled => _configuration.GetValue<bool>("Offboarding:SendNotifications");

    public void Enqueue(OffboardingNotificationEvent notificationEvent)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ProcessAsync(notificationEvent, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Offboarding notification {Trigger} for {OffboardingId} failed.",
                    notificationEvent.TriggerCode, notificationEvent.OffboardingId);
            }
        });
    }

    public async Task<int> ProcessAsync(OffboardingNotificationEvent evt, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IOffboardingRepository>();

        var setting = await repo.GetNotificationSettingAsync(evt.OrganizationId, evt.TriggerCode, cancellationToken);
        if (setting is not { IsEnabled: true }) return 0;

        var roles = setting.Recipients.Where(r => OffboardingRecipientRoles.All.Contains(r)).ToList();
        var channels = new List<string>();
        if (setting.ChannelEmail) channels.Add("Email");
        if (setting.ChannelWhatsapp) channels.Add("WhatsApp");
        if (roles.Count == 0 || channels.Count == 0) return 0;

        // Defence in depth: the repository only resolves the Employee role for explicit, non-involuntary opt-ins,
        // but the policy is re-checked here so a future query change cannot silently notify a dismissed employee.
        var header = await repo.GetHeaderAsync(evt.OrganizationId, evt.OffboardingId, cancellationToken);
        if (header == null) return 0;
        var employeeAllowed = OffboardingValidation.CanNotifyEmployee(header.SeparationType, header.NotifyEmployee);

        var recipients = (await repo.ResolveNotificationRecipientsAsync(
                evt.OrganizationId, evt.OffboardingId, evt.TaskId, roles, cancellationToken))
            .Where(r => employeeAllowed || !r.Role.Equals(OffboardingRecipientRoles.Employee, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (recipients.Count == 0) return 0;

        var sendEnabled = SendingEnabled;
        var employeeName = header.EmployeeName;
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
                    evt.OrganizationId, evt.OffboardingId, evt.TriggerCode, key, channel,
                    isEmail ? recipient.Email : recipient.Name,
                    skipReason != null ? OffboardingNotificationStatuses.Skipped : OffboardingNotificationStatuses.Queued,
                    skipReason, cancellationToken);
                if (!inserted) continue; // duplicate: already handled
                recorded++;

                if (skipReason != null || !sendEnabled) continue;

                if (!isEmail)
                {
                    await repo.UpdateNotificationAttemptAsync(evt.OrganizationId, key, OffboardingNotificationStatuses.Skipped,
                        "WhatsApp delivery is not available for offboarding notifications yet.", cancellationToken);
                    continue;
                }

                try
                {
                    var (subject, text) = BuildMessage(evt.TriggerCode, employeeName, recipient.Role);
                    var email = scope.ServiceProvider.GetRequiredService<IEmailService>();
                    await email.SendEmailAsync(recipient.Email!, subject, text, $"<p>{System.Net.WebUtility.HtmlEncode(text)}</p>", cancellationToken);
                    await repo.UpdateNotificationAttemptAsync(evt.OrganizationId, key, OffboardingNotificationStatuses.Sent, null, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Sending offboarding email {Trigger} failed.", evt.TriggerCode);
                    await repo.UpdateNotificationAttemptAsync(evt.OrganizationId, key, OffboardingNotificationStatuses.Failed, ex.Message, cancellationToken);
                }
            }
        }

        return recorded;
    }

    /// <summary>
    /// Plain-text subject/body for a trigger. Messages never include the separation reason or any confidential detail;
    /// the employee being offboarded only ever receives a neutral message.
    /// </summary>
    public static (string Subject, string Text) BuildMessage(string triggerCode, string employeeName, string? recipientRole = null)
    {
        if (string.Equals(recipientRole, OffboardingRecipientRoles.Employee, StringComparison.OrdinalIgnoreCase))
        {
            return triggerCode switch
            {
                OffboardingTriggers.TaskAssigned or OffboardingTriggers.TaskDueSoon or OffboardingTriggers.TaskOverdue =>
                    ("Offboarding task", "You have an offboarding task that needs your attention. Please contact HR if you have questions."),
                OffboardingTriggers.LastWorkingDayApproaching =>
                    ("Your last working day is approaching", "Your last working day is approaching. Please contact HR if you have questions."),
                _ => ("Offboarding update", "There is an update on your offboarding process. Please contact HR if you have questions.")
            };
        }

        return triggerCode switch
        {
            OffboardingTriggers.OffboardingCreated => ($"Offboarding created: {employeeName}", $"An offboarding was created for {employeeName}."),
            OffboardingTriggers.OffboardingStarted => ($"Offboarding started: {employeeName}", $"The offboarding for {employeeName} has started."),
            OffboardingTriggers.OffboardingPendingClearance => ($"Offboarding pending clearance: {employeeName}", $"All required tasks for {employeeName} are complete and the offboarding is pending clearance."),
            OffboardingTriggers.OffboardingCompleted => ($"Offboarding completed: {employeeName}", $"The offboarding for {employeeName} is complete."),
            OffboardingTriggers.OffboardingCancelled => ($"Offboarding cancelled: {employeeName}", $"The offboarding for {employeeName} was cancelled."),
            OffboardingTriggers.TaskAssigned => ($"New offboarding task: {employeeName}", $"You have a new offboarding task for {employeeName}."),
            OffboardingTriggers.TaskDueSoon => ($"Offboarding task due soon: {employeeName}", $"An offboarding task for {employeeName} is due soon."),
            OffboardingTriggers.TaskOverdue => ($"Offboarding task overdue: {employeeName}", $"An offboarding task for {employeeName} is overdue."),
            OffboardingTriggers.TaskApprovalRequested => ($"Approval needed: {employeeName}", $"An offboarding task for {employeeName} needs your approval."),
            OffboardingTriggers.TaskApproved => ($"Task approved: {employeeName}", $"Your offboarding task for {employeeName} was approved."),
            OffboardingTriggers.TaskRejected => ($"Task needs changes: {employeeName}", $"Your offboarding task for {employeeName} was rejected and needs changes."),
            OffboardingTriggers.LastWorkingDayApproaching => ($"Last working day approaching: {employeeName}", $"The last working day for {employeeName} is approaching."),
            OffboardingTriggers.AssetReturnOverdue => ($"Asset return overdue: {employeeName}", $"Company assets for {employeeName} have not been returned yet."),
            _ => ($"Offboarding update: {employeeName}", $"There is an update on the offboarding for {employeeName}.")
        };
    }
}
