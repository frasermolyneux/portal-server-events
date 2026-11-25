using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.RulesEngine;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Rules;

public sealed class FeatureExecutionOrchestratorTests
{
    [Fact]
    public async Task ConfiguredOrderAndStopArePreservedWhenActionIsUnsupported()
    {
        var storage = new Storage();
        var executor = new Executor { Unsupported = "Unavailable executor." };
        var early = Rule("early", 0);
        var late = Rule("late", 1);

        await Create(storage, executor).ProcessAsync(Invocation(),
            Snapshot(late, early), default);

        var action = Assert.Single(storage.Execution!.Actions);
        Assert.Equal("early", action.Correlation.RuleId);
        Assert.Equal(RuleActionExecutionOutcome.SkippedUnsupported, action.Outcome);
        Assert.Equal(RuleFeatureExecutionState.Stopped, storage.Execution.State);
        Assert.Empty(executor.Effects);
    }

    [Fact]
    public async Task EnrichmentRevisitsUnknownWithoutReplayingCompletedRule()
    {
        var storage = new Storage();
        var executor = new Executor();
        var first = Rule("needs-ip", 0, "player.first-seen-ip");
        first.AfterMatch = RuleAfterMatch.ContinueToNextRule;
        var second = Rule("guid-only", 1);
        second.AfterMatch = RuleAfterMatch.ContinueToNextRule;
        var snapshot = Snapshot(first, second);
        var invocation = Invocation();
        var engine = Create(storage, executor);

        await engine.ProcessAsync(invocation, snapshot, default);
        Assert.Equal(["guid-only"], executor.Effects);
        Assert.Equal(RuleFeatureExecutionState.Evaluating, storage.Execution!.State);

        await engine.ProcessAsync(invocation with { Facts = invocation.Facts with { IpHistoryAvailable = true } }, snapshot, default);
        await engine.ProcessAsync(invocation, snapshot, default);

        Assert.Equal(["guid-only", "needs-ip"], executor.Effects);
        Assert.Equal(RuleFeatureExecutionState.Completed, storage.Execution.State);
    }

    [Fact]
    public async Task RepeatedActionIdsInDifferentRulesHaveIndependentLogicalEffects()
    {
        var storage = new Storage();
        var executor = new Executor();
        var first = Rule("first", 0);
        first.AfterMatch = RuleAfterMatch.ContinueToNextRule;

        await Create(storage, executor).ProcessAsync(Invocation(), Snapshot(first, Rule("second", 1)), default);

        Assert.Equal(["first", "second"], executor.Effects);
        Assert.Equal(2, storage.Execution!.Actions.Select(action => action.LogicalEffectId).Distinct().Count());
    }

    [Theory]
    [InlineData("player.kick")]
    [InlineData("moderation.kick-with-record")]
    public async Task AgentDropCommandRetainsConfiguredReasonForAudit(string actionType)
    {
        var storage = new Storage();
        var rule = Rule("kick", 0);
        rule.Actions[0].TypeId = actionType;
        var original = Invocation();
        var invocation = original with { Trigger = original.Trigger with { Runtime = new() { Id = "server-agent" } } };

        await Create(storage, new Executor()).ProcessAsync(invocation, Snapshot(rule), default);

        Assert.Equal("Rule matched.", Assert.Single(storage.Execution!.Actions).Command.Effect.Reason);
    }

    [Fact]
    public async Task LateIpSupplementsActivationHistoryWithoutReplacingFrozenGuidHistory()
    {
        var storage = new Storage();
        var executor = new Executor();
        var rule = Rule("late-ip", 0, "player.first-seen-ip");
        rule.Condition!.Parameters!["referenceScope"] = JsonSerializer.SerializeToElement("SiegeBaseline");
        var snapshot = Snapshot(rule) with
        {
            FeatureId = RulesEngineCatalogueV1.SiegeModeFeature,
            SiegeSessionId = Guid.NewGuid(),
            Baseline = new(new(true, null), new(false, null))
        };
        var engine = Create(storage, executor);
        var invocation = Invocation();

        await engine.ProcessAsync(invocation, snapshot, default);
        Assert.Empty(executor.Effects);
        await engine.ProcessAsync(invocation, snapshot with
        {
            Baseline = new(new(true, DateTimeOffset.UtcNow.AddYears(-1)), new(true, null))
        }, default);

        Assert.Equal(["late-ip"], executor.Effects);
        var progress = JsonSerializer.Deserialize<RuleExecutionProgress>(storage.Execution!.DecisionSnapshotJson!, RulesSerialization.Options)!;
        Assert.Null(progress.Baseline!.Guid.FirstSeenAtUtc);
        Assert.True(progress.Baseline.Ip.Available);
    }

