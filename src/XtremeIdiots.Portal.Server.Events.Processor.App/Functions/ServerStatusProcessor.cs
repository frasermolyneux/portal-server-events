using System.Text.Json;

using Azure.Messaging.ServiceBus;

using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using MX.GeoLocation.Api.Client.V1;

using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.GameServers;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.LiveStatus;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RecentPlayers;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

/// <summary>
/// Processes server status snapshot events published by game server agents every 60 seconds.
/// Updates live server status in Table Storage via the LiveStatus API,
/// and creates player count snapshots for analytics.
/// </summary>
public sealed class ServerStatusProcessor(
    ILogger<ServerStatusProcessor> logger,
    IRepositoryApiClient repositoryApiClient,
    IGeoLocationApiClient geoLocationApiClient,
    IMemoryCache memoryCache,
    TelemetryClient telemetryClient)
{
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PlayerCacheExpiration = TimeSpan.FromMinutes(15);

    [Function(nameof(ProcessServerStatus))]
    public async Task ProcessServerStatus(
        [ServiceBusTrigger(Queues.ServerStatus, Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        FunctionContext context)
    {
        ServerStatusEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<ServerStatusEvent>(message.Body, JsonOptions.Default);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "ServerStatus message was not in expected format. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (evt is null)
        {
            logger.LogWarning("ServerStatus deserialized to null. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (evt.ServerId == Guid.Empty)
        {
            logger.LogWarning("ServerStatus has empty ServerId. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (string.IsNullOrWhiteSpace(evt.MapName))
        {
            logger.LogWarning("ServerStatus has null/empty MapName. ServerId: {ServerId}", evt.ServerId);
            return;
        }

        if (string.IsNullOrWhiteSpace(evt.GameType))
        {
            logger.LogWarning("ServerStatus has null/empty GameType. ServerId: {ServerId}", evt.ServerId);
            return;
        }

        if (!Enum.TryParse<GameType>(evt.GameType, out var gameType))
        {
            logger.LogWarning("ServerStatus has invalid GameType: {GameType}. ServerId: {ServerId}", evt.GameType, evt.ServerId);
            return;
        }

        if (evt.IsStale(StaleThreshold))
        {
            logger.LogWarning("ServerStatus event is stale ({Age} old). ServerId: {ServerId}",
                DateTime.UtcNow - evt.EventGeneratedUtc, evt.ServerId);
            return;
        }

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["GameType"] = evt.GameType,
            ["ServerId"] = evt.ServerId
        });

        var failedSteps = new List<string>();
        var livePlayerDtos = new List<CreateLivePlayerDto>();

        // Step 1: Build live player list with GeoIP enrichment and set live status
        try
        {
            foreach (var player in evt.Players)
            {
                var livePlayer = new CreateLivePlayerDto
                {
                    Name = player.Username,
                    IpAddress = player.IpAddress,
                    GameType = gameType,
                    Num = player.SlotId,
                    GameServerId = evt.ServerId,
                    Score = player.Score,
                    Ping = player.Ping,
                    Rate = player.Rate,
                    ConnectedAtUtc = player.ConnectedAtUtc
                };

                // Resolve PlayerId from cache/API (best-effort per player)
                if (!string.IsNullOrWhiteSpace(player.PlayerGuid))
                {
                    try
                    {
                        var playerId = await GetPlayerId(gameType, player.PlayerGuid).ConfigureAwait(false);
                        if (playerId != Guid.Empty)
                            livePlayer.PlayerId = playerId;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Player ID resolution failed for {PlayerGuid}", player.PlayerGuid);
                    }
                }

                // IP Intelligence enrichment (best-effort per player)
                if (!string.IsNullOrWhiteSpace(player.IpAddress))
                {
                    try
                    {
                        var intelResult = await geoLocationApiClient.GeoLookup.V1_1
                            .GetIpIntelligence(player.IpAddress)
                            .ConfigureAwait(false);

                        if (intelResult.IsSuccess && intelResult.Result?.Data is not null)
                        {
                            livePlayer.GeoIntelligence = intelResult.Result.Data;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "IP intelligence enrichment failed for player {Username} with IP {IpAddress}",
                            player.Username, player.IpAddress);
                    }
                }

                livePlayerDtos.Add(livePlayer);
            }

            var liveStatusDto = new SetGameServerLiveStatusDto
            {
                Title = evt.ServerTitle,
                Map = evt.MapName,
                Mod = evt.ServerMod,
                GameType = gameType,
                MaxPlayers = evt.MaxPlayers ?? 0,
                CurrentPlayers = evt.PlayerCount,
                Players = livePlayerDtos
            };

            await repositoryApiClient.LiveStatus.V1
                .SetGameServerLiveStatus(evt.ServerId, liveStatusDto)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failedSteps.Add("SetLiveStatus");
            logger.LogWarning(ex, "Failed to set live status for {ServerId}", evt.ServerId);
        }

        // Step 2: Create player count snapshot for analytics
        try
        {
            var statDtos = new List<CreateGameServerStatDto>
            {
                new(evt.ServerId, evt.PlayerCount, evt.MapName)
            };
            await repositoryApiClient.GameServersStats.V1
                .CreateGameServerStats(statDtos)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failedSteps.Add("CreateStats");
            logger.LogWarning(ex, "Failed to create player count snapshot for {ServerId}", evt.ServerId);
        }

        // Step 3: Populate recent players for map geo-location display
        try
        {
            var recentPlayerDtos = new List<CreateRecentPlayerDto>();
            foreach (var livePlayer in livePlayerDtos)
            {
                if (livePlayer.PlayerId is null || livePlayer.PlayerId == Guid.Empty)
                    continue;

                var recentPlayer = new CreateRecentPlayerDto(
                    livePlayer.Name ?? "Unknown",
                    gameType,
                    livePlayer.PlayerId.Value)
                {
                    IpAddress = livePlayer.IpAddress,
                    GameServerId = evt.ServerId,
                    Lat = livePlayer.GeoIntelligence?.Latitude,
                    Long = livePlayer.GeoIntelligence?.Longitude,
                    CountryCode = livePlayer.GeoIntelligence?.CountryCode
                };

                recentPlayerDtos.Add(recentPlayer);
            }

            if (recentPlayerDtos.Count > 0)
            {
                await repositoryApiClient.RecentPlayers.V1
                    .CreateRecentPlayers(recentPlayerDtos)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            failedSteps.Add("CreateRecentPlayers");
            logger.LogWarning(ex, "Failed to create recent players for {ServerId}", evt.ServerId);
        }

        // Step 4: Track telemetry
        telemetryClient.TrackEvent(new EventTelemetry("ServerStatusReceived")
        {
            Properties =
            {
                ["ServerId"] = evt.ServerId.ToString(),
                ["GameType"] = evt.GameType,
                ["MapName"] = evt.MapName,
                ["PlayerCount"] = evt.PlayerCount.ToString()
            }
        });

        telemetryClient.TrackMetric("ServerPlayerCount", evt.PlayerCount,
            new Dictionary<string, string>
            {
                ["ServerId"] = evt.ServerId.ToString(),
                ["GameType"] = evt.GameType
            });

        if (failedSteps.Count > 0)
        {
            logger.LogWarning("Partially processed server status for {ServerId}: failed steps: {FailedSteps}",
                evt.ServerId, string.Join(", ", failedSteps));
        }
        else
        {
            logger.LogInformation("Processed server status for {ServerId}: {PlayerCount} players on {MapName}",
                evt.ServerId, evt.PlayerCount, evt.MapName);
        }
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
}
