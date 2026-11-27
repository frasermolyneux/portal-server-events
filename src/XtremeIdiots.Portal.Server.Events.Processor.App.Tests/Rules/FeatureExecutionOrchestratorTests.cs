using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using MX.Api.Abstractions;
using MX.Api.Client;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.RulesEngine;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Rules;

public sealed class FeatureExecutionOrchestratorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentNoMatchCompletionDiscardsStaleProgressWithoutRetryingWrite(bool throwConflict)
    {
        var storage = new Storage();
        var executor = new Executor();
        var invocation = Invocation();
        invocation = invocation with
        {
            Facts = invocation.Facts with { GuidFirstSeenAtUtc = DateTimeOffset.UtcNow.AddDays(-1) }
        };
        var snapshot = Snapshot(Rule("known", 0));
        var firstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        storage.Api.Setup(item => item.UpdateProgress(It.IsAny<Guid>(), It.IsAny<Guid>(),
                It.IsAny<UpdateRuleFeatureExecutionProgressDto>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, Guid _, UpdateRuleFeatureExecutionProgressDto request, CancellationToken _) =>
            {
                Assert.Equal(0, request.ExpectedVersion);
                if (Interlocked.Increment(ref writes) == 1)
                {
                    firstWrite.SetResult();
                    await releaseFirst.Task;
                    if (throwConflict)
                    {
                        throw new ApiHttpRequestException("Conflict", HttpStatusCode.Conflict, null);
                    }
                    return new ApiResult<RuleFeatureExecutionDto>(HttpStatusCode.Conflict);
                }
                storage.Execution = CompleteNoMatch(storage.Execution!, request);
                return new ApiResult<RuleFeatureExecutionDto>(HttpStatusCode.OK,
                    new ApiResponse<RuleFeatureExecutionDto>(storage.Execution));
            });

        var first = Create(storage, executor).ProcessAsync(invocation, snapshot, default);
        await firstWrite.Task;
        var second = await Create(storage, executor).ProcessAsync(invocation with
        {
            Trigger = invocation.Trigger with { TriggerId = "player.ip-resolved" }
        }, snapshot, default);
        var authoritativeProgress = storage.Execution!.DecisionSnapshotJson;
        releaseFirst.SetResult();

        Assert.Equal(second, await first);
        Assert.Equal(authoritativeProgress, storage.Execution.DecisionSnapshotJson);
        Assert.Equal(RuleFeatureExecutionState.Completed, storage.Execution.State);
        Assert.Empty(executor.Effects);
        Assert.Empty(invocation.Commands);
        Assert.Equal(second, await Create(storage, executor).ProcessAsync(invocation, snapshot, default));
        Assert.Equal(2, writes);
        storage.Api.Verify(item => item.GetExecution(It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("matched")]
    [InlineData("execution")]
    [InlineData("connection")]
    [InlineData("revision")]
    [InlineData("frozen-rules")]
    [InlineData("version")]
    [InlineData("cursor")]
    [InlineData("mode")]
    [InlineData("missing-evidence")]
    [InlineData("action")]
    [InlineData("prepared-action")]
    [InlineData("unavailable")]
    public async Task ConflictRetainsFailureWhenAuthoritativeCompletionIsUncertain(string mismatch)
    {
        var storage = new Storage();
        var executor = new Executor();
        var invocation = Invocation();
        invocation = invocation with
        {
            Facts = invocation.Facts with { GuidFirstSeenAtUtc = DateTimeOffset.UtcNow.AddDays(-1) }
        };
        storage.Api.Setup(item => item.UpdateProgress(It.IsAny<Guid>(), It.IsAny<Guid>(),
                It.IsAny<UpdateRuleFeatureExecutionProgressDto>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, Guid _, UpdateRuleFeatureExecutionProgressDto request, CancellationToken _) =>
            {
                var current = CompleteNoMatch(storage.Execution!, request);
                storage.Execution = mismatch switch
                {
                    "active" => current with { State = RuleFeatureExecutionState.Evaluating },
                    "matched" => current with { MatchStatus = RuleExecutionMatchStatus.Matched },
                    "execution" => current with { ExecutionId = Guid.NewGuid() },
                    "connection" => current with { ConnectionId = Guid.NewGuid() },
                    "revision" => current with { RuleSetRevision = "different" },
                    "frozen-rules" => current with { FrozenRulesJson = "{}" },
                    "version" => current with { Version = request.ExpectedVersion },
                    "cursor" => current with { ActionIndex = 1 },
                    "mode" => current with
                    {
                        DecisionSnapshotJson = JsonSerializer.Serialize(
                        new RuleExecutionProgress { Mode = RuleExecutionMode.DryRun }, RulesSerialization.Options)
                    },
                    "missing-evidence" => current with { DecisionSnapshotJson = null },
                    "action" => current with
                    {
                        Actions = [new RuleActionExecutionDto
                        { Outcome = RuleActionExecutionOutcome.Claimed }]
                    },
                    "prepared-action" => current with { PreparedActions = [new()] },
                    _ => current
                };
                return Task.FromResult(new ApiResult<RuleFeatureExecutionDto>(HttpStatusCode.Conflict));
            });
        if (mismatch == "unavailable")
        {
            storage.Api.Setup(item => item.GetExecution(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ApiResult<RuleFeatureExecutionDto>(HttpStatusCode.ServiceUnavailable));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create(storage, executor).ProcessAsync(invocation, Snapshot(Rule("known", 0)), default));

        Assert.Empty(executor.Effects);
        storage.Api.Verify(item => item.UpdateProgress(It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.IsAny<UpdateRuleFeatureExecutionProgressDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConflictDoesNotReconcileLocalMatchedWork()
    {
        var storage = new Storage();
        var executor = new Executor();
        var conflict = new ApiHttpRequestException("Conflict", HttpStatusCode.Conflict, null);
        storage.Api.Setup(item => item.UpdateProgress(It.IsAny<Guid>(), It.IsAny<Guid>(),
                It.IsAny<UpdateRuleFeatureExecutionProgressDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(conflict);

        Assert.Same(conflict, await Assert.ThrowsAsync<ApiHttpRequestException>(() =>
            Create(storage, executor).ProcessAsync(Invocation(), Snapshot(Rule("matched", 0)), default)));

        Assert.Empty(executor.Effects);
        storage.Api.Verify(item => item.GetExecution(It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("action")]
    [InlineData("prepared-action")]
    [InlineData("outcome")]
    [InlineData("unknown")]
    public async Task ConflictDoesNotDiscardLocalActionOrUnresolvedWork(string work)
    {
        var storage = new Storage();
        var progress = new RuleExecutionProgress
        {
            MatchEvidence = new() { NoMatchConfirmed = true, FalseRuleIds = ["known"] }
        };
        var execution = new RuleFeatureExecutionDto();
        switch (work)
        {
            case "action":
                execution = execution with { Actions = [new() { Outcome = RuleActionExecutionOutcome.Dispatched }] };
                break;
            case "prepared-action":
                execution = execution with { PreparedActions = [new()] };
                break;
            case "outcome":
                progress.ActionOutcomes.Add("known:action", new(ActionExecutionOutcome.OutcomeUnknown));
                break;
            case "unknown":
                progress.UnknownRuleIds.Add("unknown");
                break;
        }
        var conflict = new ApiHttpRequestException("Conflict", HttpStatusCode.Conflict, null);
        storage.Api.Setup(item => item.UpdateProgress(It.IsAny<Guid>(), It.IsAny<Guid>(),
                It.IsAny<UpdateRuleFeatureExecutionProgressDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(conflict);

        Assert.Same(conflict, await Assert.ThrowsAsync<ApiHttpRequestException>(() =>
            FeatureExecutionOrchestrator.PersistProgressAsync(storage.Repository.Object, execution, progress, 1, 0,
                RuleFeatureExecutionState.Completed, NullLogger.Instance, DateTimeOffset.UtcNow, default)));

        storage.Api.Verify(item => item.GetExecution(It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static RuleFeatureExecutionDto CompleteNoMatch(
        RuleFeatureExecutionDto execution, UpdateRuleFeatureExecutionProgressDto request) =>
        execution with
        {
            Version = execution.Version + 1,
            State = request.State,
            MatchStatus = request.MatchStatus!.Value,
            RuleIndex = request.RuleIndex,
            ActionIndex = request.ActionIndex,
            DecisionSnapshotJson = request.DecisionSnapshotJson
        };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitFalseEvidenceSurvivesRetryUntilEveryEnabledRuleIsFalse(bool resume)
    {
        var storage = new Storage();
        var invocation = Invocation();
        var first = Rule("known", 0);
        var second = Rule("needs-ip", 1, "player.first-seen-ip");
        var disabled = Rule("disabled", 2);
        disabled.Enabled = false;
        var snapshot = Snapshot(first, second, disabled);
        var engine = Create(storage, new Executor());
        var facts = invocation.Facts with { GuidFirstSeenAtUtc = DateTimeOffset.UtcNow.AddDays(-1) };

        await engine.ProcessAsync(invocation with { Facts = facts }, snapshot, default);

        var evidence = Progress(storage).MatchEvidence!;
        Assert.Equal(1, evidence.Version);
        Assert.Equal(["known"], evidence.FalseRuleIds);
        Assert.False(evidence.NoMatchConfirmed);
        Assert.False(evidence.ObservedMatch);
        Assert.Equal(RuleExecutionMatchStatus.Unknown, storage.Execution!.MatchStatus);
        if (resume)
        {
            await Create(storage, new Executor()).ProcessAsync(invocation with
            {
                Facts = facts with { IpHistoryAvailable = true, IpFirstSeenAtUtc = DateTimeOffset.UtcNow.AddDays(-1) }
            }, snapshot, default);

            evidence = Progress(storage).MatchEvidence!;
            Assert.True(evidence.NoMatchConfirmed);
            Assert.Equal(2, evidence.FalseRuleIds.Count);
            Assert.DoesNotContain("disabled", evidence.FalseRuleIds);
            Assert.Equal(RuleFeatureExecutionState.Completed, storage.Execution!.State);
            Assert.Equal(RuleExecutionMatchStatus.NoMatch, storage.Execution.MatchStatus);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObservedTrueIsRecordedBeforeActionAndRetainedWhileWaiting(bool native)
    {
        var storage = new Storage();
        var executor = new Executor { Deferred = !native, ReturnNativeCommand = native };

        await Create(storage, executor).ProcessAsync(Invocation(), Snapshot(Rule("matched", 0)), default);

        Assert.True(Progress(storage).MatchEvidence!.ObservedMatch);
        Assert.False(Progress(storage).MatchEvidence!.NoMatchConfirmed);
        Assert.Equal(RuleExecutionMatchStatus.Matched, storage.Execution!.MatchStatus);
        if (native)
        {
            Assert.True(storage.MatchObservedAtClaim);
            Assert.Equal(RuleExecutionMatchStatus.Matched, storage.MatchStatusAtClaim);
        }
        else
        {
            Assert.Empty(storage.Execution!.Actions);
        }
    }

    [Fact]
    public async Task UnknownRuleBecomesMatchedOnlyWhenLaterEvaluationIsTrue()
    {
        var storage = new Storage();
        var invocation = Invocation();
        var rule = Rule("needs-ip", 0, "player.first-seen-ip");
        var snapshot = Snapshot(rule);
        var engine = Create(storage, new Executor());

        await engine.ProcessAsync(invocation, snapshot, default);

        Assert.False(Progress(storage).MatchEvidence!.ObservedMatch);
        Assert.False(Progress(storage).MatchEvidence!.NoMatchConfirmed);
        Assert.Equal(RuleExecutionMatchStatus.Unknown, storage.Execution!.MatchStatus);
        Assert.Contains(rule.Id, Progress(storage).UnknownRuleIds);

        await Create(storage, new Executor()).ProcessAsync(invocation with
        {
            Facts = invocation.Facts with { IpHistoryAvailable = true }
        }, snapshot, default);

        Assert.True(Progress(storage).MatchEvidence!.ObservedMatch);
        Assert.False(Progress(storage).MatchEvidence!.NoMatchConfirmed);
        Assert.Equal(RuleFeatureExecutionState.Stopped, storage.Execution!.State);
        Assert.Equal(RuleExecutionMatchStatus.Matched, storage.Execution.MatchStatus);
    }

    [Fact]
    public async Task FalseRuleThenMatchedRuleKeepsMatchEvidenceAfterCompletion()
    {
        var storage = new Storage();
        var invocation = Invocation();
        var first = Rule("false", 0);
        var second = Rule("matched", 1, "always");
        second.Condition!.Parameters!.Clear();
        second.AfterMatch = RuleAfterMatch.ContinueToNextRule;

        await Create(storage, new Executor()).ProcessAsync(invocation with
        {
            Facts = invocation.Facts with { GuidFirstSeenAtUtc = DateTimeOffset.UtcNow.AddDays(-1) }
        }, Snapshot(first, second), default);

        Assert.True(Progress(storage).MatchEvidence!.ObservedMatch);
        Assert.Contains("false", Progress(storage).MatchEvidence!.FalseRuleIds);
        Assert.False(Progress(storage).MatchEvidence!.NoMatchConfirmed);
        Assert.Equal(RuleFeatureExecutionState.Completed, storage.Execution!.State);
        Assert.Equal(RuleExecutionMatchStatus.Matched, storage.Execution.MatchStatus);
    }

    [Theory]
    [InlineData("matched")]
    [InlineData("completed")]
    [InlineData("no-match-diagnostic")]
    public async Task LegacyProgressDoesNotEstablishMatchEvidence(string legacy)
    {
        var storage = new Storage();
        var invocation = Invocation();
        var rule = Rule("legacy", 0);
        var snapshot = Snapshot(rule);
        var progress = new RuleExecutionProgress
        {
            MatchedRuleIds = legacy == "matched" ? [rule.Id] : [],
            CompletedRuleIds = legacy != "matched" ? [rule.Id] : [],
            Diagnostics = new()
        };
        if (legacy == "no-match-diagnostic")
        {
            progress.Diagnostics.Add(new() { Code = RuleExecutionDiagnosticCode.NoMatch, RuleId = rule.Id });
        }
        storage.Execution = new()
        {
            ExecutionId = Guid.NewGuid(),
            GameServerId = invocation.Trigger.GameServerId,
            ConnectionId = invocation.Trigger.ConnectionId,
            FeatureId = snapshot.FeatureId,
            Trigger = invocation.Trigger,
            FrozenRulesJson = snapshot.RulesJson,
            DecisionSnapshotJson = JsonSerializer.Serialize(progress, RulesSerialization.Options)
        };

        await Create(storage, new Executor()).ProcessAsync(invocation, snapshot, default);

        Assert.False(Progress(storage).MatchEvidence!.ObservedMatch);
        Assert.False(Progress(storage).MatchEvidence!.NoMatchConfirmed);
        Assert.Empty(Progress(storage).MatchEvidence!.FalseRuleIds);
        Assert.Equal(RuleExecutionMatchStatus.Unknown, storage.Execution!.MatchStatus);
    }

    [Fact]
    public async Task ExpiredUnevaluatedRulesDoNotBecomeFalseEvidence()
    {
        var storage = new Storage();
        var invocation = Invocation();
        var clock = new Clock(invocation.Trigger.OccurredAtUtc.AddMinutes(3));
        var snapshot = Snapshot(WelcomeRule("expired", 0)) with
        {
            FeatureId = WelcomeFeatureRulesProvider.Id,
            Timing = new() { ConnectionDelaySeconds = 0, FreshnessSeconds = 120 }
        };

        await Create(storage, new Executor(), clock: clock).ProcessAsync(invocation, snapshot, default);

        Assert.False(Progress(storage).MatchEvidence!.ObservedMatch);
        Assert.False(Progress(storage).MatchEvidence!.NoMatchConfirmed);
        Assert.Empty(Progress(storage).MatchEvidence!.FalseRuleIds);
        Assert.Equal(RuleExecutionMatchStatus.Unknown, storage.Execution!.MatchStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyExplicitFalseForEveryEligibleRuleConfirmsNoMatch(bool enabled)
    {
        var storage = new Storage();
        var invocation = Invocation();
        var rule = Rule("false", 0);
        rule.Enabled = enabled;

        await Create(storage, new Executor()).ProcessAsync(invocation with
        {
            Facts = invocation.Facts with { GuidFirstSeenAtUtc = DateTimeOffset.UtcNow.AddDays(-1) }
        }, Snapshot(rule), default);

        Assert.False(Progress(storage).MatchEvidence!.ObservedMatch);
        Assert.Equal(enabled, Progress(storage).MatchEvidence!.NoMatchConfirmed);
        Assert.Equal(enabled ? RuleExecutionMatchStatus.NoMatch : RuleExecutionMatchStatus.Unknown,
            storage.Execution!.MatchStatus);
    }

    [Fact]
    public async Task FalseRuleFollowedByExpiredUnknownRuleRemainsUnconfirmed()
    {
        var storage = new Storage();
        var invocation = Invocation();
        var clock = new Clock(invocation.Trigger.OccurredAtUtc);
        var first = WelcomeRule("false", 0);
        first.Condition = Rule("false", 0).Condition;
        var second = WelcomeRule("unknown", 1);
        second.Condition = Rule("unknown", 1, "player.first-seen-ip").Condition;
        var snapshot = Snapshot(first, second) with
        {
            FeatureId = WelcomeFeatureRulesProvider.Id,
            Timing = new() { ConnectionDelaySeconds = 0, FreshnessSeconds = 120 }
        };
        var facts = invocation.Facts with { GuidFirstSeenAtUtc = DateTimeOffset.UtcNow.AddDays(-1) };

        await Create(storage, new Executor(), clock: clock).ProcessAsync(invocation with { Facts = facts }, snapshot, default);
        Assert.Equal(RuleFeatureExecutionState.Waiting, storage.Execution!.State);
        Assert.Equal(["false"], Progress(storage).MatchEvidence!.FalseRuleIds);

        clock.Now = invocation.Trigger.OccurredAtUtc.AddMinutes(3);
        await Create(storage, new Executor(), clock: clock).ProcessAsync(invocation with { Facts = facts }, snapshot, default);

        Assert.Equal(RuleFeatureExecutionState.Completed, storage.Execution.State);
        Assert.False(Progress(storage).MatchEvidence!.NoMatchConfirmed);
        Assert.Equal(["false"], Progress(storage).MatchEvidence!.FalseRuleIds);
        Assert.Equal(RuleExecutionMatchStatus.Unknown, storage.Execution.MatchStatus);
    }

    [Fact]
    public async Task RecordedMatchCannotBeDowngradedByLaterFalseEvidence()
    {
        var storage = new Storage();
        var invocation = Invocation();
        var snapshot = Snapshot(Rule("false", 0));
        storage.Execution = new()
        {
            ExecutionId = Guid.NewGuid(),
            GameServerId = invocation.Trigger.GameServerId,
            ConnectionId = invocation.Trigger.ConnectionId,
            FeatureId = snapshot.FeatureId,
            Trigger = invocation.Trigger,
            FrozenRulesJson = snapshot.RulesJson,
            MatchStatus = RuleExecutionMatchStatus.Matched
        };

        await Create(storage, new Executor()).ProcessAsync(invocation with
        {
            Facts = invocation.Facts with { GuidFirstSeenAtUtc = DateTimeOffset.UtcNow.AddDays(-1) }
        }, snapshot, default);

        Assert.Equal(RuleExecutionMatchStatus.Matched, storage.Execution.MatchStatus);
    }

    private static RuleExecutionProgress Progress(Storage storage) =>
        JsonSerializer.Deserialize<RuleExecutionProgress>(storage.Execution!.DecisionSnapshotJson!, RulesSerialization.Options)!;

    [Theory]
    [InlineData("server-agent", 1)]
    [InlineData("cod4x-plugin", 0)]
    public async Task OriginalConnectionDelaySurvivesRestartAndConfigurationChanges(string runtimeId, int scheduledCount)
    {
        var storage = new Storage();
        var executor = new Executor();
        var continuation = new Continuations();
        var messages = new Messages();
        var original = Invocation();
        var clock = new Clock(original.Trigger.OccurredAtUtc.AddSeconds(1));
        var invocation = original with { Trigger = original.Trigger with { Runtime = new() { Id = runtimeId } } };
        var snapshot = Snapshot(WelcomeRule("delayed", 0)) with
        {
            FeatureId = WelcomeFeatureRulesProvider.Id,
            Timing = new() { ConnectionDelaySeconds = 10, FreshnessSeconds = 120 }
        };

        await Create(storage, executor, continuation, clock, messages).ProcessAsync(invocation, snapshot, default);

        Assert.Empty(messages.Deliveries);
        Assert.Empty(storage.Execution!.Actions);
        Assert.Equal(RuleFeatureExecutionState.Waiting, storage.Execution.State);
        Assert.Equal("Waiting for original connection delay.", storage.Execution.Coordination!.WaitingReason);
        Assert.Equal(RuleExecutionDiagnosticCode.WaitingForDelay, Assert.Single(storage.Execution.Diagnostics).Code);
        Assert.Equal(scheduledCount, continuation.Scheduled.Count);
        if (scheduledCount > 0)
        {
            Assert.Equal(original.Trigger.OccurredAtUtc.AddSeconds(10), Assert.Single(continuation.DueTimes));
        }
        clock.Now = original.Trigger.OccurredAtUtc.AddSeconds(10);
        await Create(storage, executor, continuation, clock, messages).ProcessAsync(invocation, snapshot with
        {
            Timing = new() { ConnectionDelaySeconds = 60, FreshnessSeconds = 900 }
        }, default);

        Assert.Equal(["delayed"], messages.Deliveries);
        var progress = JsonSerializer.Deserialize<RuleExecutionProgress>(
            storage.Execution.DecisionSnapshotJson!, RulesSerialization.Options)!;
        Assert.Equal(original.Trigger.OccurredAtUtc.AddSeconds(120), progress.RuleWindows["delayed"].ExpiresAtUtc);
        Assert.Null(progress.WaitingReason);
        Assert.Null(storage.Execution.Coordination!.WaitingReason);
        Assert.Empty(storage.Execution.Diagnostics);
    }

    [Fact]
    public async Task MessagePreparationDeadlineSurvivesRetriesWithoutFeatureExpiry()
    {
        var storage = new Storage();
        var original = Invocation();
        var invocation = original with { Trigger = original.Trigger with { Runtime = new() { Id = "server-agent" } } };
        var rule = WelcomeRule("notice", 0);
        var key = JsonSerializer.Serialize(new[] { rule.Id, rule.Actions[0].Id });
        var deadlines = new List<DateTimeOffset>();
        var messages = new Mock<IRuleMessageDeliveryService>();
        messages.Setup(item => item.DispatchAsync(It.IsAny<RuleFeatureExecutionDto>(), It.IsAny<RuleDefinition>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<RuleActionDefinition>(),
                It.IsAny<Func<RuleActionDefinition, RuleActionCommand>>(), It.IsAny<RuleInvocation>(),
                It.IsAny<RuleExecutionProgress>(), It.IsAny<CancellationToken>()))
            .Returns((RuleFeatureExecutionDto execution, RuleDefinition _, int _, int _, RuleActionDefinition _,
                Func<RuleActionDefinition, RuleActionCommand> _, RuleInvocation _, RuleExecutionProgress progress, CancellationToken _) =>
            {
                var deadline = progress.MessagePreparationExpiries[key];
                deadlines.Add(deadline);
                return Task.FromResult(new RuleMessageDispatchResult(execution, ResumeAtUtc: deadline));
            });
        var engine = Create(storage, new Executor(), messages: messages.Object);

        await engine.ProcessAsync(invocation, Snapshot(rule), default);
        await engine.ProcessAsync(invocation, Snapshot(rule), default);

        Assert.Equal(2, deadlines.Count);
        Assert.Equal(deadlines[0], deadlines[1]);
        var progress = JsonSerializer.Deserialize<RuleExecutionProgress>(
            storage.Execution!.DecisionSnapshotJson!, RulesSerialization.Options)!;
        Assert.Equal(deadlines[0], progress.MessagePreparationExpiries[key]);
    }

    [Theory]
    [InlineData("server-agent", 1)]
    [InlineData("cod4x-plugin", 0)]
    public async Task PendingMessageClaimSchedulesOnlyAgentRecovery(string runtimeId, int expectedSchedules)
    {
        var storage = new Storage();
        var original = Invocation();
        var invocation = original with { Trigger = original.Trigger with { Runtime = new() { Id = runtimeId } } };
        var rule = WelcomeRule("notice", 0);
        var snapshot = Snapshot(rule);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        var command = new RuleActionCommand
        {
            ExpiresAtUtc = deadline,
            ActionId = rule.Actions[0].Id,
            Correlation = new() { RuleId = rule.Id, ActionId = rule.Actions[0].Id },
            DeliveryIdentity = new()
        };
        storage.Execution = new()
        {
            ExecutionId = Guid.NewGuid(),
            GameServerId = invocation.Trigger.GameServerId,
            ConnectionId = invocation.Trigger.ConnectionId,
            Trigger = invocation.Trigger,
            FeatureId = snapshot.FeatureId,
            FrozenRulesJson = snapshot.RulesJson,
            State = RuleFeatureExecutionState.AwaitingActionOutcome,
            Actions = [new()
            {
                Command = command,
                Correlation = command.Correlation,
                Outcome = RuleActionExecutionOutcome.Claimed
            }],
            DecisionSnapshotJson = JsonSerializer.Serialize(new RuleExecutionProgress
            {
                Mode = RuleExecutionMode.Enforce,
                MatchedRuleIds = [rule.Id],
                ExpiresAtUtc = deadline,
                ContextTrigger = invocation.Trigger,
                ContextObservedAtUtc = invocation.Trigger.OccurredAtUtc
            }, RulesSerialization.Options)
        };
        var messages = new Mock<IRuleMessageDeliveryService>();
        messages.Setup(item => item.DispatchAsync(It.IsAny<RuleFeatureExecutionDto>(), It.IsAny<RuleDefinition>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<RuleActionDefinition>(),
                It.IsAny<Func<RuleActionDefinition, RuleActionCommand>>(), It.IsAny<RuleInvocation>(),
                It.IsAny<RuleExecutionProgress>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RuleMessageDispatchResult(storage.Execution, AwaitingOutcome: true));
        var continuations = new Continuations();

        await Create(storage, new Executor(), continuations, messages: messages.Object).ProcessAsync(invocation, snapshot, default);

        Assert.Equal(expectedSchedules, continuations.Scheduled.Count);
        if (expectedSchedules != 0)
        {
            Assert.Equal(deadline, Assert.Single(continuations.DueTimes));
        }
        Assert.Empty(invocation.Commands);
    }

    [Fact]
    public async Task ExpiryWhileWaitingNeverClaimsOrDispatchesAnOldGreeting()
    {
        var storage = new Storage();
        var executor = new Executor();
        var messages = new Messages();
        var original = Invocation();
        var clock = new Clock(original.Trigger.OccurredAtUtc);
        var snapshot = Snapshot(WelcomeRule("delayed", 0)) with
        {
            FeatureId = WelcomeFeatureRulesProvider.Id,
            Timing = new() { ConnectionDelaySeconds = 5, FreshnessSeconds = 120 }
        };
        await Create(storage, executor, clock: clock, messages: messages).ProcessAsync(original, snapshot, default);

        clock.Now = clock.Now.AddSeconds(120);
        await Create(storage, executor, clock: clock, messages: messages).ProcessAsync(original, snapshot, default);

        Assert.Empty(messages.Deliveries);
        Assert.Empty(storage.Execution!.Actions);
        Assert.Empty(executor.Effects);
        Assert.Equal(RuleFeatureExecutionState.Completed, storage.Execution.State);
        Assert.Equal(RuleExecutionDiagnosticCode.Expired, Assert.Single(storage.Execution.Diagnostics).Code);
    }

    [Fact]
    public async Task ContinuingTaggedAlertBypassesDelayWithoutReplayingBeforeLaterGreeting()
    {
        var storage = new Storage();
        var messages = new Messages();
        var original = Invocation();
        var clock = new Clock(original.Trigger.OccurredAtUtc);
        var alert = WelcomeRule("alert", 0);
        alert.AfterMatch = RuleAfterMatch.ContinueToNextRule;
        alert.Actions[0].TypeId = "message.private-tagged";
        alert.Actions[0].Parameters["recipientTags"] = JsonSerializer.SerializeToElement(new[] { Guid.NewGuid() });
        alert.Actions[0].Parameters["recipientTagMatch"] = JsonSerializer.SerializeToElement("Any");
        var snapshot = Snapshot(alert, WelcomeRule("greeting", 1)) with
        {
            FeatureId = WelcomeFeatureRulesProvider.Id,
            Timing = new() { ConnectionDelaySeconds = 10 }
        };
        var engine = Create(storage, new Executor(), clock: clock, messages: messages);

        await engine.ProcessAsync(original, snapshot, default);
        Assert.Equal(["alert"], messages.Deliveries);
        Assert.Equal(RuleFeatureExecutionState.Waiting, storage.Execution!.State);
        clock.Now = clock.Now.AddSeconds(10);
        await engine.ProcessAsync(original, snapshot, default);

        Assert.Equal(["alert", "greeting"], messages.Deliveries);
        Assert.Equal(RuleFeatureExecutionState.Stopped, storage.Execution.State);
    }

    [Fact]
    public async Task UnknownHigherPriorityWelcomeDoesNotSelectCatchAll()
    {
        var storage = new Storage();
        var executor = new Executor();
        var first = Rule("needs-tags", 0, "player.tags");
        first.Condition!.Parameters = new()
        {
            ["tags"] = JsonSerializer.SerializeToElement(new[] { Guid.NewGuid().ToString() }),
            ["operator"] = JsonSerializer.SerializeToElement("ContainsAny")
        };
        var second = Rule("everyone", 1, "always");
        second.Condition!.Parameters = [];
        foreach (var rule in new[] { first, second })
        {
            rule.Actions[0].TypeId = "message.public";
            rule.Actions[0].Parameters = new() { ["message"] = JsonSerializer.SerializeToElement("Welcome") };
        }
        var snapshot = Snapshot(first, second) with
        {
            FeatureId = WelcomeFeatureRulesProvider.Id,
            Timing = new() { ConnectionDelaySeconds = 0 }
        };

        var invocation = Invocation();
        await Create(storage, executor).ProcessAsync(invocation, snapshot, default);

        var progress = JsonSerializer.Deserialize<RuleExecutionProgress>(
            storage.Execution!.DecisionSnapshotJson!, RulesSerialization.Options)!;
        Assert.Contains("needs-tags", progress.UnknownRuleIds);
        Assert.Empty(progress.MatchedRuleIds);
        Assert.Empty(storage.Execution.Actions);
        Assert.Equal(storage.Execution.ExecutionId, Assert.Single(invocation.PendingExecutionIds));
        Assert.Equal(RuleExecutionDiagnosticCode.WaitingForContext, Assert.Single(storage.Execution.Diagnostics).Code);
    }

    [Fact]
    public async Task NonMatchingRulePersistsTypedReason()
    {
        var storage = new Storage();
        var rule = Rule("known-only", 0);
        rule.Condition!.Parameters!["operator"] = JsonSerializer.SerializeToElement("IsKnown");

        await Create(storage, new Executor()).ProcessAsync(Invocation(), Snapshot(rule), default);

        var diagnostic = Assert.Single(storage.Execution!.Diagnostics);
        Assert.Equal(RuleExecutionDiagnosticCode.NoMatch, diagnostic.Code);
        Assert.Equal("known-only", diagnostic.RuleId);
        Assert.Empty(storage.Execution.Actions);
    }

    [Fact]
    public async Task RetryingRenderedActionReplacesProvisionalDiagnosticsBeforeClaim()
    {
        var storage = new Storage();
        var executor = new Executor { Deferred = true };
        var rule = Rule("templated", 0);
        rule.Actions[0].TemplateParameters.Add("reason");
        var first = rule.Actions[0];
        var resolved = new RuleActionDefinition
        {
            Id = first.Id,
            TypeId = first.TypeId,
            Parameters = new() { ["reason"] = JsonSerializer.SerializeToElement("Fresh reason") }
        };
        var preparer = new Mock<IRuleActionTextPreparer>();
        preparer.SetupSequence(item => item.PrepareAsync(It.IsAny<RuleActionDefinition>(), It.IsAny<RuleInvocation>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RuleActionTextPreparation(first,
                [new("FallbackUsed", "player.name", RuleTextTokenSource.Connection, "Used fallback.")]))
            .ReturnsAsync(new RuleActionTextPreparation(resolved, []));
        var engine = Create(storage, executor, textPreparer: preparer.Object);
        var invocation = Invocation();

        await engine.ProcessAsync(invocation, Snapshot(rule), default);

        Assert.Contains(storage.Execution!.Diagnostics, item => item.Code == RuleExecutionDiagnosticCode.FallbackUsed);
        Assert.Contains(storage.Execution.Diagnostics, item => item.Code == RuleExecutionDiagnosticCode.WaitingForContext);
        executor.Deferred = false;
        await engine.ProcessAsync(invocation, Snapshot(rule), default);

        Assert.Empty(storage.DiagnosticsAtClaim!);
        Assert.Empty(storage.Execution.Diagnostics);
        Assert.Equal("Fresh reason", Assert.Single(storage.Execution.Actions).Command.Effect.Reason);
    }

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
            TypeId = "player.kick",
            Parameters = new() { ["reason"] = JsonSerializer.SerializeToElement("Rule matched.") }
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
        var versionAfterEnrichment = storage.Execution.Version;
        await engine.ProcessAsync(original, snapshot, default);

        var progress = JsonSerializer.Deserialize<RuleExecutionProgress>(
            storage.Execution.DecisionSnapshotJson!, RulesSerialization.Options)!;
        Assert.Equal(enrichedTrigger.TriggerOccurrenceId, progress.ContextTrigger!.TriggerOccurrenceId);
        Assert.Equal(enrichedTrigger.PlayerIpAddress, progress.ContextTrigger.PlayerIpAddress);
        Assert.Equal(claimed, Assert.Single(storage.Execution.Actions).Command);
        Assert.Equal(ruleIndex, storage.Execution.RuleIndex);
        Assert.Equal(actionIndex, storage.Execution.ActionIndex);
        Assert.Equal(RuleFeatureExecutionState.AwaitingActionOutcome, storage.Execution.State);
        Assert.Equal(versionAfterEnrichment, storage.Execution.Version);
        Assert.Single(executor.Effects);
        Assert.Empty(enriched.Commands);
    }

    [Fact]
    public async Task PreviouslyClaimedLegacyMessageRemainsPendingWithoutNewDeliveryPlan()
    {
        var storage = new Storage();
        var executor = new Executor();
        var invocation = Invocation();
        var rule = Rule("legacy", 0);
        rule.Actions[0] = new()
        {
            Id = "action",
            TypeId = "message.public",
            Parameters = new() { ["message"] = JsonSerializer.SerializeToElement("Hello") }
        };
        var snapshot = Snapshot(rule);
        var command = new RuleActionCommand
        {
            GameServerId = invocation.Trigger.GameServerId,
            ConnectionId = invocation.Trigger.ConnectionId,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1),
            ActionId = "action",
            LogicalEffectId = new string('a', 64),
            Correlation = new() { RuleId = "legacy", ActionId = "action" },
            Effect = new() { Type = RuleNativeEffectType.PublicMessage, Message = "Hello" }
        };
        storage.Execution = new()
        {
            ExecutionId = Guid.NewGuid(),
            GameServerId = command.GameServerId,
            ConnectionId = command.ConnectionId,
            Trigger = invocation.Trigger,
            FeatureId = snapshot.FeatureId,
            RuleSetRevision = snapshot.Revision,
            FrozenRulesJson = snapshot.RulesJson,
            State = RuleFeatureExecutionState.AwaitingActionOutcome,
            Actions = [new()
            {
                Command = command,
                LogicalEffectId = command.LogicalEffectId,
                Correlation = command.Correlation,
                Outcome = RuleActionExecutionOutcome.Dispatched
            }],
            DecisionSnapshotJson = JsonSerializer.Serialize(new RuleExecutionProgress
            {
                Mode = RuleExecutionMode.Enforce,
                MatchedRuleIds = ["legacy"],
                ContextTrigger = invocation.Trigger,
                ContextObservedAtUtc = invocation.Trigger.OccurredAtUtc
            }, RulesSerialization.Options)
        };

        await Create(storage, executor).ProcessAsync(invocation, snapshot, default);

        Assert.Empty(executor.Effects);
        Assert.Empty(storage.Execution.PreparedActions);
        Assert.Single(storage.Execution.Actions);
        Assert.Equal(RuleFeatureExecutionState.AwaitingActionOutcome, storage.Execution.State);
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
        Storage storage, Executor executor, IRuleExecutionContinuationPublisher? continuation = null, TimeProvider? clock = null,
        IRuleMessageDeliveryService? messages = null, IRuleActionTextPreparer? textPreparer = null) =>
        new(storage.Repository.Object, executor, continuation ?? new Continuations(),
            NullLogger<FeatureExecutionOrchestrator>.Instance, NullLogger<OrderedActionDispatcher>.Instance,
            textPreparer ?? Mock.Of<IRuleActionTextPreparer>(), messages ?? Mock.Of<IRuleMessageDeliveryService>(), clock);

    private static RuleDefinition WelcomeRule(string id, int order)
    {
        var rule = Rule(id, order, "always");
        rule.Condition!.Parameters!.Clear();
        rule.Actions[0].TypeId = "message.public";
        rule.Actions[0].Parameters = new() { ["message"] = JsonSerializer.SerializeToElement("Welcome") };
        return rule;
    }

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
        public List<DateTimeOffset> DueTimes { get; } = [];
        public Task ScheduleAsync(Guid serverId, Guid executionId, DateTimeOffset scheduledAtUtc, CancellationToken ct)
        {
            Scheduled.Add((serverId, executionId));
            DueTimes.Add(scheduledAtUtc);
            return Task.CompletedTask;
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Messages : IRuleMessageDeliveryService
    {
        public List<string> Deliveries { get; } = [];
        public Task<RuleMessageDispatchResult> DispatchAsync(
            RuleFeatureExecutionDto execution, RuleDefinition rule, int ruleIndex, int actionIndex,
            RuleActionDefinition action, Func<RuleActionDefinition, RuleActionCommand> commandFactory,
            RuleInvocation invocation, RuleExecutionProgress progress, CancellationToken ct)
        {
            Assert.Equal(RuleFeatureExecutionState.Evaluating, execution.State);
            Assert.Equal(ruleIndex, execution.RuleIndex);
            Assert.Equal(actionIndex, execution.ActionIndex);
            Deliveries.Add(rule.Id);
            return Task.FromResult(new RuleMessageDispatchResult(execution, new(ActionExecutionOutcome.Applied)));
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
        public Mock<IRuleExecutionsApi> Api { get; } = new();
        public RuleFeatureExecutionDto? Execution { get; set; }
        public bool FailOutcomes { get; set; }
        public bool ClaimAlreadyOwned { get; init; }
        public RuleExecutionDiagnostic[]? DiagnosticsAtClaim { get; private set; }
        public bool MatchObservedAtClaim { get; private set; }
        public RuleExecutionMatchStatus MatchStatusAtClaim { get; private set; }

        public Storage()
        {
            var api = Api;
            var versioned = new Mock<IVersionedRuleExecutionsApi>();
            versioned.Setup(item => item.V1).Returns(api.Object);
            Repository.Setup(item => item.RuleExecutions).Returns(versioned.Object);
            api.Setup(item => item.GetExecution(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(Ok(Execution!)));
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
                        FactSnapshotId = request.FactSnapshotId,
                        Coordination = request.Coordination
                    };
                    return Task.FromResult(Ok(Execution));
                });
            api.Setup(item => item.UpdateProgress(It.IsAny<Guid>(), It.IsAny<Guid>(),
                    It.IsAny<UpdateRuleFeatureExecutionProgressDto>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, Guid _, UpdateRuleFeatureExecutionProgressDto request, CancellationToken _) =>
                {
                    Assert.Equal(Execution!.Version, request.ExpectedVersion);
                    if (request.MatchStatus is not (null or RuleExecutionMatchStatus.Unknown))
                    {
                        Assert.NotNull(request.DecisionSnapshotJson);
                    }
                    if (request.MatchStatus == RuleExecutionMatchStatus.NoMatch)
                    {
                        Assert.Equal(RuleFeatureExecutionState.Completed, request.State);
                        Assert.NotEqual(RuleExecutionMatchStatus.Matched, Execution.MatchStatus);
                    }
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
                        DecisionSnapshotJson = request.DecisionSnapshotJson,
                        MatchStatus = request.MatchStatus is null or RuleExecutionMatchStatus.Unknown
                            ? Execution.MatchStatus : request.MatchStatus.Value,
                        Coordination = request.Coordination,
                        Diagnostics = request.Diagnostics ?? Execution.Diagnostics
                    };
                    return Task.FromResult(Ok(Execution));
                });
            api.Setup(item => item.ClaimAction(It.IsAny<Guid>(), It.IsAny<Guid>(),
                    It.IsAny<ClaimRuleActionDto>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, Guid _, ClaimRuleActionDto request, CancellationToken _) =>
                {
                    Assert.Equal(Execution!.Version, request.ExpectedVersion);
                    DiagnosticsAtClaim = Execution.Diagnostics;
                    MatchObservedAtClaim = Progress(this).MatchEvidence?.ObservedMatch is true;
                    MatchStatusAtClaim = Execution.MatchStatus;
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
