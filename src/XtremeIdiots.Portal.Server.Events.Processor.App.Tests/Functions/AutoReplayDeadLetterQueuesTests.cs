using Azure.Messaging.ServiceBus;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.FeatureManagement;

using Moq;

using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Functions;

public class AutoReplayDeadLetterQueuesTests
{
    private readonly Mock<ILogger<AutoReplayDeadLetterQueues>> _logger = new();
    private readonly Mock<ServiceBusClient> _serviceBusClient = new();
    private readonly Mock<IFeatureManager> _featureManager = new();
    private readonly Mock<FunctionContext> _functionContext = new();
    private readonly AutoReplayDeadLetterQueues _sut;

    public AutoReplayDeadLetterQueuesTests()
    {
        _sut = new AutoReplayDeadLetterQueues(_logger.Object, _serviceBusClient.Object, _featureManager.Object);
    }

    private static TimerInfo CreateTimerInfo()
    {
        return new TimerInfo();
    }

    [Fact]
    public async Task Run_WhenFeatureDisabled_DoesNothing()
    {
        _featureManager.Setup(f => f.IsEnabledAsync("ServerEvents.AutoDlqReplay"))
            .ReturnsAsync(false);

        await _sut.Run(CreateTimerInfo(), _functionContext.Object);

        _serviceBusClient.Verify(c => c.CreateReceiver(It.IsAny<string>(), It.IsAny<ServiceBusReceiverOptions>()), Times.Never);
    }

