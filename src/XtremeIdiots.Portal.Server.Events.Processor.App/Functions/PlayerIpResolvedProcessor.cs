using System.Text.Json;

using Azure.Messaging.ServiceBus;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

/// <summary>
/// Processes PlayerIpResolved events emitted by the agent when RCON sync discovers a player's IP.
/// Persists the IP to the Players table via the dedicated UpdatePlayerIpAddress endpoint.
/// </summary>
public class PlayerIpResolvedProcessor(
    ILogger<PlayerIpResolvedProcessor> logger,
    IRepositoryApiClient repositoryApiClient,
    IMemoryCache memoryCache,
    IAuditLogger auditLogger)
{
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PlayerCacheExpiration = TimeSpan.FromMinutes(15);

    [Function(nameof(ProcessPlayerIpResolved))]
    public async Task ProcessPlayerIpResolved(
        [ServiceBusTrigger(Queues.PlayerIpResolved, Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        FunctionContext context)
    {
        PlayerIpResolvedEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<PlayerIpResolvedEvent>(message.Body, JsonOptions.Default);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "PlayerIpResolved message was not in expected format. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (evt is null)
        {
            logger.LogWarning("PlayerIpResolved deserialized to null. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (string.IsNullOrWhiteSpace(evt.GameType) ||
            string.IsNullOrWhiteSpace(evt.PlayerGuid) ||
            string.IsNullOrWhiteSpace(evt.IpAddress))
        {
            logger.LogWarning("PlayerIpResolved missing required fields. GameType: {GameType}, PlayerGuid: {PlayerGuid}, IpAddress: {IpAddress}",
                evt.GameType, evt.PlayerGuid, evt.IpAddress);
            return;
        }

        if (evt.ServerId == Guid.Empty)
        {
            logger.LogWarning("PlayerIpResolved has empty ServerId. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (!Enum.TryParse<GameType>(evt.GameType, out var gameType))
        {
            logger.LogWarning("PlayerIpResolved has invalid GameType: {GameType}", evt.GameType);
            return;
        }

        if (evt.IsStale(StaleThreshold))
        {
            logger.LogWarning("PlayerIpResolved event is stale ({Age} old). ServerId: {ServerId}, PlayerGuid: {PlayerGuid}",
                DateTime.UtcNow - evt.EventGeneratedUtc, evt.ServerId, evt.PlayerGuid);
            return;
        }

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["GameType"] = evt.GameType,
            ["ServerId"] = evt.ServerId,
            ["PlayerGuid"] = evt.PlayerGuid
        });

        var playerId = await GetPlayerId(gameType, evt.PlayerGuid).ConfigureAwait(false);

        if (playerId == Guid.Empty)
        {
            logger.LogDebug("Player not yet created for {PlayerGuid}, skipping IP persistence", evt.PlayerGuid);
            return;
        }

        try
        {
            await repositoryApiClient.Players.V1
                .UpdatePlayerIpAddress(new UpdatePlayerIpAddressDto(playerId, evt.IpAddress))
                .ConfigureAwait(false);

            InvalidatePlayerCache(gameType, evt.PlayerGuid);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist IP {IpAddress} for player {PlayerGuid}", evt.IpAddress, evt.PlayerGuid);
            return;
        }

        auditLogger.LogAudit(AuditEvent.ServerAction("PlayerIpResolved", AuditAction.Update)
            .WithGameContext(evt.GameType, evt.ServerId)
            .WithPlayer(evt.PlayerGuid, null)
            .WithProperty("IpAddress", evt.IpAddress)
            .Build());

        logger.LogInformation("Persisted IP {IpAddress} for player {PlayerGuid}", evt.IpAddress, evt.PlayerGuid);
    }

    private async Task<Guid> GetPlayerId(GameType gameType, string guid)
    {
        var cacheKey = $"player-id-{gameType}-{guid}";

        if (memoryCache.TryGetValue(cacheKey, out Guid cachedId))
            return cachedId;

        var response = await repositoryApiClient.Players.V1
            .GetPlayerByGameType(gameType, guid, PlayerEntityOptions.None)
            .ConfigureAwait(false);

        if (!response.IsSuccess || response.Result?.Data is null)
            return Guid.Empty;

        var playerId = response.Result.Data.PlayerId;
        memoryCache.Set(cacheKey, playerId,
            new MemoryCacheEntryOptions().SetSlidingExpiration(PlayerCacheExpiration));

        return playerId;
    }

    private void InvalidatePlayerCache(GameType gameType, string guid)
    {
        memoryCache.Remove($"player-id-{gameType}-{guid}");
        memoryCache.Remove($"player-ctx-{gameType}-{guid}");
    }
}
