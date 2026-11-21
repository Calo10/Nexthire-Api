using System.Text.Json;
using nexthire_api.WorkflowAutomation.Conversations;
using Nexa.WorkflowAutomation.Activities.AI;
using Nexa.WorkflowAutomation.Contracts.Activities;
using Nexa.WorkflowAutomation.Exceptions;
using Nexa.WorkflowAutomation.Serialization;
using Nexa.WorkflowAutomation.Utilities;

namespace nexthire_api.WorkflowAutomation.Activities;

/// <summary>
/// NextHire <c>conversation.askQuestions</c>: starts an interview via Messenger and waits for
/// <c>conversation.completed</c> (or configured completionEventType).
/// </summary>
public sealed class NextHireAskQuestionsActivity : IWorkflowActivity
{
    private readonly IWorkflowVariableResolver _variableResolver;

    public NextHireAskQuestionsActivity(IWorkflowVariableResolver variableResolver)
    {
        _variableResolver = variableResolver;
    }

    public string ActivityType => AskQuestionsActivity.TypeName;

    public async Task<ActivityExecutionResult> ExecuteAsync(
        ActivityExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var configuration = context.Step.Configuration;

        var channel = ActivityConfigurationReader.GetRequiredString(
            AskQuestionsActivity.TypeName, configuration, "channel");

        var recipientVariable =
            ActivityConfigurationReader.GetOptionalString(configuration, "recipientVariable")
            ?? ActivityConfigurationReader.GetOptionalString(configuration, "contactVariable")
            ?? throw new ActivityConfigurationException(
                AskQuestionsActivity.TypeName, "'recipientVariable' is required.");

        var introMessage = ActivityConfigurationReader.GetOptionalString(configuration, "introMessage");
        var completionEventType =
            ActivityConfigurationReader.GetOptionalString(configuration, "completionEventType")
            ?? "conversation.completed";

        var questions = ReadQuestions(configuration);
        if (questions.Count == 0)
        {
            throw new ActivityConfigurationException(
                AskQuestionsActivity.TypeName, "'questions' must contain at least one item.");
        }

        var recipient = _variableResolver.ResolveAsString(context.WorkflowContext.Variables, recipientVariable)
            ?? throw new ActivityConfigurationException(
                AskQuestionsActivity.TypeName, $"Variable '{recipientVariable}' is empty.");

        var interview = ActivityServiceLocator.GetRequiredService<IConversationInterviewService>(
            context, AskQuestionsActivity.TypeName);

        var started = await interview.StartAsync(new StartInterviewRequest
        {
            OrganizationId = context.Execution.OrganizationId,
            WorkflowExecutionId = context.Execution.Id,
            Channel = channel,
            Recipient = recipient,
            IntroMessage = introMessage,
            Questions = questions,
            CompletionEventType = completionEventType
        }, cancellationToken);

        if (!started.Success)
        {
            return ActivityExecutionResult.Failed(
                started.ErrorCode ?? "interview.start.failed",
                started.ErrorMessage ?? "Failed to start interview.");
        }

        var conversationId = started.ConversationId
            ?? throw new ProviderOperationException("interview", "conversationId was not returned.");

        context.Execution.ConversationId = conversationId;
        context.WorkflowContext.ConversationId = conversationId;

        var output = WorkflowJsonSerializer.SerializeToElement(new
        {
            conversationId,
            questionIndex = 0,
            answers = new Dictionary<string, string>()
        });

        return ActivityExecutionResult.WaitForEvent(
            completionEventType.Trim(),
            conversationId,
            resumeAt: null,
            output);
    }

    private static IReadOnlyList<InterviewQuestionDefinition> ReadQuestions(JsonElement configuration)
    {
        if (configuration.ValueKind != JsonValueKind.Object ||
            !configuration.TryGetProperty("questions", out var questionsElement) ||
            questionsElement.ValueKind != JsonValueKind.Array)
        {
            throw new ActivityConfigurationException(
                AskQuestionsActivity.TypeName, "'questions' is required.");
        }

        List<InterviewQuestionDefinition>? questions;
        try
        {
            questions = WorkflowJsonSerializer.Deserialize<List<InterviewQuestionDefinition>>(questionsElement);
        }
        catch (Exception ex)
        {
            throw new ActivityConfigurationException(
                AskQuestionsActivity.TypeName, $"Invalid 'questions' configuration: {ex.Message}");
        }

        if (questions is null)
        {
            throw new ActivityConfigurationException(
                AskQuestionsActivity.TypeName, "'questions' could not be deserialized.");
        }

        for (var i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            if (string.IsNullOrWhiteSpace(q.Id))
            {
                throw new ActivityConfigurationException(
                    AskQuestionsActivity.TypeName, $"questions[{i}].id is required.");
            }

            if (string.IsNullOrWhiteSpace(q.Text))
            {
                throw new ActivityConfigurationException(
                    AskQuestionsActivity.TypeName, $"questions[{i}].text is required.");
            }

            if (string.IsNullOrWhiteSpace(q.Variable))
            {
                throw new ActivityConfigurationException(
                    AskQuestionsActivity.TypeName, $"questions[{i}].variable is required.");
            }
        }

        return questions;
    }
}
