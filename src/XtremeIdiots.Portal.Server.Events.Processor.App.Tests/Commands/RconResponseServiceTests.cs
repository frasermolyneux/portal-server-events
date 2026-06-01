using Microsoft.Extensions.Logging;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class RconResponseServiceTests
{
    private readonly Mock<IRconApi> _rconApi = new();
    private readonly Mock<ILogger<RconResponseService>> _logger = new();
    private readonly RconResponseService _sut;

    private static readonly Guid TestServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public RconResponseServiceTests()
    {
        _sut = new RconResponseService(_rconApi.Object, _logger.Object);
    }

    [Fact]
    public async Task TrySayAsync_WhenFresh_SendsMessage()
    {
        _rconApi.Setup(x => x.Say(TestServerId, "Hello"))
            .ReturnsAsync(new ApiResult(System.Net.HttpStatusCode.OK));

        var result = await _sut.TrySayAsync(TestServerId, "Hello", DateTime.UtcNow);

        Assert.True(result);
        _rconApi.Verify(x => x.Say(TestServerId, "Hello"), Times.Once);
    }

    [Fact]
    public async Task TrySayAsync_WhenStale_SkipsMessage()
    {
        var staleTime = DateTime.UtcNow.AddSeconds(-10);

        var result = await _sut.TrySayAsync(TestServerId, "Hello", staleTime);

        Assert.False(result);
        _rconApi.Verify(x => x.Say(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TrySayAsync_WhenRconFails_ReturnsFalse()
    {
        _rconApi.Setup(x => x.Say(TestServerId, "Hello"))
            .ThrowsAsync(new Exception("connection refused"));

        var result = await _sut.TrySayAsync(TestServerId, "Hello", DateTime.UtcNow);

        Assert.False(result);
    }

    [Fact]
    public async Task TryTellAsync_WhenFresh_SendsMessage()
    {
        _rconApi.Setup(x => x.GetServerStatus(TestServerId))
            .ReturnsAsync(new ApiResult<ServerRconStatusResponseDto>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<ServerRconStatusResponseDto>(
                    new ServerRconStatusResponseDto
                    {
                        Players = [new ServerRconPlayerDto { Num = 5, Guid = "guid-1", Name = "PlayerOne" }]
                    })));

        _rconApi.Setup(x => x.TellPlayerWithVerification(TestServerId, 5, "Hello", "PlayerOne"))
            .ReturnsAsync(new ApiResult(System.Net.HttpStatusCode.OK));

        var result = await _sut.TryTellAsync(TestServerId, "guid-1", "Hello", "PlayerOne", DateTime.UtcNow);

        Assert.True(result);
        _rconApi.Verify(x => x.TellPlayerWithVerification(TestServerId, 5, "Hello", "PlayerOne"), Times.Once);
    }

    [Fact]
    public async Task TryTellAsync_WhenStale_SkipsMessage()
    {
        var staleTime = DateTime.UtcNow.AddSeconds(-10);

        var result = await _sut.TryTellAsync(TestServerId, "guid-1", "Hello", "PlayerOne", staleTime);

        Assert.False(result);
        _rconApi.Verify(x => x.TellPlayerWithVerification(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        _rconApi.Verify(x => x.GetServerStatus(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task TryTellAsync_WhenPlayerMissing_ReturnsFalse()
    {
        _rconApi.Setup(x => x.GetServerStatus(TestServerId))
            .ReturnsAsync(new ApiResult<ServerRconStatusResponseDto>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<ServerRconStatusResponseDto>(
                    new ServerRconStatusResponseDto
                    {
                        Players = [new ServerRconPlayerDto { Num = 6, Guid = "other-guid", Name = "Other" }]
                    })));

        var result = await _sut.TryTellAsync(TestServerId, "guid-1", "Hello", "PlayerOne", DateTime.UtcNow);

        Assert.False(result);
        _rconApi.Verify(x => x.TellPlayerWithVerification(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task TryTellAsync_WhenStatusCallFails_ReturnsFalse()
    {
        _rconApi.Setup(x => x.GetServerStatus(TestServerId))
            .ReturnsAsync(new ApiResult<ServerRconStatusResponseDto>(System.Net.HttpStatusCode.BadGateway));

        var result = await _sut.TryTellAsync(TestServerId, "guid-1", "Hello", "PlayerOne", DateTime.UtcNow);

        Assert.False(result);
        _rconApi.Verify(x => x.TellPlayerWithVerification(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task TryTellAsync_WhenTellReturnsNonSuccess_ReturnsFalse()
    {
        _rconApi.Setup(x => x.GetServerStatus(TestServerId))
            .ReturnsAsync(new ApiResult<ServerRconStatusResponseDto>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<ServerRconStatusResponseDto>(
                    new ServerRconStatusResponseDto
                    {
                        Players = [new ServerRconPlayerDto { Num = 5, Guid = "guid-1", Name = "PlayerOne" }]
                    })));

        _rconApi.Setup(x => x.TellPlayerWithVerification(TestServerId, 5, "Hello", "PlayerOne"))
            .ReturnsAsync(new ApiResult(System.Net.HttpStatusCode.BadRequest));

        var result = await _sut.TryTellAsync(TestServerId, "guid-1", "Hello", "PlayerOne", DateTime.UtcNow);

        Assert.False(result);
    }

    [Fact]
    public async Task TryTellAsync_WhenTellThrows_ReturnsFalse()
    {
        _rconApi.Setup(x => x.GetServerStatus(TestServerId))
            .ReturnsAsync(new ApiResult<ServerRconStatusResponseDto>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<ServerRconStatusResponseDto>(
                    new ServerRconStatusResponseDto
                    {
                        Players = [new ServerRconPlayerDto { Num = 5, Guid = "guid-1", Name = "PlayerOne" }]
                    })));

        _rconApi.Setup(x => x.TellPlayerWithVerification(TestServerId, 5, "Hello", "PlayerOne"))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _sut.TryTellAsync(TestServerId, "guid-1", "Hello", "PlayerOne", DateTime.UtcNow);

        Assert.False(result);
    }

    [Fact]
    public async Task TryTellAsync_WithSlot_WhenFreshAndTellSucceeds_ReturnsTrue()
    {
        _rconApi.Setup(x => x.TellPlayerWithVerification(TestServerId, 5, "Hello", "PlayerOne"))
            .ReturnsAsync(new ApiResult(System.Net.HttpStatusCode.OK));

        var result = await _sut.TryTellAsync(TestServerId, "guid-1", 5, "Hello", "PlayerOne", DateTime.UtcNow);

        Assert.True(result);
        _rconApi.Verify(x => x.GetServerStatus(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task TryTellAsync_WithSlot_WhenTellFails_FallsBackToGuidLookup()
    {
        _rconApi.Setup(x => x.TellPlayerWithVerification(TestServerId, 5, "Hello", "PlayerOne"))
            .ReturnsAsync(new ApiResult(System.Net.HttpStatusCode.BadRequest));

        _rconApi.Setup(x => x.GetServerStatus(TestServerId))
            .ReturnsAsync(new ApiResult<ServerRconStatusResponseDto>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<ServerRconStatusResponseDto>(
                    new ServerRconStatusResponseDto
                    {
                        Players = [new ServerRconPlayerDto { Num = 7, Guid = "guid-1", Name = "PlayerOne" }]
                    })));

        _rconApi.Setup(x => x.TellPlayerWithVerification(TestServerId, 7, "Hello", "PlayerOne"))
            .ReturnsAsync(new ApiResult(System.Net.HttpStatusCode.OK));

        var result = await _sut.TryTellAsync(TestServerId, "guid-1", 5, "Hello", "PlayerOne", DateTime.UtcNow);

        Assert.True(result);
        _rconApi.Verify(x => x.GetServerStatus(TestServerId), Times.Once);
        _rconApi.Verify(x => x.TellPlayerWithVerification(TestServerId, 7, "Hello", "PlayerOne"), Times.Once);
    }

    [Fact]
    public async Task TryTellAsync_WithSlot_WhenStale_SkipsWithoutCallingRcon()
    {
        var staleTime = DateTime.UtcNow.AddSeconds(-10);

        var result = await _sut.TryTellAsync(TestServerId, "guid-1", 5, "Hello", "PlayerOne", staleTime);

        Assert.False(result);
        _rconApi.Verify(x => x.TellPlayerWithVerification(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        _rconApi.Verify(x => x.GetServerStatus(It.IsAny<Guid>()), Times.Never);
    }
}
