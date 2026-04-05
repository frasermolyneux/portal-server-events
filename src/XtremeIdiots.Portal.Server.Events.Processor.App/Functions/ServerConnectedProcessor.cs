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

public class ServerConnectedProcessor(
    ILogger<ServerConnectedProcessor> logger,
    IRepositoryApiClient repositoryApiClient,
    TelemetryClient telemetryClient)
{
    [Function(nameof(ProcessServerConnected))]
    public async Task ProcessServerConnected(
        [ServiceBusTrigger(Queues.ServerConnected, Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        FunctionContext context)
    {
        ServerConnectedEvent? serverEvent;
        try
        {
            serverEvent = JsonSerializer.Deserialize<ServerConnectedEvent>(message.Body, JsonOptions.Default);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "ServerConnected message was not in expected format. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (serverEvent is null)
        {
            logger.LogWarning("ServerConnected deserialized to null. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (serverEvent.ServerId == Guid.Empty)
        {
            logger.LogWarning("ServerConnected has empty ServerId. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (string.IsNullOrWhiteSpace(serverEvent.GameType))
        {
            logger.LogWarning("ServerConnected has empty GameType. MessageId: {MessageId}", message.MessageId);
            return;
        }

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["GameType"] = serverEvent.GameType,
            ["ServerId"] = serverEvent.ServerId
        });

        var eventData = JsonSerializer.Serialize(serverEvent, JsonOptions.Default);

        var gameServerEventDto = new CreateGameServerEventDto(
            serverEvent.ServerId,
            "OnServerConnected",
            eventData);

        await repositoryApiClient.GameServersEvents.V1
            .CreateGameServerEvent(gameServerEventDto)
            .ConfigureAwait(false);

        telemetryClient.TrackEvent(new EventTelemetry("ServerConnected")
        {
            Properties =
            {
                ["GameType"] = serverEvent.GameType,
                ["ServerId"] = serverEvent.ServerId.ToString()
            }
        });
    }
}
