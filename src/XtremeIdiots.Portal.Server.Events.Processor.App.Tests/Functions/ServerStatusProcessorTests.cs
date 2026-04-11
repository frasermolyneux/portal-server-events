using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Moq;

using MX.Api.Abstractions;
using MX.GeoLocation.Abstractions.Models.V1_1;
using MX.GeoLocation.Api.Client.V1;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.GameServers;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.LiveStatus;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

using static XtremeIdiots.Portal.Server.Events.Processor.App.Tests.ServiceBusTestHelpers;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Functions;

public class ServerStatusProcessorTests
{
    private readonly Mock<ILogger<ServerStatusProcessor>> _logger = new();
    private readonly Mock<IRepositoryApiClient> _repoClient = new();
    private readonly Mock<IGeoLocationApiClient> _geoClient = new();
    private readonly Mock<IVersionedGeoLookupApi> _versionedGeoLookup = new();
    private readonly Mock<MX.GeoLocation.Abstractions.Interfaces.V1_1.IGeoLookupApi> _geoLookupApi = new();
    private readonly Mock<IVersionedPlayersApi> _versionedPlayers = new();
    private readonly Mock<IPlayersApi> _playersApi = new();
    private readonly Mock<IVersionedLiveStatusApi> _versionedLiveStatus = new();
    private readonly Mock<ILiveStatusApi> _liveStatusApi = new();
    private readonly Mock<IVersionedGameServersStatsApi> _versionedGameServersStats = new();
    private readonly Mock<IGameServersStatsApi> _gameServersStatsApi = new();
    private readonly IMemoryCache _cache;
    private readonly TelemetryClient _telemetry;
    private readonly Mock<FunctionContext> _functionContext = new();
    private readonly ServerStatusProcessor _sut;

    private static readonly Guid TestServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TestPlayerId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public ServerStatusProcessorTests()
    {
        _versionedPlayers.Setup(x => x.V1).Returns(_playersApi.Object);
        _repoClient.Setup(x => x.Players).Returns(_versionedPlayers.Object);

        _versionedLiveStatus.Setup(x => x.V1).Returns(_liveStatusApi.Object);
        _repoClient.Setup(x => x.LiveStatus).Returns(_versionedLiveStatus.Object);

        _versionedGameServersStats.Setup(x => x.V1).Returns(_gameServersStatsApi.Object);
        _repoClient.Setup(x => x.GameServersStats).Returns(_versionedGameServersStats.Object);

        _versionedGeoLookup.Setup(x => x.V1_1).Returns(_geoLookupApi.Object);
        _geoClient.Setup(x => x.GeoLookup).Returns(_versionedGeoLookup.Object);

        _cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        _telemetry = new TelemetryClient(new Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration
        {
            TelemetryChannel = new Mock<ITelemetryChannel>().Object
        });

        SetupDefaultApiSuccessResponses();

        _sut = new ServerStatusProcessor(_logger.Object, _repoClient.Object, _geoClient.Object, _cache, _telemetry);
    }

