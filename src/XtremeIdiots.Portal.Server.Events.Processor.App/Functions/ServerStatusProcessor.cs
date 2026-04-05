using System.Text.Json;

using Azure.Messaging.ServiceBus;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

/// <summary>
/// Processes server status snapshot events. Currently logs for observability.
/// Live stats updating (GameServer fields, player list, analytics snapshots) will be added later.
/// </summary>
public sealed class ServerStatusProcessor
{
    private readonly ILogger<ServerStatusProcessor> _logger;

    public ServerStatusProcessor(ILogger<ServerStatusProcessor> logger)
    {
        _logger = logger;
    }

    [Function(nameof(ProcessServerStatus))]
    public Task ProcessServerStatus(
        [ServiceBusTrigger(Queues.ServerStatus, Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        FunctionContext context)
    {
        try
        {
            var evt = message.Body.ToObjectFromJson<ServerStatusEvent>(JsonOptions.Default);

            if (evt is null)
            {
                _logger.LogWarning("ServerStatus was not in expected format. MessageId: {MessageId}", message.MessageId);
                return Task.CompletedTask;
            }

            _logger.LogDebug("Server status: {ServerId} - {PlayerCount} players on {MapName}",
                evt.ServerId, evt.PlayerCount, evt.MapName);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "ServerStatus was not in expected format. MessageId: {MessageId}", message.MessageId);
        }

        return Task.CompletedTask;
    }
}
