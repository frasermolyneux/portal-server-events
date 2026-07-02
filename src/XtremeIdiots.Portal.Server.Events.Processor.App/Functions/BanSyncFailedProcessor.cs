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

public class BanSyncFailedProcessor(
    ILogger<BanSyncFailedProcessor> logger,
    IRepositoryApiClient repositoryApiClient,
    IAuditLogger auditLogger)
{
    [Function(nameof(ProcessBanSyncFailed))]
    public async Task ProcessBanSyncFailed(
        [ServiceBusTrigger(Queues.BanSyncFailed, Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        FunctionContext context)
    {
        BanSyncFailedEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<BanSyncFailedEvent>(message.Body, JsonOptions.Default);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "BanSyncFailed message was not in expected format. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (evt is null)
        {
            logger.LogWarning("BanSyncFailed deserialized to null. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (evt.ServerId == Guid.Empty)
        {
            logger.LogWarning("BanSyncFailed has empty ServerId. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (string.IsNullOrWhiteSpace(evt.GameType))
        {
            logger.LogWarning("BanSyncFailed has empty GameType. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (!Enum.TryParse<GameType>(evt.GameType, out _))
        {
            logger.LogWarning("BanSyncFailed has invalid GameType: {GameType}", evt.GameType);
            return;
        }

        if (string.IsNullOrWhiteSpace(evt.Operation) ||
            string.IsNullOrWhiteSpace(evt.FailureReason) ||
            string.IsNullOrWhiteSpace(evt.Source))
        {
            logger.LogWarning(
                "BanSyncFailed missing required fields. Operation: {Operation}, Source: {Source}",
                evt.Operation,
                evt.Source);
            return;
        }

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["GameType"] = evt.GameType,
            ["ServerId"] = evt.ServerId,
            ["Operation"] = evt.Operation
        });

        var eventData = JsonSerializer.Serialize(evt, JsonOptions.Default);

        var gameServerEventDto = new CreateGameServerEventDto(
            evt.ServerId,
            "BanSyncFailed",
            eventData);

        var result = await repositoryApiClient.GameServersEvents.V1
            .CreateGameServerEvent(gameServerEventDto)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            logger.LogWarning(
                "Failed to persist BanSyncFailed game server event for server {ServerId}. Status: {StatusCode}",
                evt.ServerId,
                result.StatusCode);
            throw new InvalidOperationException($"Failed to persist BanSyncFailed game server event. Status: {result.StatusCode}");
        }

        var auditBuilder = AuditEvent.ServerAction("BanSyncFailed", AuditAction.Update)
            .WithGameContext(evt.GameType, evt.ServerId)
            .WithProperty("Operation", evt.Operation)
            .WithProperty("Source", evt.Source)
            .WithProperty("FailureReason", evt.FailureReason);

        if (!string.IsNullOrWhiteSpace(evt.PlayerGuid))
        {
            auditBuilder = auditBuilder.WithPlayer(evt.PlayerGuid, evt.PlayerName);
        }

        auditLogger.LogAudit(auditBuilder.Build());
    }
}