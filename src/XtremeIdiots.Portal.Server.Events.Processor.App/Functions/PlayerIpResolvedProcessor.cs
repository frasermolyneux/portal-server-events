using System.Text.Json;

using Azure.Messaging.ServiceBus;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using MX.GeoLocation.Api.Client.V1;

using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;
using XtremeIdiots.Portal.Server.Events.Processor.App.Services;
using XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

/// <summary>
/// Processes PlayerIpResolved events emitted by the agent when RCON sync discovers a player's IP.
/// Persists the IP observation with its source event time before running protection checks.
/// </summary>
public class PlayerIpResolvedProcessor(
    ILogger<PlayerIpResolvedProcessor> logger,
    IRepositoryApiClient repositoryApiClient,
    IGeoLocationApiClient geoLocationApiClient,
    IRulesTriggerService rulesTriggerService,
    IVpnDetectedTagService vpnDetectedTagService,
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

        if (!IpAddressGuard.IsPersistable(evt.IpAddress))
        {
            logger.LogWarning("PlayerIpResolved has non-persistable placeholder IP {IpAddress}. ServerId: {ServerId}, PlayerGuid: {PlayerGuid}",
                evt.IpAddress, evt.ServerId, evt.PlayerGuid);
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

        var isStale = evt.IsStale(StaleThreshold);
        if (isStale)
        {
            logger.LogWarning("PlayerIpResolved event is stale ({Age} old); persisting history without enforcement. ServerId: {ServerId}, PlayerGuid: {PlayerGuid}",
                DateTime.UtcNow - evt.EventGeneratedUtc, evt.ServerId, evt.PlayerGuid);
        }

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["GameType"] = evt.GameType,
            ["ServerId"] = evt.ServerId,
            ["PlayerGuid"] = evt.PlayerGuid
        });

        var prepared = isStale
            ? null
            : await rulesTriggerService.PrepareAsync(evt, context.CancellationToken).ConfigureAwait(false);

        PlayerContext playerContext;
        try
        {
            playerContext = await GetPlayerContext(gameType, evt.PlayerGuid).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await DelayRetryAsync(message, context.CancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Failed to resolve player for IP persistence: {evt.PlayerGuid}", ex);
        }

        if (playerContext.PlayerId == Guid.Empty)
        {
            logger.LogWarning("Player not yet available for IP persistence: {PlayerGuid}; retrying delivery {DeliveryCount}",
                evt.PlayerGuid, message.DeliveryCount);
            await DelayRetryAsync(message, context.CancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Player not available for IP persistence: {evt.PlayerGuid}");
        }

        try
        {
            var updateResult = await repositoryApiClient.Players.V1
                .RecordPlayerIpObservation(
                    playerContext.PlayerId,
                    new RecordPlayerIpObservationDto
                    {
                        MessageId = message.MessageId,
                        IpAddress = evt.IpAddress,
                        ObservedAtUtc = new DateTimeOffset(DateTime.SpecifyKind(evt.EventGeneratedUtc, DateTimeKind.Utc))
                    },
                    context.CancellationToken)
                .ConfigureAwait(false);

            if (!updateResult.IsSuccess)
            {
                throw new InvalidOperationException($"IP observation persistence returned {updateResult.StatusCode}.");
            }

            InvalidatePlayerCache(gameType, evt.PlayerGuid);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist IP for player {PlayerGuid}; retrying delivery {DeliveryCount}",
                evt.PlayerGuid, message.DeliveryCount);
            await DelayRetryAsync(message, context.CancellationToken).ConfigureAwait(false);
            throw;
        }

        auditLogger.LogAudit(AuditEvent.ServerAction("PlayerIpResolved", AuditAction.Update)
            .WithGameContext(evt.GameType, evt.ServerId)
            .WithPlayer(evt.PlayerGuid, null)
            .WithProperty("IpAddress", evt.IpAddress)
            .Build());

        logger.LogInformation("Persisted IP {IpAddress} for player {PlayerGuid}", evt.IpAddress, evt.PlayerGuid);

        MX.GeoLocation.Abstractions.Models.V1_1.IpIntelligenceDto? intelligence = null;
        try
        {
            var intelligenceResult = await geoLocationApiClient.GeoLookup.V1_1
                .GetIpIntelligence(evt.IpAddress, context.CancellationToken)
                .ConfigureAwait(false);
            if (intelligenceResult.IsSuccess && intelligenceResult.Result?.Data is not null)
            {
                intelligence = intelligenceResult.Result.Data;
            }
            else
            {
                logger.LogWarning(
                    "IP intelligence lookup failed for resolved IP {IpAddress}. Status: {StatusCode}",
                    evt.IpAddress,
                    intelligenceResult.StatusCode);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "IP intelligence lookup failed for resolved IP {IpAddress}", evt.IpAddress);
        }

        if (intelligence is not null)
        {
            await vpnDetectedTagService
                .AddIfDetectedAsync(playerContext.PlayerId, intelligence, context.CancellationToken)
                .ConfigureAwait(false);
        }

        if (isStale)
        {
            return;
        }

        await rulesTriggerService.ProcessAsync(prepared, intelligence, context.CancellationToken).ConfigureAwait(false);
    }

    private static Task DelayRetryAsync(ServiceBusReceivedMessage message, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromSeconds(Math.Clamp(message.DeliveryCount, 1, 10)), ct);

    private async Task<PlayerContext> GetPlayerContext(GameType gameType, string guid)
    {
        var cacheKey = $"player-ctx-{gameType}-{guid}";

        if (memoryCache.TryGetValue(cacheKey, out PlayerContext? cachedContext) && cachedContext is not null)
        {
            return cachedContext;
        }

        var response = await repositoryApiClient.Players.V1
            .GetPlayerByGameType(gameType, guid, PlayerEntityOptions.Tags)
            .ConfigureAwait(false);

        if (response.IsNotFound)
        {
            return PlayerContext.Empty;
        }

        if (!response.IsSuccess || response.Result?.Data is null)
        {
            throw new InvalidOperationException($"Player lookup failed with status {response.StatusCode}.");
        }

        var player = response.Result.Data;
        var tags = player.Tags
            .Select(static playerTag => playerTag.Tag?.Name)
            .Where(static tag => !string.IsNullOrWhiteSpace(tag))
            .Select(static tag => tag!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var playerContext = new PlayerContext(player.PlayerId, player.Username, tags);
        memoryCache.Set(cacheKey, playerContext,
            new MemoryCacheEntryOptions().SetSlidingExpiration(PlayerCacheExpiration));

        return playerContext;
    }

    private void InvalidatePlayerCache(GameType gameType, string guid)
    {
        memoryCache.Remove($"player-id-{gameType}-{guid}");
        memoryCache.Remove($"player-ctx-{gameType}-{guid}");
    }

    private sealed record PlayerContext(Guid PlayerId, string Username, string[] Tags)
    {
        public static readonly PlayerContext Empty = new(Guid.Empty, string.Empty, []);
    }
}
