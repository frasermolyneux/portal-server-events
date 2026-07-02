using MX.Observability.ApplicationInsights.Auditing;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.GameServers;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

using static XtremeIdiots.Portal.Server.Events.Processor.App.Tests.ServiceBusTestHelpers;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Functions;

public class BanSyncFailedProcessorTests
{
    private readonly Mock<ILogger<BanSyncFailedProcessor>> _logger = new();
    private readonly Mock<IRepositoryApiClient> _repoClient = new();
    private readonly Mock<IVersionedGameServersEventsApi> _versionedEvents = new();
    private readonly Mock<IGameServersEventsApi> _eventsApi = new();
    private readonly Mock<IAuditLogger> _auditLogger = new();
    private readonly Mock<FunctionContext> _functionContext = new();
    private readonly BanSyncFailedProcessor _sut;

    private static readonly Guid TestServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public BanSyncFailedProcessorTests()
    {
        _versionedEvents.Setup(x => x.V1).Returns(_eventsApi.Object);
        _repoClient.Setup(x => x.GameServersEvents).Returns(_versionedEvents.Object);

        _sut = new BanSyncFailedProcessor(_logger.Object, _repoClient.Object, _auditLogger.Object);
    }

    private static BanSyncFailedEvent CreateValidEvent(
        Guid? serverId = null,
        string? gameType = null,
        string? operation = null,
        string? failureReason = null,
        string? source = null,
        string? playerGuid = null,
        string? playerName = null) => new()
        {
            EventGeneratedUtc = DateTime.UtcNow.AddSeconds(-10),
            EventPublishedUtc = DateTime.UtcNow.AddSeconds(-5),
            ServerId = serverId ?? TestServerId,
            GameType = gameType ?? "CallOfDuty4x",
            SequenceId = 1,
            Operation = operation ?? "ReconcileBan",
            FailureReason = failureReason ?? "RCON response did not confirm ban",
            Source = source ?? "Agent",
            PlayerGuid = playerGuid,
            PlayerName = playerName,
            CorrelationId = "trace-123"
        };

    [Fact]
    public async Task ValidEvent_CreatesServerEvent()
    {
        var evt = CreateValidEvent(playerGuid: "abc123guid", playerName: "TestPlayer");
        var message = CreateMessage(evt);

        _eventsApi.Setup(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessBanSyncFailed(message, _functionContext.Object);

        _eventsApi.Verify(x => x.CreateGameServerEvent(It.Is<CreateGameServerEventDto>(dto =>
            dto.GameServerId == TestServerId &&
            dto.EventType == "BanSyncFailed" &&
            dto.EventData.Contains("ReconcileBan", StringComparison.Ordinal)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ValidEventWithoutPlayer_CreatesServerEvent()
    {
        var evt = CreateValidEvent(playerGuid: null, playerName: null);
        var message = CreateMessage(evt);

        _eventsApi.Setup(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessBanSyncFailed(message, _functionContext.Object);

        _eventsApi.Verify(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EmptyServerId_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(serverId: Guid.Empty);
        var message = CreateMessage(evt);

        await _sut.ProcessBanSyncFailed(message, _functionContext.Object);

        _eventsApi.Verify(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InvalidGameType_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(gameType: "NotARealGame");
        var message = CreateMessage(evt);

        await _sut.ProcessBanSyncFailed(message, _functionContext.Object);

        _eventsApi.Verify(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MissingRequiredFields_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(operation: "", failureReason: "", source: "");
        var message = CreateMessage(evt);

        await _sut.ProcessBanSyncFailed(message, _functionContext.Object);

        _eventsApi.Verify(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MalformedJson_LogsWarningAndReturns()
    {
        var message = CreateMessage("{{bad json");

        await _sut.ProcessBanSyncFailed(message, _functionContext.Object);

        _eventsApi.Verify(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateGameServerEventFailure_ThrowsToAllowRetry()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        _eventsApi.Setup(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(System.Net.HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.ProcessBanSyncFailed(message, _functionContext.Object));
    }
}