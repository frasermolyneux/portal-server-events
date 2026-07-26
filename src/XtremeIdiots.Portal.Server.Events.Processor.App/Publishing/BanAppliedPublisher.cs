using System.Text.Json;

using Azure.Messaging.ServiceBus;

using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Publishing;

/// <summary>
/// Sends <see cref="BanAppliedEvent"/> messages to the shared ban-applied queue using the app's
/// <see cref="ServiceBusClient"/>. Serialises with the shared <see cref="JsonOptions.Default"/>
/// camelCase contract the agent publishes and <c>BanAppliedProcessor</c> consumes.
/// </summary>
internal sealed class BanAppliedPublisher(ServiceBusClient serviceBusClient) : IBanAppliedPublisher
{
    private readonly ServiceBusSender sender = serviceBusClient.CreateSender(Queues.BanApplied);

    public async Task PublishAsync(BanAppliedEvent banAppliedEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(banAppliedEvent);

        var body = JsonSerializer.Serialize(banAppliedEvent, JsonOptions.Default);
        var message = new ServiceBusMessage(BinaryData.FromString(body))
        {
            ContentType = "application/json",
            MessageId = $"{banAppliedEvent.ServerId}-{banAppliedEvent.PlayerGuid}-{banAppliedEvent.EventGeneratedUtc.Ticks}"
        };

        await sender.SendMessageAsync(message, ct).ConfigureAwait(false);
    }
}
