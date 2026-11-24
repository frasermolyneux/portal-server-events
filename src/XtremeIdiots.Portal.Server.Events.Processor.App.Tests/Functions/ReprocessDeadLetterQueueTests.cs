using System.Net;

using Azure.Messaging.ServiceBus;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Moq;

using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Functions;

public class ReprocessDeadLetterQueueTests
{
    private readonly Mock<ILogger<ReprocessDeadLetterQueue>> _logger = new();
    private readonly Mock<ServiceBusClient> _serviceBusClient = new();
    private readonly Mock<FunctionContext> _functionContext = new();
    private readonly ReprocessDeadLetterQueue _sut;

    public ReprocessDeadLetterQueueTests()
    {
        _sut = new ReprocessDeadLetterQueue(_logger.Object, _serviceBusClient.Object);
    }

    private static HttpRequestData CreateRequest(string url)
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton(Options.Create(new WorkerOptions
        {
            Serializer = new Azure.Core.Serialization.JsonObjectSerializer()
        }));
        var serviceProvider = serviceCollection.BuildServiceProvider();

        var context = new Mock<FunctionContext>();
        context.Setup(c => c.InstanceServices).Returns(serviceProvider);

        var request = new Mock<HttpRequestData>(context.Object);
        request.Setup(r => r.Url).Returns(new Uri(url));
        request.Setup(r => r.CreateResponse()).Returns(() =>
        {
            var response = new Mock<HttpResponseData>(context.Object);
            response.SetupProperty(r => r.StatusCode);
            response.SetupProperty(r => r.Headers, new HttpHeadersCollection());
            response.Setup(r => r.Body).Returns(new MemoryStream());
            return response.Object;
        });
        return request.Object;
    }

    [Fact]
    public async Task Run_MissingQueueName_ReturnsBadRequest()
    {
        var req = CreateRequest("https://localhost/api/v1/ReprocessDeadLetterQueue");

        var result = await _sut.Run(req, _functionContext.Object);

        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
    }

    [Fact]
    public async Task Run_InvalidQueueName_ReturnsBadRequest()
    {
        var req = CreateRequest("https://localhost/api/v1/ReprocessDeadLetterQueue?queueName=unknown-queue");

        var result = await _sut.Run(req, _functionContext.Object);

        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
    }

    [Theory]
    [InlineData("ban-applied")]
    [InlineData("ban-lift-applied")]
    [InlineData("ban-sync-failed")]
    [InlineData("player-ip-resolved")]
    public async Task Run_DryRun_NewBanLifecycleQueues_AreAccepted(string queueName)
    {
        var mockReceiver = new Mock<ServiceBusReceiver>();
        mockReceiver.Setup(r => r.PeekMessagesAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ServiceBusReceivedMessage>().AsReadOnly() as IReadOnlyList<ServiceBusReceivedMessage>);

        _serviceBusClient.Setup(c => c.CreateReceiver(
                queueName,
                It.Is<ServiceBusReceiverOptions>(o => o.SubQueue == SubQueue.DeadLetter)))
            .Returns(mockReceiver.Object);

        var req = CreateRequest($"https://localhost/api/v1/ReprocessDeadLetterQueue?queueName={queueName}&dryRun=true");

        var result = await _sut.Run(req, _functionContext.Object);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        _serviceBusClient.Verify(c => c.CreateReceiver(
            queueName,
            It.Is<ServiceBusReceiverOptions>(o => o.SubQueue == SubQueue.DeadLetter)), Times.Once);
    }

    [Fact]
    public async Task Run_DryRun_PeeksOnly()
    {
        var dlqMessage = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData(/*lang=json,strict*/ "{\"test\":true}"),
            messageId: "msg-1",
            sequenceNumber: 1);

        var mockReceiver = new Mock<ServiceBusReceiver>();
        mockReceiver.Setup(r => r.PeekMessagesAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { dlqMessage }.ToList().AsReadOnly() as IReadOnlyList<ServiceBusReceivedMessage>);

        // Second call returns empty to stop iteration
        mockReceiver.SetupSequence(r => r.PeekMessagesAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { dlqMessage }.ToList().AsReadOnly() as IReadOnlyList<ServiceBusReceivedMessage>)
            .ReturnsAsync(new List<ServiceBusReceivedMessage>().AsReadOnly() as IReadOnlyList<ServiceBusReceivedMessage>);

        _serviceBusClient.Setup(c => c.CreateReceiver(
                "chat-message",
                It.Is<ServiceBusReceiverOptions>(o => o.SubQueue == SubQueue.DeadLetter)))
            .Returns(mockReceiver.Object);

        var req = CreateRequest("https://localhost/api/v1/ReprocessDeadLetterQueue?queueName=chat-message&dryRun=true");

        var result = await _sut.Run(req, _functionContext.Object);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        mockReceiver.Verify(r => r.PeekMessagesAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        mockReceiver.Verify(r => r.CompleteMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Run_WithValidQueueName_ReprocessesMessages()
    {
        var dlqMessage = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData(/*lang=json,strict*/ "{\"test\":true}"),
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
                "player-connected",
                It.Is<ServiceBusReceiverOptions>(o => o.SubQueue == SubQueue.DeadLetter)))
            .Returns(mockReceiver.Object);

        _serviceBusClient.Setup(c => c.CreateSender("player-connected"))
            .Returns(mockSender.Object);

        var req = CreateRequest("https://localhost/api/v1/ReprocessDeadLetterQueue?queueName=player-connected");

        var result = await _sut.Run(req, _functionContext.Object);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        mockSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        mockReceiver.Verify(r => r.CompleteMessageAsync(dlqMessage, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReplayDeadLetterMessages_SkipsMessagesExceedingMaxRetries()
    {
        var properties = new Dictionary<string, object> { ["DlqReplayCount"] = 3 };
        var dlqMessage = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData(/*lang=json,strict*/ "{\"test\":true}"),
            messageId: "msg-max-retries",
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

        var count = await _sut.ReplayDeadLetterMessages("chat-message", 50);

        Assert.Equal(0, count);
        mockSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        mockReceiver.Verify(r => r.CompleteMessageAsync(dlqMessage, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PlayerIpResolvedReplay_LeavesExhaustedMessageInDlq()
    {
        var dlqMessage = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData("{}"),
            messageId: "ip-resolved-max-retries",
            properties: new Dictionary<string, object> { ["DlqReplayCount"] = 3 });
        var receiver = new Mock<ServiceBusReceiver>();
        receiver.Setup(r => r.ReceiveMessagesAsync(It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { dlqMessage });
        var sender = new Mock<ServiceBusSender>();
        _serviceBusClient.Setup(c => c.CreateReceiver(Queues.PlayerIpResolved, It.IsAny<ServiceBusReceiverOptions>()))
            .Returns(receiver.Object);
        _serviceBusClient.Setup(c => c.CreateSender(Queues.PlayerIpResolved)).Returns(sender.Object);

        var count = await _sut.ReplayDeadLetterMessages(Queues.PlayerIpResolved, 10);

        Assert.Equal(0, count);
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        receiver.Verify(r => r.CompleteMessageAsync(dlqMessage, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PlayerIpResolvedReplay_RetainsExhaustedAndReplaysOtherMessages()
    {
        var exhausted = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData("{}"), messageId: "exhausted",
            properties: new Dictionary<string, object> { ["DlqReplayCount"] = 3 });
        var eligible = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData("{}"), messageId: "eligible");
        var receiver = new Mock<ServiceBusReceiver>();
        receiver.SetupSequence(r => r.ReceiveMessagesAsync(It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { exhausted, eligible })
            .ReturnsAsync(Array.Empty<ServiceBusReceivedMessage>());
        var sender = new Mock<ServiceBusSender>();
        _serviceBusClient.Setup(c => c.CreateReceiver(Queues.PlayerIpResolved, It.IsAny<ServiceBusReceiverOptions>()))
            .Returns(receiver.Object);
        _serviceBusClient.Setup(c => c.CreateSender(Queues.PlayerIpResolved)).Returns(sender.Object);

        var count = await _sut.ReplayDeadLetterMessages(Queues.PlayerIpResolved, 2);

        Assert.Equal(1, count);
        sender.Verify(s => s.SendMessageAsync(It.Is<ServiceBusMessage>(m => m.MessageId == "eligible"), It.IsAny<CancellationToken>()), Times.Once);
        receiver.Verify(r => r.CompleteMessageAsync(exhausted, It.IsAny<CancellationToken>()), Times.Never);
        receiver.Verify(r => r.CompleteMessageAsync(eligible, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PlayerIpResolvedReplay_AdvancesPastAFullBatchOfExhaustedMessages()
    {
        var exhausted = Enumerable.Range(0, 20).Select(i => ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData("{}"), messageId: $"exhausted-{i}",
            properties: new Dictionary<string, object> { ["DlqReplayCount"] = 3 })).ToArray();
        var eligible = ServiceBusModelFactory.ServiceBusReceivedMessage(body: new BinaryData("{}"), messageId: "eligible");
        var receiver = new Mock<ServiceBusReceiver>();
        receiver.SetupSequence(r => r.ReceiveMessagesAsync(It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(exhausted)
            .ReturnsAsync(new[] { eligible });
        var sender = new Mock<ServiceBusSender>();
        _serviceBusClient.Setup(c => c.CreateReceiver(Queues.PlayerIpResolved, It.IsAny<ServiceBusReceiverOptions>()))
            .Returns(receiver.Object);
        _serviceBusClient.Setup(c => c.CreateSender(Queues.PlayerIpResolved)).Returns(sender.Object);

        var count = await _sut.ReplayDeadLetterMessages(Queues.PlayerIpResolved, 20);

        Assert.Equal(1, count);
        sender.Verify(s => s.SendMessageAsync(It.Is<ServiceBusMessage>(m => m.MessageId == "eligible"), It.IsAny<CancellationToken>()), Times.Once);
        receiver.Verify(r => r.CompleteMessageAsync(It.Is<ServiceBusReceivedMessage>(m => m.MessageId.StartsWith("exhausted-")), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PlaytimeReplay_ResendsExhaustedMessageBeforeCompletingIt()
    {
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData("{}"),
            messageId: "playtime-1",
            properties: new Dictionary<string, object> { ["DlqReplayCount"] = 3 });
        var receiver = new Mock<ServiceBusReceiver>();
        receiver.Setup(r => r.ReceiveMessagesAsync(It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { message });
        var sender = new Mock<ServiceBusSender>();
        sender.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceBusException("Send failed", ServiceBusFailureReason.ServiceTimeout));
        _serviceBusClient.Setup(c => c.CreateReceiver(Queues.PlaytimeObservation, It.IsAny<ServiceBusReceiverOptions>()))
            .Returns(receiver.Object);
        _serviceBusClient.Setup(c => c.CreateSender(Queues.PlaytimeObservation)).Returns(sender.Object);

        await Assert.ThrowsAsync<ServiceBusException>(() => _sut.ReplayDeadLetterMessages(Queues.PlaytimeObservation, 1));

        sender.Verify(s => s.SendMessageAsync(It.Is<ServiceBusMessage>(m =>
            m.MessageId == "playtime-1" && (int)m.ApplicationProperties["DlqReplayCount"] == 4), It.IsAny<CancellationToken>()), Times.Once);
        receiver.Verify(r => r.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PlaytimeReplay_PreservesObservationBodyAndMessageId()
    {
        const string body = /*lang=json,strict*/ """{"playtime":{"observationId":"22222222-2222-2222-2222-222222222222"}}""";
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData(body),
            messageId: "playtime-1",
            properties: new Dictionary<string, object> { ["DlqReplayCount"] = 3 });
        var receiver = new Mock<ServiceBusReceiver>();
        receiver.Setup(r => r.ReceiveMessagesAsync(It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { message });
        ServiceBusMessage? sent = null;
        var sender = new Mock<ServiceBusSender>();
        sender.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, CancellationToken>((replay, _) => sent = replay)
            .Returns(Task.CompletedTask);
        receiver.Setup(r => r.CompleteMessageAsync(message, It.IsAny<CancellationToken>()))
            .Callback<ServiceBusReceivedMessage, CancellationToken>((_, _) => Assert.NotNull(sent))
            .Returns(Task.CompletedTask);
        _serviceBusClient.Setup(c => c.CreateReceiver(Queues.PlaytimeObservation, It.IsAny<ServiceBusReceiverOptions>()))
            .Returns(receiver.Object);
        _serviceBusClient.Setup(c => c.CreateSender(Queues.PlaytimeObservation)).Returns(sender.Object);

        var count = await _sut.ReplayDeadLetterMessages(Queues.PlaytimeObservation, 1);

        Assert.Equal(1, count);
        Assert.NotNull(sent);
        Assert.Equal(body, sent.Body.ToString());
        Assert.Equal("playtime-1", sent.MessageId);
        Assert.Equal(4, sent.ApplicationProperties["DlqReplayCount"]);
        receiver.Verify(r => r.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }
}
