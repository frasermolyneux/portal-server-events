using System.Net;

using Microsoft.Extensions.Logging;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.VpnProtection;

public sealed class VpnProtectionRconEnforcerTests
{
    private static readonly Guid ServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly Mock<IServersApiClient> serversApiClient = new();
    private readonly Mock<IVersionedCod4RconApi> versionedCod4RconApi = new();
    private readonly Mock<ICod4RconApi> cod4RconApi = new();
    private readonly Mock<IVersionedCoD4xRconApi> versionedCod4xRconApi = new();
    private readonly Mock<ICoD4xRconApi> cod4xRconApi = new();
    private readonly Mock<ILogger<VpnProtectionRconEnforcer>> logger = new();

    public VpnProtectionRconEnforcerTests()
    {
        versionedCod4RconApi.Setup(x => x.V1).Returns(cod4RconApi.Object);
        serversApiClient.Setup(x => x.Cod4Rcon).Returns(versionedCod4RconApi.Object);
        versionedCod4xRconApi.Setup(x => x.V1).Returns(cod4xRconApi.Object);
        serversApiClient.Setup(x => x.CoD4xRcon).Returns(versionedCod4xRconApi.Object);
    }

    [Fact]
    public async Task EnforceAsync_Cod4Ban_VerifiesGuidAndSlotThenBans()
    {
        cod4RconApi
            .Setup(x => x.Status(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(StatusResult(new RconStatusPlayerDto { Num = 4, Guid = "player-guid", Name = "TestPlayer" }));
        cod4RconApi
            .Setup(x => x.Ban(ServerId, It.IsAny<ClientSlotRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StringResult(HttpStatusCode.OK));

        var result = await CreateSut().EnforceAsync(
            CreateContext(GameType.CallOfDuty4),
            VpnProtectionAction.Ban,
            "VPN Protection",
            CancellationToken.None);

        Assert.Equal(VpnProtectionRconOutcome.Succeeded, result);
        cod4RconApi.Verify(x => x.Ban(
            ServerId,
            It.Is<ClientSlotRequest>(request => request.ClientId == 4),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnforceAsync_Cod4SlotReused_DoesNotRemovePlayer()
    {
        cod4RconApi
            .Setup(x => x.Status(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(StatusResult(new RconStatusPlayerDto { Num = 4, Guid = "different-guid", Name = "Other" }));

        var result = await CreateSut().EnforceAsync(
            CreateContext(GameType.CallOfDuty4),
            VpnProtectionAction.Kick,
            "VPN Protection",
            CancellationToken.None);

        Assert.Equal(VpnProtectionRconOutcome.PlayerNotConnected, result);
        cod4RconApi.Verify(
            x => x.Kick(It.IsAny<Guid>(), It.IsAny<ClientSlotRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EnforceAsync_Cod4xKick_UsesVerifiedOnlyKickWithReason()
    {
        cod4xRconApi
            .Setup(x => x.Status(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(
                HttpStatusCode.OK,
                new ApiResponse<CoD4xStatusResponseDto>(new CoD4xStatusResponseDto
                {
                    Players = [new CoD4xStatusPlayerDto { Num = 4, PlayerIdentifier = "player-guid", Name = "TestPlayer" }]
                })));
        cod4xRconApi
            .Setup(x => x.OnlyKick(ServerId, It.IsAny<CoD4xClientReasonRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StringResult(HttpStatusCode.OK));

        var result = await CreateSut().EnforceAsync(
            CreateContext(GameType.CallOfDuty4x),
            VpnProtectionAction.Kick,
            "VPN Protection",
            CancellationToken.None);

        Assert.Equal(VpnProtectionRconOutcome.Succeeded, result);
        cod4xRconApi.Verify(x => x.OnlyKick(
            ServerId,
            It.Is<CoD4xClientReasonRequestDto>(request => request.ClientId == 4 && request.Reason == "VPN Protection"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnforceAsync_UnsupportedGame_ReturnsUnsupportedWithoutApiCall()
    {
        var result = await CreateSut().EnforceAsync(
            CreateContext(GameType.Insurgency),
            VpnProtectionAction.Ban,
            "VPN Protection",
            CancellationToken.None);

        Assert.Equal(VpnProtectionRconOutcome.UnsupportedGame, result);
    }

    [Fact]
    public async Task EnforceAsync_Observation_DoesNotUseRcon()
    {
        var result = await CreateSut().EnforceAsync(
            CreateContext(GameType.CallOfDuty4),
            VpnProtectionAction.Observation,
            "VPN Protection",
            CancellationToken.None);

        Assert.Equal(VpnProtectionRconOutcome.NotRequired, result);
        cod4RconApi.Verify(
            x => x.Status(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private VpnProtectionRconEnforcer CreateSut() => new(serversApiClient.Object, logger.Object);

    private static VpnProtectionContext CreateContext(GameType gameType) => new()
    {
        ServerId = ServerId,
        GameType = gameType,
        PlayerId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        PlayerGuid = "player-guid",
        Username = "TestPlayer",
        PlayerTags = [],
        SlotId = 4
    };

    private static ApiResult<RconStatusResponseDto> StatusResult(params RconStatusPlayerDto[] players) =>
        new(HttpStatusCode.OK, new ApiResponse<RconStatusResponseDto>(new RconStatusResponseDto { Players = [.. players] }));

    private static ApiResult<string> StringResult(HttpStatusCode statusCode) =>
        new(statusCode, new ApiResponse<string>("ok"));
}