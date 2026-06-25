using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Moderation;

using static XtremeIdiots.Portal.Server.Events.Processor.App.Tests.ServiceBusTestHelpers;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Moderation;

public class ChatModerationSettingsProviderTests
{
    private readonly Mock<IRepositoryApiClient> _repositoryClient = new();
    private readonly Mock<IVersionedGlobalConfigurationsApi> _versionedGlobalConfigs = new();
    private readonly Mock<IGlobalConfigurationsApi> _globalConfigsApi = new();
    private readonly Mock<IVersionedGameServerConfigurationsApi> _versionedServerConfigs = new();
    private readonly Mock<IGameServerConfigurationsApi> _serverConfigsApi = new();
    private readonly Mock<ILogger<ChatModerationSettingsProvider>> _logger = new();
    private readonly IMemoryCache _memoryCache;

    private static readonly Guid ServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public ChatModerationSettingsProviderTests()
    {
        _memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

        _versionedGlobalConfigs.Setup(x => x.V1).Returns(_globalConfigsApi.Object);
        _repositoryClient.Setup(x => x.GlobalConfigurations).Returns(_versionedGlobalConfigs.Object);

        _versionedServerConfigs.Setup(x => x.V1).Returns(_serverConfigsApi.Object);
        _repositoryClient.Setup(x => x.GameServerConfigurations).Returns(_versionedServerConfigs.Object);
    }

    [Fact]
    public async Task GetForServerAsync_GlobalAndServerConfigs_AreMergedWithServerPrecedence()
    {
        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfig("moderation", /*lang=json,strict*/ "{\"minMessageLength\":9,\"contentSafetyHateSeverityThreshold\":2,\"contentSafetyViolenceSeverityThreshold\":3,\"contentSafetySexualSeverityThreshold\":4,\"contentSafetySelfHarmSeverityThreshold\":5}")
            ])));

        _serverConfigsApi
            .Setup(x => x.GetConfigurations(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfig("moderation", /*lang=json,strict*/ "{\"contentSafetyHateSeverityThreshold\":6,\"contentSafetyViolenceSeverityThreshold\":-1}")
            ])));

        var sut = CreateSut();

        var result = await sut.GetForServerAsync(ServerId);

        Assert.Equal(9, result.MinMessageLength);
        Assert.Equal(6, result.HateSeverityThreshold);
        Assert.Null(result.ViolenceSeverityThreshold);
        Assert.Equal(4, result.SexualSeverityThreshold);
        Assert.Equal(5, result.SelfHarmSeverityThreshold);
    }

    [Fact]
    public async Task GetForServerAsync_WhenOnlyLegacyThresholdIsConfigured_UsesCategoryDefaults()
    {
        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfig("moderation", /*lang=json,strict*/ "{\"minMessageLength\":9,\"contentSafetySeverityThreshold\":1}")
            ])));

        _serverConfigsApi
            .Setup(x => x.GetConfigurations(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>([])));

        var sut = CreateSut();

        var result = await sut.GetForServerAsync(ServerId);

        Assert.Equal(9, result.MinMessageLength);
        Assert.Equal(4, result.HateSeverityThreshold);
        Assert.Equal(4, result.ViolenceSeverityThreshold);
        Assert.Equal(4, result.SexualSeverityThreshold);
        Assert.Equal(4, result.SelfHarmSeverityThreshold);
    }

    [Fact]
    public async Task GetForServerAsync_WhenConfigApisFail_UsesSafeConfigurationDefaults()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["ContentSafety:MinMessageLength"] = "7",
            ["ContentSafety:HateSeverityThreshold"] = "2",
            ["ContentSafety:ViolenceSeverityThreshold"] = "3",
            ["ContentSafety:SexualSeverityThreshold"] = "4",
            ["ContentSafety:SelfHarmSeverityThreshold"] = "5"
        });

        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        _serverConfigsApi
            .Setup(x => x.GetConfigurations(ServerId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var sut = CreateSut(configuration);

        var result = await sut.GetForServerAsync(ServerId);

        Assert.Equal(7, result.MinMessageLength);
        Assert.Equal(2, result.HateSeverityThreshold);
        Assert.Equal(3, result.ViolenceSeverityThreshold);
        Assert.Equal(4, result.SexualSeverityThreshold);
        Assert.Equal(5, result.SelfHarmSeverityThreshold);
    }

    [Fact]
    public async Task GetForServerAsync_OutOfRangeThresholds_FallBackToDefaults()
    {
        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfig("moderation", /*lang=json,strict*/ "{\"contentSafetyHateSeverityThreshold\":99,\"contentSafetyViolenceSeverityThreshold\":-5,\"contentSafetySexualSeverityThreshold\":-1,\"contentSafetySelfHarmSeverityThreshold\":0}")
            ])));

        _serverConfigsApi
            .Setup(x => x.GetConfigurations(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>([])));

        var sut = CreateSut();

        var result = await sut.GetForServerAsync(ServerId);

        Assert.Equal(4, result.HateSeverityThreshold);
        Assert.Equal(4, result.ViolenceSeverityThreshold);
        Assert.Equal(4, result.SexualSeverityThreshold);
        Assert.Equal(4, result.SelfHarmSeverityThreshold);
    }

    private ChatModerationSettingsProvider CreateSut(IConfiguration? configuration = null)
    {
        return new ChatModerationSettingsProvider(
            _repositoryClient.Object,
            configuration ?? BuildConfiguration(),
            _memoryCache,
            _logger.Object);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?>? values = null)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values ?? new Dictionary<string, string?>
            {
                ["ContentSafety:MinMessageLength"] = "5",
                ["ContentSafety:HateSeverityThreshold"] = "4",
                ["ContentSafety:ViolenceSeverityThreshold"] = "4",
                ["ContentSafety:SexualSeverityThreshold"] = "4",
                ["ContentSafety:SelfHarmSeverityThreshold"] = "4"
            })
            .Build();
    }

    private static ConfigurationDto CreateConfig(string ns, string configuration)
    {
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            Namespace = ns,
            Configuration = configuration,
            LastModifiedUtc = DateTime.UtcNow
        });

        return Newtonsoft.Json.JsonConvert.DeserializeObject<ConfigurationDto>(json)!;
    }
}