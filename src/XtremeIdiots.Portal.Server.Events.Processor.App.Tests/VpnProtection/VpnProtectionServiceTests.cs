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
    private readonly Mock<IVersionedAdminActionsApi> versionedAdminActionsApi = new();
    private readonly Mock<IAdminActionsApi> adminActionsApi = new();
    private readonly Mock<IAdminActionTopics> adminActionTopics = new();
    private readonly Mock<IAuditLogger> auditLogger = new();
    private readonly Mock<ILogger<VpnProtectionService>> logger = new();

    public VpnProtectionServiceTests()
    {
        versionedAdminActionsApi.Setup(x => x.V1).Returns(adminActionsApi.Object);
        repositoryApiClient.Setup(x => x.AdminActions).Returns(versionedAdminActionsApi.Object);

        settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EffectiveVpnProtectionSettings { Enabled = true });
        rconEnforcer
            .Setup(x => x.EnforceAsync(
                It.IsAny<VpnProtectionContext>(),
                It.IsAny<VpnProtectionAction>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(VpnProtectionRconOutcome.Succeeded);
        adminActionTopics
            .Setup(x => x.CreateTopicForAdminAction(
                It.IsAny<AdminActionType>(),
                It.IsAny<GameType>(),
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<DateTime>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1234);
        adminActionsApi
            .Setup(x => x.CreateAdminAction(It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.Created));
    }

    [Fact]
    public async Task ProcessAsync_ExcludedTag_SkipsEvaluationAndActions()
    {
        settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EffectiveVpnProtectionSettings
            {
                Enabled = true,
                ExcludedPlayerTags = new HashSet<string>(["Trusted VPN"], StringComparer.OrdinalIgnoreCase)
            });
        var context = CreateContext(["trusted vpn"]);

        var result = await CreateSut().ProcessAsync(context, new IpIntelligenceDto());

        Assert.True(result.WasExcluded);
        Assert.False(result.AdminActionCreated);
        evaluator.Verify(
            x => x.Evaluate(It.IsAny<EffectiveVpnProtectionSettings>(), It.IsAny<IpIntelligenceDto>()),
            Times.Never);
        adminActionsApi.Verify(
            x => x.CreateAdminAction(It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_NoMatchingRules_DoesNotCreateAction()
    {
        evaluator
            .Setup(x => x.Evaluate(It.IsAny<EffectiveVpnProtectionSettings>(), It.IsAny<IpIntelligenceDto>()))
            .Returns(VpnProtectionDecision.NoMatch);

        var result = await CreateSut().ProcessAsync(CreateContext(), new IpIntelligenceDto());

        Assert.False(result.AdminActionCreated);
        rconEnforcer.Verify(
            x => x.EnforceAsync(
                It.IsAny<VpnProtectionContext>(),
                It.IsAny<VpnProtectionAction>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_RconFails_StillCreatesForumTopicAndAdminAction()
    {
        var decision = CreateDecision(VpnProtectionAction.Ban);
        evaluator
            .Setup(x => x.Evaluate(It.IsAny<EffectiveVpnProtectionSettings>(), It.IsAny<IpIntelligenceDto>()))
            .Returns(decision);
        rconEnforcer
            .Setup(x => x.EnforceAsync(
                It.IsAny<VpnProtectionContext>(),
                VpnProtectionAction.Ban,
                decision.Reason,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(VpnProtectionRconOutcome.Failed);

        var result = await CreateSut().ProcessAsync(CreateContext(), new IpIntelligenceDto());

        Assert.True(result.AdminActionCreated);
        Assert.Equal(VpnProtectionRconOutcome.Failed, result.RconOutcome);
        adminActionTopics.Verify(x => x.CreateTopicForAdminAction(
            AdminActionType.Ban,
            GameType.CallOfDuty4,
            PlayerId,
            "TestPlayer",
            It.IsAny<DateTime>(),
            It.Is<string>(text => text.Contains("RCON outcome: Failed", StringComparison.Ordinal)),
            BotAdminId,
            It.IsAny<CancellationToken>()), Times.Once);
        adminActionsApi.Verify(x => x.CreateAdminAction(
            It.Is<CreateAdminActionDto>(dto =>
                dto.PlayerId == PlayerId &&
                dto.Type == AdminActionType.Ban &&
                dto.AdminId == BotAdminId &&
                dto.ForumTopicId == 1234 &&
                dto.Text.Contains("vpn", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_AdminActionApiFails_Throws()
    {
        evaluator
            .Setup(x => x.Evaluate(It.IsAny<EffectiveVpnProtectionSettings>(), It.IsAny<IpIntelligenceDto>()))
            .Returns(CreateDecision(VpnProtectionAction.Kick));
        adminActionsApi
            .Setup(x => x.CreateAdminAction(It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateSut().ProcessAsync(CreateContext(), new IpIntelligenceDto()));
    }

    [Fact]
    public async Task ProcessAsync_UnsupportedDestructiveAction_DoesNotCreateAdminAction()
    {
        evaluator
            .Setup(x => x.Evaluate(It.IsAny<EffectiveVpnProtectionSettings>(), It.IsAny<IpIntelligenceDto>()))
            .Returns(CreateDecision(VpnProtectionAction.Ban));
        rconEnforcer
            .Setup(x => x.EnforceAsync(
                It.IsAny<VpnProtectionContext>(),
                VpnProtectionAction.Ban,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(VpnProtectionRconOutcome.UnsupportedGame);
        var context = CreateContext() with { GameType = GameType.Insurgency };

        var result = await CreateSut().ProcessAsync(context, new IpIntelligenceDto());

        Assert.False(result.AdminActionCreated);
        Assert.Equal(VpnProtectionRconOutcome.UnsupportedGame, result.RconOutcome);
        adminActionTopics.Verify(x => x.CreateTopicForAdminAction(
            It.IsAny<AdminActionType>(),
            It.IsAny<GameType>(),
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<DateTime>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
        adminActionsApi.Verify(x => x.CreateAdminAction(
            It.IsAny<CreateAdminActionDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private VpnProtectionService CreateSut()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ContentSafety:BotAdminId"] = BotAdminId
            })
            .Build();

        return new VpnProtectionService(
            settingsProvider.Object,
            evaluator.Object,
            rconEnforcer.Object,
            repositoryApiClient.Object,
            adminActionTopics.Object,
            configuration,
            auditLogger.Object,
            logger.Object);
    }

    private static VpnProtectionContext CreateContext(IReadOnlyCollection<string>? tags = null) => new()
    {
        ServerId = ServerId,
        GameType = GameType.CallOfDuty4,
        PlayerId = PlayerId,
        PlayerGuid = "player-guid",
        Username = "TestPlayer",
        PlayerTags = tags ?? [],
        SlotId = 4
    };

    private static VpnProtectionDecision CreateDecision(VpnProtectionAction action) => new()
    {
        Action = action,
        Reason = "VPN Protection: vpn",
        MatchedRules =
        [
            new VpnProtectionRuleMatch
            {
                RuleId = "vpn",
                Signal = VpnProtectionSignal.ProxyCheckIsVpn,
                ActualValue = "True",
                ExpectedValue = "true",
                Action = action,
                Reason = "VPN Protection: vpn",
                OrderIndex = 0
            }
        ]
    };
}