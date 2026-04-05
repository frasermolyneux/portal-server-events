using Microsoft.Extensions.Logging;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
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
}
