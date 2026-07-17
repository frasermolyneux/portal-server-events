using System.Net;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.VpnProtection;

public sealed class VpnProtectionSettingsProviderTests
{
    private static readonly Guid ServerId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly Mock<IRepositoryApiClient> repositoryClient = new();
    private readonly Mock<IVersionedGlobalConfigurationsApi> versionedGlobalConfigurations = new();
    private readonly Mock<IGlobalConfigurationsApi> globalConfigurationsApi = new();
    private readonly Mock<IVersionedGameServerConfigurationsApi> versionedServerConfigurations = new();
    private readonly Mock<IGameServerConfigurationsApi> serverConfigurationsApi = new();
    private readonly IMemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());
    private readonly Mock<ILogger<VpnProtectionSettingsProvider>> logger = new();

    public VpnProtectionSettingsProviderTests()
    {
        versionedGlobalConfigurations.Setup(x => x.V1).Returns(globalConfigurationsApi.Object);
        repositoryClient.Setup(x => x.GlobalConfigurations).Returns(versionedGlobalConfigurations.Object);
        versionedServerConfigurations.Setup(x => x.V1).Returns(serverConfigurationsApi.Object);
        repositoryClient.Setup(x => x.GameServerConfigurations).Returns(versionedServerConfigurations.Object);

        globalConfigurationsApi
            .Setup(x => x.GetConfiguration(VpnProtectionSettingsConstants.Namespace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<ConfigurationDto>(HttpStatusCode.NotFound));
        serverConfigurationsApi
            .Setup(x => x.GetConfiguration(ServerId, VpnProtectionSettingsConstants.Namespace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<ConfigurationDto>(HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task GetEffectiveSettingsAsync_MissingDocuments_ReturnsDisabledWithoutValidationFailure()
    {
        var result = await CreateSut().GetEffectiveSettingsAsync(ServerId);

        Assert.False(result.Enabled);
        Assert.False(result.ValidationFailed);
        Assert.Empty(result.Rules);
    }

    [Fact]
    public async Task GetEffectiveSettingsAsync_GlobalAndServerDocuments_MergesOverridesAndExclusions()
    {
        SetupGlobalConfiguration(/*lang=json,strict*/ """
            {
                "schemaVersion": 1,
                "enabled": false,
                "rules": [
                    {
                        "id": "vpn",
                        "signal": "ProxyCheckIsVpn",
                        "operator": "Equal",
                        "expectedValue": "true",
                        "action": "Observation"
                    }
                ],
                "excludedPlayerTags": ["Global Exempt"]
            }
            """);
        SetupServerConfiguration(/*lang=json,strict*/ """
            {
                "schemaVersion": 1,
                "enabled": true,
                "ruleOverrides": [
                    { "id": "vpn", "action": "Kick" }
                ],
                "excludedPlayerTags": ["Server Exempt"]
            }
            """);

        var result = await CreateSut().GetEffectiveSettingsAsync(ServerId);

        Assert.True(result.Enabled);
        Assert.False(result.ValidationFailed);
        var rule = Assert.Single(result.Rules);
        Assert.Equal(VpnProtectionAction.Kick, rule.Action);
        Assert.Equal(2, result.ExcludedPlayerTags.Count);
        Assert.Contains("Global Exempt", result.ExcludedPlayerTags);
        Assert.Contains("Server Exempt", result.ExcludedPlayerTags);
    }

    [Fact]
    public async Task GetEffectiveSettingsAsync_MalformedDocument_FailsClosed()
    {
        SetupGlobalConfiguration("{\"schemaVersion\":1,\"rules\":[");

        var result = await CreateSut().GetEffectiveSettingsAsync(ServerId);

        Assert.False(result.Enabled);
        Assert.True(result.ValidationFailed);
        Assert.Empty(result.Rules);
    }

    [Fact]
    public async Task GetEffectiveSettingsAsync_RepositoryFailure_FailsClosed()
    {
        globalConfigurationsApi
            .Setup(x => x.GetConfiguration(VpnProtectionSettingsConstants.Namespace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<ConfigurationDto>(HttpStatusCode.InternalServerError));

        var result = await CreateSut().GetEffectiveSettingsAsync(ServerId);

        Assert.False(result.Enabled);
        Assert.True(result.ValidationFailed);
    }

    [Fact]
    public async Task GetEffectiveSettingsAsync_SecondCall_UsesCache()
    {
        SetupGlobalConfiguration(/*lang=json,strict*/ """
            {
                "schemaVersion": 1,
                "enabled": true,
                "rules": [],
                "excludedPlayerTags": []
            }
            """);
        var sut = CreateSut();

        await sut.GetEffectiveSettingsAsync(ServerId);
        await sut.GetEffectiveSettingsAsync(ServerId);

        globalConfigurationsApi.Verify(
            x => x.GetConfiguration(VpnProtectionSettingsConstants.Namespace, It.IsAny<CancellationToken>()),
            Times.Once);
        serverConfigurationsApi.Verify(
            x => x.GetConfiguration(ServerId, VpnProtectionSettingsConstants.Namespace, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private VpnProtectionSettingsProvider CreateSut() => new(
        repositoryClient.Object,
        memoryCache,
        logger.Object);

    private void SetupGlobalConfiguration(string configuration)
    {
        globalConfigurationsApi
            .Setup(x => x.GetConfiguration(VpnProtectionSettingsConstants.Namespace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(CreateConfiguration(configuration)));
    }

    private void SetupServerConfiguration(string configuration)
    {
        serverConfigurationsApi
            .Setup(x => x.GetConfiguration(ServerId, VpnProtectionSettingsConstants.Namespace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(CreateConfiguration(configuration)));
    }

    private static ApiResult<ConfigurationDto> SuccessResult(ConfigurationDto configuration) =>
        new(HttpStatusCode.OK, new ApiResponse<ConfigurationDto>(configuration));

    private static ConfigurationDto CreateConfiguration(string configuration)
    {
        var dto = new ConfigurationDto();
        typeof(ConfigurationDto).GetProperty(nameof(ConfigurationDto.Namespace))!
            .SetValue(dto, VpnProtectionSettingsConstants.Namespace);
        typeof(ConfigurationDto).GetProperty(nameof(ConfigurationDto.Configuration))!
            .SetValue(dto, configuration);
        return dto;
    }
}