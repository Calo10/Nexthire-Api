using System.Text.Json;
using Nexa.WorkflowAutomation.Utilities;

namespace nexthire_api.WorkflowAutomation;

/// <summary>
/// Designer <c>waitForReply</c> maps onto activity <c>completionEventType</c>.
/// </summary>
public static class WorkflowReplyWait
{
    public const string EmailReplyEvent = "email.reply.received";
    public const string WhatsAppReplyEvent = "whatsapp.received";

    public static string? ResolveCompletionEventType(JsonElement configuration, string defaultWhenWaiting)
    {
        var waitForReply = ActivityConfigurationReader.GetOptionalBool(configuration, "waitForReply");
        var explicitType = ActivityConfigurationReader.GetOptionalString(configuration, "completionEventType");

        if (waitForReply == false)
            return null;

        if (waitForReply == true)
            return string.IsNullOrWhiteSpace(explicitType) ? defaultWhenWaiting : explicitType.Trim();

        return string.IsNullOrWhiteSpace(explicitType) ? null : explicitType.Trim();
    }
}
