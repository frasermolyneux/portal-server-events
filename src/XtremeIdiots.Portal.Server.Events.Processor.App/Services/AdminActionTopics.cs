using System.Globalization;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using MX.InvisionCommunity.Api.Abstractions;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Services;

internal sealed class AdminActionTopics(
    ILogger<AdminActionTopics> logger,
    IInvisionApiClient invisionClient,
    IConfiguration configuration) : IAdminActionTopics
{
    public async Task<int> CreateTopicForAdminAction(
        AdminActionType type,
        GameType gameType,
        Guid playerId,
        string username,
        DateTime created,
        string text,
        string? adminId,
        CancellationToken ct = default)
    {
        try
        {
            var userId = int.TryParse(configuration["XtremeIdiots:Forums:DefaultAdminUserId"], out var defaultUserId) ? defaultUserId : 21145;
            if (!string.IsNullOrEmpty(adminId) && int.TryParse(adminId, out var parsedUserId))
            {
                userId = parsedUserId;
            }

            var forumId = ResolveForumId(type, gameType);

            var postTopicResult = await invisionClient.Forums
                .PostTopic(forumId, userId, $"{username} - {type}", PostContent(type, playerId, username, created, text), type.ToString())
                .ConfigureAwait(false);

            if (postTopicResult.IsSuccess && postTopicResult.Result?.Data != null)
            {
                return postTopicResult.Result.Data.TopicId;
            }

            logger.LogError("Error creating admin action topic — call to post topic returned null");
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating admin action topic");
            return 0;
        }
    }

    private string PostContent(AdminActionType type, Guid playerId, string username, DateTime created, string text)
    {
        var portalBaseUrl = (configuration["XtremeIdiots:PortalBaseUrl"] ?? "https://portal.xtremeidiots.com").TrimEnd('/');
        return $"""
            <p>
               Username: {username}<br>
               Player Link: <a href="{portalBaseUrl}/Players/Details/{playerId}">Portal</a><br>
               {type} Created: {created.ToString(CultureInfo.InvariantCulture)}
            </p>
            <p>
               {text}
            </p>
            <p>
               <small>Do not edit this post directly as it will be overwritten by the Portal. Add comments on posts below or edit the record in the Portal.</small>
            </p>
            """;
    }

    private int ResolveForumId(AdminActionType type, GameType gameType)
    {
        var defaultForumId = int.TryParse(configuration["XtremeIdiots:Forums:DefaultForumId"], out var parsedForumId) ? parsedForumId : 28;

        var category = type switch
        {
            AdminActionType.Observation or AdminActionType.Warning or AdminActionType.Kick => "AdminLogs",
            AdminActionType.TempBan or AdminActionType.Ban => "Bans",
            _ => null
        };

        if (category is null)
            return defaultForumId;

        var gameKey = gameType switch
        {
            GameType.Arma or GameType.Arma2 or GameType.Arma3 => "Arma",
            _ => gameType.ToString()
        };

        var configValue = configuration[$"XtremeIdiots:Forums:{category}:{gameKey}"];
        if (configValue is not null && int.TryParse(configValue, out var forumId))
            return forumId;

        return type switch
        {
            AdminActionType.Observation => gameType.ForumIdForObservations(),
            AdminActionType.Warning => gameType.ForumIdForWarnings(),
            AdminActionType.Kick => gameType.ForumIdForKicks(),
            AdminActionType.TempBan => gameType.ForumIdForTempBans(),
            AdminActionType.Ban => gameType.ForumIdForBans(),
            _ => defaultForumId
        };
    }
}
