using System.Text.RegularExpressions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.AdminActions;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Services;

public sealed class ProtectedNameService(
    IRepositoryApiClient repositoryApiClient,
    IAdminActionTopics adminActionTopics,
    IServersApiClient serversApiClient,
    IMemoryCache memoryCache,
    IAuditLogger auditLogger,
    IConfiguration configuration,
    ILogger<ProtectedNameService> logger) : IProtectedNameService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private const string CacheKeyPrefix = "protected-names-list:";
    private const int ProtectedNamesPageSize = 500;
    private const string ProtectedNameViolationReasonMarker = "Protected Name Violation";
    private const string AutomationReasonMarker = "[PORTAL-AUTOMATION]";
    private static readonly Regex QuakeColorCodeRegex = new(@"\^[0-9A-Za-z]", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex NonAlphaNumericRegex = new(@"[^a-z0-9]+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

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
            {
                return;
            }

            var playerNameLower = context.Username.ToLowerInvariant();

            foreach (var protectedName in protectedNames)
            {
                var protectedNameLower = protectedName.Name.ToLowerInvariant();

                var isMatch = playerNameLower.Contains(protectedNameLower)
                    || protectedNameLower.Contains(playerNameLower);

                if (!isMatch)
                {
                    continue;
                }

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

                var reason = $"{ProtectedNameViolationReasonMarker} - using '{protectedName.Name}' which is registered to {ownerUsername}";
                var createdAdminAction = false;

                var verificationResult = await serversApiClient.CoD4xRcon.V1.Status(context.ServerId, ct).ConfigureAwait(false);
                if (!verificationResult.IsSuccess || verificationResult.Result?.Data is null)
                {
                    logger.LogWarning(
                        "Protected name enforcement could not verify live player state for player {PlayerId} on server {ServerId}. Status: {StatusCode}",
                        context.PlayerId,
                        context.ServerId,
                        verificationResult.StatusCode);
                    return;
                }

                var slotPlayer = verificationResult.Result.Data.Players.FirstOrDefault(p => p.Num == context.SlotId);
                if (slotPlayer is null)
                {
                    logger.LogWarning(
                        "Protected name enforcement verification failed for player {PlayerId} on server {ServerId}. Slot {SlotId} is no longer connected.",
                        context.PlayerId,
                        context.ServerId,
                        context.SlotId);
                    return;
                }

                var resolvedName = string.IsNullOrWhiteSpace(slotPlayer.Name) ? slotPlayer.RawName : slotPlayer.Name;
                if (!IsLikelySamePlayerName(context.Username, resolvedName))
                {
                    logger.LogWarning(
                        "Protected name enforcement verification failed for player {PlayerId} on server {ServerId}. Slot {SlotId} now maps to '{ResolvedName}' (expected '{ExpectedName}').",
                        context.PlayerId,
                        context.ServerId,
                        context.SlotId,
                        resolvedName,
                        context.Username);
                    return;
                }

                var botAdminId = configuration["ContentSafety:BotAdminId"];
                var automationRuleId = BuildAutomationRuleId(protectedName);
                var ensureResult = await repositoryApiClient.AdminActions.V1
                    .EnsureAutomatedAction(
                        new EnsureAutomatedActionDto(
                            context.PlayerId,
                            AdminActionType.Ban,
                            reason,
                            AutomationFeature.ProtectedName,
                            automationRuleId)
                        {
                            AdminId = botAdminId
                        },
                        ct)
                    .ConfigureAwait(false);

                if (!ensureResult.IsSuccess || ensureResult.Result?.Data is null)
                {
                    logger.LogWarning(
                        "Failed to ensure protected name admin action for player {PlayerId}. Status: {StatusCode}",
                        context.PlayerId,
                        ensureResult.StatusCode);
                    return;
                }

                var adminAction = ensureResult.Result.Data.AdminAction;
                if (ensureResult.Result.Data.Created)
                {
                    var forumTopicId = await adminActionTopics.CreateTopicForAdminAction(
                        AdminActionType.Ban,
                        contextGameType,
                        context.PlayerId,
                        context.Username,
                        adminAction.Created,
                        reason,
                        botAdminId,
                        ct).ConfigureAwait(false);

                    if (forumTopicId > 0)
                    {
                        var updateResult = await repositoryApiClient.AdminActions.V1
                            .UpdateAdminAction(
                                new EditAdminActionDto(adminAction.AdminActionId)
                                {
                                    ForumTopicId = forumTopicId
                                },
                                ct)
                            .ConfigureAwait(false);

                        if (!updateResult.IsSuccess)
                        {
                            logger.LogWarning(
                                "Failed to link protected name forum topic {ForumTopicId} to admin action {AdminActionId}",
                                forumTopicId,
                                adminAction.AdminActionId);
                        }
                    }
                    else
                    {
                        logger.LogWarning("Failed to create protected name forum topic for admin action {AdminActionId}", adminAction.AdminActionId);
                    }
                }

                var banResult = await serversApiClient.CoD4xRcon.V1.BanClient(
                        context.ServerId,
                        new CoD4xClientReasonRequestDto
                        {
                            ClientId = context.SlotId,
                            Reason = $"{AutomationReasonMarker} ProtectedName:{automationRuleId} {ProtectedNameViolationReasonMarker}"
                        },
                        ct)
                    .ConfigureAwait(false);

                if (!banResult.IsSuccess)
                {
                    logger.LogWarning(
                        "Protected name enforcement RCON ban failed for player {PlayerId} on server {ServerId}. Status: {StatusCode}",
                        context.PlayerId,
                        context.ServerId,
                        banResult.StatusCode);
                    return;
                }

                if (ensureResult.Result.Data.Created)
                {
                    auditLogger.LogAudit(AuditEvent.ServerAction("ProtectedNameBanEnforced", AuditAction.Moderate)
                        .WithGameContext(context.GameType, context.ServerId)
                        .WithPlayer(string.Empty, context.Username)
                        .WithSource("ProtectedNameService")
                        .WithProperty("ProtectedName", protectedName.Name)
                        .Build());

                    TrackViolation(context, protectedName, ownerUsername);
                    createdAdminAction = true;
                }

                logger.LogInformation(
                    "Protected name violation: player {PlayerId} ('{Username}') matched '{ProtectedName}' owned by {OwnerId}. Admin action created: {CreatedAdminAction}. RCON ban verification executed for {ServerId}",
                    context.PlayerId,
                    context.Username,
                    protectedName.Name,
                    protectedName.PlayerId,
                    createdAdminAction,
                    context.ServerId);

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
        {
            return cached;
        }

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
            {
                break;
            }

            scopedItems.AddRange(page);

            if (page.Count < ProtectedNamesPageSize)
            {
                break;
            }

            skip += ProtectedNamesPageSize;
        }

        memoryCache.Set(cacheKey, (IReadOnlyList<ScopedProtectedName>)scopedItems,
            new MemoryCacheEntryOptions().SetAbsoluteExpiration(CacheDuration));

        return scopedItems;
    }

    private static bool IsLikelySamePlayerName(string expectedName, string? resolvedName)
    {
        var normalizedExpected = NormalizePlayerName(expectedName);
        var normalizedResolved = NormalizePlayerName(resolvedName);

        if (normalizedExpected.Length == 0 || normalizedResolved.Length == 0)
        {
            return false;
        }

        return string.Equals(normalizedExpected, normalizedResolved, StringComparison.Ordinal);
    }

    private static string NormalizePlayerName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var withoutColorCodes = QuakeColorCodeRegex.Replace(value, string.Empty);
        var lowered = withoutColorCodes.ToLowerInvariant();
        var alphanumericOnly = NonAlphaNumericRegex.Replace(lowered, string.Empty);

        return alphanumericOnly.Trim();
    }

    private async Task<OwnerPlayerInfo?> GetOwnerPlayerAsync(Guid ownerId, CancellationToken ct)
    {
        try
        {
            var response = await repositoryApiClient.Players.V1
                .GetPlayer(ownerId, PlayerEntityOptions.None)
                .ConfigureAwait(false);

            if (response.IsSuccess && response.Result?.Data is not null)
            {
                return new OwnerPlayerInfo(response.Result.Data.Username);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to look up owner player details for {OwnerId}", ownerId);
        }

        return null;
    }

    private static string BuildAutomationRuleId(ScopedProtectedName protectedName)
        => $"{protectedName.PlayerId:N}:{NormalizePlayerName(protectedName.Name)}";

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
