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
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;
using XtremeIdiots.Portal.Server.Events.Processor.App.Services;

using static XtremeIdiots.Portal.Server.Events.Processor.App.Tests.ServiceBusTestHelpers;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Functions;

public class PlayerConnectedProcessorTests
{
    private readonly Mock<ILogger<PlayerConnectedProcessor>> _logger = new();
    private readonly Mock<IRepositoryApiClient> _repoClient = new();
    private readonly Mock<IGeoLocationApiClient> _geoClient = new();
    private readonly Mock<IVersionedGeoLookupApi> _versionedGeoLookup = new();
    private readonly Mock<MX.GeoLocation.Abstractions.Interfaces.V1_1.IGeoLookupApi> _geoLookupApi = new();
    private readonly Mock<IVersionedPlayersApi> _versionedPlayers = new();
    private readonly Mock<IPlayersApi> _playersApi = new();
    private readonly Mock<IProtectedNameService> _protectedNameService = new();
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

        _versionedGeoLookup.Setup(x => x.V1_1).Returns(_geoLookupApi.Object);
        _geoClient.Setup(x => x.GeoLookup).Returns(_versionedGeoLookup.Object);

        _cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        _telemetry = new TelemetryClient(new Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration
        {
            TelemetryChannel = new Mock<ITelemetryChannel>().Object
        });

        _sut = new PlayerConnectedProcessor(_logger.Object, _repoClient.Object, _geoClient.Object, _protectedNameService.Object, _cache, _telemetry);
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

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.None))
            .ReturnsAsync(SuccessResult(playerDto));

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

        _playersApi.Setup(x => x.RecordPlayerSession(It.IsAny<RecordPlayerSessionDto>()))
            .ReturnsAsync(SuccessResult());

        _playersApi.Setup(x => x.UpdatePlayerIpAddress(It.IsAny<UpdatePlayerIpAddressDto>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _playersApi.Verify(x => x.RecordPlayerSession(It.Is<RecordPlayerSessionDto>(dto =>
            dto.PlayerId == TestPlayerId &&
            dto.Username == "TestPlayer")), Times.Once);

        _playersApi.Verify(x => x.UpdatePlayerIpAddress(It.Is<UpdatePlayerIpAddressDto>(dto =>
            dto.PlayerId == TestPlayerId &&
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

        _playersApi.Setup(x => x.RecordPlayerSession(It.IsAny<RecordPlayerSessionDto>()))
            .ReturnsAsync(SuccessResult());

        _playersApi.Setup(x => x.UpdatePlayerIpAddress(It.IsAny<UpdatePlayerIpAddressDto>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _playersApi.Verify(x => x.RecordPlayerSession(It.IsAny<RecordPlayerSessionDto>()), Times.Once);
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

    [Fact]
    public async Task ProcessPlayerConnected_WithIpAddress_EnrichesWithGeoLocation()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, "abc123guid"))
            .ReturnsAsync(SuccessResult());

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.None))
            .ReturnsAsync(SuccessResult(playerDto));

        _playersApi.Setup(x => x.RecordPlayerSession(It.IsAny<RecordPlayerSessionDto>()))
            .ReturnsAsync(SuccessResult());

        _playersApi.Setup(x => x.UpdatePlayerIpAddress(It.IsAny<UpdatePlayerIpAddressDto>()))
            .ReturnsAsync(SuccessResult());

        var geoData = Newtonsoft.Json.JsonConvert.DeserializeObject<IpIntelligenceDto>(
            Newtonsoft.Json.JsonConvert.SerializeObject(new { Latitude = 51.5074, Longitude = -0.1278, CountryCode = "GB" }))!;

        _geoLookupApi.Setup(x => x.GetIpIntelligence("192.168.1.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<IpIntelligenceDto>(System.Net.HttpStatusCode.OK, new ApiResponse<IpIntelligenceDto>(geoData)));

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _geoLookupApi.Verify(x => x.GetIpIntelligence("192.168.1.1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessPlayerConnected_GeoLookupFails_StillProcessesPlayer()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, "abc123guid"))
            .ReturnsAsync(SuccessResult());

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.None))
            .ReturnsAsync(SuccessResult(playerDto));

        _playersApi.Setup(x => x.RecordPlayerSession(It.IsAny<RecordPlayerSessionDto>()))
            .ReturnsAsync(SuccessResult());

        _playersApi.Setup(x => x.UpdatePlayerIpAddress(It.IsAny<UpdatePlayerIpAddressDto>()))
            .ReturnsAsync(SuccessResult());

        _geoLookupApi.Setup(x => x.GetIpIntelligence("192.168.1.1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("GeoLocation API unavailable"));

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        // Player session was still recorded despite geo lookup failure
        _playersApi.Verify(x => x.RecordPlayerSession(It.Is<RecordPlayerSessionDto>(dto =>
            dto.PlayerId == TestPlayerId &&
            dto.Username == "TestPlayer")), Times.Once);
    }

    [Fact]
    public async Task ProcessPlayerConnected_EmptyIpAddress_SkipsGeoLookup()
    {
        var evt = CreateValidEvent(ipAddress: "");
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, "abc123guid"))
            .ReturnsAsync(SuccessResult());

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.None))
            .ReturnsAsync(SuccessResult(playerDto));

        _playersApi.Setup(x => x.RecordPlayerSession(It.IsAny<RecordPlayerSessionDto>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _geoLookupApi.Verify(x => x.GetIpIntelligence(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _playersApi.Verify(x => x.UpdatePlayerIpAddress(It.IsAny<UpdatePlayerIpAddressDto>()), Times.Never);
    }
}

