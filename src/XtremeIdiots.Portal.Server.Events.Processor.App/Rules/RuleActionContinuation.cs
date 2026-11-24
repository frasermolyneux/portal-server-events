using System.Text.Json;

using Azure.Messaging.ServiceBus;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

internal sealed record RuleActionPublicationIntent(
    string LogicalEffectId,
    Guid ExecutionId,
    string ActionId,
    Guid GameServerId);

internal sealed record RuleActionContinuation(
    string LogicalEffectId,
    Guid ExecutionId,
    string ActionId,
    Guid AdminActionId,
    Guid GameServerId,
    GameType GameType,
    Guid PlayerId,
    string? Username,
    AdminActionType ActionType,
    DateTime Created,
    string Text,
    string? AdminId);

internal interface IRuleActionContinuationPublisher
{
    Task PublishAsync(RuleActionPublicationIntent intent, CancellationToken ct = default);
}

internal sealed class RuleActionContinuationPublisher(ServiceBusClient client) : IRuleActionContinuationPublisher, IAsyncDisposable
{
    private readonly ServiceBusSender sender = client.CreateSender(Queues.RuleActionContinuations);

    public Task PublishAsync(RuleActionPublicationIntent intent, CancellationToken ct = default) =>
        sender.SendMessageAsync(CreateMessage(intent), ct);

    public Task ScheduleAsync(RuleActionPublicationIntent intent, DateTimeOffset scheduledAtUtc, CancellationToken ct) =>
        sender.ScheduleMessageAsync(CreateMessage(intent), scheduledAtUtc, ct);

    public ValueTask DisposeAsync() => sender.DisposeAsync();

    private static ServiceBusMessage CreateMessage(RuleActionPublicationIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.LogicalEffectId);
        return new ServiceBusMessage(BinaryData.FromString(JsonSerializer.Serialize(intent, JsonOptions.Default)))
        {
            MessageId = intent.LogicalEffectId,
            ContentType = "application/json"
        };
    }
}
