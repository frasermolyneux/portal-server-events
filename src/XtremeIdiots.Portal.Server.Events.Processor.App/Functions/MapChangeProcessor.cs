using System.Text.Json;

using Azure.Messaging.ServiceBus;

using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.GameServers;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

public class MapChangeProcessor(
    ILogger<MapChangeProcessor> logger,
    IRepositoryApiClient repositoryApiClient,
    TelemetryClient telemetryClient)
{
    [Function(nameof(ProcessMapChange))]
    public async Task ProcessMapChange(
        [ServiceBusTrigger(Queues.MapChange, Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        FunctionContext context)
    {
        MapChangeEvent? mapEvent;
        try
        {
            mapEvent = JsonSerializer.Deserialize<MapChangeEvent>(message.Body, JsonOptions.Default);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "MapChange message was not in expected format. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (mapEvent is null)
        {
            logger.LogWarning("MapChange deserialized to null. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (mapEvent.ServerId == Guid.Empty)
        {
            logger.LogWarning("MapChange has empty ServerId. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (string.IsNullOrWhiteSpace(mapEvent.MapName))
        {
            logger.LogWarning("MapChange has empty MapName. ServerId: {ServerId}", mapEvent.ServerId);
            return;
        }

        if (string.IsNullOrWhiteSpace(mapEvent.GameType))
        {
            logger.LogWarning("MapChange has empty GameType. ServerId: {ServerId}", mapEvent.ServerId);
            return;
        }

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["GameType"] = mapEvent.GameType,
            ["ServerId"] = mapEvent.ServerId,
            ["MapName"] = mapEvent.MapName
        });

        var eventData = JsonSerializer.Serialize(mapEvent, JsonOptions.Default);

        var gameServerEventDto = new CreateGameServerEventDto(
            mapEvent.ServerId,
            "MapChange",
            eventData);

        await repositoryApiClient.GameServersEvents.V1
            .CreateGameServerEvent(gameServerEventDto)
            .ConfigureAwait(false);

        telemetryClient.TrackEvent(new EventTelemetry("MapChange")
        {
            Properties =
            {
                ["GameType"] = mapEvent.GameType,
                ["ServerId"] = mapEvent.ServerId.ToString(),
                ["MapName"] = mapEvent.MapName
            }
        });
    }
}
