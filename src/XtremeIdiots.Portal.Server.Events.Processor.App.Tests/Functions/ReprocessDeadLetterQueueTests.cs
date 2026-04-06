using System.Net;

using Azure.Messaging.ServiceBus;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Moq;

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

    [Fact]
    public async Task Run_DryRun_PeeksOnly()
    {
        var dlqMessage = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData("{\"test\":true}"),
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
            body: new BinaryData("{\"test\":true}"),
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
}
