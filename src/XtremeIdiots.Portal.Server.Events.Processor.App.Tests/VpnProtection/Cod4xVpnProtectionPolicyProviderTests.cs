using System.Net;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Cod4xPlugin;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.VpnProtection;

public sealed class Cod4xVpnProtectionPolicyProviderTests
{
    private static readonly Guid ServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly Mock<IRepositoryApiClient> repositoryApiClient = new();
    private readonly Mock<IVersionedGlobalConfigurationsApi> versionedGlobalConfigurations = new();
    private readonly Mock<IGlobalConfigurationsApi> globalConfigurationsApi = new();
    private readonly Mock<IVersionedGameServerConfigurationsApi> versionedServerConfigurations = new();
    private readonly Mock<IGameServerConfigurationsApi> serverConfigurationsApi = new();
    private readonly IMemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());
    private readonly Mock<ILogger<Cod4xVpnProtectionPolicyProvider>> logger = new();

    public Cod4xVpnProtectionPolicyProviderTests()
    {
        versionedGlobalConfigurations.Setup(x => x.V1).Returns(globalConfigurationsApi.Object);
        repositoryApiClient.Setup(x => x.GlobalConfigurations).Returns(versionedGlobalConfigurations.Object);
        versionedServerConfigurations.Setup(x => x.V1).Returns(serverConfigurationsApi.Object);
        repositoryApiClient.Setup(x => x.GameServerConfigurations).Returns(versionedServerConfigurations.Object);
        globalConfigurationsApi
            .Setup(x => x.GetConfiguration(Cod4xPluginSettingsConstants.Namespace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<ConfigurationDto>(HttpStatusCode.NotFound));
        serverConfigurationsApi
            .Setup(x => x.GetConfiguration(ServerId, Cod4xPluginSettingsConstants.Namespace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<ConfigurationDto>(HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task IsEnabledAsync_GlobalFlagsEnabled_ReturnsTrue()
    {
        SetupGlobal(/*lang=json,strict*/ "{\"schemaVersion\":1,\"enabled\":true,\"vpnProtectionEnabled\":true}");

        var result = await CreateSut().IsEnabledAsync(ServerId);

        Assert.True(result);
    }

    [Fact]
    public async Task IsEnabledAsync_ServerDisablesVpnProtection_ReturnsFalse()
    {
        SetupGlobal(/*lang=json,strict*/ "{\"schemaVersion\":1,\"enabled\":true,\"vpnProtectionEnabled\":true}");
        SetupServer(/*lang=json,strict*/ "{\"schemaVersion\":1,\"vpnProtectionEnabled\":false}");

        var result = await CreateSut().IsEnabledAsync(ServerId);

        Assert.False(result);
    }

    [Fact]
    public async Task IsEnabledAsync_ServerEnablesBothFlagsOverDisabledGlobal_ReturnsTrue()
    {
        SetupGlobal(/*lang=json,strict*/ "{\"schemaVersion\":1,\"enabled\":false,\"vpnProtectionEnabled\":false}");
        SetupServer(/*lang=json,strict*/ "{\"schemaVersion\":1,\"enabled\":true,\"vpnProtectionEnabled\":true}");

        var result = await CreateSut().IsEnabledAsync(ServerId);

        Assert.True(result);
    }

    [Fact]
    public async Task IsEnabledAsync_MalformedSettings_FailsClosed()
    {
        SetupGlobal("{\"schemaVersion\":1,");

        var result = await CreateSut().IsEnabledAsync(ServerId);

        Assert.False(result);
    }

    private Cod4xVpnProtectionPolicyProvider CreateSut() => new(
        repositoryApiClient.Object,
        memoryCache,
        logger.Object);

    private void SetupGlobal(string configuration) => globalConfigurationsApi
        .Setup(x => x.GetConfiguration(Cod4xPluginSettingsConstants.Namespace, It.IsAny<CancellationToken>()))
        .ReturnsAsync(SuccessResult(configuration));

    private void SetupServer(string configuration) => serverConfigurationsApi
        .Setup(x => x.GetConfiguration(ServerId, Cod4xPluginSettingsConstants.Namespace, It.IsAny<CancellationToken>()))
        .ReturnsAsync(SuccessResult(configuration));

    private static ApiResult<ConfigurationDto> SuccessResult(string configuration)
    {
        var dto = new ConfigurationDto();
        typeof(ConfigurationDto).GetProperty(nameof(ConfigurationDto.Namespace))!
            .SetValue(dto, Cod4xPluginSettingsConstants.Namespace);
        typeof(ConfigurationDto).GetProperty(nameof(ConfigurationDto.Configuration))!
            .SetValue(dto, configuration);
        return new ApiResult<ConfigurationDto>(HttpStatusCode.OK, new ApiResponse<ConfigurationDto>(dto));
    }
}
