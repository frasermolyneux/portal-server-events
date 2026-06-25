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

public class ChatCommandSettingsProviderTests
{
    private static readonly Guid ServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly Mock<IRepositoryApiClient> _repositoryClient = new();
    private readonly Mock<IVersionedGlobalConfigurationsApi> _versionedGlobalConfigs = new();
    private readonly Mock<IGlobalConfigurationsApi> _globalConfigsApi = new();
    private readonly Mock<IVersionedGameServerConfigurationsApi> _versionedServerConfigs = new();
    private readonly Mock<IGameServerConfigurationsApi> _serverConfigsApi = new();
    private readonly IMemoryCache _memoryCache = new MemoryCache(new MemoryCacheOptions());
    private readonly Mock<ILogger<ChatCommandSettingsProvider>> _logger = new();

    public ChatCommandSettingsProviderTests()
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
    public async Task GetEffectiveSettingsAsync_WhenNoConfig_UsesHardcodedDefaults()
    {
        var sut = CreateSut();

        var result = await sut.GetEffectiveSettingsAsync(ServerId, "register", isMutating: true);

        Assert.True(result.Enabled);
        Assert.Equal(ChatCommandSettingsConstants.HardcodedMutatingFreshnessSeconds, result.FreshnessSeconds);
        Assert.Equal(SettingsValueSource.Hardcoded, result.EnabledSource);
    }

