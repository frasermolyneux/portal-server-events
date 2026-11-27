using System.Text.Json;

using XtremeIdiots.Portal.RulesEngine.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.RulesEngine;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

internal static class ChatCommandPolicyEvaluator
{
    public static async Task<CommandAuthorizationResult> EvaluateAsync(
        ChatCommandPolicy? policy, CommandContext context, CancellationToken cancellationToken)
    {
        var result = EvaluateCore(policy, null, DateTime.UtcNow, out var needsFacts);
        if (!needsFacts || context.PolicyFacts is null)
        {
            return result;
        }

        var facts = await context.PolicyFacts.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        return EvaluateCore(policy, facts, DateTime.UtcNow, out _);
    }

    internal static CommandAuthorizationResult Evaluate(
        ChatCommandPolicy? policy, CommandPolicyFacts? facts, DateTime utcNow) =>
        EvaluateCore(policy, facts, utcNow, out _);

    private static CommandAuthorizationResult EvaluateCore(
        ChatCommandPolicy? policy, CommandPolicyFacts? facts, DateTime utcNow, out bool needsFacts)
    {
        needsFacts = false;
        if (policy is null)
        {
            return CommandAuthorizationResult.Allow();
        }

        if (policy.Rules is null || policy.Rules.Any(static rule => rule is null))
        {
            return CommandAuthorizationResult.Deny("Command policy is invalid.");
        }

        foreach (var rule in policy.Rules.Where(static rule => rule.Enabled).OrderBy(static rule => rule.Order))
        {
            var result = RuleMatcher.EvaluateCondition(rule.Condition, condition => EvaluateLeaf(condition, facts, utcNow));
            if (result.Value == RuleTruthValue.Unknown)
            {
                needsFacts = facts is null && RequiresFacts(rule.Condition);
                return CommandAuthorizationResult.Deny("Command policy dependencies are unavailable.");
            }

            if (result.Value == RuleTruthValue.True)
            {
                return ToAuthorizationResult(rule.Decision);
            }
        }

        return ToAuthorizationResult(policy.DefaultDecision);
    }

    private static bool RequiresFacts(RuleConditionNode? condition) =>
        condition is not null &&
        (condition.ConditionId is "player.tags" or "chat.player-first-seen" ||
         condition.Conditions?.Any(RequiresFacts) == true);

    private static ConditionEvaluation EvaluateLeaf(RuleConditionNode condition, CommandPolicyFacts? facts, DateTime utcNow)
    {
        if (condition.Parameters is not { } parameters)
        {
            return ConditionEvaluation.Unknown("Command condition is unsupported or invalid.");
        }

        bool? matches = condition.ConditionId switch
        {
            "player.tags" => EvaluateTags(parameters, facts?.TagIds),
            "chat.player-first-seen" => EvaluateFirstSeen(parameters, facts?.FirstSeen, utcNow),
            _ => null
        };
        return matches switch
        {
            true => ConditionEvaluation.True,
            false => ConditionEvaluation.False,
            null => ConditionEvaluation.Unknown("Command condition facts are unavailable or invalid.")
        };
    }

    private static bool? EvaluateTags(IReadOnlyDictionary<string, JsonElement> parameters, IReadOnlySet<Guid>? tagIds)
    {
        var operation = parameters.TryGetValue("operator", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
        return SharedRuleConditionResolver.EvaluateTags(tagIds, parameters, operation);
    }

    private static bool? EvaluateFirstSeen(
        IReadOnlyDictionary<string, JsonElement> parameters, DateTime? firstSeen, DateTime utcNow)
    {
        if (!parameters.TryGetValue("withinDays", out var value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var days) || days is < 0 or > 36500)
        {
            return null;
        }

        if (days == 0)
        {
            return false;
        }

        return firstSeen.HasValue
            ? utcNow.Ticks - firstSeen.Value.Ticks < days * TimeSpan.TicksPerDay
            : null;
    }

    private static CommandAuthorizationResult ToAuthorizationResult(ChatCommandPolicyDecision decision) => decision switch
    {
        ChatCommandPolicyDecision.Allow => CommandAuthorizationResult.Allow(),
        ChatCommandPolicyDecision.Deny => CommandAuthorizationResult.Deny("Command policy denied access."),
        _ => CommandAuthorizationResult.Deny("Command policy decision is invalid.")
    };
}
