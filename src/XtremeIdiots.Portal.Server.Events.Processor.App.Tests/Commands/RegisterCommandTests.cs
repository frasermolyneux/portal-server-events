using System.Net;

using Microsoft.Extensions.Logging;

using Moq;

using MX.Observability.ApplicationInsights.Auditing;

using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class RegisterCommandTests
{
    private readonly Mock<IRepositoryApiClient> _repoClient = new();
    private readonly Mock<XtremeIdiots.Portal.Repository.Api.Client.V1.IVersionedConnectedPlayersApi> _versionedConnectedPlayers = new();
    private readonly Mock<IConnectedPlayersApi> _connectedPlayersApi = new();
    private readonly Mock<IRconResponseService> _rconResponseService = new();
    private readonly Mock<IRegisterCommandRateLimiter> _rateLimiter = new();
    private readonly Mock<IAuditLogger> _auditLogger = new();
    private readonly Mock<ILogger<RegisterCommand>> _logger = new();

    private readonly RegisterCommand _sut;

    private static readonly Guid TestServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TestPlayerId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public RegisterCommandTests()
    {
        _versionedConnectedPlayers.Setup(x => x.V1).Returns(_connectedPlayersApi.Object);
        _repoClient.Setup(x => x.ConnectedPlayers).Returns(_versionedConnectedPlayers.Object);
        _rateLimiter
            .Setup(x => x.TryAcquire(It.IsAny<Guid>(), It.IsAny<DateTime>(), out It.Ref<TimeSpan>.IsAny))
            .Returns((Guid _, DateTime _, out TimeSpan retryAfter) =>
            {
                retryAfter = TimeSpan.Zero;
                return true;
            });

        _rconResponseService
            .Setup(x => x.TryTellAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _rconResponseService
            .Setup(x => x.TryTellAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _sut = new RegisterCommand(_repoClient.Object, _rconResponseService.Object, _rateLimiter.Object, _auditLogger.Object, _logger.Object);
    }

    private static CommandContext CreateContext(string message = "!register AB12CD", Guid? playerId = null) => new()
    {
        ServerId = TestServerId,
        GameType = "CallOfDuty4",
        PlayerGuid = "abc123",
        Username = "TestPlayer",
        SlotId = 3,
        Message = message,
        EventGeneratedUtc = DateTime.UtcNow,
        EventPublishedUtc = DateTime.UtcNow,
        PlayerId = playerId ?? TestPlayerId
    };

    [Theory]
    [InlineData("!register AB12CD", true)]
    [InlineData("!REGISTER AB12CD", true)]
    [InlineData(" !register AB12CD", true)]
    [InlineData("!registering AB12CD", false)]
    [InlineData("!like", false)]
    public void CanHandle_ReturnsExpected(string message, bool expected)
    {
        var result = _sut.CanHandle(message);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPlayerIdMissing_ReturnsFailed()
    {
        var result = await _sut.ExecuteAsync(CreateContext(playerId: null) with { PlayerId = null });

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Equal("Player context unavailable", result.ResponseMessage);

        _rconResponseService.Verify(x => x.TryTellAsync(
            TestServerId,
            "abc123",
            3,
            "Player context unavailable",
            "TestPlayer",
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);

        _connectedPlayersApi.Verify(x => x.ConsumeConnectedPlayerActivationCode(It.IsAny<XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ConnectedPlayers.ConsumeConnectedPlayerActivationCodeDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("!register")]
    [InlineData("!register AB12CD EXTRA")]
    [InlineData("!register 123")]
    [InlineData("!register abc!23")]
    public async Task ExecuteAsync_WhenCommandFormatInvalid_ReturnsFailed(string message)
    {
        var result = await _sut.ExecuteAsync(CreateContext(message));

        Assert.True(result.Handled);
        Assert.False(result.Success);
        _connectedPlayersApi.Verify(x => x.ConsumeConnectedPlayerActivationCode(It.IsAny<XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ConnectedPlayers.ConsumeConnectedPlayerActivationCodeDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenConsumeSucceeds_ReturnsSuccessAndUsesNormalizedCode()
    {
        _connectedPlayersApi
            .Setup(x => x.ConsumeConnectedPlayerActivationCode(It.IsAny<XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ConnectedPlayers.ConsumeConnectedPlayerActivationCodeDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MX.Api.Abstractions.ApiResult<XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ConnectedPlayers.ConnectedPlayerDto>(HttpStatusCode.Created));

        var result = await _sut.ExecuteAsync(CreateContext("!register ab12cd"));

        Assert.True(result.Handled);
        Assert.True(result.Success);

        _rconResponseService.Verify(x => x.TryTellAsync(
            TestServerId,
            "abc123",
            3,
            "Registration successful. Your account is now linked.",
            "TestPlayer",
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);

        _connectedPlayersApi.Verify(x => x.ConsumeConnectedPlayerActivationCode(
            It.Is<XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ConnectedPlayers.ConsumeConnectedPlayerActivationCodeDto>(dto =>
                dto.PlayerId == TestPlayerId && dto.Code == "AB12CD"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenConsumeReturnsOk_ReturnsSuccess()
    {
        _connectedPlayersApi
            .Setup(x => x.ConsumeConnectedPlayerActivationCode(It.IsAny<XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ConnectedPlayers.ConsumeConnectedPlayerActivationCodeDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MX.Api.Abstractions.ApiResult<XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ConnectedPlayers.ConnectedPlayerDto>(HttpStatusCode.OK));

        var result = await _sut.ExecuteAsync(CreateContext());

        Assert.True(result.Handled);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task ExecuteAsync_WithTabSeparatedArguments_ParsesAndSucceeds()
    {
        _connectedPlayersApi
            .Setup(x => x.ConsumeConnectedPlayerActivationCode(It.IsAny<XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ConnectedPlayers.ConsumeConnectedPlayerActivationCodeDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MX.Api.Abstractions.ApiResult<XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ConnectedPlayers.ConnectedPlayerDto>(HttpStatusCode.OK));

        var result = await _sut.ExecuteAsync(CreateContext("!register\tAB12CD"));

        Assert.True(result.Handled);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task ExecuteAsync_WhenConsumeConflicts_ReturnsFailed()
    {
        _connectedPlayersApi
            .Setup(x => x.ConsumeConnectedPlayerActivationCode(It.IsAny<XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ConnectedPlayers.ConsumeConnectedPlayerActivationCodeDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MX.Api.Abstractions.ApiResult<XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ConnectedPlayers.ConnectedPlayerDto>(HttpStatusCode.Conflict));

        var result = await _sut.ExecuteAsync(CreateContext());

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Equal("Player is already linked to a different profile", result.ResponseMessage);

        _rconResponseService.Verify(x => x.TryTellAsync(
            TestServerId,
            "abc123",
            3,
            "Player is already linked to a different profile",
            "TestPlayer",
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenConsumeBadRequest_ReturnsFailed()
    {
        _connectedPlayersApi
            .Setup(x => x.ConsumeConnectedPlayerActivationCode(It.IsAny<XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ConnectedPlayers.ConsumeConnectedPlayerActivationCodeDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MX.Api.Abstractions.ApiResult<XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ConnectedPlayers.ConnectedPlayerDto>(HttpStatusCode.BadRequest));

        var result = await _sut.ExecuteAsync(CreateContext());

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Equal("Activation code is invalid, expired, inactive, or exhausted", result.ResponseMessage);

        _rconResponseService.Verify(x => x.TryTellAsync(
            TestServerId,
            "abc123",
            3,
            "Activation code is invalid, expired, inactive, or exhausted",
            "TestPlayer",
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenConsumeThrows_ReturnsFailedAndSendsPrivateFailure()
    {
        _connectedPlayersApi
            .Setup(x => x.ConsumeConnectedPlayerActivationCode(It.IsAny<XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ConnectedPlayers.ConsumeConnectedPlayerActivationCodeDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _sut.ExecuteAsync(CreateContext());

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Equal("Registration failed due to a temporary error. Please try again.", result.ResponseMessage);

        _rconResponseService.Verify(x => x.TryTellAsync(
            TestServerId,
            "abc123",
            3,
            "Registration failed due to a temporary error. Please try again.",
            "TestPlayer",
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenRateLimited_ReturnsFailedAndDoesNotCallConsumeApi()
    {
        _rateLimiter
            .Setup(x => x.TryAcquire(It.IsAny<Guid>(), It.IsAny<DateTime>(), out It.Ref<TimeSpan>.IsAny))
            .Returns((Guid _, DateTime _, out TimeSpan retryAfter) =>
            {
                retryAfter = TimeSpan.FromSeconds(42);
                return false;
            });

        var result = await _sut.ExecuteAsync(CreateContext());

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Equal("Too many !register attempts. Please wait 42 seconds and try again.", result.ResponseMessage);

        _connectedPlayersApi.Verify(x => x.ConsumeConnectedPlayerActivationCode(
            It.IsAny<XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ConnectedPlayers.ConsumeConnectedPlayerActivationCodeDto>(),
            It.IsAny<CancellationToken>()), Times.Never);

        _rconResponseService.Verify(x => x.TryTellAsync(
            TestServerId,
            "abc123",
            3,
            "Too many !register attempts. Please wait 42 seconds and try again.",
            "TestPlayer",
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSlotMissing_UsesGuidLookupTellPath()
    {
        _connectedPlayersApi
            .Setup(x => x.ConsumeConnectedPlayerActivationCode(It.IsAny<XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ConnectedPlayers.ConsumeConnectedPlayerActivationCodeDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MX.Api.Abstractions.ApiResult<XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ConnectedPlayers.ConnectedPlayerDto>(HttpStatusCode.OK));

        var result = await _sut.ExecuteAsync(CreateContext() with { SlotId = null });

        Assert.True(result.Handled);
        Assert.True(result.Success);

        _rconResponseService.Verify(x => x.TryTellAsync(
            TestServerId,
            "abc123",
            "Registration successful. Your account is now linked.",
            "TestPlayer",
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);

        _rconResponseService.Verify(x => x.TryTellAsync(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
