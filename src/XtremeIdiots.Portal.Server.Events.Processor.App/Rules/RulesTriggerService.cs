using System.Text.Json;

using Microsoft.Extensions.Logging;

using MX.GeoLocation.Abstractions.Models.V1_1;
using MX.GeoLocation.Api.Client.V1;
using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;
using XtremeIdiots.Portal.Server.Events.Processor.App.Services;

using RuleRuntimeDescriptor = XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine.RuleRuntimeDescriptor;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

internal sealed class RulesTriggerService(
    IRepositoryApiClient repository,
    IGeoLocationApiClient geoLocation,
    RegisteredFeatureHost host,
    ILogger<RulesTriggerService> logger,
    IAuditLogger audit) : IRulesTriggerService, IRulesPluginTransport, IRulesExecutionResumer
{
    public Task<PreparedRulesTrigger?> PrepareAsync(PlayerConnectedEvent source, CancellationToken ct) =>
        PrepareEventAsync(() => ConnectionTriggerNormalizer.From(source), source.ServerId, ct);

    public Task<PreparedRulesTrigger?> PrepareAsync(PlayerIpResolvedEvent source, CancellationToken ct) =>
        PrepareEventAsync(() => ConnectionTriggerNormalizer.From(source), source.ServerId, ct);

    public async Task<RulesProcessingSummary> ProcessAsync(
        PreparedRulesTrigger? prepared, IpIntelligenceDto? intelligence, CancellationToken ct)
    {
        if (prepared is null || prepared.Trigger.Runtime?.Id == "cod4x-plugin")
        {
            // Plugin-owned connections execute through its request/response transport, not mirrored queue messages.
            return new();
        }

        var invocation = new RuleInvocation(prepared.Trigger, prepared.Facts, intelligence);
        var results = await host.ProcessAsync(invocation, ct).ConfigureAwait(false);
        var failures = results.Where(item => item.Failure is not null).Select(item => item.Failure!).ToArray();
        if (failures.Length != 0)
        {
            throw new AggregateException("One or more rules features failed.", failures);
        }

        return new(invocation.SuppressWelcome);
    }

    public async Task<RuleEvaluationResponse> EvaluateAsync(RuleTriggerEnvelope trigger, CancellationToken ct)
    {
        if (!IsValid(trigger) || trigger.Runtime?.Id != "cod4x-plugin")
        {
            throw new ArgumentException("A normalized plugin trigger with proven connection identity is required.", nameof(trigger));
        }

        var prepared = await PrepareCoreAsync(trigger, ct).ConfigureAwait(false);
        var invocation = await CreateInvocationAsync(prepared, ct).ConfigureAwait(false);
        var results = await host.ProcessAsync(invocation, ct).ConfigureAwait(false);
        return Response(invocation, results, retryUnstartedFeatures: true);
    }

    public async Task<RuleEvaluationResponse> PollAsync(RulePollRequest request, CancellationToken ct)
    {
        if (request.ProtocolVersion != 1 || request.GameServerId == Guid.Empty || request.ConnectionId == Guid.Empty ||
            request.ConnectionGeneration == 0 || request.SlotId < 0 || string.IsNullOrWhiteSpace(request.PlayerGuid))
        {
            throw new ArgumentException("A complete polling connection identity is required.", nameof(request));
        }

        var commands = new List<RuleActionCommand>();
        var pending = new List<Guid>();
        foreach (var executionId in request.ExecutionIds.Distinct())
        {
            var execution = FeatureExecutionOrchestrator.Require(await repository.RuleExecutions.V1.GetExecution(
                request.GameServerId, executionId, ct).ConfigureAwait(false), "load rules execution");
            if (execution.Trigger.Runtime is not { Id: "cod4x-plugin", IdentityModel: RuleRuntimeIdentityModel.PluginRuntimeGeneration } ||
                execution.ConnectionId != request.ConnectionId ||
                execution.Trigger.ConnectionGeneration != request.ConnectionGeneration ||
                execution.Trigger.SlotId != request.SlotId ||
                !string.Equals(execution.Trigger.PlayerGuid, request.PlayerGuid, StringComparison.Ordinal))
            {
                throw new ArgumentException("Poll identity does not match the execution.", nameof(request));
            }

            var response = await ResumeAsync(execution, ct).ConfigureAwait(false);
            commands.AddRange(response.Commands);
            pending.AddRange(response.ExecutionIds);
        }

        return new() { Commands = commands.ToArray(), ExecutionIds = pending.Distinct().ToArray() };
    }

    public async Task<RuleEvaluationResponse> OutcomeAsync(RuleActionResult result, CancellationToken ct)
    {
        var receivedAtUtc = DateTimeOffset.UtcNow;
        if (result.ProtocolVersion != 1 || result.GameServerId == Guid.Empty || result.ExecutionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(result.ActionId) || string.IsNullOrWhiteSpace(result.LogicalEffectId) ||
            result.OccurredAtUtc == default || result.OccurredAtUtc > receivedAtUtc.AddSeconds(30) ||
            result.Outcome is not (RuleActionExecutionOutcome.Applied or RuleActionExecutionOutcome.Dispatched or
                RuleActionExecutionOutcome.Failed or RuleActionExecutionOutcome.Stale or
                RuleActionExecutionOutcome.SkippedUnsupported or RuleActionExecutionOutcome.OutcomeUnknown))
        {
            throw new ArgumentException("A complete correlated native outcome is required.", nameof(result));
        }

        var execution = FeatureExecutionOrchestrator.Require(await repository.RuleExecutions.V1.GetExecution(
            result.GameServerId, result.ExecutionId, ct).ConfigureAwait(false), "load rules outcome");
        var action = execution.Actions.SingleOrDefault(item => item.LogicalEffectId == result.LogicalEffectId);
        if (execution.GameServerId != result.GameServerId || execution.ExecutionId != result.ExecutionId ||
            action is null || action.GameServerId != result.GameServerId || action.ExecutionId != result.ExecutionId ||
            action.Correlation != result.Correlation || action.Correlation.ActionId != result.ActionId ||
            action.Command.ExecutionId != result.ExecutionId || action.Command.GameServerId != result.GameServerId ||
            action.Command.ActionId != result.ActionId || action.Command.LogicalEffectId != result.LogicalEffectId ||
            action.Command.Correlation != result.Correlation || action.Command.ConnectionId != execution.ConnectionId ||
            action.Correlation.ConnectionId != execution.ConnectionId ||
            action.Command.ConnectionGeneration != execution.Trigger.ConnectionGeneration ||
            action.Command.SlotId != execution.Trigger.SlotId || action.Command.PlayerGuid != execution.Trigger.PlayerGuid ||
            !SameRuntimeIdentity(action.Command.Runtime, execution.Trigger.Runtime) ||
            action.Command.Runtime.Id != "cod4x-plugin" || action.Mode != RuleExecutionMode.Enforce ||
            action.Command.Effect.Type is not (RuleNativeEffectType.DropPlayer or RuleNativeEffectType.PublicMessage or RuleNativeEffectType.PrivateMessage) ||
            receivedAtUtc < action.ClaimedAtUtc.AddSeconds(-30) || result.OccurredAtUtc < action.ClaimedAtUtc.AddSeconds(-30))
        {
            throw new ArgumentException("The native outcome does not match the claimed plugin command.", nameof(result));
        }

        var applied = result.Outcome == RuleActionExecutionOutcome.Applied;
        if (applied && result.OccurredAtUtc > action.Command.ExpiresAtUtc.AddSeconds(30))
        {
            throw new ArgumentException("Applied native evidence falls outside the claimed command window and clock-skew allowance.", nameof(result));
        }
        var compositeBan = action.ActionTypeId is "moderation.ban" or "moderation.temporary-ban";
        if (applied && (compositeBan || action.ActionTypeId == "moderation.kick-with-record") && action.AdminActionId is null)
        {
            throw new ArgumentException("The native effect has no linked durable moderation record.", nameof(result));
        }
        var recorded = result;
        if (applied && compositeBan)
        {
            recorded = result with
            {
                Outcome = RuleActionExecutionOutcome.OutcomeUnknown,
                Detail = "Native drop confirmed; the durable ban record exists but ongoing ban-cache enforcement is not confirmed."
            };
        }

        if (result.Outcome == RuleActionExecutionOutcome.Dispatched &&
            action.Outcome == RuleActionExecutionOutcome.Dispatched &&
            action.ResultJson is not null && StoredAggregate(action) == result)
        {
            return PendingResponse(execution);
        }

        var lateConfirmation = action.Outcome == RuleActionExecutionOutcome.OutcomeUnknown && applied &&
            DateTimeOffset.UtcNow > action.Command.ExpiresAtUtc && (!compositeBan || action.NativeEvidence is null);
        if (action.Outcome is not (RuleActionExecutionOutcome.Claimed or RuleActionExecutionOutcome.Dispatched) && !lateConfirmation)
        {
            if (applied && compositeBan ? action.NativeEvidence != result : StoredAggregate(action) != recorded)
            {
                throw new ArgumentException("The claimed action already has a different terminal outcome.", nameof(result));
            }
            return PendingResponse(execution);
        }
        else
        {
            if (lateConfirmation && compositeBan)
            {
                recorded = StoredAggregate(action);
            }
            var updated = FeatureExecutionOrchestrator.Require(await repository.RuleExecutions.V1.UpdateActionOutcome(
                result.GameServerId, result.ExecutionId, result.ActionId, new UpdateRuleActionOutcomeDto
                {
                    ExpectedVersion = execution.Version,
                    Result = recorded,
                    AdminActionId = action.AdminActionId,
                    NativeEvidence = applied && compositeBan ? result : null,
                    NextRuleIndex = execution.RuleIndex,
                    NextActionIndex = lateConfirmation || result.Outcome == RuleActionExecutionOutcome.Dispatched
                        ? execution.ActionIndex : execution.ActionIndex + 1,
                    NextState = lateConfirmation ? execution.State : result.Outcome == RuleActionExecutionOutcome.Dispatched
                        ? RuleFeatureExecutionState.AwaitingActionOutcome
                        : RuleFeatureExecutionState.Evaluating,
                    DecisionSnapshotJson = execution.DecisionSnapshotJson
                }, ct).ConfigureAwait(false), "persist correlated native outcome");
            execution = updated.Execution;
            if (result.Outcome == RuleActionExecutionOutcome.Applied)
            {
                audit.LogAudit(AuditEvent.ServerAction("RulesNativeEffectApplied", AuditAction.Update)
                    .WithGameContext(execution.Trigger.GameType.ToString(), execution.GameServerId)
                    .WithPlayer(execution.Trigger.PlayerGuid ?? "", execution.Trigger.Username)
                    .WithProperty("ExecutionId", result.ExecutionId.ToString())
                    .WithProperty("LogicalEffectId", result.LogicalEffectId)
                    .WithProperty("ActionId", result.ActionId)
                    .WithProperty("Effect", action.Command.Effect.Type.ToString())
                    .Build());
            }
        }

        if (lateConfirmation)
        {
            return PendingResponse(execution);
        }
        return await ResumeAsync(execution, ct).ConfigureAwait(false);
    }

    private static RuleActionResult StoredAggregate(RuleActionExecutionDto action) =>
        action.ResultJson is { } json
            ? JsonSerializer.Deserialize<RuleActionResult>(json, RulesSerialization.Options)
                ?? throw new InvalidOperationException("The persisted aggregate result is missing.")
            : throw new InvalidOperationException("The persisted aggregate result is missing.");

    private static bool SameRuntimeIdentity(RuleRuntimeDescriptor? runtime, RuleRuntimeDescriptor? producer) =>
        runtime is not null && producer is not null &&
        runtime.Id == producer.Id && runtime.Version == producer.Version &&
        runtime.ProtocolVersion == producer.ProtocolVersion && runtime.IdentityModel == producer.IdentityModel &&
        runtime.InstanceId == producer.InstanceId && runtime.StartedAtUtc == producer.StartedAtUtc;

    private static RuleEvaluationResponse PendingResponse(RuleFeatureExecutionDto execution) => new()
    {
        ExecutionIds = execution.State == RuleFeatureExecutionState.AwaitingActionOutcome ? [execution.ExecutionId] : []
    };

    public async Task ResumeAsync(Guid serverId, Guid executionId, CancellationToken ct)
    {
        var execution = FeatureExecutionOrchestrator.Require(await repository.RuleExecutions.V1.GetExecution(
            serverId, executionId, ct).ConfigureAwait(false), "load deferred rules execution");
        if (execution.Trigger.Runtime?.Id != "server-agent")
        {
            throw new InvalidOperationException("Only agent-owned executions may resume without a plugin response transport.");
        }

        await ResumeAsync(execution, ct, propagateFailure: true).ConfigureAwait(false);
    }

    private async Task<RuleEvaluationResponse> ResumeAsync(
        RuleFeatureExecutionDto execution, CancellationToken ct, bool propagateFailure = false)
    {
        if (execution.State is RuleFeatureExecutionState.Completed or RuleFeatureExecutionState.Stopped)
        {
            return new();
        }
        var prepared = await PrepareCoreAsync(ResumeTrigger(execution), ct).ConfigureAwait(false);
        var invocation = await CreateInvocationAsync(prepared, ct).ConfigureAwait(false);
        invocation.ExecutionIds[execution.FeatureId] = execution.ExecutionId;
        var results = await host.ProcessAsync(invocation, ct, execution.FeatureId).ConfigureAwait(false);
        if (propagateFailure && results.Any(item => item.Failure is not null))
        {
            throw new AggregateException("Deferred rules execution failed.",
                results.Where(item => item.Failure is not null).Select(item => item.Failure!));
        }
        if (results.Any(item => item.FeatureId == execution.FeatureId && item.Failure is null && item.ExecutionId is null))
        {
            return await StopInactiveAsync(execution, ct).ConfigureAwait(false);
        }
        return Response(invocation, results);
    }

    private static RuleTriggerEnvelope ResumeTrigger(RuleFeatureExecutionDto execution)
    {
        var progress = execution.DecisionSnapshotJson is null ? null :
            JsonSerializer.Deserialize<RuleExecutionProgress>(execution.DecisionSnapshotJson, RulesSerialization.Options);
        if (progress?.ContextTrigger is not { } context)
        {
            return execution.Trigger;
        }
        var original = execution.Trigger;
        if (!IsValid(context) || context.GameServerId != original.GameServerId ||
            context.ConnectionId != original.ConnectionId || context.ConnectionGeneration != original.ConnectionGeneration ||
            context.GameType != original.GameType || context.SlotId != original.SlotId ||
            context.PlayerGuid != original.PlayerGuid || context.OccurredAtUtc < original.OccurredAtUtc ||
            !SameRuntimeIdentity(context.Runtime, original.Runtime))
        {
            throw new InvalidOperationException("Persisted rules context does not match the execution connection.");
        }
        return context;
    }

    private async Task<RuleEvaluationResponse> StopInactiveAsync(RuleFeatureExecutionDto execution, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = execution.Actions.Where(action =>
            action.Outcome is RuleActionExecutionOutcome.Claimed or RuleActionExecutionOutcome.Dispatched).ToArray();
        if (pending.Any(action => action.Command.ExpiresAtUtc > now))
        {
            return new() { ExecutionIds = [execution.ExecutionId] };
        }
        foreach (var action in pending)
        {
            var updated = FeatureExecutionOrchestrator.Require(await repository.RuleExecutions.V1.UpdateActionOutcome(
                execution.GameServerId, execution.ExecutionId, action.Correlation.ActionId, new UpdateRuleActionOutcomeDto
                {
                    ExpectedVersion = execution.Version,
                    Result = new()
                    {
                        GameServerId = execution.GameServerId,
                        ExecutionId = execution.ExecutionId,
                        ActionId = action.Correlation.ActionId,
                        LogicalEffectId = action.LogicalEffectId,
                        Correlation = action.Correlation,
                        Outcome = RuleActionExecutionOutcome.OutcomeUnknown,
                        OccurredAtUtc = now,
                        Detail = "Feature is inactive and the native command expired without confirmed completion."
                    },
                    NextRuleIndex = execution.RuleIndex,
                    NextActionIndex = execution.ActionIndex,
                    NextState = RuleFeatureExecutionState.Stopped,
                    DecisionSnapshotJson = execution.DecisionSnapshotJson
                }, ct).ConfigureAwait(false), "finalize expired inactive-feature action");
            execution = updated.Execution;
        }
        if (execution.State != RuleFeatureExecutionState.Stopped)
        {
            FeatureExecutionOrchestrator.Require(await repository.RuleExecutions.V1.UpdateProgress(
                execution.GameServerId, execution.ExecutionId, new UpdateRuleFeatureExecutionProgressDto
                {
                    ExpectedVersion = execution.Version,
                    RuleIndex = execution.RuleIndex,
                    ActionIndex = execution.ActionIndex,
                    State = RuleFeatureExecutionState.Stopped,
                    DecisionSnapshotJson = execution.DecisionSnapshotJson,
                    FactSnapshotId = execution.FactSnapshotId
                }, ct).ConfigureAwait(false), "stop inactive rules feature");
        }
        return new();
    }

    private async Task<PreparedRulesTrigger?> PrepareEventAsync(Func<RuleTriggerEnvelope> normalize, Guid serverId, CancellationToken ct)
    {
        RuleTriggerEnvelope trigger;
        try
        {
            trigger = normalize();
        }

        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Skipping normalized rules for event without connection evidence on server {ServerId}", serverId);
            return null;
        }
        if (!IsValid(trigger))
        {
            logger.LogWarning("Skipping normalized rules for event without observation evidence on server {ServerId}", serverId);
            return null;
        }

        return await PrepareCoreAsync(trigger, ct).ConfigureAwait(false);
    }

    private async Task<PreparedRulesTrigger> PrepareCoreAsync(RuleTriggerEnvelope trigger, CancellationToken ct)
    {
        if (trigger.PlayerIpAddress is { } ipAddress && IpAddressGuard.IsPersistable(ipAddress))
        {
            trigger = trigger with { PlayerIpAddress = IpAddressGuard.Normalize(ipAddress) };
        }
        var facts = FeatureExecutionOrchestrator.Require(await repository.RuleExecutions.V1.CreateFactSnapshot(
            trigger.GameServerId, new RuleFactSnapshotRequestDto
            {
                ObservationId = trigger.ObservationId,
                ConnectionId = trigger.ConnectionId,
                GameType = trigger.GameType,
                PlayerGuid = trigger.PlayerGuid,
                PlayerIpAddress = trigger.PlayerIpAddress,
                ObservedAtUtc = trigger.OccurredAtUtc
            }, ct).ConfigureAwait(false), "capture pre-observation rules facts");
        if (facts.SnapshotId == Guid.Empty || facts.GameServerId != trigger.GameServerId ||
            facts.ConnectionId != trigger.ConnectionId || facts.GameType != trigger.GameType ||
            !string.Equals(facts.PlayerGuid, trigger.PlayerGuid, StringComparison.Ordinal) ||
            trigger.PlayerIpAddress is not null && !string.Equals(facts.PlayerIpAddress, trigger.PlayerIpAddress, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Rules fact snapshot does not match its connection observation.");
        }

        return new(trigger, facts);
    }

    private Task<RuleInvocation> CreateInvocationAsync(PreparedRulesTrigger prepared, CancellationToken ct) =>
        Task.FromResult(new RuleInvocation(prepared.Trigger, prepared.Facts, null)
        {
            IntelligenceLookup = new(() => LookupIntelligenceAsync(prepared, ct))
        });

    private async Task<IpIntelligenceDto?> LookupIntelligenceAsync(PreparedRulesTrigger prepared, CancellationToken ct)
    {
        IpIntelligenceDto? intelligence = null;
        var ipAddress = prepared.Facts.PlayerIpAddress;
        if (IpAddressGuard.IsPersistable(ipAddress))
        {
            try
            {
                var response = await geoLocation.GeoLookup.V1_1.GetIpIntelligence(ipAddress!, ct).ConfigureAwait(false);
                if (response.IsSuccess && response.Result?.Data is { } data)
                {
                    intelligence = data;
                }
                else
                {
                    logger.LogWarning("Rules IP intelligence is unavailable for connection {ConnectionId}: {StatusCode}",
                        prepared.Trigger.ConnectionId, response.StatusCode);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Rules IP intelligence is unavailable for connection {ConnectionId}", prepared.Trigger.ConnectionId);
            }
        }

        return intelligence;
    }

    private static RuleEvaluationResponse Response(
        RuleInvocation invocation, IReadOnlyList<FeatureProcessingResult> results, bool retryUnstartedFeatures = false) =>
        new()
        {
            Commands = invocation.Commands.ToArray(),
            RetryTrigger = retryUnstartedFeatures && results.Any(item => item.Failure is not null && item.ExecutionId is null),
            ExecutionIds = invocation.PendingExecutionIds
                .Concat(results.Where(item => item.Failure is not null && item.ExecutionId.HasValue)
                    .Select(item => item.ExecutionId!.Value))
                .Distinct().ToArray()
        };

    private static bool IsValid(RuleTriggerEnvelope trigger) =>
        trigger.ProtocolVersion == 1 && trigger.GameServerId != Guid.Empty && trigger.ConnectionId != Guid.Empty &&
        trigger.ObservationId != Guid.Empty && trigger.TriggerOccurrenceId != Guid.Empty &&
        trigger.SlotId is >= 0 && !string.IsNullOrWhiteSpace(trigger.PlayerGuid) &&
        trigger.GameType != GameType.Unknown && Enum.IsDefined(trigger.GameType) &&
        trigger.OccurredAtUtc != default && trigger.OccurredAtUtc <= DateTimeOffset.UtcNow.AddSeconds(30) &&
        trigger.TriggerId is "player.connected" or "player.ip-resolved" &&
        trigger.Runtime is { ProtocolVersion: 1 } runtime && !string.IsNullOrWhiteSpace(runtime.Version) &&
        runtime.InstanceId != Guid.Empty && runtime.StartedAtUtc != default &&
        (runtime.Id == "cod4x-plugin" && runtime.IdentityModel == RuleRuntimeIdentityModel.PluginRuntimeGeneration &&
            trigger.ConnectionGeneration is > 0 ||
         runtime.Id == "server-agent" && runtime.IdentityModel == RuleRuntimeIdentityModel.AgentObservedConnection &&
            trigger.ConnectionGeneration is null);
}

public interface IRulesExecutionResumer
{
    Task ResumeAsync(Guid serverId, Guid executionId, CancellationToken ct);
}
