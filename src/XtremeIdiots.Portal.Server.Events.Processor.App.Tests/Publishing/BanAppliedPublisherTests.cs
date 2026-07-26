using Azure.Messaging.ServiceBus;

using Moq;

using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Publishing;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Publishing;

public sealed class BanAppliedPublisherTests
{
    [Fact]
    public async Task PublishAsync_SendsCamelCaseBanAppliedEventToBanAppliedQueue()
    {
        var client = new Mock<ServiceBusClient>();
        var sender = new Mock<ServiceBusSender>();
        client.Setup(c => c.CreateSender("ban-applied")).Returns(sender.Object);

        ServiceBusMessage? sent = null;
        sender
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, CancellationToken>((message, _) => sent = message)
            .Returns(Task.CompletedTask);

        var now = DateTime.UtcNow;
        var evt = new BanAppliedEvent
        {
            EventGeneratedUtc = now,
            EventPublishedUtc = now,
            ServerId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            GameType = "CallOfDuty4x",
            SequenceId = 123,
            PlayerGuid = "player-guid",
            PlayerName = "TestPlayer",
            IsTemporary = false,
            Source = "CoD4xVpnProtection",
            Reason = "VPN Protection"
        };

        var publisher = new BanAppliedPublisher(client.Object);
        await publisher.PublishAsync(evt);

        client.Verify(c => c.CreateSender("ban-applied"), Times.Once);
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(sent);
        Assert.Equal("application/json", sent!.ContentType);

        var body = sent.Body.ToString();
        Assert.Contains("\"source\":\"CoD4xVpnProtection\"", body, StringComparison.Ordinal);
        Assert.Contains("\"playerGuid\":\"player-guid\"", body, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"VPN Protection\"", body, StringComparison.Ordinal);
    }
}
