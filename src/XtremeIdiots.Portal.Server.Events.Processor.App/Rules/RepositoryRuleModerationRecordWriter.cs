using System.Text.Json;

using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.RulesEngine;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

internal sealed class RepositoryRuleModerationRecordWriter(
    IRepositoryApiClient repository,
    IRuleActionContinuationPublisher continuations,
    IAuditLogger audit) : IRuleModerationRecordWriter
{
    public async Task<Guid> PersistAsync(
        RuleActionDefinition action, RuleActionCommand command, RuleInvocation invocation, CancellationToken ct)
    {
        if (command.Mode != RuleExecutionMode.Enforce)
        {
            throw new InvalidOperationException("Dry-run actions cannot create moderation records.");
        }
        var type = action.TypeId switch
        {
            "moderation.observation" => AdminActionType.Observation,
            "moderation.kick-with-record" => AdminActionType.Kick,
            "moderation.temporary-ban" => AdminActionType.TempBan,
            "moderation.ban" => AdminActionType.Ban,
            _ => throw new InvalidOperationException("The action does not produce a moderation record.")
        };
        if (!action.Parameters.TryGetValue("reason", out var reason) || reason.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(reason.GetString()))
        {
            throw new InvalidOperationException("A moderation record requires a reason.");
        }
        var execution = FeatureExecutionOrchestrator.Require(await repository.RuleExecutions.V1.GetExecution(
            command.GameServerId, command.ExecutionId, ct).ConfigureAwait(false), "load moderation action claim");
        var claim = execution.Actions.SingleOrDefault(item => item.LogicalEffectId == command.LogicalEffectId);
        if (execution.GameServerId != command.GameServerId || execution.ConnectionId != command.ConnectionId ||
            claim is null || claim.Correlation != command.Correlation || claim.ActionTypeId != action.TypeId ||
            claim.Mode != RuleExecutionMode.Enforce || claim.Outcome != RuleActionExecutionOutcome.Claimed)
        {
            throw new InvalidOperationException("The moderation record does not match a live action claim.");
        }
        DateTime? expires = null;
        if (type == AdminActionType.TempBan)
        {
            if (!action.Parameters.TryGetValue("durationMinutes", out var duration) ||
                !duration.TryGetInt32(out var minutes) || minutes is < 1 or > 525600)
            {
                throw new InvalidOperationException("A temporary ban requires a valid duration.");
            }
            expires = claim.ClaimedAtUtc.AddMinutes(minutes).UtcDateTime;
        }

        var result = FeatureExecutionOrchestrator.Require(await repository.RuleExecutions.V1.EnsureRuleActionAdminAction(
            command.GameServerId, command.ExecutionId, command.ActionId, new EnsureRuleActionAdminActionDto
            {
                ExpectedVersion = execution.Version,
                LogicalEffectId = command.LogicalEffectId,
                Type = type,
                Text = reason.GetString()!,
                Expires = expires
            }, ct).ConfigureAwait(false), "persist and link idempotent rule moderation record");
        var record = result.AdminAction;
        if (record is null || record.AdminActionId == Guid.Empty || record.PlayerId == Guid.Empty || record.Type != type)
        {
            throw new InvalidOperationException("The repository returned an invalid rule moderation record.");
        }
        if (result.Created)
        {
            audit.LogAudit(AuditEvent.ServerAction("RuleModerationRecordCreated", AuditAction.Create)
                .WithGameContext(invocation.Trigger.GameType.ToString(), command.GameServerId)
                .WithPlayer(command.PlayerGuid, invocation.Trigger.Username)
                .WithProperty("AdminActionId", record.AdminActionId.ToString())
                .WithProperty("ExecutionId", command.ExecutionId.ToString())
                .WithProperty("LogicalEffectId", command.LogicalEffectId)
                .WithProperty("ActionId", command.ActionId)
                .WithProperty("Type", type.ToString())
                .Build());
        }
        if (action.Parameters.TryGetValue("postToForums", out var forums) && forums.ValueKind == JsonValueKind.True &&
            record.ForumTopicId is null)
        {
            var username = invocation.Trigger.Username ?? record.Player?.Username;
            await continuations.PublishAsync(new(
                command.LogicalEffectId, command.ExecutionId, command.ActionId, record.AdminActionId,
                command.GameServerId, invocation.Trigger.GameType, record.PlayerId, username, record.Type,
                record.Created, record.Text, null), ct).ConfigureAwait(false);
        }
        return record.AdminActionId;
    }
}
