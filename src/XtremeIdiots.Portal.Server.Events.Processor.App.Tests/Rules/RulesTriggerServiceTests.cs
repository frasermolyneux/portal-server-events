using System.Net;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using MX.Api.Abstractions;
using MX.GeoLocation.Api.Client.V1;
using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Rules;

public sealed class RulesTriggerServiceTests
{
    [Fact]
    public async Task MirroredPluginQueueEventDoesNotClaimCommandsWithoutDeliveryTransport()
    {
        var fixture = new Fixture();
        var result = await fixture.Service.ProcessAsync(
            new(fixture.Execution.Trigger, new()), null, default);

        Assert.False(result.SuppressWelcome);
        fixture.Api.VerifyNoOtherCalls();
        fixture.Audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PollRejectsAgentExecutionRatherThanInventingNativeGeneration()
    {
        var fixture = new Fixture();
        fixture.Execution = fixture.Execution with
        {
            Trigger = fixture.Execution.Trigger with
            {
                ConnectionGeneration = null,
                Runtime = fixture.Execution.Trigger.Runtime! with
                {
                    Id = "server-agent",
                    IdentityModel = RuleRuntimeIdentityModel.AgentObservedConnection
                }
            }
        };

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.PollAsync(new()
        {
            GameServerId = fixture.Execution.GameServerId,
            ConnectionId = fixture.Execution.ConnectionId,
            ConnectionGeneration = 1,
            PlayerGuid = "player",
            SlotId = 4,
            ExecutionIds = [fixture.Execution.ExecutionId]
        }, default));

        fixture.Api.Verify(item => item.CreateFactSnapshot(
            It.IsAny<Guid>(), It.IsAny<RuleFactSnapshotRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MismatchedRuleCorrelationCannotCompleteAClaim()
    {
        var fixture = new Fixture();
        var result = fixture.Result(RuleActionExecutionOutcome.Applied);

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.OutcomeAsync(
            result with { Correlation = result.Correlation with { RuleId = "different-rule" } }, default));

        fixture.Api.Verify(item => item.UpdateActionOutcome(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<UpdateRuleActionOutcomeDto>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConfirmedDropDoesNotClaimContinuingBanEnforcementAndDuplicateDoesNotReaudit()
    {
        var fixture = new Fixture();
        var result = fixture.Result(RuleActionExecutionOutcome.Applied);

        await fixture.Service.OutcomeAsync(result, default);
        await fixture.Service.OutcomeAsync(result, default);

        var action = Assert.Single(fixture.Execution.Actions);
        Assert.Equal(RuleActionExecutionOutcome.OutcomeUnknown, action.Outcome);
        Assert.Contains("ongoing ban-cache enforcement is not confirmed", action.Detail);
        fixture.Audit.Verify(item => item.LogAudit(It.IsAny<AuditEvent>()), Times.Once);
        fixture.Api.Verify(item => item.UpdateActionOutcome(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<UpdateRuleActionOutcomeDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FailedNativeEffectNeverEmitsAppliedAudit()
    {
        var fixture = new Fixture();

        await fixture.Service.OutcomeAsync(fixture.Result(RuleActionExecutionOutcome.Failed), default);

        Assert.Equal(RuleActionExecutionOutcome.Failed, Assert.Single(fixture.Execution.Actions).Outcome);
        fixture.Audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task VerifiedLateNativeEvidenceDoesNotRewindOrResumeTheExecution()
    {
        var fixture = new Fixture();
        var action = fixture.Execution.Actions[0];
        action = action with
        {
            ActionTypeId = "player.kick",
            Outcome = RuleActionExecutionOutcome.OutcomeUnknown,
            ClaimedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            Command = action.Command with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-10) }
        };
        fixture.Execution = fixture.Execution with
        {
            State = RuleFeatureExecutionState.Stopped,
            RuleIndex = 2,
            ActionIndex = 5,
            Actions = [action]
        };
        var result = fixture.Result(RuleActionExecutionOutcome.Applied) with
        {
            OccurredAtUtc = DateTimeOffset.UtcNow.AddSeconds(-20)
        };

        await fixture.Service.OutcomeAsync(result, default);
        await fixture.Service.OutcomeAsync(result, default);

        Assert.Equal(RuleFeatureExecutionState.Stopped, fixture.Execution.State);
        Assert.Equal(2, fixture.Execution.RuleIndex);
        Assert.Equal(5, fixture.Execution.ActionIndex);
        Assert.Equal(RuleActionExecutionOutcome.Applied, Assert.Single(fixture.Execution.Actions).Outcome);
        fixture.Api.Verify(item => item.CreateFactSnapshot(
            It.IsAny<Guid>(), It.IsAny<RuleFactSnapshotRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Audit.Verify(item => item.LogAudit(It.IsAny<AuditEvent>()), Times.Once);
    }

    [Fact]
    public async Task FailureAfterStartRetainsItsPollIdAlongsideHealthyCommands()
    {
        var failedId = Guid.NewGuid();
        var healthyId = Guid.NewGuid();
        var broken = new Feature("vpn-protection", invocation =>
        {
            invocation.ExecutionIds["vpn-protection"] = failedId;
            throw new InvalidOperationException("Cursor write failed after starting.");
        });
        var working = new Feature("siege-mode", invocation =>
        {
            invocation.Commands.Add(new() { ExecutionId = healthyId });
            invocation.PendingExecutionIds.Add(healthyId);
            return healthyId;
        });
        var fixture = new Fixture(broken, working);

        var response = await fixture.Service.EvaluateAsync(fixture.Execution.Trigger, default);

        Assert.Equal(healthyId, Assert.Single(response.Commands).ExecutionId);
        Assert.Contains(failedId, response.ExecutionIds);
        Assert.Contains(healthyId, response.ExecutionIds);
        fixture.Api.Verify(item => item.GetExecution(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProviderFailureDuringPollCannotLoseKnownExecution()
    {
        var fixture = new Fixture(new Feature("vpn-protection", _ => throw new InvalidOperationException("Settings unavailable.")));

        var response = await fixture.Service.PollAsync(fixture.PollRequest(), default);

        Assert.Equal(fixture.Execution.ExecutionId, Assert.Single(response.ExecutionIds));
        Assert.Empty(response.Commands);
    }

    [Fact]
    public async Task InactiveFeatureKeepsAwaitingAlreadyDispatchedCommandUntilExpiry()
    {
        var fixture = new Fixture(new Feature("vpn-protection", _ => null));

        var response = await fixture.Service.PollAsync(fixture.PollRequest(), default);

        Assert.Equal(fixture.Execution.ExecutionId, Assert.Single(response.ExecutionIds));
        Assert.Equal(RuleActionExecutionOutcome.Dispatched, Assert.Single(fixture.Execution.Actions).Outcome);
        fixture.Api.Verify(item => item.UpdateActionOutcome(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<UpdateRuleActionOutcomeDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InactiveFeatureFinalizesExpiredClaimWithoutReplayingEffects()
    {
        var fixture = new Fixture(new Feature("vpn-protection", _ => null));
        var action = fixture.Execution.Actions[0];
        fixture.Execution = fixture.Execution with
        {
            Actions = [action with { Command = action.Command with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1) } }]
        };

        var response = await fixture.Service.PollAsync(fixture.PollRequest(), default);

        Assert.Empty(response.ExecutionIds);
        Assert.Empty(response.Commands);
        Assert.Equal(RuleActionExecutionOutcome.OutcomeUnknown, Assert.Single(fixture.Execution.Actions).Outcome);
        Assert.Equal(RuleFeatureExecutionState.Stopped, fixture.Execution.State);
        fixture.Audit.VerifyNoOtherCalls();
    }

    private sealed class Feature(string featureId, Func<RuleInvocation, Guid?> process) : IRegisteredRuleFeature
    {
        public string FeatureId => featureId;
        public IReadOnlySet<string> TriggerIds { get; } = new HashSet<string> { "player.connected" };
        public Task<Guid?> ProcessAsync(RuleInvocation invocation, CancellationToken ct) =>
            Task.FromResult(process(invocation));
    }

    private static ApiResult<T> Ok<T>(T value) => new(HttpStatusCode.OK, new ApiResponse<T>(value));

    private sealed class Fixture
    {
        public Mock<IRuleExecutionsApi> Api { get; } = new();
        public Mock<IAuditLogger> Audit { get; } = new();
        public RulesTriggerService Service { get; }
        public RuleFeatureExecutionDto Execution { get; set; }

        public Fixture(params IRegisteredRuleFeature[] features)
        {
            var trigger = new RuleTriggerEnvelope
            {
                GameServerId = Guid.NewGuid(),
                ObservationId = Guid.NewGuid(),
                TriggerOccurrenceId = Guid.NewGuid(),
                TriggerId = "player.connected",
                OccurredAtUtc = DateTimeOffset.UtcNow,
                ConnectionId = Guid.NewGuid(),
                PlayerGuid = "player",
                SlotId = 4,
                GameType = GameType.CallOfDuty4x,
                ConnectionGeneration = 1,
                Runtime = new()
                {
                    Id = "cod4x-plugin",
                    Version = "1.0",
                    IdentityModel = RuleRuntimeIdentityModel.PluginRuntimeGeneration,
                    InstanceId = Guid.NewGuid(),
                    StartedAtUtc = DateTimeOffset.UtcNow.AddHours(-1)
                }
            };
            var command = new RuleActionCommand
            {
                ExecutionId = Guid.NewGuid(),
                GameServerId = trigger.GameServerId,
                Runtime = trigger.Runtime,
                Correlation = new()
                {
                    ConnectionId = trigger.ConnectionId,
                    RuleId = "rule",
                    ActionId = "action",
                    FeatureId = "vpn-protection"
                },
                ActionId = "action",
                LogicalEffectId = "effect",
                Mode = RuleExecutionMode.Enforce,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(30),
                Effect = new() { Type = RuleNativeEffectType.DropPlayer }
            };
            Execution = new()
            {
                ExecutionId = command.ExecutionId,
                GameServerId = trigger.GameServerId,
                ConnectionId = trigger.ConnectionId,
                Trigger = trigger,
                FeatureId = "vpn-protection",
                State = RuleFeatureExecutionState.AwaitingActionOutcome,
                Actions =
                [
                    new()
                    {
                        Command = command,
                        ExecutionId = command.ExecutionId,
                        GameServerId = trigger.GameServerId,
                        Correlation = command.Correlation,
                        LogicalEffectId = command.LogicalEffectId,
                        ActionTypeId = "moderation.temporary-ban",
                        Mode = RuleExecutionMode.Enforce,
                        Outcome = RuleActionExecutionOutcome.Dispatched,
                        ClaimedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1)
                    }
                ]
            };
            var repository = new Mock<IRepositoryApiClient>();
            var versioned = new Mock<IVersionedRuleExecutionsApi>();
            versioned.Setup(item => item.V1).Returns(Api.Object);
            repository.Setup(item => item.RuleExecutions).Returns(versioned.Object);
            Api.Setup(item => item.GetExecution(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(Ok(Execution)));
            Api.Setup(item => item.CreateFactSnapshot(It.IsAny<Guid>(), It.IsAny<RuleFactSnapshotRequestDto>(), It.IsAny<CancellationToken>()))
                .Returns((Guid serverId, RuleFactSnapshotRequestDto request, CancellationToken _) => Task.FromResult(Ok(new RuleFactSnapshotDto
                {
                    SnapshotId = Guid.NewGuid(),
                    GameServerId = serverId,
                    ConnectionId = request.ConnectionId,
                    PlayerGuid = request.PlayerGuid,
                    GameType = request.GameType
                })));
            var persisted = false;
            Api.Setup(item => item.UpdateActionOutcome(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                    It.IsAny<UpdateRuleActionOutcomeDto>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, Guid _, string _, UpdateRuleActionOutcomeDto request, CancellationToken _) =>
                {
                    var action = Execution.Actions[0] with { Outcome = request.Result.Outcome, Detail = request.Result.Detail };
                    Execution = Execution with
                    {
                        Version = Execution.Version + 1,
                        Actions = [action],
                        State = request.NextState,
                        ActionIndex = request.NextActionIndex
                    };
                    persisted = true;
                    return Task.FromResult(Ok(new RuleActionClaimResultDto { Action = action, Execution = Execution }));
                });
            Audit.Setup(item => item.LogAudit(It.IsAny<AuditEvent>())).Callback(() => Assert.True(persisted));
            Service = new(repository.Object, Mock.Of<IGeoLocationApiClient>(),
                new RegisteredFeatureHost(features, NullLogger<RegisteredFeatureHost>.Instance),
                NullLogger<RulesTriggerService>.Instance, Audit.Object);
        }

        public RulePollRequest PollRequest() => new()
        {
            GameServerId = Execution.GameServerId,
            ConnectionId = Execution.ConnectionId,
            ConnectionGeneration = 1,
            PlayerGuid = "player",
            SlotId = 4,
            ExecutionIds = [Execution.ExecutionId]
        };

        public RuleActionResult Result(RuleActionExecutionOutcome outcome)
        {
            var command = Execution.Actions[0].Command;
            return new()
            {
                GameServerId = command.GameServerId,
                ExecutionId = command.ExecutionId,
                ActionId = command.ActionId,
                LogicalEffectId = command.LogicalEffectId,
                Correlation = command.Correlation,
                Outcome = outcome,
                OccurredAtUtc = DateTimeOffset.UtcNow
            };
        }
    }
}
