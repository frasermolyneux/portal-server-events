using System.Text.Json;

using Azure.Messaging.ServiceBus;

using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ChatMessages;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

public class ChatMessageProcessor(
    ILogger<ChatMessageProcessor> logger,
    IRepositoryApiClient repositoryApiClient,
    IMemoryCache memoryCache,
    TelemetryClient telemetryClient)
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
        var playerId = await GetPlayerId(gameType, chatEvent.PlayerGuid).ConfigureAwait(false);

        if (playerId == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"Player not found for Guid '{chatEvent.PlayerGuid}'. Message will retry.");
        }

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
    }

    private async Task<Guid> GetPlayerId(GameType gameType, string guid)
    {
        var cacheKey = $"player-ctx-{gameType}-{guid}";

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
