using System.Net;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class FuMessageSettingsProviderTests
{
    private static readonly Guid ServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly Mock<IRepositoryApiClient> _repositoryClient = new();
    private readonly Mock<IVersionedGlobalConfigurationsApi> _versionedGlobalConfigs = new();
    private readonly Mock<IGlobalConfigurationsApi> _globalConfigsApi = new();
    private readonly Mock<IVersionedGameServerConfigurationsApi> _versionedServerConfigs = new();
    private readonly Mock<IGameServerConfigurationsApi> _serverConfigsApi = new();
    private readonly IMemoryCache _memoryCache = new MemoryCache(new MemoryCacheOptions());
    private readonly Mock<ILogger<FuMessageSettingsProvider>> _logger = new();

    public FuMessageSettingsProviderTests()
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
    public async Task IsEnabledAsync_WhenGlobalHasNoUsableMessages_ReturnsFalse()
    {
        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto("funnyMessages", "{\"messages\":[{\"message\":\"\",\"enabled\":true}]}")
            ])));

        var sut = CreateSut();

        var enabled = await sut.IsEnabledAsync(ServerId);

        Assert.False(enabled);
    }

    [Fact]
    public async Task GetEffectiveMessagesAsync_WhenServerMissing_UsesGlobalMessages()
    {
        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto("funnyMessages", "{\"messages\":[{\"message\":\"^1FU^7 {name}\",\"enabled\":true}]}")
            ])));

        _serverConfigsApi
            .Setup(x => x.GetConfigurations(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(Array.Empty<ConfigurationDto>())));

        var sut = CreateSut();

        var messages = await sut.GetEffectiveMessagesAsync(ServerId);

        Assert.Single(messages);
        Assert.Equal("^1FU^7 {name}", messages[0]);
    }

    [Fact]
    public async Task GetEffectiveMessagesAsync_WhenServerMessagesEmpty_FallsBackToGlobal()
    {
        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto("funnyMessages", "{\"messages\":[{\"message\":\"global-{name}\",\"enabled\":true}]}")
            ])));

        _serverConfigsApi
            .Setup(x => x.GetConfigurations(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto("funnyMessages", "{\"messages\":[]}")
            ])));

        var sut = CreateSut();

        var messages = await sut.GetEffectiveMessagesAsync(ServerId);

        Assert.Single(messages);
        Assert.Equal("global-{name}", messages[0]);
    }

    [Fact]
    public async Task GetEffectiveMessagesAsync_WhenServerHasEnabledMessages_OverridesGlobal()
    {
        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto("funnyMessages", "{\"messages\":[{\"message\":\"global-{name}\",\"enabled\":true}]}")
            ])));

        _serverConfigsApi
            .Setup(x => x.GetConfigurations(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto("funnyMessages", "{\"messages\":[{\"message\":\"server-{name}\",\"enabled\":true}]}")
            ])));

        var sut = CreateSut();

        var messages = await sut.GetEffectiveMessagesAsync(ServerId);

        Assert.Single(messages);
        Assert.Equal("server-{name}", messages[0]);
    }

    [Fact]
    public async Task IsEnabledAsync_WhenGlobalMissingButServerHasMessages_ReturnsFalse()
    {
        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(Array.Empty<ConfigurationDto>())));

        _serverConfigsApi
            .Setup(x => x.GetConfigurations(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto("funnyMessages", "{\"messages\":[{\"message\":\"server-{name}\",\"enabled\":true}]}")
            ])));

        var sut = CreateSut();

        var enabled = await sut.IsEnabledAsync(ServerId);
        var messages = await sut.GetEffectiveMessagesAsync(ServerId);

        Assert.False(enabled);
        Assert.Empty(messages);
    }

    [Fact]
    public async Task IsEnabledAsync_WhenFirstCallThrows_DoesNotCacheDisabledState()
    {
        _globalConfigsApi
            .SetupSequence(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transient"))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto("funnyMessages", "{\"messages\":[{\"message\":\"global-{name}\",\"enabled\":true}]}")
            ])));

        var sut = CreateSut();

        var firstAttempt = await sut.IsEnabledAsync(ServerId);
        var secondAttempt = await sut.IsEnabledAsync(ServerId);

        Assert.False(firstAttempt);
        Assert.True(secondAttempt);

        _globalConfigsApi.Verify(x => x.GetConfigurations(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private FuMessageSettingsProvider CreateSut()
        => new(_repositoryClient.Object, _memoryCache, _logger.Object);

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
