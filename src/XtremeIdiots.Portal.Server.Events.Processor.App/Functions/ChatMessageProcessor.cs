using System.Text.Json;

using Azure.Messaging.ServiceBus;

using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ChatMessages;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;
using XtremeIdiots.Portal.Server.Events.Processor.App.Moderation;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

public class ChatMessageProcessor(
    ILogger<ChatMessageProcessor> logger,
    IRepositoryApiClient repositoryApiClient,
    IMemoryCache memoryCache,
    TelemetryClient telemetryClient,
    IChatCommandProcessor chatCommandProcessor,
    IChatModerationPipeline moderationPipeline,
    IConfiguration configuration)
{
    private static readonly TimeSpan DelayWarningThreshold = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PlayerCacheExpiration = TimeSpan.FromMinutes(15);

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

        TrackEvent("ChatMessagePersisted", chatEvent);

        // Process commands after persisting the chat message
        var commandContext = new CommandContext
        {
            ServerId = chatEvent.ServerId,
            GameType = chatEvent.GameType,
            PlayerGuid = chatEvent.PlayerGuid,
            Username = chatEvent.Username,
            Message = chatEvent.Message,
            EventGeneratedUtc = chatEvent.EventGeneratedUtc,
            EventPublishedUtc = chatEvent.EventPublishedUtc,
            PlayerId = playerId
        };

        var commandResult = await chatCommandProcessor.ProcessAsync(commandContext, context.CancellationToken).ConfigureAwait(false);

        if (commandResult.Handled)
        {
            logger.LogInformation("Command processed for {Username}: Success={Success}",
                chatEvent.Username, commandResult.Success);
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

        var ctx = new PlayerContextInfo(player.PlayerId, player.FirstSeen, hasTag);
        memoryCache.Set(cacheKey, ctx,
            new MemoryCacheEntryOptions().SetSlidingExpiration(PlayerCacheExpiration));

        return ctx;
    }

    private readonly record struct PlayerContextInfo(Guid PlayerId, DateTime FirstSeen, bool HasModerateChatTag);

    private void TrackEvent(string eventName, ChatMessageEvent chatEvent)
    {
        telemetryClient.TrackEvent(new EventTelemetry(eventName)
        {
            Properties =
            {
                ["GameType"] = chatEvent.GameType,
                ["ServerId"] = chatEvent.ServerId.ToString(),
                ["PlayerGuid"] = chatEvent.PlayerGuid,
                ["ChatType"] = chatEvent.Type.ToString()
            }
        });
    }
}
