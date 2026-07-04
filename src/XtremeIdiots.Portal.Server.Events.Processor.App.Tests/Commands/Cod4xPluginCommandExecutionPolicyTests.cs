using System.Net;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Cod4xPlugin;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class Cod4xPluginCommandExecutionPolicyTests
{
    private readonly Mock<IRepositoryApiClient> _repositoryApiClient = new();
    private readonly Mock<IVersionedGlobalConfigurationsApi> _versionedGlobalConfigurationsApi = new();
    private readonly Mock<IGlobalConfigurationsApi> _globalConfigurationsApi = new();
    private readonly Mock<IVersionedGameServerConfigurationsApi> _versionedGameServerConfigurationsApi = new();
    private readonly Mock<IGameServerConfigurationsApi> _gameServerConfigurationsApi = new();

    private readonly Mock<IServersApiClient> _serversApiClient = new();
    private readonly Mock<IVersionedCoD4xRconApi> _versionedCod4xRconApi = new();
    private readonly Mock<ICoD4xRconApi> _cod4xRconApi = new();

    private readonly IMemoryCache _cache;
    private readonly ICommandParser _commandParser = new ChatCommandParser();
    private readonly Mock<ILogger<Cod4xPluginCommandExecutionPolicy>> _logger = new();

    private readonly Cod4xPluginCommandExecutionPolicy _sut;

    private static readonly Guid TestServerId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    public Cod4xPluginCommandExecutionPolicyTests()
    {
        _versionedGlobalConfigurationsApi.Setup(x => x.V1).Returns(_globalConfigurationsApi.Object);
        _repositoryApiClient.Setup(x => x.GlobalConfigurations).Returns(_versionedGlobalConfigurationsApi.Object);

        _versionedGameServerConfigurationsApi.Setup(x => x.V1).Returns(_gameServerConfigurationsApi.Object);
        _repositoryApiClient.Setup(x => x.GameServerConfigurations).Returns(_versionedGameServerConfigurationsApi.Object);

        _versionedCod4xRconApi.Setup(x => x.V1).Returns(_cod4xRconApi.Object);
        _serversApiClient.Setup(x => x.CoD4xRcon).Returns(_versionedCod4xRconApi.Object);

        _globalConfigurationsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessCollectionResult([]));

        _gameServerConfigurationsApi
            .Setup(x => x.GetConfigurations(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessCollectionResult([]));

        _cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

        _sut = new Cod4xPluginCommandExecutionPolicy(
            _repositoryApiClient.Object,
            _serversApiClient.Object,
            _cache,
            _commandParser,
            _logger.Object);
    }

    [Fact]
    public async Task ShouldSkipBackendExecutionAsync_WhenGameTypeIsNotCod4x_ReturnsFalse()
    {
        var result = await _sut.ShouldSkipBackendExecutionAsync(
            TestServerId,
            "CallOfDuty4",
            "!commands",
            CancellationToken.None);

        Assert.False(result);

        _globalConfigurationsApi.Verify(x => x.GetConfigurations(It.IsAny<CancellationToken>()), Times.Never);
        _cod4xRconApi.Verify(x => x.AdminListCommands(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ShouldSkipBackendExecutionAsync_WhenPluginSourceDisabled_ReturnsFalse()
    {
        var result = await _sut.ShouldSkipBackendExecutionAsync(
            TestServerId,
            "CallOfDuty4x",
            "!commands",
            CancellationToken.None);

        Assert.False(result);
        _cod4xRconApi.Verify(x => x.AdminListCommands(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ShouldSkipBackendExecutionAsync_WhenPluginEnabledButMessageIsNotCommand_ReturnsFalse()
    {
        _globalConfigurationsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessCollectionResult([
                CreateConfigurationDto(
                    Cod4xPluginSettingsConstants.Namespace,
                    /*lang=json,strict*/ """
                    {
                      "schemaVersion": 1,
                      "enabled": true
                    }
                    """)
            ]));

        var result = await _sut.ShouldSkipBackendExecutionAsync(
            TestServerId,
            "CallOfDuty4x",
            "hello world",
            CancellationToken.None);

        Assert.False(result);
        _cod4xRconApi.Verify(x => x.AdminListCommands(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ShouldSkipBackendExecutionAsync_WhenPluginEnabledAndCommandIsEnabledOnServer_ReturnsTrue()
    {
        _globalConfigurationsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessCollectionResult([
                CreateConfigurationDto(
                    Cod4xPluginSettingsConstants.Namespace,
                    /*lang=json,strict*/ """
                    {
                      "schemaVersion": 1,
                      "enabled": true
                    }
                    """)
            ]));

        _cod4xRconApi
            .Setup(x => x.AdminListCommands(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                HttpStatusCode.OK,
                new ApiResponse<string>("^2commands               40\n^7kick                   35")));

        var result = await _sut.ShouldSkipBackendExecutionAsync(
            TestServerId,
            "CallOfDuty4x",
            "!commands",
            CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task ShouldSkipBackendExecutionAsync_WhenPluginEnabledButCommandIsNotEnabledOnServer_ReturnsFalse()
    {
        _globalConfigurationsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessCollectionResult([
                CreateConfigurationDto(
                    Cod4xPluginSettingsConstants.Namespace,
                    /*lang=json,strict*/ """
                    {
                      "schemaVersion": 1,
                      "enabled": true
                    }
                    """)
            ]));

        _cod4xRconApi
            .Setup(x => x.AdminListCommands(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                HttpStatusCode.OK,
                new ApiResponse<string>("kick                   35")));

        var result = await _sut.ShouldSkipBackendExecutionAsync(
            TestServerId,
            "CallOfDuty4x",
            "!commands",
            CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task ShouldSkipBackendExecutionAsync_CachesAdminListCommandsToAvoidRconSpam()
    {
        _globalConfigurationsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessCollectionResult([
                CreateConfigurationDto(
                    Cod4xPluginSettingsConstants.Namespace,
                    /*lang=json,strict*/ """
                    {
                      "schemaVersion": 1,
                      "enabled": true
                    }
                    """)
            ]));

        _cod4xRconApi
            .Setup(x => x.AdminListCommands(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                HttpStatusCode.OK,
                new ApiResponse<string>("commands               40")));

        var first = await _sut.ShouldSkipBackendExecutionAsync(
            TestServerId,
            "CallOfDuty4x",
            "!commands",
            CancellationToken.None);

        var second = await _sut.ShouldSkipBackendExecutionAsync(
            TestServerId,
            "CallOfDuty4x",
            "!commands",
            CancellationToken.None);

        Assert.True(first);
        Assert.True(second);

        _cod4xRconApi.Verify(x => x.AdminListCommands(TestServerId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ShouldSkipBackendExecutionAsync_WhenAdminListCommandsFails_ReturnsFalse()
    {
        _globalConfigurationsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessCollectionResult([
                CreateConfigurationDto(
                    Cod4xPluginSettingsConstants.Namespace,
                    /*lang=json,strict*/ """
                    {
                      "schemaVersion": 1,
                      "enabled": true
                    }
                    """)
            ]));

        _cod4xRconApi
            .Setup(x => x.AdminListCommands(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(HttpStatusCode.InternalServerError));

        var result = await _sut.ShouldSkipBackendExecutionAsync(
            TestServerId,
            "CallOfDuty4x",
            "!commands",
            CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task ShouldSkipBackendExecutionAsync_WhenAdminListCommandsThrows_ReturnsFalse()
    {
        _globalConfigurationsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessCollectionResult([
                CreateConfigurationDto(
                    Cod4xPluginSettingsConstants.Namespace,
                    /*lang=json,strict*/ """
                    {
                      "schemaVersion": 1,
                      "enabled": true
                    }
                    """)
            ]));

        _cod4xRconApi
            .Setup(x => x.AdminListCommands(TestServerId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("rcon unavailable"));

        var result = await _sut.ShouldSkipBackendExecutionAsync(
            TestServerId,
            "CallOfDuty4x",
            "!commands",
            CancellationToken.None);

        Assert.False(result);
    }

    private static ApiResult<CollectionModel<ConfigurationDto>> SuccessCollectionResult(
        IReadOnlyCollection<ConfigurationDto> items) =>
        new(HttpStatusCode.OK, new ApiResponse<CollectionModel<ConfigurationDto>>(new CollectionModel<ConfigurationDto>(items)));

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
