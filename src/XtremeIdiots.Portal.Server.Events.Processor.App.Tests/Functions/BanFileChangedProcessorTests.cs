using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using Moq;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.AdminActions;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;
using XtremeIdiots.Portal.Server.Events.Processor.App.Services;

using static XtremeIdiots.Portal.Server.Events.Processor.App.Tests.ServiceBusTestHelpers;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Functions;

public class BanFileChangedProcessorTests
{
    private readonly Mock<ILogger<BanFileChangedProcessor>> _logger = new();
    private readonly Mock<IRepositoryApiClient> _repoClient = new();
    private readonly Mock<IVersionedPlayersApi> _versionedPlayers = new();
    private readonly Mock<IPlayersApi> _playersApi = new();
    private readonly Mock<IVersionedAdminActionsApi> _versionedAdminActions = new();
    private readonly Mock<IAdminActionsApi> _adminActionsApi = new();
    private readonly Mock<IAdminActionTopics> _adminActionTopics = new();
    private readonly TelemetryClient _telemetry;
    private readonly Mock<FunctionContext> _functionContext = new();
    private readonly BanFileChangedProcessor _sut;

    private static readonly Guid TestServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TestPlayerId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public BanFileChangedProcessorTests()
    {
        _versionedPlayers.Setup(x => x.V1).Returns(_playersApi.Object);
        _repoClient.Setup(x => x.Players).Returns(_versionedPlayers.Object);

        _versionedAdminActions.Setup(x => x.V1).Returns(_adminActionsApi.Object);
        _repoClient.Setup(x => x.AdminActions).Returns(_versionedAdminActions.Object);

        // Default: no active bans found
        _adminActionsApi.Setup(x => x.GetAdminActions(
                It.IsAny<GameType>(), It.IsAny<Guid>(), It.IsAny<string?>(),
                It.IsAny<AdminActionFilter?>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<AdminActionOrder?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new MX.Api.Abstractions.CollectionModel<AdminActionDto>()));

        _telemetry = new TelemetryClient(new Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration
        {
            TelemetryChannel = new Mock<ITelemetryChannel>().Object
        });

        _sut = new BanFileChangedProcessor(_logger.Object, _repoClient.Object, _adminActionTopics.Object, _telemetry);
    }

    private static BanDetectedEvent CreateValidEvent(
        Guid? serverId = null,
        string? gameType = null,
        IReadOnlyList<DetectedBan>? newBans = null) => new()
    {
        EventGeneratedUtc = DateTime.UtcNow.AddSeconds(-10),
        EventPublishedUtc = DateTime.UtcNow.AddSeconds(-5),
        ServerId = serverId ?? TestServerId,
        GameType = gameType ?? "CallOfDuty4",
        SequenceId = 1,
        NewBans = newBans ?? [new DetectedBan { PlayerGuid = "abc123guid", PlayerName = "TestPlayer" }]
    };

