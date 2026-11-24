namespace XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

internal sealed record RuleMatch<T>(bool? Value, IReadOnlyList<T> Evidence);

internal static class RuleMatcher
{
    public static bool? Evaluate<TCondition>(
        TCondition? condition,
        Func<TCondition, (RuleConditionGroupOperator? Operator, IReadOnlyCollection<TCondition>? Children)> getGroup,
        Func<TCondition, bool?> evaluateLeaf)
        where TCondition : class
        => EvaluateWithEvidence(
            condition, getGroup, node => new RuleMatch<object>(evaluateLeaf(node), [])).Value;

    public static RuleMatch<TEvidence> EvaluateWithEvidence<TCondition, TEvidence>(
        TCondition? condition,
        Func<TCondition, (RuleConditionGroupOperator? Operator, IReadOnlyCollection<TCondition>? Children)> getGroup,
        Func<TCondition, RuleMatch<TEvidence>> evaluateLeaf)
        where TCondition : class
    {
        if (condition is null)
        {
            return new(null, []);
        }

        var (groupOperator, children) = getGroup(condition);
        if (groupOperator is null)
        {
            return evaluateLeaf(condition);
        }

        if (children is null || groupOperator is not (RuleConditionGroupOperator.And or RuleConditionGroupOperator.Or))
        {
            return new(null, []);
        }

        var hasUnknown = false;
        var hasTrue = false;
        var hasFalse = false;
        var evidence = new List<TEvidence>();
        foreach (var child in children)
        {
            var result = EvaluateWithEvidence(child, getGroup, evaluateLeaf);
            if (result.Value == true)
            {
                hasTrue = true;
                evidence.AddRange(result.Evidence);
            }
            else if (result.Value == false)
            {
                hasFalse = true;
            }
            else
            {
                hasUnknown = true;
            }
        }

        bool? value = groupOperator switch
        {
            RuleConditionGroupOperator.And when hasFalse => false,
            RuleConditionGroupOperator.Or when hasTrue => true,
            _ when hasUnknown => null,
            _ => groupOperator == RuleConditionGroupOperator.And
        };
        return new(value, value == true ? evidence : []);
    }
}
