using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.RulesEngine;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

internal interface IRuleModerationRecordWriter
{
    Task<Guid> PersistAsync(
        RuleActionDefinition action, RuleActionCommand command, RuleInvocation invocation, CancellationToken ct);
}

internal sealed class UniversalRuleActionExecutor(
    IRepositoryApiClient repository,
    IServersApiClient servers,
    IRuleModerationRecordWriter records,
    ILogger<UniversalRuleActionExecutor> logger,
    TimeProvider? timeProvider = null) : IUniversalRuleActionExecutor
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public Task<string?> UnsupportedReasonAsync(RuleActionDefinition action, RuleInvocation invocation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var definition = RulesEngineCatalogueV1.Current.Actions.SingleOrDefault(item => item.ActionTypeId == action.TypeId);
        if (definition is null || definition.GameTypes.Count != 0 && !definition.GameTypes.Contains((int)invocation.Trigger.GameType))
        {
            return Task.FromResult<string?>("Action is unsupported for this game.");
        }
        if (action.TypeId == "moderation.observation")
        {
            return Task.FromResult<string?>(null);
        }

        var runtime = invocation.Trigger.Runtime;
        if (runtime is null || runtime.ProtocolVersion != 1 || string.IsNullOrWhiteSpace(runtime.Version) ||
            runtime.InstanceId == Guid.Empty || runtime.StartedAtUtc == default)
        {
            return Task.FromResult<string?>("No supported executor identity was supplied by the trigger producer.");
        }
        if (runtime.Id == "cod4x-plugin" && runtime.IdentityModel == RuleRuntimeIdentityModel.PluginRuntimeGeneration)
        {
            var capability = action.TypeId switch
            {
                "message.public" => RulesEngineCatalogueV1.Cod4xPublicChatCapability,
                "message.private" => RulesEngineCatalogueV1.Cod4xPrivateChatCapability,
                _ => RulesEngineCatalogueV1.Cod4xPlayerDropCapability
            };
            return Task.FromResult(invocation.Trigger.GameType == GameType.CallOfDuty4x &&
                invocation.Trigger.ConnectionGeneration is > 0 && runtime.Capabilities?.Contains(capability, StringComparer.Ordinal) == true
                ? null
                : $"Runtime does not support '{capability}' with a proven native connection generation.");
        }
        if (runtime.Id == "server-agent" && runtime.IdentityModel == RuleRuntimeIdentityModel.AgentObservedConnection &&
            invocation.Trigger.ConnectionGeneration is null)
        {
            if (invocation.Trigger.GameType is not (GameType.CallOfDuty2 or GameType.CallOfDuty4 or GameType.CallOfDuty5 or GameType.CallOfDuty4x))
            {
                return Task.FromResult<string?>("The immediate-identity-checked RCON adapter is unavailable for this game.");
            }
            return Task.FromResult(action.TypeId == "player.kick"
                ? "This RCON adapter cannot deliver the requested kick display reason."
                : null);
        }
        return Task.FromResult<string?>("Producer identity does not select an implemented executor.");
    }

    public async Task<ActionAvailability> AvailabilityAsync(RuleActionCommand command, DateTimeOffset waitUntilUtc, CancellationToken ct)
    {
        if (command.Effect.Type == RuleNativeEffectType.Unknown ||
            command.Runtime.IdentityModel == RuleRuntimeIdentityModel.PluginRuntimeGeneration)
        {
            return new();
        }
        var response = await repository.RuleConnectionBindings.V1.GetCurrentBinding(
            command.GameServerId, command.SlotId, command.PlayerGuid, ct).ConfigureAwait(false);
        if (response.IsNotFound)
        {
            return clock.GetUtcNow() < waitUntilUtc
                ? new(Deferred: true)
                : new(Unavailable: new(ActionExecutionOutcome.Stale, "No fresh authoritative connection binding became available."));
        }
        var binding = FeatureExecutionOrchestrator.Require(response, "verify current agent connection ownership");
        var now = clock.GetUtcNow();
        if (binding.GameServerId != command.GameServerId || binding.SlotId != command.SlotId ||
            !string.Equals(binding.PlayerGuid, command.PlayerGuid, StringComparison.Ordinal) ||
            binding.ConnectionId != command.ConnectionId || binding.ConnectionGeneration != command.ConnectionGeneration ||
            binding.Runtime.Id != command.Runtime.Id || binding.Runtime.IdentityModel != RuleRuntimeIdentityModel.AgentObservedConnection ||
            binding.Runtime.InstanceId != command.Runtime.InstanceId || binding.Runtime.StartedAtUtc != command.Runtime.StartedAtUtc ||
            binding.ObservedAtUtc > now.AddSeconds(30) || binding.ObservedAtUtc < now.AddMinutes(-2) ||
            binding.ExpiresAtUtc != binding.ObservedAtUtc.AddMinutes(2) || binding.ExpiresAtUtc <= now)
        {
            return new(Unavailable: new(ActionExecutionOutcome.Stale, "Authoritative observed connection ownership has changed or expired."));
        }
        return new();
    }

    public async Task<ActionExecutionResult> ExecuteAsync(
        RuleActionDefinition action, RuleActionCommand command, RuleInvocation invocation, CancellationToken ct)
    {
        if (command.Mode != RuleExecutionMode.Enforce)
        {
            throw new InvalidOperationException("A dry-run action cannot dispatch effects.");
        }
        if (command.ExpiresAtUtc <= clock.GetUtcNow())
        {
            return new(ActionExecutionOutcome.Stale, "Action decision expired before execution.");
        }
        var definition = RulesEngineCatalogueV1.Current.Actions.Single(item => item.ActionTypeId == action.TypeId);
        if (definition.ProducesModerationRecord)
        {
            var adminActionId = await records.PersistAsync(action, command, invocation, ct).ConfigureAwait(false);
            if (adminActionId == Guid.Empty)
            {
                throw new InvalidOperationException("The moderation record has no durable admin action identity.");
            }
        }
        if (action.TypeId == "moderation.observation")
        {
            return new(ActionExecutionOutcome.Applied, "Moderation observation persisted.");
        }
        if (command.ExpiresAtUtc <= clock.GetUtcNow())
        {
            return new(ActionExecutionOutcome.Stale, "The moderation record was persisted, but the native command expired before dispatch.");
        }

        if (command.Runtime.IdentityModel == RuleRuntimeIdentityModel.PluginRuntimeGeneration)
        {
            return new(ActionExecutionOutcome.Dispatched, "Awaiting the correlated native plugin result.", command);
        }

        var availability = await AvailabilityAsync(command, clock.GetUtcNow(), ct).ConfigureAwait(false);
        if (availability.Unavailable is { } unavailable)
        {
            return unavailable;
        }
        if (availability.Deferred)
        {
            return new(ActionExecutionOutcome.Stale, "Current observed connection ownership is unavailable.");
        }

        // The agent binding is observed identity; the integration rechecks GUID/slot, not native generation.
        var response = await servers.RulesNativeAction.V1.Execute(command.GameServerId, command, ct).ConfigureAwait(false);
        if (!response.IsSuccess || response.Result?.Data is not { } result)
        {
            logger.LogError("Native RCON dispatch has no confirmed result for effect {LogicalEffectId}: {StatusCode}",
                command.LogicalEffectId, response.StatusCode);
            return new(ActionExecutionOutcome.OutcomeUnknown, "Native request failed after it may have been dispatched; it will not be retried.");
        }
        if (result.GameServerId != command.GameServerId || result.ExecutionId != command.ExecutionId ||
            result.LogicalEffectId != command.LogicalEffectId || result.Correlation != command.Correlation ||
            result.ActionId != command.ActionId)
        {
            logger.LogError("Native integration returned mismatched correlation for effect {LogicalEffectId}", command.LogicalEffectId);
            return new(ActionExecutionOutcome.OutcomeUnknown, "Integration returned a mismatched native result.");
        }
        if (result.Outcome is RuleActionExecutionOutcome.Dispatched or RuleActionExecutionOutcome.Applied)
        {
            invocation.SuppressWelcome |= command.Effect.Type == RuleNativeEffectType.DropPlayer;
            return new(ActionExecutionOutcome.OutcomeUnknown,
                "RCON send was dispatched once; UDP cannot confirm application or continuing ban enforcement.");
        }
        return result.Outcome switch
        {
            RuleActionExecutionOutcome.Stale => new(ActionExecutionOutcome.Stale, result.Detail),
            RuleActionExecutionOutcome.SkippedUnsupported => new(ActionExecutionOutcome.SkippedUnsupported, result.Detail),
            RuleActionExecutionOutcome.Failed => new(ActionExecutionOutcome.Failed, result.Detail),
            _ => new(ActionExecutionOutcome.OutcomeUnknown, result.Detail ?? "Integration did not confirm a terminal native outcome.")
        };
    }
}
