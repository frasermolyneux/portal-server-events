using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class FuCommandTests
{
    private static readonly Guid TestServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly Mock<IChatCommandSettingsProvider> _settingsProvider = new();
    private readonly FuMessageTemplateRenderer _templateRenderer = new();
    private readonly Mock<IServersApiClient> _serversClient = new();
    private readonly Mock<IVersionedCoD4xRconApi> _versionedCoD4xRcon = new();
    private readonly Mock<ICoD4xRconApi> _coD4xRconApi = new();
    private readonly Mock<IRepositoryApiClient> _repositoryClient = new();
    private readonly Mock<IVersionedGlobalConfigurationsApi> _versionedGlobalConfigs = new();
    private readonly Mock<IGlobalConfigurationsApi> _globalConfigsApi = new();
    private readonly Mock<IVersionedGameServerConfigurationsApi> _versionedServerConfigs = new();
    private readonly Mock<IGameServerConfigurationsApi> _serverConfigsApi = new();
    private readonly Mock<IRconResponseService> _rconResponseService = new();
    private readonly Mock<ILogger<FuCommand>> _logger = new();

    private readonly FuCommand _sut;

    public FuCommandTests()
    {
        _versionedCoD4xRcon.Setup(x => x.V1).Returns(_coD4xRconApi.Object);
        _serversClient.Setup(x => x.CoD4xRcon).Returns(_versionedCoD4xRcon.Object);

        _versionedGlobalConfigs.Setup(x => x.V1).Returns(_globalConfigsApi.Object);
        _repositoryClient.Setup(x => x.GlobalConfigurations).Returns(_versionedGlobalConfigs.Object);

        _versionedServerConfigs.Setup(x => x.V1).Returns(_serverConfigsApi.Object);
        _repositoryClient.Setup(x => x.GameServerConfigurations).Returns(_versionedServerConfigs.Object);

        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto("agent", /*lang=json,strict*/ "{\"agentName\":\"^5[GlobalBot]^7\"}")
            ])));

        _serverConfigsApi
            .Setup(x => x.GetConfigurations(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto("agent", /*lang=json,strict*/ "{\"agentName\":\"^2[ServerBot]^7\"}")
            ])));

        _settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(TestServerId, "fu", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateEnabledFuSettings(["^1FU^7 {name}"]));

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

        _rconResponseService
            .Setup(x => x.TrySayAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _sut = new FuCommand(
            _settingsProvider.Object,
            _templateRenderer,
            _serversClient.Object,
            _repositoryClient.Object,
            _rconResponseService.Object,
            _logger.Object);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoArgument_ReturnsUsageAndSendsPrivateTell()
    {
        var result = await _sut.ExecuteAsync(CreateContext("!fu"));

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Equal("Usage: !fu <player name>", result.ResponseMessage);

        _rconResponseService.Verify(x => x.TryTellAsync(
            TestServerId,
            "abc123",
            3,
            "Usage: !fu <player name>",
            "Issuer",
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);

        _rconResponseService.Verify(x => x.TrySayAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSettingsHaveNoUsableMessages_ReturnsNotHandled()
    {
        _settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(TestServerId, "fu", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EffectiveChatCommandSettings
            {
                CommandName = "fu",
                Enabled = true,
                FreshnessSeconds = 5,
                Settings = JsonSerializer.SerializeToElement(new { messages = Array.Empty<object>() }),
                EnabledSource = SettingsValueSource.ServerCommand,
                FreshnessSource = SettingsValueSource.ServerCommand,
                AuthorizationSource = SettingsValueSource.ServerCommand,
                PayloadSource = SettingsValueSource.ServerCommand
            });

        var result = await _sut.ExecuteAsync(CreateContext("!fu target"));

        Assert.False(result.Handled);

        _rconResponseService.Verify(x => x.TrySayAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        _rconResponseService.Verify(x => x.TryTellAsync(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenMessagesInvalidOrDisabled_ReturnsNotHandled()
    {
        _settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(TestServerId, "fu", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EffectiveChatCommandSettings
            {
                CommandName = "fu",
                Enabled = true,
                FreshnessSeconds = 5,
                Settings = JsonSerializer.SerializeToElement(new
                {
                    messages = new object[]
                    {
                        new { message = "", enabled = true },
                        new { message = "valid-but-disabled", enabled = false },
                        new { message = "invalid-enabled", enabled = "nope" }
                    }
                }),
                EnabledSource = SettingsValueSource.ServerCommand,
                FreshnessSource = SettingsValueSource.ServerCommand,
                AuthorizationSource = SettingsValueSource.ServerCommand,
                PayloadSource = SettingsValueSource.ServerCommand
            });

        var result = await _sut.ExecuteAsync(CreateContext("!fu target"));

        Assert.False(result.Handled);
        _rconResponseService.Verify(x => x.TrySayAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        _rconResponseService.Verify(x => x.TryTellAsync(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenResolved_SendsPublicSayWithRenderedName()
    {
        _coD4xRconApi
            .Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(HttpStatusCode.OK, new ApiResponse<CoD4xStatusResponseDto>(new CoD4xStatusResponseDto
            {
                Players =
                [
                    new CoD4xStatusPlayerDto
                    {
                        Num = 4,
                        PlayerIdentifier = "target-guid",
                        Name = "^3Target^7"
                    }
                ]
            })));

        var result = await _sut.ExecuteAsync(CreateContext("!fu target"));

        Assert.True(result.Handled);
        Assert.True(result.Success);
        Assert.Equal("^2[ServerBot]^7 ^1FU^7 ^3Target^7", result.ResponseMessage);

        _rconResponseService.Verify(x => x.TrySayAsync(
            TestServerId,
            "^2[ServerBot]^7 ^1FU^7 ^3Target^7",
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

    [Fact]
    public async Task ExecuteAsync_WhenTemplateHasNoToken_LeavesTemplateUnchanged()
    {
        _settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(TestServerId, "fu", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateEnabledFuSettings(["owned"]));

        _coD4xRconApi
            .Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(HttpStatusCode.OK, new ApiResponse<CoD4xStatusResponseDto>(new CoD4xStatusResponseDto
            {
                Players =
                [
                    new CoD4xStatusPlayerDto
                    {
                        Num = 4,
                        PlayerIdentifier = "target-guid",
                        Name = "Target"
                    }
                ]
            })));

        var result = await _sut.ExecuteAsync(CreateContext("!fu target"));

        Assert.True(result.Success);
        Assert.Equal("^2[ServerBot]^7 owned", result.ResponseMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNotFound_SendsPrivateTellOnly()
    {
        _coD4xRconApi
            .Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(HttpStatusCode.OK, new ApiResponse<CoD4xStatusResponseDto>(new CoD4xStatusResponseDto
            {
                Players = []
            })));

        var result = await _sut.ExecuteAsync(CreateContext("!fu target"));

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Equal("No player found.", result.ResponseMessage);

        _rconResponseService.Verify(x => x.TryTellAsync(
            TestServerId,
            "abc123",
            3,
            "No player found.",
            "Issuer",
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);

        _rconResponseService.Verify(x => x.TrySayAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAmbiguous_SendsPrivateTellWithSuggestionsOnly()
    {
        _coD4xRconApi
            .Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(HttpStatusCode.OK, new ApiResponse<CoD4xStatusResponseDto>(new CoD4xStatusResponseDto
            {
                Players =
                [
                    new CoD4xStatusPlayerDto { Num = 3, PlayerIdentifier = "g1", Name = "Name1" },
                    new CoD4xStatusPlayerDto { Num = 5, PlayerIdentifier = "g2", Name = "Name2" }
                ]
            })));

        var result = await _sut.ExecuteAsync(CreateContext("!fu name"));

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Equal("No exact player found. Did you mean: Name1 (slot 3), Name2 (slot 5)", result.ResponseMessage);

        _rconResponseService.Verify(x => x.TryTellAsync(
            TestServerId,
            "abc123",
            3,
            "No exact player found. Did you mean: Name1 (slot 3), Name2 (slot 5)",
            "Issuer",
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);

        _rconResponseService.Verify(x => x.TrySayAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenResolvePlayerThrows_SendsPrivateTellOnly()
    {
        _coD4xRconApi
            .Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("timeout"));

        var result = await _sut.ExecuteAsync(CreateContext("!fu target"));

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Equal("Unable to resolve player right now. Please try again.", result.ResponseMessage);

        _rconResponseService.Verify(x => x.TryTellAsync(
            TestServerId,
            "abc123",
            3,
            "Unable to resolve player right now. Please try again.",
            "Issuer",
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);

        _rconResponseService.Verify(x => x.TrySayAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static CommandContext CreateContext(string message) => new()
    {
        ServerId = TestServerId,
        GameType = "CallOfDuty4",
        PlayerGuid = "abc123",
        Username = "Issuer",
        SlotId = 3,
        Message = message,
        EventGeneratedUtc = DateTime.UtcNow,
        EventPublishedUtc = DateTime.UtcNow,
        SequenceId = 1,
        PlayerId = Guid.Parse("22222222-2222-2222-2222-222222222222")
    };

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

    private static EffectiveChatCommandSettings CreateEnabledFuSettings(IReadOnlyList<string> messages)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            messages = messages.Select(m => new { message = m, enabled = true }).ToArray()
        });

        return new EffectiveChatCommandSettings
        {
            CommandName = "fu",
            Enabled = true,
            FreshnessSeconds = 5,
            Settings = payload,
            EnabledSource = SettingsValueSource.ServerCommand,
            FreshnessSource = SettingsValueSource.ServerCommand,
            AuthorizationSource = SettingsValueSource.ServerCommand,
            PayloadSource = SettingsValueSource.ServerCommand
        };
    }
}
