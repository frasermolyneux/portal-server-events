using MX.Observability.ApplicationInsights.Auditing;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.AdminActions;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.GameServers;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;
using XtremeIdiots.Portal.Server.Events.Processor.App.Services;
using Newtonsoft.Json;

using static XtremeIdiots.Portal.Server.Events.Processor.App.Tests.ServiceBusTestHelpers;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Functions;

public class BanAppliedProcessorTests
{
    private readonly Mock<ILogger<BanAppliedProcessor>> _logger = new();
    private readonly Mock<IRepositoryApiClient> _repoClient = new();
    private readonly Mock<IVersionedGameServersEventsApi> _versionedEvents = new();
    private readonly Mock<IGameServersEventsApi> _eventsApi = new();
    private readonly Mock<IVersionedPlayersApi> _versionedPlayers = new();
    private readonly Mock<IPlayersApi> _playersApi = new();
    private readonly Mock<IVersionedAdminActionsApi> _versionedAdminActions = new();
    private readonly Mock<IAdminActionsApi> _adminActionsApi = new();
    private readonly Mock<IAdminActionTopics> _adminActionTopics = new();
    private readonly Mock<IAuditLogger> _auditLogger = new();
    private readonly Mock<FunctionContext> _functionContext = new();
    private readonly BanAppliedProcessor _sut;

    private static readonly Guid TestServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public BanAppliedProcessorTests()
    {
        _versionedEvents.Setup(x => x.V1).Returns(_eventsApi.Object);
        _repoClient.Setup(x => x.GameServersEvents).Returns(_versionedEvents.Object);
        _versionedPlayers.Setup(x => x.V1).Returns(_playersApi.Object);
        _repoClient.Setup(x => x.Players).Returns(_versionedPlayers.Object);
        _versionedAdminActions.Setup(x => x.V1).Returns(_adminActionsApi.Object);
        _repoClient.Setup(x => x.AdminActions).Returns(_versionedAdminActions.Object);

        _sut = new BanAppliedProcessor(_logger.Object, _repoClient.Object, _adminActionTopics.Object, _auditLogger.Object);
    }

    private static BanAppliedEvent CreateValidEvent(
        Guid? serverId = null,
        string? gameType = null,
        string? playerGuid = null,
        string? playerName = null,
        string? source = null,
        string? reason = null,
        bool isTemporary = false,
        DateTime? expiresUtc = null) => new()
        {
            EventGeneratedUtc = DateTime.UtcNow.AddSeconds(-10),
            EventPublishedUtc = DateTime.UtcNow.AddSeconds(-5),
            ServerId = serverId ?? TestServerId,
            GameType = gameType ?? "CallOfDuty4",
            SequenceId = 1,
            PlayerGuid = playerGuid ?? "abc123guid",
            PlayerName = playerName ?? "TestPlayer",
            IsTemporary = isTemporary,
            ExpiresUtc = expiresUtc,
            Source = source ?? "Agent",
            Reason = reason ?? "Reconcile drift"
        };