    private void SetupDefaultApiSuccessResponses()
    {
        _liveStatusApi.Setup(x => x.SetGameServerLiveStatus(It.IsAny<Guid>(), It.IsAny<SetGameServerLiveStatusDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        _gameServersStatsApi.Setup(x => x.CreateGameServerStats(It.IsAny<List<CreateGameServerStatDto>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());
    }

    private static ServerStatusEvent CreateValidEvent(
        string? gameType = null,
        string? mapName = null,
        int playerCount = 12,
        Guid? serverId = null,
        DateTime? eventGeneratedUtc = null,
        IReadOnlyList<ConnectedPlayer>? players = null) => new()
    {
        EventGeneratedUtc = eventGeneratedUtc ?? DateTime.UtcNow.AddSeconds(-10),
        EventPublishedUtc = DateTime.UtcNow.AddSeconds(-5),
        ServerId = serverId ?? TestServerId,
        GameType = gameType ?? "CallOfDuty4",
        SequenceId = 1,
        MapName = mapName ?? "mp_crash",
        GameName = "Call of Duty 4",
        PlayerCount = playerCount,
        Players = players ?? new List<ConnectedPlayer>
        {
            new()
            {
                PlayerGuid = "abc123guid",
                Username = "TestPlayer",
                IpAddress = "192.168.1.1",
                SlotId = 0,
                ConnectedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            },
            new()
            {
                PlayerGuid = "def456guid",
                Username = "Player2",
                IpAddress = "10.0.0.1",
                SlotId = 1,
                ConnectedAtUtc = DateTime.UtcNow.AddMinutes(-3)
            }
        }
    };

    [Fact]
    public async Task ProcessServerStatus_ValidEvent_SetsLiveStatusAndCreatesStats()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        var player1Dto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.None))
            .ReturnsAsync(SuccessResult(player1Dto));

        var player2Id = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var player2Dto = CreatePlayerDto(player2Id);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "def456guid", PlayerEntityOptions.None))
            .ReturnsAsync(SuccessResult(player2Dto));

        await _sut.ProcessServerStatus(message, _functionContext.Object);

        // Verify live status was set with server metadata and players
        _liveStatusApi.Verify(x => x.SetGameServerLiveStatus(
            TestServerId,
            It.Is<SetGameServerLiveStatusDto>(dto =>
                dto.Map == "mp_crash" &&
                dto.CurrentPlayers == 12 &&
                dto.GameType == GameType.CallOfDuty4 &&
                dto.Players.Count == 2 &&
                dto.Players[0].Name == "TestPlayer" &&
                dto.Players[0].PlayerId == TestPlayerId &&
                dto.Players[0].ConnectedAtUtc.HasValue &&
                dto.Players[1].Name == "Player2" &&
                dto.Players[1].PlayerId == player2Id),
            It.IsAny<CancellationToken>()), Times.Once);

        // Verify stats snapshot was created
        _gameServersStatsApi.Verify(x => x.CreateGameServerStats(
            It.Is<List<CreateGameServerStatDto>>(list =>
                list.Count == 1 &&
                list[0].GameServerId == TestServerId &&
                list[0].PlayerCount == 12 &&
                list[0].MapName == "mp_crash"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessServerStatus_StaleEvent_Skipped()
    {
        var evt = CreateValidEvent(eventGeneratedUtc: DateTime.UtcNow.AddMinutes(-5));
        var message = CreateMessage(evt);

        await _sut.ProcessServerStatus(message, _functionContext.Object);

        _liveStatusApi.Verify(x => x.SetGameServerLiveStatus(It.IsAny<Guid>(), It.IsAny<SetGameServerLiveStatusDto>(), It.IsAny<CancellationToken>()), Times.Never);
        _gameServersStatsApi.Verify(x => x.CreateGameServerStats(It.IsAny<List<CreateGameServerStatDto>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessServerStatus_CreatesPlayerCountSnapshot()
    {
        var evt = CreateValidEvent(playerCount: 24, mapName: "mp_backlot", players: new List<ConnectedPlayer>());
        var message = CreateMessage(evt);

        await _sut.ProcessServerStatus(message, _functionContext.Object);

        _gameServersStatsApi.Verify(x => x.CreateGameServerStats(
            It.Is<List<CreateGameServerStatDto>>(list =>
                list.Count == 1 &&
                list[0].GameServerId == TestServerId &&
                list[0].PlayerCount == 24 &&
                list[0].MapName == "mp_backlot"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessServerStatus_GeoEnrichmentFailure_StillSetsLiveStatus()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        // GeoIP lookup throws for all players
        _geoLookupApi.Setup(x => x.GetIpIntelligence(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("GeoLocation API unavailable"));

        // Player lookup succeeds
        _playersApi.Setup(x => x.GetPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>(), PlayerEntityOptions.None))
            .ReturnsAsync(NotFoundResult<PlayerDto>());

        await _sut.ProcessServerStatus(message, _functionContext.Object);

        // Live status should still be set despite GeoIP failures
        _liveStatusApi.Verify(x => x.SetGameServerLiveStatus(
            TestServerId,
            It.Is<SetGameServerLiveStatusDto>(dto =>
                dto.Players.Count == 2 &&
                dto.Players[0].GeoIntelligence == null),
            It.IsAny<CancellationToken>()), Times.Once);

        // Stats snapshot should still be created
        _gameServersStatsApi.Verify(x => x.CreateGameServerStats(
            It.IsAny<List<CreateGameServerStatDto>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessServerStatus_InvalidMessage_LogsWarning()
    {
        var message = CreateMessage("not valid json {{{");

        await _sut.ProcessServerStatus(message, _functionContext.Object);

        _liveStatusApi.Verify(x => x.SetGameServerLiveStatus(It.IsAny<Guid>(), It.IsAny<SetGameServerLiveStatusDto>(), It.IsAny<CancellationToken>()), Times.Never);
        _gameServersStatsApi.Verify(x => x.CreateGameServerStats(It.IsAny<List<CreateGameServerStatDto>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessServerStatus_EmptyServerId_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(serverId: Guid.Empty);
        var message = CreateMessage(evt);

        await _sut.ProcessServerStatus(message, _functionContext.Object);

        _liveStatusApi.Verify(x => x.SetGameServerLiveStatus(It.IsAny<Guid>(), It.IsAny<SetGameServerLiveStatusDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessServerStatus_InvalidGameType_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(gameType: "NotARealGame");
        var message = CreateMessage(evt);

        await _sut.ProcessServerStatus(message, _functionContext.Object);

        _liveStatusApi.Verify(x => x.SetGameServerLiveStatus(It.IsAny<Guid>(), It.IsAny<SetGameServerLiveStatusDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessServerStatus_PlayerIdResolutionMiss_SetsNullPlayerId()
    {
        var evt = CreateValidEvent(players: new List<ConnectedPlayer>
        {
            new()
            {
                PlayerGuid = "unknown-guid",
                Username = "NewPlayer",
                IpAddress = "10.0.0.5",
                SlotId = 3,
                ConnectedAtUtc = DateTime.UtcNow.AddMinutes(-1)
            }
        });
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "unknown-guid", PlayerEntityOptions.None))
            .ReturnsAsync(NotFoundResult<PlayerDto>());

        await _sut.ProcessServerStatus(message, _functionContext.Object);

        _liveStatusApi.Verify(x => x.SetGameServerLiveStatus(
            TestServerId,
            It.Is<SetGameServerLiveStatusDto>(dto =>
                dto.Players.Count == 1 &&
                dto.Players[0].PlayerId == null &&
                dto.Players[0].Name == "NewPlayer"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessServerStatus_WithGeoData_EnrichesLivePlayers()
    {
        var evt = CreateValidEvent(players: new List<ConnectedPlayer>
        {
            new()
            {
                PlayerGuid = "abc123guid",
                Username = "TestPlayer",
                IpAddress = "203.0.113.50",
                SlotId = 0,
                ConnectedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            }
        });
        var message = CreateMessage(evt);

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.None))
            .ReturnsAsync(SuccessResult(playerDto));

        var geoData = Newtonsoft.Json.JsonConvert.DeserializeObject<IpIntelligenceDto>(
            Newtonsoft.Json.JsonConvert.SerializeObject(new { Latitude = 51.5074, Longitude = -0.1278, CountryCode = "GB" }))!;

        _geoLookupApi.Setup(x => x.GetIpIntelligence("203.0.113.50", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<IpIntelligenceDto>(System.Net.HttpStatusCode.OK, new ApiResponse<IpIntelligenceDto>(geoData)));

        await _sut.ProcessServerStatus(message, _functionContext.Object);

        _liveStatusApi.Verify(x => x.SetGameServerLiveStatus(
            TestServerId,
            It.Is<SetGameServerLiveStatusDto>(dto =>
                dto.Players.Count == 1 &&
                dto.Players[0].GeoIntelligence != null &&
                dto.Players[0].GeoIntelligence.Latitude == 51.5074 &&
                dto.Players[0].GeoIntelligence.Longitude == -0.1278 &&
                dto.Players[0].GeoIntelligence.CountryCode == "GB" &&
                dto.Players[0].PlayerId == TestPlayerId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessServerStatus_SetLiveStatusFails_StillCreatesStats()
    {
        var evt = CreateValidEvent(players: new List<ConnectedPlayer>());
        var message = CreateMessage(evt);

        _liveStatusApi.Setup(x => x.SetGameServerLiveStatus(It.IsAny<Guid>(), It.IsAny<SetGameServerLiveStatusDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Repository API unavailable"));

        await _sut.ProcessServerStatus(message, _functionContext.Object);

        _gameServersStatsApi.Verify(x => x.CreateGameServerStats(
            It.IsAny<List<CreateGameServerStatDto>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessServerStatus_CreateStatsFails_DoesNotThrow()
    {
        var evt = CreateValidEvent(players: new List<ConnectedPlayer>());
        var message = CreateMessage(evt);

        _gameServersStatsApi.Setup(x => x.CreateGameServerStats(It.IsAny<List<CreateGameServerStatDto>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Repository API unavailable"));

        await _sut.ProcessServerStatus(message, _functionContext.Object);

        _liveStatusApi.Verify(x => x.SetGameServerLiveStatus(
            TestServerId,
            It.IsAny<SetGameServerLiveStatusDto>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}

