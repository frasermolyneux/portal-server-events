using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Rules;

public sealed class RuleMatcherTests
{
    [Theory]
    [InlineData(true, true, null, null)]
    [InlineData(true, false, null, false)]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, null, null)]
    [InlineData(false, true, null, true)]
    [InlineData(false, false, false, false)]
    public void Evaluate_CombinesThreeValuedResults(
        bool and, bool? first, bool? second, bool? expected)
    {
        var node = new Node(and ? RuleConditionGroupOperator.And : RuleConditionGroupOperator.Or, [new(Value: first), new(Value: second)]);

        Assert.Equal(expected, Evaluate(node));
    }

    [Fact]
    public void Evaluate_NestedGroups_PropagatesUnknown()
    {
        var node = new Node(RuleConditionGroupOperator.And,
        [
            new(Value: true),
            new(RuleConditionGroupOperator.Or, [new(Value: false), new(Value: null)])
        ]);

        Assert.Null(Evaluate(node));
    }

    [Fact]
    public void Evaluate_UnknownGroupOrMissingChildren_IsUnknown()
    {
        Assert.Null(Evaluate(new Node(RuleConditionGroupOperator.Unknown, [])));
        Assert.Null(Evaluate(new Node(RuleConditionGroupOperator.And)));
        Assert.Null(Evaluate(null));
    }

    [Fact]
    public void EvaluateWithEvidence_ExcludesTrueLeavesFromFalseBranches()
    {
        var node = new Node(RuleConditionGroupOperator.Or,
        [
            new(RuleConditionGroupOperator.And, [new(Value: true), new(Value: false)]),
            new(Value: true),
            new(Value: true)
        ]);

        var result = RuleMatcher.EvaluateWithEvidence(
            node,
            static item => (item.Operator, item.Children),
            static item => new RuleMatch<Node>(item.Value, item.Value == true ? [item] : []));

        Assert.True(result.Value);
        Assert.Equal(node.Children!.Skip(1), result.Evidence);
    }

    private static bool? Evaluate(Node? node) =>
        RuleMatcher.Evaluate(node, static item => (item.Operator, item.Children), static item => item.Value);

    private sealed record Node(
        RuleConditionGroupOperator? Operator = null,
        IReadOnlyCollection<Node>? Children = null,
        bool? Value = null);
}
