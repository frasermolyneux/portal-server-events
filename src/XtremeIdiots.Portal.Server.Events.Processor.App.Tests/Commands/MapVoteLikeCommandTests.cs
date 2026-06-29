using System.Net;

using Microsoft.Extensions.Logging;

using Moq;

using MX.Api.Abstractions;
using MX.Observability.ApplicationInsights.Auditing;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Maps;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class MapVoteLikeCommandTests
{
    private readonly Mock<IRepositoryApiClient> _repoClient = new();
    private readonly Mock<IServersApiClient> _serversClient = new();
    private readonly Mock<IVersionedCod4RconApi> _versionedCod4Rcon = new();
    private readonly Mock<ICod4RconApi> _cod4RconApi = new();
    private readonly Mock<Repository.Api.Client.V1.IVersionedMapsApi> _versionedMaps = new();
    private readonly Mock<Repository.Abstractions.Interfaces.V1.IMapsApi> _mapsApi = new();
    private readonly Mock<IVersionedGlobalConfigurationsApi> _versionedGlobalConfigs = new();
    private readonly Mock<IGlobalConfigurationsApi> _globalConfigsApi = new();
    private readonly Mock<IVersionedGameServerConfigurationsApi> _versionedServerConfigs = new();
    private readonly Mock<IGameServerConfigurationsApi> _serverConfigsApi = new();
    private readonly Mock<ICommandSafetyService> _commandSafetyService = new();
    private readonly Mock<IRconResponseService> _rconService = new();
    private readonly Mock<IAuditLogger> _auditLogger = new();
    private readonly Mock<ILogger<MapVoteLikeCommand>> _logger = new();
    private readonly MapVoteLikeCommand _sut;

    private static readonly Guid TestServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TestPlayerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TestMapId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public MapVoteLikeCommandTests()
    {
        _versionedCod4Rcon.Setup(x => x.V1).Returns(_cod4RconApi.Object);
        _serversClient.Setup(x => x.Cod4Rcon).Returns(_versionedCod4Rcon.Object);

        _versionedMaps.Setup(x => x.V1).Returns(_mapsApi.Object);
        _repoClient.Setup(x => x.Maps).Returns(_versionedMaps.Object);
        _versionedGlobalConfigs.Setup(x => x.V1).Returns(_globalConfigsApi.Object);
        _repoClient.Setup(x => x.GlobalConfigurations).Returns(_versionedGlobalConfigs.Object);
        _versionedServerConfigs.Setup(x => x.V1).Returns(_serverConfigsApi.Object);
        _repoClient.Setup(x => x.GameServerConfigurations).Returns(_versionedServerConfigs.Object);

        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<ConfigurationDto>>(
                HttpStatusCode.OK,
                new ApiResponse<CollectionModel<ConfigurationDto>>(new CollectionModel<ConfigurationDto>(new[]
                {
                    CreateConfigurationDto("agent", /*lang=json,strict*/ "{\"agentName\":\"^5[GlobalBot]^7\"}")
                }))));

        _serverConfigsApi
            .Setup(x => x.GetConfigurations(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<ConfigurationDto>>(
                HttpStatusCode.OK,
                new ApiResponse<CollectionModel<ConfigurationDto>>(new CollectionModel<ConfigurationDto>(new[]
                {
                    CreateConfigurationDto("agent", /*lang=json,strict*/ "{\"agentName\":\"^2[ServerBot]^7\"}")
                }))));

        _commandSafetyService
            .Setup(x => x.ValidateMapTargetAsync(TestServerId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MapValidationResult(true));

        _sut = new MapVoteLikeCommand(_repoClient.Object, _serversClient.Object, _commandSafetyService.Object, _rconService.Object, _auditLogger.Object, _logger.Object);
    }

    private static CommandContext CreateContext(Guid? playerId = null, string message = "!like") => new()
    {
        ServerId = TestServerId,
        GameType = "CallOfDuty4",
        PlayerGuid = "abc123",
        Username = "TestPlayer",
        SlotId = 3,
        Message = message,
        EventGeneratedUtc = DateTime.UtcNow,
        EventPublishedUtc = DateTime.UtcNow,
        SequenceId = 1,
        PlayerId = playerId ?? TestPlayerId
    };

    [Fact]
    public async Task ExecuteAsync_WithValidPlayer_CreatesVoteAndSendsRcon()
    {
        _cod4RconApi.Setup(x => x.GetCurrentMap(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RconCurrentMapDto>(HttpStatusCode.OK,
                new ApiResponse<RconCurrentMapDto>(new RconCurrentMapDto("mp_crash"))));

        _mapsApi.Setup(x => x.GetMap(GameType.CallOfDuty4, "mp_crash", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<MapDto>(HttpStatusCode.OK,
                new ApiResponse<MapDto>(CreateMapDto())));

        _mapsApi.Setup(x => x.UpsertMapVote(It.IsAny<UpsertMapVoteDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));

        _rconService.Setup(x => x.TrySayAsync(TestServerId, It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _sut.ExecuteAsync(CreateContext());

        Assert.True(result.Handled);
        Assert.True(result.Success);

        _mapsApi.Verify(x => x.UpsertMapVote(
            It.Is<UpsertMapVoteDto>(d => d.MapId == TestMapId && d.PlayerId == TestPlayerId && d.Like),
            It.IsAny<CancellationToken>()), Times.Once);

        _rconService.Verify(x => x.TrySayAsync(
            TestServerId,
            It.Is<string>(s => s.StartsWith("^2[ServerBot]^7 ") && s.Contains("LIKE")),
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPlayerNotFound_ReturnsFailed()
    {
        var context = CreateContext(playerId: null) with { PlayerId = null };

        var result = await _sut.ExecuteAsync(context);

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Equal("Player not found", result.ResponseMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WhenMapNotFound_ReturnsFailed()
    {
        _cod4RconApi.Setup(x => x.GetCurrentMap(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RconCurrentMapDto>(HttpStatusCode.OK,
                new ApiResponse<RconCurrentMapDto>(new RconCurrentMapDto("mp_crash"))));

        _mapsApi.Setup(x => x.GetMap(GameType.CallOfDuty4, "mp_crash", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<MapDto>(HttpStatusCode.NotFound));

        var result = await _sut.ExecuteAsync(CreateContext());

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Equal("Map not found", result.ResponseMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WhenMapLookupReturnsNullResult_ReturnsFailed()
    {
        _cod4RconApi.Setup(x => x.GetCurrentMap(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RconCurrentMapDto>(HttpStatusCode.OK,
                new ApiResponse<RconCurrentMapDto>(new RconCurrentMapDto("mp_crash"))));

        _mapsApi.Setup(x => x.GetMap(GameType.CallOfDuty4, "mp_crash", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApiResult<MapDto>)null!);

        var result = await _sut.ExecuteAsync(CreateContext());

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Equal("Map not found", result.ResponseMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WhenLiveMapValidationReturnsMissingCurrentMap_StillCreatesVote()
    {
        _cod4RconApi.Setup(x => x.GetCurrentMap(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RconCurrentMapDto>(HttpStatusCode.OK,
                new ApiResponse<RconCurrentMapDto>(new RconCurrentMapDto("mp_crash"))));

        _mapsApi.Setup(x => x.GetMap(GameType.CallOfDuty4, "mp_crash", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<MapDto>(HttpStatusCode.OK,
                new ApiResponse<MapDto>(CreateMapDto())));

        _mapsApi.Setup(x => x.UpsertMapVote(It.IsAny<UpsertMapVoteDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));

        _commandSafetyService
            .Setup(x => x.ValidateMapTargetAsync(TestServerId, "mp_crash", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MapValidationResult(false, "Map was not found in the live server map list.", IsLiveMapListMismatch: true));

        var result = await _sut.ExecuteAsync(CreateContext());

        Assert.True(result.Handled);
        Assert.True(result.Success);
        _mapsApi.Verify(x => x.UpsertMapVote(
            It.Is<UpsertMapVoteDto>(d => d.MapId == TestMapId && d.PlayerId == TestPlayerId && d.Like),
            It.IsAny<CancellationToken>()), Times.Once);
        _rconService.Verify(x => x.TrySayAsync(
            TestServerId,
            It.Is<string>(s => s.Contains("LIKE")),
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenStale_VoteCreatedButNoRcon()
    {
        _cod4RconApi.Setup(x => x.GetCurrentMap(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RconCurrentMapDto>(HttpStatusCode.OK,
                new ApiResponse<RconCurrentMapDto>(new RconCurrentMapDto("mp_crash"))));

        _mapsApi.Setup(x => x.GetMap(GameType.CallOfDuty4, "mp_crash", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<MapDto>(HttpStatusCode.OK,
                new ApiResponse<MapDto>(CreateMapDto())));

        _mapsApi.Setup(x => x.UpsertMapVote(It.IsAny<UpsertMapVoteDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));

        _rconService.Setup(x => x.TrySayAsync(TestServerId, It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _sut.ExecuteAsync(CreateContext());

        Assert.True(result.Success);
        _mapsApi.Verify(x => x.UpsertMapVote(It.IsAny<UpsertMapVoteDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_UsesGlobalPrefix_WhenServerOverrideMissing()
    {
        _cod4RconApi.Setup(x => x.GetCurrentMap(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RconCurrentMapDto>(HttpStatusCode.OK,
                new ApiResponse<RconCurrentMapDto>(new RconCurrentMapDto("mp_crash"))));

        _mapsApi.Setup(x => x.GetMap(GameType.CallOfDuty4, "mp_crash", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<MapDto>(HttpStatusCode.OK,
                new ApiResponse<MapDto>(CreateMapDto())));

        _mapsApi.Setup(x => x.UpsertMapVote(It.IsAny<UpsertMapVoteDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));

        _serverConfigsApi
            .Setup(x => x.GetConfigurations(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<ConfigurationDto>>(
                HttpStatusCode.OK,
                new ApiResponse<CollectionModel<ConfigurationDto>>(new CollectionModel<ConfigurationDto>(Array.Empty<ConfigurationDto>()))));

        _rconService.Setup(x => x.TrySayAsync(TestServerId, It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _sut.ExecuteAsync(CreateContext());

        Assert.True(result.Success);
        _rconService.Verify(x => x.TrySayAsync(
            TestServerId,
            It.Is<string>(s => s.StartsWith("^5[GlobalBot]^7 ") && s.Contains("LIKE")),
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_UsesDefaultPrefix_WhenGlobalAndServerOverridesMissing()
    {
        _cod4RconApi.Setup(x => x.GetCurrentMap(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RconCurrentMapDto>(HttpStatusCode.OK,
                new ApiResponse<RconCurrentMapDto>(new RconCurrentMapDto("mp_crash"))));

        _mapsApi.Setup(x => x.GetMap(GameType.CallOfDuty4, "mp_crash", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<MapDto>(HttpStatusCode.OK,
                new ApiResponse<MapDto>(CreateMapDto())));

        _mapsApi.Setup(x => x.UpsertMapVote(It.IsAny<UpsertMapVoteDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));

        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<ConfigurationDto>>(
                HttpStatusCode.OK,
                new ApiResponse<CollectionModel<ConfigurationDto>>(new CollectionModel<ConfigurationDto>(Array.Empty<ConfigurationDto>()))));

        _serverConfigsApi
            .Setup(x => x.GetConfigurations(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<ConfigurationDto>>(
                HttpStatusCode.OK,
                new ApiResponse<CollectionModel<ConfigurationDto>>(new CollectionModel<ConfigurationDto>(Array.Empty<ConfigurationDto>()))));

        _rconService.Setup(x => x.TrySayAsync(TestServerId, It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _sut.ExecuteAsync(CreateContext());

        Assert.True(result.Success);
        _rconService.Verify(x => x.TrySayAsync(
            TestServerId,
            It.Is<string>(s => s.StartsWith("^4[^1>XI< BOT^4]^7 ") && s.Contains("LIKE")),
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static MapDto CreateMapDto(Guid? mapId = null)
    {
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            MapId = mapId ?? TestMapId,
            GameType = GameType.CallOfDuty4,
            MapName = "mp_crash"
        });
        return Newtonsoft.Json.JsonConvert.DeserializeObject<MapDto>(json)!;
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
