using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.AdminActions;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
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
    private const string CacheKey = "protected-names-list";

    public async Task CheckAsync(ProtectedNameContext context, CancellationToken ct = default)
    {
        try
        {
            if (context.SlotId <= 0)
            {
                logger.LogDebug("Skipping protected name check — SlotId {SlotId} is not valid for enforcement", context.SlotId);
                return;
            }

            var protectedNames = await GetProtectedNamesAsync(ct).ConfigureAwait(false);

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

                // Violation found — enforce
                var ownerUsername = await GetOwnerUsernameAsync(protectedName.PlayerId, ct).ConfigureAwait(false);

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

    private async Task<IEnumerable<ProtectedNameDto>?> GetProtectedNamesAsync(CancellationToken ct)
    {
        if (memoryCache.TryGetValue(CacheKey, out IEnumerable<ProtectedNameDto>? cached))
            return cached;

        var response = await repositoryApiClient.Players.V1
            .GetProtectedNames(0, 1000)
            .ConfigureAwait(false);

        if (!response.IsSuccess || response.Result?.Data?.Items is null)
        {
            logger.LogWarning("Failed to fetch protected names: {StatusCode}", response.StatusCode);
            return null;
        }

        var items = response.Result.Data.Items.ToList();

        memoryCache.Set(CacheKey, (IEnumerable<ProtectedNameDto>)items,
            new MemoryCacheEntryOptions().SetAbsoluteExpiration(CacheDuration));

        return items;
    }

    private async Task<string> GetOwnerUsernameAsync(Guid ownerId, CancellationToken ct)
    {
        try
        {
            var response = await repositoryApiClient.Players.V1
                .GetPlayer(ownerId, PlayerEntityOptions.None)
                .ConfigureAwait(false);

            if (response.IsSuccess && response.Result?.Data is not null)
                return response.Result.Data.Username;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to look up owner username for {OwnerId}", ownerId);
        }

        return ownerId.ToString();
    }

    private void TrackViolation(ProtectedNameContext context, ProtectedNameDto protectedName, string ownerUsername)
    {
        auditLogger.LogAudit(AuditEvent.ServerAction("ProtectedNameViolation", AuditAction.Moderate)
            .WithGameContext(context.GameType, context.ServerId)
            .WithPlayer(string.Empty, context.Username)
            .WithProperty("ProtectedName", protectedName.Name)
            .WithProperty("Owner", ownerUsername)
            .Build());
    }
}
