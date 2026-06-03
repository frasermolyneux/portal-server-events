using System.Text.Json;

using Azure.Messaging.ServiceBus;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ChatMessages;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ConnectedPlayers;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.GameServers;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;
using XtremeIdiots.Portal.Server.Events.Processor.App.Moderation;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

public class ChatMessageProcessor(
    ILogger<ChatMessageProcessor> logger,
    IRepositoryApiClient repositoryApiClient,
    IMemoryCache memoryCache,
    IAuditLogger auditLogger,
    IChatCommandProcessor chatCommandProcessor,
    IChatModerationPipeline moderationPipeline,
    IConfiguration configuration)
{
    private static readonly TimeSpan DelayWarningThreshold = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PlayerCacheExpiration = TimeSpan.FromMinutes(15);
    private const string ChatCommandExecutionEventType = "ChatCommandExecution";
    private const string ChatCommandDeniedEventType = "ChatCommandDenied";

    [Function(nameof(ProcessChatMessage))]
    public async Task ProcessChatMessage(
        [ServiceBusTrigger(Queues.ChatMessage, Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        FunctionContext context)
    {
        ChatMessageEvent? chatEvent;
        try
        {
            chatEvent = JsonSerializer.Deserialize<ChatMessageEvent>(message.Body, JsonOptions.Default);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "ChatMessage was not in expected format. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (chatEvent is null)
        {
            logger.LogWarning("ChatMessage deserialized to null. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (string.IsNullOrWhiteSpace(chatEvent.GameType) ||
            string.IsNullOrWhiteSpace(chatEvent.PlayerGuid) ||
            string.IsNullOrWhiteSpace(chatEvent.Username) ||
            string.IsNullOrWhiteSpace(chatEvent.Message))
        {
            logger.LogWarning("ChatMessage missing required fields. GameType: {GameType}, PlayerGuid: {PlayerGuid}",
                chatEvent.GameType, chatEvent.PlayerGuid);
            return;
        }

        if (chatEvent.ServerId == Guid.Empty)
        {
            logger.LogWarning("ChatMessage has empty ServerId. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (chatEvent.SlotId < 0)
        {
            logger.LogWarning("ChatMessage has invalid SlotId: {SlotId}. MessageId: {MessageId}", chatEvent.SlotId, message.MessageId);
            return;
        }

        if (!Enum.TryParse<GameType>(chatEvent.GameType, out var gameType))
        {
            logger.LogWarning("ChatMessage has invalid GameType: {GameType}", chatEvent.GameType);
            return;
        }

        var eventAge = DateTime.UtcNow - chatEvent.EventGeneratedUtc;
        if (eventAge > DelayWarningThreshold)
        {
            logger.LogWarning("ChatMessage is delayed ({Age}). ServerId: {ServerId}, PlayerGuid: {PlayerGuid}",
                eventAge, chatEvent.ServerId, chatEvent.PlayerGuid);
        }

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["GameType"] = chatEvent.GameType,
            ["ServerId"] = chatEvent.ServerId,
            ["PlayerGuid"] = chatEvent.PlayerGuid
        });

        // Get player context (cached)
        var playerContext = await GetPlayerContext(gameType, chatEvent.PlayerGuid).ConfigureAwait(false);

        if (playerContext is null)
        {
            throw new InvalidOperationException(
                $"Player not found for Guid '{chatEvent.PlayerGuid}'. Message will retry.");
        }

        var playerId = playerContext.Value.PlayerId;

        // Map ChatMessageType to ChatType
        var chatType = chatEvent.Type == ChatMessageType.Team ? ChatType.Team : ChatType.All;

        var chatMessageDto = new CreateChatMessageDto(
            chatEvent.ServerId,
            playerId,
            chatType,
            chatEvent.Username,
            chatEvent.Message,
            chatEvent.EventGeneratedUtc);

        await repositoryApiClient.ChatMessages.V1
            .CreateChatMessage(chatMessageDto)
            .ConfigureAwait(false);

        auditLogger.LogAudit(AuditEvent.ServerAction("ChatMessagePersisted", AuditAction.Create)
            .WithGameContext(chatEvent.GameType, chatEvent.ServerId)
            .WithPlayer(chatEvent.PlayerGuid, chatEvent.Username)
            .Build());

        // Process commands after persisting the chat message
        var commandContext = new CommandContext
        {
            ServerId = chatEvent.ServerId,
            GameType = chatEvent.GameType,
            PlayerGuid = chatEvent.PlayerGuid,
            Username = chatEvent.Username,
            SlotId = chatEvent.SlotId,
            Message = chatEvent.Message,
            EventGeneratedUtc = chatEvent.EventGeneratedUtc,
            EventPublishedUtc = chatEvent.EventPublishedUtc,
            PlayerId = playerId,
            AuthorizationSnapshot = playerContext.Value.AuthorizationSnapshot
        };

        var commandResult = await chatCommandProcessor.ProcessAsync(commandContext, context.CancellationToken).ConfigureAwait(false);

        if (commandResult.Handled)
        {
            logger.LogInformation("Command processed for {Username}: Success={Success}",
                chatEvent.Username, commandResult.Success);

            await TryPersistChatCommandExecutionEventAsync(chatEvent, commandContext, commandResult, context.CancellationToken)
                .ConfigureAwait(false);
        }

        // Run moderation pipeline (never throws)
        var moderationContext = new ModerationContext
        {
            ServerId = chatEvent.ServerId,
            GameType = chatEvent.GameType,
            PlayerGuid = chatEvent.PlayerGuid,
            Username = chatEvent.Username,
            Message = chatEvent.Message,
            PlayerId = playerId,
            PlayerFirstSeen = playerContext.Value.FirstSeen,
            HasModerateChatTag = playerContext.Value.HasModerateChatTag
        };

        await moderationPipeline.RunAsync(moderationContext, context.CancellationToken).ConfigureAwait(false);
    }

    private async Task TryPersistChatCommandExecutionEventAsync(
        ChatMessageEvent chatEvent,
        CommandContext commandContext,
        CommandResult commandResult,
        CancellationToken cancellationToken)
    {
        var commandPrefix = ExtractCommandPrefix(chatEvent.Message);
        if (string.IsNullOrWhiteSpace(commandPrefix))
        {
            return;
        }

        var eventData = JsonSerializer.Serialize(new
        {
            CommandPrefix = commandPrefix,
            commandResult.Success,
            commandResult.Denied,
            commandResult.ResponseMessage,
            commandContext.PlayerGuid,
            commandContext.Username,
            commandContext.SlotId,
            commandContext.EventGeneratedUtc,
            commandContext.EventPublishedUtc
        }, JsonOptions.Default);

        try
        {
            await repositoryApiClient.GameServersEvents.V1
                .CreateGameServerEvent(
                    new CreateGameServerEventDto(
                        chatEvent.ServerId,
                        commandResult.Denied ? ChatCommandDeniedEventType : ChatCommandExecutionEventType,
                        eventData),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex,
                "Failed to persist ChatCommandExecution event for {CommandPrefix} on server {ServerId}",
                commandPrefix,
                chatEvent.ServerId);
        }
    }

    private static string? ExtractCommandPrefix(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var trimmed = message.TrimStart();
        if (!trimmed.StartsWith('!'))
            return null;

        var firstToken = trimmed
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(firstToken) ? null : firstToken;
    }

    private async Task<PlayerContextInfo?> GetPlayerContext(GameType gameType, string guid)
    {
        var cacheKey = $"player-ctx-{gameType}-{guid}";

        if (memoryCache.TryGetValue(cacheKey, out PlayerContextInfo cached))
            return cached;

        var response = await repositoryApiClient.Players.V1
            .GetPlayerByGameType(gameType, guid, PlayerEntityOptions.Tags)
            .ConfigureAwait(false);

        if (!response.IsSuccess || response.Result?.Data is null)
            return null;

        var player = response.Result.Data;
        var moderateTagName = configuration["ContentSafety:ModerateChatTagName"] ?? "moderate-chat";
        var hasTag = player.Tags.Any(t =>
            string.Equals(t.Tag?.Name, moderateTagName, StringComparison.OrdinalIgnoreCase));

        var tagNames = player.Tags
            .Select(t => t.Tag?.Name)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var (claimNames, claimsResolved) = await GetAuthorizationClaimsAsync(gameType, player.PlayerId).ConfigureAwait(false);
        var authorizationSnapshot = new CommandAuthorizationSnapshot
        {
            Tags = tagNames,
            Claims = claimNames,
            TagsResolved = true,
            ClaimsResolved = claimsResolved
        };

        var ctx = new PlayerContextInfo(player.PlayerId, player.FirstSeen, hasTag, authorizationSnapshot);
        if (authorizationSnapshot.ClaimsResolved)
        {
            memoryCache.Set(cacheKey, ctx,
                new MemoryCacheEntryOptions().SetSlidingExpiration(PlayerCacheExpiration));
        }

        return ctx;
    }

    private async Task<(IReadOnlySet<string> Claims, bool ClaimsResolved)> GetAuthorizationClaimsAsync(GameType gameType, Guid playerId)
    {
        try
        {
            var connectedPlayers = await repositoryApiClient.ConnectedPlayers.V1
                .GetConnectedPlayers(playerId, null, gameType, true, 0, 1)
                .ConfigureAwait(false);

            if (!connectedPlayers.IsSuccess)
            {
                return (new HashSet<string>(StringComparer.OrdinalIgnoreCase), false);
            }

            var connectedPlayer = connectedPlayers.Result?.Data?.Items?.FirstOrDefault();
            if (connectedPlayer is null)
            {
                return (new HashSet<string>(StringComparer.OrdinalIgnoreCase), true);
            }

            var userProfile = await repositoryApiClient.UserProfiles.V1
                .GetUserProfile(connectedPlayer.UserProfileId)
                .ConfigureAwait(false);

            if (!userProfile.IsSuccess || userProfile.Result?.Data is null)
            {
                return (new HashSet<string>(StringComparer.OrdinalIgnoreCase), false);
            }

            var claims = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var claim in userProfile.Result.Data.UserProfileClaims)
            {
                if (string.IsNullOrWhiteSpace(claim.ClaimType))
                {
                    continue;
                }

                claims.Add(claim.ClaimType);
                if (!string.IsNullOrWhiteSpace(claim.ClaimValue))
                {
                    claims.Add($"{claim.ClaimType}:{claim.ClaimValue}");
                }
            }

            return (claims, true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve authorization claims for player {PlayerId}", playerId);
            return (new HashSet<string>(StringComparer.OrdinalIgnoreCase), false);
        }
    }

    private readonly record struct PlayerContextInfo(
        Guid PlayerId,
        DateTime FirstSeen,
        bool HasModerateChatTag,
        CommandAuthorizationSnapshot AuthorizationSnapshot);
}
