using System.Text.Json;

using Azure.Messaging.ServiceBus;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

/// <summary>
/// Processes ban file change events. Currently logs for observability.
/// Ban file parsing and admin action creation will be added later.
/// </summary>
public sealed class BanFileChangedProcessor
{
    private readonly ILogger<BanFileChangedProcessor> _logger;

    public BanFileChangedProcessor(ILogger<BanFileChangedProcessor> logger)
    {
        _logger = logger;
    }

    [Function(nameof(ProcessBanFileChanged))]
    public Task ProcessBanFileChanged(
        [ServiceBusTrigger(Queues.BanFileChanged, Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        FunctionContext context)
    {
        try
        {
            var evt = message.Body.ToObjectFromJson<BanFileChangedEvent>(JsonOptions.Default);

            if (evt is null)
            {
                _logger.LogWarning("BanFileChanged was not in expected format. MessageId: {MessageId}", message.MessageId);
                return Task.CompletedTask;
            }

            _logger.LogInformation("Ban file changed on server {ServerId}: {FilePath} ({FileSize} bytes)",
                evt.ServerId, evt.FilePath, evt.FileSize);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "BanFileChanged was not in expected format. MessageId: {MessageId}", message.MessageId);
        }

        return Task.CompletedTask;
    }
}
