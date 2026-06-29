using Microsoft.Extensions.Logging;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class RconResponseServiceTests
{
    private readonly Mock<IServersApiClient> _serversApiClient = new();
    private readonly Mock<IVersionedCoD4xRconApi> _versionedCoD4xRconApi = new();
    private readonly Mock<ICoD4xRconApi> _coD4xRconApi = new();
    private readonly Mock<ILogger<RconResponseService>> _logger = new();
    private readonly RconResponseService _sut;

    private static readonly Guid TestServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public RconResponseServiceTests()
    {
        _versionedCoD4xRconApi.Setup(x => x.V1).Returns(_coD4xRconApi.Object);
        _serversApiClient.Setup(x => x.CoD4xRcon).Returns(_versionedCoD4xRconApi.Object);

        _sut = new RconResponseService(_serversApiClient.Object, _logger.Object);
    }

    [Fact]
    public async Task TrySayAsync_WhenFresh_SendsMessage()
    {
        _coD4xRconApi.Setup(x => x.ConSay(TestServerId, It.IsAny<CoD4xMessageRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(System.Net.HttpStatusCode.OK, new ApiResponse<string>("ok")));

        var result = await _sut.TrySayAsync(TestServerId, "Hello", DateTime.UtcNow);

        Assert.True(result);
        _coD4xRconApi.Verify(x => x.ConSay(
            TestServerId,
            It.Is<CoD4xMessageRequestDto>(r => r.Message == "Hello"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TrySayAsync_WhenStale_SkipsMessage()
    {
        var staleTime = DateTime.UtcNow.AddSeconds(-10);

        var result = await _sut.TrySayAsync(TestServerId, "Hello", staleTime);

        Assert.False(result);
        _coD4xRconApi.Verify(x => x.ConSay(It.IsAny<Guid>(), It.IsAny<CoD4xMessageRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryTellAsync_WhenFresh_SendsMessage()
    {
        _coD4xRconApi.Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<CoD4xStatusResponseDto>(new CoD4xStatusResponseDto
                {
                    Players = [new CoD4xStatusPlayerDto { Num = 5, PlayerIdentifier = "guid-1", Name = "PlayerOne" }]
                })));

        _coD4xRconApi.Setup(x => x.Tell(TestServerId, It.IsAny<CoD4xTargetMessageRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(System.Net.HttpStatusCode.OK, new ApiResponse<string>("ok")));

        var result = await _sut.TryTellAsync(TestServerId, "guid-1", "Hello", "PlayerOne", DateTime.UtcNow);

        Assert.True(result);
        _coD4xRconApi.Verify(x => x.Tell(
            TestServerId,
            It.Is<CoD4xTargetMessageRequestDto>(r => r.Target == "5" && r.Message == "Hello"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryTellAsync_WhenExpectedNameDoesNotMatch_ReturnsFalse()
    {
        _coD4xRconApi.Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<CoD4xStatusResponseDto>(new CoD4xStatusResponseDto
                {
                    Players = [new CoD4xStatusPlayerDto { Num = 5, PlayerIdentifier = "guid-1", Name = "OtherName" }]
                })));

        var result = await _sut.TryTellAsync(TestServerId, "guid-1", "Hello", "PlayerOne", DateTime.UtcNow);

        Assert.False(result);
        _coD4xRconApi.Verify(x => x.Tell(It.IsAny<Guid>(), It.IsAny<CoD4xTargetMessageRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryTellAsync_WhenExpectedNameHasColorCodes_StillSendsMessage()
    {
        _coD4xRconApi.Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<CoD4xStatusResponseDto>(new CoD4xStatusResponseDto
                {
                    Players = [new CoD4xStatusPlayerDto { Num = 5, PlayerIdentifier = "guid-1", Name = "PlayerOne" }]
                })));

        _coD4xRconApi.Setup(x => x.Tell(TestServerId, It.IsAny<CoD4xTargetMessageRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(System.Net.HttpStatusCode.OK, new ApiResponse<string>("ok")));

        var result = await _sut.TryTellAsync(TestServerId, "guid-1", "Hello", "^1Player^7One", DateTime.UtcNow);

        Assert.True(result);
        _coD4xRconApi.Verify(x => x.Tell(
            TestServerId,
            It.Is<CoD4xTargetMessageRequestDto>(r => r.Target == "5" && r.Message == "Hello"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryTellAsync_WhenNameMissing_UsesRawNameForMatch()
    {
        _coD4xRconApi.Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<CoD4xStatusResponseDto>(new CoD4xStatusResponseDto
                {
                    Players = [new CoD4xStatusPlayerDto { Num = 5, PlayerIdentifier = "guid-1", Name = string.Empty, RawName = "PlayerOne" }]
                })));

        _coD4xRconApi.Setup(x => x.Tell(TestServerId, It.IsAny<CoD4xTargetMessageRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(System.Net.HttpStatusCode.OK, new ApiResponse<string>("ok")));

        var result = await _sut.TryTellAsync(TestServerId, "guid-1", "Hello", "PlayerOne", DateTime.UtcNow);

        Assert.True(result);
        _coD4xRconApi.Verify(x => x.Tell(
            TestServerId,
            It.Is<CoD4xTargetMessageRequestDto>(r => r.Target == "5" && r.Message == "Hello"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryTellAsync_WhenPlayerMissing_ReturnsFalse()
    {
        _coD4xRconApi.Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<CoD4xStatusResponseDto>(new CoD4xStatusResponseDto
                {
                    Players = [new CoD4xStatusPlayerDto { Num = 6, PlayerIdentifier = "other-guid", Name = "Other" }]
                })));

        var result = await _sut.TryTellAsync(TestServerId, "guid-1", "Hello", "PlayerOne", DateTime.UtcNow);

        Assert.False(result);
        _coD4xRconApi.Verify(x => x.Tell(It.IsAny<Guid>(), It.IsAny<CoD4xTargetMessageRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryTellAsync_WithSlot_WhenSlotAndGuidMatch_SendsWithoutFallback()
    {
        _coD4xRconApi.Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<CoD4xStatusResponseDto>(new CoD4xStatusResponseDto
                {
                    Players = [new CoD4xStatusPlayerDto { Num = 5, PlayerIdentifier = "guid-1", Name = "PlayerOne" }]
                })));

        _coD4xRconApi.Setup(x => x.Tell(TestServerId, It.IsAny<CoD4xTargetMessageRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(System.Net.HttpStatusCode.OK, new ApiResponse<string>("ok")));

        var result = await _sut.TryTellAsync(TestServerId, "guid-1", 5, "Hello", "PlayerOne", DateTime.UtcNow);

        Assert.True(result);
        _coD4xRconApi.Verify(x => x.Status(TestServerId, It.IsAny<CancellationToken>()), Times.Once);
        _coD4xRconApi.Verify(x => x.Tell(
            TestServerId,
            It.Is<CoD4xTargetMessageRequestDto>(r => r.Target == "5" && r.Message == "Hello"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryTellAsync_WithSlot_WhenSlotLookupMismatch_FallsBackToGuidLookup()
    {
        _coD4xRconApi.SetupSequence(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<CoD4xStatusResponseDto>(new CoD4xStatusResponseDto
                {
                    Players = [new CoD4xStatusPlayerDto { Num = 5, PlayerIdentifier = "other-guid", Name = "Other" }]
                })))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<CoD4xStatusResponseDto>(new CoD4xStatusResponseDto
                {
                    Players = [new CoD4xStatusPlayerDto { Num = 7, PlayerIdentifier = "guid-1", Name = "PlayerOne" }]
                })));

        _coD4xRconApi.Setup(x => x.Tell(TestServerId, It.IsAny<CoD4xTargetMessageRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(System.Net.HttpStatusCode.OK, new ApiResponse<string>("ok")));

        var result = await _sut.TryTellAsync(TestServerId, "guid-1", 5, "Hello", "PlayerOne", DateTime.UtcNow);

        Assert.True(result);
        _coD4xRconApi.Verify(x => x.Status(TestServerId, It.IsAny<CancellationToken>()), Times.Exactly(2));
        _coD4xRconApi.Verify(x => x.Tell(
            TestServerId,
            It.Is<CoD4xTargetMessageRequestDto>(r => r.Target == "7" && r.Message == "Hello"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryTellAsync_WithSlot_WhenStale_SkipsWithoutCallingRcon()
    {
        var staleTime = DateTime.UtcNow.AddSeconds(-10);

        var result = await _sut.TryTellAsync(TestServerId, "guid-1", 5, "Hello", "PlayerOne", staleTime);

        Assert.False(result);
        _coD4xRconApi.Verify(x => x.Status(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _coD4xRconApi.Verify(x => x.Tell(It.IsAny<Guid>(), It.IsAny<CoD4xTargetMessageRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
