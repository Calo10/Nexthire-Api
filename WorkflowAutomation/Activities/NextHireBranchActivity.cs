using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nexa.WorkflowAutomation.Activities.Flow;
using Nexa.WorkflowAutomation.Contracts.Activities;
using Nexa.WorkflowAutomation.Exceptions;
using Nexa.WorkflowAutomation.Models.Definitions;
using Nexa.WorkflowAutomation.Utilities;

namespace nexthire_api.WorkflowAutomation.Activities;

/// <summary>
/// NextHire branch activity for <c>flow.branch</c>.
/// Supports package structured config and a simple <c>condition</c> expression.
/// </summary>
public sealed class NextHireBranchActivity : IWorkflowActivity
{
    private static readonly Regex ConditionRegex = new(
        @"^\s*(?<left>[A-Za-z_][A-Za-z0-9_.]*)\s*(?<op>==|!=|>=|<=|>|<)\s*(?<right>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IWorkflowVariableResolver _variableResolver;
    private readonly BranchActivity _structuredBranch;

    public NextHireBranchActivity(IWorkflowVariableResolver variableResolver)
    {
        _variableResolver = variableResolver ?? throw new ArgumentNullException(nameof(variableResolver));
        _structuredBranch = new BranchActivity(variableResolver);
    }

    public string ActivityType => BranchActivity.TypeName;

    public Task<ActivityExecutionResult> ExecuteAsync(
        ActivityExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var configuration = context.Step.Configuration;
        var condition = ActivityConfigurationReader.GetOptionalString(configuration, "condition");

        if (string.IsNullOrWhiteSpace(condition))
        {
            // Preserve package structured config: variable / operator / value / trueTransition / falseTransition
            return _structuredBranch.ExecuteAsync(context, cancellationToken);
        }

        EnsureRequiredTransitions(context.Step);

        var trueTransition = ActivityConfigurationReader.GetOptionalString(configuration, "trueTransition") ?? "true";
        var falseTransition = ActivityConfigurationReader.GetOptionalString(configuration, "falseTransition") ?? "false";

        bool matched;
        try
        {
            matched = EvaluateCondition(condition, context);
        }
        catch (ActivityConfigurationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActivityExecutionResult.Failed(
                "branch.condition.invalid",
                $"Condition could not be evaluated: {ex.Message}"));
        }

        return Task.FromResult(ActivityExecutionResult.Branch(matched ? trueTransition : falseTransition));
    }

    private static void EnsureRequiredTransitions(WorkflowStepDefinition step)
    {
        var keys = step.Transitions?
            .Select(t => t.Key)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!keys.Contains("true"))
        {
            throw new ActivityConfigurationException(
                BranchActivity.TypeName,
                "Transition with key 'true' is required for flow.branch.");
        }

        if (!keys.Contains("false"))
        {
            throw new ActivityConfigurationException(
                BranchActivity.TypeName,
                "Transition with key 'false' is required for flow.branch.");
        }
    }

    private bool EvaluateCondition(string condition, ActivityExecutionContext context)
    {
        var match = ConditionRegex.Match(condition);
        if (!match.Success)
        {
            throw new ActivityConfigurationException(
                BranchActivity.TypeName,
                $"Unsupported condition '{condition}'. Expected: Variable Op Value (e.g. Lead.Score >= 80).");
        }

        var left = match.Groups["left"].Value;
        var op = match.Groups["op"].Value;
        var rightRaw = match.Groups["right"].Value.Trim();

        if (!_variableResolver.TryResolve(context.WorkflowContext.Variables, left, out var actual) ||
            actual.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new ActivityConfigurationException(
                BranchActivity.TypeName,
                $"Variable '{left}' is missing or null; condition cannot be evaluated.");
        }

        var expected = ParseLiteral(rightRaw);
        return op switch
        {
            "==" => ValuesEqual(actual, expected),
            "!=" => !ValuesEqual(actual, expected),
            ">" => Compare(actual, expected) > 0,
            ">=" => Compare(actual, expected) >= 0,
            "<" => Compare(actual, expected) < 0,
            "<=" => Compare(actual, expected) <= 0,
            _ => throw new ActivityConfigurationException(
                BranchActivity.TypeName,
                $"Unsupported operator '{op}'.")
        };
    }

    private static JsonElement ParseLiteral(string raw)
    {
        if (raw.Length >= 2 &&
            ((raw.StartsWith('"') && raw.EndsWith('"')) || (raw.StartsWith('\'') && raw.EndsWith('\''))))
        {
            var unquoted = raw[1..^1];
            return JsonSerializer.SerializeToElement(unquoted);
        }

        if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
        {
            return JsonSerializer.SerializeToElement(number);
        }

        if (bool.TryParse(raw, out var boolean))
        {
            return JsonSerializer.SerializeToElement(boolean);
        }

        // Bare word treated as string literal
        return JsonSerializer.SerializeToElement(raw);
    }

    private static bool ValuesEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.String || right.ValueKind == JsonValueKind.String)
        {
            return string.Equals(AsString(left), AsString(right), StringComparison.OrdinalIgnoreCase);
        }

        if (TryGetDecimal(left, out var leftNumber) && TryGetDecimal(right, out var rightNumber))
        {
            return leftNumber == rightNumber;
        }

        if (left.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            right.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return left.GetBoolean() == right.GetBoolean();
        }

        return left.GetRawText() == right.GetRawText();
    }

    private static int Compare(JsonElement left, JsonElement right)
    {
        if (TryGetDecimal(left, out var leftNumber) && TryGetDecimal(right, out var rightNumber))
        {
            return leftNumber.CompareTo(rightNumber);
        }

        return string.Compare(AsString(left), AsString(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetDecimal(JsonElement element, out decimal value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value))
        {
            return true;
        }

        return decimal.TryParse(AsString(element), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private static string? AsString(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => element.GetRawText().Trim('"')
        };
}
