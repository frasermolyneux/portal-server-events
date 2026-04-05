using System.Text.Json;

using Azure.Messaging.ServiceBus;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

/// <summary>
/// Processes player disconnected events. Currently logs the event for observability.
/// Live player list management will be added when the server-status processor is implemented.
/// </summary>
public sealed class PlayerDisconnectedProcessor
{
    private readonly ILogger<PlayerDisconnectedProcessor> _logger;

    public PlayerDisconnectedProcessor(ILogger<PlayerDisconnectedProcessor> logger)
    {
        _logger = logger;
    }

    [Function(nameof(ProcessPlayerDisconnected))]
    public Task ProcessPlayerDisconnected(
        [ServiceBusTrigger(Queues.PlayerDisconnected, Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        FunctionContext context)
    {
        try
        {
            var evt = message.Body.ToObjectFromJson<PlayerDisconnectedEvent>(JsonOptions.Default);

            if (evt is null)
            {
                _logger.LogWarning("PlayerDisconnected was not in expected format. MessageId: {MessageId}", message.MessageId);
                return Task.CompletedTask;
            }

            _logger.LogInformation("Player disconnected: {Username} ({PlayerGuid}) from server {ServerId}",
                evt.Username, evt.PlayerGuid, evt.ServerId);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "PlayerDisconnected was not in expected format. MessageId: {MessageId}", message.MessageId);
        }

        return Task.CompletedTask;
    }
}
