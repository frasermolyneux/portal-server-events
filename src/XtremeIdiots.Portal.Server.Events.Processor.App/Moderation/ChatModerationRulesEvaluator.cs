using System.Text.Json;

using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.RulesEngine.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Moderation;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.RulesEngine;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Moderation;

public interface IChatModerationRulesEvaluator
{
    Task EvaluateAsync(ModerationContext context, ChatModerationResult? analysis, CancellationToken ct);
}

internal sealed class ChatModerationRulesEvaluator(
    ConfiguredFeatureRulesProvider settings,
    IRepositoryApiClient repository,
    ILogger<ChatModerationRulesEvaluator> logger,
    TimeProvider? timeProvider = null) : IChatModerationRulesEvaluator
{
    private const int MaximumCheckDetails = 64;
    private const string CheckId = "condition";
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task EvaluateAsync(ModerationContext context, ChatModerationResult? analysis, CancellationToken ct)
    {
        if (context.OccurrenceId is not { } occurrenceId || occurrenceId == Guid.Empty)
        {
            logger.LogDebug("Chat moderation rules skipped because the event has no occurrence identity.");
            return;
        }

        var configured = await settings.GetAsync(context.ServerId, ChatModerationRulesConstants.FeatureId,
            ChatModerationRulesConstants.Namespace, ct).ConfigureAwait(false);
        if (!configured.Enabled)
        {
            return;
        }

        var now = clock.GetUtcNow().ToUniversalTime();
        var snapshot = new FeatureRulesSnapshot(ChatModerationRulesConstants.FeatureId, configured.Rules,
            RuleExecutionMode.DryRun);
        var evaluation = Evaluate(context, analysis, snapshot, occurrenceId, now);
        var result = await repository.RuleExecutions.V1.CreateChatEvaluation(context.ServerId, evaluation, ct)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Failed to record chat moderation evaluation: {result.StatusCode}.");
        }
    }

    internal static CreateRuleChatEvaluationDto Evaluate(
        ModerationContext context,
        ChatModerationResult? analysis,
        FeatureRulesSnapshot snapshot,
        Guid occurrenceId,
        DateTimeOffset now)
    {
        var nodes = new List<RuleEvaluationNodeEvidence>();
        var decisions = new List<RuleActionDecisionEvidence>();
        var details = new List<RuleChatCheckDetail>();
        var omittedNodes = 0;
        var omittedDecisions = 0;
        var evaluated = false;
        var matched = false;
        var unknown = false;
        var rules = snapshot.Rules.OrderBy(rule => rule.Order).ToArray();

        for (var ruleIndex = 0; ruleIndex < rules.Length; ruleIndex++)
        {
            var rule = rules[ruleIndex];
            if (!rule.Enabled)
            {
                continue;
            }

            evaluated = true;
            var paths = new Dictionary<RuleConditionNode, int[]>();
            CollectPaths(rule.Condition, [], paths);
            var index = ruleIndex;
            var evaluation = RuleMatcher.EvaluateCondition(rule.Condition, condition =>
            {
                var check = EvaluateLeaf(condition, context, analysis, now);
                var path = paths[condition];
                var node = new RuleEvaluationNodeEvidence
                {
                    RuleId = rule.Id,
                    RuleIndex = index,
                    Kind = RuleEvidenceNodeKind.Check,
                    ConditionPath = path,
                    ConditionId = condition.ConditionId,
                    CheckId = CheckId,
                    Status = Status(check.Evaluation.Value),
                    ReasonCode = check.Evaluation.Value == RuleTruthValue.Unknown ? "Unavailable" : null,
                    ObservedAtUtc = now
                };
                if (AddNode(node) && check.Detail is { } detail && details.Count < MaximumCheckDetails)
                {
                    details.Add(detail with { RuleIndex = index, ConditionPath = path, CheckId = CheckId });
                }
                return check.Evaluation;
            });

            AddNode(new RuleEvaluationNodeEvidence
            {
                RuleId = rule.Id,
                RuleIndex = index,
                Kind = RuleEvidenceNodeKind.Rule,
                Status = Status(evaluation.Value),
                ReasonCode = evaluation.Value == RuleTruthValue.Unknown ? "Unavailable" : null,
                ObservedAtUtc = now
            });

            if (evaluation.Value == RuleTruthValue.Unknown)
            {
                unknown = true;
                continue;
            }
            if (evaluation.Value != RuleTruthValue.True)
            {
                continue;
            }

            matched = true;
            for (var actionIndex = 0; actionIndex < rule.Actions.Count; actionIndex++)
            {
                if (decisions.Count < RuleEvaluationEvidence.MaximumActionDecisionCount)
                {
                    decisions.Add(new RuleActionDecisionEvidence
                    {
                        RuleId = rule.Id,
                        RuleIndex = index,
                        ActionId = rule.Actions[actionIndex].Id,
                        ActionIndex = actionIndex,
                        Status = RuleActionDecisionStatus.NotRun,
                        ReasonCode = "EvaluationOnly",
                        ObservedAtUtc = now
                    });
                }
                else
                {
                    omittedDecisions++;
                }
            }
            if (rule.AfterMatch == RuleAfterMatch.StopProcessingRules)
            {
                break;
            }
        }

        return new CreateRuleChatEvaluationDto
        {
            AttemptId = Guid.NewGuid(),
            PlayerId = context.PlayerId,
            OccurrenceId = occurrenceId,
            FeatureId = snapshot.FeatureId,
            RuleSetRevision = snapshot.Revision,
            Mode = RuleExecutionMode.DryRun,
            EvaluatedAtUtc = now,
            MatchStatus = matched ? RuleExecutionMatchStatus.Matched :
                unknown || !evaluated ? RuleExecutionMatchStatus.Unknown : RuleExecutionMatchStatus.NoMatch,
            Evidence = new RuleEvaluationEvidence
            {
                Nodes = nodes.ToArray(),
                ActionDecisions = decisions.ToArray(),
                OmittedNodeCount = omittedNodes,
                OmittedActionDecisionCount = omittedDecisions
            },
            CheckDetails = details.ToArray()
        };

        bool AddNode(RuleEvaluationNodeEvidence node)
        {
            if (nodes.Count < RuleEvaluationEvidence.MaximumNodeCount)
            {
                nodes.Add(node);
                return true;
            }
            omittedNodes++;
            return false;
        }
    }

    private static void CollectPaths(RuleConditionNode? node, int[] path, Dictionary<RuleConditionNode, int[]> paths)
    {
        if (node is null)
        {
            return;
        }
        paths[node] = path;
        if (node.Conditions is not null)
        {
            for (var index = 0; index < node.Conditions.Count; index++)
            {
                CollectPaths(node.Conditions[index], [.. path, index], paths);
            }
        }
    }

    private static LeafCheck EvaluateLeaf(
        RuleConditionNode condition, ModerationContext context, ChatModerationResult? analysis, DateTimeOffset now)
    {
        var parameters = condition.Parameters ?? throw new InvalidOperationException("Rule condition parameters are missing.");
        switch (condition.ConditionId)
        {
            case ChatModerationRulesConstants.PlayerFirstSeen:
                var firstSeen = context.PlayerFirstSeen == default ? (DateTimeOffset?)null :
                    new DateTimeOffset(DateTime.SpecifyKind(context.PlayerFirstSeen, DateTimeKind.Utc));
                var occurredAt = context.EventGeneratedUtc == default ? (DateTimeOffset?)null :
                    new DateTimeOffset(DateTime.SpecifyKind(context.EventGeneratedUtc, DateTimeKind.Utc));
                if (firstSeen is null || occurredAt is null || firstSeen > occurredAt)
                {
                    return Unknown();
                }
                return Check(occurredAt.Value - firstSeen.Value <= TimeSpan.FromDays(parameters["withinDays"].GetInt32()));

            case ChatModerationRulesConstants.PlayerTagName:
                var expected = parameters["tagName"].GetString();
                if (context.PlayerTagNames.Any(tag => string.Equals(tag, expected, StringComparison.OrdinalIgnoreCase)))
                {
                    return Check(true);
                }
                return context.PlayerTagNames.Any(string.IsNullOrWhiteSpace) ? Unknown() : Check(false);

            case ChatModerationRulesConstants.MinimumLength:
                return Check(context.Message.Length >= parameters["minimum"].GetInt32());

            case ChatModerationRulesConstants.Audience:
                return Check(string.Equals(context.MessageType.ToString(), parameters["scope"].GetString(),
                    StringComparison.Ordinal));

            case ChatModerationRulesConstants.ContentSafety:
                var category = parameters["category"].GetString();
                var threshold = parameters["threshold"].GetInt32();
                var severity = category switch
                {
                    "Hate" => analysis?.HateSeverity,
                    "Violence" => analysis?.ViolenceSeverity,
                    "Sexual" => analysis?.SexualSeverity,
                    "SelfHarm" => analysis?.SelfHarmSeverity,
                    _ => null
                };
                if (!Enum.TryParse<RuleChatSafetyCategory>(category, out var safetyCategory) ||
                    safetyCategory == RuleChatSafetyCategory.Unrecognized)
                {
                    return Unknown();
                }
                var safetyDetail = new RuleChatCheckDetail
                {
                    Category = safetyCategory,
                    ObservedSeverity = severity is >= 0 and <= 7 ? severity : null,
                    Threshold = threshold
                };
                return severity is >= 0 and <= 7
                    ? Check(severity >= threshold, safetyDetail) : Unknown(safetyDetail);

            case ChatModerationRulesConstants.BannedWords:
            case ChatModerationRulesConstants.Hackusation:
                var policy = parameters["policy"].Deserialize<ChatLiteralPolicy>(RulesSerialization.Options)
                    ?? throw new InvalidOperationException("The literal policy is missing.");
                var match = new ChatLiteralMatcher(policy).Match(context.Message);
                var literalDetail = new RuleChatCheckDetail
                {
                    MatcherVersion = policy.MatcherVersion,
                    MatchedEntryId = match.MatchedEntryId
                };
                return new LeafCheck(new ConditionEvaluation(match.Value), literalDetail);

            default:
                return Unknown();
        }
    }

    private static RuleEvaluationNodeStatus Status(RuleTruthValue value) => value switch
    {
        RuleTruthValue.True => RuleEvaluationNodeStatus.Matched,
        RuleTruthValue.False => RuleEvaluationNodeStatus.DidNotMatch,
        _ => RuleEvaluationNodeStatus.Unknown
    };

    private static LeafCheck Check(bool value, RuleChatCheckDetail? detail = null) =>
        new(value ? ConditionEvaluation.True : ConditionEvaluation.False, detail);

    private static LeafCheck Unknown(RuleChatCheckDetail? detail = null) =>
        new(ConditionEvaluation.Unknown(), detail);

    private sealed record LeafCheck(ConditionEvaluation Evaluation, RuleChatCheckDetail? Detail);
}
