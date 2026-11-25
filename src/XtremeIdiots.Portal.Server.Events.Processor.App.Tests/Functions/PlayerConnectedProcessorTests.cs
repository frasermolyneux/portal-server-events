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
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;
using XtremeIdiots.Portal.Server.Events.Processor.App.Services;
using XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;
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
    private readonly Mock<IRulesTriggerService> _rulesTriggerService = new();
    private readonly PreparedRulesTrigger _prepared = new(null!, null!);
    private readonly Mock<IVpnDetectedTagService> _vpnDetectedTagService = new();
    private readonly Mock<IVersionedPlayersApi> _versionedPlayers = new();
    private readonly Mock<IPlayersApi> _playersApi = new();
    private readonly Mock<IVersionedGameServerConfigurationsApi> _versionedServerConfigs = new();
    private readonly Mock<IGameServerConfigurationsApi> _serverConfigsApi = new();
    private readonly Mock<IVersionedGlobalConfigurationsApi> _versionedGlobalConfigs = new();
    private readonly Mock<IGlobalConfigurationsApi> _globalConfigsApi = new();
    private readonly Mock<IProtectedNameService> _protectedNameService = new();
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
        _playersApi
            .Setup(x => x.RecordPlayerIpObservation(
                It.IsAny<Guid>(),
                It.IsAny<RecordPlayerIpObservationDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        _versionedServerConfigs.Setup(x => x.V1).Returns(_serverConfigsApi.Object);
        _repoClient.Setup(x => x.GameServerConfigurations).Returns(_versionedServerConfigs.Object);

        _versionedGlobalConfigs.Setup(x => x.V1).Returns(_globalConfigsApi.Object);
        _repoClient.Setup(x => x.GlobalConfigurations).Returns(_versionedGlobalConfigs.Object);

        _versionedGeoLookup.Setup(x => x.V1_1).Returns(_geoLookupApi.Object);
        _geoClient.Setup(x => x.GeoLookup).Returns(_versionedGeoLookup.Object);

        _protectedNameService
            .Setup(x => x.CheckAsync(It.IsAny<ProtectedNameContext>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _rulesTriggerService
            .Setup(x => x.PrepareAsync(It.IsAny<PlayerConnectedEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_prepared);
        _rulesTriggerService
            .Setup(x => x.ProcessAsync(
                It.IsAny<PreparedRulesTrigger?>(),
                It.IsAny<IpIntelligenceDto?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RulesProcessingSummary());
        _vpnDetectedTagService
            .Setup(x => x.AddIfDetectedAsync(It.IsAny<Guid>(), It.IsAny<IpIntelligenceDto>(), It.IsAny<CancellationToken>()))
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
            _rulesTriggerService.Object,
            _vpnDetectedTagService.Object,
            _protectedNameService.Object,
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
        DateTime? eventGeneratedUtc = null,
        bool protectionEvaluatedByPlugin = false,
        Guid? connectionId = null) => new()
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
            SlotId = 0,
            ProtectionEvaluatedByPlugin = protectionEvaluatedByPlugin,
            ConnectionId = connectionId
        };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConnectedEvent_WithoutIp_PreparesIdentityAndProcessesUnknownIntelligence(bool newPlayer)
    {
        var evt = CreateValidEvent(ipAddress: "", connectionId: Guid.Parse("33333333-3333-3333-3333-333333333333"));
        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, "abc123guid"))
            .ReturnsAsync(newPlayer ? NotFoundResult() : SuccessResult());
        _playersApi.Setup(x => x.CreatePlayer(It.IsAny<CreatePlayerDto>())).ReturnsAsync(SuccessResult());
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(CreatePlayerDto(TestPlayerId)));
        _playersApi.Setup(x => x.RecordPlayerSession(It.IsAny<RecordPlayerSessionDto>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessPlayerConnected(CreateMessage(evt), _functionContext.Object);

        _rulesTriggerService.Verify(x => x.PrepareAsync(
            It.Is<PlayerConnectedEvent>(source =>
                source.ServerId == TestServerId &&
                source.GameType == evt.GameType &&
                source.PlayerGuid == evt.PlayerGuid &&
                source.SlotId == evt.SlotId &&
                source.ConnectionId == evt.ConnectionId &&
                source.EventGeneratedUtc == evt.EventGeneratedUtc &&
                string.IsNullOrEmpty(source.IpAddress)),
            It.IsAny<CancellationToken>()), Times.Once);
        _rulesTriggerService.Verify(x => x.ProcessAsync(_prepared, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnpreparedRulesTrigger_DoesNotSkipConnectedPlayerProcessing()
    {
        var evt = CreateValidEvent(protectionEvaluatedByPlugin: true);
        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, "abc123guid"))
            .ReturnsAsync(SuccessResult());
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(CreatePlayerDto(TestPlayerId)));
        _playersApi.Setup(x => x.RecordPlayerSession(It.IsAny<RecordPlayerSessionDto>())).ReturnsAsync(SuccessResult());
        var intelligence = Newtonsoft.Json.JsonConvert.DeserializeObject<IpIntelligenceDto>(
            Newtonsoft.Json.JsonConvert.SerializeObject(new { ProxyCheck = new { IsVpn = true } }))!;
        _geoLookupApi.Setup(x => x.GetIpIntelligence("192.168.1.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<IpIntelligenceDto>(
                System.Net.HttpStatusCode.OK, new ApiResponse<IpIntelligenceDto>(intelligence)));
        _rulesTriggerService.Setup(x => x.PrepareAsync(It.IsAny<PlayerConnectedEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PreparedRulesTrigger?)null);

        await _sut.ProcessPlayerConnected(CreateMessage(evt), _functionContext.Object);

        _playersApi.Verify(x => x.RecordPlayerSession(It.IsAny<RecordPlayerSessionDto>()), Times.Once);
        _playersApi.Verify(x => x.RecordPlayerIpObservation(
            TestPlayerId, It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()), Times.Once);
        _vpnDetectedTagService.Verify(x => x.AddIfDetectedAsync(TestPlayerId, intelligence, It.IsAny<CancellationToken>()), Times.Once);
        _rulesTriggerService.Verify(x => x.ProcessAsync(null, intelligence, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PluginEvent_PreparesBeforeWritesAndProcessesAfterEnrichment(bool newPlayer)
    {
        var evt = CreateValidEvent(protectionEvaluatedByPlugin: true);
        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, "abc123guid"))
            .ReturnsAsync(newPlayer ? NotFoundResult() : SuccessResult());
        _playersApi.Setup(x => x.CreatePlayer(It.IsAny<CreatePlayerDto>())).ReturnsAsync(SuccessResult());
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(CreatePlayerDto(TestPlayerId)));
        _playersApi.Setup(x => x.RecordPlayerSession(It.IsAny<RecordPlayerSessionDto>())).ReturnsAsync(SuccessResult());
        _playersApi.Setup(x => x.RecordPlayerIpObservation(It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(SuccessResult());
        var intelligence = Newtonsoft.Json.JsonConvert.DeserializeObject<IpIntelligenceDto>(
            Newtonsoft.Json.JsonConvert.SerializeObject(new { ProxyCheck = new { IsVpn = true } }))!;
        _geoLookupApi.Setup(x => x.GetIpIntelligence("192.168.1.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<IpIntelligenceDto>(
                System.Net.HttpStatusCode.OK, new ApiResponse<IpIntelligenceDto>(intelligence)));
        _rulesTriggerService.Setup(x => x.PrepareAsync(It.IsAny<PlayerConnectedEvent>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                Assert.Empty(_playersApi.Invocations);
                Assert.Empty(_vpnDetectedTagService.Invocations);
            })
            .ReturnsAsync(_prepared);
        _rulesTriggerService.Setup(x => x.ProcessAsync(_prepared, intelligence, It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                _playersApi.Verify(x => x.RecordPlayerIpObservation(
                    TestPlayerId, It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()), Times.Once);
                _vpnDetectedTagService.Verify(x => x.AddIfDetectedAsync(TestPlayerId, intelligence, It.IsAny<CancellationToken>()), Times.Once);
            })
            .ReturnsAsync(new RulesProcessingSummary());

        await _sut.ProcessPlayerConnected(CreateMessage(evt), _functionContext.Object);

        _playersApi.Verify(x => x.CreatePlayer(It.IsAny<CreatePlayerDto>()), newPlayer ? Times.Once : Times.Never);
        _playersApi.Verify(x => x.RecordPlayerSession(It.IsAny<RecordPlayerSessionDto>()), newPlayer ? Times.Never : Times.Once);
        _rulesTriggerService.Verify(x => x.PrepareAsync(It.IsAny<PlayerConnectedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        _vpnDetectedTagService.Verify(x => x.AddIfDetectedAsync(TestPlayerId, intelligence, It.IsAny<CancellationToken>()), Times.Once);
        _rulesTriggerService.Verify(x => x.ProcessAsync(_prepared, intelligence, It.IsAny<CancellationToken>()), Times.Once);
    }

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
            dto.IpAddress == null)), Times.Once);
        _playersApi.Verify(x => x.RecordPlayerIpObservation(
            TestPlayerId,
            It.Is<RecordPlayerIpObservationDto>(dto =>
                dto.MessageId == message.MessageId &&
                dto.IpAddress == "192.168.1.1" &&
                dto.ObservedAtUtc.HasValue &&
                dto.ObservedAtUtc.Value.UtcDateTime == DateTime.SpecifyKind(evt.EventGeneratedUtc, DateTimeKind.Utc)),
            It.IsAny<CancellationToken>()), Times.Once);
        _rulesTriggerService.Verify(x => x.ProcessAsync(_prepared, It.IsAny<IpIntelligenceDto?>(), It.IsAny<CancellationToken>()), Times.Once);
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

        _playersApi.Setup(x => x.RecordPlayerIpObservation(It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _playersApi.Verify(x => x.RecordPlayerSession(It.Is<RecordPlayerSessionDto>(dto =>
            dto.PlayerId == TestPlayerId &&
            dto.Username == "TestPlayer" &&
            dto.SteamId == "76561198000000002")), Times.Once);

        _playersApi.Verify(x => x.RecordPlayerIpObservation(
            TestPlayerId,
            It.Is<RecordPlayerIpObservationDto>(dto =>
                dto.MessageId == message.MessageId &&
                dto.IpAddress == "192.168.1.1" &&
                dto.ObservedAtUtc.HasValue &&
                dto.ObservedAtUtc.Value.UtcDateTime == DateTime.SpecifyKind(evt.EventGeneratedUtc, DateTimeKind.Utc)),
            It.IsAny<CancellationToken>()), Times.Once);
        _rulesTriggerService.Verify(x => x.ProcessAsync(_prepared, It.IsAny<IpIntelligenceDto?>(), It.IsAny<CancellationToken>()), Times.Once);
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

        _playersApi.Setup(x => x.RecordPlayerIpObservation(It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()))
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
        _rulesTriggerService.VerifyNoOtherCalls();
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

        _playersApi.Setup(x => x.RecordPlayerIpObservation(It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        var geoData = Newtonsoft.Json.JsonConvert.DeserializeObject<IpIntelligenceDto>(
            Newtonsoft.Json.JsonConvert.SerializeObject(new { Latitude = 51.5074, Longitude = -0.1278, CountryCode = "GB" }))!;

        _geoLookupApi.Setup(x => x.GetIpIntelligence("192.168.1.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<IpIntelligenceDto>(System.Net.HttpStatusCode.OK, new ApiResponse<IpIntelligenceDto>(geoData)));

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _geoLookupApi.Verify(x => x.GetIpIntelligence("192.168.1.1", It.IsAny<CancellationToken>()), Times.Once);
        _rulesTriggerService.Verify(x => x.ProcessAsync(
            _prepared,
            geoData,
            It.IsAny<CancellationToken>()), Times.Once);
        _vpnDetectedTagService.Verify(x => x.AddIfDetectedAsync(TestPlayerId, geoData, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessPlayerConnected_ProtectionResultDoesNotStartAnotherWelcomePath(bool newPlayer)
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);
        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, "abc123guid"))
            .ReturnsAsync(newPlayer ? NotFoundResult() : SuccessResult());
        _playersApi.Setup(x => x.CreatePlayer(It.IsAny<CreatePlayerDto>())).ReturnsAsync(SuccessResult());
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(CreatePlayerDto(TestPlayerId)));
        _playersApi.Setup(x => x.RecordPlayerSession(It.IsAny<RecordPlayerSessionDto>()))
            .ReturnsAsync(SuccessResult());
        _playersApi.Setup(x => x.RecordPlayerIpObservation(It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());
        var geoData = Newtonsoft.Json.JsonConvert.DeserializeObject<IpIntelligenceDto>(
            Newtonsoft.Json.JsonConvert.SerializeObject(new { ProxyCheck = new { IsVpn = true } }))!;
        _geoLookupApi.Setup(x => x.GetIpIntelligence("192.168.1.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<IpIntelligenceDto>(System.Net.HttpStatusCode.OK, new ApiResponse<IpIntelligenceDto>(geoData)));
        _rulesTriggerService
            .Setup(x => x.ProcessAsync(_prepared, geoData, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RulesProcessingSummary(SuppressWelcome: true));

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _rulesTriggerService.Verify(x => x.ProcessAsync(_prepared, geoData, It.IsAny<CancellationToken>()), Times.Once);
        _globalConfigsApi.Verify(x => x.GetConfigurations(It.IsAny<CancellationToken>()), Times.Never);
        _serverConfigsApi.Verify(x => x.GetConfigurations(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
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

        _playersApi.Setup(x => x.RecordPlayerIpObservation(It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        _geoLookupApi.Setup(x => x.GetIpIntelligence("192.168.1.1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("GeoLocation API unavailable"));

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        // Player session was still recorded despite geo lookup failure
        _playersApi.Verify(x => x.RecordPlayerSession(It.Is<RecordPlayerSessionDto>(dto =>
            dto.PlayerId == TestPlayerId &&
            dto.Username == "TestPlayer")), Times.Once);
        _rulesTriggerService.Verify(x => x.ProcessAsync(_prepared, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task RulesFailure_PropagatesWithCancellationToken(bool duringPreparation, bool cancellation)
    {
        var ct = new CancellationToken(canceled: cancellation);
        _functionContext.SetupGet(x => x.CancellationToken).Returns(ct);
        Exception failure = cancellation
            ? new OperationCanceledException(ct)
            : new HttpRequestException("Rules service unavailable");
        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, "abc123guid"))
            .ReturnsAsync(SuccessResult());
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(CreatePlayerDto(TestPlayerId)));
        _playersApi.Setup(x => x.RecordPlayerSession(It.IsAny<RecordPlayerSessionDto>()))
            .ReturnsAsync(SuccessResult());
        if (duringPreparation)
        {
            _rulesTriggerService.Setup(x => x.PrepareAsync(It.IsAny<PlayerConnectedEvent>(), ct))
                .ThrowsAsync(failure);
        }
        else
        {
            _rulesTriggerService.Setup(x => x.ProcessAsync(_prepared, null, ct))
                .ThrowsAsync(failure);
        }

        var actual = await Assert.ThrowsAnyAsync<Exception>(
            () => _sut.ProcessPlayerConnected(CreateMessage(CreateValidEvent(ipAddress: "")), _functionContext.Object));

        Assert.Same(failure, actual);
        _playersApi.Verify(x => x.RecordPlayerSession(It.IsAny<RecordPlayerSessionDto>()),
            duringPreparation ? Times.Never : Times.Once);
    }

    [Fact]
    public async Task NoPreparedTrigger_StillPersistsAndDelegatesProcessing()
    {
        _rulesTriggerService.Setup(x => x.PrepareAsync(It.IsAny<PlayerConnectedEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PreparedRulesTrigger?)null);
        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, "abc123guid"))
            .ReturnsAsync(SuccessResult());
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(CreatePlayerDto(TestPlayerId)));
        _playersApi.Setup(x => x.RecordPlayerSession(It.IsAny<RecordPlayerSessionDto>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessPlayerConnected(CreateMessage(CreateValidEvent(ipAddress: "")), _functionContext.Object);

        _playersApi.Verify(x => x.RecordPlayerSession(It.IsAny<RecordPlayerSessionDto>()), Times.Once);
        _rulesTriggerService.Verify(x => x.ProcessAsync(null, null, It.IsAny<CancellationToken>()), Times.Once);
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
        _playersApi.Verify(x => x.RecordPlayerIpObservation(It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()), Times.Never);
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

        _playersApi.Setup(x => x.RecordPlayerIpObservation(It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()))
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

        _playersApi.Setup(x => x.RecordPlayerIpObservation(It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _protectedNameService.Verify(x => x.CheckAsync(It.IsAny<ProtectedNameContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExistingPlayer_CoD4xPluginEnabled_DelegatesOnlyToSharedRules()
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

        _playersApi.Setup(x => x.RecordPlayerIpObservation(It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()))
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

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _rulesTriggerService.Verify(x => x.ProcessAsync(_prepared, null, It.IsAny<CancellationToken>()), Times.Once);
        _serverConfigsApi.Verify(x => x.GetConfigurations(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExistingPlayer_CoD4xPluginDisabled_DelegatesOnlyToSharedRules()
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

        _playersApi.Setup(x => x.RecordPlayerIpObservation(It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()))
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

        await _sut.ProcessPlayerConnected(message, _functionContext.Object);

        _rulesTriggerService.Verify(x => x.ProcessAsync(_prepared, null, It.IsAny<CancellationToken>()), Times.Once);
        _serverConfigsApi.Verify(x => x.GetConfigurations(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
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