    [Fact]
    public async Task SessionExtensionUpdatesUnclaimedDecisionWindowWithoutChangingFrozenRules()
    {
        var storage = new Storage();
        var executor = new Executor();
        var engine = Create(storage, executor);
        var invocation = Invocation();
        var snapshot = Snapshot(Rule("needs-ip", 0, "player.first-seen-ip")) with
        {
            FeatureId = RulesEngineCatalogueV1.SiegeModeFeature,
            SiegeSessionId = Guid.NewGuid(),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
        };
        await engine.ProcessAsync(invocation, snapshot, default);
        var progress = JsonSerializer.Deserialize<RuleExecutionProgress>(
            storage.Execution!.DecisionSnapshotJson!, RulesSerialization.Options)!;
        progress.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        storage.Execution = storage.Execution with
        {
            DecisionSnapshotJson = JsonSerializer.Serialize(progress, RulesSerialization.Options)
        };

        await engine.ProcessAsync(invocation with { Facts = invocation.Facts with { IpHistoryAvailable = true } },
            snapshot with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(2) }, default);

        Assert.Equal(["needs-ip"], executor.Effects);
        Assert.Equal(snapshot.RulesJson, storage.Execution.FrozenRulesJson);
    }

    [Fact]
    public async Task LatestObservationRemainsFrozenWhenAnOlderTriggerRetries()
    {
        var storage = new Storage();
        var executor = new Executor();
        var engine = Create(storage, executor);
        var original = Invocation();
        var enriched = new RuleInvocation(original.Trigger with
        {
            TriggerId = "player.ip-resolved",
            ObservationId = Guid.NewGuid(),
            TriggerOccurrenceId = Guid.NewGuid(),
            PlayerIpAddress = "8.8.8.8",
            OccurredAtUtc = original.Trigger.OccurredAtUtc.AddSeconds(1)
        }, original.Facts, null);
        var rules = Snapshot(Rule("unavailable-ip", 0, "player.first-seen-ip"));

        await engine.ProcessAsync(original, rules, default);
        await engine.ProcessAsync(enriched, rules, default);
        await engine.ProcessAsync(original, rules, default);

        var progress = JsonSerializer.Deserialize<RuleExecutionProgress>(
            storage.Execution!.DecisionSnapshotJson!, RulesSerialization.Options)!;
        Assert.Equal(enriched.Trigger.ObservationId, progress.ContextTrigger!.ObservationId);
        Assert.Equal(enriched.Trigger.TriggerOccurrenceId, progress.ContextTrigger.TriggerOccurrenceId);
        Assert.Equal(enriched.Trigger.OccurredAtUtc, progress.ContextObservedAtUtc);
        Assert.Empty(executor.Effects);
    }

    [Fact]
    public async Task DryRunPersistsWouldExecuteWithoutCallingAnyExecutor()
    {
        var storage = new Storage();
        var executor = new Executor();
        var snapshot = Snapshot(Rule("dry", 0)) with { Mode = RuleExecutionMode.DryRun };

        await Create(storage, executor).ProcessAsync(Invocation(), snapshot, default);

        Assert.Empty(executor.Effects);
        Assert.Equal(RuleActionExecutionOutcome.WouldExecute, Assert.Single(storage.Execution!.Actions).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentCloudOrDryRunClaimCannotBeAcquiredTwice(bool dryRun)
    {
        var storage = new Storage { ClaimAlreadyOwned = true };
        var executor = new Executor();
        var invocation = Invocation();
        var snapshot = Snapshot(Rule("observation", 0)) with
        {
            Mode = dryRun ? RuleExecutionMode.DryRun : RuleExecutionMode.Enforce
        };

        await Create(storage, executor).ProcessAsync(invocation, snapshot, default);

        Assert.Empty(executor.Effects);
        Assert.Equal(RuleActionExecutionOutcome.Claimed, Assert.Single(storage.Execution!.Actions).Outcome);
        Assert.Contains(storage.Execution.ExecutionId, invocation.PendingExecutionIds);
    }

    [Fact]
    public async Task ObservationRecordIdentityIsIncludedInCorrelatedAggregateCompletion()
    {
        var storage = new Storage();
        var executor = new Executor { AdminActionId = Guid.NewGuid() };

        await Create(storage, executor).ProcessAsync(Invocation(), Snapshot(Rule("observation", 0)), default);

        var action = Assert.Single(storage.Execution!.Actions);
        Assert.Equal(executor.AdminActionId, action.AdminActionId);
        Assert.Equal(RuleActionExecutionOutcome.Applied, action.Outcome);
    }

    [Fact]
    public async Task LateIpContextIsRetainedWhileOriginalNativeClaimRemainsPending()
    {
        var storage = new Storage();
        var executor = new Executor { ReturnNativeCommand = true };
        var engine = Create(storage, executor);
        var seed = Invocation();
        var original = new RuleInvocation(seed.Trigger with
        {
            ObservationId = Guid.NewGuid(),
            TriggerOccurrenceId = Guid.NewGuid(),
            ConnectionGeneration = 1,
            Runtime = new()
            {
                Id = "cod4x-plugin",
                Version = "1.0",
                IdentityModel = RuleRuntimeIdentityModel.PluginRuntimeGeneration,
                InstanceId = Guid.NewGuid(),
                StartedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
                Capabilities = [RulesEngineCatalogueV1.Cod4xPublicChatCapability]
            }
        }, seed.Facts, null);
        var rule = Rule("native", 0);
        rule.Actions[0] = new()
        {
            Id = "action",
            TypeId = "message.public",
            Parameters = new() { ["message"] = JsonSerializer.SerializeToElement("Hello") }
        };
        var snapshot = Snapshot(rule);
        await engine.ProcessAsync(original, snapshot, default);
        var claimed = Assert.Single(storage.Execution!.Actions).Command;
        var ruleIndex = storage.Execution.RuleIndex;
        var actionIndex = storage.Execution.ActionIndex;
        var enrichedTrigger = original.Trigger with
        {
            TriggerId = "player.ip-resolved",
            ObservationId = Guid.NewGuid(),
            TriggerOccurrenceId = Guid.NewGuid(),
            OccurredAtUtc = original.Trigger.OccurredAtUtc.AddMilliseconds(10),
            PlayerIpAddress = "203.0.113.1"
        };
        var enriched = new RuleInvocation(enrichedTrigger, original.Facts, null);

        await engine.ProcessAsync(enriched, snapshot, default);
        await engine.ProcessAsync(original, snapshot, default);

        var progress = JsonSerializer.Deserialize<RuleExecutionProgress>(
            storage.Execution.DecisionSnapshotJson!, RulesSerialization.Options)!;
        Assert.Equal(enrichedTrigger.TriggerOccurrenceId, progress.ContextTrigger!.TriggerOccurrenceId);
        Assert.Equal(enrichedTrigger.PlayerIpAddress, progress.ContextTrigger.PlayerIpAddress);
        Assert.Equal(claimed, Assert.Single(storage.Execution.Actions).Command);
        Assert.Equal(ruleIndex, storage.Execution.RuleIndex);
        Assert.Equal(actionIndex, storage.Execution.ActionIndex);
        Assert.Equal(RuleFeatureExecutionState.AwaitingActionOutcome, storage.Execution.State);
        Assert.Single(executor.Effects);
        Assert.Empty(enriched.Commands);
    }

    [Fact]
    public async Task NativeCommandIsReturnedOnceOnlyAfterDurableDispatch()
    {
        var storage = new Storage();
        var executor = new Executor { ReturnNativeCommand = true };
        var engine = Create(storage, executor);
        var first = Invocation();
        var rules = Snapshot(Rule("native", 0));

        await engine.ProcessAsync(first, rules, default);
        var duplicate = Invocation();
        await engine.ProcessAsync(duplicate, rules, default);

        Assert.Single(first.Commands);
        Assert.Empty(duplicate.Commands);
        Assert.Single(executor.Effects);
        Assert.Equal(RuleActionExecutionOutcome.Dispatched, Assert.Single(storage.Execution!.Actions).Outcome);
        Assert.Equal(RuleFeatureExecutionState.AwaitingActionOutcome, storage.Execution.State);
    }

    [Fact]
    public async Task AwaitingBindingDefersBeforeClaimAndSchedulesOriginalExecution()
    {
        var storage = new Storage();
        var executor = new Executor { Deferred = true };
        var continuation = new Continuations();
        var engine = Create(storage, executor, continuation);
        var original = Invocation();
        var invocation = original with { Trigger = original.Trigger with { Runtime = new() { Id = "server-agent" } } };
        var rules = Snapshot(Rule("waiting", 0));

        await engine.ProcessAsync(invocation, rules, default);

        Assert.Empty(storage.Execution!.Actions);
        Assert.Empty(executor.Effects);
        Assert.Equal((storage.Execution.GameServerId, storage.Execution.ExecutionId), Assert.Single(continuation.Scheduled));

        executor.Deferred = false;
        await engine.ProcessAsync(invocation, rules, default);

        Assert.Equal(["waiting"], executor.Effects);
        Assert.Equal(RuleFeatureExecutionState.Stopped, storage.Execution.State);
    }

    [Fact]
    public async Task PluginContextDeferralRemainsOnPollTransportWithoutAgentQueueResume()
    {
        var storage = new Storage();
        var executor = new Executor { Deferred = true };
        var continuations = new Continuations();
        var original = Invocation();
        var invocation = original with { Trigger = original.Trigger with { Runtime = new() { Id = "cod4x-plugin" } } };

        await Create(storage, executor, continuations).ProcessAsync(invocation, Snapshot(Rule("waiting", 0)), default);

        Assert.Empty(storage.Execution!.Actions);
        Assert.Empty(continuations.Scheduled);
        Assert.Equal(storage.Execution.ExecutionId, Assert.Single(invocation.PendingExecutionIds));
    }

    [Fact]
    public async Task UnrelatedRulesDoNotStartAnIntelligenceLookup()
    {
        var storage = new Storage();
        var executor = new Executor();
        var invocation = Invocation() with
        {
            IntelligenceLookup = new(() => throw new InvalidOperationException("Unrelated lookup must not run."))
        };

        await Create(storage, executor).ProcessAsync(invocation, Snapshot(Rule("guid-only", 0)), default);

        Assert.Equal(["guid-only"], executor.Effects);
        Assert.False(invocation.IntelligenceLookup.IsValueCreated);
    }

    [Fact]
    public async Task FailedDispatchPersistenceDoesNotExposeOrResendNativeCommand()
    {
        var storage = new Storage { FailOutcomes = true };
        var executor = new Executor { ReturnNativeCommand = true };
        var engine = Create(storage, executor);
        var invocation = Invocation();
        var rules = Snapshot(Rule("native", 0));

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ProcessAsync(invocation, rules, default));
        Assert.Empty(invocation.Commands);
        storage.FailOutcomes = false;
        await engine.ProcessAsync(invocation, rules, default);

        Assert.Single(executor.Effects);
        Assert.Empty(invocation.Commands);
        Assert.Equal(RuleActionExecutionOutcome.Claimed, Assert.Single(storage.Execution!.Actions).Outcome);
    }

    private static FeatureExecutionOrchestrator Create(
        Storage storage, Executor executor, IRuleExecutionContinuationPublisher? continuation = null) =>
        new(storage.Repository.Object, executor, continuation ?? new Continuations(),
            NullLogger<FeatureExecutionOrchestrator>.Instance, NullLogger<OrderedActionDispatcher>.Instance);

    private static FeatureRulesSnapshot Snapshot(params RuleDefinition[] rules) =>
        new(RulesEngineCatalogueV1.VpnProtectionFeature, rules, RuleExecutionMode.Enforce);

    private static RuleDefinition Rule(string id, int order, string condition = "player.first-seen-guid") => new()
    {
        Id = id,
        Order = order,
        Condition = new()
        {
            ConditionId = condition,
            Parameters = new()
            {
                ["operator"] = JsonSerializer.SerializeToElement("IsNew"),
                ["referenceScope"] = JsonSerializer.SerializeToElement("ObservedHistory")
            }
        },
        Actions =
        [
            new()
            {
                Id = "action",
                TypeId = "moderation.observation",
                Parameters = new() { ["reason"] = JsonSerializer.SerializeToElement("Rule matched.") }
            }
        ]
    };

    private static RuleInvocation Invocation()
    {
        var trigger = new RuleTriggerEnvelope
        {
            GameServerId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            ConnectionId = Guid.Parse("00000000-0000-0000-0000-000000000002"),
            TriggerId = "player.connected",
            GameType = GameType.CallOfDuty4x,
            OccurredAtUtc = DateTimeOffset.UtcNow,
            PlayerGuid = "player",
            SlotId = 4
        };
        return new(trigger, new RuleFactSnapshotDto { SnapshotId = Guid.NewGuid(), GuidHistoryAvailable = true }, null);
    }

    private sealed class Continuations : IRuleExecutionContinuationPublisher
    {
        public List<(Guid ServerId, Guid ExecutionId)> Scheduled { get; } = [];
        public Task ScheduleAsync(Guid serverId, Guid executionId, DateTimeOffset scheduledAtUtc, CancellationToken ct)
        {
            Scheduled.Add((serverId, executionId));
            return Task.CompletedTask;
        }
    }

    private sealed class Executor : IUniversalRuleActionExecutor
    {
        public List<string> Effects { get; } = [];
        public string? Unsupported { get; init; }
        public bool ReturnNativeCommand { get; init; }
        public Guid? AdminActionId { get; init; }
        public bool Deferred { get; set; }
        public Task<string?> UnsupportedReasonAsync(RuleActionDefinition action, RuleInvocation invocation, CancellationToken ct) =>
            Task.FromResult(Unsupported);
        public Task<ActionAvailability> AvailabilityAsync(
            RuleActionDefinition action, RuleActionCommand command, RuleInvocation invocation, DateTimeOffset waitUntilUtc, CancellationToken ct) =>
            Task.FromResult(new ActionAvailability(Deferred));
        public Task<ActionExecutionResult> ExecuteAsync(
            RuleActionDefinition action, RuleActionCommand command, RuleInvocation invocation, CancellationToken ct)
        {
            Effects.Add(command.Correlation.RuleId);
            return Task.FromResult(ReturnNativeCommand
                ? new ActionExecutionResult(ActionExecutionOutcome.Dispatched, NativeCommand: command)
                : new ActionExecutionResult(ActionExecutionOutcome.Applied, AdminActionId: AdminActionId));
        }
    }

    private sealed class Storage
    {
        public Mock<IRepositoryApiClient> Repository { get; } = new();
        public RuleFeatureExecutionDto? Execution { get; set; }
        public bool FailOutcomes { get; set; }
        public bool ClaimAlreadyOwned { get; init; }

        public Storage()
        {
            var api = new Mock<IRuleExecutionsApi>();
            var versioned = new Mock<IVersionedRuleExecutionsApi>();
            versioned.Setup(item => item.V1).Returns(api.Object);
            Repository.Setup(item => item.RuleExecutions).Returns(versioned.Object);
            api.Setup(item => item.StartExecution(It.IsAny<Guid>(), It.IsAny<StartRuleFeatureExecutionDto>(), It.IsAny<CancellationToken>()))
                .Returns((Guid serverId, StartRuleFeatureExecutionDto request, CancellationToken _) =>
                {
                    Execution ??= new RuleFeatureExecutionDto
                    {
                        ExecutionId = Guid.NewGuid(),
                        GameServerId = serverId,
                        ConnectionId = request.Trigger.ConnectionId,
                        SiegeSessionId = request.SiegeSessionId,
                        FeatureId = request.FeatureId,
                        Trigger = request.Trigger,
                        RuleSetRevision = request.RuleSetRevision,
                        FrozenRulesJson = request.FrozenRulesJson,
                        FactSnapshotId = request.FactSnapshotId
                    };
                    return Task.FromResult(Ok(Execution));
                });
            api.Setup(item => item.UpdateProgress(It.IsAny<Guid>(), It.IsAny<Guid>(),
                    It.IsAny<UpdateRuleFeatureExecutionProgressDto>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, Guid _, UpdateRuleFeatureExecutionProgressDto request, CancellationToken _) =>
                {
                    Assert.Equal(Execution!.Version, request.ExpectedVersion);
                    if (Execution.Actions.Any(action => action.Outcome is RuleActionExecutionOutcome.Claimed or RuleActionExecutionOutcome.Dispatched))
                    {
                        Assert.Equal(Execution.RuleIndex, request.RuleIndex);
                        Assert.Equal(Execution.ActionIndex, request.ActionIndex);
                        Assert.Equal(Execution.State, request.State);
                    }
                    Execution = Execution with
                    {
                        Version = Execution.Version + 1,
                        RuleIndex = request.RuleIndex,
                        ActionIndex = request.ActionIndex,
                        State = request.State,
                        DecisionSnapshotJson = request.DecisionSnapshotJson
                    };
                    return Task.FromResult(Ok(Execution));
                });
            api.Setup(item => item.ClaimAction(It.IsAny<Guid>(), It.IsAny<Guid>(),
                    It.IsAny<ClaimRuleActionDto>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, Guid _, ClaimRuleActionDto request, CancellationToken _) =>
                {
                    Assert.Equal(Execution!.Version, request.ExpectedVersion);
                    using var frozen = JsonDocument.Parse(Execution.FrozenRulesJson);
                    var frozenRule = frozen.RootElement.GetProperty("rules")[request.RuleIndex];
                    Assert.Equal(request.Command.Correlation.RuleId, frozenRule.GetProperty("id").GetString());
                    var frozenAction = frozenRule.GetProperty("actions")[request.ActionIndex];
                    Assert.Equal(request.Command.ActionId, frozenAction.GetProperty("id").GetString());
                    var action = new RuleActionExecutionDto
                    {
                        ExecutionId = Execution.ExecutionId,
                        GameServerId = Execution.GameServerId,
                        LogicalEffectId = request.LogicalEffectId,
                        Correlation = request.Command.Correlation,
                        Command = request.Command,
                        Mode = request.Command.Mode,
                        ActionTypeId = frozenAction.GetProperty("typeId").GetString()!,
                        ParametersJson = frozenAction.GetProperty("parameters").GetRawText(),
                        Outcome = RuleActionExecutionOutcome.Claimed,
                        ClaimedAtUtc = DateTimeOffset.UtcNow
                    };
                    Execution = Execution with
                    {
                        Version = Execution.Version + 1,
                        Actions = [.. Execution.Actions, action],
                        RuleIndex = request.RuleIndex,
                        ActionIndex = request.ActionIndex,
                        State = RuleFeatureExecutionState.AwaitingActionOutcome
                    };
                    return Task.FromResult(new ApiResult<RuleActionClaimResultDto>(
                        ClaimAlreadyOwned ? HttpStatusCode.OK : HttpStatusCode.Created,
                        new ApiResponse<RuleActionClaimResultDto>(new RuleActionClaimResultDto
                        {
                            Action = action,
                            Execution = Execution,
                            ShouldDispatch = !ClaimAlreadyOwned && request.Command.Mode == RuleExecutionMode.Enforce &&
                                request.Command.Effect.Type != RuleNativeEffectType.Unknown
                        })));
                });
            api.Setup(item => item.UpdateActionOutcome(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                    It.IsAny<UpdateRuleActionOutcomeDto>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, Guid _, string _, UpdateRuleActionOutcomeDto request, CancellationToken _) =>
                {
                    if (FailOutcomes)
                    {
                        return Task.FromResult(new ApiResult<RuleActionClaimResultDto>(HttpStatusCode.InternalServerError));
                    }
                    Assert.Equal(Execution!.Version, request.ExpectedVersion);
                    var action = Execution.Actions.Single(item => item.LogicalEffectId == request.Result.LogicalEffectId) with
                    {
                        AdminActionId = request.AdminActionId,
                        Outcome = request.Result.Outcome,
                        Detail = request.Result.Detail
                    };
                    Execution = Execution with
                    {
                        Version = Execution.Version + 1,
                        Actions = Execution.Actions.Select(item => item.LogicalEffectId == action.LogicalEffectId ? action : item).ToArray(),
                        RuleIndex = request.NextRuleIndex,
                        ActionIndex = request.NextActionIndex,
                        State = request.NextState,
                        DecisionSnapshotJson = request.DecisionSnapshotJson
                    };
                    return Task.FromResult(Ok(new RuleActionClaimResultDto { Action = action, Execution = Execution }));
                });
        }

        private static ApiResult<T> Ok<T>(T value) => new(HttpStatusCode.OK, new ApiResponse<T>(value));
    }
}
