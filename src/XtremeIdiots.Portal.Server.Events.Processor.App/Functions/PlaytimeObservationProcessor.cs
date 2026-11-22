using System.Text.Json;

using Azure.Messaging.ServiceBus;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Services;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

public sealed class PlaytimeObservationProcessor(
    IPlaytimeIngestion ingestion,
    ILogger<PlaytimeObservationProcessor> logger)
{
    [Function(nameof(ProcessPlaytimeObservation))]
    public async Task ProcessPlaytimeObservation(
        [ServiceBusTrigger(Queues.PlaytimeObservation, Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        FunctionContext context)
    {
        PlaytimeObservationEvent? observation;
        try
        {
            observation = JsonSerializer.Deserialize<PlaytimeObservationEvent>(message.Body, JsonOptions.Default);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid playtime observation message {MessageId}", message.MessageId);
            throw;
        }

        if (observation is null)
        {
            throw new InvalidOperationException($"Playtime observation {message.MessageId} was null.");
        }

        await ingestion.ProcessAsync(observation, context.CancellationToken).ConfigureAwait(false);
    }
}
