using System.Text.Json;
using nexthire_api.DTOs;
using Nexa.WorkflowAutomation.Serialization;

namespace nexthire_api.WorkflowAutomation;

/// <summary>
/// Maps Candidate / Application fields into workflow initial variables.
/// </summary>
public static class CandidateWorkflowVariables
{
    public const string CandidateCreatedTrigger = "candidate.created";
    public const string ApplicationCreatedTrigger = "application.created";
    public const string ApplicationStageChangedTrigger = "application.stage_changed";
    public const string SourcingCompletedTrigger = "sourcing.completed";
    public const string ScreeningOutreachTrigger = "screening.outreach";
    public const string EmailReceivedTrigger = "email.received";
    public const string WhatsAppReceivedTrigger = "whatsapp.received";

    public const string CandidateEntityType = "Candidate";
    public const string ApplicationEntityType = "Application";
    public const string JobEntityType = "Job";

    public static IReadOnlyDictionary<string, JsonElement> FromCandidate(CandidateDto candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var name = $"{candidate.FirstName} {candidate.LastName}".Trim();
        var variables = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["Candidate.Id"] = WorkflowJsonSerializer.SerializeToElement(candidate.Id.ToString("D")),
        };

        if (!string.IsNullOrWhiteSpace(candidate.FirstName))
            variables["Candidate.FirstName"] = WorkflowJsonSerializer.SerializeToElement(candidate.FirstName);
        if (!string.IsNullOrWhiteSpace(candidate.LastName))
            variables["Candidate.LastName"] = WorkflowJsonSerializer.SerializeToElement(candidate.LastName);
        if (!string.IsNullOrWhiteSpace(name))
            variables["Candidate.Name"] = WorkflowJsonSerializer.SerializeToElement(name);
        if (!string.IsNullOrWhiteSpace(candidate.Email))
            variables["Candidate.Email"] = WorkflowJsonSerializer.SerializeToElement(candidate.Email);
        if (!string.IsNullOrWhiteSpace(candidate.Phone))
            variables["Candidate.Phone"] = WorkflowJsonSerializer.SerializeToElement(candidate.Phone);
        if (!string.IsNullOrWhiteSpace(candidate.Source))
            variables["Candidate.Source"] = WorkflowJsonSerializer.SerializeToElement(candidate.Source);

        return variables;
    }

    public static IReadOnlyDictionary<string, JsonElement> FromApplication(
        ApplicationListItemDto application,
        CandidateDto? candidate = null)
    {
        ArgumentNullException.ThrowIfNull(application);

        var variables = candidate is null
            ? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonElement>(FromCandidate(candidate), StringComparer.OrdinalIgnoreCase);

        variables["Application.Id"] = WorkflowJsonSerializer.SerializeToElement(application.ApplicationId.ToString("D"));
        variables["Application.JobId"] = WorkflowJsonSerializer.SerializeToElement(application.JobId.ToString("D"));
        variables["Application.StageId"] = WorkflowJsonSerializer.SerializeToElement(application.CurrentStageId.ToString("D"));
        if (!string.IsNullOrWhiteSpace(application.JobTitle))
            variables["Job.Title"] = WorkflowJsonSerializer.SerializeToElement(application.JobTitle);
        if (!string.IsNullOrWhiteSpace(application.CurrentStageName))
            variables["Application.Stage"] = WorkflowJsonSerializer.SerializeToElement(application.CurrentStageName);
        if (!string.IsNullOrWhiteSpace(application.Status))
            variables["Application.Status"] = WorkflowJsonSerializer.SerializeToElement(application.Status);
        if (!string.IsNullOrWhiteSpace(application.CandidateName))
            variables["Candidate.Name"] = WorkflowJsonSerializer.SerializeToElement(application.CandidateName);
        if (!string.IsNullOrWhiteSpace(application.CandidateEmail))
            variables["Candidate.Email"] = WorkflowJsonSerializer.SerializeToElement(application.CandidateEmail);

        variables["Candidate.Id"] = WorkflowJsonSerializer.SerializeToElement(application.CandidateId.ToString("D"));
        variables["Job.Id"] = WorkflowJsonSerializer.SerializeToElement(application.JobId.ToString("D"));

        return variables;
    }
}
