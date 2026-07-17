using System.Net;

using Microsoft.Extensions.Logging;

using Moq;

using MX.Api.Abstractions;
using MX.GeoLocation.Abstractions.Models.V1_1;

using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.VpnProtection;

public sealed class VpnDetectedTagServiceTests
{
    private static readonly Guid PlayerId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly Mock<IRepositoryApiClient> repositoryApiClient = new();
    private readonly Mock<IVersionedPlayersApi> versionedPlayersApi = new();
    private readonly Mock<IPlayersApi> playersApi = new();
    private readonly Mock<ILogger<VpnDetectedTagService>> logger = new();

    public VpnDetectedTagServiceTests()
    {
        versionedPlayersApi.Setup(x => x.V1).Returns(playersApi.Object);
        repositoryApiClient.Setup(x => x.Players).Returns(versionedPlayersApi.Object);
        playersApi
            .Setup(x => x.SetVpnDetectedTag(PlayerId, It.IsAny<SetVpnDetectedTagDto>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));
    }

    [Fact]
    public async Task AddIfDetectedAsync_VpnDetected_AddsSystemTag()
    {
        var intelligence = CreateIntelligence(isVpn: true);

        await CreateSut().AddIfDetectedAsync(PlayerId, intelligence);

        playersApi.Verify(
            x => x.SetVpnDetectedTag(PlayerId, It.Is<SetVpnDetectedTagDto>(dto => dto.IsDetected)),
            Times.Once);
    }

    [Theory]
    [InlineData(false)]
    public async Task AddIfDetectedAsync_VpnNotDetected_DoesNotMutateTag(bool isVpn)
    {
        await CreateSut().AddIfDetectedAsync(PlayerId, CreateIntelligence(isVpn));

        playersApi.Verify(x => x.SetVpnDetectedTag(It.IsAny<Guid>(), It.IsAny<SetVpnDetectedTagDto>()), Times.Never);
    }

    [Fact]
    public async Task AddIfDetectedAsync_MissingProxyCheck_DoesNotMutateTag()
    {
        await CreateSut().AddIfDetectedAsync(PlayerId, new IpIntelligenceDto());

        playersApi.Verify(x => x.SetVpnDetectedTag(It.IsAny<Guid>(), It.IsAny<SetVpnDetectedTagDto>()), Times.Never);
    }

    [Fact]
    public async Task AddIfDetectedAsync_RepositoryFailure_DoesNotThrow()
    {
        playersApi
            .Setup(x => x.SetVpnDetectedTag(PlayerId, It.IsAny<SetVpnDetectedTagDto>()))
            .ThrowsAsync(new HttpRequestException("Repository unavailable"));

        await CreateSut().AddIfDetectedAsync(PlayerId, CreateIntelligence(isVpn: true));
    }

    private VpnDetectedTagService CreateSut() => new(repositoryApiClient.Object, logger.Object);

    private static IpIntelligenceDto CreateIntelligence(bool isVpn) =>
        Newtonsoft.Json.JsonConvert.DeserializeObject<IpIntelligenceDto>(
            Newtonsoft.Json.JsonConvert.SerializeObject(new { ProxyCheck = new { IsVpn = isVpn } }))!;
}