    [Fact]
    public async Task ValidEvent_CreatesServerEvent()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        _eventsApi.Setup(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessBanApplied(message, _functionContext.Object);

        _eventsApi.Verify(x => x.CreateGameServerEvent(It.Is<CreateGameServerEventDto>(dto =>
            dto.GameServerId == TestServerId &&
            dto.EventType == "BanApplied" &&
            dto.EventData.Contains("abc123guid", StringComparison.Ordinal)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EmptyServerId_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(serverId: Guid.Empty);
        var message = CreateMessage(evt);

        await _sut.ProcessBanApplied(message, _functionContext.Object);

        _eventsApi.Verify(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InvalidGameType_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(gameType: "NotARealGame");
        var message = CreateMessage(evt);

        await _sut.ProcessBanApplied(message, _functionContext.Object);

        _eventsApi.Verify(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MissingRequiredFields_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(playerGuid: "", source: "", reason: "");
        var message = CreateMessage(evt);

        await _sut.ProcessBanApplied(message, _functionContext.Object);

        _eventsApi.Verify(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MalformedJson_LogsWarningAndReturns()
    {
        var message = CreateMessage("{{bad json");

        await _sut.ProcessBanApplied(message, _functionContext.Object);

        _eventsApi.Verify(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateGameServerEventFailure_ThrowsToAllowRetry()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        _eventsApi.Setup(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(System.Net.HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.ProcessBanApplied(message, _functionContext.Object));
    }

    [Fact]
    public async Task RconDumpBanListEvent_CreatesActionWithReasonAndForumTopic()
    {
        var evt = CreateValidEvent(gameType: nameof(GameType.CallOfDuty4x), playerGuid: "2310346613824768397", source: "RconDumpbanlist", reason: "VPN Protection: matched rule 'proxycheck-risk-score-dangerous'");
        var playerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        _eventsApi.Setup(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(SuccessResult());
        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4x, evt.PlayerGuid)).ReturnsAsync(SuccessResult());
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4x, evt.PlayerGuid, PlayerEntityOptions.None))
            .ReturnsAsync(new ApiResult<PlayerDto>(System.Net.HttpStatusCode.OK, new ApiResponse<PlayerDto>(CreatePlayer(playerId, evt.PlayerGuid))));
        var adminAction = CreateAdminAction(playerId);
        var claimId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        _adminActionsApi.Setup(x => x.EnsureAutomatedAction(It.IsAny<EnsureAutomatedActionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateEnsureResult(adminAction, created: true));
        _adminActionsApi.Setup(x => x.ClaimForumTopicPublication(adminAction.AdminActionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateClaimResult(adminAction.AdminActionId, claimId));
        _adminActionTopics.Setup(x => x.CreateTopicForAdminAction(AdminActionType.Ban, GameType.CallOfDuty4x, playerId, "TestPlayer", It.IsAny<DateTime>(), evt.Reason, null, It.IsAny<CancellationToken>())).ReturnsAsync(12345);
        _adminActionsApi.Setup(x => x.CompleteForumTopicPublication(adminAction.AdminActionId, It.IsAny<CompleteForumTopicPublicationDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(SuccessResult());

        await _sut.ProcessBanApplied(CreateMessage(evt), _functionContext.Object);

        _adminActionsApi.Verify(x => x.EnsureAutomatedAction(It.Is<EnsureAutomatedActionDto>(action =>
            action.PlayerId == playerId &&
            action.Type == AdminActionType.Ban &&
            action.Text == evt.Reason &&
            action.AutomationFeature == AutomationFeature.RconBanImport &&
            action.AutomationRuleId == $"{evt.ServerId:N}:{evt.PlayerGuid}"),
            It.IsAny<CancellationToken>()), Times.Once);
        _adminActionsApi.Verify(x => x.CompleteForumTopicPublication(
            adminAction.AdminActionId,
            It.Is<CompleteForumTopicPublicationDto>(completion => completion.ClaimId == claimId && completion.ForumTopicId == 12345),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Cod4xVpnProtectionEvent_CreatesActionWithReasonAndForumTopic()
    {
        var evt = CreateValidEvent(gameType: nameof(GameType.CallOfDuty4x), playerGuid: "2310346613824768397", source: "CoD4xVpnProtection", reason: "VPN Protection: matched rule 'proxycheck-risk-score-dangerous'");
        var playerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        _eventsApi.Setup(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(SuccessResult());
        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4x, evt.PlayerGuid)).ReturnsAsync(SuccessResult());
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4x, evt.PlayerGuid, PlayerEntityOptions.None))
            .ReturnsAsync(new ApiResult<PlayerDto>(System.Net.HttpStatusCode.OK, new ApiResponse<PlayerDto>(CreatePlayer(playerId, evt.PlayerGuid))));
        var adminAction = CreateAdminAction(playerId);
        var claimId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        _adminActionsApi.Setup(x => x.EnsureAutomatedAction(It.IsAny<EnsureAutomatedActionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateEnsureResult(adminAction, created: true));
        _adminActionsApi.Setup(x => x.ClaimForumTopicPublication(adminAction.AdminActionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateClaimResult(adminAction.AdminActionId, claimId));
        _adminActionTopics.Setup(x => x.CreateTopicForAdminAction(AdminActionType.Ban, GameType.CallOfDuty4x, playerId, "TestPlayer", It.IsAny<DateTime>(), evt.Reason, null, It.IsAny<CancellationToken>())).ReturnsAsync(12345);
        _adminActionsApi.Setup(x => x.CompleteForumTopicPublication(adminAction.AdminActionId, It.IsAny<CompleteForumTopicPublicationDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(SuccessResult());

        await _sut.ProcessBanApplied(CreateMessage(evt), _functionContext.Object);

        _adminActionsApi.Verify(x => x.EnsureAutomatedAction(It.Is<EnsureAutomatedActionDto>(action =>
            action.PlayerId == playerId &&
            action.Type == AdminActionType.Ban &&
            action.Text == evt.Reason &&
            action.AutomationFeature == AutomationFeature.RconBanImport &&
            action.AutomationRuleId == $"{evt.ServerId:N}:{evt.PlayerGuid}"),
            It.IsAny<CancellationToken>()), Times.Once);
        _adminActionsApi.Verify(x => x.CompleteForumTopicPublication(
            adminAction.AdminActionId,
            It.Is<CompleteForumTopicPublicationDto>(completion => completion.ClaimId == claimId && completion.ForumTopicId == 12345),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RconDumpBanListEvent_WithExistingUnlinkedAction_ClaimsAndCompletesForumTopic()
    {
        var evt = CreateValidEvent(gameType: nameof(GameType.CallOfDuty4x), playerGuid: "2310346613824768397", source: "RconDumpbanlist");
        var playerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        _eventsApi.Setup(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(SuccessResult());
        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4x, evt.PlayerGuid)).ReturnsAsync(SuccessResult());
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4x, evt.PlayerGuid, PlayerEntityOptions.None))
            .ReturnsAsync(new ApiResult<PlayerDto>(System.Net.HttpStatusCode.OK, new ApiResponse<PlayerDto>(CreatePlayer(playerId, evt.PlayerGuid))));
        var adminAction = CreateAdminAction(playerId);
        var claimId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        _adminActionsApi.Setup(x => x.EnsureAutomatedAction(It.IsAny<EnsureAutomatedActionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateEnsureResult(adminAction, created: false));
        _adminActionsApi.Setup(x => x.ClaimForumTopicPublication(adminAction.AdminActionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateClaimResult(adminAction.AdminActionId, claimId));
        _adminActionTopics.Setup(x => x.CreateTopicForAdminAction(AdminActionType.Ban, GameType.CallOfDuty4x, playerId, "TestPlayer", It.IsAny<DateTime>(), evt.Reason, null, It.IsAny<CancellationToken>())).ReturnsAsync(12345);
        _adminActionsApi.Setup(x => x.CompleteForumTopicPublication(adminAction.AdminActionId, It.IsAny<CompleteForumTopicPublicationDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(SuccessResult());

        await _sut.ProcessBanApplied(CreateMessage(evt), _functionContext.Object);

        _adminActionsApi.Verify(x => x.CreateAdminAction(It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()), Times.Never);
        _adminActionsApi.Verify(x => x.CompleteForumTopicPublication(adminAction.AdminActionId, It.IsAny<CompleteForumTopicPublicationDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RconDumpBanListEvent_WhenTopicCreationFails_ThrowsToAllowRetry()
    {
        var evt = CreateValidEvent(gameType: nameof(GameType.CallOfDuty4x), playerGuid: "2310346613824768397", source: "RconDumpbanlist");
        var playerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        _eventsApi.Setup(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(SuccessResult());
        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4x, evt.PlayerGuid)).ReturnsAsync(SuccessResult());
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4x, evt.PlayerGuid, PlayerEntityOptions.None))
            .ReturnsAsync(new ApiResult<PlayerDto>(System.Net.HttpStatusCode.OK, new ApiResponse<PlayerDto>(CreatePlayer(playerId, evt.PlayerGuid))));
        var adminAction = CreateAdminAction(playerId);
        _adminActionsApi.Setup(x => x.EnsureAutomatedAction(It.IsAny<EnsureAutomatedActionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateEnsureResult(adminAction, created: false));
        _adminActionsApi.Setup(x => x.ClaimForumTopicPublication(adminAction.AdminActionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateClaimResult(adminAction.AdminActionId, Guid.Parse("44444444-4444-4444-4444-444444444444")));
        _adminActionTopics.Setup(x => x.CreateTopicForAdminAction(It.IsAny<AdminActionType>(), It.IsAny<GameType>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.ProcessBanApplied(CreateMessage(evt), _functionContext.Object));

        _adminActionsApi.Verify(x => x.CreateAdminAction(It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()), Times.Never);
        _adminActionsApi.Verify(x => x.CompleteForumTopicPublication(It.IsAny<Guid>(), It.IsAny<CompleteForumTopicPublicationDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RconDumpBanListEvent_WhenPublicationRequiresManualRecovery_DoesNotPostAnotherTopic()
    {
        var evt = CreateValidEvent(gameType: nameof(GameType.CallOfDuty4x), playerGuid: "2310346613824768397", source: "RconDumpbanlist");
        var playerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var adminAction = CreateAdminAction(playerId);
        _eventsApi.Setup(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(SuccessResult());
        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4x, evt.PlayerGuid)).ReturnsAsync(SuccessResult());
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4x, evt.PlayerGuid, PlayerEntityOptions.None))
            .ReturnsAsync(new ApiResult<PlayerDto>(System.Net.HttpStatusCode.OK, new ApiResponse<PlayerDto>(CreatePlayer(playerId, evt.PlayerGuid))));
        _adminActionsApi.Setup(x => x.EnsureAutomatedAction(It.IsAny<EnsureAutomatedActionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateEnsureResult(adminAction, created: false));
        _adminActionsApi.Setup(x => x.ClaimForumTopicPublication(adminAction.AdminActionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateManualRecoveryClaimResult(adminAction.AdminActionId));

        await _sut.ProcessBanApplied(CreateMessage(evt), _functionContext.Object);

        _adminActionTopics.Verify(x => x.CreateTopicForAdminAction(
            It.IsAny<AdminActionType>(),
            It.IsAny<GameType>(),
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<DateTime>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _adminActionsApi.Verify(x => x.CompleteForumTopicPublication(It.IsAny<Guid>(), It.IsAny<CompleteForumTopicPublicationDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RconDumpBanListEvent_TemporaryBanPromotionUsesSameLifecycleRuleId()
    {
        var evt = CreateValidEvent(gameType: nameof(GameType.CallOfDuty4x), playerGuid: "2310346613824768397", source: "RconDumpbanlist");
        var playerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var adminAction = CreateAdminAction(playerId);
        _eventsApi.Setup(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(SuccessResult());
        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4x, evt.PlayerGuid)).ReturnsAsync(SuccessResult());
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4x, evt.PlayerGuid, PlayerEntityOptions.None))
            .ReturnsAsync(new ApiResult<PlayerDto>(System.Net.HttpStatusCode.OK, new ApiResponse<PlayerDto>(CreatePlayer(playerId, evt.PlayerGuid))));
        _adminActionsApi.Setup(x => x.EnsureAutomatedAction(It.IsAny<EnsureAutomatedActionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateEnsureResult(adminAction, created: false));
        _adminActionsApi.Setup(x => x.ClaimForumTopicPublication(adminAction.AdminActionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCompletedClaimResult(adminAction.AdminActionId, 12345));

        var temporaryEvent = CreateValidEvent(
            gameType: nameof(GameType.CallOfDuty4x),
            playerGuid: evt.PlayerGuid,
            source: "RconDumpbanlist",
            isTemporary: true,
            expiresUtc: DateTime.UtcNow.AddMinutes(30));

        await _sut.ProcessBanApplied(CreateMessage(temporaryEvent), _functionContext.Object);
        await _sut.ProcessBanApplied(CreateMessage(evt), _functionContext.Object);

        _adminActionsApi.Verify(x => x.EnsureAutomatedAction(
            It.Is<EnsureAutomatedActionDto>(request =>
                request.AutomationRuleId == $"{evt.ServerId:N}:{evt.PlayerGuid}" &&
                request.Type == AdminActionType.TempBan),
            It.IsAny<CancellationToken>()), Times.Once);
        _adminActionsApi.Verify(x => x.EnsureAutomatedAction(
            It.Is<EnsureAutomatedActionDto>(request =>
                request.AutomationRuleId == $"{evt.ServerId:N}:{evt.PlayerGuid}" &&
                request.Type == AdminActionType.Ban),
            It.IsAny<CancellationToken>()), Times.Once);
        _adminActionTopics.Verify(x => x.CreateTopicForAdminAction(
            It.IsAny<AdminActionType>(),
            It.IsAny<GameType>(),
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<DateTime>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static PlayerDto CreatePlayer(Guid playerId, string playerGuid)
        => JsonConvert.DeserializeObject<PlayerDto>(JsonConvert.SerializeObject(new
        {
            PlayerId = playerId,
            Guid = playerGuid,
            Username = "TestPlayer",
            GameType = GameType.CallOfDuty4x
        }))!;

    private static AdminActionDto CreateAdminAction(Guid playerId)
        => JsonConvert.DeserializeObject<AdminActionDto>(JsonConvert.SerializeObject(new
        {
            AdminActionId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            PlayerId = playerId,
            Type = AdminActionType.Ban,
            Text = "RCON import",
            Created = DateTime.UtcNow,
            Player = new { PlayerId = playerId, Guid = "2310346613824768397", Username = "TestPlayer" }
        }))!;

    private static ApiResult<EnsureAutomatedActionResultDto> CreateEnsureResult(AdminActionDto adminAction, bool created)
        => new(System.Net.HttpStatusCode.OK, new ApiResponse<EnsureAutomatedActionResultDto>(JsonConvert.DeserializeObject<EnsureAutomatedActionResultDto>(JsonConvert.SerializeObject(new
        {
            Created = created,
            AdminAction = adminAction
        }))!));

    private static ApiResult<ForumTopicPublicationClaimResultDto> CreateClaimResult(Guid adminActionId, Guid claimId)
        => new(System.Net.HttpStatusCode.OK, new ApiResponse<ForumTopicPublicationClaimResultDto>(JsonConvert.DeserializeObject<ForumTopicPublicationClaimResultDto>(JsonConvert.SerializeObject(new
        {
            AdminActionId = adminActionId,
            ClaimId = claimId,
            RequiresManualRecovery = false
        }))!));

    private static ApiResult<ForumTopicPublicationClaimResultDto> CreateManualRecoveryClaimResult(Guid adminActionId)
        => new(System.Net.HttpStatusCode.OK, new ApiResponse<ForumTopicPublicationClaimResultDto>(JsonConvert.DeserializeObject<ForumTopicPublicationClaimResultDto>(JsonConvert.SerializeObject(new
        {
            AdminActionId = adminActionId,
            RequiresManualRecovery = true
        }))!));

    private static ApiResult<ForumTopicPublicationClaimResultDto> CreateCompletedClaimResult(Guid adminActionId, int forumTopicId)
        => new(System.Net.HttpStatusCode.OK, new ApiResponse<ForumTopicPublicationClaimResultDto>(JsonConvert.DeserializeObject<ForumTopicPublicationClaimResultDto>(JsonConvert.SerializeObject(new
        {
            AdminActionId = adminActionId,
            ForumTopicId = forumTopicId,
            RequiresManualRecovery = false
        }))!));
}
