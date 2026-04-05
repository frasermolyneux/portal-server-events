using System.Text.Json;

using Azure.Messaging.ServiceBus;

using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

public class PlayerConnectedProcessor(
    ILogger<PlayerConnectedProcessor> logger,
    IRepositoryApiClient repositoryApiClient,
    IMemoryCache memoryCache,
    TelemetryClient telemetryClient)
{
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PlayerCacheExpiration = TimeSpan.FromMinutes(15);

    [Function(nameof(ProcessPlayerConnected))]
    public async Task ProcessPlayerConnected(
        [ServiceBusTrigger(Queues.PlayerConnected, Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        FunctionContext context)
    {
        PlayerConnectedEvent? playerEvent;
        try
        {
            playerEvent = JsonSerializer.Deserialize<PlayerConnectedEvent>(message.Body, JsonOptions.Default);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "PlayerConnected message was not in expected format. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (playerEvent is null)
        {
            logger.LogWarning("PlayerConnected deserialized to null. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (string.IsNullOrWhiteSpace(playerEvent.GameType) ||
            string.IsNullOrWhiteSpace(playerEvent.PlayerGuid) ||
            string.IsNullOrWhiteSpace(playerEvent.Username))
        {
            logger.LogWarning("PlayerConnected missing required fields. GameType: {GameType}, PlayerGuid: {PlayerGuid}, Username: {Username}",
                playerEvent.GameType, playerEvent.PlayerGuid, playerEvent.Username);
            return;
        }

        if (playerEvent.ServerId == Guid.Empty)
        {
            logger.LogWarning("PlayerConnected has empty ServerId. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (!Enum.TryParse<GameType>(playerEvent.GameType, out var gameType))
        {
            logger.LogWarning("PlayerConnected has invalid GameType: {GameType}", playerEvent.GameType);
            return;
        }

        if (playerEvent.IsStale(StaleThreshold))
        {
            logger.LogWarning("PlayerConnected event is stale ({Age} old). ServerId: {ServerId}, PlayerGuid: {PlayerGuid}",
                DateTime.UtcNow - playerEvent.EventGeneratedUtc, playerEvent.ServerId, playerEvent.PlayerGuid);
            return;
        }

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["GameType"] = playerEvent.GameType,
            ["ServerId"] = playerEvent.ServerId,
            ["PlayerGuid"] = playerEvent.PlayerGuid
        });

        // Check if player exists
        var playerExistsResponse = await repositoryApiClient.Players.V1
            .HeadPlayerByGameType(gameType, playerEvent.PlayerGuid)
            .ConfigureAwait(false);

        if (playerExistsResponse.IsNotFound)
        {
            var createPlayerDto = new CreatePlayerDto(
                playerEvent.Username,
                playerEvent.PlayerGuid,
                gameType)
            {
                IpAddress = playerEvent.IpAddress
            };

            var createResult = await repositoryApiClient.Players.V1
                .CreatePlayer(createPlayerDto)
                .ConfigureAwait(false);

            if (createResult.IsConflict)
            {
                // Race condition: another event created the player; fall through to update
                logger.LogInformation("Player creation returned 409 Conflict, falling through to update");
            }
            else if (createResult.IsSuccess)
            {
                InvalidatePlayerCache(gameType, playerEvent.PlayerGuid);
                TrackEvent("PlayerCreated", playerEvent);
                return;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Failed to create player. API returned {createResult.StatusCode}.");
            }
        }

        // Get player ID for update
        var playerId = await GetPlayerId(gameType, playerEvent.PlayerGuid).ConfigureAwait(false);

        if (playerId == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"Player not found after HEAD success for Guid '{playerEvent.PlayerGuid}'. Will retry.");
        }

        var editPlayerDto = new EditPlayerDto(playerId)
        {
            Username = playerEvent.Username,
            IpAddress = playerEvent.IpAddress
        };

        await repositoryApiClient.Players.V1.UpdatePlayer(editPlayerDto).ConfigureAwait(false);
        InvalidatePlayerCache(gameType, playerEvent.PlayerGuid);
        TrackEvent("PlayerConnected", playerEvent);
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

    private void TrackEvent(string eventName, PlayerConnectedEvent playerEvent)
    {
        telemetryClient.TrackEvent(new EventTelemetry(eventName)
        {
            Properties =
            {
                ["GameType"] = playerEvent.GameType,
                ["ServerId"] = playerEvent.ServerId.ToString(),
                ["PlayerGuid"] = playerEvent.PlayerGuid,
                ["Username"] = playerEvent.Username
            }
        });
    }
}
