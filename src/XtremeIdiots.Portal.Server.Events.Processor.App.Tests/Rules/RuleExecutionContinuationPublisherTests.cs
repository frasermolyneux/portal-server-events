using System.Text.Json;

using Azure.Messaging.ServiceBus;

using Moq;

using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Rules;

public sealed class RuleExecutionContinuationPublisherTests
{
    [Fact]
    public async Task SchedulesOriginalExecutionAtRequestedDueTime()
    {
        var serverId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var dueAtUtc = DateTimeOffset.UtcNow.AddSeconds(43);
        var sender = new Mock<ServiceBusSender>();
        var client = new Mock<ServiceBusClient>();
        client.Setup(item => item.CreateSender(Queues.RuleActionContinuations)).Returns(sender.Object);
        ServiceBusMessage? published = null;
        sender.Setup(item => item.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, CancellationToken>((message, _) => published = message)
            .Returns(Task.CompletedTask);

        await new RuleExecutionContinuationPublisher(client.Object)
            .ScheduleAsync(serverId, executionId, dueAtUtc, default);

        Assert.NotNull(published);
        Assert.Equal(dueAtUtc, published.ScheduledEnqueueTime);
        Assert.Equal(RuleExecutionContinuationPublisher.MessageKind, published.ApplicationProperties["kind"]);
        Assert.Equal(new RuleExecutionContinuation(serverId, executionId),
            JsonSerializer.Deserialize<RuleExecutionContinuation>(published.Body, RulesSerialization.Options));
    }
}
