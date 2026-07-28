using System.Text.Json;

using Azure.Messaging.ServiceBus;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.AdminActions;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.GameServers;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Services;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

public class BanAppliedProcessor(
    ILogger<BanAppliedProcessor> logger,
    IRepositoryApiClient repositoryApiClient,
    IAdminActionTopics adminActionTopics,
    IAuditLogger auditLogger)
{
    [Function(nameof(ProcessBanApplied))]
    public async Task ProcessBanApplied(
        [ServiceBusTrigger(Queues.BanApplied, Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        FunctionContext context)
    {
        BanAppliedEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<BanAppliedEvent>(message.Body, JsonOptions.Default);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "BanApplied message was not in expected format. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (evt is null)
        {
            logger.LogWarning("BanApplied deserialized to null. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (evt.ServerId == Guid.Empty)
        {
            logger.LogWarning("BanApplied has empty ServerId. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (string.IsNullOrWhiteSpace(evt.GameType))
        {
            logger.LogWarning("BanApplied has empty GameType. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (!Enum.TryParse<GameType>(evt.GameType, out var gameType))
        {
            logger.LogWarning("BanApplied has invalid GameType: {GameType}", evt.GameType);
            return;
        }

        if (string.IsNullOrWhiteSpace(evt.PlayerGuid) ||
            string.IsNullOrWhiteSpace(evt.PlayerName) ||
            string.IsNullOrWhiteSpace(evt.Source) ||
            string.IsNullOrWhiteSpace(evt.Reason))
        {
            logger.LogWarning(
                "BanApplied missing required fields. PlayerGuid: {PlayerGuid}, PlayerName: {PlayerName}, Source: {Source}",
                evt.PlayerGuid,
                evt.PlayerName,
                evt.Source);
            return;
        }

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["GameType"] = evt.GameType,
            ["ServerId"] = evt.ServerId,
            ["PlayerGuid"] = evt.PlayerGuid
        });

        var eventData = JsonSerializer.Serialize(evt, JsonOptions.Default);

        var gameServerEventDto = new CreateGameServerEventDto(
            evt.ServerId,
            "BanApplied",
            eventData);

        var result = await repositoryApiClient.GameServersEvents.V1
            .CreateGameServerEvent(gameServerEventDto)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            logger.LogWarning(
                "Failed to persist BanApplied game server event for server {ServerId}. Status: {StatusCode}",
                evt.ServerId,
                result.StatusCode);
            throw new InvalidOperationException($"Failed to persist BanApplied game server event. Status: {result.StatusCode}");
        }

        if (string.Equals(evt.Source, BanImportSources.RconDumpBanList, StringComparison.Ordinal)
            || string.Equals(evt.Source, BanImportSources.Cod4xVpnProtection, StringComparison.Ordinal))
        {
            await ImportRconDumpBanListActionAsync(gameType, evt, context.CancellationToken).ConfigureAwait(false);
        }

        auditLogger.LogAudit(AuditEvent.ServerAction("BanApplied", AuditAction.Update)
            .WithGameContext(evt.GameType, evt.ServerId)
            .WithPlayer(evt.PlayerGuid, evt.PlayerName)
            .WithProperty("Source", evt.Source)
            .WithProperty("Reason", evt.Reason)
            .Build());
    }

    private async Task ImportRconDumpBanListActionAsync(GameType gameType, BanAppliedEvent evt, CancellationToken ct)
    {
        var playerExists = await repositoryApiClient.Players.V1
            .HeadPlayerByGameType(gameType, evt.PlayerGuid)
            .ConfigureAwait(false);

        if (playerExists.IsNotFound)
        {
            var createResult = await repositoryApiClient.Players.V1
                .CreatePlayer(new CreatePlayerDto(evt.PlayerName, evt.PlayerGuid, gameType))
                .ConfigureAwait(false);

            if (!createResult.IsSuccess && !createResult.IsConflict)
            {
                throw new InvalidOperationException($"Failed to create player for RCON dumpbanlist import. Status: {createResult.StatusCode}");
            }
        }

        var playerResult = await repositoryApiClient.Players.V1
            .GetPlayerByGameType(gameType, evt.PlayerGuid, PlayerEntityOptions.None)
            .ConfigureAwait(false);

        if (!playerResult.IsSuccess || playerResult.Result?.Data is null)
        {
            throw new InvalidOperationException("Player could not be resolved for RCON dumpbanlist import.");
        }

        var player = playerResult.Result.Data;
        var actionType = evt.IsTemporary ? AdminActionType.TempBan : AdminActionType.Ban;
        var ensureResult = await repositoryApiClient.AdminActions.V1
            .EnsureAutomatedAction(
                new EnsureAutomatedActionDto(
                    player.PlayerId,
                    actionType,
                    evt.Reason,
                    AutomationFeature.RconBanImport,
                    BuildRconImportRuleId(evt))
                {
                    Expires = evt.IsTemporary ? evt.ExpiresUtc : null
                },
                ct)
            .ConfigureAwait(false);

        if (!ensureResult.IsSuccess || ensureResult.Result?.Data?.AdminAction is null)
        {
            throw new InvalidOperationException($"Failed to ensure RCON dumpbanlist admin action. Status: {ensureResult.StatusCode}");
        }

        var adminAction = ensureResult.Result.Data.AdminAction;
        var claimResult = await repositoryApiClient.AdminActions.V1
            .ClaimForumTopicPublication(adminAction.AdminActionId, ct)
            .ConfigureAwait(false);

        if (!claimResult.IsSuccess || claimResult.Result?.Data is null)
        {
            throw new InvalidOperationException($"Failed to claim RCON dumpbanlist forum topic publication. Status: {claimResult.StatusCode}");
        }

        var claim = claimResult.Result.Data;
        if (claim.ForumTopicId.HasValue)
        {
            return;
        }

        if (claim.RequiresManualRecovery || !claim.ClaimId.HasValue)
        {
            logger.LogError(
                "RCON dumpbanlist import for action {AdminActionId} requires manual forum-topic recovery before another post can be attempted",
                adminAction.AdminActionId);
            return;
        }

        var created = evt.EventGeneratedUtc == default ? DateTime.UtcNow : evt.EventGeneratedUtc;
        var forumTopicId = await adminActionTopics
            .CreateTopicForAdminAction(actionType, gameType, player.PlayerId, player.Username, created, evt.Reason, null, ct)
            .ConfigureAwait(false);

        if (forumTopicId <= 0)
        {
            throw new InvalidOperationException("Failed to create forum topic for RCON dumpbanlist import.");
        }

        var completeResult = await repositoryApiClient.AdminActions.V1
            .CompleteForumTopicPublication(
                adminAction.AdminActionId,
                new CompleteForumTopicPublicationDto(claim.ClaimId.Value, forumTopicId),
                ct)
            .ConfigureAwait(false);

        if (!completeResult.IsSuccess)
        {
            throw new InvalidOperationException($"Failed to complete RCON dumpbanlist forum topic publication. Status: {completeResult.StatusCode}");
        }
    }

    private static string BuildRconImportRuleId(BanAppliedEvent evt)
        => $"{evt.ServerId:N}:{evt.PlayerGuid}";
}
