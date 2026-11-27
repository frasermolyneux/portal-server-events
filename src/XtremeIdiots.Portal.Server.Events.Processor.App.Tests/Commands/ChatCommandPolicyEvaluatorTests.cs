using System.Text.Json;

using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.RulesEngine;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public sealed class ChatCommandPolicyEvaluatorTests
{
    private static readonly Guid TagId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTime Now = new(2026, 1, 8, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MissingPolicyLeavesExistingAuthorizationUnchanged()
    {
        Assert.True(Evaluate(null, null).Allowed);
    }

    [Fact]
    public void FirstMatchingRuleUsesOrderAndPreservesArrayOrderForTies()
    {
        var policy = new ChatCommandPolicy
        {
            DefaultDecision = ChatCommandPolicyDecision.Deny,
            Rules =
            [
                Rule(10, ChatCommandPolicyDecision.Deny, Always()),
                Rule(1, ChatCommandPolicyDecision.Allow, Tags("ContainsAny")),
                Rule(1, ChatCommandPolicyDecision.Deny, Always())
            ]
        };

        Assert.True(Evaluate(policy, Snapshot(TagId)).Allowed);
        Assert.False(Evaluate(policy, Snapshot()).Allowed);
    }

    [Fact]
    public void DisabledRuleIsSkippedAndDefaultDecisionApplies()
    {
        var rule = Rule(0, ChatCommandPolicyDecision.Deny, Always());
        rule.Enabled = false;
        var policy = new ChatCommandPolicy { DefaultDecision = ChatCommandPolicyDecision.Allow, Rules = [rule] };

        Assert.True(Evaluate(policy, null).Allowed);
    }

    [Fact]
    public void UnknownFirstApplicableRuleBlocksLaterAllowAndDefault()
    {
        var policy = new ChatCommandPolicy
        {
            DefaultDecision = ChatCommandPolicyDecision.Allow,
            Rules = [Rule(0, ChatCommandPolicyDecision.Deny, Tags("ContainsNone")), Rule(1, ChatCommandPolicyDecision.Allow, Always())]
        };

        Assert.False(Evaluate(policy, null).Allowed);
        Assert.False(Evaluate(policy, new CommandPolicyFacts(null, null)).Allowed);
    }

    [Fact]
    public void UnsupportedConditionDoesNotGrantAccess()
    {
        var policy = new ChatCommandPolicy
        {
            DefaultDecision = ChatCommandPolicyDecision.Allow,
            Rules = [Rule(0, ChatCommandPolicyDecision.Allow, new RuleConditionNode { ConditionId = "player.first-seen-guid", Parameters = [] })]
        };

        Assert.False(Evaluate(policy, Snapshot()).Allowed);
    }

    [Fact]
    public void UnsupportedLeavesDoNotOverrideAnEarlierMatchOrDisabledRule()
    {
        var unsupported = new RuleConditionNode { ConditionId = "future.condition", Parameters = [] };
        var disabled = Rule(0, ChatCommandPolicyDecision.Deny, unsupported);
        disabled.Enabled = false;
        var policy = new ChatCommandPolicy
        {
            DefaultDecision = ChatCommandPolicyDecision.Deny,
            Rules = [disabled, Rule(1, ChatCommandPolicyDecision.Allow, Always()), Rule(2, ChatCommandPolicyDecision.Deny, unsupported)]
        };

        Assert.True(Evaluate(policy, null).Allowed);
    }

    [Fact]
    public void FalseAndUnknownGroupIsFalseRatherThanBlockingLaterRule()
    {
        var group = new RuleConditionNode
        {
            GroupOperator = RuleConditionGroupOperator.And,
            Conditions = [Tags("ContainsAny"), new RuleConditionNode { ConditionId = "unknown", Parameters = [] }]
        };
        var policy = new ChatCommandPolicy
        {
            DefaultDecision = ChatCommandPolicyDecision.Deny,
            Rules = [Rule(0, ChatCommandPolicyDecision.Deny, group), Rule(1, ChatCommandPolicyDecision.Allow, Always())]
        };

        Assert.True(Evaluate(policy, Snapshot()).Allowed);
    }

    [Fact]
    public void TrueOrUnknownGroupMatchesWithoutTreatingUnknownAsFalse()
    {
        var group = new RuleConditionNode
        {
            GroupOperator = RuleConditionGroupOperator.Or,
            Conditions = [Always(), new RuleConditionNode { ConditionId = "future.condition", Parameters = [] }]
        };
        var policy = new ChatCommandPolicy
        {
            DefaultDecision = ChatCommandPolicyDecision.Deny,
            Rules = [Rule(0, ChatCommandPolicyDecision.Allow, group)]
        };

        Assert.True(Evaluate(policy, null).Allowed);
    }

    [Fact]
    public void FalseOrUnknownGroupBlocksTheAllowDefault()
    {
        var group = new RuleConditionNode
        {
            GroupOperator = RuleConditionGroupOperator.Or,
            Conditions = [Tags("ContainsAny"), new RuleConditionNode { ConditionId = "future.condition", Parameters = [] }]
        };
        var policy = new ChatCommandPolicy
        {
            DefaultDecision = ChatCommandPolicyDecision.Allow,
            Rules = [Rule(0, ChatCommandPolicyDecision.Deny, group)]
        };

        Assert.False(Evaluate(policy, Snapshot()).Allowed);
    }

    [Fact]
    public void UnknownDecisionAndMissingRulesFailClosed()
    {
        Assert.False(Evaluate(
            new ChatCommandPolicy { DefaultDecision = ChatCommandPolicyDecision.Unknown, Rules = [] }, Snapshot()).Allowed);
        Assert.False(Evaluate(
            new ChatCommandPolicy { DefaultDecision = ChatCommandPolicyDecision.Allow }, Snapshot()).Allowed);
    }

    [Fact]
    public void FirstSeenUsesStrictSelectedPlayerAgeBoundaryAndUnknownFactsDeny()
    {
        var policy = new ChatCommandPolicy
        {
            DefaultDecision = ChatCommandPolicyDecision.Deny,
            Rules = [Rule(0, ChatCommandPolicyDecision.Allow, FirstSeen(7))]
        };
        var cutoff = Now.AddDays(-7);

        Assert.True(Evaluate(policy, new CommandPolicyFacts(cutoff.AddTicks(1), null)).Allowed);
        Assert.False(Evaluate(policy, new CommandPolicyFacts(cutoff, null)).Allowed);
        Assert.False(Evaluate(policy, new CommandPolicyFacts(cutoff.AddTicks(-1), null)).Allowed);
        Assert.False(Evaluate(policy, new CommandPolicyFacts(null, null)).Allowed);

        policy.Rules![0].Condition = FirstSeen(36500);
        var maximumCutoff = Now.AddDays(-36500);
        Assert.True(Evaluate(policy, new CommandPolicyFacts(maximumCutoff.AddTicks(1), null)).Allowed);
        Assert.False(Evaluate(policy, new CommandPolicyFacts(maximumCutoff, null)).Allowed);
    }

    [Fact]
    public void FirstSeenZeroWindowNeverMatchesAndInvalidWindowsAreUnknown()
    {
        var policy = new ChatCommandPolicy
        {
            DefaultDecision = ChatCommandPolicyDecision.Deny,
            Rules = [Rule(0, ChatCommandPolicyDecision.Allow, FirstSeen(0))]
        };
        Assert.False(Evaluate(policy, new CommandPolicyFacts(Now.AddDays(1), null)).Allowed);

        policy.DefaultDecision = ChatCommandPolicyDecision.Allow;
        Assert.True(Evaluate(policy, null).Allowed);
        policy.Rules![0].Condition = FirstSeen(36501);
        Assert.False(Evaluate(policy, new CommandPolicyFacts(Now, null)).Allowed);
        policy.Rules[0].Condition = FirstSeen(-1);
        Assert.False(Evaluate(policy, new CommandPolicyFacts(Now, null)).Allowed);
    }

    [Fact]
    public async Task PolicyFactsAreLoadedOnlyWhenNeededAndSharedWithinTheCommandContext()
    {
        var calls = 0;
        var context = new CommandContext
        {
            ServerId = Guid.NewGuid(),
            GameType = "CallOfDuty4",
            PlayerGuid = "guid",
            Username = "player",
            SlotId = 1,
            Message = "!fu target",
            EventGeneratedUtc = Now,
            EventPublishedUtc = Now,
            SequenceId = 1,
            PolicyFacts = new Lazy<Task<CommandPolicyFacts?>>(() =>
            {
                calls++;
                return Task.FromResult<CommandPolicyFacts?>(new CommandPolicyFacts(Now, new HashSet<Guid> { TagId }));
            })
        };
        var policy = new ChatCommandPolicy
        {
            DefaultDecision = ChatCommandPolicyDecision.Deny,
            Rules = [Rule(0, ChatCommandPolicyDecision.Allow, Tags("ContainsAny"))]
        };
        var earlierMatch = new ChatCommandPolicy
        {
            DefaultDecision = ChatCommandPolicyDecision.Deny,
            Rules = [Rule(0, ChatCommandPolicyDecision.Allow, Always()), Rule(1, ChatCommandPolicyDecision.Deny, Tags("ContainsAny"))]
        };

        Assert.True((await ChatCommandPolicyEvaluator.EvaluateAsync(null, context, default)).Allowed);
        Assert.True((await ChatCommandPolicyEvaluator.EvaluateAsync(earlierMatch, context, default)).Allowed);
        Assert.Equal(0, calls);
        Assert.True((await ChatCommandPolicyEvaluator.EvaluateAsync(policy, context, default)).Allowed);
        Assert.True((await ChatCommandPolicyEvaluator.EvaluateAsync(policy, context with { }, default)).Allowed);
        Assert.Equal(1, calls);
    }

    private static CommandAuthorizationResult Evaluate(ChatCommandPolicy? policy, CommandPolicyFacts? facts) =>
        ChatCommandPolicyEvaluator.Evaluate(policy, facts, Now);

    private static ChatCommandPolicyRule Rule(int order, ChatCommandPolicyDecision decision, RuleConditionNode condition) =>
        new() { Id = Guid.NewGuid().ToString("N"), Order = order, Decision = decision, Condition = condition };

    private static RuleConditionNode Always() => new() { ConditionId = "always", Parameters = [] };

    private static RuleConditionNode Tags(string operation) => new()
    {
        ConditionId = "player.tags",
        Parameters = new Dictionary<string, JsonElement>
        {
            ["operator"] = JsonSerializer.SerializeToElement(operation),
            ["tags"] = JsonSerializer.SerializeToElement(new[] { TagId.ToString() })
        }
    };

    private static RuleConditionNode FirstSeen(int days) => new()
    {
        ConditionId = "chat.player-first-seen",
        Parameters = new Dictionary<string, JsonElement> { ["withinDays"] = JsonSerializer.SerializeToElement(days) }
    };

    private static CommandPolicyFacts Snapshot(params Guid[] tags) => new(null, tags.ToHashSet());
}
