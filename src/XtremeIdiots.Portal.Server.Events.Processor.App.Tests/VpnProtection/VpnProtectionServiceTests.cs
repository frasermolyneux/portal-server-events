using System.Net;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Moq;

using MX.Api.Abstractions;
using MX.GeoLocation.Abstractions.Models.V1_1;
using MX.Observability.ApplicationInsights.Auditing;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.AdminActions;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.GameServers;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Services;
using XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.VpnProtection;

public sealed class VpnProtectionServiceTests
{
    private static readonly Guid ServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PlayerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string BotAdminId = "33333333-3333-3333-3333-333333333333";

    private readonly Mock<IVpnProtectionSettingsProvider> settingsProvider = new();
    private readonly Mock<IVpnProtectionEvaluator> evaluator = new();
    private readonly Mock<IVpnProtectionRconEnforcer> rconEnforcer = new();
    private readonly Mock<IRepositoryApiClient> repositoryApiClient = new();
    private readonly Mock<IAdminActionsApi> adminActionsApi = new();
    private readonly Mock<IGameServersEventsApi> gameServerEventsApi = new();
    private readonly Mock<IAdminActionTopics> topics = new();

    public VpnProtectionServiceTests()
    {
        var versionedAdminActions = new Mock<IVersionedAdminActionsApi>();
        versionedAdminActions.Setup(x => x.V1).Returns(adminActionsApi.Object);
        repositoryApiClient.Setup(x => x.AdminActions).Returns(versionedAdminActions.Object);
        var versionedEvents = new Mock<IVersionedGameServersEventsApi>();
        versionedEvents.Setup(x => x.V1).Returns(gameServerEventsApi.Object);
        repositoryApiClient.Setup(x => x.GameServersEvents).Returns(versionedEvents.Object);

        settingsProvider.Setup(x => x.GetEffectiveSettingsAsync(ServerId, It.IsAny<CancellationToken>())).ReturnsAsync(new EffectiveVpnProtectionSettings { Enabled = true });
        rconEnforcer.Setup(x => x.EnforceAsync(It.IsAny<VpnProtectionContext>(), It.IsAny<VpnProtectionAction>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(VpnProtectionRconOutcome.Succeeded);
        adminActionsApi.Setup(x => x.EnsureAutomatedAction(It.IsAny<EnsureAutomatedActionDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(EnsureResult(true));
        adminActionsApi.Setup(x => x.UpdateAdminAction(It.IsAny<EditAdminActionDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ApiResult(HttpStatusCode.OK));
        topics.Setup(x => x.CreateTopicForAdminAction(It.IsAny<AdminActionType>(), It.IsAny<GameType>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(1234);
        gameServerEventsApi.Setup(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ApiResult(HttpStatusCode.Created));
    }

    [Fact]
    public async Task ProcessAsync_NewBan_EnsuresActionCreatesTopicAndMarksRconReason()
    {
        evaluator.Setup(x => x.Evaluate(It.IsAny<EffectiveVpnProtectionSettings>(), It.IsAny<IpIntelligenceDto>())).Returns(Decision(VpnProtectionAction.Ban));

        var result = await CreateSut().ProcessAsync(Context(), new IpIntelligenceDto());

        Assert.True(result.AdminActionCreated);
        adminActionsApi.Verify(x => x.EnsureAutomatedAction(It.Is<EnsureAutomatedActionDto>(dto => dto.PlayerId == PlayerId && dto.Type == AdminActionType.Ban && dto.AutomationFeature == AutomationFeature.VpnProtection && dto.AutomationRuleId == "vpn" && dto.AdminId == BotAdminId), It.IsAny<CancellationToken>()), Times.Once);
        topics.Verify(x => x.CreateTopicForAdminAction(AdminActionType.Ban, GameType.CallOfDuty4, PlayerId, "TestPlayer", It.IsAny<DateTime>(), It.IsAny<string>(), BotAdminId, It.IsAny<CancellationToken>()), Times.Once);
        adminActionsApi.Verify(x => x.UpdateAdminAction(It.Is<EditAdminActionDto>(dto => dto.ForumTopicId == 1234), It.IsAny<CancellationToken>()), Times.Once);
        rconEnforcer.Verify(x => x.EnforceAsync(It.IsAny<VpnProtectionContext>(), VpnProtectionAction.Ban, It.Is<string>(reason => reason.Contains("[PORTAL-AUTOMATION] VPN:vpn", StringComparison.Ordinal)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_ExistingBan_DoesNotCreateTopic()
    {
        evaluator.Setup(x => x.Evaluate(It.IsAny<EffectiveVpnProtectionSettings>(), It.IsAny<IpIntelligenceDto>())).Returns(Decision(VpnProtectionAction.Ban));
        adminActionsApi.Setup(x => x.EnsureAutomatedAction(It.IsAny<EnsureAutomatedActionDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(EnsureResult(false));

        var result = await CreateSut().ProcessAsync(Context(), new IpIntelligenceDto());

        Assert.False(result.AdminActionCreated);
        topics.Verify(x => x.CreateTopicForAdminAction(It.IsAny<AdminActionType>(), It.IsAny<GameType>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        adminActionsApi.Verify(x => x.UpdateAdminAction(It.IsAny<EditAdminActionDto>(), It.IsAny<CancellationToken>()), Times.Never);
        rconEnforcer.Verify(x => x.EnforceAsync(It.IsAny<VpnProtectionContext>(), VpnProtectionAction.Ban, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_ObservationExisting_DoesNotCreateTopicOrRconAction()
    {
        evaluator.Setup(x => x.Evaluate(It.IsAny<EffectiveVpnProtectionSettings>(), It.IsAny<IpIntelligenceDto>())).Returns(Decision(VpnProtectionAction.Observation));
        adminActionsApi.Setup(x => x.EnsureAutomatedAction(It.IsAny<EnsureAutomatedActionDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(EnsureResult(false));

        await CreateSut().ProcessAsync(Context(), new IpIntelligenceDto());

        topics.Verify(x => x.CreateTopicForAdminAction(It.IsAny<AdminActionType>(), It.IsAny<GameType>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_EnsureActionFails_DoesNotApplyMarkedRconAction()
    {
        evaluator.Setup(x => x.Evaluate(It.IsAny<EffectiveVpnProtectionSettings>(), It.IsAny<IpIntelligenceDto>())).Returns(Decision(VpnProtectionAction.Ban));
        adminActionsApi.Setup(x => x.EnsureAutomatedAction(It.IsAny<EnsureAutomatedActionDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ApiResult<EnsureAutomatedActionResultDto>(HttpStatusCode.InternalServerError));

        var result = await CreateSut().ProcessAsync(Context(), new IpIntelligenceDto());

        Assert.False(result.AdminActionCreated);
        rconEnforcer.Verify(x => x.EnforceAsync(It.IsAny<VpnProtectionContext>(), It.IsAny<VpnProtectionAction>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private VpnProtectionService CreateSut()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ContentSafety:BotAdminId"] = BotAdminId }).Build();
        return new VpnProtectionService(settingsProvider.Object, evaluator.Object, rconEnforcer.Object, repositoryApiClient.Object, topics.Object, configuration, new Mock<IAuditLogger>().Object, new Mock<ILogger<VpnProtectionService>>().Object);
    }

    private static VpnProtectionContext Context() => new()
    {
        ServerId = ServerId,
        GameType = GameType.CallOfDuty4,
        PlayerId = PlayerId,
        PlayerGuid = "player-guid",
        Username = "TestPlayer",
        PlayerTags = [],
        SlotId = 4
    };

    private static VpnProtectionDecision Decision(VpnProtectionAction action) => new()
    {
        Action = action,
        Reason = "VPN Protection: vpn",
        MatchedRules = [new VpnProtectionRuleMatch { RuleId = "vpn", Signal = VpnProtectionSignal.ProxyCheckIsVpn, ActualValue = "True", ExpectedValue = "true", Action = action, Reason = "VPN Protection: vpn", OrderIndex = 0 }]
    };

    private static ApiResult<EnsureAutomatedActionResultDto> EnsureResult(bool created)
    {
        var forumTopic = created ? "null" : "1234";
        var json = "{\"created\":" + created.ToString().ToLowerInvariant() + ",\"adminAction\":{\"adminActionId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"playerId\":\"" + PlayerId + "\",\"forumTopicId\":" + forumTopic + ",\"type\":\"Ban\",\"text\":\"VPN Protection: vpn\",\"created\":\"2026-01-01T00:00:00Z\"}}";
        var data = Newtonsoft.Json.JsonConvert.DeserializeObject<EnsureAutomatedActionResultDto>(json)!;
        return new ApiResult<EnsureAutomatedActionResultDto>(created ? HttpStatusCode.Created : HttpStatusCode.OK, new ApiResponse<EnsureAutomatedActionResultDto>(data));
    }
}