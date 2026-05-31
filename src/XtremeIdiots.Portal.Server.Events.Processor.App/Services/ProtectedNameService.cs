using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.AdminActions;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Services;

public sealed class ProtectedNameService(
    IRepositoryApiClient repositoryApiClient,
    IRconApi rconApi,
    IMemoryCache memoryCache,
    IAuditLogger auditLogger,
    IConfiguration configuration,
    ILogger<ProtectedNameService> logger) : IProtectedNameService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private const string CacheKeyPrefix = "protected-names-list:";
    private const int ProtectedNamesPageSize = 500;

    private sealed record OwnerPlayerInfo(string Username);
    private sealed record ScopedProtectedName(string Name, Guid PlayerId, GameType OwnerGameType);

    public async Task CheckAsync(ProtectedNameContext context, CancellationToken ct = default)
    {
        try
        {
            if (context.SlotId <= 0)
            {
                logger.LogDebug("Skipping protected name check — SlotId {SlotId} is not valid for enforcement", context.SlotId);
                return;
            }

            if (!Enum.TryParse<GameType>(context.GameType, true, out var contextGameType))
            {
                logger.LogWarning(
                    "Skipping protected name check — unrecognised game type '{GameType}' for player {PlayerId}",
                    context.GameType,
                    context.PlayerId);
                return;
            }

            var protectedNames = await GetProtectedNamesAsync(contextGameType, ct).ConfigureAwait(false);

            if (protectedNames is null || !protectedNames.Any())
                return;

            var playerNameLower = context.Username.ToLowerInvariant();

            foreach (var protectedName in protectedNames)
            {
                var protectedNameLower = protectedName.Name.ToLowerInvariant();

                var isMatch = playerNameLower.Contains(protectedNameLower)
                    || protectedNameLower.Contains(playerNameLower);

                if (!isMatch)
                    continue;

                if (context.PlayerId == protectedName.PlayerId)
                {
                    logger.LogInformation(
                        "Player {PlayerId} matched protected name '{ProtectedName}' but is the owner — no action",
                        context.PlayerId, protectedName.Name);
                    return;
                }

                if (protectedName.OwnerGameType != contextGameType)
                {
                    logger.LogInformation(
                        "Skipping protected name enforcement for '{ProtectedName}' on player {PlayerId} due to cross-game scope: owner game {OwnerGameType}, player game {PlayerGameType}",
                        protectedName.Name,
                        context.PlayerId,
                        protectedName.OwnerGameType,
                        contextGameType);
                    continue;
                }

                var ownerPlayer = await GetOwnerPlayerAsync(protectedName.PlayerId, ct).ConfigureAwait(false);

                if (ownerPlayer is null)
                {
                    logger.LogWarning(
                        "Skipping protected name enforcement for '{ProtectedName}' on player {PlayerId} because owner {OwnerId} could not be resolved",
                        protectedName.Name,
                        context.PlayerId,
                        protectedName.PlayerId);
                    continue;
                }

                // Violation found — enforce
                var ownerUsername = ownerPlayer.Username;

                var reason = $"Protected Name Violation - using '{protectedName.Name}' which is registered to {ownerUsername}";

                var botAdminId = configuration["ContentSafety:BotAdminId"];

                var adminAction = new CreateAdminActionDto(context.PlayerId, AdminActionType.Ban, reason)
                {
                    AdminId = botAdminId
                };

                await repositoryApiClient.AdminActions.V1
                    .CreateAdminAction(adminAction, ct)
                    .ConfigureAwait(false);

                await rconApi.BanPlayerWithVerification(context.ServerId, context.SlotId, context.Username)
                    .ConfigureAwait(false);

                auditLogger.LogAudit(AuditEvent.ServerAction("ProtectedNameBanEnforced", AuditAction.Moderate)
                    .WithGameContext(context.GameType, context.ServerId)
                    .WithPlayer(string.Empty, context.Username)
                    .WithSource("ProtectedNameService")
                    .WithProperty("ProtectedName", protectedName.Name)
                    .Build());

                TrackViolation(context, protectedName, ownerUsername);

                logger.LogInformation(
                    "Protected name violation: player {PlayerId} ('{Username}') matched '{ProtectedName}' owned by {OwnerId}. Banned and kicked from {ServerId}",
                    context.PlayerId, context.Username, protectedName.Name, protectedName.PlayerId, context.ServerId);

                return;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Protected name check failed for player {PlayerId} ('{Username}') on server {ServerId}",
                context.PlayerId, context.Username, context.ServerId);
        }
    }

    private async Task<IReadOnlyList<ScopedProtectedName>?> GetProtectedNamesAsync(GameType contextGameType, CancellationToken ct)
    {
        var cacheKey = $"{CacheKeyPrefix}{contextGameType}";

        if (memoryCache.TryGetValue(cacheKey, out IReadOnlyList<ScopedProtectedName>? cached))
            return cached;

        var scopedItems = new List<ScopedProtectedName>();
        var skip = 0;

        while (true)
        {
            var response = await repositoryApiClient.Players.V1
                .GetProtectedNames(skip, ProtectedNamesPageSize, contextGameType)
                .ConfigureAwait(false);

            if (!response.IsSuccess || response.Result?.Data?.Items is null)
            {
                logger.LogWarning("Failed to fetch protected names for game {GameType} at skip {Skip}: {StatusCode}", contextGameType, skip, response.StatusCode);
                return null;
            }

            var page = response.Result.Data.Items
                .Select(item => new ScopedProtectedName(item.Name, item.PlayerId, item.OwnerGameType))
                .ToList();

            if (page.Count == 0)
                break;

            scopedItems.AddRange(page);

            if (page.Count < ProtectedNamesPageSize)
                break;

            skip += ProtectedNamesPageSize;
        }

        memoryCache.Set(cacheKey, (IReadOnlyList<ScopedProtectedName>)scopedItems,
            new MemoryCacheEntryOptions().SetAbsoluteExpiration(CacheDuration));

        return scopedItems;
    }

    private async Task<OwnerPlayerInfo?> GetOwnerPlayerAsync(Guid ownerId, CancellationToken ct)
    {
        try
        {
            var response = await repositoryApiClient.Players.V1
                .GetPlayer(ownerId, PlayerEntityOptions.None)
                .ConfigureAwait(false);

            if (response.IsSuccess && response.Result?.Data is not null)
                return new OwnerPlayerInfo(response.Result.Data.Username);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to look up owner player details for {OwnerId}", ownerId);
        }

        return null;
    }

    private void TrackViolation(ProtectedNameContext context, ScopedProtectedName protectedName, string ownerUsername)
    {
        auditLogger.LogAudit(AuditEvent.ServerAction("ProtectedNameViolation", AuditAction.Moderate)
            .WithGameContext(context.GameType, context.ServerId)
            .WithPlayer(string.Empty, context.Username)
            .WithProperty("ProtectedName", protectedName.Name)
            .WithProperty("Owner", ownerUsername)
            .Build());
    }
}
