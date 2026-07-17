using MX.Observability.ApplicationInsights.Auditing;

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
using XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

using static XtremeIdiots.Portal.Server.Events.Processor.App.Tests.ServiceBusTestHelpers;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Functions;

public class PlayerIpResolvedProcessorTests
{
    private readonly Mock<ILogger<PlayerIpResolvedProcessor>> _logger = new();
    private readonly Mock<IRepositoryApiClient> _repoClient = new();
    private readonly Mock<IVersionedPlayersApi> _versionedPlayers = new();
    private readonly Mock<IPlayersApi> _playersApi = new();
    private readonly Mock<IGeoLocationApiClient> _geoLocationApiClient = new();
    private readonly Mock<IVersionedGeoLookupApi> _versionedGeoLookupApi = new();
    private readonly Mock<MX.GeoLocation.Abstractions.Interfaces.V1_1.IGeoLookupApi> _geoLookupApi = new();
    private readonly Mock<IVpnProtectionService> _vpnProtectionService = new();
    private readonly Mock<IVpnDetectedTagService> _vpnDetectedTagService = new();
    private readonly IMemoryCache _cache;
    private readonly Mock<IAuditLogger> _auditLogger = new();
    private readonly Mock<FunctionContext> _functionContext = new();
    private readonly PlayerIpResolvedProcessor _sut;

