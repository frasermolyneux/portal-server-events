using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Moq;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

using static XtremeIdiots.Portal.Server.Events.Processor.App.Tests.ServiceBusTestHelpers;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Functions;

public class PlayerConnectedProcessorTests
{
    private readonly Mock<ILogger<PlayerConnectedProcessor>> _logger = new();
    private readonly Mock<IRepositoryApiClient> _repoClient = new();
    private readonly Mock<IVersionedPlayersApi> _versionedPlayers = new();
    private readonly Mock<IPlayersApi> _playersApi = new();
    private readonly IMemoryCache _cache;
    private readonly TelemetryClient _telemetry;
    private readonly Mock<FunctionContext> _functionContext = new();
    private readonly PlayerConnectedProcessor _sut;

    private static readonly Guid TestServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TestPlayerId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public PlayerConnectedProcessorTests()
    {
        _versionedPlayers.Setup(x => x.V1).Returns(_playersApi.Object);
        _repoClient.Setup(x => x.Players).Returns(_versionedPlayers.Object);

        _cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        _telemetry = new TelemetryClient(new Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration
        {
            TelemetryChannel = new Mock<ITelemetryChannel>().Object
        });

        _sut = new PlayerConnectedProcessor(_logger.Object, _repoClient.Object, _cache, _telemetry);
    }

    private static PlayerConnectedEvent CreateValidEvent(
        string? gameType = null,
        string? playerGuid = null,
        string? username = null,
        string? ipAddress = null,
        Guid? serverId = null,
        DateTime? eventGeneratedUtc = null) => new()
    {
        EventGeneratedUtc = eventGeneratedUtc ?? DateTime.UtcNow.AddSeconds(-10),
        EventPublishedUtc = DateTime.UtcNow.AddSeconds(-5),
        ServerId = serverId ?? TestServerId,
        GameType = gameType ?? "CallOfDuty4",
        SequenceId = 1,
        PlayerGuid = playerGuid ?? "abc123guid",
        Username = username ?? "TestPlayer",
        IpAddress = ipAddress ?? "192.168.1.1",
        SlotId = 0
    };

    [Fact]
    public async Task ValidNewPlayer_CreatesPlayer()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, "abc123guid"))
            .ReturnsAsync(NotFoundResult());

        _playersApi.Setup(x => x.CreatePlayer(It.IsAny<CreatePlayerDto>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _playersApi.Verify(x => x.CreatePlayer(It.Is<CreatePlayerDto>(dto =>
            dto.Username == "TestPlayer" &&
            dto.Guid == "abc123guid" &&
            dto.IpAddress == "192.168.1.1")), Times.Once);
    }

    [Fact]
    public async Task ExistingPlayer_UpdatesPlayer()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, "abc123guid"))
            .ReturnsAsync(SuccessResult());

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.None))
            .ReturnsAsync(SuccessResult(playerDto));

        _playersApi.Setup(x => x.UpdatePlayer(It.IsAny<EditPlayerDto>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _playersApi.Verify(x => x.UpdatePlayer(It.Is<EditPlayerDto>(dto =>
            dto.PlayerId == TestPlayerId &&
            dto.Username == "TestPlayer" &&
            dto.IpAddress == "192.168.1.1")), Times.Once);
    }

    [Fact]
    public async Task ConflictOnCreate_FallsThroughToUpdate()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, "abc123guid"))
            .ReturnsAsync(NotFoundResult());

        _playersApi.Setup(x => x.CreatePlayer(It.IsAny<CreatePlayerDto>()))
            .ReturnsAsync(ConflictResult());

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.None))
            .ReturnsAsync(SuccessResult(playerDto));

        _playersApi.Setup(x => x.UpdatePlayer(It.IsAny<EditPlayerDto>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _playersApi.Verify(x => x.UpdatePlayer(It.IsAny<EditPlayerDto>()), Times.Once);
    }

    [Fact]
    public async Task MissingUsername_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(username: "");
        var message = CreateMessage(evt);

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _playersApi.Verify(x => x.HeadPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task InvalidGameType_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(gameType: "NotARealGame");
        var message = CreateMessage(evt);

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _playersApi.Verify(x => x.HeadPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task StaleEvent_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(eventGeneratedUtc: DateTime.UtcNow.AddMinutes(-45));
        var message = CreateMessage(evt);

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _playersApi.Verify(x => x.HeadPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task EmptyServerId_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(serverId: Guid.Empty);
        var message = CreateMessage(evt);

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _playersApi.Verify(x => x.HeadPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task MalformedJson_LogsWarningAndReturns()
    {
        var message = CreateMessage("not valid json {{{");

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _playersApi.Verify(x => x.HeadPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>()), Times.Never);
    }
}
