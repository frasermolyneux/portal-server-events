using System.Text.Json;

using Azure.Messaging.ServiceBus;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

internal sealed record RuleActionContinuation(
    string LogicalEffectId,
    Guid ExecutionId,
    string ActionId,
    Guid AdminActionId,
    Guid GameServerId,
    GameType GameType,
    Guid PlayerId,
    string Username,
    AdminActionType ActionType,
    DateTime Created,
    string Text,
    string? AdminId);

internal interface IRuleActionContinuationPublisher
{
    Task PublishAsync(RuleActionContinuation continuation, CancellationToken ct = default);
}

internal sealed class RuleActionContinuationPublisher(ServiceBusClient client) : IRuleActionContinuationPublisher
{
    private readonly ServiceBusSender sender = client.CreateSender(Queues.RuleActionContinuations);

    public Task PublishAsync(RuleActionContinuation continuation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        ArgumentException.ThrowIfNullOrWhiteSpace(continuation.LogicalEffectId);

        var body = JsonSerializer.Serialize(continuation, JsonOptions.Default);
        var message = new ServiceBusMessage(BinaryData.FromString(body))
        {
            MessageId = continuation.LogicalEffectId,
            ContentType = "application/json"
        };
        return sender.SendMessageAsync(message, ct);
    }
}