    [Fact]
    public async Task Run_WhenFeatureEnabled_ReprocessesAllQueues()
    {
        _featureManager.Setup(f => f.IsEnabledAsync("ServerEvents.AutoDlqReplay"))
            .ReturnsAsync(true);

        // Setup empty receivers for all queues (no DLQ messages)
        var mockReceiver = new Mock<ServiceBusReceiver>();
        mockReceiver.Setup(r => r.ReceiveMessagesAsync(It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ServiceBusReceivedMessage>().AsReadOnly() as IReadOnlyList<ServiceBusReceivedMessage>);

        _serviceBusClient.Setup(c => c.CreateReceiver(
                It.IsAny<string>(),
                It.Is<ServiceBusReceiverOptions>(o => o.SubQueue == SubQueue.DeadLetter)))
            .Returns(mockReceiver.Object);

        var mockSender = new Mock<ServiceBusSender>();
        _serviceBusClient.Setup(c => c.CreateSender(It.IsAny<string>()))
            .Returns(mockSender.Object);

        await _sut.Run(CreateTimerInfo(), _functionContext.Object);

        // Should have created receivers for all 7 queues
        _serviceBusClient.Verify(c => c.CreateReceiver(
            It.IsAny<string>(),
            It.Is<ServiceBusReceiverOptions>(o => o.SubQueue == SubQueue.DeadLetter)), Times.Exactly(7));
    }

    [Fact]
    public async Task Run_WhenQueueFails_ContinuesToNextQueue()
    {
        _featureManager.Setup(f => f.IsEnabledAsync("ServerEvents.AutoDlqReplay"))
            .ReturnsAsync(true);

        // First receiver throws, second returns empty
        var failingReceiver = new Mock<ServiceBusReceiver>();
        failingReceiver.Setup(r => r.ReceiveMessagesAsync(It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceBusException("Transient error", ServiceBusFailureReason.ServiceTimeout));

        var emptyReceiver = new Mock<ServiceBusReceiver>();
        emptyReceiver.Setup(r => r.ReceiveMessagesAsync(It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ServiceBusReceivedMessage>().AsReadOnly() as IReadOnlyList<ServiceBusReceivedMessage>);

        // First queue fails, all others succeed
        _serviceBusClient.SetupSequence(c => c.CreateReceiver(
                It.IsAny<string>(),
                It.Is<ServiceBusReceiverOptions>(o => o.SubQueue == SubQueue.DeadLetter)))
            .Returns(failingReceiver.Object)
            .Returns(emptyReceiver.Object)
            .Returns(emptyReceiver.Object)
            .Returns(emptyReceiver.Object)
            .Returns(emptyReceiver.Object)
            .Returns(emptyReceiver.Object)
            .Returns(emptyReceiver.Object);

        var mockSender = new Mock<ServiceBusSender>();
        _serviceBusClient.Setup(c => c.CreateSender(It.IsAny<string>()))
            .Returns(mockSender.Object);

        // Should NOT throw — it logs the error and continues
        await _sut.Run(CreateTimerInfo(), _functionContext.Object);

        // Should still have tried all 7 queues
        _serviceBusClient.Verify(c => c.CreateReceiver(
            It.IsAny<string>(),
            It.Is<ServiceBusReceiverOptions>(o => o.SubQueue == SubQueue.DeadLetter)), Times.Exactly(7));
    }

    [Fact]
    public async Task ReplayQueueDlq_ReplaysMessagesWithReplayCount()
    {
        var dlqMessage = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData("{\"test\":true}"),
            messageId: "msg-1",
            sequenceNumber: 1);

        var mockReceiver = new Mock<ServiceBusReceiver>();
        mockReceiver.SetupSequence(r => r.ReceiveMessagesAsync(It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { dlqMessage }.ToList().AsReadOnly() as IReadOnlyList<ServiceBusReceivedMessage>)
            .ReturnsAsync(new List<ServiceBusReceivedMessage>().AsReadOnly() as IReadOnlyList<ServiceBusReceivedMessage>);

        var mockSender = new Mock<ServiceBusSender>();
        mockSender.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _serviceBusClient.Setup(c => c.CreateReceiver(
                "chat-message",
                It.Is<ServiceBusReceiverOptions>(o => o.SubQueue == SubQueue.DeadLetter)))
            .Returns(mockReceiver.Object);

        _serviceBusClient.Setup(c => c.CreateSender("chat-message"))
            .Returns(mockSender.Object);

        var count = await _sut.ReplayQueueDlq("chat-message");

        Assert.Equal(1, count);
        mockSender.Verify(s => s.SendMessageAsync(
            It.Is<ServiceBusMessage>(m => (int)m.ApplicationProperties["DlqReplayCount"] == 1),
            It.IsAny<CancellationToken>()), Times.Once);
        mockReceiver.Verify(r => r.CompleteMessageAsync(dlqMessage, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReplayQueueDlq_SkipsMessagesExceedingMaxRetries()
    {
        var properties = new Dictionary<string, object> { ["DlqReplayCount"] = 3 };
        var dlqMessage = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData("{\"test\":true}"),
            messageId: "msg-max",
            properties: properties,
            sequenceNumber: 1);

        var mockReceiver = new Mock<ServiceBusReceiver>();
        mockReceiver.SetupSequence(r => r.ReceiveMessagesAsync(It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { dlqMessage }.ToList().AsReadOnly() as IReadOnlyList<ServiceBusReceivedMessage>)
            .ReturnsAsync(new List<ServiceBusReceivedMessage>().AsReadOnly() as IReadOnlyList<ServiceBusReceivedMessage>);

        var mockSender = new Mock<ServiceBusSender>();

        _serviceBusClient.Setup(c => c.CreateReceiver(
                "chat-message",
                It.Is<ServiceBusReceiverOptions>(o => o.SubQueue == SubQueue.DeadLetter)))
            .Returns(mockReceiver.Object);

        _serviceBusClient.Setup(c => c.CreateSender("chat-message"))
            .Returns(mockSender.Object);

        var count = await _sut.ReplayQueueDlq("chat-message");

        Assert.Equal(0, count);
        mockSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        mockReceiver.Verify(r => r.CompleteMessageAsync(dlqMessage, It.IsAny<CancellationToken>()), Times.Once);
    }
}