    [Fact]
    public async Task GetEffectiveSettingsAsync_WhenServerOverridePresent_ServerWins()
    {
        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto(ChatCommandSettingsConstants.Namespace,
                    /*lang=json,strict*/
                                         "{\"schemaVersion\":1,\"defaults\":{\"enabled\":true,\"freshnessSeconds\":{\"readOnly\":7}},\"commands\":{\"fu\":{\"enabled\":true,\"freshnessSeconds\":6}}}")
            ])));

        _serverConfigsApi
            .Setup(x => x.GetConfigurations(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto(ChatCommandSettingsConstants.Namespace,
                    /*lang=json,strict*/
                                         "{\"schemaVersion\":1,\"commands\":{\"fu\":{\"enabled\":false,\"freshnessSeconds\":2}}}")
            ])));

        var sut = CreateSut();

        var result = await sut.GetEffectiveSettingsAsync(ServerId, "fu", isMutating: false);

        Assert.False(result.Enabled);
        Assert.Equal(2, result.FreshnessSeconds);
        Assert.Equal(SettingsValueSource.ServerCommand, result.EnabledSource);
        Assert.Equal(SettingsValueSource.ServerCommand, result.FreshnessSource);
    }

    [Fact]
    public async Task GetEffectiveSettingsAsync_WhenUsingFixtures_LocksCurrentMergeAndReadBehavior()
    {
        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto(ChatCommandSettingsConstants.Namespace,
                    SettingsFixtureLoader.LoadSettings("chatCommands.global.json"))
            ])));

        _serverConfigsApi
            .Setup(x => x.GetConfigurations(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto(ChatCommandSettingsConstants.Namespace,
                    SettingsFixtureLoader.LoadSettings("chatCommands.server.json"))
            ])));

        var sut = CreateSut();

        var register = await sut.GetEffectiveSettingsAsync(ServerId, "register", isMutating: true);
        Assert.True(register.Enabled);
        Assert.Equal(2, register.FreshnessSeconds);
        Assert.Equal(SettingsValueSource.ServerCommand, register.EnabledSource);
        Assert.Equal(SettingsValueSource.ServerCommand, register.FreshnessSource);
        Assert.Equal(SettingsValueSource.ServerCommand, register.AuthorizationSource);
        Assert.Equal(SettingsValueSource.ServerCommand, register.PayloadSource);
        Assert.Equal(["event-admin"], register.RequiredTags);
        Assert.True(register.Settings.HasValue);
        Assert.Equal("server-override", register.Settings.Value.GetProperty("mode").GetString());

        var whoAmI = await sut.GetEffectiveSettingsAsync(ServerId, "whoami", isMutating: false);
        Assert.True(whoAmI.Enabled);
        Assert.Equal(6, whoAmI.FreshnessSeconds);
        Assert.Equal(SettingsValueSource.GlobalCommand, whoAmI.EnabledSource);
        Assert.Equal(SettingsValueSource.GlobalCommand, whoAmI.FreshnessSource);

        var unknown = await sut.GetEffectiveSettingsAsync(ServerId, "unknown", isMutating: false);
        Assert.True(unknown.Enabled);
        Assert.Equal(8, unknown.FreshnessSeconds);
        Assert.Equal(SettingsValueSource.GlobalDefaults, unknown.EnabledSource);
        Assert.Equal(SettingsValueSource.GlobalDefaults, unknown.FreshnessSource);
    }

    [Fact]
    public async Task GetEffectiveSettingsAsync_WhenValidationFails_FailsClosed()
    {
        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto(ChatCommandSettingsConstants.Namespace,
                    /*lang=json,strict*/
                                         "{\"schemaVersion\":999}")
            ])));

        var sut = CreateSut();

        var result = await sut.GetEffectiveSettingsAsync(ServerId, "fu", isMutating: false);

        Assert.False(result.Enabled);
        Assert.True(result.ValidationFailed);
        Assert.Equal(SettingsValueSource.ValidationFailure, result.EnabledSource);
    }

    [Fact]
    public async Task GetEffectiveSettingsAsync_WhenGlobalJsonIsMalformed_FailsClosed()
    {
        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto(ChatCommandSettingsConstants.Namespace,
                    "{\"schemaVersion\":1")
            ])));

        var sut = CreateSut();

        var result = await sut.GetEffectiveSettingsAsync(ServerId, "register", isMutating: false);

        Assert.False(result.Enabled);
        Assert.True(result.ValidationFailed);
        Assert.Equal(SettingsValueSource.ValidationFailure, result.EnabledSource);
    }

    [Fact]
    public async Task GetEffectiveSettingsAsync_WhenServerJsonIsMalformed_FailsClosed()
    {
        _serverConfigsApi
            .Setup(x => x.GetConfigurations(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto(ChatCommandSettingsConstants.Namespace,
                    "{\"schemaVersion\":1")
            ])));

        var sut = CreateSut();

        var result = await sut.GetEffectiveSettingsAsync(ServerId, "register", isMutating: false);

        Assert.False(result.Enabled);
        Assert.True(result.ValidationFailed);
        Assert.Equal(SettingsValueSource.ValidationFailure, result.EnabledSource);
    }

    [Fact]
    public async Task GetEffectiveSettingsAsync_WhenGlobalFetchFails_FailsClosed()
    {
        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<ConfigurationDto>>(HttpStatusCode.InternalServerError));

        var sut = CreateSut();

        var result = await sut.GetEffectiveSettingsAsync(ServerId, "register", isMutating: false);

        Assert.False(result.Enabled);
        Assert.True(result.ValidationFailed);
        Assert.Equal(SettingsValueSource.ValidationFailure, result.EnabledSource);
    }

    [Fact]
    public async Task GetEffectiveSettingsAsync_WhenServerFetchFails_FailsClosed()
    {
        _serverConfigsApi
            .Setup(x => x.GetConfigurations(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<ConfigurationDto>>(HttpStatusCode.InternalServerError));

        var sut = CreateSut();

        var result = await sut.GetEffectiveSettingsAsync(ServerId, "register", isMutating: false);

        Assert.False(result.Enabled);
        Assert.True(result.ValidationFailed);
        Assert.Equal(SettingsValueSource.ValidationFailure, result.EnabledSource);
    }

    [Fact]
    public async Task GetEffectiveSettingsAsync_WhenCancelled_Throws()
    {
        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var sut = CreateSut();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.GetEffectiveSettingsAsync(ServerId, "register", isMutating: false, new CancellationToken(true)));
    }

    [Fact]
    public async Task GetEffectiveSettingsAsync_WhenCached_DoesNotRefetch()
    {
        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto(ChatCommandSettingsConstants.Namespace,
                    /*lang=json,strict*/
                                         "{\"schemaVersion\":1,\"commands\":{\"register\":{\"enabled\":false}}}")
            ])));

        var sut = CreateSut();

        var first = await sut.GetEffectiveSettingsAsync(ServerId, "register", isMutating: false);
        var second = await sut.GetEffectiveSettingsAsync(ServerId, "register", isMutating: false);

        Assert.False(first.Enabled);
        Assert.False(second.Enabled);
        _globalConfigsApi.Verify(x => x.GetConfigurations(It.IsAny<CancellationToken>()), Times.Once);
        _serverConfigsApi.Verify(x => x.GetConfigurations(ServerId, It.IsAny<CancellationToken>()), Times.Once);
    }

    private ChatCommandSettingsProvider CreateSut()
        => new(
            _repositoryClient.Object,
            _memoryCache,
            new ChatCommandSettingsValidator(),
            new ChatCommandSettingsMerger(),
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
