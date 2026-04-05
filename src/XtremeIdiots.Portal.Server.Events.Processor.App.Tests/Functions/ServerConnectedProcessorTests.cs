using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using Moq;

using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.GameServers;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

using static XtremeIdiots.Portal.Server.Events.Processor.App.Tests.ServiceBusTestHelpers;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Functions;

public class ServerConnectedProcessorTests
{
    private readonly Mock<ILogger<ServerConnectedProcessor>> _logger = new();
    private readonly Mock<IRepositoryApiClient> _repoClient = new();
    private readonly Mock<IVersionedGameServersEventsApi> _versionedEvents = new();
    private readonly Mock<IGameServersEventsApi> _eventsApi = new();
    private readonly TelemetryClient _telemetry;
    private readonly Mock<FunctionContext> _functionContext = new();
    private readonly ServerConnectedProcessor _sut;

    private static readonly Guid TestServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public ServerConnectedProcessorTests()
    {
        _versionedEvents.Setup(x => x.V1).Returns(_eventsApi.Object);
        _repoClient.Setup(x => x.GameServersEvents).Returns(_versionedEvents.Object);

        _telemetry = new TelemetryClient(new Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration
        {
            TelemetryChannel = new Mock<ITelemetryChannel>().Object
        });

        _sut = new ServerConnectedProcessor(_logger.Object, _repoClient.Object, _telemetry);
    }

    private static ServerConnectedEvent CreateValidEvent(
        Guid? serverId = null,
        string? gameType = null) => new()
    {
        EventGeneratedUtc = DateTime.UtcNow.AddSeconds(-10),
        EventPublishedUtc = DateTime.UtcNow.AddSeconds(-5),
        ServerId = serverId ?? TestServerId,
        GameType = gameType ?? "CallOfDuty4",
        SequenceId = 1
    };

    [Fact]
    public async Task ValidEvent_CreatesServerEvent()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        _eventsApi.Setup(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessServerConnected(message, _functionContext.Object);

        _eventsApi.Verify(x => x.CreateGameServerEvent(It.Is<CreateGameServerEventDto>(dto =>
            dto.GameServerId == TestServerId &&
            dto.EventType == "OnServerConnected"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EmptyServerId_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(serverId: Guid.Empty);
        var message = CreateMessage(evt);

        await _sut.ProcessServerConnected(message, _functionContext.Object);

        _eventsApi.Verify(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EmptyGameType_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(gameType: "");
        var message = CreateMessage(evt);

        await _sut.ProcessServerConnected(message, _functionContext.Object);

        _eventsApi.Verify(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MalformedJson_LogsWarningAndReturns()
    {
        var message = CreateMessage("{{bad json");

        await _sut.ProcessServerConnected(message, _functionContext.Object);

        _eventsApi.Verify(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
