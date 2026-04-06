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
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Services;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

public class PlayerConnectedProcessor(
    ILogger<PlayerConnectedProcessor> logger,
    IRepositoryApiClient repositoryApiClient,
    IGeoLocationApiClient geoLocationApiClient,
    IProtectedNameService protectedNameService,
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

                // Protected name enforcement (best-effort, never blocks player processing)
                var newPlayerId = await GetPlayerId(gameType, playerEvent.PlayerGuid).ConfigureAwait(false);
                if (newPlayerId != Guid.Empty)
                {
                    await protectedNameService.CheckAsync(new ProtectedNameContext
                    {
                        ServerId = playerEvent.ServerId,
                        GameType = playerEvent.GameType,
                        Username = playerEvent.Username,
                        PlayerId = newPlayerId,
                        SlotId = playerEvent.SlotId
                    }).ConfigureAwait(false);
                }

                await EnrichWithGeoLocation(playerEvent).ConfigureAwait(false);
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

        // Protected name enforcement (best-effort, never blocks player processing)
        await protectedNameService.CheckAsync(new ProtectedNameContext
        {
            ServerId = playerEvent.ServerId,
            GameType = playerEvent.GameType,
            Username = playerEvent.Username,
            PlayerId = playerId,
            SlotId = playerEvent.SlotId
        }).ConfigureAwait(false);

        // GeoIP enrichment (best-effort, never blocks player processing)
        await EnrichWithGeoLocation(playerEvent).ConfigureAwait(false);
    }

    private async Task EnrichWithGeoLocation(PlayerConnectedEvent playerEvent)
    {
        if (string.IsNullOrWhiteSpace(playerEvent.IpAddress))
            return;

        try
        {
            var geoResult = await geoLocationApiClient.GeoLookup.V1_1
                .GetIpIntelligence(playerEvent.IpAddress)
                .ConfigureAwait(false);

            if (geoResult.IsSuccess && geoResult.Result?.Data is not null)
            {
                var intel = geoResult.Result.Data;

                var properties = new Dictionary<string, string>
                {
                    ["GameType"] = playerEvent.GameType,
                    ["ServerId"] = playerEvent.ServerId.ToString(),
                    ["PlayerGuid"] = playerEvent.PlayerGuid,
                    ["Username"] = playerEvent.Username,
                    ["CountryCode"] = intel.CountryCode ?? string.Empty,
                    ["CountryName"] = intel.CountryName ?? string.Empty,
                    ["CityName"] = intel.CityName ?? string.Empty,
                    ["Latitude"] = intel.Latitude?.ToString() ?? string.Empty,
                    ["Longitude"] = intel.Longitude?.ToString() ?? string.Empty
                };

                if (intel.Anonymizer is not null)
                {
                    properties["IsAnonymous"] = intel.Anonymizer.IsAnonymous.ToString();
                    properties["IsAnonymousVpn"] = intel.Anonymizer.IsAnonymousVpn.ToString();
                    properties["IsTorExitNode"] = intel.Anonymizer.IsTorExitNode.ToString();
                    properties["IsHostingProvider"] = intel.Anonymizer.IsHostingProvider.ToString();
                    properties["IsPublicProxy"] = intel.Anonymizer.IsPublicProxy.ToString();
                }

                if (intel.ProxyCheck is not null)
                {
                    properties["RiskScore"] = intel.ProxyCheck.RiskScore.ToString();
                    properties["IsProxy"] = intel.ProxyCheck.IsProxy.ToString();
                    properties["IsVpn"] = intel.ProxyCheck.IsVpn.ToString();
                    properties["ProxyType"] = intel.ProxyCheck.ProxyType;
                }

                telemetryClient.TrackEvent(new EventTelemetry("PlayerIntelligenceEnriched")
                {
                    Properties = { ["GameType"] = playerEvent.GameType, ["ServerId"] = playerEvent.ServerId.ToString(),
                        ["PlayerGuid"] = playerEvent.PlayerGuid, ["Username"] = playerEvent.Username,
                        ["CountryCode"] = intel.CountryCode ?? string.Empty,
                        ["RiskScore"] = intel.ProxyCheck?.RiskScore.ToString() ?? string.Empty,
                        ["IsVpn"] = intel.ProxyCheck?.IsVpn.ToString() ?? string.Empty,
                        ["IsProxy"] = intel.ProxyCheck?.IsProxy.ToString() ?? string.Empty,
                        ["IsAnonymous"] = intel.Anonymizer?.IsAnonymous.ToString() ?? string.Empty,
                        ["IsTorExitNode"] = intel.Anonymizer?.IsTorExitNode.ToString() ?? string.Empty
                    }
                });

                logger.LogInformation("IP Intelligence for {Username}: Country={CountryCode}, RiskScore={RiskScore}, IsVpn={IsVpn}, IsProxy={IsProxy}",
                    playerEvent.Username,
                    intel.CountryCode,
                    intel.ProxyCheck?.RiskScore,
                    intel.ProxyCheck?.IsVpn,
                    intel.ProxyCheck?.IsProxy);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "IP intelligence enrichment failed for {IpAddress}", playerEvent.IpAddress);
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
