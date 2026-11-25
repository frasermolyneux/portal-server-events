using System.Text;
using System.Text.Json;

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
                "message.private-tagged" => "cod4x.chat.private.recipient",
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
            return Task.FromResult<string?>(null);
        }
        return Task.FromResult<string?>("Producer identity does not select an implemented executor.");
    }

    public async Task<ActionAvailability> AvailabilityAsync(
        RuleActionDefinition action, RuleActionCommand command, RuleInvocation invocation, DateTimeOffset waitUntilUtc, CancellationToken ct)
    {
        if (InvalidRecipientTarget(command))
        {
            return new(Unavailable: new(ActionExecutionOutcome.SkippedUnsupported,
                "A separate same-server recipient target is only valid for a private message."));
        }
        if (command.Effect.Type == RuleNativeEffectType.DropPlayer && command.Effect.Reason is { } reason &&
            (Encoding.UTF8.GetByteCount(reason) > 256 || reason.Any(character => character < 32 || character == 127)))
        {
            return new(Unavailable: new(ActionExecutionOutcome.SkippedUnsupported,
                "The resolved native reason exceeds 256 UTF-8 bytes or contains control characters."));
        }
        if (command.Mode == RuleExecutionMode.Enforce &&
            RulesEngineCatalogueV1.Current.Actions.Single(item => item.ActionTypeId == action.TypeId).ProducesModerationRecord)
        {
            ct.ThrowIfCancellationRequested();
            var player = await repository.Players.V1.HeadPlayerByGameType(
                invocation.Trigger.GameType, command.PlayerGuid).WaitAsync(ct).ConfigureAwait(false);
            if (player.IsNotFound)
            {
                return clock.GetUtcNow() < waitUntilUtc
                    ? new(Deferred: true)
                    : new(Unavailable: new(ActionExecutionOutcome.Stale, "The player profile did not become available before the context deadline."));
            }
            if (!player.IsSuccess)
            {
                throw new InvalidOperationException($"Unable to verify moderation player readiness: {player.StatusCode}.");
            }
        }
        var availability = await NativeAvailabilityAsync(command, invocation, waitUntilUtc, ct).ConfigureAwait(false);
        if (availability.Deferred || availability.Unavailable is not null ||
            command.Effect.Type == RuleNativeEffectType.Unknown ||
            command.Runtime.IdentityModel == RuleRuntimeIdentityModel.PluginRuntimeGeneration)
        {
            return availability;
        }

        var probe = command with { Mode = RuleExecutionMode.DryRun };
        var response = await servers.RulesNativeAction.V1.Execute(command.GameServerId, probe, ct).ConfigureAwait(false);
        var result = FeatureExecutionOrchestrator.Require(response, "verify native RCON eligibility without dispatch");
        if (!Matches(probe, result))
        {
            throw new InvalidOperationException("Native eligibility returned mismatched command correlation.");
        }
        if (command.ExpiresAtUtc <= clock.GetUtcNow())
        {
            return new(Unavailable: new(ActionExecutionOutcome.Stale, "The native eligibility decision expired before claim."));
        }
        return result.Outcome switch
        {
            RuleActionExecutionOutcome.WouldExecute => new(),
            RuleActionExecutionOutcome.Stale => new(Unavailable: new(ActionExecutionOutcome.Stale, result.Detail)),
            RuleActionExecutionOutcome.SkippedUnsupported => new(Unavailable: new(ActionExecutionOutcome.SkippedUnsupported, result.Detail)),
            RuleActionExecutionOutcome.Failed => new(Unavailable: new(ActionExecutionOutcome.Failed, result.Detail)),
            _ => throw new InvalidOperationException($"Native eligibility returned unexpected outcome '{result.Outcome}'.")
        };
    }

    private async Task<ActionAvailability> NativeAvailabilityAsync(
        RuleActionCommand command, RuleInvocation invocation, DateTimeOffset waitUntilUtc, CancellationToken ct)
    {
        if (command.Effect.Type == RuleNativeEffectType.Unknown ||
            command.Runtime.IdentityModel == RuleRuntimeIdentityModel.PluginRuntimeGeneration)
        {
            return new();
        }
        var target = command.RecipientTarget ?? new RuleMessageRecipientTarget
        {
            GameServerId = command.GameServerId,
            SlotId = command.SlotId,
            PlayerGuid = command.PlayerGuid,
            ConnectionId = command.ConnectionId,
            ConnectionGeneration = command.ConnectionGeneration,
            Runtime = command.Runtime
        };
        var response = await repository.RuleConnectionBindings.V1.GetCurrentBinding(
            target.GameServerId, target.SlotId, target.PlayerGuid, ct).ConfigureAwait(false);
        if (response.IsNotFound)
        {
            if (command.RecipientTarget is not null)
            {
                return new(Unavailable: new(ActionExecutionOutcome.Stale, "The snapshotted recipient binding is no longer available."),
                    ConnectionUnavailable: true);
            }
            return clock.GetUtcNow() < waitUntilUtc
                ? new(Deferred: true)
                : new(Unavailable: new(ActionExecutionOutcome.Stale, "No fresh authoritative connection binding became available."),
                    ConnectionUnavailable: true);
        }
        var binding = FeatureExecutionOrchestrator.Require(response, "verify current agent connection ownership");
        var now = clock.GetUtcNow();
        if (binding.GameServerId != target.GameServerId || target.GameServerId != command.GameServerId ||
            binding.SlotId != target.SlotId || !string.Equals(binding.PlayerGuid, target.PlayerGuid, StringComparison.OrdinalIgnoreCase))
        {
            return new(Unavailable: new(ActionExecutionOutcome.Stale, "Authoritative observed connection ownership has changed or expired."),
                ConnectionUnavailable: true);
        }
        if (binding.ObservedAtUtc > now.AddSeconds(30) || binding.ObservedAtUtc < now.AddMinutes(-2) ||
            binding.ExpiresAtUtc != binding.ObservedAtUtc.AddMinutes(2) || binding.ExpiresAtUtc <= now)
        {
            return new(Unavailable: new(ActionExecutionOutcome.Stale, "Authoritative observed connection ownership has changed or expired."),
                ConnectionUnavailable: true);
        }
        if (binding.ConnectionId != target.ConnectionId || binding.ConnectionGeneration != target.ConnectionGeneration ||
            binding.Runtime.Id != target.Runtime.Id || binding.Runtime.IdentityModel != RuleRuntimeIdentityModel.AgentObservedConnection ||
            binding.Runtime.InstanceId != target.Runtime.InstanceId || binding.Runtime.StartedAtUtc != target.Runtime.StartedAtUtc)
        {
            if (command.RecipientTarget is null && command.Correlation.TriggerOccurrenceId != Guid.Empty &&
                command.Correlation.TriggerOccurrenceId == invocation.Trigger.TriggerOccurrenceId &&
                invocation.Trigger.OccurredAtUtc != default && binding.ObservedAtUtc < invocation.Trigger.OccurredAtUtc &&
                now < waitUntilUtc)
            {
                return new(Deferred: true);
            }

            return new(Unavailable: new(ActionExecutionOutcome.Stale, "Authoritative observed connection ownership has changed or expired."),
                ConnectionUnavailable: true);
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
        if (InvalidRecipientTarget(command))
        {
            throw new InvalidOperationException("A moderation or public action cannot use a separate recipient target.");
        }
        if (command.ExpiresAtUtc <= clock.GetUtcNow())
        {
            return new(ActionExecutionOutcome.Stale, "Action decision expired before execution.");
        }

        var definition = RulesEngineCatalogueV1.Current.Actions.Single(item => item.ActionTypeId == action.TypeId);
        Guid? adminActionId = null;
        if (definition.ProducesModerationRecord)
        {
            adminActionId = await records.PersistAsync(action, command, invocation, ct).ConfigureAwait(false);
            if (adminActionId == Guid.Empty)
            {
                throw new InvalidOperationException("The moderation record has no durable admin action identity.");
            }
        }
        if (action.TypeId == "moderation.observation")
        {
            return Complete(ActionExecutionOutcome.Applied, "Moderation observation persisted.");
        }
        if (command.ExpiresAtUtc <= clock.GetUtcNow())
        {
            return Complete(ActionExecutionOutcome.Stale, "The moderation record was persisted, but the native command expired before dispatch.");
        }

        if (command.Runtime.IdentityModel == RuleRuntimeIdentityModel.PluginRuntimeGeneration)
        {
            return Complete(ActionExecutionOutcome.Dispatched, "Awaiting the correlated native plugin result.", command);
        }

        var availability = await NativeAvailabilityAsync(command, invocation, clock.GetUtcNow(), ct).ConfigureAwait(false);
        if (availability.Unavailable is { } unavailable)
        {
            return unavailable with { AdminActionId = adminActionId };
        }
        if (availability.Deferred)
        {
            return Complete(ActionExecutionOutcome.Stale, "Current observed connection ownership is unavailable.");
        }

        // The agent binding is observed identity; the integration rechecks GUID/slot, not native generation.
        var response = await servers.RulesNativeAction.V1.Execute(command.GameServerId, command, ct).ConfigureAwait(false);
        if (!response.IsSuccess || response.Result?.Data is not { } result)
        {
            logger.LogError("Native RCON dispatch has no confirmed result for effect {LogicalEffectId}: {StatusCode}",
                command.LogicalEffectId, response.StatusCode);
            return Complete(ActionExecutionOutcome.OutcomeUnknown, "Native request failed after it may have been dispatched; it will not be retried.");
        }
        if (!Matches(command, result))
        {
            logger.LogError("Native integration returned mismatched correlation for effect {LogicalEffectId}", command.LogicalEffectId);
            return Complete(ActionExecutionOutcome.OutcomeUnknown, "Integration returned a mismatched native result.");
        }
        if (result.Outcome is RuleActionExecutionOutcome.Dispatched or RuleActionExecutionOutcome.Applied)
        {
            if (command.Effect.Type == RuleNativeEffectType.DropPlayer)
            {
                invocation.SuppressWelcome = true;
            }
            return Complete(ActionExecutionOutcome.OutcomeUnknown,
                "RCON send was dispatched once; UDP cannot confirm application or continuing ban enforcement.");
        }
        return result.Outcome switch
        {
            RuleActionExecutionOutcome.Stale => Complete(ActionExecutionOutcome.Stale, result.Detail),
            RuleActionExecutionOutcome.SkippedUnsupported => Complete(ActionExecutionOutcome.SkippedUnsupported, result.Detail),
            RuleActionExecutionOutcome.Failed => Complete(ActionExecutionOutcome.Failed, result.Detail),
            _ => Complete(ActionExecutionOutcome.OutcomeUnknown, result.Detail ?? "Integration did not confirm a terminal native outcome.")
        };

        ActionExecutionResult Complete(ActionExecutionOutcome outcome, string? detail, RuleActionCommand? nativeCommand = null) =>
            new(outcome, detail, nativeCommand, adminActionId);
    }

    private static bool InvalidRecipientTarget(RuleActionCommand command) =>
        command.RecipientTarget is { } target &&
        (command.Effect.Type != RuleNativeEffectType.PrivateMessage || target.GameServerId != command.GameServerId ||
            target.ConnectionId == Guid.Empty || target.SlotId < 0 || string.IsNullOrWhiteSpace(target.PlayerGuid));

    private static bool Matches(RuleActionCommand command, RuleActionResult result) =>
        result.GameServerId == command.GameServerId && result.ExecutionId == command.ExecutionId &&
        result.LogicalEffectId == command.LogicalEffectId && result.Correlation == command.Correlation &&
        result.ActionId == command.ActionId;
}