    [Fact]
    public async Task NewBan_CreatesPlayerAndAdminAction()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, "abc123guid"))
            .ReturnsAsync(NotFoundResult());

        _playersApi.Setup(x => x.CreatePlayer(It.IsAny<CreatePlayerDto>()))
            .ReturnsAsync(SuccessResult());

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.None))
            .ReturnsAsync(SuccessResult(playerDto));

        _adminActionTopics.Setup(x => x.CreateTopicForAdminAction(
                AdminActionType.Ban, GameType.CallOfDuty4, TestPlayerId, It.IsAny<string>(),
                It.IsAny<DateTime>(), "Imported from server ban file", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(12345);

        _adminActionsApi.Setup(x => x.CreateAdminAction(It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessBanFileChanged(message, _functionContext.Object);

        _playersApi.Verify(x => x.CreatePlayer(It.Is<CreatePlayerDto>(dto =>
            dto.Username == "TestPlayer" &&
            dto.Guid == "abc123guid")), Times.Once);

        _adminActionsApi.Verify(x => x.CreateAdminAction(It.Is<CreateAdminActionDto>(dto =>
            dto.PlayerId == TestPlayerId &&
            dto.Type == AdminActionType.Ban &&
            dto.Text == "Imported from server ban file" &&
            dto.ForumTopicId == 12345), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExistingPlayerNoBan_CreatesAdminActionOnly()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, "abc123guid"))
            .ReturnsAsync(SuccessResult());

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.None))
            .ReturnsAsync(SuccessResult(playerDto));

        _adminActionsApi.Setup(x => x.CreateAdminAction(It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessBanFileChanged(message, _functionContext.Object);

        _playersApi.Verify(x => x.CreatePlayer(It.IsAny<CreatePlayerDto>()), Times.Never);
        _adminActionsApi.Verify(x => x.CreateAdminAction(It.Is<CreateAdminActionDto>(dto =>
            dto.PlayerId == TestPlayerId &&
            dto.Type == AdminActionType.Ban), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EmptyServerId_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(serverId: Guid.Empty);
        var message = CreateMessage(evt);

        await _sut.ProcessBanFileChanged(message, _functionContext.Object);

        _playersApi.Verify(x => x.HeadPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task InvalidGameType_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(gameType: "NotARealGame");
        var message = CreateMessage(evt);

        await _sut.ProcessBanFileChanged(message, _functionContext.Object);

        _playersApi.Verify(x => x.HeadPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task EmptyBansList_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(newBans: []);
        var message = CreateMessage(evt);

        await _sut.ProcessBanFileChanged(message, _functionContext.Object);

        _playersApi.Verify(x => x.HeadPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task MalformedJson_LogsWarningAndReturns()
    {
        var message = CreateMessage("{{bad json");

        await _sut.ProcessBanFileChanged(message, _functionContext.Object);

        _playersApi.Verify(x => x.HeadPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task BanWithEmptyGuid_SkipsThatBan()
    {
        var evt = CreateValidEvent(newBans:
        [
            new DetectedBan { PlayerGuid = "", PlayerName = "BadPlayer" },
            new DetectedBan { PlayerGuid = "valid123guid", PlayerName = "GoodPlayer" }
        ]);
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, "valid123guid"))
            .ReturnsAsync(NotFoundResult());

        _playersApi.Setup(x => x.CreatePlayer(It.IsAny<CreatePlayerDto>()))
            .ReturnsAsync(SuccessResult());

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "valid123guid", PlayerEntityOptions.None))
            .ReturnsAsync(SuccessResult(playerDto));

        _adminActionsApi.Setup(x => x.CreateAdminAction(It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessBanFileChanged(message, _functionContext.Object);

        // Only the valid ban should be processed
        _playersApi.Verify(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, "valid123guid"), Times.Once);
        _playersApi.Verify(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, ""), Times.Never);
    }

    [Fact]
    public async Task ConflictOnPlayerCreate_FallsThroughToLookup()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, "abc123guid"))
            .ReturnsAsync(NotFoundResult());

        _playersApi.Setup(x => x.CreatePlayer(It.IsAny<CreatePlayerDto>()))
            .ReturnsAsync(ConflictResult());

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.None))
            .ReturnsAsync(SuccessResult(playerDto));

        _adminActionsApi.Setup(x => x.CreateAdminAction(It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessBanFileChanged(message, _functionContext.Object);

        _adminActionsApi.Verify(x => x.CreateAdminAction(It.Is<CreateAdminActionDto>(dto =>
            dto.PlayerId == TestPlayerId &&
            dto.Type == AdminActionType.Ban), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NewBan_CallsCreateTopicForAdminAction()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, "abc123guid"))
            .ReturnsAsync(NotFoundResult());

        _playersApi.Setup(x => x.CreatePlayer(It.IsAny<CreatePlayerDto>()))
            .ReturnsAsync(SuccessResult());

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.None))
            .ReturnsAsync(SuccessResult(playerDto));

        _adminActionTopics.Setup(x => x.CreateTopicForAdminAction(
                AdminActionType.Ban, GameType.CallOfDuty4, TestPlayerId, It.IsAny<string>(),
                It.IsAny<DateTime>(), "Imported from server ban file", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(99999);

        _adminActionsApi.Setup(x => x.CreateAdminAction(It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessBanFileChanged(message, _functionContext.Object);

        _adminActionTopics.Verify(x => x.CreateTopicForAdminAction(
            AdminActionType.Ban, GameType.CallOfDuty4, TestPlayerId, It.IsAny<string>(),
            It.IsAny<DateTime>(), "Imported from server ban file", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NewBan_SetsForumTopicIdOnDto()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, "abc123guid"))
            .ReturnsAsync(NotFoundResult());

        _playersApi.Setup(x => x.CreatePlayer(It.IsAny<CreatePlayerDto>()))
            .ReturnsAsync(SuccessResult());

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.None))
            .ReturnsAsync(SuccessResult(playerDto));

        _adminActionTopics.Setup(x => x.CreateTopicForAdminAction(
                It.IsAny<AdminActionType>(), It.IsAny<GameType>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(42);

        _adminActionsApi.Setup(x => x.CreateAdminAction(It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessBanFileChanged(message, _functionContext.Object);

        _adminActionsApi.Verify(x => x.CreateAdminAction(It.Is<CreateAdminActionDto>(dto =>
            dto.ForumTopicId == 42), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ForumTopicCreationFails_StillCreatesBanWithNullTopicId()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, "abc123guid"))
            .ReturnsAsync(NotFoundResult());

        _playersApi.Setup(x => x.CreatePlayer(It.IsAny<CreatePlayerDto>()))
            .ReturnsAsync(SuccessResult());

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.None))
            .ReturnsAsync(SuccessResult(playerDto));

        _adminActionTopics.Setup(x => x.CreateTopicForAdminAction(
                It.IsAny<AdminActionType>(), It.IsAny<GameType>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        _adminActionsApi.Setup(x => x.CreateAdminAction(It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessBanFileChanged(message, _functionContext.Object);

        _adminActionsApi.Verify(x => x.CreateAdminAction(It.Is<CreateAdminActionDto>(dto =>
            dto.PlayerId == TestPlayerId &&
            dto.Type == AdminActionType.Ban &&
            dto.ForumTopicId == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExistingActiveBan_DoesNotCreateForumTopicOrAdminAction()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.HeadPlayerByGameType(GameType.CallOfDuty4, "abc123guid"))
            .ReturnsAsync(SuccessResult());

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.None))
            .ReturnsAsync(SuccessResult(playerDto));

        var activeBanDto = new AdminActionDto();
        var collection = new MX.Api.Abstractions.CollectionModel<AdminActionDto>
        {
            Items = [activeBanDto]
        };
        _adminActionsApi.Setup(x => x.GetAdminActions(
                GameType.CallOfDuty4, TestPlayerId, null, AdminActionFilter.ActiveBans, 0, 1, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(collection));

        await _sut.ProcessBanFileChanged(message, _functionContext.Object);

        _adminActionTopics.Verify(x => x.CreateTopicForAdminAction(
            It.IsAny<AdminActionType>(), It.IsAny<GameType>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        _adminActionsApi.Verify(x => x.CreateAdminAction(
            It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
