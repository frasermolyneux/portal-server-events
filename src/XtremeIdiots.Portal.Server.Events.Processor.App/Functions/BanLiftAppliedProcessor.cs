using System.Text.Json;

using Azure.Messaging.ServiceBus;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.GameServers;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

public class BanLiftAppliedProcessor(
    ILogger<BanLiftAppliedProcessor> logger,
    IRepositoryApiClient repositoryApiClient,
    IAuditLogger auditLogger)
{
    [Function(nameof(ProcessBanLiftApplied))]
    public async Task ProcessBanLiftApplied(
        [ServiceBusTrigger(Queues.BanLiftApplied, Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        FunctionContext context)
    {
        BanLiftAppliedEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<BanLiftAppliedEvent>(message.Body, JsonOptions.Default);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "BanLiftApplied message was not in expected format. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (evt is null)
        {
            logger.LogWarning("BanLiftApplied deserialized to null. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (evt.ServerId == Guid.Empty)
        {
            logger.LogWarning("BanLiftApplied has empty ServerId. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (string.IsNullOrWhiteSpace(evt.GameType))
        {
            logger.LogWarning("BanLiftApplied has empty GameType. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (!Enum.TryParse<GameType>(evt.GameType, out _))
        {
            logger.LogWarning("BanLiftApplied has invalid GameType: {GameType}", evt.GameType);
            return;
        }

        if (string.IsNullOrWhiteSpace(evt.PlayerGuid) ||
            string.IsNullOrWhiteSpace(evt.PlayerName) ||
            string.IsNullOrWhiteSpace(evt.Source) ||
            string.IsNullOrWhiteSpace(evt.LiftReason))
        {
            logger.LogWarning(
                "BanLiftApplied missing required fields. PlayerGuid: {PlayerGuid}, PlayerName: {PlayerName}, Source: {Source}",
                evt.PlayerGuid,
                evt.PlayerName,
                evt.Source);
            return;
        }

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["GameType"] = evt.GameType,
            ["ServerId"] = evt.ServerId,
            ["PlayerGuid"] = evt.PlayerGuid
        });

        var eventData = JsonSerializer.Serialize(evt, JsonOptions.Default);

        var gameServerEventDto = new CreateGameServerEventDto(
            evt.ServerId,
            "BanLiftApplied",
            eventData);

        var result = await repositoryApiClient.GameServersEvents.V1
            .CreateGameServerEvent(gameServerEventDto)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            logger.LogWarning(
                "Failed to persist BanLiftApplied game server event for server {ServerId}. Status: {StatusCode}",
                evt.ServerId,
                result.StatusCode);
            throw new InvalidOperationException($"Failed to persist BanLiftApplied game server event. Status: {result.StatusCode}");
        }

        auditLogger.LogAudit(AuditEvent.ServerAction("BanLiftApplied", AuditAction.Update)
            .WithGameContext(evt.GameType, evt.ServerId)
            .WithPlayer(evt.PlayerGuid, evt.PlayerName)
            .WithProperty("Source", evt.Source)
            .WithProperty("LiftReason", evt.LiftReason)
            .Build());
    }
}