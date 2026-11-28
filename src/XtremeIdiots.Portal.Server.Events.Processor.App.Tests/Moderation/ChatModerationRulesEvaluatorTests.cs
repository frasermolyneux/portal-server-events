using System.Text.Json;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FeatureManagement;

using Moq;

using MX.Observability.ApplicationInsights.Auditing;

using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.AdminActions;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Moderation;
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Moderation;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.RulesEngine;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Shared;

using static XtremeIdiots.Portal.Server.Events.Processor.App.Tests.ServiceBusTestHelpers;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Moderation;

public sealed class ChatModerationRulesEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PlayerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OccurrenceId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void Evaluate_UsesConfiguredPolicyIdsAndProducesRedactedDryRunEvidence()
    {
        var condition = And(
            Leaf(ChatModerationRulesConstants.PlayerFirstSeen, ("withinDays", 7)),
            Leaf(ChatModerationRulesConstants.PlayerTagName, ("tagName", "ModerateChat")),
            Leaf(ChatModerationRulesConstants.MinimumLength, ("minimum", 5)),
            Leaf(ChatModerationRulesConstants.Audience, ("scope", "Team")),
            Leaf(ChatModerationRulesConstants.ContentSafety, ("category", "Hate"), ("threshold", 4)),
            Literal(ChatModerationRulesConstants.BannedWords, "configured-id", "secret phrase"));
        var context = Context("a secret phrase appears") with
        {
            MessageType = ChatMessageType.Team,
            PlayerTagNames = ["moderatechat"],
            PlayerFirstSeen = Now.AddDays(-1).UtcDateTime
        };
        var snapshot = Snapshot(Rule("configured-rule", condition));

        var result = ChatModerationRulesEvaluator.Evaluate(context,
            new ChatModerationResult(4, null, null, null, 4, "Hate"), snapshot, OccurrenceId, Now);

        Assert.Equal(RuleExecutionMode.DryRun, result.Mode);
        Assert.Equal(RuleExecutionMatchStatus.Matched, result.MatchStatus);
        Assert.Equal("configured-id", Assert.Single(result.CheckDetails, item => item.MatcherVersion is not null).MatchedEntryId);
        Assert.Equal(4, Assert.Single(result.CheckDetails, item => item.Category == RuleChatSafetyCategory.Hate).ObservedSeverity);
        Assert.Equal(RuleActionDecisionStatus.NotRun, Assert.Single(result.Evidence.ActionDecisions).Status);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(context.Message, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret phrase", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_MissingCategoryAtZeroThresholdIsUnknown()
    {
        var snapshot = Snapshot(Rule("safety",
            Leaf(ChatModerationRulesConstants.ContentSafety, ("category", "Hate"), ("threshold", 0))));

        var result = ChatModerationRulesEvaluator.Evaluate(Context(), new ChatModerationResult(null, 0, 0, 0, 0, "Hate"),
            snapshot, OccurrenceId, Now);

        Assert.Equal(RuleExecutionMatchStatus.Unknown, result.MatchStatus);
        Assert.Null(Assert.Single(result.CheckDetails).ObservedSeverity);
        Assert.Empty(result.Evidence.ActionDecisions);
    }

    [Fact]
    public void Evaluate_NoEnabledRulesIsUnknown()
    {
        var rule = Rule("disabled", Leaf(ChatModerationRulesConstants.MinimumLength, ("minimum", 5)));
        rule.Enabled = false;

        var result = ChatModerationRulesEvaluator.Evaluate(Context(), null, Snapshot(rule), OccurrenceId, Now);

        Assert.Equal(RuleExecutionMatchStatus.Unknown, result.MatchStatus);
        Assert.Empty(result.Evidence.Nodes);
    }

    [Theory]
    [InlineData("That player is a CHEATER", RuleExecutionMatchStatus.Matched)]
    [InlineData("That is an anti-cheater plugin", RuleExecutionMatchStatus.NoMatch)]
    public void Evaluate_HackusationPolicyRespectsConfiguredExceptions(string message, RuleExecutionMatchStatus expected)
    {
        var condition = Leaf(ChatModerationRulesConstants.Hackusation, ("policy", new ChatLiteralPolicy
        {
            MatcherVersion = ChatLiteralPolicyConstants.MatcherVersion,
            Entries = [new ChatLiteralEntry
            {
                Id = "hackusation-1",
                Phrase = "cheater",
                MatchMode = ChatLiteralPolicyConstants.WholeWord,
                Exceptions = ["anti-cheater"]
            }]
        }));

        var result = ChatModerationRulesEvaluator.Evaluate(Context(message), null,
            Snapshot(Rule("nudge", condition)), OccurrenceId, Now);

        Assert.Equal(expected, result.MatchStatus);
        Assert.Equal(expected == RuleExecutionMatchStatus.Matched ? "hackusation-1" : null,
            Assert.Single(result.CheckDetails).MatchedEntryId);
    }

    [Fact]
    public void Evaluate_MissingTagNameIsUnknownUnlessKnownTagMatches()
    {
        var snapshot = Snapshot(Rule("tag",
            Leaf(ChatModerationRulesConstants.PlayerTagName, ("tagName", "ModerateChat"))));
        var context = Context() with { PlayerTagNames = [null, "other"] };

        var unknown = ChatModerationRulesEvaluator.Evaluate(context, null, snapshot, OccurrenceId, Now);
        var matched = ChatModerationRulesEvaluator.Evaluate(context with { PlayerTagNames = [null, "moderatechat"] },
            null, snapshot, OccurrenceId, Now);

        Assert.Equal(RuleExecutionMatchStatus.Unknown, unknown.MatchStatus);
        Assert.Equal(RuleExecutionMatchStatus.Matched, matched.MatchStatus);
    }

    [Fact]
    public void Evaluate_BoundsEvidenceAndCheckDetails()
    {
        var rules = Enumerable.Range(0, 100).Select(index =>
        {
            var rule = Rule($"r{index}", And(Enumerable.Range(0, 4)
                .Select(_ => Leaf(ChatModerationRulesConstants.ContentSafety, ("category", "Hate"), ("threshold", 0)))
                .ToArray()));
            rule.AfterMatch = RuleAfterMatch.ContinueToNextRule;
            rule.Actions = Enumerable.Range(0, 3).Select(action => new RuleActionDefinition { Id = $"a{action}" }).ToList();
            return rule;
        }).ToArray();

        var result = ChatModerationRulesEvaluator.Evaluate(Context(), new ChatModerationResult(0, 0, 0, 0, 0, "Hate"),
            Snapshot(rules), OccurrenceId, Now);

        Assert.Equal(256, result.Evidence.Nodes.Length);
        Assert.True(result.Evidence.OmittedNodeCount > 0);
        Assert.Equal(256, result.Evidence.ActionDecisions.Length);
        Assert.Equal(44, result.Evidence.OmittedActionDecisionCount);
        Assert.Equal(64, result.CheckDetails.Length);
        Assert.All(result.CheckDetails, detail => Assert.Contains(result.Evidence.Nodes, node =>
            node.Kind == RuleEvidenceNodeKind.Check && node.RuleIndex == detail.RuleIndex &&
            node.CheckId == detail.CheckId && node.ConditionPath!.SequenceEqual(detail.ConditionPath!)));
    }

    [Fact]
    public async Task EvaluateAsync_EnabledRulesUseReleasedSettingsAndPersistEvaluation()
    {
        var document = new ChatModerationRulesDocument
        {
            Enabled = true,
            Rules = [Rule("words", Literal(ChatModerationRulesConstants.BannedWords, "policy-1", "phrase"))]
        };
        var fixture = new Fixture(JsonSerializer.Serialize(document, RulesSerialization.Options));
        CreateRuleChatEvaluationDto? stored = null;
        fixture.Executions.Setup(api => api.CreateChatEvaluation(ServerId, It.IsAny<CreateRuleChatEvaluationDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, CreateRuleChatEvaluationDto, CancellationToken>((_, evaluation, _) => stored = evaluation)
            .ReturnsAsync(() => SuccessResult(new RuleChatEvaluationDto { Evaluation = stored! }));

        await fixture.CreateEvaluator().EvaluateAsync(Context("a phrase") with { OccurrenceId = OccurrenceId }, null, default);

        Assert.NotNull(stored);
        Assert.Equal(OccurrenceId, stored.OccurrenceId);
        Assert.Equal(PlayerId, stored.PlayerId);
        Assert.Equal(RuleExecutionMatchStatus.Matched, stored.MatchStatus);
        fixture.Executions.Verify(api => api.CreateChatEvaluation(ServerId, It.IsAny<CreateRuleChatEvaluationDto>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Pipeline_SharesOneEligibleAnalysisWithObservationAndRules()
    {
        var document = new ChatModerationRulesDocument
        {
            Enabled = true,
            Rules = [Rule("safety",
                Leaf(ChatModerationRulesConstants.ContentSafety, ("category", "Hate"), ("threshold", 4)))]
        };
        var fixture = new Fixture(JsonSerializer.Serialize(document, RulesSerialization.Options));
        CreateRuleChatEvaluationDto? stored = null;
        fixture.Executions.Setup(api => api.CreateChatEvaluation(ServerId, It.IsAny<CreateRuleChatEvaluationDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, CreateRuleChatEvaluationDto, CancellationToken>((_, evaluation, _) => stored = evaluation)
            .ReturnsAsync(() => SuccessResult(new RuleChatEvaluationDto { Evaluation = stored! }));
        var contentSafety = new Mock<IChatModerationService>();
        contentSafety.Setup(service => service.AnalyseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatModerationResult(4, 0, 0, 0, 4, "Hate"));
        var moderationSettings = new Mock<IChatModerationSettingsProvider>();
        moderationSettings.Setup(provider => provider.GetForServerAsync(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatModerationSettings(1, 4, 4, 4, 4));
        var featureManager = new Mock<IFeatureManager>();
        featureManager.Setup(manager => manager.IsEnabledAsync("EventIngest.ChatToxicityDetection")).ReturnsAsync(true);
        var adminActions = new Mock<IAdminActionsApi>();
        var adminVersions = new Mock<IVersionedAdminActionsApi>();
        adminVersions.Setup(item => item.V1).Returns(adminActions.Object);
        fixture.Repository.Setup(item => item.AdminActions).Returns(adminVersions.Object);
        adminActions.Setup(api => api.CreateAdminAction(It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());
        var pipeline = new ChatModerationPipeline(contentSafety.Object, moderationSettings.Object, fixture.Repository.Object,
            new ConfigurationBuilder().AddInMemoryCollection().Build(), featureManager.Object, Mock.Of<IAuditLogger>(),
            fixture.CreateEvaluator(), NullLogger<ChatModerationPipeline>.Instance);

        await pipeline.RunAsync(Context("suspicious text") with
        {
            OccurrenceId = OccurrenceId,
            PlayerFirstSeen = DateTime.UtcNow.AddDays(-1)
        });

        contentSafety.Verify(service => service.AnalyseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        adminActions.Verify(api => api.CreateAdminAction(It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(stored);
        Assert.Equal(RuleExecutionMatchStatus.Matched, stored.MatchStatus);
        Assert.Equal(4, Assert.Single(stored.CheckDetails).ObservedSeverity);
    }

    [Fact]
    public async Task EvaluateAsync_ServerOverrideCanEnableRules()
    {
        var server = new ChatModerationRulesServerDocument
        {
            EnabledOverride = FeatureEnablementOverride.Enabled,
            Rules = [Rule("length", Leaf(ChatModerationRulesConstants.MinimumLength, ("minimum", 3)))]
        };
        var fixture = new Fixture(null, JsonSerializer.Serialize(server, RulesSerialization.Options));
        CreateRuleChatEvaluationDto? stored = null;
        fixture.Executions.Setup(api => api.CreateChatEvaluation(ServerId, It.IsAny<CreateRuleChatEvaluationDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, CreateRuleChatEvaluationDto, CancellationToken>((_, evaluation, _) => stored = evaluation)
            .ReturnsAsync(() => SuccessResult(new RuleChatEvaluationDto { Evaluation = stored! }));

        await fixture.CreateEvaluator().EvaluateAsync(Context() with { OccurrenceId = OccurrenceId }, null, default);

        Assert.NotNull(stored);
        Assert.Equal(RuleExecutionMatchStatus.Matched, stored.MatchStatus);
    }

    [Fact]
    public async Task EvaluateAsync_DisabledOrLegacyEventsDoNotWriteAudit()
    {
        var fixture = new Fixture(null);
        var evaluator = fixture.CreateEvaluator();

        await evaluator.EvaluateAsync(Context() with { OccurrenceId = OccurrenceId }, null, default);
        await evaluator.EvaluateAsync(Context(), null, default);

        fixture.Executions.Verify(api => api.CreateChatEvaluation(It.IsAny<Guid>(),
            It.IsAny<CreateRuleChatEvaluationDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EvaluateAsync_RejectsInvalidChatOverride()
    {
        var fixture = new Fixture(null, /*lang=json,strict*/ """{"featureId":"chat-moderation","enabledOverride":"Enabled","rules":[{"id":"invalid","condition":{"conditionId":"chat.content-safety","parameters":{"category":"Hate","threshold":7}},"actions":[]}]}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.CreateEvaluator().EvaluateAsync(Context() with { OccurrenceId = OccurrenceId }, null, default));
        fixture.Executions.Verify(api => api.CreateChatEvaluation(It.IsAny<Guid>(),
            It.IsAny<CreateRuleChatEvaluationDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static ModerationContext Context(string message = "hello") => new()
    {
        ServerId = ServerId,
        GameType = "CallOfDuty4",
        PlayerGuid = "guid",
        Username = "player",
        Message = message,
        MessageType = ChatMessageType.All,
        SlotId = 1,
        EventGeneratedUtc = Now.UtcDateTime,
        PlayerId = PlayerId,
        PlayerFirstSeen = Now.AddDays(-1).UtcDateTime,
        PlayerTagNames = [],
        HasModerateChatTag = false
    };

    private static FeatureRulesSnapshot Snapshot(params RuleDefinition[] rules) =>
        new(ChatModerationRulesConstants.FeatureId, rules, RuleExecutionMode.DryRun);

    private static RuleDefinition Rule(string id, RuleConditionNode condition) => new()
    {
        Id = id,
        Condition = condition,
        Actions = [new RuleActionDefinition
        {
            Id = "observation",
            TypeId = "moderation.observation",
            Parameters = new() { ["reason"] = JsonSerializer.SerializeToElement("Configured reason") }
        }]
    };

    private static RuleConditionNode And(params RuleConditionNode[] children) =>
        new() { GroupOperator = RuleConditionGroupOperator.And, Conditions = [.. children] };

    private static RuleConditionNode Leaf(string id, params (string Name, object Value)[] parameters) => new()
    {
        ConditionId = id,
        Parameters = parameters.ToDictionary(item => item.Name, item => JsonSerializer.SerializeToElement(item.Value))
    };

    private static RuleConditionNode Literal(string id, string entryId, string phrase) =>
        Leaf(id, ("policy", new ChatLiteralPolicy
        {
            MatcherVersion = ChatLiteralPolicyConstants.MatcherVersion,
            Entries = [new ChatLiteralEntry
            {
                Id = entryId,
                Phrase = phrase,
                MatchMode = ChatLiteralPolicyConstants.Phrase
            }]
        }));

    private sealed class Fixture
    {
        public Mock<IRepositoryApiClient> Repository { get; } = new();
        private readonly MemoryCache cache = new(new MemoryCacheOptions());
        public Mock<IRuleExecutionsApi> Executions { get; } = new();

        public Fixture(string? globalJson, string? serverJson = null)
        {
            var global = new Mock<IGlobalConfigurationsApi>();
            var globalVersions = new Mock<IVersionedGlobalConfigurationsApi>();
            globalVersions.Setup(item => item.V1).Returns(global.Object);
            Repository.Setup(item => item.GlobalConfigurations).Returns(globalVersions.Object);
            global.Setup(item => item.GetConfiguration(ChatModerationRulesConstants.Namespace, It.IsAny<CancellationToken>()))
                .ReturnsAsync(globalJson is null ? NotFoundResult<ConfigurationDto>() : SuccessResult(Configuration(globalJson)));

            var server = new Mock<IGameServerConfigurationsApi>();
            var serverVersions = new Mock<IVersionedGameServerConfigurationsApi>();
            serverVersions.Setup(item => item.V1).Returns(server.Object);
            Repository.Setup(item => item.GameServerConfigurations).Returns(serverVersions.Object);
            server.Setup(item => item.GetConfiguration(ServerId, ChatModerationRulesConstants.Namespace, It.IsAny<CancellationToken>()))
                .ReturnsAsync(serverJson is null ? NotFoundResult<ConfigurationDto>() : SuccessResult(Configuration(serverJson)));

            var executionVersions = new Mock<IVersionedRuleExecutionsApi>();
            executionVersions.Setup(item => item.V1).Returns(Executions.Object);
            Repository.Setup(item => item.RuleExecutions).Returns(executionVersions.Object);
        }

        public ChatModerationRulesEvaluator CreateEvaluator() => new(
            new ConfiguredFeatureRulesProvider(Repository.Object, cache, NullLogger<ConfiguredFeatureRulesProvider>.Instance),
            Repository.Object, NullLogger<ChatModerationRulesEvaluator>.Instance);

        private static ConfigurationDto Configuration(string json) =>
            Newtonsoft.Json.JsonConvert.DeserializeObject<ConfigurationDto>(
                Newtonsoft.Json.JsonConvert.SerializeObject(new { Namespace = ChatModerationRulesConstants.Namespace, Configuration = json }))!;
    }
}
