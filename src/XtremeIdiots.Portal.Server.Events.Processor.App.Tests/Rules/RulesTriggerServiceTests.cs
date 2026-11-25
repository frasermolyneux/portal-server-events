using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using MX.Api.Abstractions;
using MX.Api.Client;
using MX.GeoLocation.Api.Client.V1;
using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.GameServers;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Rules;

public sealed class RulesTriggerServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IpEnrichmentResumesOnlyAnExistingPendingWelcome(bool pending)
    {
        var calls = 0;
        var welcome = new Feature(WelcomeFeatureRulesProvider.Id, _ =>
        {
            calls++;
            return Guid.NewGuid();
        }, ["player.connected"]);
        var fixture = new Fixture(welcome);
        fixture.Api.Setup(item => item.GetConnectionExecutions(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new[]
            {
                fixture.Execution with
                {
                    FeatureId = WelcomeFeatureRulesProvider.Id,
                    State = pending ? RuleFeatureExecutionState.Waiting : RuleFeatureExecutionState.Completed
                }
            }));
        var trigger = fixture.Execution.Trigger with
        {
            TriggerId = "player.ip-resolved",
            TriggerOccurrenceId = Guid.NewGuid(),
            ObservationId = Guid.NewGuid(),
            PlayerIpAddress = "8.8.8.8"
        };

        await fixture.Service.EvaluateAsync(trigger, default);

        Assert.Equal(pending ? 1 : 0, calls);
    }

    [Theory]
    [InlineData(RuleActionExecutionOutcome.Dispatched, RuleActionExecutionOutcome.OutcomeUnknown)]
    [InlineData(RuleActionExecutionOutcome.Applied, RuleActionExecutionOutcome.Applied)]
    [InlineData(RuleActionExecutionOutcome.Stale, RuleActionExecutionOutcome.Stale)]
    [InlineData(RuleActionExecutionOutcome.Failed, RuleActionExecutionOutcome.Failed)]
    public async Task PreparedChildReceiptDoesNotAdvanceTheParentActionCursor(
        RuleActionExecutionOutcome receipt, RuleActionExecutionOutcome expected)
    {
        var fixture = new Fixture();
        var action = fixture.Execution.Actions[0];
        fixture.Execution = fixture.Execution with
        {
            ActionIndex = 3,
            Actions = [action with
            {
                ActionTypeId = "message.private",
                AdminActionId = null,
                Command = action.Command with
                {
                    Effect = new() { Type = RuleNativeEffectType.PrivateMessage, Message = "Hello" },
                    DeliveryIdentity = new() { ParentLogicalEffectId = new string('a', 64) }
                }
            }]
        };
        var result = fixture.Result(receipt);

        await fixture.Service.OutcomeAsync(result, default);
        await fixture.Service.OutcomeAsync(result, default);

        Assert.Equal(3, fixture.Execution.ActionIndex);
        Assert.Equal(expected, fixture.Execution.Actions[0].Outcome);
        Assert.Equal(RuleFeatureExecutionState.Evaluating, fixture.Execution.State);
        fixture.Api.Verify(item => item.UpdateActionOutcome(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<UpdateRuleActionOutcomeDto>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.EventApi.Verify(item => item.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(fixture.Logger.Entries, item => item.Level == LogLevel.Information &&
            item.Message.Contains("Prepared message receipt"));
        fixture.Audit.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("::ffff:8.8.8.8", "8.8.8.8")]
    [InlineData("2001:0DB8:0000:0000:0000:0000:0000:0001", "2001:db8::1")]
    public async Task PluginFactsUseCanonicalIpBeforeValidatingRepositoryResponse(string address, string expected)
    {
        var fixture = new Fixture();

        await fixture.Service.EvaluateAsync(fixture.Execution.Trigger with { PlayerIpAddress = address }, default);

        fixture.Api.Verify(item => item.CreateFactSnapshot(It.IsAny<Guid>(),
            It.Is<RuleFactSnapshotRequestDto>(request => request.PlayerIpAddress == expected),
            It.IsAny<CancellationToken>()), Times.Once);
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PluginConnectionWithoutTopLevelObservationSkipsMirrorButEvaluatesNatively(bool emptyObservation)
    {
        var featureCalls = 0;
        var fixture = new Fixture(new Feature("vpn-protection", _ =>
        {
            featureCalls++;
            return Guid.NewGuid();
        }));
        var trigger = fixture.Execution.Trigger;
        var json = JsonSerializer.SerializeToNode(ConnectedEvent(trigger), JsonOptions.Default)!.AsObject();
        json.Remove("observationId");
        json["playtime"] = new JsonObject { ["observationId"] = Guid.NewGuid().ToString() };
        if (emptyObservation)
        {
            json["observationId"] = Guid.Empty.ToString();
        }
        var source = json.Deserialize<PlayerConnectedEvent>(JsonOptions.Default)!;

        var prepared = await fixture.Service.PrepareAsync(source, default);
        await fixture.Service.ProcessAsync(prepared, null, default);

        Assert.Null(prepared);
        Assert.Equal(0, featureCalls);
        Assert.DoesNotContain(fixture.Logger.Entries, item => item.Level == LogLevel.Warning);
        fixture.Api.Verify(item => item.CreateFactSnapshot(
            It.IsAny<Guid>(), It.IsAny<RuleFactSnapshotRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);

        await fixture.Service.EvaluateAsync(trigger, default);

        Assert.Equal(1, featureCalls);
        fixture.Api.Verify(item => item.CreateFactSnapshot(
            It.IsAny<Guid>(), It.IsAny<RuleFactSnapshotRequestDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PluginConnectionWithObservationStillCapturesFactsWithoutEvaluatingTheMirror()
    {
        var featureCalls = 0;
        var fixture = new Fixture(new Feature("vpn-protection", _ =>
        {
            featureCalls++;
            return Guid.NewGuid();
        }));
        var trigger = fixture.Execution.Trigger;

        var prepared = await fixture.Service.PrepareAsync(ConnectedEvent(trigger, trigger.ObservationId), default);
        await fixture.Service.ProcessAsync(prepared, null, default);

        Assert.NotNull(prepared);
        Assert.Equal(trigger.ObservationId, prepared.Trigger.ObservationId);
        Assert.Equal(0, featureCalls);

        await fixture.Service.EvaluateAsync(trigger, default);

        Assert.Equal(1, featureCalls);
        fixture.Api.Verify(item => item.CreateFactSnapshot(
            It.IsAny<Guid>(), It.IsAny<RuleFactSnapshotRequestDto>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentConnectionStillRequiresObservationBeforeEvaluatingRules(bool hasObservation)
    {
        var featureCalls = 0;
        var fixture = new Fixture(new Feature("vpn-protection", _ =>
        {
            featureCalls++;
            return Guid.NewGuid();
        }));
        var source = ConnectedEvent(fixture.Execution.Trigger,
            observationId: hasObservation ? Guid.NewGuid() : null,
            connectionGeneration: null,
            runtimeId: "server-agent");

        var prepared = await fixture.Service.PrepareAsync(source, default);
        await fixture.Service.ProcessAsync(prepared, null, default);

        if (hasObservation)
        {
            Assert.NotNull(prepared);
            Assert.Equal(1, featureCalls);
            fixture.Api.Verify(item => item.CreateFactSnapshot(
                It.IsAny<Guid>(), It.IsAny<RuleFactSnapshotRequestDto>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        else
        {
            Assert.Null(prepared);
            Assert.Equal(0, featureCalls);
            Assert.Contains(fixture.Logger.Entries, item => item.Level == LogLevel.Warning &&
                item.Message.Contains("without observation evidence"));
            fixture.Api.Verify(item => item.CreateFactSnapshot(
                It.IsAny<Guid>(), It.IsAny<RuleFactSnapshotRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("generation")]
    [InlineData("runtime")]
    [InlineData("future")]
    [InlineData("unmarked")]
    [InlineData("not-evaluated")]
    public async Task IncompletePluginConnectionStillWarns(string invalidEvidence)
    {
        var fixture = new Fixture();
        var source = ConnectedEvent(fixture.Execution.Trigger,
            connectionId: invalidEvidence == "connection" ? Guid.Empty : null,
            connectionGeneration: invalidEvidence == "generation" ? 0UL : 1UL,
            runtimeId: invalidEvidence == "runtime" ? null : "cod4x-plugin",
            eventGeneratedUtc: invalidEvidence == "future" ? DateTime.UtcNow.AddMinutes(2) : null,
            rulesEvaluatedByPlugin: invalidEvidence == "unmarked" ? null : invalidEvidence != "not-evaluated");

        var prepared = await fixture.Service.PrepareAsync(source, default);

        Assert.Null(prepared);
        Assert.Contains(fixture.Logger.Entries, item => item.Level == LogLevel.Warning);
        fixture.Api.Verify(item => item.CreateFactSnapshot(
            It.IsAny<Guid>(), It.IsAny<RuleFactSnapshotRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task PluginNativeEvaluationStillRejectsMissingObservation(bool hasObservation, bool hasOccurrence)
    {
        var fixture = new Fixture();
        var trigger = fixture.Execution.Trigger with
        {
            ObservationId = hasObservation ? Guid.NewGuid() : Guid.Empty,
            TriggerOccurrenceId = hasOccurrence ? Guid.NewGuid() : Guid.Empty
        };

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.EvaluateAsync(trigger, default));

        fixture.Api.Verify(item => item.CreateFactSnapshot(
            It.IsAny<Guid>(), It.IsAny<RuleFactSnapshotRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PluginIpResolvedEventStillWarnsWhenObservationIsMissing()
    {
        var fixture = new Fixture();
        var connected = ConnectedEvent(fixture.Execution.Trigger);
        var source = new PlayerIpResolvedEvent
        {
            ServerId = connected.ServerId,
            GameType = connected.GameType,
            SequenceId = connected.SequenceId,
            EventGeneratedUtc = connected.EventGeneratedUtc,
            EventPublishedUtc = connected.EventPublishedUtc,
            PlayerGuid = connected.PlayerGuid,
            IpAddress = connected.IpAddress,
            SlotId = connected.SlotId,
            ConnectionId = connected.ConnectionId,
            ConnectionGeneration = connected.ConnectionGeneration,
            Runtime = connected.Runtime
        };

        var prepared = await fixture.Service.PrepareAsync(source, default);

        Assert.Null(prepared);
        Assert.Contains(fixture.Logger.Entries, item => item.Level == LogLevel.Warning &&
            item.Message.Contains("without observation evidence"));
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
        fixture.EventApi.Verify(item => item.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
        fixture.Audit.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("generation")]
    [InlineData("connection")]
    [InlineData("command")]
    public async Task ClockSkewAllowanceCannotBypassPersistedCommandIdentity(string mismatch)
    {
        var fixture = new Fixture();
        var action = fixture.Execution.Actions[0];
        var command = mismatch switch
        {
            "runtime" => action.Command with { Runtime = action.Command.Runtime with { InstanceId = Guid.NewGuid() } },
            "generation" => action.Command with { ConnectionGeneration = action.Command.ConnectionGeneration + 1 },
            "connection" => action.Command with { ConnectionId = Guid.NewGuid() },
            _ => action.Command with { ExecutionId = Guid.NewGuid() }
        };
        var result = fixture.Result(RuleActionExecutionOutcome.Applied) with
        {
            OccurredAtUtc = action.ClaimedAtUtc.AddSeconds(-5)
        };
        fixture.Execution = fixture.Execution with { Actions = [action with { Command = command }] };

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.OutcomeAsync(result, default));

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

    [Theory]
    [InlineData(-5)]
    [InlineData(5)]
    public async Task CorrelatedNativeEvidenceAllowsExistingClockSkewWithoutChangingSourceTime(int skewSeconds)
    {
        var fixture = new Fixture();
        var result = fixture.Result(RuleActionExecutionOutcome.Applied) with
        {
            OccurredAtUtc = fixture.Execution.Actions[0].ClaimedAtUtc.AddSeconds(skewSeconds)
        };

        await fixture.Service.OutcomeAsync(result, default);
        await fixture.Service.OutcomeAsync(result, default);

        Assert.Equal(result, Assert.Single(fixture.Execution.Actions).NativeEvidence);
        fixture.Api.Verify(item => item.UpdateActionOutcome(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<UpdateRuleActionOutcomeDto>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Audit.Verify(item => item.LogAudit(It.IsAny<AuditEvent>()), Times.Once);
    }

    [Theory]
    [InlineData("player.kick", false)]
    [InlineData("moderation.kick-with-record", true)]
    [InlineData("moderation.ban", true)]
    [InlineData("moderation.temporary-ban", true)]
    public async Task PluginKickDispatchReceiptCompletesKickWithoutClaimingBanEnforcement(string actionType, bool hasRecord)
    {
        var fixture = new Fixture();
        var action = fixture.Execution.Actions[0];
        fixture.Execution = fixture.Execution with
        {
            Actions = [action with { ActionTypeId = actionType, AdminActionId = hasRecord ? action.AdminActionId : null }]
        };
        var receipt = fixture.Result(RuleActionExecutionOutcome.Dispatched) with
        {
            Detail = "Native API invoked; effect is not confirmed"
        };

        await fixture.Service.OutcomeAsync(receipt, default);
        var version = fixture.Execution.Version;
        var replay = await fixture.Service.OutcomeAsync(receipt, default);

        var updated = Assert.Single(fixture.Execution.Actions);
        var compositeBan = actionType is "moderation.ban" or "moderation.temporary-ban";
        Assert.Equal(compositeBan ? RuleActionExecutionOutcome.OutcomeUnknown : RuleActionExecutionOutcome.Applied, updated.Outcome);
        Assert.Equal(RuleFeatureExecutionState.Evaluating, fixture.Execution.State);
        Assert.Equal(1, fixture.Execution.ActionIndex);
        Assert.Equal(version, fixture.Execution.Version);
        Assert.Empty(replay.Commands);
        Assert.Equal(hasRecord ? action.AdminActionId : null, updated.AdminActionId);
        if (compositeBan)
        {
            Assert.Contains("ongoing ban-cache enforcement is not confirmed", updated.Detail);
            Assert.Equal(RuleActionExecutionOutcome.Applied, updated.NativeEvidence!.Outcome);
            Assert.Equal(receipt.OccurredAtUtc, updated.NativeEvidence.OccurredAtUtc);
        }
        else
        {
            Assert.Equal("Native kick API invoked.", updated.Detail);
            Assert.Equal(receipt.OccurredAtUtc, JsonSerializer.Deserialize<RuleActionResult>(
                updated.ResultJson!, RulesSerialization.Options)!.OccurredAtUtc);
        }
        fixture.Audit.Verify(item => item.LogAudit(It.IsAny<AuditEvent>()), Times.Once);
        fixture.Api.Verify(item => item.UpdateActionOutcome(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<UpdateRuleActionOutcomeDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("player.kick", false)]
    [InlineData("moderation.ban", true)]
    public async Task KickReceiptsWriteIdempotentNativeEventWithoutClaimingBanEnforcement(string actionType, bool hasRecord)
    {
        var fixture = new Fixture();
        var action = fixture.Execution.Actions[0];
        fixture.Execution = fixture.Execution with
        {
            Actions = [action with { ActionTypeId = actionType, AdminActionId = hasRecord ? action.AdminActionId : null }]
        };
        var receipt = fixture.Result(RuleActionExecutionOutcome.Dispatched);

        await fixture.Service.OutcomeAsync(receipt, default);
        await fixture.Service.OutcomeAsync(receipt, default);

        Assert.Equal(2, fixture.Events.Count);
        var first = fixture.Events[0];
        Assert.Equal("NativeAction.DropPlayer.Plugin.Applied", first.EventType);
        Assert.Equal(Guid.Parse("0befef6f-1115-39c5-4d7a-cc1e297417f1"), first.IdempotencyKey);
        Assert.Equal(first.IdempotencyKey, fixture.Events[1].IdempotencyKey);
        Assert.Equal(first.EventData, fixture.Events[1].EventData);
        using var data = JsonDocument.Parse(first.EventData);
        Assert.Equal("Plugin", data.RootElement.GetProperty("source").GetString());
        Assert.Equal("Applied", data.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(actionType, data.RootElement.GetProperty("actionType").GetString());
        Assert.Equal(hasRecord ? RuleActionExecutionOutcome.OutcomeUnknown : RuleActionExecutionOutcome.Applied,
            Assert.Single(fixture.Execution.Actions).Outcome);
    }

    [Theory]
    [InlineData(RuleNativeEffectType.PublicMessage, "message.public")]
    [InlineData(RuleNativeEffectType.PrivateMessage, "message.private")]
    public async Task MessageReceiptRecordsDispatchWithoutClaimingDelivery(RuleNativeEffectType effect, string actionType)
    {
        var fixture = new Fixture();
        var action = fixture.Execution.Actions[0];
        fixture.Execution = fixture.Execution with
        {
            Actions = [action with
            {
                ActionTypeId = actionType,
                AdminActionId = null,
                Command = action.Command with { Effect = new() { Type = effect, Message = "Hello" } }
            }]
        };
        var receipt = fixture.Result(RuleActionExecutionOutcome.Dispatched);

        await fixture.Service.OutcomeAsync(receipt, default);
        await fixture.Service.OutcomeAsync(receipt, default);

        Assert.Equal($"NativeAction.{effect}.Plugin.Dispatched", fixture.Events[0].EventType);
        Assert.Equal(fixture.Events[0].IdempotencyKey, fixture.Events[1].IdempotencyKey);
        Assert.Equal(RuleActionExecutionOutcome.Dispatched, Assert.Single(fixture.Execution.Actions).Outcome);
        using var data = JsonDocument.Parse(fixture.Events[0].EventData);
        Assert.Equal("Dispatched", data.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("Hello", data.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task EventWriteFailureDoesNotRetryOrUndoNativeAction()
    {
        var fixture = new Fixture();
        fixture.EventApi.Setup(item => item.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Event store unavailable"));
        var receipt = fixture.Result(RuleActionExecutionOutcome.Dispatched);

        await fixture.Service.OutcomeAsync(receipt, default);

        Assert.Equal(RuleActionExecutionOutcome.OutcomeUnknown, Assert.Single(fixture.Execution.Actions).Outcome);
        Assert.Equal(RuleActionExecutionOutcome.Applied, Assert.Single(fixture.Execution.Actions).NativeEvidence!.Outcome);
        fixture.Api.Verify(item => item.UpdateActionOutcome(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<UpdateRuleActionOutcomeDto>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(fixture.Logger.Entries, item => item.Level == LogLevel.Warning &&
            item.Message.Contains("Failed to record native plugin receipt") && item.Exception is HttpRequestException);
    }

    [Fact]
    public async Task ConflictingEventIsReportedWithoutRetryingNativeAction()
    {
        var fixture = new Fixture();
        fixture.EventApi.Setup(item => item.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ApiHttpRequestException("Conflict", HttpStatusCode.Conflict, null));
        var receipt = fixture.Result(RuleActionExecutionOutcome.Dispatched);

        await fixture.Service.OutcomeAsync(receipt, default);

        Assert.Equal(RuleActionExecutionOutcome.Applied, Assert.Single(fixture.Execution.Actions).NativeEvidence!.Outcome);
        fixture.EventApi.Verify(item => item.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(fixture.Logger.Entries, item => item.Level == LogLevel.Error &&
            item.Message.Contains("conflicts with existing event") && item.Exception is ApiHttpRequestException);
    }

    [Fact]
    public async Task FailedPluginReceiptIsRecordedWithoutClaimingSuccessfulEffect()
    {
        var fixture = new Fixture();
        var receipt = fixture.Result(RuleActionExecutionOutcome.Failed);

        await fixture.Service.OutcomeAsync(receipt, default);

        var entry = Assert.Single(fixture.Events);
        Assert.Equal("NativeAction.DropPlayer.Plugin.Failed", entry.EventType);
        using var data = JsonDocument.Parse(entry.EventData);
        Assert.Equal("Failed", data.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(RuleActionExecutionOutcome.Failed, Assert.Single(fixture.Execution.Actions).Outcome);
    }

    [Theory]
    [InlineData(RuleActionExecutionOutcome.SkippedUnsupported, "Unsupported")]
    [InlineData(RuleActionExecutionOutcome.OutcomeUnknown, "OutcomeUnknown")]
    public async Task LongestMessageOutcomeNamesFitServerEventContract(
        RuleActionExecutionOutcome outcome, string suffix)
    {
        var fixture = new Fixture();
        var action = fixture.Execution.Actions[0];
        fixture.Execution = fixture.Execution with
        {
            Actions = [action with
            {
                ActionTypeId = "message.private",
                AdminActionId = null,
                Command = action.Command with { Effect = new() { Type = RuleNativeEffectType.PrivateMessage } }
            }]
        };

        await fixture.Service.OutcomeAsync(fixture.Result(outcome), default);

        var entry = Assert.Single(fixture.Events);
        Assert.Equal($"NativeAction.PrivateMessage.Plugin.{suffix}", entry.EventType);
        Assert.True(entry.EventType.Length <= 50);
        using var data = JsonDocument.Parse(entry.EventData);
        Assert.Equal(outcome.ToString(), data.RootElement.GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LatePluginKickDispatchReceiptImprovesEvidenceWithoutResuming(bool compositeBan)
    {
        var fixture = new Fixture();
        var action = fixture.Execution.Actions[0];
        fixture.Execution = fixture.Execution with
        {
            State = RuleFeatureExecutionState.Stopped,
            RuleIndex = 2,
            ActionIndex = 5,
            Actions =
            [
                action with
                {
                    ActionTypeId = compositeBan ? "moderation.temporary-ban" : "player.kick",
                    AdminActionId = compositeBan ? action.AdminActionId : null,
                    Outcome = RuleActionExecutionOutcome.OutcomeUnknown,
                    ClaimedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Command = action.Command with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-10) },
                    ResultJson = JsonSerializer.Serialize(fixture.Result(RuleActionExecutionOutcome.OutcomeUnknown),
                        RulesSerialization.Options)
                }
            ]
        };
        var receipt = fixture.Result(RuleActionExecutionOutcome.Dispatched) with
        {
            OccurredAtUtc = DateTimeOffset.UtcNow.AddSeconds(-20)
        };

        await fixture.Service.OutcomeAsync(receipt, default);
        await fixture.Service.OutcomeAsync(receipt, default);

        var updated = Assert.Single(fixture.Execution.Actions);
        Assert.Equal(compositeBan ? RuleActionExecutionOutcome.OutcomeUnknown : RuleActionExecutionOutcome.Applied, updated.Outcome);
        Assert.Equal(RuleFeatureExecutionState.Stopped, fixture.Execution.State);
        Assert.Equal(2, fixture.Execution.RuleIndex);
        Assert.Equal(5, fixture.Execution.ActionIndex);
        Assert.Equal(2, fixture.Events.Count);
        Assert.Equal(fixture.Events[0].IdempotencyKey, fixture.Events[1].IdempotencyKey);
        Assert.Equal(fixture.Events[0].EventData, fixture.Events[1].EventData);
        if (compositeBan)
        {
            Assert.Equal(RuleActionExecutionOutcome.Applied, updated.NativeEvidence!.Outcome);
        }
        fixture.Api.Verify(item => item.UpdateActionOutcome(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<UpdateRuleActionOutcomeDto>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Api.Verify(item => item.CreateFactSnapshot(
            It.IsAny<Guid>(), It.IsAny<RuleFactSnapshotRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchReceiptOutsideKickCommandWindowIsRejected()
    {
        var fixture = new Fixture();
        var action = fixture.Execution.Actions[0];
        fixture.Execution = fixture.Execution with
        {
            Actions = [action with { Command = action.Command with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1) } }]
        };
        var receipt = fixture.Result(RuleActionExecutionOutcome.Dispatched) with
        {
            OccurredAtUtc = fixture.Execution.Actions[0].Command.ExpiresAtUtc.AddSeconds(30.5)
        };

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.OutcomeAsync(receipt, default));

        fixture.Api.Verify(item => item.UpdateActionOutcome(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<UpdateRuleActionOutcomeDto>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DispatchReceiptCannotUseNonPluginIdentityModel()
    {
        var fixture = new Fixture();
        var action = fixture.Execution.Actions[0];
        var runtime = action.Command.Runtime with { IdentityModel = RuleRuntimeIdentityModel.AgentObservedConnection };
        fixture.Execution = fixture.Execution with
        {
            Trigger = fixture.Execution.Trigger with { Runtime = runtime },
            Actions = [action with { Command = action.Command with { Runtime = runtime } }]
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.OutcomeAsync(fixture.Result(RuleActionExecutionOutcome.Dispatched), default));

        fixture.Api.Verify(item => item.UpdateActionOutcome(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<UpdateRuleActionOutcomeDto>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Audit.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(RuleActionExecutionOutcome.Failed)]
    [InlineData(RuleActionExecutionOutcome.Stale)]
    [InlineData(RuleActionExecutionOutcome.OutcomeUnknown)]
    public async Task UnsuccessfulPluginKickReceiptIsNotConvertedToSuccess(RuleActionExecutionOutcome outcome)
    {
        var fixture = new Fixture();
        var action = fixture.Execution.Actions[0];
        fixture.Execution = fixture.Execution with
        {
            Actions = [action with { ActionTypeId = "player.kick", AdminActionId = null }]
        };

        await fixture.Service.OutcomeAsync(fixture.Result(outcome), default);

        Assert.Equal(outcome, Assert.Single(fixture.Execution.Actions).Outcome);
        fixture.Audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CachedDispatchedReceiptKeepsOriginalTimeAndCannotDispatchOrAdvanceAgain()
    {
        var fixture = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var action = fixture.Execution.Actions[0];
        fixture.Execution = fixture.Execution with
        {
            Actions =
            [
                action with
                {
                    ActionTypeId = "message.public",
                    AdminActionId = null,
                    ClaimedAtUtc = now.AddMinutes(-3),
                    Command = action.Command with { Effect = new() { Type = RuleNativeEffectType.PublicMessage } }
                }
            ],
            ActionIndex = 4
        };
        var cached = fixture.Result(RuleActionExecutionOutcome.Dispatched) with
        {
            OccurredAtUtc = now.AddMinutes(-3).AddSeconds(-5)
        };

        await fixture.Service.OutcomeAsync(cached, default);
        var version = fixture.Execution.Version;
        var replay = await fixture.Service.OutcomeAsync(cached, default);

        Assert.Equal(4, fixture.Execution.ActionIndex);
        Assert.Equal(version, fixture.Execution.Version);
        Assert.Empty(replay.Commands);
        Assert.Equal(cached, JsonSerializer.Deserialize<RuleActionResult>(
            Assert.Single(fixture.Execution.Actions).ResultJson!, RulesSerialization.Options));
        fixture.Api.Verify(item => item.UpdateActionOutcome(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<UpdateRuleActionOutcomeDto>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Api.Verify(item => item.CreateFactSnapshot(
            It.IsAny<Guid>(), It.IsAny<RuleFactSnapshotRequestDto>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Audit.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeEvidenceOutsideExistingSkewBoundsIsRejectedWithoutWrites(bool beforeClaim)
    {
        var fixture = new Fixture();
        var action = fixture.Execution.Actions[0];
        var result = fixture.Result(RuleActionExecutionOutcome.Applied) with
        {
            OccurredAtUtc = beforeClaim
                ? action.ClaimedAtUtc.AddSeconds(-31)
                : DateTimeOffset.UtcNow.AddSeconds(31)
        };

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.OutcomeAsync(result, default));

        fixture.Api.Verify(item => item.UpdateActionOutcome(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<UpdateRuleActionOutcomeDto>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FailedNativeEffectNeverEmitsAppliedAudit()
    {
        var fixture = new Fixture();

        await fixture.Service.OutcomeAsync(fixture.Result(RuleActionExecutionOutcome.Failed), default);

        Assert.Equal(RuleActionExecutionOutcome.Failed, Assert.Single(fixture.Execution.Actions).Outcome);
        fixture.Audit.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(-65, false)]
    [InlineData(-20, false)]
    [InlineData(-5, false)]
    [InlineData(-20, true)]
    public async Task VerifiedLateNativeEvidenceDoesNotRewindOrResumeTheExecution(int sourceOffsetSeconds, bool linkedRecord)
    {
        var fixture = new Fixture();
        var action = fixture.Execution.Actions[0];
        action = action with
        {
            ActionTypeId = linkedRecord ? "moderation.kick-with-record" : "player.kick",
            AdminActionId = linkedRecord ? action.AdminActionId : null,
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
            OccurredAtUtc = DateTimeOffset.UtcNow.AddSeconds(sourceOffsetSeconds)
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
    public async Task PartialStartRetriesMissingFeatureWithoutDroppingHealthyCommands()
    {
        var fail = true;
        var effects = new HashSet<string>();
        var missingId = Guid.NewGuid();
        var healthyId = Guid.NewGuid();
        var missing = new Feature("vpn-protection", invocation =>
        {
            if (fail)
            {
                throw new InvalidOperationException("Provider unavailable before Start.");
            }
            if (effects.Add("vpn"))
            {
                invocation.Commands.Add(new() { ExecutionId = missingId });
            }
            return missingId;
        });
        var healthy = new Feature("siege-mode", invocation =>
        {
            if (effects.Add("siege"))
            {
                invocation.Commands.Add(new() { ExecutionId = healthyId });
            }
            return healthyId;
        });
        var fixture = new Fixture(missing, healthy);
        var trigger = fixture.Execution.Trigger;

        var first = await fixture.Service.EvaluateAsync(trigger, default);
        fail = false;
        var recovered = await fixture.Service.EvaluateAsync(trigger, default);
        var duplicate = await fixture.Service.EvaluateAsync(trigger, default);

        Assert.True(first.RetryTrigger);
        Assert.Equal(healthyId, Assert.Single(first.Commands).ExecutionId);
        Assert.False(recovered.RetryTrigger);
        Assert.Equal(missingId, Assert.Single(recovered.Commands).ExecutionId);
        Assert.Empty(duplicate.Commands);
        Assert.Equal(2, effects.Count);
    }

    [Fact]
    public async Task LateBanDropEvidencePreservesAggregateAndDoesNotResumeSkippedDependents()
    {
        var fixture = new Fixture();
        var action = fixture.Execution.Actions[0];
        var occurredAt = DateTimeOffset.UtcNow.AddSeconds(-20);
        var aggregate = fixture.Result(RuleActionExecutionOutcome.OutcomeUnknown) with
        {
            Detail = "Expired without a result."
        };
        fixture.Execution = fixture.Execution with
        {
            State = RuleFeatureExecutionState.Stopped,
            RuleIndex = 2,
            ActionIndex = 5,
            Actions =
            [
                action with
                {
                    Outcome = RuleActionExecutionOutcome.OutcomeUnknown,
                    ClaimedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Command = action.Command with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-10) },
                    ResultJson = JsonSerializer.Serialize(aggregate, RulesSerialization.Options)
                }
            ]
        };
        var evidence = fixture.Result(RuleActionExecutionOutcome.Applied) with { OccurredAtUtc = occurredAt };

        await fixture.Service.OutcomeAsync(evidence, default);
        await fixture.Service.OutcomeAsync(evidence, default);

        action = Assert.Single(fixture.Execution.Actions);
        Assert.Equal(RuleActionExecutionOutcome.OutcomeUnknown, action.Outcome);
        Assert.Equal(evidence, action.NativeEvidence);
        Assert.Equal(RuleFeatureExecutionState.Stopped, fixture.Execution.State);
        Assert.Equal(5, fixture.Execution.ActionIndex);
        fixture.Api.Verify(item => item.CreateFactSnapshot(
            It.IsAny<Guid>(), It.IsAny<RuleFactSnapshotRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Audit.Verify(item => item.LogAudit(It.IsAny<AuditEvent>()), Times.Once);
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

    [Fact]
    public async Task OutcomeContinuationRetainsLateIpObservationForFollowingCommands()
    {
        RuleTriggerEnvelope? resumed = null;
        var fixture = new Fixture(new Feature("vpn-protection", invocation =>
        {
            resumed = invocation.Trigger;
            return Guid.NewGuid();
        }));
        var enriched = fixture.Execution.Trigger with
        {
            TriggerId = "player.ip-resolved",
            ObservationId = Guid.NewGuid(),
            TriggerOccurrenceId = Guid.NewGuid(),
            OccurredAtUtc = fixture.Execution.Trigger.OccurredAtUtc.AddMilliseconds(10),
            PlayerIpAddress = "8.8.8.8"
        };
        fixture.Execution = fixture.Execution with
        {
            DecisionSnapshotJson = JsonSerializer.Serialize(new RuleExecutionProgress
            {
                ContextTrigger = enriched,
                ContextObservedAtUtc = enriched.OccurredAtUtc
            }, RulesSerialization.Options)
        };

        await fixture.Service.OutcomeAsync(fixture.Result(RuleActionExecutionOutcome.Failed), default);

        Assert.NotNull(resumed);
        Assert.Equal(enriched.TriggerId, resumed.TriggerId);
        Assert.Equal(enriched.TriggerOccurrenceId, resumed.TriggerOccurrenceId);
        Assert.Equal(enriched.ObservationId, resumed.ObservationId);
        Assert.Equal(enriched.PlayerIpAddress, resumed.PlayerIpAddress);
        Assert.Equal(enriched.ConnectionId, resumed.ConnectionId);
    }

    [Fact]
    public async Task PersistedContextCannotSubstituteAnotherConnectionDuringResume()
    {
        var fixture = new Fixture();
        fixture.Execution = fixture.Execution with
        {
            DecisionSnapshotJson = JsonSerializer.Serialize(new RuleExecutionProgress
            {
                ContextTrigger = fixture.Execution.Trigger with { ConnectionId = Guid.NewGuid() }
            }, RulesSerialization.Options)
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.PollAsync(fixture.PollRequest(), default));

        fixture.Api.Verify(item => item.CreateFactSnapshot(
            It.IsAny<Guid>(), It.IsAny<RuleFactSnapshotRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static PlayerConnectedEvent ConnectedEvent(
        RuleTriggerEnvelope trigger,
        Guid? observationId = null,
        Guid? connectionId = null,
        ulong? connectionGeneration = 1,
        string? runtimeId = "cod4x-plugin",
        DateTime? eventGeneratedUtc = null,
        bool? rulesEvaluatedByPlugin = true) => new()
        {
            ServerId = trigger.GameServerId,
            GameType = trigger.GameType.ToString(),
            SequenceId = 1,
            EventGeneratedUtc = eventGeneratedUtc ?? trigger.OccurredAtUtc.UtcDateTime,
            EventPublishedUtc = trigger.OccurredAtUtc.UtcDateTime,
            PlayerGuid = trigger.PlayerGuid ?? throw new ArgumentException("Player GUID is required.", nameof(trigger)),
            Username = "Player",
            IpAddress = trigger.PlayerIpAddress ?? string.Empty,
            SlotId = trigger.SlotId!.Value,
            ConnectionId = connectionId ?? trigger.ConnectionId,
            ObservationId = observationId,
            ConnectionGeneration = connectionGeneration,
            Runtime = runtimeId is null ? null : new()
            {
                Id = runtimeId,
                Version = trigger.Runtime!.Version,
                ProtocolVersion = trigger.Runtime.ProtocolVersion,
                InstanceId = trigger.Runtime.InstanceId,
                StartedAtUtc = trigger.Runtime.StartedAtUtc
            },
            RulesEvaluatedByPlugin = runtimeId == "cod4x-plugin" ? rulesEvaluatedByPlugin : false
        };

    private sealed class Feature(string featureId, Func<RuleInvocation, Guid?> process, string[]? triggerIds = null) : IRegisteredRuleFeature
    {
        public string FeatureId => featureId;
        public IReadOnlySet<string> TriggerIds { get; } =
            new HashSet<string>(triggerIds ?? ["player.connected", "player.ip-resolved"]);
        public Task<Guid?> ProcessAsync(RuleInvocation invocation, CancellationToken ct) =>
            Task.FromResult(process(invocation));
    }

    private static ApiResult<T> Ok<T>(T value) => new(HttpStatusCode.OK, new ApiResponse<T>(value));

    private sealed class TestLogger : ILogger<RulesTriggerService>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullLogger.Instance.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private sealed class Fixture
    {
        public Mock<IRuleExecutionsApi> Api { get; } = new();
        public Mock<IAuditLogger> Audit { get; } = new();
        public TestLogger Logger { get; } = new();
        public Mock<IGameServersEventsApi> EventApi { get; } = new();
        public List<CreateGameServerEventDto> Events { get; } = [];
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
                ConnectionId = trigger.ConnectionId,
                ConnectionGeneration = trigger.ConnectionGeneration,
                SlotId = trigger.SlotId!.Value,
                PlayerGuid = trigger.PlayerGuid,
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
                        AdminActionId = Guid.NewGuid(),
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
            Api.Setup(item => item.GetConnectionExecutions(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok(Array.Empty<RuleFeatureExecutionDto>()));
            var eventVersions = new Mock<IVersionedGameServersEventsApi>();
            eventVersions.Setup(item => item.V1).Returns(EventApi.Object);
            repository.Setup(item => item.GameServersEvents).Returns(eventVersions.Object);
            EventApi.Setup(item => item.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>()))
                .Callback<CreateGameServerEventDto, CancellationToken>((item, _) => Events.Add(item))
                .ReturnsAsync(new ApiResult(HttpStatusCode.Created));
            Api.Setup(item => item.GetExecution(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(Ok(Execution)));
            Api.Setup(item => item.CreateFactSnapshot(It.IsAny<Guid>(), It.IsAny<RuleFactSnapshotRequestDto>(), It.IsAny<CancellationToken>()))
                .Returns((Guid serverId, RuleFactSnapshotRequestDto request, CancellationToken _) => Task.FromResult(Ok(new RuleFactSnapshotDto
                {
                    SnapshotId = Guid.NewGuid(),
                    GameServerId = serverId,
                    ConnectionId = request.ConnectionId,
                    PlayerGuid = request.PlayerGuid,
                    PlayerIpAddress = request.PlayerIpAddress,
                    GameType = request.GameType
                })));
            var persisted = false;
            Api.Setup(item => item.UpdateActionOutcome(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                    It.IsAny<UpdateRuleActionOutcomeDto>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, Guid _, string _, UpdateRuleActionOutcomeDto request, CancellationToken _) =>
                {
                    var action = Execution.Actions[0] with
                    {
                        Outcome = request.Result.Outcome,
                        Detail = request.Result.Detail,
                        AdminActionId = request.AdminActionId ?? Execution.Actions[0].AdminActionId,
                        NativeEvidence = request.NativeEvidence ?? Execution.Actions[0].NativeEvidence,
                        ResultJson = JsonSerializer.Serialize(request.Result, RulesSerialization.Options)
                    };
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
                Logger, Audit.Object);
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
