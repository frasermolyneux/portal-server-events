using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.FeatureManagement;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.AdminActions;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Moderation;

using static XtremeIdiots.Portal.Server.Events.Processor.App.Tests.ServiceBusTestHelpers;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Moderation;

public class ChatModerationPipelineTests
{
    private readonly Mock<IChatModerationService> _contentSafety = new();
    private readonly Mock<IRepositoryApiClient> _repoClient = new();
    private readonly Mock<IVersionedAdminActionsApi> _versionedAdminActions = new();
    private readonly Mock<IAdminActionsApi> _adminActionsApi = new();
    private readonly Mock<IFeatureManager> _featureManager = new();
    private readonly Mock<ILogger<ChatModerationPipeline>> _logger = new();
    private readonly TelemetryClient _telemetry;
    private readonly Dictionary<string, string?> _configValues;
    private readonly IConfiguration _configuration;
    private readonly ChatModerationPipeline _sut;

    private static readonly Guid TestServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TestPlayerId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public ChatModerationPipelineTests()
    {
        _versionedAdminActions.Setup(x => x.V1).Returns(_adminActionsApi.Object);
        _repoClient.Setup(x => x.AdminActions).Returns(_versionedAdminActions.Object);

        _adminActionsApi
            .Setup(x => x.CreateAdminAction(It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        _featureManager
            .Setup(x => x.IsEnabledAsync("EventIngest.ChatToxicityDetection"))
            .ReturnsAsync(true);

        _configValues = new Dictionary<string, string?>
        {
            ["ContentSafety:MinMessageLength"] = "5",
            ["ContentSafety:NewPlayerWindowDays"] = "7",
            ["ContentSafety:SeverityThreshold"] = "4",
            ["ContentSafety:BotAdminId"] = "33333333-3333-3333-3333-333333333333"
        };

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(_configValues)
            .Build();

        _telemetry = new TelemetryClient(new Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration
        {
            TelemetryChannel = new Mock<ITelemetryChannel>().Object
        });

        _sut = new ChatModerationPipeline(
            _contentSafety.Object,
            _repoClient.Object,
            _configuration,
            _featureManager.Object,
            _telemetry,
            _logger.Object);
    }

    private static ModerationContext CreateContext(
        string? message = null,
        bool isNewPlayer = true,
        bool hasModerateChatTag = false) => new()
    {
        ServerId = TestServerId,
        GameType = "CallOfDuty4",
        PlayerGuid = "abc123guid",
        Username = "TestPlayer",
        Message = message ?? "This is a test message",
        PlayerId = TestPlayerId,
        PlayerFirstSeen = isNewPlayer ? DateTime.UtcNow.AddDays(-1) : DateTime.UtcNow.AddDays(-30),
        HasModerateChatTag = hasModerateChatTag
    };

    [Fact]
    public async Task RunAsync_WhenFeatureDisabled_DoesNothing()
    {
        _featureManager
            .Setup(x => x.IsEnabledAsync("EventIngest.ChatToxicityDetection"))
            .ReturnsAsync(false);

        await _sut.RunAsync(CreateContext());

        _contentSafety.Verify(x => x.AnalyseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_ShortMessage_Skipped()
    {
        await _sut.RunAsync(CreateContext(message: "hi"));

        _contentSafety.Verify(x => x.AnalyseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_QuickMessage_Skipped()
    {
        await _sut.RunAsync(CreateContext(message: "QUICKMESSAGE_hello"));

        _contentSafety.Verify(x => x.AnalyseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_ContentSafety_AboveThreshold_CreatesObservation()
    {
        _contentSafety
            .Setup(x => x.AnalyseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatModerationResult(4, 0, 0, 2, 4, "Hate"));

        await _sut.RunAsync(CreateContext(isNewPlayer: true));

        _adminActionsApi.Verify(x => x.CreateAdminAction(
            It.Is<CreateAdminActionDto>(dto =>
                dto.PlayerId == TestPlayerId &&
                dto.Type == Repository.Abstractions.Constants.V1.AdminActionType.Observation &&
                dto.Text.Contains("AI Content Safety")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_ContentSafety_BelowThreshold_NoAction()
    {
        _contentSafety
            .Setup(x => x.AnalyseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatModerationResult(2, 0, 0, 0, 2, "Hate"));

        await _sut.RunAsync(CreateContext(isNewPlayer: true));

        _adminActionsApi.Verify(x => x.CreateAdminAction(
            It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_OnlyNewOrTaggedPlayers_GetContentSafetyCheck()
    {
        // Old player, no tag — should not call content safety
        await _sut.RunAsync(CreateContext(isNewPlayer: false, hasModerateChatTag: false));

        _contentSafety.Verify(x => x.AnalyseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // Old player with tag — should call content safety
        _contentSafety
            .Setup(x => x.AnalyseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatModerationResult(0, 0, 0, 0, 0, "Hate"));

        await _sut.RunAsync(CreateContext(isNewPlayer: false, hasModerateChatTag: true));

        _contentSafety.Verify(x => x.AnalyseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_OnException_DoesNotThrow()
    {
        _contentSafety.Setup(x => x.AnalyseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        // Should not throw
        await _sut.RunAsync(CreateContext());
    }
}
