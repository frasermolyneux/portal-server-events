using System.Text.Json;

using Azure.Messaging.ServiceBus;

using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

internal sealed record RuleExecutionContinuation(Guid GameServerId, Guid ExecutionId);

internal interface IRuleExecutionContinuationPublisher
{
    Task ScheduleAsync(Guid serverId, Guid executionId, CancellationToken ct);
}

internal sealed class RuleExecutionContinuationPublisher(ServiceBusClient client) : IRuleExecutionContinuationPublisher
{
    private readonly ServiceBusSender sender = client.CreateSender(Queues.RuleActionContinuations);
    public const string MessageKind = "rules-execution-resume";

    public Task ScheduleAsync(Guid serverId, Guid executionId, CancellationToken ct)
    {
        var message = new ServiceBusMessage(BinaryData.FromString(
            JsonSerializer.Serialize(new RuleExecutionContinuation(serverId, executionId), JsonOptions.Default)))
        {
            MessageId = $"resume:{executionId:N}",
            ContentType = "application/json",
            ScheduledEnqueueTime = DateTimeOffset.UtcNow.AddSeconds(10)
        };
        message.ApplicationProperties["kind"] = MessageKind;
        return sender.SendMessageAsync(message, ct);
    }
}
