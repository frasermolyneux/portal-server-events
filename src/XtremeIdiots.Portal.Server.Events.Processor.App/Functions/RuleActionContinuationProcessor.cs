using System.Text.Json;

using Azure.Messaging.ServiceBus;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.AdminActions;
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
    IRulesExecutionResumer rulesResumer)
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

        var continuation = JsonSerializer.Deserialize<RuleActionContinuation>(message.Body, JsonOptions.Default)
            ?? throw new InvalidOperationException("Rule action continuation is null.");

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
