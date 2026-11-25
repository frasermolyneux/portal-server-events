using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.RulesEngine;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Rules;

public sealed class RuleMessageDeliveryServiceTests
{
    [Theory]
    [InlineData("message.public", RuleMessageChannels.RconPublicChat)]
    [InlineData("message.private-tagged", RuleMessageChannels.RconPrivateChat)]
    public async Task BudgetProviderRequestsCorrectRconChannelAndPreservesConstraints(string actionType, string channel)
    {
        var servers = new Mock<IServersApiClient>();
        var serverId = Guid.NewGuid();
        var constraints = new RconMessageCharacterConstraintsDto
        {
            AsciiOnly = true,
            ControlsDisallowed = true,
            DisallowedCharacters = ";\"\\",
            DisallowLocalizationInserts = true
        };
        var budget = new MessageChannelBudget(channel, 149, ReservedPrefixUtf8Bytes: 32, RconSafePrintableAscii: true);
        servers.Setup(item => item.RulesNativeAction.V1.GetMessageChannelBudget(serverId, channel, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RuleMessageChannelBudgetDto>(HttpStatusCode.OK, new ApiResponse<RuleMessageChannelBudgetDto>(new RuleMessageChannelBudgetDto
            {
                Status = RuleMessageBudgetStatus.Available,
                Budget = budget,
                Constraints = constraints
            })));
        var invocation = new RuleInvocation(new() { GameServerId = serverId, Runtime = new() { Id = "server-agent" } }, new(), null);

        var result = await new RuleMessageBudgetProvider(servers.Object).GetAsync(invocation, actionType, default);

        Assert.Equal(budget, result.Budget);
        Assert.Equal(constraints, result.Constraints);
    }

    [Fact]
    public async Task PluginBudgetRequiresAdvertisedFragmentSupport()
    {
        var servers = new Mock<IServersApiClient>(MockBehavior.Strict);
        var invocation = new RuleInvocation(new()
        {
            Runtime = new() { Id = "cod4x-plugin", Capabilities = ["cod4x.chat.fragments"] }
        }, new(), null);
        var provider = new RuleMessageBudgetProvider(servers.Object);

        var available = await provider.GetAsync(invocation, "message.public", default);
        var unsupported = await provider.GetAsync(invocation with
        {
            Trigger = invocation.Trigger with { Runtime = invocation.Trigger.Runtime! with { Capabilities = [] } }
        }, "message.public", default);

        Assert.Equal(149, available.Budget!.MaxPayloadBytes);
        Assert.Null(unsupported.Budget);
        servers.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnsafeLocalizationInLaterItemIsRejectedBeforeAnyFragmentDispatch()
    {
        var fixture = new Fixture();
        fixture.Budget = new(new("test", 4), Constraints: new() { DisallowLocalizationInserts = true });
        fixture.Action.Parameters["message"] = JsonSerializer.SerializeToElement("safe");
        fixture.Action.Parameters["messages"] = JsonSerializer.SerializeToElement(new[] { "ab&&1" });

        var result = await fixture.DispatchAsync();

        Assert.Equal(ActionExecutionOutcome.SkippedUnsupported, result.Outcome!.Outcome);
        Assert.Equal(RuleExecutionDiagnosticCode.UnsupportedDelivery, result.DiagnosticCode);
        Assert.Empty(fixture.Sent);
        Assert.Empty(fixture.Execution.PreparedActions);
    }

    [Theory]
    [InlineData("é", true, "")]
    [InlineData(";", false, ";")]
    [InlineData("\"", false, "\"")]
    [InlineData("\\", false, "\\")]
    public async Task TransportConstraintsRejectAnyUnsafeItemBeforeDispatch(string unsafeText, bool asciiOnly, string forbidden)
    {
        var fixture = new Fixture();
        fixture.Budget = new(new("test", 8), Constraints: new()
        {
            AsciiOnly = asciiOnly,
            ControlsDisallowed = true,
            DisallowedCharacters = forbidden
        });
        fixture.Action.Parameters["message"] = JsonSerializer.SerializeToElement("safe");
        fixture.Action.Parameters["messages"] = JsonSerializer.SerializeToElement(new[] { unsafeText });

        var result = await fixture.DispatchAsync();

        Assert.Equal(ActionExecutionOutcome.SkippedUnsupported, result.Outcome!.Outcome);
        Assert.Empty(fixture.Sent);
        Assert.Empty(fixture.Execution.PreparedActions);
    }

    [Fact]
    public async Task UnsafeExpandedTextIsTerminalRatherThanRetried()
    {
        var fixture = new Fixture();
        fixture.Budget = new(new("rcon", 8, RconSafePrintableAscii: true));
        fixture.Action.Parameters["message"] = JsonSerializer.SerializeToElement("bad;");

        var result = await fixture.DispatchAsync();

        Assert.Equal(ActionExecutionOutcome.SkippedUnsupported, result.Outcome!.Outcome);
        Assert.Empty(fixture.Sent);
        Assert.Empty(fixture.Execution.PreparedActions);
    }

    [Fact]
    public async Task InvalidLaterFragmentCannotPartiallyDispatchTheBatch()
    {
        var fixture = new Fixture();
        fixture.Action.Parameters["message"] = JsonSerializer.SerializeToElement("good");
        fixture.Action.Parameters["messages"] = JsonSerializer.SerializeToElement(new[] { "bad;" });
        fixture.Executor.Setup(item => item.AvailabilityAsync(It.IsAny<RuleActionDefinition>(),
                It.Is<RuleActionCommand>(command => command.Effect.Message == "bad;"),
                It.IsAny<RuleInvocation>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionAvailability(Unavailable: new(ActionExecutionOutcome.SkippedUnsupported, "Unsafe text.")));

        var result = await fixture.DispatchAsync();

        Assert.Equal(ActionExecutionOutcome.SkippedUnsupported, result.Outcome!.Outcome);
        Assert.Empty(fixture.Sent);
        Assert.Empty(fixture.Execution.PreparedActions);
        Assert.Empty(fixture.Execution.Actions);
    }

    [Fact]
    public async Task FreezesAllItemsBeforeDispatchAndResumesWithoutReplayingEarlierFragments()
    {
        var fixture = new Fixture();
        fixture.Action.Parameters["message"] = JsonSerializer.SerializeToElement("abcdefgh");
        fixture.Action.Parameters["messages"] = JsonSerializer.SerializeToElement(new[] { "tail" });

        var first = await fixture.DispatchAsync();
        Assert.NotNull(first.ResumeAtUtc);
        fixture.Action.Parameters["message"] = JsonSerializer.SerializeToElement("changed");
        await fixture.FinishAsync();
        await fixture.DispatchAsync();

        Assert.Equal(["abcd", "efgh", "tail"], fixture.Sent.Select(item => item.Effect.Message));
        Assert.Equal(1, fixture.Preparations);
        Assert.Equal(3, fixture.Execution.Actions.Select(item => item.LogicalEffectId).Distinct().Count());
        Assert.All(fixture.Sent, command => Assert.Equal(command.DeliveryIdentity!.ToLogicalEffectId(), command.LogicalEffectId));
        Assert.Equal(["abcd", "efgh", "tail"],
            Assert.Single(fixture.Execution.PreparedActions).Commands.Select(item => item.Effect.Message));
    }

    [Fact]
    public async Task PersistsDeduplicatedRendererDiagnosticsBeforeFreezingMessagePlan()
    {
        var fixture = new Fixture
        {
            TextDiagnostics =
            [
                new("SourceUnavailable", null, RuleTextTokenSource.Connection, "Connection unavailable."),
                new("FallbackUsed", "player.name", RuleTextTokenSource.Connection, "Fallback used."),
                new("FallbackUsed", "player.name", RuleTextTokenSource.Connection, "Fallback used.")
            ]
        };

        await fixture.DispatchAsync();

        var diagnostics = Assert.IsType<RuleExecutionDiagnostic[]>(fixture.DiagnosticsAtPreparation);
        Assert.Equal([RuleExecutionDiagnosticCode.SourceUnavailable, RuleExecutionDiagnosticCode.FallbackUsed],
            diagnostics.Select(item => item.Code));
        Assert.Equal("player.name", diagnostics[1].TokenKey);
        Assert.Equal("Connection", diagnostics[1].Source);
        Assert.Single(fixture.Execution.PreparedActions);
    }

    [Theory]
    [InlineData("Any", RuleRecipientTagMatch.Any)]
    [InlineData("All", RuleRecipientTagMatch.All)]
    public async Task EmptyAuthoritativeAudienceIsSuccessfulWithoutRetargetingToJoiner(string match, RuleRecipientTagMatch expected)
    {
        var fixture = new Fixture(tagged: true);
        fixture.Action.Parameters["recipientTagMatch"] = JsonSerializer.SerializeToElement(match);

        var result = await fixture.DispatchAsync();

        Assert.Equal(ActionExecutionOutcome.Applied, result.Outcome!.Outcome);
        Assert.Equal(RuleExecutionDiagnosticCode.AudienceEmpty, result.DiagnosticCode);
        Assert.Equal(expected, fixture.AudienceRequest!.Match);
        Assert.Empty(fixture.Sent);
        Assert.Empty(Assert.Single(fixture.Execution.PreparedActions).Commands);
    }

    [Fact]
    public async Task PartialAudienceWaitsAndNeverBecomesSuccessfulNobodyOnline()
    {
        var fixture = new Fixture(tagged: true);
        fixture.Audience = fixture.Audience with { Partial = true, Detail = "A tag lookup failed." };

        var result = await fixture.DispatchAsync();

        Assert.NotNull(result.ResumeAtUtc);
        Assert.Equal(RuleExecutionDiagnosticCode.AudienceUnavailable, result.DiagnosticCode);
        Assert.Null(result.Outcome);
        Assert.Empty(fixture.Execution.PreparedActions);
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(120);
        result = await fixture.DispatchAsync();
        Assert.Equal(ActionExecutionOutcome.Stale, result.Outcome!.Outcome);
        Assert.Equal(RuleExecutionDiagnosticCode.Expired, result.DiagnosticCode);
        Assert.Empty(fixture.Sent);
    }

    [Fact]
    public async Task UnavailableBudgetCannotExtendPersistedPreparationDeadline()
    {
        var fixture = new Fixture();
        var deadline = fixture.Clock.Now.AddSeconds(30);
        fixture.Progress.ExpiresAtUtc = null;
        fixture.Progress.MessagePreparationExpiries[JsonSerializer.Serialize(new[] { "rule", "action" })] = deadline;
        fixture.Budget = new(null, "Unavailable.", Unavailable: true);

        var waiting = await fixture.DispatchAsync();
        fixture.Clock.Now = deadline;
        fixture.Progress.ExpiresAtUtc = deadline.AddMinutes(2);
        fixture.Budget = new(new("test", 4));
        var expired = await fixture.DispatchAsync();

        Assert.NotNull(waiting.ResumeAtUtc);
        Assert.Equal(ActionExecutionOutcome.Stale, expired.Outcome!.Outcome);
        Assert.Empty(fixture.Execution.PreparedActions);
        Assert.Empty(fixture.Sent);
    }

    [Fact]
    public async Task FanoutDeduplicatesConnectionsAndSkipsReplacedRecipientWithoutResendingOthers()
    {
        var fixture = new Fixture(tagged: true);
        var replaced = fixture.Recipient();
        var current = fixture.Recipient();
        fixture.Audience = fixture.Audience with { Recipients = [replaced, current, current] };
        fixture.Action.Parameters["message"] = JsonSerializer.SerializeToElement("abcdefgh");
        fixture.Executor.Setup(item => item.AvailabilityAsync(It.IsAny<RuleActionDefinition>(),
                It.Is<RuleActionCommand>(command => command.RecipientTarget!.ConnectionId == replaced.ConnectionId),
                It.IsAny<RuleInvocation>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionAvailability(Unavailable: new(ActionExecutionOutcome.Stale, "Recipient replaced."),
                ConnectionUnavailable: true));

        await fixture.FinishAsync();

        Assert.Equal(["abcd", "efgh"], fixture.Sent.Select(item => item.Effect.Message));
        Assert.All(fixture.Sent, command =>
        {
            Assert.Equal(current.ConnectionId, command.RecipientTarget!.ConnectionId);
            Assert.Equal(fixture.Execution.ConnectionId, command.ConnectionId);
            Assert.Equal(fixture.Execution.ConnectionId, command.Correlation.ConnectionId);
        });
        Assert.Equal(4, Assert.Single(fixture.Execution.PreparedActions).Commands.Length);
        Assert.Equal(4, fixture.Execution.Actions.Length);
        Assert.Equal(RuleExecutionDiagnosticCode.ConnectionUnavailable, Assert.Single(fixture.Execution.Diagnostics).Code);
    }

    [Fact]
    public async Task AmbiguousNativeStaleOutcomeIsNotReportedAsConnectionLoss()
    {
        var fixture = new Fixture();
        fixture.Executor.Setup(item => item.AvailabilityAsync(It.IsAny<RuleActionDefinition>(), It.IsAny<RuleActionCommand>(),
                It.IsAny<RuleInvocation>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionAvailability(Unavailable: new(ActionExecutionOutcome.Stale, "Native state unavailable.")));

        var result = await fixture.DispatchAsync();

        Assert.Equal(ActionExecutionOutcome.Failed, result.Outcome!.Outcome);
        Assert.Empty(fixture.Execution.Diagnostics);
    }

    [Fact]
    public async Task DurableProtectionSuppressesGreetingButNotIndependentTaggedAlert()
    {
        var fixture = new Fixture();
        fixture.Execution = fixture.Execution with { FeatureId = WelcomeFeatureRulesProvider.Id };
        fixture.Protections =
        [
            new()
            {
                FeatureId = RulesEngineCatalogueV1.VpnProtectionFeature,
                State = RuleFeatureExecutionState.AwaitingActionOutcome,
                Actions = [new()
                {
                    Mode = RuleExecutionMode.Enforce,
                    Outcome = RuleActionExecutionOutcome.OutcomeUnknown,
                    Command = new() { Effect = new() { Type = RuleNativeEffectType.DropPlayer } }
                }]
            }
        ];

        var greeting = await fixture.DispatchAsync();
        Assert.Equal(ActionExecutionOutcome.SkippedDependency, greeting.Outcome!.Outcome);
        Assert.Equal(RuleExecutionDiagnosticCode.SuppressedByProtection, greeting.DiagnosticCode);
        Assert.Empty(fixture.Execution.PreparedActions);

        fixture.ConfigureTagged();
        fixture.Audience = fixture.Audience with { Recipients = [fixture.Recipient()] };
        var alert = await fixture.DispatchAsync();
        Assert.NotEqual(ActionExecutionOutcome.SkippedDependency, alert.Outcome?.Outcome);
        Assert.Single(fixture.Sent);
    }

    [Fact]
    public async Task PacingUsesReceiptPersistenceTimeRatherThanOldClaimTime()
    {
        var fixture = new Fixture();
        fixture.Action.Parameters["message"] = JsonSerializer.SerializeToElement("abcdefgh");
        await fixture.DispatchAsync();
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(5);
        fixture.Execution = fixture.Execution with
        {
            Actions = [fixture.Execution.Actions[0] with { UpdatedAtUtc = fixture.Clock.Now }]
        };

        var waiting = await fixture.DispatchAsync();

        Assert.Equal(fixture.Clock.Now.AddSeconds(1), waiting.ResumeAtUtc);
        Assert.Single(fixture.Sent);
        fixture.Clock.Now = waiting.ResumeAtUtc!.Value;
        var result = await fixture.DispatchAsync();
        Assert.Equal(2, fixture.Sent.Count);
        Assert.Equal(ActionExecutionOutcome.OutcomeUnknown, result.Outcome!.Outcome);
    }

    [Fact]
    public async Task ExpiryAfterFirstFragmentTerminalizesUnsentChildrenWithoutDispatch()
    {
        var fixture = new Fixture();
        fixture.Action.Parameters["message"] = JsonSerializer.SerializeToElement("abcdefgh");

        await fixture.DispatchAsync();
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(120);
        var expired = await fixture.DispatchAsync();

        Assert.Equal(ActionExecutionOutcome.Failed, expired.Outcome!.Outcome);
        Assert.Single(fixture.Sent);
        Assert.Equal([RuleActionExecutionOutcome.OutcomeUnknown, RuleActionExecutionOutcome.Stale],
            fixture.Execution.Actions.Select(item => item.Outcome));
        Assert.Equal(RuleExecutionDiagnosticCode.Expired, Assert.Single(fixture.Execution.Diagnostics).Code);
    }

    [Fact]
    public async Task ProtectionAfterPreparationTerminalizesRemainingGreetingFragments()
    {
        var fixture = new Fixture();
        fixture.Execution = fixture.Execution with { FeatureId = WelcomeFeatureRulesProvider.Id };
        fixture.Action.Parameters["message"] = JsonSerializer.SerializeToElement("abcdefgh");
        await fixture.DispatchAsync();
        fixture.Protections =
        [
            new()
            {
                FeatureId = RulesEngineCatalogueV1.VpnProtectionFeature,
                State = RuleFeatureExecutionState.Completed,
                Actions = [new()
                {
                    Mode = RuleExecutionMode.Enforce,
                    Outcome = RuleActionExecutionOutcome.Applied,
                    Command = new() { Effect = new() { Type = RuleNativeEffectType.DropPlayer } }
                }]
            }
        ];
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1);

        var suppressed = await fixture.DispatchAsync();

        Assert.NotNull(suppressed.Outcome);
        Assert.Single(fixture.Sent);
        Assert.Equal(RuleActionExecutionOutcome.SkippedDependency, fixture.Execution.Actions[1].Outcome);
        Assert.Equal(RuleExecutionDiagnosticCode.SuppressedByProtection, suppressed.DiagnosticCode);
    }

    [Fact]
    public async Task LaterProtectionDoesNotRelabelAnAlreadyCompletedGreetingAsSuppressed()
    {
        var fixture = new Fixture();
        fixture.Execution = fixture.Execution with { FeatureId = WelcomeFeatureRulesProvider.Id };
        await fixture.DispatchAsync();
        fixture.Protections =
        [
            new()
            {
                FeatureId = RulesEngineCatalogueV1.VpnProtectionFeature,
                State = RuleFeatureExecutionState.Completed,
                Actions = [new()
                {
                    Mode = RuleExecutionMode.Enforce,
                    Outcome = RuleActionExecutionOutcome.Applied,
                    Command = new() { Effect = new() { Type = RuleNativeEffectType.DropPlayer } }
                }]
            }
        ];

        var result = await fixture.DispatchAsync();

        Assert.Equal(ActionExecutionOutcome.OutcomeUnknown, result.Outcome!.Outcome);
        Assert.Null(result.DiagnosticCode);
        Assert.Single(fixture.Sent);
    }

    [Fact]
    public async Task FailedDispatchPersistenceDoesNotExposeOrResendNativeFragment()
    {
        var fixture = new Fixture();
        fixture.Executor.Setup(item => item.ExecuteAsync(It.IsAny<RuleActionDefinition>(),
                It.IsAny<RuleActionCommand>(), It.IsAny<RuleInvocation>(), It.IsAny<CancellationToken>()))
            .Returns((RuleActionDefinition _, RuleActionCommand command, RuleInvocation _, CancellationToken _) =>
            {
                fixture.Sent.Add(command);
                return Task.FromResult(new ActionExecutionResult(ActionExecutionOutcome.Dispatched, NativeCommand: command));
            });
        fixture.FailOutcomes = true;

        await Assert.ThrowsAsync<InvalidOperationException>(fixture.DispatchAsync);
        fixture.FailOutcomes = false;
        var retry = await fixture.DispatchAsync();

        Assert.Single(fixture.Sent);
        Assert.True(retry.AwaitingOutcome);
        Assert.Empty(retry.Commands);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture
    {
        public Clock Clock { get; } = new(DateTimeOffset.UtcNow);
        public Mock<IUniversalRuleActionExecutor> Executor { get; } = new();
        public List<RuleActionCommand> Sent { get; } = [];
        public RuleFeatureExecutionDto Execution { get; set; }
        public RuleTaggedAudienceDto Audience { get; set; }
        public RuleFeatureExecutionDto[] Protections { get; set; } = [];
        public RuleTaggedAudienceRequestDto? AudienceRequest { get; private set; }
        public int Preparations { get; private set; }
        public bool FailOutcomes { get; set; }
        public RuleTextDiagnostic[] TextDiagnostics { get; init; } = [];
        public RuleExecutionDiagnostic[]? DiagnosticsAtPreparation { get; private set; }
        public RuleExecutionProgress Progress => progress;
        public RuleMessageBudgetResult Budget { get; set; } = new(new("test", 4));
        public RuleActionDefinition Action { get; } = new()
        {
            Id = "action",
            TypeId = "message.private",
            Parameters = new() { ["message"] = JsonSerializer.SerializeToElement("text") }
        };
        private readonly RuleMessageDeliveryService service;
        private readonly RuleInvocation invocation;
        private readonly RuleExecutionProgress progress;

        public Fixture(bool tagged = false)
        {
            Execution = new()
            {
                GameServerId = Guid.NewGuid(),
                ExecutionId = Guid.NewGuid(),
                ConnectionId = Guid.NewGuid(),
                FeatureId = RulesEngineCatalogueV1.VpnProtectionFeature,
                Trigger = new() { OccurredAtUtc = Clock.Now },
                FrozenRulesJson = "{}"
            };
            invocation = new(new(), new(), null);
            progress = new() { Mode = RuleExecutionMode.Enforce, ExpiresAtUtc = Clock.Now.AddSeconds(120) };
            Audience = new()
            {
                GameServerId = Execution.GameServerId,
                SnapshotId = Guid.NewGuid(),
                ObservedAtUtc = Clock.Now,
                Available = true
            };
            if (tagged)
            {
                ConfigureTagged();
            }
            var api = new Mock<IRuleExecutionsApi>();
            var repository = new Mock<IRepositoryApiClient>();
            var versions = new Mock<IVersionedRuleExecutionsApi>();
            versions.Setup(item => item.V1).Returns(api.Object);
            repository.Setup(item => item.RuleExecutions).Returns(versions.Object);
            api.Setup(item => item.GetExecution(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(Ok(Execution)));
            api.Setup(item => item.GetConnectionExecutions(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(Ok(Protections)));
            api.Setup(item => item.GetTaggedAudience(It.IsAny<Guid>(), It.IsAny<RuleTaggedAudienceRequestDto>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, RuleTaggedAudienceRequestDto request, CancellationToken _) =>
                {
                    AudienceRequest = request;
                    return Task.FromResult(Ok(Audience));
                });
            api.Setup(item => item.UpdateProgress(It.IsAny<Guid>(), It.IsAny<Guid>(),
                    It.IsAny<UpdateRuleFeatureExecutionProgressDto>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, Guid _, UpdateRuleFeatureExecutionProgressDto request, CancellationToken _) =>
                {
                    Assert.Equal(Execution.Version, request.ExpectedVersion);
                    Execution = Execution with
                    {
                        Version = Execution.Version + 1,
                        RuleIndex = request.RuleIndex,
                        ActionIndex = request.ActionIndex,
                        State = request.State,
                        DecisionSnapshotJson = request.DecisionSnapshotJson,
                        Coordination = request.Coordination,
                        Diagnostics = request.Diagnostics ?? Execution.Diagnostics
                    };
                    return Task.FromResult(Ok(Execution));
                });
            api.Setup(item => item.PrepareAction(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<PrepareRuleActionDto>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, Guid _, PrepareRuleActionDto request, CancellationToken _) =>
                {
                    Assert.Empty(Sent);
                    Assert.Equal(Execution.Version, request.ExpectedVersion);
                    DiagnosticsAtPreparation = Execution.Diagnostics;
                    Preparations++;
                    var plan = new RulePreparedActionDto
                    {
                        ExecutionId = Execution.ExecutionId,
                        RuleIndex = request.RuleIndex,
                        ActionIndex = request.ActionIndex,
                        ActionId = Action.Id,
                        ResolvedParametersJson = request.ResolvedParametersJson,
                        Commands = request.Commands,
                        AudienceSnapshot = request.AudienceSnapshot
                    };
                    Execution = Execution with { Version = Execution.Version + 1, PreparedActions = [plan] };
                    return Task.FromResult(Ok(plan));
                });
            api.Setup(item => item.ClaimAction(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<ClaimRuleActionDto>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, Guid _, ClaimRuleActionDto request, CancellationToken _) =>
                {
                    Assert.Equal(Execution.Version, request.ExpectedVersion);
                    var claim = new RuleActionExecutionDto
                    {
                        Command = request.Command,
                        LogicalEffectId = request.LogicalEffectId,
                        Correlation = request.Command.Correlation,
                        Outcome = request.TerminalOutcome ?? RuleActionExecutionOutcome.Claimed,
                        Detail = request.TerminalDetail,
                        ClaimedAtUtc = Clock.Now
                    };
                    Execution = Execution with { Version = Execution.Version + 1, Actions = [.. Execution.Actions, claim] };
                    return Task.FromResult(new ApiResult<RuleActionClaimResultDto>(HttpStatusCode.Created,
                        new ApiResponse<RuleActionClaimResultDto>(new RuleActionClaimResultDto { Execution = Execution, Action = claim })));
                });
            api.Setup(item => item.UpdateActionOutcome(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                    It.IsAny<UpdateRuleActionOutcomeDto>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, Guid _, string _, UpdateRuleActionOutcomeDto request, CancellationToken _) =>
                {
                    if (FailOutcomes)
                    {
                        return Task.FromResult(new ApiResult<RuleActionClaimResultDto>(HttpStatusCode.InternalServerError));
                    }
                    Assert.Equal(Execution.Version, request.ExpectedVersion);
                    var updated = Execution.Actions.Single(item => item.LogicalEffectId == request.Result.LogicalEffectId) with
                    {
                        Outcome = request.Result.Outcome,
                        Detail = request.Result.Detail
                    };
                    Execution = Execution with
                    {
                        Version = Execution.Version + 1,
                        Actions = Execution.Actions.Select(item => item.LogicalEffectId == updated.LogicalEffectId ? updated : item).ToArray()
                    };
                    return Task.FromResult(Ok(new RuleActionClaimResultDto { Execution = Execution, Action = updated }));
                });
            Executor.Setup(item => item.UnsupportedReasonAsync(It.IsAny<RuleActionDefinition>(),
                It.IsAny<RuleInvocation>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
            Executor.Setup(item => item.AvailabilityAsync(It.IsAny<RuleActionDefinition>(), It.IsAny<RuleActionCommand>(),
                It.IsAny<RuleInvocation>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ActionAvailability());
            Executor.Setup(item => item.ExecuteAsync(It.IsAny<RuleActionDefinition>(), It.IsAny<RuleActionCommand>(),
                    It.IsAny<RuleInvocation>(), It.IsAny<CancellationToken>()))
                .Returns((RuleActionDefinition _, RuleActionCommand command, RuleInvocation _, CancellationToken _) =>
                {
                    Sent.Add(command);
                    return Task.FromResult(new ActionExecutionResult(ActionExecutionOutcome.OutcomeUnknown, "Best effort dispatch."));
                });
            var text = new Mock<IRuleActionTextPreparer>();
            text.Setup(item => item.PrepareAsync(It.IsAny<RuleActionDefinition>(), It.IsAny<RuleInvocation>(),
                    It.IsAny<DateTimeOffset>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((RuleActionDefinition action, RuleInvocation _, DateTimeOffset _, string _, string _, CancellationToken _) =>
                    Task.FromResult(new RuleActionTextPreparation(action, TextDiagnostics)));
            var budgets = new Mock<IRuleMessageBudgetProvider>();
            budgets.Setup(item => item.GetAsync(It.IsAny<RuleInvocation>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(Budget));
            service = new(repository.Object, Executor.Object, text.Object, budgets.Object, NullLogger<RuleMessageDeliveryService>.Instance, Clock);
        }

        public void ConfigureTagged()
        {
            Action.TypeId = "message.private-tagged";
            Action.Parameters["recipientTags"] = JsonSerializer.SerializeToElement(new[] { Guid.NewGuid() });
            Action.Parameters["recipientTagMatch"] = JsonSerializer.SerializeToElement("Any");
        }

        public RuleMessageRecipientTarget Recipient() => new()
        {
            GameServerId = Execution.GameServerId,
            ConnectionId = Guid.NewGuid(),
            PlayerGuid = Guid.NewGuid().ToString(),
            SlotId = 4,
            Runtime = new() { Id = "server-agent" }
        };

        public Task<RuleMessageDispatchResult> DispatchAsync() => service.DispatchAsync(Execution,
            new() { Id = "rule", Actions = [Action] }, 0, 0, Action, _ => new()
            {
                GameServerId = Execution.GameServerId,
                ExecutionId = Execution.ExecutionId,
                ConnectionId = Execution.ConnectionId,
                Correlation = new() { ConnectionId = Execution.ConnectionId, RuleId = "rule", ActionId = Action.Id },
                ActionId = Action.Id,
                LogicalEffectId = new string('a', 64),
                Mode = RuleExecutionMode.Enforce,
                ExpiresAtUtc = progress.ExpiresAtUtc ?? Clock.Now.AddSeconds(30)
            }, invocation, progress, default);

        public async Task FinishAsync()
        {
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var result = await DispatchAsync();
                if (result.Outcome is not null)
                {
                    return;
                }
                Assert.NotNull(result.ResumeAtUtc);
                Clock.Now = result.ResumeAtUtc.Value;
            }
            Assert.Fail("Message plan did not finish within its expected fragment count.");
        }

        private static ApiResult<T> Ok<T>(T value) => new(HttpStatusCode.OK, new ApiResponse<T>(value));
    }
}
