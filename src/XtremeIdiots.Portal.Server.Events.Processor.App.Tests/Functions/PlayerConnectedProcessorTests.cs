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
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;
using XtremeIdiots.Portal.Server.Events.Processor.App.Services;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Cod4xPlugin;

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
    private readonly Mock<IVersionedGameServerConfigurationsApi> _versionedServerConfigs = new();
    private readonly Mock<IGameServerConfigurationsApi> _serverConfigsApi = new();
    private readonly Mock<IVersionedGlobalConfigurationsApi> _versionedGlobalConfigs = new();
    private readonly Mock<IGlobalConfigurationsApi> _globalConfigsApi = new();
    private readonly Mock<IProtectedNameService> _protectedNameService = new();
    private readonly Mock<IWelcomeMessageOrchestrator> _welcomeMessageOrchestrator = new();
    private readonly IMemoryCache _cache;
    private readonly Mock<IAuditLogger> _auditLogger = new();
    private readonly Mock<FunctionContext> _functionContext = new();
    private readonly PlayerConnectedProcessor _sut;

    private static readonly Guid TestServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TestPlayerId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public PlayerConnectedProcessorTests()
    {
        _versionedPlayers.Setup(x => x.V1).Returns(_playersApi.Object);
        _repoClient.Setup(x => x.Players).Returns(_versionedPlayers.Object);

        _versionedServerConfigs.Setup(x => x.V1).Returns(_serverConfigsApi.Object);
        _repoClient.Setup(x => x.GameServerConfigurations).Returns(_versionedServerConfigs.Object);

        _versionedGlobalConfigs.Setup(x => x.V1).Returns(_globalConfigsApi.Object);
        _repoClient.Setup(x => x.GlobalConfigurations).Returns(_versionedGlobalConfigs.Object);

        _versionedGeoLookup.Setup(x => x.V1_1).Returns(_geoLookupApi.Object);
        _geoClient.Setup(x => x.GeoLookup).Returns(_versionedGeoLookup.Object);

        _protectedNameService
            .Setup(x => x.CheckAsync(It.IsAny<ProtectedNameContext>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _serverConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>([])));

        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>([])));

        _cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

        _sut = new PlayerConnectedProcessor(
            _logger.Object,
            _repoClient.Object,
            _geoClient.Object,
            _protectedNameService.Object,
            _welcomeMessageOrchestrator.Object,
            _cache,
            _auditLogger.Object);
    }

    private static PlayerConnectedEvent CreateValidEvent(
        string? gameType = null,
        string? playerGuid = null,
        string? username = null,
        string? steamId = null,
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
            SteamId = steamId,
            IpAddress = ipAddress ?? "192.168.1.1",
            SlotId = 0
        };

    [Fact]
    public async Task ValidNewPlayer_CreatesPlayer()
    {
        var evt = CreateValidEvent(steamId: "76561198000000001");
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, "abc123guid"))
            .ReturnsAsync(NotFoundResult());

        _playersApi.Setup(x => x.CreatePlayer(It.IsAny<CreatePlayerDto>()))
            .ReturnsAsync(SuccessResult());

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _playersApi.Verify(x => x.CreatePlayer(It.Is<CreatePlayerDto>(dto =>
            dto.Username == "TestPlayer" &&
            dto.Guid == "abc123guid" &&
            dto.SteamId == "76561198000000001" &&
            dto.IpAddress == "192.168.1.1")), Times.Once);
    }

    [Fact]
    public async Task ExistingPlayer_UpdatesPlayer()
    {
        var evt = CreateValidEvent(steamId: "76561198000000002");
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, "abc123guid"))
            .ReturnsAsync(SuccessResult());

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        _playersApi.Setup(x => x.RecordPlayerSession(It.IsAny<RecordPlayerSessionDto>()))
            .ReturnsAsync(SuccessResult());

        _playersApi.Setup(x => x.UpdatePlayerIpAddress(It.IsAny<UpdatePlayerIpAddressDto>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _playersApi.Verify(x => x.RecordPlayerSession(It.Is<RecordPlayerSessionDto>(dto =>
            dto.PlayerId == TestPlayerId &&
            dto.Username == "TestPlayer" &&
            dto.SteamId == "76561198000000002")), Times.Once);

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
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
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
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        _playersApi.Setup(x => x.RecordPlayerSession(It.IsAny<RecordPlayerSessionDto>()))
            .ReturnsAsync(SuccessResult());

        _playersApi.Setup(x => x.UpdatePlayerIpAddress(It.IsAny<UpdatePlayerIpAddressDto>()))
            .ReturnsAsync(SuccessResult());

        var geoData = Newtonsoft.Json.JsonConvert.DeserializeObject<IpIntelligenceDto>(
            Newtonsoft.Json.JsonConvert.SerializeObject(new { Latitude = 51.5074, Longitude = -0.1278, CountryCode = "GB" }));

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
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
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
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        _playersApi.Setup(x => x.RecordPlayerSession(It.IsAny<RecordPlayerSessionDto>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _geoLookupApi.Verify(x => x.GetIpIntelligence(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _playersApi.Verify(x => x.UpdatePlayerIpAddress(It.IsAny<UpdatePlayerIpAddressDto>()), Times.Never);
    }

    [Fact]
    public async Task ValidNewPlayer_CoD4x_InvokesProtectedNameCheck()
    {
        var evt = CreateValidEvent(gameType: "CallOfDuty4x");
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4x, "abc123guid"))
            .ReturnsAsync(NotFoundResult());

        _playersApi.Setup(x => x.CreatePlayer(It.IsAny<CreatePlayerDto>()))
            .ReturnsAsync(SuccessResult());

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4x, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _protectedNameService.Verify(x => x.CheckAsync(
            It.Is<ProtectedNameContext>(ctx =>
                ctx.ServerId == evt.ServerId &&
                string.Equals(ctx.GameType, "CallOfDuty4x", StringComparison.OrdinalIgnoreCase) &&
                ctx.PlayerId == TestPlayerId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ValidNewPlayer_NonCoD4x_SkipsProtectedNameCheck()
    {
        var evt = CreateValidEvent(gameType: "CallOfDuty4");
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, "abc123guid"))
            .ReturnsAsync(NotFoundResult());

        _playersApi.Setup(x => x.CreatePlayer(It.IsAny<CreatePlayerDto>()))
            .ReturnsAsync(SuccessResult());

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _protectedNameService.Verify(x => x.CheckAsync(It.IsAny<ProtectedNameContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExistingPlayer_CoD4x_InvokesProtectedNameCheck()
    {
        var evt = CreateValidEvent(gameType: "CallOfDuty4x");
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4x, "abc123guid"))
            .ReturnsAsync(SuccessResult());

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4x, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        _playersApi.Setup(x => x.RecordPlayerSession(It.IsAny<RecordPlayerSessionDto>()))
            .ReturnsAsync(SuccessResult());

        _playersApi.Setup(x => x.UpdatePlayerIpAddress(It.IsAny<UpdatePlayerIpAddressDto>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _protectedNameService.Verify(x => x.CheckAsync(
            It.Is<ProtectedNameContext>(ctx =>
                ctx.ServerId == evt.ServerId &&
                string.Equals(ctx.GameType, "CallOfDuty4x", StringComparison.OrdinalIgnoreCase) &&
                ctx.PlayerId == TestPlayerId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExistingPlayer_NonCoD4x_SkipsProtectedNameCheck()
    {
        var evt = CreateValidEvent(gameType: "CallOfDuty4");
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, "abc123guid"))
            .ReturnsAsync(SuccessResult());

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        _playersApi.Setup(x => x.RecordPlayerSession(It.IsAny<RecordPlayerSessionDto>()))
            .ReturnsAsync(SuccessResult());

        _playersApi.Setup(x => x.UpdatePlayerIpAddress(It.IsAny<UpdatePlayerIpAddressDto>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _protectedNameService.Verify(x => x.CheckAsync(It.IsAny<ProtectedNameContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExistingPlayer_CoD4xPluginEnabled_SkipsWelcomeOrchestration()
    {
        var evt = CreateValidEvent(gameType: "CallOfDuty4x");
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4x, "abc123guid"))
            .ReturnsAsync(SuccessResult());

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4x, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        _playersApi.Setup(x => x.RecordPlayerSession(It.IsAny<RecordPlayerSessionDto>()))
            .ReturnsAsync(SuccessResult());

        _playersApi.Setup(x => x.UpdatePlayerIpAddress(It.IsAny<UpdatePlayerIpAddressDto>()))
            .ReturnsAsync(SuccessResult());

        _serverConfigsApi
            .Setup(x => x.GetConfigurations(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>([
                CreateConfigurationDto(
                    Cod4xPluginSettingsConstants.Namespace,
                                        /*lang=json,strict*/ """
                                        {
                                            "schemaVersion": 1,
                                            "enabled": true
                                        }
                                        """)
            ])));

        _welcomeMessageOrchestrator
            .Setup(x => x.ProcessAsync(It.IsAny<PlayerConnectedEvent>(), It.IsAny<GameType>(), It.IsAny<string[]>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _welcomeMessageOrchestrator.Verify(
            x => x.ProcessAsync(It.IsAny<PlayerConnectedEvent>(), It.IsAny<GameType>(), It.IsAny<string[]>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExistingPlayer_CoD4xPluginDisabled_ExecutesWelcomeOrchestration()
    {
        var evt = CreateValidEvent(gameType: "CallOfDuty4x");
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4x, "abc123guid"))
            .ReturnsAsync(SuccessResult());

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4x, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        _playersApi.Setup(x => x.RecordPlayerSession(It.IsAny<RecordPlayerSessionDto>()))
            .ReturnsAsync(SuccessResult());

        _playersApi.Setup(x => x.UpdatePlayerIpAddress(It.IsAny<UpdatePlayerIpAddressDto>()))
            .ReturnsAsync(SuccessResult());

        _serverConfigsApi
            .Setup(x => x.GetConfigurations(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>([
                CreateConfigurationDto(
                    Cod4xPluginSettingsConstants.Namespace,
                                        /*lang=json,strict*/ """
                                        {
                                            "schemaVersion": 1,
                                            "enabled": false
                                        }
                                        """)
            ])));

        _welcomeMessageOrchestrator
            .Setup(x => x.ProcessAsync(It.IsAny<PlayerConnectedEvent>(), It.IsAny<GameType>(), It.IsAny<string[]>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _welcomeMessageOrchestrator.Verify(
            x => x.ProcessAsync(
                It.Is<PlayerConnectedEvent>(e => e.ServerId == evt.ServerId &&
                                             string.Equals(e.PlayerGuid, evt.PlayerGuid, StringComparison.Ordinal)),
                GameType.CallOfDuty4x,
                It.IsAny<string[]>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static ConfigurationDto CreateConfigurationDto(string ns, string configurationJson)
    {
        var dto = new ConfigurationDto();
        SetConfigProperty(dto, nameof(ConfigurationDto.Namespace), ns);
        SetConfigProperty(dto, nameof(ConfigurationDto.Configuration), configurationJson);
        return dto;
    }

    private static void SetConfigProperty(ConfigurationDto dto, string propertyName, object? value) =>
        typeof(ConfigurationDto).GetProperty(propertyName)!.SetValue(dto, value);
}

