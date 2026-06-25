using System.Net;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;
using XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Fixtures;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class WelcomeMessageSettingsProviderTests
{
    private static readonly Guid ServerId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly Mock<IRepositoryApiClient> _repositoryClient = new();
    private readonly Mock<IVersionedGlobalConfigurationsApi> _versionedGlobalConfigs = new();
    private readonly Mock<IGlobalConfigurationsApi> _globalConfigsApi = new();
    private readonly Mock<IVersionedGameServerConfigurationsApi> _versionedServerConfigs = new();
    private readonly Mock<IGameServerConfigurationsApi> _serverConfigsApi = new();
    private readonly IMemoryCache _memoryCache = new MemoryCache(new MemoryCacheOptions());
    private readonly Mock<ILogger<WelcomeMessageSettingsProvider>> _logger = new();

    public WelcomeMessageSettingsProviderTests()
    {
        _versionedGlobalConfigs.Setup(x => x.V1).Returns(_globalConfigsApi.Object);
        _repositoryClient.Setup(x => x.GlobalConfigurations).Returns(_versionedGlobalConfigs.Object);

        _versionedServerConfigs.Setup(x => x.V1).Returns(_serverConfigsApi.Object);
        _repositoryClient.Setup(x => x.GameServerConfigurations).Returns(_versionedServerConfigs.Object);

        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(Array.Empty<ConfigurationDto>())));

        _serverConfigsApi
            .Setup(x => x.GetConfigurations(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(Array.Empty<ConfigurationDto>())));
    }

    [Fact]
    public async Task GetEffectiveSettingsAsync_WhenUsingFixtures_LocksCurrentMergeAndReadBehavior()
    {
        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto(WelcomeMessageSettingsConstants.Namespace,
                    SettingsFixtureLoader.LoadSettings("welcomeMessages.global.json"))
            ])));

        _serverConfigsApi
            .Setup(x => x.GetConfigurations(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto(WelcomeMessageSettingsConstants.Namespace,
                    SettingsFixtureLoader.LoadSettings("welcomeMessages.server.merge.json"))
            ])));

        var sut = CreateSut();

        var result = await sut.GetEffectiveSettingsAsync(ServerId);

        Assert.True(result.Enabled);
        Assert.Equal("ServerFallback", result.CountryFallback);
        Assert.Equal(300, result.StaleThresholdSeconds);
        Assert.False(result.ValidationFailed);
        Assert.Equal(3, result.Rules.Count);

        var alpha = result.Rules.Single(r => r.Id == "global-alpha");
        Assert.False(alpha.Enabled);
        Assert.Equal(20, alpha.Priority);
        Assert.Equal(WelcomeMessageVisibility.Public, alpha.Visibility);
        Assert.Equal("Server alpha override", alpha.MessageTemplate);
        Assert.Equal(["vip"], alpha.RequiredTags);
        Assert.Equal(6, alpha.ConnectionDelaySeconds);
        Assert.Equal(0, alpha.OrderIndex);

        var beta = result.Rules.Single(r => r.Id == "global-beta");
        Assert.True(beta.Enabled);
        Assert.Equal(5, beta.Priority);
        Assert.Equal(WelcomeMessageVisibility.Public, beta.Visibility);
        Assert.Equal("Welcome beta", beta.MessageTemplate);
        Assert.Equal([], beta.RequiredTags);
        Assert.Equal(5, beta.ConnectionDelaySeconds);
        Assert.Equal(1, beta.OrderIndex);

        var gamma = result.Rules.Single(r => r.Id == "server-gamma");
        Assert.True(gamma.Enabled);
        Assert.Equal(30, gamma.Priority);
        Assert.Equal(WelcomeMessageVisibility.Private, gamma.Visibility);
        Assert.Equal("Server gamma", gamma.MessageTemplate);
        Assert.Equal(["admin"], gamma.RequiredTags);
        Assert.Equal(7, gamma.ConnectionDelaySeconds);
        Assert.Equal(2, gamma.OrderIndex);
    }

    [Fact]
    public async Task GetEffectiveSettingsAsync_WhenServerDisablesGlobalInheritance_UsesOnlyServerRules()
    {
        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto(WelcomeMessageSettingsConstants.Namespace,
                    SettingsFixtureLoader.LoadSettings("welcomeMessages.global.json"))
            ])));

        _serverConfigsApi
            .Setup(x => x.GetConfigurations(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto(WelcomeMessageSettingsConstants.Namespace,
                    SettingsFixtureLoader.LoadSettings("welcomeMessages.server.inheritOff.json"))
            ])));

        var sut = CreateSut();

        var result = await sut.GetEffectiveSettingsAsync(ServerId);

        Assert.Single(result.Rules);
        var onlyRule = result.Rules[0];
        Assert.Equal("server-only", onlyRule.Id);
        Assert.Equal("Server only", onlyRule.MessageTemplate);
        Assert.Equal(["solo"], onlyRule.RequiredTags);
        Assert.Equal(4, onlyRule.ConnectionDelaySeconds);
    }

    [Fact]
    public async Task GetEffectiveSettingsAsync_WhenValidationFails_FailsClosed()
    {
        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto(WelcomeMessageSettingsConstants.Namespace,
                    /*lang=json,strict*/
                                         "{\"schemaVersion\":999}")
            ])));

        var sut = CreateSut();

        var result = await sut.GetEffectiveSettingsAsync(ServerId);

        Assert.False(result.Enabled);
        Assert.True(result.ValidationFailed);
        Assert.Empty(result.Rules);
    }

    [Fact]
    public async Task GetEffectiveSettingsAsync_WhenGlobalFetchFails_FailsClosed()
    {
        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<ConfigurationDto>>(HttpStatusCode.InternalServerError));

        var sut = CreateSut();

        var result = await sut.GetEffectiveSettingsAsync(ServerId);

        Assert.False(result.Enabled);
        Assert.True(result.ValidationFailed);
        Assert.Empty(result.Rules);
    }

    [Fact]
    public async Task GetEffectiveSettingsAsync_WhenGlobalJsonIsMalformed_FailsClosed()
    {
        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto(WelcomeMessageSettingsConstants.Namespace,
                    "{\"schemaVersion\":1,\"rules\":[")
            ])));

        var sut = CreateSut();

        var result = await sut.GetEffectiveSettingsAsync(ServerId);

        Assert.False(result.Enabled);
        Assert.True(result.ValidationFailed);
        Assert.Empty(result.Rules);
    }

    [Fact]
    public async Task GetEffectiveSettingsAsync_WhenServerFetchFails_FailsClosed()
    {
        _serverConfigsApi
            .Setup(x => x.GetConfigurations(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<ConfigurationDto>>(HttpStatusCode.InternalServerError));

        var sut = CreateSut();

        var result = await sut.GetEffectiveSettingsAsync(ServerId);

        Assert.False(result.Enabled);
        Assert.True(result.ValidationFailed);
        Assert.Empty(result.Rules);
    }

    [Fact]
    public async Task GetEffectiveSettingsAsync_WhenServerJsonIsMalformed_FailsClosed()
    {
        _serverConfigsApi
            .Setup(x => x.GetConfigurations(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto(WelcomeMessageSettingsConstants.Namespace,
                    "{\"schemaVersion\":1,\"rules\":[")
            ])));

        var sut = CreateSut();

        var result = await sut.GetEffectiveSettingsAsync(ServerId);

        Assert.False(result.Enabled);
        Assert.True(result.ValidationFailed);
        Assert.Empty(result.Rules);
    }

    private WelcomeMessageSettingsProvider CreateSut()
        => new(
            _repositoryClient.Object,
            _memoryCache,
            new WelcomeMessageSettingsValidator(),
            new WelcomeMessageSettingsMerger(),
            _logger.Object);

    private static ApiResult<CollectionModel<ConfigurationDto>> SuccessResult(CollectionModel<ConfigurationDto> data)
        => new(HttpStatusCode.OK, new ApiResponse<CollectionModel<ConfigurationDto>>(data));

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
