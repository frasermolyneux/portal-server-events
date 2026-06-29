using System.Text.Json;

using Azure.Messaging.ServiceBus;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.AdminActions;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.GameServers;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Server.Events.Processor.App.Services;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

/// <summary>
/// Processes ban detection events from the agent. For each new ban entry,
/// creates the player (if not found) and an AdminAction record of type Ban.
/// </summary>
public sealed class BanFileChangedProcessor(
    ILogger<BanFileChangedProcessor> logger,
    IRepositoryApiClient repositoryApiClient,
    IAdminActionTopics adminActionTopics,
    IAuditLogger auditLogger)
{
    [Function(nameof(ProcessBanFileChanged))]
    public async Task ProcessBanFileChanged(
        [ServiceBusTrigger(Queues.BanFileChanged, Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        FunctionContext context)
    {
        BanDetectedEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<BanDetectedEvent>(message.Body, JsonOptions.Default);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "BanDetected message was not in expected format. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (evt is null)
        {
            logger.LogWarning("BanDetected deserialized to null. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (string.IsNullOrWhiteSpace(evt.GameType))
        {
            logger.LogWarning("BanDetected missing GameType. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (evt.ServerId == Guid.Empty)
        {
            logger.LogWarning("BanDetected has empty ServerId. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (evt.NewBans is null || evt.NewBans.Count == 0)
        {
            logger.LogWarning("BanDetected has no bans. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (!Enum.TryParse<GameType>(evt.GameType, out var gameType))
        {
            logger.LogWarning("BanDetected has invalid GameType: {GameType}", evt.GameType);
            return;
        }

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["GameType"] = evt.GameType,
            ["ServerId"] = evt.ServerId
        });

        logger.LogInformation("Processing {Count} ban(s) for server {ServerId}", evt.NewBans.Count, evt.ServerId);

        var processed = new List<DetectedBan>(evt.NewBans.Count);

        foreach (var ban in evt.NewBans)
        {
            if (string.IsNullOrWhiteSpace(ban.PlayerGuid) || string.IsNullOrWhiteSpace(ban.PlayerName))
            {
                logger.LogWarning("Skipping ban entry with missing GUID or name");
                continue;
            }

            try
            {
                var imported = await ProcessSingleBanAsync(gameType, evt.ServerId, ban, context.CancellationToken).ConfigureAwait(false);
                if (imported)
                {
                    processed.Add(ban);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process ban for player {PlayerGuid}", ban.PlayerGuid);
                throw;
            }
        }

        if (processed.Count > 0)
        {
            await RecordManualBansDetectedAsync(evt.ServerId, processed, context.CancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes a <c>ManualBanDetected</c> game-server event so the manual ban additions made
    /// directly on a server (and ingested via the agent's BanFileWatcher) are visible on the
    /// portal-web Server Events page. Failures are logged and swallowed — the bans themselves
    /// have already been persisted as AdminActions, so a missing UI event must not roll them back.
    /// </summary>
    private async Task RecordManualBansDetectedAsync(Guid serverId, IReadOnlyList<DetectedBan> bans, CancellationToken ct)
    {
        var eventData = JsonSerializer.Serialize(new
        {
            Count = bans.Count,
            Players = bans.Select(b => new { PlayerGuid = b.PlayerGuid.Trim(), b.PlayerName }).ToArray()
        }, JsonOptions.Default);

        try
        {
            var result = await repositoryApiClient.GameServersEvents.V1
                .CreateGameServerEvent(
                    new CreateGameServerEventDto(serverId, "ManualBanDetected", eventData),
                    ct)
                .ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                logger.LogWarning(
                    "Failed to write ManualBanDetected game server event for server {ServerId}. Status: {StatusCode}",
                    serverId,
                    result.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write ManualBanDetected game server event for server {ServerId}", serverId);
        }
    }

    internal async Task<bool> ProcessSingleBanAsync(
        GameType gameType,
        Guid serverId,
        DetectedBan ban,
        CancellationToken ct)
    {
        var normalizedPlayerGuid = ban.PlayerGuid.Trim();

        // Check if player exists
        var playerExistsResponse = await repositoryApiClient.Players.V1
            .HeadPlayerByGameType(gameType, normalizedPlayerGuid)
            .ConfigureAwait(false);

        Guid playerId;

        if (playerExistsResponse.IsNotFound)
        {
            // Create the player first
            var createPlayerDto = new CreatePlayerDto(ban.PlayerName, normalizedPlayerGuid, gameType);

            var createResult = await repositoryApiClient.Players.V1
                .CreatePlayer(createPlayerDto)
                .ConfigureAwait(false);

            if (createResult.IsConflict)
            {
                logger.LogInformation("Player creation returned 409 Conflict for {PlayerGuid}, falling through to lookup",
                    ban.PlayerGuid);
            }
            else if (!createResult.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"Failed to create player for GUID '{normalizedPlayerGuid}'. API returned {createResult.StatusCode}.");
            }
            else
            {
                auditLogger.LogAudit(AuditEvent.ServerAction("BanPlayerCreated", AuditAction.Create)
                    .WithGameContext(gameType.ToString(), serverId)
                    .WithPlayer(normalizedPlayerGuid, ban.PlayerName)
                    .Build());
            }
        }

        // Look up the player to get their ID
        var playerResponse = await repositoryApiClient.Players.V1
            .GetPlayerByGameType(gameType, normalizedPlayerGuid, PlayerEntityOptions.None)
            .ConfigureAwait(false);

        if (!playerResponse.IsSuccess || playerResponse.Result?.Data is null)
        {
            throw new InvalidOperationException(
                $"Player not found for GUID '{normalizedPlayerGuid}' after creation. Will retry.");
        }

        var player = playerResponse.Result.Data;
        playerId = player.PlayerId;

        // Check for existing active ban using server-side filter
        var activeBansResult = await repositoryApiClient.AdminActions.V1
            .GetAdminActions(gameType, playerId, null, AdminActionFilter.ActiveBans, 0, 1, null, ct)
            .ConfigureAwait(false);

        if (!activeBansResult.IsSuccess)
        {
            logger.LogWarning(
                "Skipping imported ban for player {PlayerGuid}: failed to check active bans (status {StatusCode})",
                normalizedPlayerGuid,
                activeBansResult.StatusCode);
            return false;
        }

        var hasActiveBan = activeBansResult.Result?.Data?.Items?.Any() == true;

        if (hasActiveBan)
        {
            logger.LogInformation("Player {PlayerGuid} already has an active ban, skipping", normalizedPlayerGuid);
            return false;
        }

        var adminActionType = AdminActionType.Ban;
        DateTime? expiresAtUtc = null;

        // Create forum topic (best-effort)
        var forumTopicId = await adminActionTopics.CreateTopicForAdminAction(
            adminActionType, gameType, playerId, player.Username,
            DateTime.UtcNow, "Imported from server ban file", null, ct).ConfigureAwait(false);

        // Create the ban admin action
        var adminAction = new CreateAdminActionDto(playerId, adminActionType, "Imported from server ban file")
        {
            ForumTopicId = forumTopicId > 0 ? forumTopicId : null,
            Expires = expiresAtUtc
        };
        var adminResult = await repositoryApiClient.AdminActions.V1
            .CreateAdminAction(adminAction, ct)
            .ConfigureAwait(false);

        if (adminResult.IsSuccess)
        {
            logger.LogInformation("Created ban for player {PlayerGuid} ({PlayerName}) on server {ServerId}",
                normalizedPlayerGuid, ban.PlayerName, serverId);
            auditLogger.LogAudit(AuditEvent.ServerAction("BanImported", AuditAction.Import)
                .WithService("BanFileProcessor")
                .WithGameContext(gameType.ToString(), serverId)
                .WithPlayer(normalizedPlayerGuid, ban.PlayerName)
                .Build());
            return true;
        }

        logger.LogWarning("Failed to create ban for player {PlayerGuid}. Status: {StatusCode}",
            normalizedPlayerGuid, adminResult.StatusCode);
        return false;
    }
}
