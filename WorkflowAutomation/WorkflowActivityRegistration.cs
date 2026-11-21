using nexthire_api.WorkflowAutomation.Activities;
using Microsoft.Extensions.DependencyInjection;
using Nexa.WorkflowAutomation.Activities.Communication;
using Nexa.WorkflowAutomation.Activities.Flow;
using Nexa.WorkflowAutomation.DependencyInjection;

namespace nexthire_api.WorkflowAutomation;

public static class WorkflowActivityRegistration
{
    /// <summary>
    /// Registers core package activities, replacing selected activities with NextHire variants
    /// (same activity type keys).
    /// </summary>
    public static IServiceCollection AddNextHireWorkflowActivities(this IServiceCollection services)
    {
        services.AddWorkflowActivity<EndActivity>();
        services.AddWorkflowActivity<NextHireDelayActivity>();
        services.AddWorkflowActivity<NextHireWaitForEventActivity>();
        services.AddWorkflowActivity<NextHireBranchActivity>();
        services.AddWorkflowActivity<SetVariableActivity>();
        services.AddWorkflowActivity<NextHireSendEmailActivity>();
        services.AddWorkflowActivity<NextHireSendWhatsAppActivity>();
        services.AddWorkflowActivity<CallActivity>();
        services.AddWorkflowActivity<NextHireSendDocumentActivity>();
        services.AddWorkflowActivity<NextHireSendForSignatureActivity>();
        services.AddWorkflowActivity<NextHireScheduleMeetingActivity>();
        services.AddWorkflowActivity<NextHireExecuteAIActivity>();
        services.AddWorkflowActivity<NextHireAskQuestionsActivity>();
        services.AddWorkflowActivity<NextHireMoveStageActivity>();
        services.AddWorkflowActivity<NextHireCreateTaskActivity>();
        return services;
    }
}
