using System.Net;

using Microsoft.Extensions.Logging;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Maps;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class MapVoteDislikeCommandTests
{
    private readonly Mock<IRepositoryApiClient> _repoClient = new();
    private readonly Mock<IServersApiClient> _serversClient = new();
    private readonly Mock<IVersionedRconApi> _versionedRcon = new();
    private readonly Mock<IRconApi> _rconApi = new();
    private readonly Mock<XtremeIdiots.Portal.Repository.Api.Client.V1.IVersionedMapsApi> _versionedMaps = new();
    private readonly Mock<XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1.IMapsApi> _mapsApi = new();
    private readonly Mock<IRconResponseService> _rconService = new();
    private readonly Mock<ILogger<MapVoteDislikeCommand>> _logger = new();
    private readonly MapVoteDislikeCommand _sut;

    private static readonly Guid TestServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TestPlayerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TestMapId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public MapVoteDislikeCommandTests()
    {
        _versionedRcon.Setup(x => x.V1).Returns(_rconApi.Object);
        _serversClient.Setup(x => x.Rcon).Returns(_versionedRcon.Object);

        _versionedMaps.Setup(x => x.V1).Returns(_mapsApi.Object);
        _repoClient.Setup(x => x.Maps).Returns(_versionedMaps.Object);

        _sut = new MapVoteDislikeCommand(_repoClient.Object, _serversClient.Object, _rconService.Object, _logger.Object);
    }

    private static CommandContext CreateContext(Guid? playerId = null, string message = "!dislike") => new()
    {
        ServerId = TestServerId,
        GameType = "CallOfDuty4",
        PlayerGuid = "abc123",
        Username = "TestPlayer",
        Message = message,
        EventGeneratedUtc = DateTime.UtcNow,
        EventPublishedUtc = DateTime.UtcNow,
        PlayerId = playerId ?? TestPlayerId
    };

    [Fact]
    public async Task ExecuteAsync_WithValidPlayer_CreatesVoteAndSendsRcon()
    {
        _rconApi.Setup(x => x.GetCurrentMap(TestServerId))
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
            It.Is<UpsertMapVoteDto>(d => d.MapId == TestMapId && d.PlayerId == TestPlayerId && !d.Like),
            It.IsAny<CancellationToken>()), Times.Once);

        _rconService.Verify(x => x.TrySayAsync(
            TestServerId,
            It.Is<string>(s => s.Contains("DISLIKE")),
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
        _rconApi.Setup(x => x.GetCurrentMap(TestServerId))
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
    public async Task ExecuteAsync_WhenStale_VoteCreatedButNoRcon()
    {
        _rconApi.Setup(x => x.GetCurrentMap(TestServerId))
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
}
