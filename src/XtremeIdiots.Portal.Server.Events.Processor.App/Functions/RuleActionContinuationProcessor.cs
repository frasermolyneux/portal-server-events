using System.Text.Json;

using Azure.Messaging.ServiceBus;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.AdminActions;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;
using XtremeIdiots.Portal.Server.Events.Processor.App.Services;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

public sealed class RuleActionContinuationProcessor(
    ILogger<RuleActionContinuationProcessor> logger,
    IRepositoryApiClient repositoryApiClient,
    IAdminActionTopics adminActionTopics,
    IAuditLogger auditLogger,
    IRulesExecutionResumer rulesResumer,
    ServiceBusClient serviceBusClient)
{
    [Function(nameof(ProcessRuleActionContinuation))]
    public async Task ProcessRuleActionContinuation(
        [ServiceBusTrigger(Queues.RuleActionContinuations, Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        ct.ThrowIfCancellationRequested();
        if (message.ApplicationProperties.TryGetValue("kind", out var kind) &&
            string.Equals(kind as string, RuleExecutionContinuationPublisher.MessageKind, StringComparison.Ordinal))
        {
            var resume = JsonSerializer.Deserialize<RuleExecutionContinuation>(message.Body, JsonOptions.Default)
                ?? throw new InvalidOperationException("Rules execution continuation is null.");
            if (resume.GameServerId == Guid.Empty || resume.ExecutionId == Guid.Empty)
            {
                throw new InvalidOperationException("Rules execution continuation has no server or execution identity.");
            }
            await rulesResumer.ResumeAsync(resume.GameServerId, resume.ExecutionId, ct).ConfigureAwait(false);
            return;
        }

        var intent = JsonSerializer.Deserialize<RuleActionPublicationIntent>(message.Body, JsonOptions.Default)
            ?? throw new InvalidOperationException("Rule publication intent is null.");
        if (intent.GameServerId == Guid.Empty || intent.ExecutionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(intent.ActionId) || string.IsNullOrWhiteSpace(intent.LogicalEffectId))
        {
            throw new InvalidOperationException("Rule publication intent has no action identity.");
        }
        var execution = FeatureExecutionOrchestrator.Require(await repositoryApiClient.RuleExecutions.V1.GetExecution(
            intent.GameServerId, intent.ExecutionId, ct).ConfigureAwait(false), "load forum publication action");
        var action = execution.Actions.SingleOrDefault(item =>
            item.ActionId == intent.ActionId && item.LogicalEffectId == intent.LogicalEffectId);
        if (execution.GameServerId != intent.GameServerId || execution.ExecutionId != intent.ExecutionId ||
            action is null || action.Mode != RuleExecutionMode.Enforce)
        {
            throw new InvalidOperationException("Forum publication does not match an enforcing action.");
        }
        if (action.AdminActionId is null)
        {
            if (action.Outcome == RuleActionExecutionOutcome.Claimed)
            {
                if (action.Command.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                {
                    throw new InvalidOperationException("Forum publication action expired without a linked record; inspect the durable claim.");
                }
                await using var publications = new RuleActionContinuationPublisher(serviceBusClient);
                await publications.ScheduleAsync(intent, DateTimeOffset.UtcNow.AddSeconds(15), ct).ConfigureAwait(false);
                return;
            }
            logger.LogWarning("Forum publication has no moderation record for effect {LogicalEffectId}, execution {ExecutionId}, action {ActionId}: {Outcome}",
                intent.LogicalEffectId, intent.ExecutionId, intent.ActionId, action.Outcome);
            return;
        }
        var record = FeatureExecutionOrchestrator.Require(await repositoryApiClient.AdminActions.V1.GetAdminAction(
            action.AdminActionId.Value, ct).ConfigureAwait(false), "load linked forum publication record");
        if (record.AdminActionId != action.AdminActionId)
        {
            throw new InvalidOperationException("Forum publication returned a different moderation record.");
        }
        var continuation = new RuleActionContinuation(
            intent.LogicalEffectId, intent.ExecutionId, intent.ActionId, record.AdminActionId, intent.GameServerId,
            execution.Trigger.GameType, record.PlayerId, execution.Trigger.Username ?? record.Player?.Username,
            record.Type, record.Created, record.Text, intent.AdminId);

        if (string.IsNullOrWhiteSpace(continuation.LogicalEffectId) ||
            string.IsNullOrWhiteSpace(continuation.ActionId) ||
            continuation.ExecutionId == Guid.Empty || continuation.AdminActionId == Guid.Empty ||
            continuation.GameServerId == Guid.Empty || continuation.PlayerId == Guid.Empty ||
            string.IsNullOrWhiteSpace(continuation.Text) ||
            continuation.Created == default || !Enum.IsDefined(continuation.GameType) ||
            !Enum.IsDefined(continuation.ActionType))
        {
            throw new InvalidOperationException("Rule action continuation is missing required publication data.");
        }

        var username = continuation.Username;
        if (string.IsNullOrWhiteSpace(username))
        {
            var player = FeatureExecutionOrchestrator.Require(await repositoryApiClient.Players.V1.GetPlayer(
                continuation.PlayerId, PlayerEntityOptions.None).WaitAsync(ct).ConfigureAwait(false), "load forum publication player");
            if (player.PlayerId != continuation.PlayerId || string.IsNullOrWhiteSpace(player.Username))
            {
                throw new InvalidOperationException("Forum publication player identity is unavailable.");
            }
            username = player.Username;
        }

        var claimResult = await repositoryApiClient.AdminActions.V1
            .ClaimForumTopicPublication(continuation.AdminActionId, ct).ConfigureAwait(false);
        if (!claimResult.IsSuccess || claimResult.Result?.Data is null)
        {
            throw new InvalidOperationException($"Failed to claim forum topic publication for {continuation.AdminActionId}. Status: {claimResult.StatusCode}");
        }

        var claim = claimResult.Result.Data;
        if (claim.ForumTopicId.HasValue)
        {
            return;
        }

        if (claim.RequiresManualRecovery || !claim.ClaimId.HasValue)
        {
            logger.LogError(
                "Forum topic publication requires manual recovery for admin action {AdminActionId}, effect {LogicalEffectId}, execution {ExecutionId}, action {ActionId}",
                continuation.AdminActionId, continuation.LogicalEffectId, continuation.ExecutionId, continuation.ActionId);
            return;
        }

        int forumTopicId;
        try
        {
            forumTopicId = await adminActionTopics.CreateTopicForAdminAction(
                continuation.ActionType, continuation.GameType, continuation.PlayerId, username,
                continuation.Created, continuation.Text, continuation.AdminId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Forum publication outcome unknown; manual recovery may be required for admin action {AdminActionId}, effect {LogicalEffectId}",
                continuation.AdminActionId, continuation.LogicalEffectId);
            throw;
        }

        if (forumTopicId <= 0)
        {
            logger.LogError("Forum publication outcome unknown; manual recovery may be required for admin action {AdminActionId}, effect {LogicalEffectId}",
                continuation.AdminActionId, continuation.LogicalEffectId);
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Failed to create forum topic.");
        }

        MX.Api.Abstractions.ApiResult completeResult;
        try
        {
            completeResult = await repositoryApiClient.AdminActions.V1
                .CompleteForumTopicPublication(continuation.AdminActionId,
                    new CompleteForumTopicPublicationDto(claim.ClaimId.Value, forumTopicId), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to link forum topic {ForumTopicId} to admin action {AdminActionId}; manual recovery may be required",
                forumTopicId, continuation.AdminActionId);
            throw;
        }
        if (!completeResult.IsSuccess)
        {
            logger.LogError("Failed to link forum topic {ForumTopicId} to admin action {AdminActionId}; manual recovery may be required",
                forumTopicId, continuation.AdminActionId);
            throw new InvalidOperationException($"Failed to complete forum topic publication. Status: {completeResult.StatusCode}");
        }

        auditLogger.LogAudit(AuditEvent.ServerAction("RuleActionForumTopicPublished", AuditAction.Update)
            .WithGameContext(continuation.GameType.ToString(), continuation.GameServerId)
            .WithProperty("AdminActionId", continuation.AdminActionId.ToString())
            .WithProperty("ForumTopicId", forumTopicId.ToString())
            .WithProperty("LogicalEffectId", continuation.LogicalEffectId)
            .WithProperty("ExecutionId", continuation.ExecutionId.ToString())
            .WithProperty("ActionId", continuation.ActionId)
            .Build());
    }
}