    private static readonly Guid TestServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TestPlayerId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public PlayerIpResolvedProcessorTests()
    {
        _versionedPlayers.Setup(x => x.V1).Returns(_playersApi.Object);
        _repoClient.Setup(x => x.Players).Returns(_versionedPlayers.Object);

        _versionedGeoLookupApi.Setup(x => x.V1_1).Returns(_geoLookupApi.Object);
        _geoLocationApiClient.Setup(x => x.GeoLookup).Returns(_versionedGeoLookupApi.Object);
        _geoLookupApi
            .Setup(x => x.GetIpIntelligence(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<IpIntelligenceDto>(System.Net.HttpStatusCode.NotFound));
        _vpnProtectionService
            .Setup(x => x.ProcessAsync(
                It.IsAny<VpnProtectionContext>(),
                It.IsAny<IpIntelligenceDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VpnProtectionProcessingResult());
        _vpnDetectedTagService
            .Setup(x => x.AddIfDetectedAsync(It.IsAny<Guid>(), It.IsAny<IpIntelligenceDto>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

        _sut = new PlayerIpResolvedProcessor(
            _logger.Object,
            _repoClient.Object,
            _geoLocationApiClient.Object,
            _vpnProtectionService.Object,
            _vpnDetectedTagService.Object,
            _cache,
            _auditLogger.Object);
    }

    private static PlayerIpResolvedEvent CreateValidEvent(
        string? gameType = null,
        string? playerGuid = null,
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
            IpAddress = ipAddress ?? "192.168.1.100"
        };

    [Fact]
    public async Task ValidEvent_PersistsIpAddress()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        _playersApi.Setup(x => x.UpdatePlayerIpAddress(It.IsAny<UpdatePlayerIpAddressDto>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessPlayerIpResolved(message, _functionContext.Object);

        _playersApi.Verify(x => x.UpdatePlayerIpAddress(It.Is<UpdatePlayerIpAddressDto>(dto =>
            dto.PlayerId == TestPlayerId &&
            dto.IpAddress == "192.168.1.100")), Times.Once);
    }

    [Fact]
    public async Task PlayerNotFound_SkipsGracefully()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(NotFoundResult<PlayerDto>());

        await _sut.ProcessPlayerIpResolved(message, _functionContext.Object);

        _playersApi.Verify(x => x.UpdatePlayerIpAddress(It.IsAny<UpdatePlayerIpAddressDto>()), Times.Never);
    }

    [Fact]
    public async Task MalformedJson_LogsWarningAndReturns()
    {
        var message = CreateMessage("not valid json {{{");

        await _sut.ProcessPlayerIpResolved(message, _functionContext.Object);

        _playersApi.Verify(x => x.GetPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>(), It.IsAny<PlayerEntityOptions>()), Times.Never);
    }

    [Fact]
    public async Task MissingIpAddress_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(ipAddress: "");
        var message = CreateMessage(evt);

        await _sut.ProcessPlayerIpResolved(message, _functionContext.Object);

        _playersApi.Verify(x => x.GetPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>(), It.IsAny<PlayerEntityOptions>()), Times.Never);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public async Task PlaceholderIpAddress_SkipsPersistence(string ipAddress)
    {
        var evt = CreateValidEvent(ipAddress: ipAddress);
        var message = CreateMessage(evt);

        await _sut.ProcessPlayerIpResolved(message, _functionContext.Object);

        _playersApi.Verify(x => x.GetPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>(), It.IsAny<PlayerEntityOptions>()), Times.Never);
        _playersApi.Verify(x => x.UpdatePlayerIpAddress(It.IsAny<UpdatePlayerIpAddressDto>()), Times.Never);
    }

    [Fact]
    public async Task MissingPlayerGuid_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(playerGuid: "");
        var message = CreateMessage(evt);

        await _sut.ProcessPlayerIpResolved(message, _functionContext.Object);

        _playersApi.Verify(x => x.GetPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>(), It.IsAny<PlayerEntityOptions>()), Times.Never);
    }

    [Fact]
    public async Task InvalidGameType_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(gameType: "NotARealGame");
        var message = CreateMessage(evt);

        await _sut.ProcessPlayerIpResolved(message, _functionContext.Object);

        _playersApi.Verify(x => x.GetPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>(), It.IsAny<PlayerEntityOptions>()), Times.Never);
    }

    [Fact]
    public async Task EmptyServerId_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(serverId: Guid.Empty);
        var message = CreateMessage(evt);

        await _sut.ProcessPlayerIpResolved(message, _functionContext.Object);

        _playersApi.Verify(x => x.GetPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>(), It.IsAny<PlayerEntityOptions>()), Times.Never);
    }

    [Fact]
    public async Task StaleEvent_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(eventGeneratedUtc: DateTime.UtcNow.AddMinutes(-45));
        var message = CreateMessage(evt);

        await _sut.ProcessPlayerIpResolved(message, _functionContext.Object);

        _playersApi.Verify(x => x.GetPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>(), It.IsAny<PlayerEntityOptions>()), Times.Never);
    }

    [Fact]
    public async Task ApiFailure_LogsWarningAndDoesNotThrow()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        _playersApi.Setup(x => x.UpdatePlayerIpAddress(It.IsAny<UpdatePlayerIpAddressDto>()))
            .ThrowsAsync(new HttpRequestException("API unavailable"));

        // Should not throw — error is caught and logged
        await _sut.ProcessPlayerIpResolved(message, _functionContext.Object);
    }

    [Fact]
    public async Task ValidEvent_WithIntelligence_InvokesVpnProtection()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);
        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));
        _playersApi.Setup(x => x.UpdatePlayerIpAddress(It.IsAny<UpdatePlayerIpAddressDto>()))
            .ReturnsAsync(SuccessResult());
        var intelligence = Newtonsoft.Json.JsonConvert.DeserializeObject<IpIntelligenceDto>(
            Newtonsoft.Json.JsonConvert.SerializeObject(new { ProxyCheck = new { IsVpn = true } }))!;
        _geoLookupApi.Setup(x => x.GetIpIntelligence("192.168.1.100", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<IpIntelligenceDto>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<IpIntelligenceDto>(intelligence)));

        await _sut.ProcessPlayerIpResolved(message, _functionContext.Object);

        _vpnProtectionService.Verify(x => x.ProcessAsync(
            It.Is<VpnProtectionContext>(vpnContext =>
                vpnContext.ServerId == TestServerId &&
                vpnContext.GameType == GameType.CallOfDuty4 &&
                vpnContext.PlayerId == TestPlayerId &&
                vpnContext.PlayerGuid == "abc123guid" &&
                vpnContext.SlotId == null),
            intelligence,
            It.IsAny<CancellationToken>()), Times.Once);
        _vpnDetectedTagService.Verify(x => x.AddIfDetectedAsync(TestPlayerId, intelligence, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GeoLookupThrows_FailsOpenAfterPersistingIp()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(CreatePlayerDto(TestPlayerId)));
        _playersApi.Setup(x => x.UpdatePlayerIpAddress(It.IsAny<UpdatePlayerIpAddressDto>()))
            .ReturnsAsync(SuccessResult());
        _geoLookupApi.Setup(x => x.GetIpIntelligence("192.168.1.100", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("GeoLocation unavailable"));

        await _sut.ProcessPlayerIpResolved(message, _functionContext.Object);

        _playersApi.Verify(x => x.UpdatePlayerIpAddress(
            It.Is<UpdatePlayerIpAddressDto>(dto => dto.IpAddress == "192.168.1.100")), Times.Once);
        _vpnProtectionService.Verify(x => x.ProcessAsync(
            It.IsAny<VpnProtectionContext>(),
            It.IsAny<IpIntelligenceDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
